#requires -Version 7
# ^ PowerShell 7+, not Windows PowerShell 5.1. Without this line 5.1 does not refuse the
#   script — it PARSES it, fails on 7-only syntax, prints ParserError to stdout and still
#   exits 0. A caller reading the exit code is told the build succeeded when no artefact
#   was produced. Mirrors the exact gotcha documented in E:\ERP\scripts\deploy\Common.ps1 —
#   same failure mode, same fix, kept here so a future edit doesn't accidentally lose it.

# =============================================================================
# Q-Mgr — shared deployment helpers
# Dot-source from scripts/deploy/build-linux.ps1.
#
# Deliberately modelled on E:\ERP\scripts\deploy\Common.ps1 (same repo owner,
# same target server) so the two products' deployment artefacts share the same
# operational shape — same version stamping, same file-lock recovery, same
# pre-compression strategy, same "never ship the build machine's uploads" gate.
#
# The one structural difference from ERP's Common.ps1: Q-Mgr is TWO deployable
# projects (Q-Mgr.API + Q-Mgr.Web), not one, so the clean/publish helpers here
# take an explicit list of project directories rather than a single hardcoded path.
# =============================================================================

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# UTF-8 without BOM — required for Linux config files (systemd, nginx) and so
# bash here-docs in install.sh don't choke on a stray BOM.
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# ── Console output ───────────────────────────────────────────────────────────

function Write-Header {
    param([string]$Message)
    Write-Host "`n============================================================" -ForegroundColor Cyan
    Write-Host "  $Message"                                                       -ForegroundColor White
    Write-Host "============================================================`n"   -ForegroundColor Cyan
}

function Write-Step {
    param([int]$Step, [int]$Total, [string]$Message)
    Write-Host "[$Step/$Total] $Message" -ForegroundColor Yellow
}

function Write-Success { param([string]$Message) Write-Host "  [OK] $Message" -ForegroundColor Green }
function Write-Info    { param([string]$Message) Write-Host "  $Message"       -ForegroundColor Gray  }
function Write-Warn    { param([string]$Message) Write-Host "  $Message"       -ForegroundColor Yellow }
function Write-Err     { param([string]$Message) Write-Host "  $Message"       -ForegroundColor Red   }

function Get-Duration {
    param([TimeSpan]$Duration)
    if ($Duration.TotalMinutes -ge 1) { return ('{0:N0}m {1:N0}s' -f [math]::Floor($Duration.TotalMinutes), $Duration.Seconds) }
    return ('{0:N1}s' -f $Duration.TotalSeconds)
}

# ── Build-server / file-lock recovery ────────────────────────────────────────
# MSBuild and VBCSCompiler keep persistent worker processes that hold .dll
# locks for minutes after a publish completes. Without explicit shutdown the
# next clean / publish silently picks up stale binaries or fails outright.
#
# IMPORTANT: never kill bare 'dotnet' processes here. A dev machine running this
# script routinely has OTHER dotnet.exe processes alive that have nothing to do
# with this build — `dotnet run` dev servers for Q-Mgr itself, or (on this box)
# a completely unrelated project (ERP) — and 'dotnet.exe' is also the parent
# process for `dotnet publish`/`dotnet build-server shutdown` themselves. An
# earlier version of this function looped over 'MSBuild', 'VBCSCompiler', AND
# 'dotnet' and force-killed every match with an empty MainWindowTitle (true for
# nearly every console app) — i.e. every plain `dotnet run` on the machine,
# unconditionally, on every clean. It happened to fail silently against this
# session's own live dev servers (access-denied, not caught) rather than
# actually killing them, but that was luck, not by design. Fixed: only ever
# target the headless, no-user-state build-WORKER processes (MSBuild,
# VBCSCompiler) — never anything literally named 'dotnet'. `dotnet build-server
# shutdown` is the correct, safe way to release their locks; killing them
# directly is only the last-resort fallback if that graceful shutdown didn't
# actually release the lock in time.

function Stop-DotnetBuildServer {
    & dotnet build-server shutdown 2>&1 | Out-Null
}

