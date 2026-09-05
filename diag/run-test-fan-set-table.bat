@echo off
rem ============================================================
rem  Live test: Fan_Set_Table controls fan speed?
rem  Auto-elevates via UAC. ~40 seconds. Fans may spin up briefly.
rem ============================================================
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights - please click YES on the UAC prompt...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -Wait"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Code Project\Projects\Y7000P2026-Fan-Controller\diag\test-fan-set-table.ps1"
pause
