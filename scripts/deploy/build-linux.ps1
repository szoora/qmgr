#requires -Version 7
<#
.SYNOPSIS
    Builds a Q-Mgr production deployment package for Linux (single-host, path-based routing).

.DESCRIPTION
    Publishes Q-Mgr.API and Q-Mgr.Web as self-contained linux-x64 executables, generates
    production config overlays, one nginx site (path-based: / -> Web, /api/ + /hubs/ -> API),
    two systemd units, and an idempotent install.sh — then packages everything into a
    versioned .tar.gz with a sidecar manifest.

    Modelled directly on E:\ERP\scripts\deploy\build-saas-linux.ps1 (same server, same owner).
    Differences from that script, all per explicit instruction:
      - Q-Mgr is TWO processes (API + Blazor Server Web), not one monolith -> two publishes,
        two appsettings overlays, two systemd units, one nginx site splitting traffic between them.
      - Single hostname (qmgr.cashbook.ug), PATH-based routing -> no wildcard server_name, no
        per-tenant subdomain logic.
      - Cert paths still follow the wildcard-under-cashbook.ug convention from
        build-saas-linux.ps1 exactly (ERP and Q-Mgr share the one wildcard cert on the box).
      - Ports 8586 (API) / 8587 (Web), both proxied — nothing public binds directly to them.
      - Q-Mgr has no catalog DB / per-tenant connection template (shared-schema tenancy) and no
        manual DB-creation step — QMgr.Infrastructure.Data.DatabaseInitializer creates the DB and
        runs migrations + RBAC/SuperAdmin/demo seeding automatically on first start. install.sh's
        final output says so explicitly instead of asking the operator to run a migration step.

.EXAMPLE
    ./build-linux.ps1 -PgPassword 'REDACTED'
#>