function Stop-BuildWorkerProcesses {
    Write-Info "Releasing build-server file locks (build-server shutdown + worker process kill)..."
    Stop-DotnetBuildServer
    Start-Sleep -Milliseconds 500
    foreach ($name in @('MSBuild', 'VBCSCompiler')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue |
            ForEach-Object { try { $_.Kill() } catch { } }
    }
    Start-Sleep -Seconds 1
}

function Remove-PathWithRetry {
    param([string]$Path, [int]$Retries = 3, [int]$DelaySeconds = 3)
    for ($i = 1; $i -le $Retries; $i++) {
        try {
            if (Test-Path $Path) { Remove-Item -Path $Path -Recurse -Force -ErrorAction Stop }
            return $true
        } catch {
            if ($i -lt $Retries) {
                Write-Info "Locked: $([System.IO.Path]::GetFileName($Path)) — retry $i/$Retries"
                Stop-DotnetBuildServer
                Start-Sleep -Seconds $DelaySeconds
            } else {
                Stop-BuildWorkerProcesses; Start-Sleep -Seconds 2
                try { Remove-Item -Path $Path -Recurse -Force -ErrorAction Stop; return $true }
                catch {
                    Write-Err "Could not remove $Path"
                    Write-Err "  Still locked after releasing build-server workers — something else has it open"
                    Write-Err "  (a running 'dotnet run' host, the published app itself, an editor/Explorer window)."
                    Write-Err "  This script will not force-kill an arbitrary dotnet.exe to get past this — stop"
                    Write-Err "  whatever's holding it yourself and re-run."
                    return $false
                }
            }
        }
    }
    return $false
}

# ── Clean, and MEAN it ───────────────────────────────────────────────────────
# Same rationale as ERP's Common.ps1: a clean that silently fails and still
# prints "[OK] Clean done" lets a publish run over a dirty obj/ — which carries
# the static-web-assets manifest, so a stale one can silently drop wwwroot
# files from the artefact while the build still reports success.
#
# $ProjectDirs is a list of paths RELATIVE TO $RepoRoot (e.g.
# 'src/Q-Mgr.API', 'src/Q-Mgr.Web') — Q-Mgr has two publishable projects where
# ERP's Common.ps1 only ever had one, hence this being a parameter here.
#
# Scoped to bin/$Configuration and obj/$Configuration only — NOT the whole bin/ and obj/ trees.
# This build always publishes Release; a dev machine running this script routinely has a live
# `dotnet run` dev server for the SAME project open in another terminal, which builds Debug and
# holds locks on bin/Debug/obj/Debug the whole time it's running. Deleting the entire bin/obj
# tree used to sweep those Debug outputs in too, so a real clean run always failed (or, before
# the Stop-BuildWorkerProcesses fix, tried to force-kill the dev server to get past it) — the
# first real build this session needed -SkipClean to work around exactly this. Scoping to just
# the Release subfolder means a clean run and a live Debug dev server never contend for the same
# files at all, so -SkipClean is no longer necessary.
function Invoke-CleanArtefacts {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string[]]$ProjectDirs,
        [string]$Configuration = 'Release',
        [string]$OutputPath
    )

    # Graceful only — releases MSBuild/VBCSCompiler's own locks without touching any other
    # process on the machine. Remove-PathWithRetry escalates to Stop-BuildWorkerProcesses (still
    # scoped to just those two build-worker process names) only if a specific path turns out to
    # actually be locked, not preemptively.
    Stop-DotnetBuildServer

    $failed = @()
    if ($OutputPath -and (Test-Path $OutputPath)) {
        if (-not (Remove-PathWithRetry -Path $OutputPath)) { $failed += $OutputPath }
    }
    foreach ($proj in $ProjectDirs) {
        foreach ($sub in @('bin', 'obj')) {
            $d = Join-Path (Join-Path (Join-Path $RepoRoot $proj) $sub) $Configuration
            if (Test-Path $d) {
                if (-not (Remove-PathWithRetry -Path $d)) { $failed += $d }
            }
        }
    }

    if ($failed.Count -gt 0) {
        Write-Err 'Clean FAILED — these could not be removed:'
        foreach ($f in $failed) { Write-Err "    $f" }
        Write-Err ''
        Write-Err '  Something is holding them open — a running host (dotnet run, the'
        Write-Err '  published app itself) or an editor with the folder open. Stop it and'
        Write-Err '  re-run.'
        Write-Err ''
        Write-Err '  The build stops here on purpose. Publishing over a dirty obj/ is how an'
        Write-Err '  artefact ends up missing its static web assets while still reporting success.'
        throw "Clean failed: $($failed -join ', ')"
    }

    Write-Success 'Clean done'
}

