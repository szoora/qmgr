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
      - Ports 8581 (Web) / 8582 (API), both proxied — nothing public binds directly to them.
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
    # API first, Web = API+1 — kept in sequence on purpose so the pair reads as one unit at a
    # glance instead of two arbitrary numbers. These are just fallback defaults, not a promise
    # either is actually free on any given target server — this box alone already had 8580-8584
    # AND 8590/8591 claimed by other unrelated apps (evolweb, CashBook, erp, evol-api, evol-ui,
    # 'must') before Q-Mgr ever got here. Always confirm both are free on the actual target with
    # `ss -tlnp` before deploying — see README.md — and pass explicit -ApiPort/-WebPort if not.
    [int]$ApiPort            = 8581,
    [int]$WebPort            = 8582,

    [string]$InstallRoot     = '/var/www/sites/qmgr',
    [string]$UploadsPath     = '/var/www/uploads/qmgr',  # persists across deploys; excluded from the package and from rsync --delete

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
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default              = 'Warning'
            'Microsoft.AspNetCore' = 'Warning'
        }
    }
    AllowedHosts = $HostName
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

# --- systemd: API ---
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
MemoryMax=1200M

NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=$InstallRoot/api/wwwroot/uploads $UploadsPath /var/log/qmgr

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
MemoryMax=800M

NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/var/log/qmgr

[Install]
WantedBy=multi-user.target
"@
Write-LinuxText -Path (Join-Path $genDir 'qmgr-web.service') -Content $webUnit
Write-Success 'systemd units generated (qmgr-api.service, qmgr-web.service)'

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
# Found the hard way (2026-09-01): a build made with the script's bare default ports (8581/8582)
# instead of this server's actual assigned ports (8586/8587 — chosen on an earlier deploy after
# 8581/8582 turned out to already belong to other unrelated apps on this shared box) silently
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

mkdir -p "$INSTALL_ROOT/api" "$INSTALL_ROOT/web" "$BACKUP_ROOT" "$UPLOADS_PATH" "$LOG_DIR"

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
chown -R www-data:www-data "$INSTALL_ROOT/api" "$INSTALL_ROOT/web" "$UPLOADS_PATH" "$LOG_DIR"
chmod -R u+rwX,go+rX,go-w "$INSTALL_ROOT/api" "$INSTALL_ROOT/web"
chmod +x "$INSTALL_ROOT/api/Q-Mgr.API" "$INSTALL_ROOT/web/Q-Mgr.Web"
ln -sfn "$UPLOADS_PATH" "$INSTALL_ROOT/api/wwwroot/uploads"

# ---- 6) systemd units ----
echo -e "${YELLOW}==> [6/9] Installing systemd units${NC}"
cp "$PKG_DIR/config/qmgr-api.service" /etc/systemd/system/qmgr-api.service
cp "$PKG_DIR/config/qmgr-web.service" /etc/systemd/system/qmgr-web.service
systemctl daemon-reload
systemctl enable qmgr-api.service qmgr-web.service
echo -e "${GREEN}    Systemd units installed and enabled${NC}"

# ---- 7) nginx ----
echo -e "${YELLOW}==> [7/9] Installing nginx site${NC}"
cp "$PKG_DIR/config/qmgr.nginx.conf" /etc/nginx/sites-available/qmgr.conf
ln -sfn /etc/nginx/sites-available/qmgr.conf /etc/nginx/sites-enabled/qmgr.conf
if nginx -t; then
    echo -e "${GREEN}    nginx config valid${NC}"
else
    echo -e "${RED}    nginx config test failed — see output above. Fix it, then: systemctl reload nginx${NC}"
    exit 1
fi
systemctl reload nginx

# ---- 8) database note ----
echo -e "${YELLOW}==> [8/9] Database${NC}"
echo "    No manual migration step needed: Q-Mgr.API auto-creates the '__PG_DATABASE__' database"
echo "    (if missing) and applies EF Core migrations + RBAC/SuperAdmin/demo seeding on first"
echo "    startup, via QMgr.Infrastructure.Data.DatabaseInitializer. Confirm the connection"
echo "    string's Postgres role in appsettings.Production.json has CREATEDB if this is a"
echo "    first install against a fresh Postgres instance."

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
    -replace '__PG_DATABASE__', $PgDatabase `
    -replace '__HOST_NAME__', $HostName `
    -replace '__API_PORT__', $ApiPort `
    -replace '__WEB_PORT__', $WebPort
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
