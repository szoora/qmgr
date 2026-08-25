@echo off
echo Starting Q-Mgr Development Environment
echo ========================================
echo.
echo API will run on: https://localhost:5001
echo Web will run on: https://localhost:5002
echo.
echo Starting API...
start "Q-Mgr API" cmd /k "cd /d %~dp0src\Q-Mgr.API && dotnet run --launch-profile https"
echo Waiting for API to start...
timeout /t 5 /nobreak > nul
echo.
echo Starting Web UI...
start "Q-Mgr Web" cmd /k "cd /d %~dp0src\Q-Mgr.Web && dotnet run --launch-profile https"
echo.
echo Both services are starting...
echo.
echo Press any key to open the Web UI in browser...
pause > nul
start https://localhost:5002
