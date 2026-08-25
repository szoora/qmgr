@echo off
REM Script to add a new Entity Framework migration
REM Usage: add-migration.bat MigrationName

if "%~1"=="" (
    echo Error: Migration name is required
    echo Usage: add-migration.bat MigrationName
    echo Example: add-migration.bat AddUserTable
    exit /b 1
)

cd "%~dp0..\src\Q-Mgr.API"

echo Adding migration: %1
dotnet ef migrations add %1

if %errorlevel% neq 0 (
    echo.
    echo Migration failed!
    exit /b %errorlevel%
)

echo.
echo Migration added successfully!
echo.
echo To apply this migration, run:
echo   update-database.bat