[CmdletBinding()]
param(
    [string]$OutputPath      = (Join-Path $PSScriptRoot 'dist'),
    [string]$Configuration   = 'Release',
    [string]$TargetRuntime   = 'linux-x64',

    [string]$HostName        = 'qmgr.cashbook.ug',
    [string]$HostSuffix      = 'cashbook.ug',            # wildcard cert covers *.cashbook.ug — shared with ERP on the same box
    # THE PORTS qmgr.cashbook.ug ACTUALLY RUNS ON. They were 8581/8582 until 2026-09-21, which was
    # wrong in the worst way a default can be: 8581 belongs to CashBook's cbpro.service and 8582 to
    # evolweb.service on this same box, so a build made without passing -ApiPort/-WebPort produced a
    # package that would have taken over two other applications' ports. install.sh's port guard
    # caught it once; the fix is for the defaults to be the truth rather than a trap.
    #
    # API first, Web = API+1 — kept in sequence on purpose so the pair reads as one unit at a glance
    # instead of two arbitrary numbers. Still not a PROMISE either is free on any given target: this
    # box's ports move independently of Q-Mgr, so confirm with `ss -tlnp` before every deploy (see
    # README.md) and pass explicit -ApiPort/-WebPort for any other server.
    [int]$ApiPort            = 8586,
    [int]$WebPort            = 8587,

    [string]$InstallRoot     = '/var/www/sites/qmgr',
    # Persists across deploys; excluded from the package and from rsync --delete. Since 2026-09-15
    # this is NOT symlinked into the API's wwwroot any more: the API reads and writes
    # $UploadsPath/media directly (Environment=MediaStorage__LocalPath in the unit) and serves it
    # through UploadsController with a per-file authorisation check. The symlink was what let the
    # static-file middleware hand out welfare evidence and visitor photographs to anyone with the
    # URL, ahead of authentication. install.sh removes a symlink left by an earlier install.
    [string]$UploadsPath     = '/var/www/uploads/qmgr',
    # Data Protection key ring. MUST live outside $InstallRoot and MUST be in the API unit's
    # ReadWritePaths. Two separate reasons, both found the hard way:
    #   1. ProtectSystem=strict mounts the whole filesystem read-only except ReadWritePaths, and
    #      the app's default key location is AppContext.BaseDirectory ($InstallRoot/api). So the
    #      key ring could not be created at all, and IDataProtector.Protect() threw on every call
    #      — which is a 500 on EVERY walk-in visitor check-in (the badge QR token is a protected
    #      payload), returned AFTER the visit had already committed. 'It says error but the
    #      visitor is checked in' was this, exactly.
    #   2. Even writable, a path under $InstallRoot is replaced on every deploy, so anything
    #      encrypted with the old ring (the platform Spotify OAuth tokens) would become
    #      undecryptable after each upgrade. /var/lib survives.
    [string]$DataProtectionPath = '/var/lib/qmgr/dataprotection-keys',
    # ---- Tenant custom domains (dashboard.theirschool.com) ----
    # One generated nginx server block per VERIFIED tenant domain, written by the root-owned helper
    # below and pulled in by an include at the top of the site file. Separate directory rather than
    # sites-enabled so an upgrade can never delete a tenant's block, and so the helper's write
    # permission is confined to a directory holding nothing else.
    [string]$TenantDomainConfPath = '/etc/nginx/qmgr-tenants',
    # How a request reaches the root helper. The API (www-data, NoNewPrivileges=true) CANNOT sudo —
    # that flag exists to stop exactly that — so it writes "<action> <domain>" into this directory
    # and reads the result back, while qmgr-domain-worker.path notices the file and runs the helper
    # as root. Root-owned, group www-data, 0770: the API may add and collect its own files there and
    # can do nothing else with root's privileges.
    [string]$DomainSpoolPath = '/var/lib/qmgr/domain-spool',
    # Webroot for the ACME http-01 challenge, and Q-Mgr's OWN one — never the directory whatever
    # else on this box renews its certificates from. The helper issues into it for a tenant domain
    # the shared certificate cannot cover, and the tenant's port-80 block keeps serving it
    # afterwards so renewals need nothing edited here.
    [string]$AcmeWebroot     = '/var/www/qmgr-acme',
    # Where Let's Encrypt sends expiry warnings for a tenant domain Q-Mgr issued. It is an
    # operator's address, not the tenant's: the tenant cannot act on it, and a school reading
    # "your certificate expires in 10 days" about a box they do not administer is alarming and
    # useless. Empty means certbot's --register-unsafely-without-email, which Let's Encrypt allows
    # and which loses the warnings, so the build says so rather than doing it quietly.
    [string]$CertEmail       = '',

    [string]$PgHost          = 'localhost',
    [string]$PgPort          = '5432',
    [string]$PgDatabase      = 'qmgr',
    # A DEDICATED role, not the shared 'postgres' superuser. This box runs many other unrelated
    # production services (ERP, CashBook, evolweb, evol-api, evol-ui, docmgr, 'must', maryhill)
    # on the same Postgres instance — rotating the shared superuser's password to satisfy one
    # app's own credential hygiene would break every one of them. A dedicated role scopes any
    # future password change to Q-Mgr alone. (An earlier version of this default pointed at
    # 'postgres' directly — wrong call, reverted.)
    [string]$PgUser          = 'qmgr_app',
    # Matches ERP's build-saas-linux.ps1 convention exactly: not mandatory, defaults to an
    # obviously-fake placeholder rather than a real secret. Pass the real password to bake it
    # into this build's appsettings.Production.json; omit it and the package ships the
    # placeholder, install.sh installs it from the .template on first install, and the operator
    # edits it by hand on the server. On an UPGRADE, install.sh always preserves whatever
    # appsettings.Production.json is already on the server regardless of what this build baked.
    [string]$PgPassword      = '__SET_ON_SERVER__',

    [string]$JwtSecret       = '',   # blank -> auto-generate a random 64-byte secret for this build

    # ---- Platform email -------------------------------------------------------------------------
    # The mailbox every tenant falls back to when it has not configured its own SMTP (see
    # ISmtpProfileResolver). These land in qmgr-api.service as Environment=Email__* rather than in
    # appsettings.Production.json, for the same reason MediaStorage__PublicBaseUrl does: install.sh
    # PRESERVES the API's appsettings.Production.json on every upgrade, so a key added there never
    # reaches a server that already exists. The unit is replaced on every install, so it does.
    #
    # The PASSWORD is never a committed default. Order of resolution:
    #   1. -SmtpPassword on the command line
    #   2. scripts/deploy/secrets.local.json  { "SmtpPassword": "..." }   <- untracked, .gitignored
    #   3. nothing -> no Email__SmtpPassword line, PlatformEmailDefaults treats the section as
    #      unconfigured, and mail is SKIPPED rather than failing. The build warns.
    [string]$SmtpHost        = 'smtp.ionos.com',
    [int]$SmtpPort           = 587,
    [bool]$SmtpUseSsl        = $true,
    [string]$SmtpUsername    = 'info@sacc.ug',
    [string]$SmtpFromEmail   = 'info@sacc.ug',
    [string]$SmtpFromName    = 'Q-Mgr',
    [string]$SmtpPassword    = '',

    [switch]$SkipClean,
    [switch]$SkipPublish
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ApiProject = Join-Path $RepoRoot 'src/Q-Mgr.API'
$WebProject = Join-Path $RepoRoot 'src/Q-Mgr.Web'
$TotalSteps = 9

Write-Header "Q-Mgr Linux Deployment Build"
Write-Info "Repo root : $RepoRoot"
Write-Info "Target    : $TargetRuntime / $Configuration"
Write-Info "Host      : $HostName (path-based; / -> Web:$WebPort, /api/ + /hubs/ -> API:$ApiPort)"

# ── Step 1: version + clean ─────────────────────────────────────────────────
Write-Step 1 $TotalSteps 'Resolving version and cleaning artefacts'
$BuildVersion = Get-RepoBuildVersion -RepoRoot $RepoRoot
Write-Info "Build version: $BuildVersion"

if (-not $SkipClean) {
    Invoke-CleanArtefacts -RepoRoot $RepoRoot -ProjectDirs @('src/Q-Mgr.API', 'src/Q-Mgr.Web') -Configuration $Configuration -OutputPath $OutputPath
} else {
    Write-Warn 'Skipping clean (-SkipClean)'
}
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

Write-BuildInfo -RepoRoot $RepoRoot -BuildVersion $BuildVersion -Configuration $Configuration -Runtime $TargetRuntime -Mode 'linux-onehost'
Write-Success "BuildInfo.cs written (src/Q-Mgr.Shared/BuildInfo.cs) — shared by both API and Web"

# ── Step 2: publish API ──────────────────────────────────────────────────────
Write-Step 2 $TotalSteps 'Publishing Q-Mgr.API (self-contained, single-file)'
$apiPublishDir = Join-Path $OutputPath 'api'
$apiArgs = @(
    'publish', $ApiProject,
    '-c', $Configuration,
    '-r', $TargetRuntime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',   # fixed, not configurable — reflection-based serialization (Mediator handlers, System.Text.Json) breaks under trimming
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-o', $apiPublishDir
)
if (-not $SkipPublish) {
    $ok = Invoke-DotnetPublishWithRetry -DotnetArgs $apiArgs -ExpectedExePath (Join-Path $apiPublishDir 'Q-Mgr.API')
    if (-not $ok) { throw 'Q-Mgr.API publish failed.' }
    Write-Success "Q-Mgr.API published -> $apiPublishDir"
} else {
    Write-Warn 'Skipping publish (-SkipPublish)'
}

# ── Step 3: publish Web ──────────────────────────────────────────────────────
Write-Step 3 $TotalSteps 'Publishing Q-Mgr.Web (self-contained, single-file)'
$webPublishDir = Join-Path $OutputPath 'web'
$webArgs = @(
    'publish', $WebProject,
    '-c', $Configuration,
    '-r', $TargetRuntime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-o', $webPublishDir
)
if (-not $SkipPublish) {
    $ok = Invoke-DotnetPublishWithRetry -DotnetArgs $webArgs -ExpectedExePath (Join-Path $webPublishDir 'Q-Mgr.Web')
    if (-not $ok) { throw 'Q-Mgr.Web publish failed.' }
    Write-Success "Q-Mgr.Web published -> $webPublishDir"
} else {
    Write-Warn 'Skipping publish (-SkipPublish)'
}

# ── Step 4: strip publish noise, guard against runtime uploads ─────────────
Write-Step 4 $TotalSteps 'Stripping publish noise'
Remove-PublishNoise -PublishDir $apiPublishDir
Remove-PublishNoise -PublishDir $webPublishDir
Write-Success 'Publish directories clean'

# ── Step 5: service-worker cache-bust + static asset pre-compression ───────
Write-Step 5 $TotalSteps 'Stamping service worker cache + pre-compressing static assets'
$swPath = Join-Path $webPublishDir 'wwwroot/service-worker.js'
if (Test-Path $swPath) {
    $swContent = Get-Content $swPath -Raw
    $swContent = $swContent -replace "(const CACHE_NAME\s*=\s*')[^']*(')", "`${1}qmgr-cache-$BuildVersion`$2"
    $swContent = $swContent -replace "(const CACHE_VERSION\s*=\s*')[^']*(')", "`${1}$BuildVersion`$2"
    [System.IO.File]::WriteAllText($swPath, $swContent, $script:Utf8NoBom)
    Write-Success "service-worker.js stamped with build $BuildVersion"
} else {
    Write-Info 'No service-worker.js found in publish output — skipping stamp'
}
Optimize-StaticAssets -WwwrootDir (Join-Path $webPublishDir 'wwwroot')
Optimize-StaticAssets -WwwrootDir (Join-Path $apiPublishDir 'wwwroot')

# ── Step 6: generate appsettings.Production.json overlays ──────────────────
Write-Step 6 $TotalSteps 'Generating production configuration overlays'

$resolvedJwtSecret = if ($JwtSecret) { $JwtSecret } else {
    $bytes = New-Object byte[] 64
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToBase64String($bytes)
}
$dbConnString = "Host=$PgHost;Port=$PgPort;Database=$PgDatabase;Username=$PgUser;Password=$PgPassword"

# Platform SMTP password: command line, else the untracked local secrets file, else nothing.
$resolvedSmtpPassword = $SmtpPassword
if (-not $resolvedSmtpPassword) {
    $secretsFile = Join-Path $PSScriptRoot 'secrets.local.json'
    if (Test-Path $secretsFile) {
        try {
            $secrets = Get-Content $secretsFile -Raw | ConvertFrom-Json
            if ($secrets.SmtpPassword) {
                $resolvedSmtpPassword = [string]$secrets.SmtpPassword
                Write-Info "SMTP password read from scripts/deploy/secrets.local.json"
            }
        } catch {
            Write-Warn "secrets.local.json could not be read ($($_.Exception.Message)) — continuing without an SMTP password."
        }
    }
}
if (-not $resolvedSmtpPassword) {
    Write-Warn "No SMTP password (-SmtpPassword or scripts/deploy/secrets.local.json). The package ships with platform email UNCONFIGURED: tenants that have not set their own SMTP will have mail skipped, not failed."
}

$apiAppSettingsProd = [ordered]@{
    ConnectionStrings = [ordered]@{
        DefaultConnection = $dbConnString
        Redis             = ''
    }
    JWT = [ordered]@{
        Secret        = $resolvedJwtSecret
        Issuer        = 'qmgr-api'
        Audience      = 'qmgr-clients'
        ExpiryMinutes = '60'
    }
    App = [ordered]@{
        PublicWebBaseUrl = "https://$HostName"
    }
    # MediaStorage:PublicBaseUrl is deliberately NOT here. install.sh preserves this file on every
    # upgrade, so a key added to it never reaches an existing server. It is set in qmgr-api.service
    # as Environment=MediaStorage__PublicBaseUrl instead; see LocalDiskMediaStorageService and
    # UploadLinkRepair for why uploads need it.
    # See the -DataProtectionPath parameter for why this must be set explicitly rather than left
    # to Program.cs's AppContext.BaseDirectory default.
    DataProtection = [ordered]@{
        KeyPath = $DataProtectionPath
    }
    Cors = [ordered]@{
        AllowedOrigins = @("https://$HostName")
    }
    Serilog = [ordered]@{
        MinimumLevel = [ordered]@{
            Default  = 'Warning'
            Override = [ordered]@{
                'Microsoft'                     = 'Warning'
                'Microsoft.Hosting.Lifetime'    = 'Information'
                'Microsoft.EntityFrameworkCore' = 'Warning'
                'QMgr'                          = 'Information'
            }
        }
    }
    # NOT locked to $HostName. Q-Mgr.API is never directly internet-facing (binds 127.0.0.1
    # only; nginx is the real public boundary and already forwards the correct Host header for
    # real external traffic) — but Q-Mgr.Web's OWN internal HttpClient calls the API directly via
    # ApiBaseUrl (http://127.0.0.1:$ApiPort), so every one of Web's server-side API calls carries
    # Host: 127.0.0.1:$ApiPort, not $HostName. Locking AllowedHosts here doesn't add real
    # protection (nginx already gates public access) but DOES silently 400 every one of Web's own
    # requests before they reach any controller — found live: this is exactly what made every
    # login attempt fail with a generic "No account found" (AuthService.IdentifyUserAsync treats
    # any non-2xx as "not found"), with nothing in the API's own logs since HostFiltering rejects
    # before the request ever reaches AuthController.
    AllowedHosts = '*'
    SaaS = [ordered]@{
        BaseDomain         = $HostSuffix
        BaseUrl            = "https://$HostSuffix"
        TrialDays          = 14
        DefaultPlanCode    = 'free'
        AllowCustomDomains = $true
    }
}
$apiSettingsPath = Join-Path $apiPublishDir 'appsettings.Production.json'
$apiSettingsJson = $apiAppSettingsProd | ConvertTo-Json -Depth 10
$apiSettingsJson | Set-Content -Path $apiSettingsPath -Encoding UTF8
# .template ships alongside so install.sh can diff an operator-edited server copy against the
# new build's shape on every upgrade — same convention as ERP's build-saas-linux.ps1.
$apiSettingsJson | Set-Content -Path "$apiSettingsPath.template" -Encoding UTF8
Write-Success "Wrote appsettings.Production.json (+ .template) for API (DB, JWT secret, CORS locked to https://$HostName)"
if ($PgPassword -eq '__SET_ON_SERVER__') {
    Write-Warn "PgPassword not supplied — API's appsettings.Production.json ships with the '__SET_ON_SERVER__' placeholder. Set the real password on the server before first start."
}

$webAppSettingsProd = [ordered]@{
    ApiBaseUrl = "http://127.0.0.1:$ApiPort"   # internal loopback call from Web -> API; nginx never sees this hop
    # BUG FIX: distinct from ApiBaseUrl on purpose. ApiBaseUrl is for Web's own server-side HTTP
    # calls to the API and must stay the fast internal loopback - but a human-facing link (the
    # "API Documentation" link) is opened by the *browser*, which cannot reach 127.0.0.1 on the
    # server at all. nginx's path-based routing (/api/ -> API) means the public hostname itself is
    # the correct browser-facing address for that link; found live when the docs link opened
    # 127.0.0.1 even against a "production" build.
    ApiPublicUrl = "https://$HostName"
    # The SAME key ring the API uses, and for the same reason — see -DataProtectionPath. Added
    # 2026-09-10 alongside the AddDataProtection call in Q-Mgr.Web/Program.cs, which the Web
    # project had never had: without it the Web app fell back to an EPHEMERAL key ring, so
    # antiforgery tokens stopped validating after every restart. That failed soft (a warning, not
    # an exception), which is why it went unnoticed while the identical gap in the API produced a
    # 500 on every visitor check-in.
    #
    # This line and the ReadWritePaths entry on the Web unit are a pair: setting the path without
    # making it writable would turn today's soft degradation into a hard startup failure.
    DataProtection = [ordered]@{
        KeyPath = $DataProtectionPath
    }
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default              = 'Warning'
            'Microsoft.AspNetCore' = 'Warning'
        }
    }
    # NOT locked to $HostName, and this one cost a night. ASP.NET Core's HostFiltering middleware
    # answers any Host it does not recognise with "Bad Request - Invalid Hostname", before the
    # request reaches a single component — so EVERY TENANT CUSTOM DOMAIN was refused by the app the
    # moment its certificate and nginx block were finally right. dashboard.maryhillug.net went from a
    # certificate warning straight to a 400, which reads like a different fault and is the same one:
    # a host list baked at build time cannot contain a domain a school adds next week.
    #
    # Nothing reaches this process except through nginx, which has an explicit server_name block per
    # host and NO WILDCARD ANYWHERE (deliberately — see the tenant-domain notes in CLAUDE.md). nginx
    # is the host filter; repeating it here only adds a list that goes stale. The API next to this
    # already runs with '*' for the same class of reason.
    AllowedHosts = '*'
}
$webSettingsPath = Join-Path $webPublishDir 'appsettings.Production.json'
$webSettingsJson = $webAppSettingsProd | ConvertTo-Json -Depth 10
$webSettingsJson | Set-Content -Path $webSettingsPath -Encoding UTF8
$webSettingsJson | Set-Content -Path "$webSettingsPath.template" -Encoding UTF8
Write-Success 'Wrote appsettings.Production.json (+ .template) for Web (ApiBaseUrl -> internal loopback)'