# ── Version stamping ─────────────────────────────────────────────────────────
# Reads version.json, computes a build id, writes BuildInfo.cs + build-version.txt.
# Unlike ERP (one project, one BuildInfo.cs under sacc/), Q-Mgr.API and Q-Mgr.Web
# both reference Q-Mgr.Shared — so BuildInfo.cs is written there ONCE and both
# published apps pick up the same stamp automatically via the project reference,
# rather than needing two separate generated files kept in sync by hand.

function Get-RepoBuildVersion {
    param([string]$RepoRoot)
    $versionJsonPath = Join-Path $RepoRoot 'version.json'
    if (-not (Test-Path $versionJsonPath)) { return '0.0.0+local' }
    $v = Get-Content $versionJsonPath -Raw | ConvertFrom-Json
    $base = "$($v.major).$($v.minor).$($v.patch)"
    if ($v.prerelease) { $base = "$base-$($v.prerelease)" }
    $stamp = Get-Date -Format 'yyyyMMdd.HHmm'
    $hash  = ''
    try { $hash = (& git -C $RepoRoot rev-parse --short HEAD 2>$null).Trim() } catch { }
    if ($hash) { return "$base+$stamp.$hash" } else { return "$base+$stamp" }
}

function Write-BuildInfo {
    param([string]$RepoRoot, [string]$BuildVersion, [string]$Configuration, [string]$Runtime, [string]$Mode)
    $branch = ''
    $hash   = ''
    try { $hash   = (& git -C $RepoRoot rev-parse --short HEAD 2>$null).Trim() } catch { }
    try { $branch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null).Trim() } catch { }
    $buildInfo = @"
// Auto-generated by scripts/deploy/Common.ps1 — do not edit manually.
// Shared via Q-Mgr.Shared so both Q-Mgr.API and Q-Mgr.Web report the same build stamp.
namespace QMgr.Shared
{
    public static class BuildInfo
    {
        public const string Version       = "$BuildVersion";
        public const string GitHash       = "$hash";
        public const string GitBranch     = "$branch";
        public const string BuildDate     = "$((Get-Date).ToUniversalTime().ToString('o'))";
        public const string Configuration = "$Configuration";
        public const string Runtime       = "$Runtime";
        public const string DeployMode    = "$Mode";
    }
}
"@
    $buildInfoPath = Join-Path $RepoRoot 'src/Q-Mgr.Shared/BuildInfo.cs'
    [System.IO.File]::WriteAllText($buildInfoPath, $buildInfo, $script:Utf8NoBom)

    $verTxtPath = Join-Path $RepoRoot 'build-version.txt'
    [System.IO.File]::WriteAllText($verTxtPath, $BuildVersion, $script:Utf8NoBom)
}

# ── Publish with retry ───────────────────────────────────────────────────────
# Returns $true on success. Validates by checking the executable was actually
# emitted — relying on $LASTEXITCODE alone misses the case where MSBuild
# returns 0 with warnings but the exe never landed.

function Invoke-DotnetPublishWithRetry {
    param(
        [string[]]$DotnetArgs,
        [string]$ExpectedExePath,    # full path to the published exe (no extension on linux)
        [int]$MaxRetries = 3
    )
    for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
        $output = & dotnet @DotnetArgs 2>&1
        $exitCode = $LASTEXITCODE
        if ((Test-Path $ExpectedExePath) -or (Test-Path "$ExpectedExePath.dll") -or (Test-Path "$ExpectedExePath.exe")) {
            return $true
        }
        if ($attempt -lt $MaxRetries) {
            Write-Warn "Publish attempt $attempt/$MaxRetries failed (exit $exitCode) — releasing locks and retrying"
            Stop-DotnetBuildServer
            Start-Sleep -Seconds 5
        } else {
            Write-Err "Publish failed after $MaxRetries attempts (exit $exitCode). Expected: $ExpectedExePath"
            Write-Host "  --- last 30 lines of build output ---" -ForegroundColor Yellow
            $output | Select-Object -Last 30 | ForEach-Object {
                if ("$_" -match 'error|Error|fail|IOException|UnauthorizedAccess|locked') {
                    Write-Host "  $_" -ForegroundColor Red
                } else {
                    Write-Host "  $_" -ForegroundColor Gray
                }
            }
            return $false
        }
    }
    return $false
}

