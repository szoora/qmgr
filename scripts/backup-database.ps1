#!/usr/bin/env pwsh
<#
Backs up the Q-Mgr Postgres database with pg_dump and records a completion
marker that HealthController.GetDatabaseHealth reads back as "Last Backup".

Requires the Postgres client tools (pg_dump) to be installed and on PATH —
NOT the case on this dev machine (confirmed via `where pg_dump`), so this
script is written and documented but has not been executed/verified here.
It targets a real production/staging host where those tools are present.

Usage:
  ./scripts/backup-database.ps1
  ./scripts/backup-database.ps1 -OutputDir "D:\backups" -RetentionDays 14

Reads connection details from src/Q-Mgr.API/appsettings.json's
ConnectionStrings:DefaultConnection by default; override any part with
-PgHost/-PgPort/-PgDatabase/-PgUser, or set PGPASSWORD in the environment
(never hardcode the password here or pass it on the command line).
#>
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\backups"),
    [int]$RetentionDays = 30,
    [string]$PgHost,
    [string]$PgPort = "5432",
    [string]$PgDatabase,
    [string]$PgUser
)

$ErrorActionPreference = "Stop"
$repoRoot = Join-Path $PSScriptRoot ".."
$apiContentRoot = Join-Path $repoRoot "src\Q-Mgr.API"

function Get-ConnectionStringPart {
    param([string]$ConnectionString, [string]$Key)
    if ($ConnectionString -match "(?i)$Key=([^;]+)") { return $Matches[1] }
    return $null
}

if (-not $PgHost -or -not $PgDatabase -or -not $PgUser) {
    $appsettingsPath = Join-Path $apiContentRoot "appsettings.json"
    if (Test-Path $appsettingsPath) {
        $appsettings = Get-Content $appsettingsPath -Raw | ConvertFrom-Json
        $connString = $appsettings.ConnectionStrings.DefaultConnection
        if (-not $PgHost)     { $PgHost     = Get-ConnectionStringPart $connString "Host" }
        if (-not $PgDatabase) { $PgDatabase = Get-ConnectionStringPart $connString "Database" }
        if (-not $PgUser)     { $PgUser     = Get-ConnectionStringPart $connString "Username" }
    }
}

if (-not $PgHost -or -not $PgDatabase -or -not $PgUser) {
    Write-Error "Could not resolve database connection details. Pass -PgHost/-PgDatabase/-PgUser explicitly."
    exit 1
}

if (-not (Get-Command pg_dump -ErrorAction SilentlyContinue)) {
    Write-Error "pg_dump not found on PATH. Install the PostgreSQL client tools on this host before running backups."
    exit 1
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$backupFile = Join-Path $OutputDir "qmgr-$timestamp.dump"

Write-Host "Backing up '$PgDatabase' on $PgHost`:$PgPort to $backupFile ..." -ForegroundColor Cyan

# -Fc = custom format (compressed, restorable with pg_restore); password must come from
# PGPASSWORD (or a .pgpass file) — never pass it as a command-line argument.
& pg_dump -h $PgHost -p $PgPort -U $PgUser -Fc -f $backupFile $PgDatabase

if ($LASTEXITCODE -ne 0) {
    Write-Error "pg_dump failed with exit code $LASTEXITCODE — NOT recording a backup marker."
    exit $LASTEXITCODE
}

Write-Host "Backup succeeded: $backupFile" -ForegroundColor Green

# Record completion for HealthController to read back as "Last Backup" — same
# "logs" directory Serilog writes to (content root, not the bin/ output dir).
$logsDir = Join-Path $apiContentRoot "logs"
if (-not (Test-Path $logsDir)) {
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
}
$markerPath = Join-Path $logsDir "last-backup.marker"
(Get-Date).ToUniversalTime().ToString("o") | Set-Content -Path $markerPath -NoNewline
Write-Host "Recorded backup marker: $markerPath" -ForegroundColor Green

if ($RetentionDays -gt 0) {
    $cutoff = (Get-Date).AddDays(-$RetentionDays)
    Get-ChildItem -Path $OutputDir -Filter "qmgr-*.dump" |
        Where-Object { $_.LastWriteTime -lt $cutoff } |
        ForEach-Object {
            Write-Host "Removing backup older than $RetentionDays days: $($_.Name)" -ForegroundColor Yellow
            Remove-Item $_.FullName -Force
        }
}

Write-Host "Done." -ForegroundColor Green