# Never let the DEV appsettings.json (hardcoded 'sav' password, dev JWT secret) reach the
# server — install.sh only copies what's in the package, and dev secrets have no business
# leaving this machine even inside a private tarball.
foreach ($dir in @($apiPublishDir, $webPublishDir)) {
    $devSettings = Join-Path $dir 'appsettings.json'
    if (Test-Path $devSettings) {
        $scrubbed = [ordered]@{ '_NOTE' = 'Dev appsettings.json intentionally scrubbed by build-linux.ps1 — see appsettings.Production.json.' }
        ($scrubbed | ConvertTo-Json) | Set-Content -Path $devSettings -Encoding UTF8
    }
    $devLocal = Join-Path $dir 'appsettings.Development.json'
    if (Test-Path $devLocal) { Remove-Item $devLocal -Force }
}
Write-Success 'Scrubbed dev secrets from packaged appsettings.json'

# ── Step 7: nginx + systemd + install.sh ────────────────────────────────────
Write-Step 7 $TotalSteps 'Generating nginx site, systemd units, and install.sh'
# 'config' for the generated unit/nginx files, matching CRM's own package layout convention
# (E:\CRM\scripts\nginx\build.ps1). install.sh itself is written straight into $OutputPath
# (the package root), NOT into this subfolder — see the note above the install.sh heredoc for
# why that placement is load-bearing, not cosmetic.
$genDir = Join-Path $OutputPath 'config'
New-Item -ItemType Directory -Force -Path $genDir | Out-Null

# --- nginx ---
# Cert paths deliberately match build-saas-linux.ps1's own wildcard convention exactly —
# same server, same shared cert, per explicit instruction to "refer to the build script of erp".
$nginxConf = @"
# Q-Mgr — generated by scripts/deploy/build-linux.ps1 — build $BuildVersion
# Single host, path-based routing: / -> Web ($WebPort), /api/ + /hubs/ -> API ($ApiPort)

map `$http_upgrade `$connection_upgrade {
    default upgrade;
    ''      close;
}

# ---- Tenant custom domains ----
# One file per verified tenant domain, written by /usr/local/bin/qmgr-tenant-domain when a domain
# passes verification and removed when it is released. The glob matches nothing on a fresh install,
# which nginx accepts silently. It is at http level because each file declares its own `server`.
include $TenantDomainConfPath/*.conf;

limit_req_zone `$binary_remote_addr zone=qmgr_general:10m rate=20r/s;
limit_req_zone `$binary_remote_addr zone=qmgr_auth:10m rate=5r/s;

server {
    listen 80;
    listen [::]:80;
    server_name $HostName;
    return 301 https://`$host`$request_uri;
}

server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name $HostName;

    ssl_certificate     /etc/ssl/certs/$HostSuffix.crt;
    ssl_certificate_key /etc/ssl/private/$HostSuffix.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    # Zone name is namespaced ('qmgr_ssl', not the generic 'SSL') so it can never collide with
    # another site's shared-memory zone of the same name but a different size — nginx refuses to
    # start if two config files declare the same zone name with different sizes. Hit exactly this
    # on first deploy: another already-enabled site's config (a generic 'SSL' zone) collided with
    # this one. Matches CRM's own build script's convention (E:\CRM\scripts\nginx\build.ps1 uses
    # 'CRM_SSL' for the same reason) — namespace every shared zone per-project, always.
    ssl_session_cache shared:qmgr_ssl:10m;
    ssl_session_timeout 1d;

    client_max_body_size 50m;

    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;

    # ---- API (REST) ----
    location /api/ {
        limit_req zone=qmgr_general burst=40 nodelay;
        proxy_pass http://127.0.0.1:$ApiPort;
        proxy_http_version 1.1;
        proxy_set_header Host `$host;
        proxy_set_header X-Real-IP `$remote_addr;
        proxy_set_header X-Forwarded-For `$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto `$scheme;
    }

    location ~ ^/api/v1/auth/(token|login)`$ {
        limit_req zone=qmgr_auth burst=10 nodelay;
        proxy_pass http://127.0.0.1:$ApiPort;
        proxy_http_version 1.1;
        proxy_set_header Host `$host;
        proxy_set_header X-Real-IP `$remote_addr;
        proxy_set_header X-Forwarded-For `$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto `$scheme;
    }

    # ---- OpenAPI document + Scalar API reference (api/docs is under /api/ already; the
    # generated OpenAPI JSON Scalar's own page fetches is served at a separate top-level
    # /openapi/ path, not /api/openapi/ - without this block it would silently fall through to
    # the catch-all "location /" below and 404 against Web instead of the API). ----
    location /openapi/ {
        limit_req zone=qmgr_general burst=40 nodelay;
        proxy_pass http://127.0.0.1:$ApiPort;
        proxy_http_version 1.1;
        proxy_set_header Host `$host;
        proxy_set_header X-Real-IP `$remote_addr;
        proxy_set_header X-Forwarded-For `$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto `$scheme;
    }

    # ---- API SignalR hubs (QueueHub, DisplayHub, NotificationHub) ----
    location /hubs/ {
        proxy_pass http://127.0.0.1:$ApiPort;
        proxy_http_version 1.1;
        proxy_set_header Upgrade `$http_upgrade;
        proxy_set_header Connection `$connection_upgrade;
        proxy_set_header Host `$host;
        proxy_set_header X-Real-IP `$remote_addr;
        proxy_set_header X-Forwarded-For `$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto `$scheme;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    # ---- Uploaded files (served by the API's UploadsController from $UploadsPath/media) ----
    # Every upload's link is https://$HostName/uploads/... (MediaStorage:PublicBaseUrl). Without
    # this block the catch-all "location /" sent those requests to Web, which has no such files,
    # so signage PDFs, images and attachments all 404ed. No limit_req: one display loading a
    # playlist fetches many files from a single IP. Since 2026-09-15 the API answers these with a
    # per-file authorisation decision (public for signage, signed token or record permission for
    # everything else) — they are no longer static files.
    location /uploads/ {
        proxy_pass http://127.0.0.1:$ApiPort;
        proxy_http_version 1.1;
        proxy_set_header Host `$host;
        proxy_set_header X-Real-IP `$remote_addr;
        proxy_set_header X-Forwarded-For `$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto `$scheme;
    }

    # ---- API health check (not proxied to Web) ----
    location = /api-health {
        proxy_pass http://127.0.0.1:$ApiPort/health;
        access_log off;
    }

    # ---- Blazor Server static assets (pre-compressed) ----
    location /_framework/ {
        gzip_static on;
        proxy_pass http://127.0.0.1:$WebPort;
        proxy_http_version 1.1;
        proxy_set_header Host `$host;
        expires 7d;
        add_header Cache-Control "public, immutable";
    }

    # ---- Blazor Server circuit (SignalR over /_blazor) ----
    location /_blazor {
        proxy_pass http://127.0.0.1:$WebPort;
        proxy_http_version 1.1;
        proxy_set_header Upgrade `$http_upgrade;
        proxy_set_header Connection `$connection_upgrade;
        proxy_set_header Host `$host;
        proxy_set_header X-Real-IP `$remote_addr;
        proxy_set_header X-Forwarded-For `$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto `$scheme;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    # ---- Everything else -> Web ----
    location / {
        limit_req zone=qmgr_general burst=40 nodelay;
        proxy_pass http://127.0.0.1:$WebPort;
        proxy_http_version 1.1;
        proxy_set_header Upgrade `$http_upgrade;
        proxy_set_header Connection `$connection_upgrade;
        proxy_set_header Host `$host;
        proxy_set_header X-Real-IP `$remote_addr;
        proxy_set_header X-Forwarded-For `$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto `$scheme;
    }
}
"@
Write-LinuxText -Path (Join-Path $genDir 'qmgr.nginx.conf') -Content $nginxConf
Write-Success 'nginx config generated (qmgr.nginx.conf)'

# --- tenant custom-domain helper ---
# THE ONLY THING THE API IS ALLOWED TO RUN AS ROOT, and it is deliberately tiny. nginx's
# configuration directory has to be written and nginx reloaded — both root — while the API runs as
# www-data under ProtectSystem=strict and must keep doing so. Handing a web application the
# ability to rewrite the web server's configuration is a far larger grant than handing it one
# argument-validated command, which is what this is.
#
# THE CERTIFICATE IS A HYBRID, and the order matters (two user decisions, both 2026-09-21).
#
# "we already have the certificate configured, and nicely running for other projects" — so the
# certificate this box already carries is the FAST PATH. A tenant on a subdomain of the platform's
# own base domain is covered by its wildcard and goes live with nothing issued, nothing to renew
# and nothing that can be rate-limited.
#
# "external domains like dashboard.maryhillug.net should be supported also. this is the reason for
# whitelabelling" — and that certificate can never cover somebody else's domain, so such a domain
# is issued one of its own here, over http-01. THIS IS NOT A NEW DEPENDENCY ON THIS BOX: it already
# runs an ACME client and already carries per-subdomain Let's Encrypt certificates for other
# applications beside Q-Mgr (admissions.maryhillug.net, read off the live host on 2026-09-21).
# If it did not, the helper REFUSES rather than installing anything.
#
# Note there is no wildcard server_name anywhere and there should not be: every tenant domain,
# subdomain or not, gets its own server block in this directory when it is activated, and the site
# file includes them. A wildcard on a box shared with ERP, CashBook and the rest would quietly
# catch hostnames that belong to somebody else.
#
# It validates the domain itself rather than trusting the caller: this is the privilege boundary,
# so the check has to be on this side of it.
$tenantHelper = @"
#!/bin/bash
# Q-Mgr tenant custom-domain helper — generated by scripts/deploy/build-linux.ps1 — build $BuildVersion
# Run as root via a narrow sudoers entry. Usage: qmgr-tenant-domain enable|disable <domain>
set -euo pipefail

CONF_DIR='$TenantDomainConfPath'
ACME_ROOT='$AcmeWebroot'
CERT_FILE='/etc/ssl/certs/$HostSuffix.crt'
CERT_EMAIL='$CertEmail'
CERT_KEY='/etc/ssl/private/$HostSuffix.key'
WEB_PORT='$WebPort'
API_PORT='$ApiPort'
PLATFORM_HOST='$HostName'

VERB="`${1:-}"
DOMAIN="`${2:-}"
SPOOL='$DomainSpoolPath'

# ---- --drain: how this helper is actually reached in production --------------------------------
# NOT sudo. The API unit sets NoNewPrivileges=true, which is precisely the flag that stops a setuid
# binary from gaining privilege — so ``sudo qmgr-tenant-domain`` could never run, and for one day it
# did not: "sudo: The 'no new privileges' flag is set, which prevents sudo from running as root"
# against every activation, while verification, DNS and certbot were all working.
#
# So the web process does not escalate at all now. It writes a request file into the spool as
# www-data; qmgr-domain-worker.path notices it, and THIS script drains the spool as root. That is a
# narrower grant than the sudoers entry it replaces: a compromised API can cause exactly this one
# helper to run, and no other setuid binary on the box.
#
# It re-enters itself for each request, so the validation below and the work below remain the single
# path — the drain is a wrapper, never a second implementation.
if [ "`$VERB" = "--drain" ]; then
    shopt -s nullglob
    for req in "`$SPOOL"/*.req; do
        id="`$(basename "`$req" .req)"
        action=''; target=''
        read -r action target < "`$req" || true
        if out="`$("`$0" "`$action" "`$target" 2>&1)"; then code=0; else code=`$?; fi
        # Written under a temporary name and moved into place: the API polls for the .res file and
        # must never read one that is half-written.
        {
            echo "exit=`$code"
            printf '%s\n' "`$out"
        } > "`$SPOOL/`$id.res.tmp"
        chown root:www-data "`$SPOOL/`$id.res.tmp" 2>/dev/null || true
        chmod 0660 "`$SPOOL/`$id.res.tmp" 2>/dev/null || true
        mv -f "`$SPOOL/`$id.res.tmp" "`$SPOOL/`$id.res"
        rm -f "`$req"
    done
    # A result nobody collected (the API restarted mid-wait) is rubbish after an hour.
    find "`$SPOOL" -maxdepth 1 -name '*.res' -mmin +60 -delete 2>/dev/null || true
    exit 0
fi

# The privilege boundary is here, so the validation is here. Lower-case letters, digits, hyphens
# and dots only, at least three labels (an apex cannot be CNAMEd, so it is never a tenant domain),
# and never the platform's own host — which would overwrite the real site's server block.
if ! [[ "`$DOMAIN" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?){2,}`$ ]]; then
    echo "refused: '`$DOMAIN' is not a valid tenant domain" >&2
    exit 2
fi
if [ "`$DOMAIN" = "`$PLATFORM_HOST" ]; then
    echo "refused: that is the platform's own host" >&2
    exit 2
fi

CONF="`$CONF_DIR/`$DOMAIN.conf"

reload() {
    if ! nginx -t; then
        echo "nginx rejected the configuration; rolling back" >&2
        rm -f "`$CONF"
        nginx -t && systemctl reload nginx || true
        exit 3
    fi
    systemctl reload nginx
}

case "`$VERB" in
  enable)
    mkdir -p "`$CONF_DIR" "`$ACME_ROOT"

    # 1) WHICH CERTIFICATE? This is a HYBRID and the order is the point.
    #
    #    The shared certificate on this box is the FAST PATH: a tenant on a subdomain of the
    #    platform's own base domain is covered by its wildcard and goes live with no issuance at
    #    all, nothing to renew and nothing that can rate-limit. That is the common case.
    #
    #    A tenant's OWN domain can never be covered by it — the certificate answers for the
    #    platform's names and nobody else's — so that domain gets a certificate of its own, issued
    #    here over http-01. This is not a new dependency on this box: it already runs an ACME
    #    client and already carries per-subdomain Let's Encrypt certificates for other applications
    #    beside Q-Mgr. If it did not, this REFUSES rather than installing anything.
    #
    #    Taking a tenant live behind a name mismatch is the one outcome neither branch may produce,
    #    so every failure below names the step and stops.
    # The shared certificate is only a CANDIDATE. If it is missing or expired that is no longer
    # fatal: a domain it cannot cover was always going to be issued one of its own.
    covered=0
    CERT_NAMES=''
    if [ -r "`$CERT_FILE" ] && openssl x509 -noout -checkend 0 -in "`$CERT_FILE" >/dev/null 2>&1; then
        # Every name it answers for: the SANs, plus the common name for an older certificate that
        # carries no SAN extension at all.
        CERT_NAMES=`$( { openssl x509 -noout -ext subjectAltName -in "`$CERT_FILE" 2>/dev/null || true; \
                         openssl x509 -noout -subject -in "`$CERT_FILE" 2>/dev/null || true; } \
                       | tr ',' '\n' \
                       | sed -n -e 's/.*DNS:[[:space:]]*\([A-Za-z0-9.*-]*\).*/\1/p' \
                                -e 's/.*CN[[:space:]]*=[[:space:]]*\([A-Za-z0-9.*-]*\).*/\1/p' )
        for name in `$CERT_NAMES; do
            if [ "`$name" = "`$DOMAIN" ]; then covered=1; break; fi
            # A wildcard matches exactly ONE label, so a.b.example.com is NOT covered by *.example.com.
            if [ "`${name#\*.}" != "`$name" ] && [ "`${DOMAIN#*.}" = "`${name#\*.}" ]; then covered=1; break; fi
        done
    fi

    if [ "`$covered" = "1" ]; then
        # THE FAST PATH. Nothing is issued, nothing renews, nothing can be rate-limited.
        CERT_USE="`$CERT_FILE"
        KEY_USE="`$CERT_KEY"
        echo "using the shared certificate: it already answers for `$DOMAIN"
    else
        LE_DIR="/etc/letsencrypt/live/`$DOMAIN"
        if [ -r "`$LE_DIR/fullchain.pem" ] && openssl x509 -noout -checkend 0 -in "`$LE_DIR/fullchain.pem" >/dev/null 2>&1; then
            echo "reusing the certificate already issued for `$DOMAIN"
        else
            if ! command -v certbot >/dev/null 2>&1; then
                echo "refused: `$DOMAIN is not covered by `$CERT_FILE and certbot is not installed." >&2
                echo "It answers for: `$(echo `$CERT_NAMES | tr '\n' ' ')" >&2
                echo "Q-Mgr never installs anything on this box. Install an ACME client, or add" >&2
                echo "`$DOMAIN to that certificate, then run this again." >&2
                exit 5
            fi

            # THE CHICKEN AND THE EGG. http-01 asks for a file over PORT 80 AT THIS HOSTNAME, and
            # the server block that would serve it is the one we are here to write. So write the
            # port-80 half FIRST, reload, issue, and only then write the whole thing. Without this
            # the challenge falls through to whatever the default server happens to be and a FIRST
            # issuance can never succeed — while renewals would, because by then the block exists.
            # That is the shape of bug that passes every test and fails on the first real customer.
            cat > "`$CONF" <<EOF
server {
    listen 80;
    listen [::]:80;
    server_name `$DOMAIN;
    location /.well-known/acme-challenge/ { root `$ACME_ROOT; }
    location / { return 503; }
}
EOF
            reload

            # --cert-name and Q-Mgr's OWN webroot keep this clear of whatever else on this box
            # renews its certificates. --keep-until-expiring makes a repeat run a no-op instead of
            # a request against Let's Encrypt's five-duplicates-a-week limit; the caller's own
            # back-off is what keeps a FAILING domain from spending the box's budget, and it is a
            # hard requirement rather than politeness.
            EMAIL_ARG='--register-unsafely-without-email'
            if [ -n "`$CERT_EMAIL" ]; then EMAIL_ARG="-m `$CERT_EMAIL"; fi
            if ! certbot certonly --webroot -w "`$ACME_ROOT" -d "`$DOMAIN" \
                    --cert-name "`$DOMAIN" --keep-until-expiring \
                    --non-interactive --agree-tos `$EMAIL_ARG; then
                # Leave nothing behind. A stranded port-80 block answers 503 for a domain that is
                # not live, which reads worse than the domain simply not resolving yet.
                rm -f "`$CONF"
                reload
                echo "refused: could not issue a certificate for `$DOMAIN." >&2
                echo "Check that `$DOMAIN points at this server and that port 80 reaches it." >&2
                echo "certbot's own reason is in /var/log/letsencrypt/letsencrypt.log." >&2
                exit 6
            fi
        fi
        CERT_USE="`$LE_DIR/fullchain.pem"
        KEY_USE="`$LE_DIR/privkey.pem"
    fi

    # 2) The real block. Same proxy rules as the platform host — the tenant domain serves the very
    #    same application and only the Host header differs, which is what TenantResolutionMiddleware
    #    reads. Duplicating the routing rather than including a fragment is deliberate: a shared
    #    fragment would make a change to the main site silently change every tenant's site too.
    cat > "`$CONF" <<EOF