# ── Static-asset pre-compression ────────────────────────────────────────────
# Brotli q11 + gzip -9 are absurdly slow for runtime, fast at build time, and
# saved permanently on disk so nginx serves them via gzip_static / brotli_static.

function Optimize-StaticAssets {
    param([string]$WwwrootDir)
    if (-not (Test-Path $WwwrootDir)) { Write-Info "Skipping pre-compression (no wwwroot)"; return }

    $hasBrotli = [bool](Get-Command brotli -ErrorAction SilentlyContinue)
    $hasGzip   = [bool](Get-Command gzip   -ErrorAction SilentlyContinue)
    if (-not ($hasBrotli -or $hasGzip)) {
        if (Get-Command node -ErrorAction SilentlyContinue) {
            Optimize-StaticAssetsWithNode -WwwrootDir $WwwrootDir
            return
        }
        Write-Info "Skipping pre-compression (no brotli/gzip/node on PATH)"
        return
    }

    # Blazor's own fingerprinted framework files (_framework/*.dll.br etc.) are
    # already compressed by the SDK during publish — this pass covers everything
    # else (site css/js, the service worker, json manifests) that isn't.
    $patterns = @('*.css', '*.js', '*.json', '*.html', '*.svg', '*.xml', '*.map')
    $compressed = 0
    foreach ($pattern in $patterns) {
        Get-ChildItem -Path $WwwrootDir -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            $file = $_.FullName
            if ($file -like '*.br' -or $file -like '*.gz') { return }
            if ($_.Length -lt 256) { return }
            if ($hasBrotli) {
                $brFile = "$file.br"
                if (-not (Test-Path $brFile)) {
                    & brotli -q 11 -o $brFile $file 2>$null
                    if ($LASTEXITCODE -eq 0) { $compressed++ }
                }
            }
            if ($hasGzip) {
                $gzFile = "$file.gz"
                if (-not (Test-Path $gzFile)) {
                    & gzip -k -9 $file 2>$null
                    if ($LASTEXITCODE -eq 0) { $compressed++ }
                }
            }
        }
    }
    if ($compressed -gt 0) { Write-Success "Pre-compressed $compressed file variants" }
    else                   { Write-Info "Pre-compression ran but produced no new files" }
}

function Optimize-StaticAssetsWithNode {
    param([string]$WwwrootDir)
    $js = @'
const fs=require('fs'),path=require('path'),zlib=require('zlib');
const root=process.argv[2];
const exts=new Set(['.css','.js','.json','.html','.svg','.xml','.map']);
let n=0;
function walk(dir){for(const e of fs.readdirSync(dir,{withFileTypes:true})){const p=path.join(dir,e.name);if(e.isDirectory()){walk(p);continue;}if(e.name.endsWith('.br')||e.name.endsWith('.gz'))continue;if(!exts.has(path.extname(e.name).toLowerCase()))continue;const buf=fs.readFileSync(p);if(buf.length<256)continue;const br=p+'.br';if(!fs.existsSync(br)){fs.writeFileSync(br,zlib.brotliCompressSync(buf,{params:{[zlib.constants.BROTLI_PARAM_QUALITY]:11}}));n++;}const gz=p+'.gz';if(!fs.existsSync(gz)){fs.writeFileSync(gz,zlib.gzipSync(buf,{level:9}));n++;}}}
walk(root);
console.log('NODE_COMPRESSED='+n);
'@
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("precompress-" + [guid]::NewGuid().ToString('N') + ".js")
    Set-Content -Path $tmp -Value $js -Encoding UTF8
    try {
        $out  = & node $tmp $WwwrootDir 2>&1
        $line = $out | Select-String 'NODE_COMPRESSED=(\d+)'
        $count = if ($line) { [int]$line.Matches[0].Groups[1].Value } else { 0 }
        if ($count -gt 0) { Write-Success "Pre-compressed $count file variants (Node zlib fallback)" }
        else              { Write-Info "Node pre-compression ran but produced no new files" }
    }
    catch { Write-Warn "Node pre-compression failed: $($_.Exception.Message)" }
    finally { Remove-Item -Path $tmp -Force -ErrorAction SilentlyContinue }
}

