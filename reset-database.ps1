#!/usr/bin/env pwsh

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Q-Mgr Database Reset Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "WARNING: This will delete all existing data!" -ForegroundColor Yellow
Write-Host "Press Ctrl+C to cancel, or"
pause

Set-Location "src\Q-Mgr.API"

Write-Host ""
Write-Host "Step 1: Dropping database..." -ForegroundColor Yellow
dotnet ef database drop --force
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to drop database. Make sure applications are stopped." -ForegroundColor Red
    pause
    exit 1
}

Write-Host ""
Write-Host "Step 2: Applying migrations..." -ForegroundColor Yellow
dotnet ef database update
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to apply migrations." -ForegroundColor Red
    pause
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Database reset complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "SuperAdmin credentials:" -ForegroundColor Cyan
Write-Host "  Email: superadmin@qmgr.platform" -ForegroundColor White
Write-Host "  Password: super123" -ForegroundColor White
Write-Host ""
Write-Host "Admin credentials:" -ForegroundColor Cyan
Write-Host "  Email: admin@qmgr.demo" -ForegroundColor White
Write-Host "  Password: admin123" -ForegroundColor White
Write-Host ""
Write-Host "Staff credentials:" -ForegroundColor Cyan
Write-Host "  Email: agent1@qmgr.demo" -ForegroundColor White
Write-Host "  Password: agent123" -ForegroundColor White
Write-Host ""
Write-Host "Now run the applications:" -ForegroundColor Yellow
Write-Host "  Terminal 1: cd src\Q-Mgr.API && dotnet run" -ForegroundColor White
Write-Host "  Terminal 2: cd src\Q-Mgr.Web && dotnet run" -ForegroundColor White
Write-Host ""
pause