server {
    listen 80;
    listen [::]:80;
    server_name `$DOMAIN;
    location /.well-known/acme-challenge/ { root `$ACME_ROOT; }
    location / { return 301 https://\`$host\`$request_uri; }
}

server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name `$DOMAIN;

    ssl_certificate     `$CERT_USE;
    ssl_certificate_key `$KEY_USE;
    ssl_protocols TLSv1.2 TLSv1.3;

    client_max_body_size 50m;
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header X-Content-Type-Options "nosniff" always;

    location /.well-known/acme-challenge/ { root `$ACME_ROOT; }

    location /api/ {
        proxy_pass http://127.0.0.1:`$API_PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \`$host;
        proxy_set_header X-Real-IP \`$remote_addr;
        proxy_set_header X-Forwarded-For \`$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \`$scheme;
    }

    location /hubs/ {
        proxy_pass http://127.0.0.1:`$API_PORT;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \`$http_upgrade;
        proxy_set_header Connection \`$connection_upgrade;
        proxy_set_header Host \`$host;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location /uploads/ {
        proxy_pass http://127.0.0.1:`$API_PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \`$host;
        proxy_set_header X-Real-IP \`$remote_addr;
        proxy_set_header X-Forwarded-For \`$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \`$scheme;
    }

    location /_framework/ {
        gzip_static on;
        proxy_pass http://127.0.0.1:`$WEB_PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \`$host;
        expires 7d;
        add_header Cache-Control "public, immutable";
    }

    location /_blazor {
        proxy_pass http://127.0.0.1:`$WEB_PORT;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \`$http_upgrade;
        proxy_set_header Connection \`$connection_upgrade;
        proxy_set_header Host \`$host;
        proxy_set_header X-Real-IP \`$remote_addr;
        proxy_set_header X-Forwarded-For \`$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \`$scheme;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location / {
        proxy_pass http://127.0.0.1:`$WEB_PORT;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \`$http_upgrade;
        proxy_set_header Connection \`$connection_upgrade;
        proxy_set_header Host \`$host;
        proxy_set_header X-Real-IP \`$remote_addr;
        proxy_set_header X-Forwarded-For \`$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \`$scheme;
    }
}
EOF
    reload

    # The expiry of the certificate THIS domain is now served with — whichever branch above chose
    # it — so the application can log a date it did not invent. Reading the shared certificate on
    # both branches would report the platform's own expiry for a tenant served off its own
    # certificate: a date that is real, confident and about a different certificate entirely.
    CERT_END=`$(openssl x509 -noout -enddate -in "`$CERT_USE" 2>/dev/null | cut -d= -f2- || true)
    if [ -n "`$CERT_END" ]; then
        echo "expires=`$(date -u -d "`$CERT_END" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || echo '')"
    fi
    echo "`$DOMAIN is live"
    ;;

  disable)
    # Only the server block goes, and that is deliberate on both branches. The shared certificate
    # belongs to the platform host and every other application on this box, so there is nothing to
    # revoke. A certificate issued for the tenant's OWN domain is LEFT IN PLACE and left renewing:
    # withdrawing a domain is usually temporary, a kept certificate makes re-enabling instant, and
    # revoking one on a withdrawal would spend the issuance budget again on every re-enable.
    rm -f "`$CONF"
    nginx -t && systemctl reload nginx
    echo "`$DOMAIN withdrawn"
    ;;

  *)
    echo "usage: qmgr-tenant-domain enable|disable <domain>" >&2
    exit 1
    ;;
