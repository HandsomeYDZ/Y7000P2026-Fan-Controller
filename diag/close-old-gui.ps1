$ErrorActionPreference = 'Stop'
$expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Lenovo Fan Controller\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Lenovo Fan Controller.exe'))
$matching = @(Get-Process -Name 'Lenovo Fan Controller' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $expected })
foreach ($process in $matching) { Stop-Process -Id $process.Id -ErrorAction Stop }
# Allow any helper to see loss of parent before asking firmware for automatic cooling.
Start-Sleep -Seconds 7
& (Join-Path $PSScriptRoot 'restore-auto-rpm.ps1')
