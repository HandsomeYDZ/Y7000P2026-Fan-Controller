@echo off
rem Run ECProbe elevated. Workdir of an elevated process is System32, so all paths are absolute.
"C:\Users\Legion-Desktop\dotnet\dotnet.exe" --elevated "D:\Code Project\Projects\Y7000P2026-Fan-Controller\diag\ECProbe\bin\Release\net8.0-windows\ECProbe.dll" > "D:\Code Project\Projects\Y7000P2026-Fan-Controller\diag\elev-out.txt" 2>&1
echo exit=%errorlevel% >> "D:\Code Project\Projects\Y7000P2026-Fan-Controller\diag\elev-out.txt"
