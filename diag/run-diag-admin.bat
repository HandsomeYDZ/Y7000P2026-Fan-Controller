@echo off
rem ============================================================
rem  Y7000P2026 EC diagnostic - auto-elevates, then scans EC RAM
rem  0xC000-0xCFFF and samples temps/fan RPM during idle + CPU
rem  load. Results are written to diag\ECProbe\bin\...\out\*.txt
rem ============================================================

rem --- self-elevate if not admin ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights - please click YES on the UAC prompt...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -Wait"
    exit /b
)

set DOTNET="C:\Users\Legion-Desktop\dotnet\dotnet.exe"
set PROBE="D:\Code Project\Projects\Y7000P2026-Fan-Controller\diag\ECProbe\bin\Release\net8.0-windows\ECProbe.dll"

echo Running EC dump-only probe...
echo.
%DOTNET% %PROBE% --dump-only
echo.
echo ============================================================
echo Done. Results are in:
echo   D:\Code Project\Projects\Y7000P2026-Fan-Controller\diag\ECProbe\bin\Release\net8.0-windows\out\
echo ============================================================
pause
