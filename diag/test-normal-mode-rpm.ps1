# Experimental, model-specific, bounded WMI test. Default is read-only.
# Source: Linux drivers/platform/x86/lenovo/wmi-other.c, and this machine's DSDT.
# SetFeatureValue(RPM ID, 0) restores auto; zero in Fan_Set_Table is DIFFERENT.
param([switch]$Apply, [switch]$Watchdog, [string]$StateDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$system = Get-CimInstance Win32_ComputerSystem
$bios = Get-CimInstance Win32_BIOS
if ($system.Model -ne '83F3' -or $bios.SMBIOSBIOSVersion -ne 'Q6CN79WW') { throw 'This experiment is limited to the audited 83F3 / Q6CN79WW.' }
$script:TestExpiryPath = $null

function Get-Other { Get-CimInstance -Namespace root/WMI -ClassName LENOVO_OTHER_METHOD }
function Set-Rpm($other, [uint32]$id, [int]$rpm) {
    $mutex = [Threading.Mutex]::new($false, 'Global\Y7000P2026-Fan-WmiWrite')
    $locked = $false
    try {
        try { $locked = $mutex.WaitOne(5000) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw 'WMI write lock timed out.' }
        if ($rpm -ne 0 -and $script:TestExpiryPath -and (Test-Path -LiteralPath $script:TestExpiryPath)) { throw 'Recovery has started; no further targets allowed.' }
        $result = Invoke-CimMethod -InputObject $other -MethodName SetFeatureValue -Arguments @{ IDs = $id; Value = [uint32]$rpm }
        # Q6CN79WW AML returns zero even if the EC command failed. RPM sampling is required.
        $returnProperty = if ($null -ne $result) { $result.PSObject.Properties['ReturnValue'] } else { $null }
        if ($null -ne $returnProperty -and $null -ne $returnProperty.Value -and [int]$returnProperty.Value -notin @(0,1)) { throw 'Firmware returned an error.' }
    } finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
function Restore-Auto($other) {
    $errors = @()
    foreach ($id in @(0x04030001,0x04030002)) {
        try { Set-Rpm $other $id 0 } catch { $errors += $_.Exception.Message }
    }
    if ($errors.Count) { throw ($errors -join '; ') }
}
function Read-State($other, $gamezone) {
    $values = @{}
    foreach ($entry in @{ CpuRpm=0x04030001; GpuRpm=0x04030002; CpuTemp=0x05040000; GpuTemp=0x05050000; PchTemp=0x05010000 }.GetEnumerator()) {
        $values[$entry.Key] = [int](Invoke-CimMethod -InputObject $other -MethodName GetFeatureValue -Arguments @{IDs=[uint32]$entry.Value}).Value
    }
    $values.Mode = [int](Invoke-CimMethod -InputObject $gamezone -MethodName GetSmartFanMode).Data
    $values.Time = (Get-Date).ToString('o')
    [pscustomobject]$values
}

if ($Watchdog) {
    if (-not $StateDirectory -or -not (Test-Path -LiteralPath $StateDirectory)) { throw 'Missing watchdog directory.' }
    $other = Get-Other
    Set-Content -LiteralPath (Join-Path $StateDirectory 'ready') -Value 'ready'
    # Independent deadline; remains alive if the testing process fails.
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath (Join-Path $StateDirectory 'disarmed')) { exit 0 }
        Start-Sleep -Milliseconds 200
    }
    # Mark expiry before release so the parent must stop sending new targets.
    Set-Content -LiteralPath (Join-Path $StateDirectory 'expired') -Value 'expired'
    if (-not (Test-Path -LiteralPath (Join-Path $StateDirectory 'armed'))) { exit 0 }
    Restore-Auto $other
    Set-Content -LiteralPath (Join-Path $StateDirectory 'recovered') -Value 'auto requested by watchdog'
    exit 0
}