esac
"@
Write-LinuxText -Path (Join-Path $genDir 'qmgr-tenant-domain') -Content $tenantHelper

# THE WORKER — how the helper is reached, now that it is not reached by sudo at all.
#
# The API unit sets NoNewPrivileges=true, the flag whose whole job is to stop a setuid binary from
# escalating. sudo is a setuid binary. So the sudoers entry this replaces could never be used, and
# was not: every activation of dashboard.maryhillug.net failed with "sudo: The 'no new privileges'
# flag is set, which prevents sudo from running as root" — one second after the domain verified.
#
# Weakening the API's hardening for one call a tenant makes once would be the wrong trade. Instead
# the web process stops escalating: it writes a request into the spool as www-data, and this
# root-owned worker drains it. The grant is strictly smaller than the sudoers entry — a compromised
# API can cause exactly this helper to run, and nothing else on the box.
$domainWorkerPath = @"
[Unit]
Description=Q-Mgr tenant domain requests (watch)

[Path]
PathExistsGlob=$DomainSpoolPath/*.req
Unit=qmgr-domain-worker.service

[Install]
WantedBy=multi-user.target
"@
Write-LinuxText -Path (Join-Path $genDir 'qmgr-domain-worker.path') -Content $domainWorkerPath

$domainWorkerService = @"
[Unit]
Description=Q-Mgr tenant domain worker — writes the nginx block and issues the certificate
After=network-online.target nginx.service

[Service]
Type=oneshot
# Root, deliberately and narrowly: it writes into $TenantDomainConfPath, reloads nginx and runs
# certbot. It reads only what the API left in the spool, and validates every domain itself — the
# privilege boundary is inside the helper, which is why the check lives on that side of it.
User=root
ExecStart=/usr/local/bin/qmgr-tenant-domain --drain
TimeoutStartSec=300
"@
Write-LinuxText -Path (Join-Path $genDir 'qmgr-domain-worker.service') -Content $domainWorkerService
Write-Success 'tenant custom-domain helper and root worker units generated'

# --- systemd: API ---
# Email__* in the unit rather than in appsettings.Production.json — install.sh preserves that file
# on an upgrade, the unit is always replaced. Password omitted entirely when there isn't one, so
# PlatformEmailDefaults sees an incomplete section and skips mail instead of failing every send.
$emailEnvLines = @(
    "Environment=Email__SmtpHost=$SmtpHost"
    "Environment=Email__SmtpPort=$SmtpPort"
    "Environment=Email__UseSsl=$($SmtpUseSsl.ToString().ToLowerInvariant())"
    "Environment=Email__SmtpUsername=$SmtpUsername"
    "Environment=Email__FromEmail=$SmtpFromEmail"
    "Environment=Email__FromName=$SmtpFromName"
)
if ($resolvedSmtpPassword) {
    $emailEnvLines += "Environment=Email__SmtpPassword=$resolvedSmtpPassword"
}
$emailUnitEnvironment = ($emailEnvLines -join "`n")

$apiUnit = @"
[Unit]
Description=Q-Mgr API
After=network.target postgresql.service
Wants=postgresql.service

[Service]
Type=simple
User=www-data
Group=www-data
WorkingDirectory=$InstallRoot/api
ExecStart=$InstallRoot/api/Q-Mgr.API
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:$ApiPort
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
Environment=MediaStorage__PublicBaseUrl=https://$HostName
# The key ring path MUST travel in the unit, not only in appsettings.Production.json: install.sh
# preserves the server's copy of that file on every upgrade, so the KeyPath written there on
# 2026-09-10 never reached the live server — it kept persisting keys under $InstallRoot/api,
# read-only under ProtectSystem=strict, and every Protect() call (badge QR, upload links, share
# tokens) threw a 500. Seen live on 2026-09-15: POST /public/shares/{slug}/open responded 500.
Environment=DataProtection__KeyPath=$DataProtectionPath
# The upload store, outside both wwwroot and $InstallRoot (2026-09-15). In the unit for the same
# reason as PublicBaseUrl: install.sh preserves the API's appsettings.Production.json, so a key
# added there never reaches an existing server. The unit is replaced on every install.
Environment=MediaStorage__LocalPath=$UploadsPath/media
$emailUnitEnvironment
MemoryMax=1200M

# KEPT, and the reason is worth knowing: this flag is what stopped ``sudo qmgr-tenant-domain`` from
# ever running, so every tenant-domain activation failed with "sudo: The 'no new privileges' flag is
# set". The answer was not to switch it off for one call a tenant makes once — it was to stop this
# process escalating at all. It writes into the spool below and a root worker does the work.
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=$UploadsPath /var/log/qmgr $DataProtectionPath $DomainSpoolPath

[Install]
WantedBy=multi-user.target
"@
Write-LinuxText -Path (Join-Path $genDir 'qmgr-api.service') -Content $apiUnit

# --- systemd: Web ---
$webUnit = @"
[Unit]
Description=Q-Mgr Web
After=network.target qmgr-api.service
Wants=qmgr-api.service

[Service]
Type=simple
User=www-data
Group=www-data
WorkingDirectory=$InstallRoot/web
ExecStart=$InstallRoot/web/Q-Mgr.Web
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:$WebPort
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
# Same key ring as the API, and for the same reason it is in the unit: see the API unit above.
Environment=DataProtection__KeyPath=$DataProtectionPath
MemoryMax=800M

NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
# $DataProtectionPath added 2026-09-10, when Q-Mgr.Web gained the AddDataProtection call it had
# never had. Both processes share the one key ring (same SetApplicationName), so both units need
# it writable. ProtectSystem=strict mounts everything else read-only — this is the same pairing
# whose absence on the API side made every walk-in check-in 500.
ReadWritePaths=/var/log/qmgr $DataProtectionPath

[Install]
WantedBy=multi-user.target
"@
Write-LinuxText -Path (Join-Path $genDir 'qmgr-web.service') -Content $webUnit
Write-Success 'systemd units generated (qmgr-api.service, qmgr-web.service)'

# --- database backup / restore scripts ---
# The API reads `<contentRoot>/logs/last-backup.marker` (HealthController "Last Backup"), so the
# backup script writes that marker after every successful dump. Credentials are read from the
# server's own appsettings.Production.json at run time, never baked into the script, so the
# operator-preserved config stays the single source of truth for the DB password.
$backupSh = @'
#!/usr/bin/env bash
# Q-Mgr PostgreSQL backup — generated by scripts/deploy/build-linux.ps1 — build __BUILD_VERSION__
# Runs daily from /etc/cron.d/qmgr-backup; safe to run by hand: sudo bash /var/www/sites/qmgr/bin/qmgr-backup-db.sh
set -euo pipefail

INSTALL_ROOT="__INSTALL_ROOT__"
BACKUP_DIR="${QMGR_BACKUP_DIR:-/var/backups/qmgr}"
RETENTION_DAYS="${QMGR_BACKUP_RETENTION_DAYS:-30}"
SETTINGS="$INSTALL_ROOT/api/appsettings.Production.json"
MARKER_DIR="$INSTALL_ROOT/api/logs"

if ! command -v pg_dump >/dev/null 2>&1; then
    echo "pg_dump not found — install postgresql-client (apt-get install -y postgresql-client)" >&2
    exit 2
fi
[ -r "$SETTINGS" ] || { echo "Cannot read $SETTINGS" >&2; exit 2; }

# Pull "Host=..;Port=..;Database=..;Username=..;Password=.." out of the JSON without needing jq.
CONN=$(grep -o '"DefaultConnection"[[:space:]]*:[[:space:]]*"[^"]*"' "$SETTINGS" | head -n1 | sed -E 's/^"DefaultConnection"[[:space:]]*:[[:space:]]*"//; s/"$//')
[ -n "$CONN" ] || { echo "DefaultConnection not found in $SETTINGS" >&2; exit 2; }
conn_part() { echo "$CONN" | tr ';' '\n' | grep -i "^$1=" | head -n1 | cut -d= -f2-; }
PGHOST=$(conn_part Host); PGPORT=$(conn_part Port); PGDATABASE=$(conn_part Database)
PGUSER=$(conn_part Username); PGPASSWORD=$(conn_part Password)
export PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD
: "${PGPORT:=5432}"

mkdir -p "$BACKUP_DIR" "$MARKER_DIR"
STAMP=$(date -u +%Y%m%d_%H%M%S)
OUT="$BACKUP_DIR/qmgr-$STAMP.dump"
TMP="$OUT.partial"

echo "Backing up $PGDATABASE@$PGHOST:$PGPORT -> $OUT"
pg_dump --format=custom --no-owner --no-privileges --file="$TMP"
mv "$TMP" "$OUT"
chmod 600 "$OUT"
SIZE=$(du -h "$OUT" | cut -f1)
echo "Done ($SIZE)"

# Marker the API health page reads (ISO-8601 UTC).
date -u +%Y-%m-%dT%H:%M:%SZ > "$MARKER_DIR/last-backup.marker"
chown www-data:www-data "$MARKER_DIR/last-backup.marker" 2>/dev/null || true

# Prune old dumps.
find "$BACKUP_DIR" -name 'qmgr-*.dump' -type f -mtime +"$RETENTION_DAYS" -print -delete | sed 's/^/pruned: /' || true
'@
$restoreSh = @'
#!/usr/bin/env bash
# Q-Mgr PostgreSQL restore — generated by scripts/deploy/build-linux.ps1 — build __BUILD_VERSION__
# Usage:
#   sudo bash /var/www/sites/qmgr/bin/qmgr-restore-db.sh /var/backups/qmgr/qmgr-YYYYMMDD_HHMMSS.dump            # restore over the live DB (stops the app first)
#   sudo bash /var/www/sites/qmgr/bin/qmgr-restore-db.sh <dump> --drill                                          # restore into qmgr_restore_drill only, leave the live DB untouched
set -euo pipefail

INSTALL_ROOT="__INSTALL_ROOT__"
SETTINGS="$INSTALL_ROOT/api/appsettings.Production.json"
DUMP="${1:-}"
MODE="${2:-}"
[ -n "$DUMP" ] && [ -r "$DUMP" ] || { echo "Usage: $0 <dump-file> [--drill]" >&2; exit 2; }
command -v pg_restore >/dev/null 2>&1 || { echo "pg_restore not found — install postgresql-client" >&2; exit 2; }

# ---------------------------------------------------------------------------------------------
# SUPPRESSION LIST. A tenant that has been purged must not come back because somebody restored a
# backup taken before the purge. This is the ICO's own condition for treating backup data as "put
# beyond use" rather than deleted: tell the person it still exists in backups, do not use it, and
# ensure a restore does not silently reintroduce the record.
#
# qmgr.tenant_tombstones is that list. It holds no personal data — an organization id, a date, and
# two one-way hashes — and it is written by ITenantPurgeService at the moment a purge is certified.
# ---------------------------------------------------------------------------------------------

# Reports what a RESTORED database would bring back, without changing anything. Used by the drill.
suppression_report() {
    local db="$1"
    local back
    back=$(psql -d "$db" -tAc "SELECT count(*) FROM qmgr.organizations o WHERE EXISTS (SELECT 1 FROM qmgr.tenant_tombstones t WHERE t.\"OrganizationId\" = o.\"Id\");" 2>/dev/null || echo "0")
    if [ "${back:-0}" != "0" ]; then
        echo "  SUPPRESSION: this dump would bring back $back purged tenant(s). A live restore re-deletes them automatically."
    else
        echo "  SUPPRESSION: no purged tenant would come back from this dump."
    fi
}

# Re-applies the deletion after a LIVE restore. Deliberately blunt and self-contained — it runs
# with the application stopped and cannot call into it — so it deletes by organization id from
# every table that has one, in dependency-free order by disabling triggers for the transaction.
# The application's own purge is the careful one; this is the safety net behind it, and it only
# ever has to run when somebody restores over a purge.
suppression_reapply() {
    local db="$1" ids="$2"
    [ -n "$ids" ] || { echo "Suppression list is empty — nothing to re-apply."; return 0; }

    echo "Re-applying the suppression list (purged tenants must not come back from a backup)..."
    psql -d "$db" -v ON_ERROR_STOP=1 <<SQL || echo "  WARNING: suppression re-apply failed — check qmgr.tenant_tombstones by hand"
BEGIN;
CREATE TEMP TABLE _suppressed(id uuid PRIMARY KEY);
INSERT INTO _suppressed(id) SELECT unnest(string_to_array('$ids', ','))::uuid ON CONFLICT DO NOTHING;
SET session_replication_role = replica;   -- defer FK enforcement for this transaction only
DO \$\$
DECLARE r record; n bigint; total bigint := 0;
BEGIN
  FOR r IN
    SELECT c.table_schema, c.table_name
    FROM information_schema.columns c
    JOIN information_schema.tables t
      ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
    WHERE c.column_name = 'OrganizationId' AND c.table_schema = 'qmgr'
      AND c.table_name NOT IN ('tenant_tombstones','tenant_purge_certificates','tenant_lifecycle_events')
  LOOP
    EXECUTE format('DELETE FROM %I.%I WHERE "OrganizationId" IN (SELECT id FROM _suppressed)', r.table_schema, r.table_name);
    GET DIAGNOSTICS n = ROW_COUNT; total := total + n;
  END LOOP;
  EXECUTE 'DELETE FROM qmgr.organizations WHERE "Id" IN (SELECT id FROM _suppressed)';
  GET DIAGNOSTICS n = ROW_COUNT; total := total + n;
  RAISE NOTICE 'Suppression re-applied: % row(s) removed', total;
END \$\$;
SET session_replication_role = origin;
COMMIT;
SQL
}

CONN=$(grep -o '"DefaultConnection"[[:space:]]*:[[:space:]]*"[^"]*"' "$SETTINGS" | head -n1 | sed -E 's/^"DefaultConnection"[[:space:]]*:[[:space:]]*"//; s/"$//')
conn_part() { echo "$CONN" | tr ';' '\n' | grep -i "^$1=" | head -n1 | cut -d= -f2-; }
PGHOST=$(conn_part Host); PGPORT=$(conn_part Port); LIVE_DB=$(conn_part Database)
PGUSER=$(conn_part Username); PGPASSWORD=$(conn_part Password)
export PGHOST PGPORT PGUSER PGPASSWORD
: "${PGPORT:=5432}"

if [ "$MODE" = "--drill" ]; then
    TARGET="${LIVE_DB}_restore_drill"
    echo "Restore drill: $DUMP -> $TARGET (live database $LIVE_DB is not touched)"
    psql -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS \"$TARGET\";" -c "CREATE DATABASE \"$TARGET\";"
    pg_restore --no-owner --no-privileges --dbname="$TARGET" "$DUMP"
    TABLES=$(psql -d "$TARGET" -tAc "SELECT count(*) FROM information_schema.tables WHERE table_schema='qmgr';")
    ORGS=$(psql -d "$TARGET" -tAc "SELECT count(*) FROM qmgr.organizations;" 2>/dev/null || echo "?")
    suppression_report "$TARGET"
    echo "Drill OK: $TABLES tables in schema qmgr, $ORGS organizations. Drop it when done:"
    echo "  psql -d postgres -c 'DROP DATABASE \"$TARGET\";'"
    exit 0
fi

echo "!! This will REPLACE the live database $LIVE_DB with $DUMP"
read -r -p "Type the database name to confirm: " CONFIRM
[ "$CONFIRM" = "$LIVE_DB" ] || { echo "Aborted."; exit 1; }

# The tombstones that survive in the CURRENT database are read before the restore replaces them,
# so a dump taken before a purge cannot bring the purged tenants back with it.
SUPPRESSED=$(psql -d "$LIVE_DB" -tAc "SELECT string_agg(DISTINCT \"OrganizationId\"::text, ',') FROM qmgr.tenant_tombstones;" 2>/dev/null || echo "")

systemctl stop qmgr-web.service qmgr-api.service || true
pg_restore --clean --if-exists --no-owner --no-privileges --dbname="$LIVE_DB" "$DUMP"

suppression_reapply "$LIVE_DB" "$SUPPRESSED"

systemctl start qmgr-api.service
sleep 3
systemctl start qmgr-web.service
echo "Restore complete. Check: journalctl -u qmgr-api -n 30 --no-pager"
'@
$cronFile = @'
# Q-Mgr nightly database backup — generated by scripts/deploy/build-linux.ps1
# Restore drill: sudo bash __INSTALL_ROOT__/bin/qmgr-restore-db.sh <dump> --drill
SHELL=/bin/bash
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
30 2 * * * root /bin/bash __INSTALL_ROOT__/bin/qmgr-backup-db.sh >> /var/log/qmgr/backup.log 2>&1
'@
foreach ($pair in @(
    @{ Name = 'qmgr-backup-db.sh'; Body = $backupSh },
    @{ Name = 'qmgr-restore-db.sh'; Body = $restoreSh },
    @{ Name = 'qmgr-backup.cron'; Body = $cronFile })) {
    $body = $pair.Body -replace '__BUILD_VERSION__', $BuildVersion -replace '__INSTALL_ROOT__', $InstallRoot
    Write-LinuxText -Path (Join-Path $genDir $pair.Name) -Content $body
}
Write-Success 'Database backup/restore scripts + cron generated (config/qmgr-backup-db.sh, qmgr-restore-db.sh, qmgr-backup.cron)'

# --- install.sh ---
# Same idempotent shape as ERP's: stop -> backup (keep-3, .incomplete marker) -> rsync copy
# with excludes -> restore preserved config via trap -> permissions -> install unit/nginx ->
# start -> status. Two app dirs instead of one; DB step is a no-op by design (see header note).
#
# IMPORTANT: written to the PACKAGE ROOT ($OutputPath), not into config/ alongside the unit/
# nginx files. Found the hard way: both ERP's and CRM's own deploy docs teach the operator to
# type `cd /tmp && tar -xzf <pkg> && sudo bash install.sh` from muscle memory — install.sh sits
# at the top of THEIR tarballs. A build here that nested it one level down (under server/) meant
# that exact command silently ran a STALE install.sh left over from a previous ERP deploy in the
# same /tmp instead of erroring — Q-Mgr's own install.sh was never invoked, and ERP's happened to
# re-run against ERP's own already-installed app. install.sh now lives at the tarball root so the
# same command operators already know how to type actually runs the right script.
$installSh = @'
#!/usr/bin/env bash
set -euo pipefail

# Q-Mgr install/upgrade script — generated by scripts/deploy/build-linux.ps1 — build __BUILD_VERSION__
# Run as root (or via sudo) on the target server, from inside the extracted package directory.

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

INSTALL_ROOT="__INSTALL_ROOT__"
UPLOADS_PATH="__UPLOADS_PATH__"
DP_KEYS_PATH="__DP_KEYS_PATH__"
BACKUP_ROOT="$INSTALL_ROOT/.backups"
LOG_DIR="/var/log/qmgr"
PKG_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# This build's own ports — baked in by build-linux.ps1's -ApiPort/-WebPort at build time.
API_PORT="__API_PORT__"
WEB_PORT="__WEB_PORT__"

FORCE_PORTS=0
for arg in "$@"; do
    case "$arg" in
        --force-ports) FORCE_PORTS=1 ;;
    esac
done

echo -e "${CYAN}==> Q-Mgr install/upgrade — build __BUILD_VERSION__${NC}"
echo "    This build's ports: API=$API_PORT  Web=$WEB_PORT"

if [ "$(id -u)" -ne 0 ]; then
    echo -e "${RED}This script must run as root (sudo).${NC}" >&2
    exit 1
fi

# ---- Guard: refuse to silently change ports that are already live ----
# Found the hard way (2026-09-01): a build made with the script's bare default ports of the time
# (8581/8582, which belong to CashBook and evolweb on this shared box — the defaults are 8586/8587
# as of 2026-09-21 for exactly this reason) instead of this server's actual assigned ports silently
# overwrote a working install's systemd units and nginx config with the wrong ports, crash-looping
# qmgr-web against a port something else already owned. Ports have to stay identical across nginx
# + both systemd units + Web's baked-in ApiBaseUrl for the app to work at all, so unlike
# appsettings.Production.json above, this isn't something "preserve the old file" can fix on its
# own — the fix is to detect drift against whatever is ALREADY live and refuse to proceed instead
# of silently trusting whichever ports this particular build happened to be made with.
CURRENT_API_PORT="$(sed -n 's#.*ASPNETCORE_URLS=http://127\.0\.0\.1:\([0-9]\+\).*#\1#p' /etc/systemd/system/qmgr-api.service 2>/dev/null | head -1)"
CURRENT_WEB_PORT="$(sed -n 's#.*ASPNETCORE_URLS=http://127\.0\.0\.1:\([0-9]\+\).*#\1#p' /etc/systemd/system/qmgr-web.service 2>/dev/null | head -1)"

if { [ -n "$CURRENT_API_PORT" ] && [ "$CURRENT_API_PORT" != "$API_PORT" ]; } || \
   { [ -n "$CURRENT_WEB_PORT" ] && [ "$CURRENT_WEB_PORT" != "$WEB_PORT" ]; }; then
    echo -e "${RED}==> REFUSING to proceed: this build's ports don't match what's already live on this server.${NC}"
    echo -e "${RED}    Currently live : API=${CURRENT_API_PORT:-?}  Web=${CURRENT_WEB_PORT:-?}${NC}"
    echo -e "${RED}    This build     : API=$API_PORT  Web=$WEB_PORT${NC}"
    echo ""
    if [ "$FORCE_PORTS" = "1" ]; then
        echo -e "${YELLOW}    --force-ports passed — proceeding with this build's ports anyway.${NC}"
    else
        echo "    If this port change is deliberate (e.g. these ports now collide with another"
        echo "    app on the box and Q-Mgr needs to move), confirm the new ports are actually"
        echo "    free with 'ss -tlnp' first, then re-run with --force-ports to proceed:"
        echo "        sudo bash install.sh --force-ports"
        echo ""
        echo "    Otherwise, this build was made with the wrong ports for this server — rebuild"
        echo "    it with the ports already live here and re-run install.sh normally:"
        echo "        ./build-linux.ps1 -ApiPort ${CURRENT_API_PORT:-?} -WebPort ${CURRENT_WEB_PORT:-?} ..."
        exit 1
    fi
fi

mkdir -p "$INSTALL_ROOT/api" "$INSTALL_ROOT/web" "$BACKUP_ROOT" "$UPLOADS_PATH" "$LOG_DIR" "$DP_KEYS_PATH"

# ---- 1) stop services (idempotent — ok if they don't exist yet) ----
echo -e "${YELLOW}==> [1/9] Stopping services (if running)${NC}"
systemctl stop qmgr-web.service 2>/dev/null || true
systemctl stop qmgr-api.service 2>/dev/null || true

# ---- 2) backup current install (keep last 3, marker prevents a half-written backup being restored) ----
echo -e "${YELLOW}==> [2/9] Backing up current install${NC}"
if [ -d "$INSTALL_ROOT/api" ] && [ -n "$(ls -A "$INSTALL_ROOT/api" 2>/dev/null || true)" ]; then
    STAMP="$(date +%Y%m%d-%H%M%S)"
    BACKUP_DIR="$BACKUP_ROOT/$STAMP"
    mkdir -p "$BACKUP_DIR"
    touch "$BACKUP_DIR/.incomplete"
    cp -a "$INSTALL_ROOT/api" "$BACKUP_DIR/api" 2>/dev/null || true
    cp -a "$INSTALL_ROOT/web" "$BACKUP_DIR/web" 2>/dev/null || true
    rm -f "$BACKUP_DIR/.incomplete"
    echo -e "${GREEN}    Backed up to $BACKUP_DIR${NC}"
    # keep only the 3 most recent COMPLETE backups (no .incomplete marker)
    ls -1dt "$BACKUP_ROOT"/*/ 2>/dev/null | while read -r d; do
        [ -f "${d}.incomplete" ] && continue
        true
    done
    mapfile -t COMPLETE_BACKUPS < <(for d in "$BACKUP_ROOT"/*/; do [ -f "${d}.incomplete" ] || echo "$d"; done | sort -r)
    if [ "${#COMPLETE_BACKUPS[@]}" -gt 3 ]; then
        for old in "${COMPLETE_BACKUPS[@]:3}"; do
            echo "    Pruning old backup: $old"
            rm -rf "$old"
        done
    fi
else
    echo "    No existing install found — first install, nothing to back up"
fi

# ---- 3) preserve operator-edited config across the copy ----
# API ONLY. Same pattern as ERP's build-saas-linux.ps1 install.sh: registered as a trap so it
# fires even if a later step exits early under `set -e` (a mid-copy failure must never leave the
# server with the build's own baked-in template instead of the operator's real settings). On a
# genuine first install there is nothing to restore, so the packaged appsettings.Production.json
# (and whatever password it was built with — __SET_ON_SERVER__ unless -PgPassword was passed) is
# what ends up live; the .template file ships alongside so the operator can diff their edits later.
#
# Web's appsettings.Production.json is deliberately NOT preserved — always overwritten fresh from
# the package. Found live: it has no operator-owned secret to protect (ApiBaseUrl/Logging/
# AllowedHosts are all build-computed), yet an earlier version of this script preserved it anyway.
# Across several redeploys that changed -ApiPort, Web kept silently reusing its FIRST-ever
# ApiBaseUrl instead of the new build's — every "Continue" on the login page was failing with a
# generic "No account found" because Web was calling a stale port nothing (or the wrong app) was
# listening on, while the API itself was healthy the whole time. Preserving only what's actually
# operator-owned (API's DB password/JWT secret) avoids this whole class of drift.
echo -e "${YELLOW}==> [3/9] Preserving operator-edited API appsettings.Production.json (if present)${NC}"
PRESERVED_API_SETTINGS=""

_restore_operator_settings() {
    if [ -n "${PRESERVED_API_SETTINGS:-}" ] && [ -f "$PRESERVED_API_SETTINGS" ]; then
        cp -a "$PRESERVED_API_SETTINGS" "$INSTALL_ROOT/api/appsettings.Production.json" 2>/dev/null || true
        rm -f "$PRESERVED_API_SETTINGS"
        echo -e "${GREEN}    Restored existing API appsettings.Production.json (operator-edited values preserved)${NC}"
        echo "    Diff vs new template: diff $INSTALL_ROOT/api/appsettings.Production.json{,.template}"
    else
        [ "${_COPY_DONE:-0}" = "1" ] && echo -e "${YELLOW}    Installed API appsettings.Production.json from template (no operator copy found — review DB password/JWT secret before going live)${NC}"
    fi
}
# Run on any exit — normal, set -e abort, SIGINT, SIGTERM
trap '_restore_operator_settings' EXIT INT TERM

if [ -f "$INSTALL_ROOT/api/appsettings.Production.json" ]; then
    PRESERVED_API_SETTINGS="$(mktemp)"
    cp -a "$INSTALL_ROOT/api/appsettings.Production.json" "$PRESERVED_API_SETTINGS"
fi

# ---- 4) copy new build in (rsync --delete, excluding runtime-writable dirs) ----
echo -e "${YELLOW}==> [4/9] Copying new build into place${NC}"
rsync -a --delete \
    --exclude 'wwwroot/uploads/' \
    "$PKG_DIR/api/" "$INSTALL_ROOT/api/"
rsync -a --delete \
    "$PKG_DIR/web/" "$INSTALL_ROOT/web/"
echo -e "${GREEN}    Web appsettings.Production.json installed fresh from this build (ApiBaseUrl always tracks the current -ApiPort)${NC}"

_COPY_DONE=1

# Explicit restore with messaging now, then clear the trap to avoid a double-restore on normal
# exit (the trap would still fire, but PRESERVED_API_SETTINGS is cleared below so it's a no-op).
_restore_operator_settings
trap - EXIT INT TERM
PRESERVED_API_SETTINGS=""

# ---- 5) permissions ----
echo -e "${YELLOW}==> [5/9] Setting permissions${NC}"
chown -R www-data:www-data "$INSTALL_ROOT/api" "$INSTALL_ROOT/web" "$UPLOADS_PATH" "$LOG_DIR" "$DP_KEYS_PATH"
# The key ring holds private keys — nobody but the service account has any business reading it.
chmod 700 "$DP_KEYS_PATH"
chmod -R u+rwX,go+rX,go-w "$INSTALL_ROOT/api" "$INSTALL_ROOT/web"
chmod +x "$INSTALL_ROOT/api/Q-Mgr.API" "$INSTALL_ROOT/web/Q-Mgr.Web"

# The upload store is served by the API's UploadsController from $UPLOADS_PATH/media, never as
# a static file. Earlier installs symlinked wwwroot/uploads -> $UPLOADS_PATH, which is exactly
# what exposed every upload ahead of authentication; that link is removed here (a leftover real
# directory is left alone — the API moves its files into the store at startup and logs it).
mkdir -p "$UPLOADS_PATH/media"
chown -R www-data:www-data "$UPLOADS_PATH"
if [ -L "$INSTALL_ROOT/api/wwwroot/uploads" ]; then
    rm -f "$INSTALL_ROOT/api/wwwroot/uploads"
    echo -e "${GREEN}    Removed the wwwroot/uploads symlink; uploads now go through the authorising controller${NC}"
fi

# ---- 6) systemd units ----
echo -e "${YELLOW}==> [6/9] Installing systemd units${NC}"
cp "$PKG_DIR/config/qmgr-api.service" /etc/systemd/system/qmgr-api.service
cp "$PKG_DIR/config/qmgr-web.service" /etc/systemd/system/qmgr-web.service
systemctl daemon-reload
systemctl enable qmgr-api.service qmgr-web.service
echo -e "${GREEN}    Systemd units installed and enabled${NC}"

# ---- 7) nginx ----
echo -e "${YELLOW}==> [7/9] Installing nginx site${NC}"

# Tenant custom domains. The directory must exist BEFORE the site file is tested: the site file
# includes __TENANT_CONF_PATH__/*.conf, and nginx -t fails on an include whose directory is missing.
# Both directories persist across upgrades and neither is inside the rsync'd app tree, so a deploy
# can never delete a tenant's live server block.
mkdir -p __TENANT_CONF_PATH__ __ACME_WEBROOT__
chown -R www-data:www-data __ACME_WEBROOT__
install -m 0755 -o root -g root "$PKG_DIR/config/qmgr-tenant-domain" /usr/local/bin/qmgr-tenant-domain

# The spool the API writes into and the root worker drains. 0770 root:www-data — the API adds and
# collects its own files, and gets none of root's privileges for doing so.
mkdir -p __DOMAIN_SPOOL_PATH__
chown root:www-data __DOMAIN_SPOOL_PATH__
chmod 0770 __DOMAIN_SPOOL_PATH__

# The sudoers drop-in earlier builds installed is REMOVED rather than left lying about: it never
# worked (the API unit's NoNewPrivileges=true stops sudo escalating at all, which is what every
# activation failed on), and a grant nothing uses is a grant nobody reviews.
if [ -f /etc/sudoers.d/qmgr-tenant-domain ]; then
    rm -f /etc/sudoers.d/qmgr-tenant-domain
    echo -e "${YELLOW}    Removed the old sudoers drop-in — the worker replaces it${NC}"
fi

install -m 0644 -o root -g root "$PKG_DIR/config/qmgr-domain-worker.service" /etc/systemd/system/qmgr-domain-worker.service
install -m 0644 -o root -g root "$PKG_DIR/config/qmgr-domain-worker.path" /etc/systemd/system/qmgr-domain-worker.path
systemctl daemon-reload
systemctl enable --now qmgr-domain-worker.path
echo -e "${GREEN}    Tenant custom-domain helper and worker installed${NC}"

cp "$PKG_DIR/config/qmgr.nginx.conf" /etc/nginx/sites-available/qmgr.conf
ln -sfn /etc/nginx/sites-available/qmgr.conf /etc/nginx/sites-enabled/qmgr.conf
if nginx -t; then
    echo -e "${GREEN}    nginx config valid${NC}"
else
    echo -e "${RED}    nginx config test failed — see output above. Fix it, then: systemctl reload nginx${NC}"
    exit 1
fi
systemctl reload nginx

# ---- 8) database ----
echo -e "${YELLOW}==> [8/9] Database${NC}"
echo "    No manual migration step needed: Q-Mgr.API auto-creates the '__PG_DATABASE__' database"
echo "    (if missing) and applies EF Core migrations + RBAC/SuperAdmin/demo seeding on first"
echo "    startup, via QMgr.Infrastructure.Data.DatabaseInitializer. Confirm the connection"
echo "    string's Postgres role in appsettings.Production.json has CREATEDB if this is a"
echo "    first install against a fresh Postgres instance."

# Nightly pg_dump + restore-drill script, installed outside the rsync'd app dirs so an upgrade
# never removes them, with a cron.d entry (02:30 daily, 30-day retention, log in /var/log/qmgr).
mkdir -p "$INSTALL_ROOT/bin" /var/backups/qmgr
install -m 0750 -o root -g root "$PKG_DIR/config/qmgr-backup-db.sh" "$INSTALL_ROOT/bin/qmgr-backup-db.sh"
install -m 0750 -o root -g root "$PKG_DIR/config/qmgr-restore-db.sh" "$INSTALL_ROOT/bin/qmgr-restore-db.sh"
install -m 0644 -o root -g root "$PKG_DIR/config/qmgr-backup.cron" /etc/cron.d/qmgr-backup
chmod 700 /var/backups/qmgr
if command -v pg_dump >/dev/null 2>&1; then
    echo -e "${GREEN}    Nightly backup scheduled (02:30, /var/backups/qmgr, 30-day retention)${NC}"
else
    echo -e "${YELLOW}    pg_dump not found — nightly backup WILL FAIL until postgresql-client is installed:${NC}"
    echo "        apt-get install -y postgresql-client"
fi
echo "    Run one now + verify a restore:  sudo bash $INSTALL_ROOT/bin/qmgr-backup-db.sh"
echo "                                      sudo bash $INSTALL_ROOT/bin/qmgr-restore-db.sh /var/backups/qmgr/<dump> --drill"

# ---- 9) start ----
echo -e "${YELLOW}==> [9/9] Starting services${NC}"
systemctl start qmgr-api.service
sleep 3
systemctl start qmgr-web.service
sleep 2

echo ""
echo -e "${CYAN}==> Status${NC}"

# `systemctl is-active` always prints the real state to stdout (active/activating/failed/...)
# and only ever exits 0 when that state is exactly "active" — so a naive
# `$(is-active X || echo fallback)` runs the fallback echo IN ADDITION to the real output
# whenever the state isn't "active", concatenating both into the captured variable (e.g.
# "activating\ninactive"). `|| true` (inside the substitution — under `set -e`, VAR=$(cmd)
# still aborts the whole script if cmd's exit status is nonzero, so this can't move outside
# the parens) swallows just the exit code without adding any stdout of its own.
API_STATUS=$(systemctl is-active qmgr-api.service 2>/dev/null || true)
WEB_STATUS=$(systemctl is-active qmgr-web.service 2>/dev/null || true)
[ -z "$API_STATUS" ] && API_STATUS="unknown"
[ -z "$WEB_STATUS" ] && WEB_STATUS="unknown"
if [ "$API_STATUS" = "active" ]; then
    echo -e "${GREEN}  Q-Mgr API:  Running${NC}"
else
    echo -e "${RED}  Q-Mgr API:  $API_STATUS — check: journalctl -u qmgr-api -n 50 --no-pager${NC}"
fi
if [ "$WEB_STATUS" = "active" ]; then
    echo -e "${GREEN}  Q-Mgr Web:  Running${NC}"
else
    echo -e "${RED}  Q-Mgr Web:  $WEB_STATUS — check: journalctl -u qmgr-web -n 50 --no-pager${NC}"
fi
echo ""
echo -e "${CYAN}==> Install/upgrade complete — build __BUILD_VERSION__${NC}"
echo "    https://__HOST_NAME__/            (Web)"
echo "    https://__HOST_NAME__/api-health  (API health check)"
echo "    Logs: journalctl -u qmgr-api -u qmgr-web -f"
'@
$installSh = $installSh `
    -replace '__BUILD_VERSION__', $BuildVersion `
    -replace '__INSTALL_ROOT__', $InstallRoot `
    -replace '__UPLOADS_PATH__', $UploadsPath `
    -replace '__DP_KEYS_PATH__', $DataProtectionPath `
    -replace '__PG_DATABASE__', $PgDatabase `
    -replace '__HOST_NAME__', $HostName `
    -replace '__API_PORT__', $ApiPort `
    -replace '__WEB_PORT__', $WebPort `
    -replace '__TENANT_CONF_PATH__', $TenantDomainConfPath `
    -replace '__ACME_WEBROOT__', $AcmeWebroot `
    -replace '__DOMAIN_SPOOL_PATH__', $DomainSpoolPath
$installShPath = Join-Path $OutputPath 'install.sh'
Write-LinuxText -Path $installShPath -Content $installSh
Write-Success 'install.sh generated (package root)'

# ── Step 8: manifest ─────────────────────────────────────────────────────────
Write-Step 8 $TotalSteps 'Writing deploy manifest'
Write-DeployManifest -PublishDir $OutputPath -BuildVersion $BuildVersion -Mode 'linux-onehost' -Runtime $TargetRuntime -Extra @{
    hostName    = $HostName
    webPort     = $WebPort
    apiPort     = $ApiPort
    installRoot = $InstallRoot
}
Write-Success 'deploy-manifest.json written'

# ── Step 9: package ──────────────────────────────────────────────────────────
Write-Step 9 $TotalSteps 'Packaging tarball'
# Filename stays short (semver + compact date-time) — full traceability (git hash, exact build
# time, runtime) already lives inside the package itself: deploy-manifest.json, BuildInfo.cs,
# and install.sh's own banner. $TargetRuntime is dropped too since this script only ever
# targets linux-x64 today; add it back to the name if a second RID is ever supported.
$semverBase  = ($BuildVersion -split '\+')[0]
$dateStamp   = if ($BuildVersion -match '\+(\d{8}\.\d{4})') { $Matches[1] } else { Get-Date -Format 'yyyyMMdd.HHmm' }
$pkgName = "qmgr-$semverBase-$dateStamp.tar.gz"
$pkgPath = Join-Path $OutputPath $pkgName

$stageDir = Join-Path $OutputPath 'stage'
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item -Path $apiPublishDir -Destination (Join-Path $stageDir 'api') -Recurse
Copy-Item -Path $webPublishDir -Destination (Join-Path $stageDir 'web') -Recurse
Copy-Item -Path $genDir -Destination (Join-Path $stageDir 'config') -Recurse
Copy-Item -Path $installShPath -Destination (Join-Path $stageDir 'install.sh')
Copy-Item -Path (Join-Path $OutputPath 'deploy-manifest.json') -Destination (Join-Path $stageDir 'deploy-manifest.json')

$tarAvailable = [bool](Get-Command tar -ErrorAction SilentlyContinue)
if ($tarAvailable) {
    Push-Location $stageDir
    try {
        & tar -czf $pkgPath .
        if ($LASTEXITCODE -ne 0) { throw "tar exited $LASTEXITCODE" }
    } finally { Pop-Location }
    Write-Success "Package: $pkgPath ($([math]::Round((Get-Item $pkgPath).Length / 1MB, 1)) MB)"
} else {
    Write-Warn "'tar' not found on PATH — leaving the built artefacts unpacked at $stageDir"
    Write-Warn "Copy that directory to the server and run server/install.sh from inside it."
}
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue

$sw.Stop()
Write-Header "Build complete — $(Get-Duration $sw.Elapsed)"
Write-Host "  Version : $BuildVersion"       -ForegroundColor White
Write-Host "  Package : $pkgPath"            -ForegroundColor White
Write-Host ""
Write-Host "  Next:" -ForegroundColor White
Write-Host "    scp -P<port> `"$pkgPath`" root@<server>:/tmp/" -ForegroundColor Cyan
Write-Host "    ssh root@<server> -p <port>"                   -ForegroundColor Cyan
Write-Host "    cd /tmp && tar -xzf $pkgName && sudo bash install.sh" -ForegroundColor Cyan
Write-Host ""
Write-Host "  If /tmp already has files from another deploy (ERP, a previous Q-Mgr build), use a" -ForegroundColor DarkGray
Write-Host "  dedicated subdirectory instead so nothing from a stale extract can get picked up:" -ForegroundColor DarkGray
Write-Host "    mkdir -p /tmp/qmgr-deploy && tar -xzf $pkgName -C /tmp/qmgr-deploy && cd /tmp/qmgr-deploy && sudo bash install.sh" -ForegroundColor DarkGray
Write-Host ""
