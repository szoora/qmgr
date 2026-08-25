@echo off
echo ========================================
echo Q-Mgr Database Reset Script
echo ========================================
echo.
echo WARNING: This will delete all existing data!
echo Press Ctrl+C to cancel, or
pause

cd "src\Q-Mgr.API"

echo.
echo Step 1: Dropping database...
dotnet ef database drop --force
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Failed to drop database. Make sure applications are stopped.
    pause
    exit /b 1
)

echo.
echo Step 2: Applying migrations...
dotnet ef database update
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Failed to apply migrations.
    pause
    exit /b 1
)

echo.
echo ========================================
echo Database reset complete!
echo ========================================
echo.
echo SuperAdmin credentials:
echo   Email: superadmin@qmgr.platform
echo   Password: super123
echo.
echo Admin credentials:
echo   Email: admin@qmgr.demo
echo   Password: admin123
echo.
echo Staff credentials:
echo   Email: agent1@qmgr.demo
echo   Password: agent123
echo.
echo Now run the applications:
echo   Terminal 1: cd src\Q-Mgr.API ^&^& dotnet run
echo   Terminal 2: cd src\Q-Mgr.Web ^&^& dotnet run
echo.
pause