$caps = @(Get-CimInstance -Namespace root/WMI -ClassName LENOVO_CAPABILITY_DATA_00)
$limits = @(Get-CimInstance -Namespace root/WMI -ClassName LENOVO_FAN_TEST_DATA)
foreach ($id in @(0x04030001,0x04030002)) {
    $matches = @($caps | Where-Object { $_.IDs -eq $id })
    if ($matches.Count -ne 1 -or ($matches[0].Capability -band 7) -ne 7) { throw 'Firmware does not advertise read/write support.' }
}
if ($limits.Count -ne 1 -or $limits[0].NumOfFans -ne 2 -or ($limits[0].FanId -join ',') -ne '1,2') { throw 'Unexpected fan identities.' }
if (($limits[0].FanMinSpeed -join ',') -ne '1700,1700' -or ($limits[0].FanMaxSpeed -join ',') -ne '5300,5300') { throw 'Fan limits changed; re-audit required.' }
$other = Get-Other
$gamezone = Get-CimInstance -Namespace root/WMI -ClassName LENOVO_GAMEZONE_DATA
$baseline = Read-State $other $gamezone
$baseline | ConvertTo-Json
if ($baseline.Mode -notin @(1,2,3)) { throw 'Select a normal mode manually before testing. This script never changes mode.' }
if ($baseline.CpuTemp -lt 1 -or $baseline.CpuTemp -ge 75 -or $baseline.GpuTemp -ge 70 -or $baseline.GpuTemp -lt 0 -or $baseline.PchTemp -lt 1 -or $baseline.PchTemp -ge 70) { throw 'Invalid sensors or insufficient thermal headroom.' }
if ($baseline.CpuRpm -lt 1700 -or $baseline.GpuRpm -lt 1700 -or $baseline.CpuRpm -gt 2400 -or $baseline.GpuRpm -gt 2400) { throw 'Baseline unsuitable for a raise-only 2600 RPM experiment.' }
if (-not $Apply) { Write-Output 'READ-ONLY: would request both fans at 2600 RPM for 8 seconds, then restore automatic control. No write performed.'; exit 0 }

$conflicts = @(Get-Process | Where-Object { $_.ProcessName -match '^(Lenovo Fan Controller|Lenovo Legion Toolkit|FanControl|LegionFanControl|LenovoTray|LenovoVantage|LegionSpace|LegionZone)$' })
if ($conflicts.Count) { throw ('Close other fan/mode applications first: ' + ($conflicts.ProcessName -join ', ')) }
$testDir = Join-Path $PSScriptRoot ('normal-rpm-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$baseline | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testDir 'baseline.json')
$watchdogArgs = '-NoProfile -File "' + $PSCommandPath + '" -Watchdog -StateDirectory "' + $testDir + '"'
$watchdogProcess = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList $watchdogArgs -WindowStyle Hidden -PassThru
$readyDeadline = (Get-Date).AddSeconds(5)
while (-not (Test-Path -LiteralPath (Join-Path $testDir 'ready'))) {
    if ($watchdogProcess.HasExited -or (Get-Date) -gt $readyDeadline) { throw 'Recovery watchdog did not start. No write performed.' }
    Start-Sleep -Milliseconds 100
}
$writeAttempted = $false
try {
    $script:TestExpiryPath = Join-Path $testDir 'expired'
    Set-Content -LiteralPath (Join-Path $testDir 'armed') -Value 'armed'
    $writeAttempted = $true
    foreach ($id in @(0x04030001,0x04030002)) {
        if (Test-Path -LiteralPath (Join-Path $testDir 'expired')) { throw 'Watchdog deadline elapsed.' }
        Set-Rpm $other $id 2600
    }
    for ($sample = 0; $sample -lt 8; $sample++) {
        Start-Sleep -Seconds 1
        $state = Read-State $other $gamezone
        $state | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $testDir 'samples.jsonl')
        if ($state.Mode -ne $baseline.Mode -or $state.CpuTemp -lt 1 -or $state.CpuTemp -ge 80 -or $state.GpuTemp -lt 0 -or $state.GpuTemp -ge 75 -or $state.PchTemp -lt 1 -or $state.PchTemp -ge 75 -or $state.CpuRpm -lt 1500 -or $state.GpuRpm -lt 1500) { throw 'Mode, sensor, or thermal abort condition.' }
    }
}
finally {
    if ($writeAttempted) {
        Restore-Auto $other
        Set-Content -LiteralPath (Join-Path $testDir 'disarmed') -Value 'auto requested by parent'
    }
}
for ($sample = 0; $sample -lt 8; $sample++) {
    Start-Sleep -Seconds 1
    Read-State $other $gamezone | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $testDir 'recovery.jsonl')
}
Write-Output "Finished. Check speed response AND automatic recovery in $testDir. WMI return values alone do not prove success."
