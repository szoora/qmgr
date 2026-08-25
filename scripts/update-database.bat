@echo off
REM Script to update the database with pending migrations
REM Usage: update-database.bat

cd "%~dp0..\src\Q-Mgr.API"

echo Updating database with pending migrations...
dotnet ef database update

if %errorlevel% neq 0 (
    echo.
    echo Database update failed!
    exit /b %errorlevel%
)

echo.
echo Database updated successfully!
