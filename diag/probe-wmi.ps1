Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
$out = @()

function Log($m) { $script:out += $m }

Log "=== WMI Probe on $env:COMPUTERNAME ==="
Log ""

# List LENOVO_FAN_METHOD methods
try {
    $fmClass = [wmiclass]'root\WMI:LENOVO_FAN_METHOD'
    Log "LENOVO_FAN_METHOD methods:"
    foreach ($m in $fmClass.Methods) {
        Log "  $($m.Name)"
    }
} catch { Log "Method list ERROR: $_" }
Log ""

$fm = Get-WmiObject -Namespace root/WMI -Class LENOVO_FAN_METHOD

Log "--- Current fan speeds (Fan_GetCurrentFanSpeed) ---"
foreach ($fid in @(0,1,2,3)) {
    try {
        $r = $fm.Fan_GetCurrentFanSpeed($fid)
        Log "Fan $fid : $($r.CurrentFanSpeed) RPM"
    } catch { Log "Fan $fid ERROR: $($_.Exception.Message)" }
}
Log ""

Log "--- Sensor temps (Fan_GetCurrentSensorTemperature) ---"
foreach ($sid in @(0,1,2,3,4,5,6,7)) {
    try {
        $r = $fm.Fan_GetCurrentSensorTemperature($sid)
        Log "Sensor $sid : $($r.CurrentSensorTemperature) C"
    } catch { Log "Sensor $sid ERROR: $($_.Exception.Message)" }
}
Log ""

Log "--- Fan_Get_MaxSpeed ---"
foreach ($fid in @(0,1)) {
    try {
        $r = $fm.Fan_Get_MaxSpeed($fid)
        $vals = ($r.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
        Log "Fan $fid MaxSpeed: $vals"
    } catch { Log "Fan $fid MaxSpeed ERROR: $($_.Exception.Message)" }
}
Log ""

Log "--- Fan_Get_FullSpeed ---"
try {
    $r = $fm.Fan_Get_FullSpeed()
    $vals = ($r.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
    Log "FullSpeed: $vals"
} catch { Log "FullSpeed ERROR: $($_.Exception.Message)" }
Log ""

Log "--- LENOVO_FAN_TABLE_DATA instances ---"
try {
    $tables = Get-WmiObject -Namespace root/WMI -Class LENOVO_FAN_TABLE_DATA
    foreach ($t in $tables) {
        Log "Instance: $($t.InstanceName)"
        foreach ($p in $t.Properties) {
            $v = $p.Value
            if ($v -is [System.Array]) { $v = '[' + ($v -join ', ') + ']' }
            Log "    $($p.Name) = $v"
        }
    }
} catch { Log "Table data ERROR: $($_.Exception.Message)" }
Log ""

Log "--- LENOVO_FAN_MAX_SPEED_DATA ---"
try {
    $m = Get-WmiObject -Namespace root/WMI -Class LENOVO_FAN_MAX_SPEED_DATA
    foreach ($o in $m) {
        foreach ($p in $o.Properties) { Log "    $($p.Name) = $($p.Value)" }
    }
} catch { Log "MaxSpeedData ERROR: $($_.Exception.Message)" }
Log ""

Log "--- Power mode (LENOVO_GAMEZONE_DATA.GetSmartFanMode) ---"
try {
    $gz = Get-WmiObject -Namespace root/WMI -Class LENOVO_GAMEZONE_DATA
    $r = $gz.GetSmartFanMode()
    Log "SmartFanMode Data = $($r.Data)"
} catch { Log "Gamezone ERROR: $($_.Exception.Message)" }
Log ""

$out | Out-File -FilePath "$PSScriptRoot\probe-wmi.out.txt" -Encoding utf8
Write-Host ($out -join "`n")