# ── Strip publish noise ─────────────────────────────────────────────────────
# .pdb/.xml add build weight for zero runtime value. NeverShip clears any
# runtime-writable directory that might carry files created while THIS
# machine ran the app in dev — Q-Mgr.API writes uploaded signage media to
# wwwroot/uploads/media (LocalDiskMediaStorageService) and the same failure
# ERP's Common.ps1 documents applies here: a build machine's own test uploads
# shipping inside a customer-facing artefact. Gate, don't just delete —
# ERP's own header explains why silent is worse than loud here.

$script:PublishNeverShip = @('wwwroot/uploads')

function Remove-PublishNoise {
    param([string]$PublishDir)
    foreach ($pattern in @('*.pdb', '*.xml')) {
        Get-ChildItem -Path $PublishDir -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    foreach ($rel in $script:PublishNeverShip) {
        $dir = Join-Path $PublishDir ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $dir)) { continue }
        $found = @(Get-ChildItem -Path $dir -Recurse -File -ErrorAction SilentlyContinue)
        if ($found.Count -gt 0) {
            Write-Warn "Removing $($found.Count) runtime upload file(s) from $rel — these belong to the build machine, not a customer."
        }
        Remove-Item -Path (Join-Path $dir '*') -Recurse -Force -ErrorAction SilentlyContinue
    }

    Assert-NoRuntimeUploads -PublishDir $PublishDir
}

function Assert-NoRuntimeUploads {
    param([string]$PublishDir)
    $leaked = @()
    foreach ($rel in $script:PublishNeverShip) {
        $dir = Join-Path $PublishDir ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $dir)) { continue }
        $leaked += @(Get-ChildItem -Path $dir -Recurse -File -ErrorAction SilentlyContinue)
    }
    if ($leaked.Count -eq 0) { return }

    Write-Err "$($leaked.Count) runtime upload file(s) reached the publish. These are the build machine's own data and must not ship:"
    foreach ($f in ($leaked | Select-Object -First 10)) {
        Write-Err "    $($f.FullName.Substring($PublishDir.Length).TrimStart('\', '/'))"
    }
    if ($leaked.Count -gt 10) { Write-Err "    ... and $($leaked.Count - 10) more" }
    throw 'Refusing to package: runtime uploads present in the publish.'
}

# ── Manifest ─────────────────────────────────────────────────────────────────

function Write-DeployManifest {
    param(
        [string]$PublishDir,
        [string]$BuildVersion,
        [string]$Mode,
        [string]$Runtime,
        [hashtable]$Extra = @{}
    )
    $manifest = [ordered]@{
        version   = $BuildVersion
        builtAt   = (Get-Date).ToUniversalTime().ToString('o')
        mode      = $Mode
        runtime   = $Runtime
        publisher = $env:USERNAME
        machine   = $env:COMPUTERNAME
    }
    foreach ($k in $Extra.Keys) { $manifest[$k] = $Extra[$k] }
    $manifestPath = Join-Path $PublishDir 'deploy-manifest.json'
    ($manifest | ConvertTo-Json -Depth 5) | Set-Content -Path $manifestPath -Encoding UTF8
}

# Every artefact consumed by LINUX (bash, systemd, nginx) must have LF line
# endings — a stray CR is fatal, not cosmetic (bash reads the shebang line
# plus a carriage return and the install aborts on line one). This script is
# authored on Windows, so normalise at the point of writing rather than
# trusting how the .ps1 file itself happens to be saved. Same fix ERP's
# build-saas-linux.ps1 applies, for the same real incident class.
function Write-LinuxText([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, ($Content -replace "`r`n", "`n"), $script:Utf8NoBom)
}
