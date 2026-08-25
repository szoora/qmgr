@echo off
REM Script to remove the last migration (if not applied to database)
REM Usage: remove-migration.bat

cd "%~dp0..\src\Q-Mgr.API"

echo Removing last migration...
dotnet ef migrations remove

if %errorlevel% neq 0 (
    echo.
    echo Migration removal failed!
    echo Note: You can only remove migrations that haven't been applied to the database
    exit /b %errorlevel%
)

echo.
echo Migration removed successfully!
