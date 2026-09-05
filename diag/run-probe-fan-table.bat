@echo off
rem ============================================================
rem  Dumps fan-table WMI structures. Auto-elevates via UAC.
rem ============================================================
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights - please click YES on the UAC prompt...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -Wait"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Code Project\Projects\Y7000P2026-Fan-Controller\diag\probe-fan-table.ps1"
pause
