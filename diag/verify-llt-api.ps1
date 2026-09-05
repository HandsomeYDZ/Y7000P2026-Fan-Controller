# Verifies the WMI interfaces that LenovoLegionToolkit (LLT) SensorsControllerV5
# uses to read fan speed and temperatures on new Legion models (e.g. Y7000P 2026).
# Requires Administrator.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
$out = @()
function Log($m) { $script:out += $m; Write-Host $m }

Log "=== LLT V5 WMI API verification on $env:COMPUTERNAME ==="

$om = Get-WmiObject -Namespace root/WMI -Class LENOVO_OTHER_METHOD
if ($null -eq $om) {
    Log "FATAL: LENOVO_OTHER_METHOD not accessible (need admin?)"
    $out | Out-File -FilePath "$PSScriptRoot\verify-llt-api.out.txt" -Encoding utf8
    exit 1
}
Log "LENOVO_OTHER_METHOD accessible. Methods:"
$om | Get-Member -MemberType Method | ForEach-Object { Log ("  " + $_.Name) }

$ids = @(
    @{ Name = 'CPU fan speed';   Id = 0x04030001 },
    @{ Name = 'GPU fan speed';   Id = 0x04030002 },
    @{ Name = 'PCH fan speed';   Id = 0x04030004 },
    @{ Name = 'CPU temperature'; Id = 0x05040000 },
    @{ Name = 'GPU temperature'; Id = 0x05050000 },
    @{ Name = 'PCH temperature'; Id = 0x05010000 }
)

Log ""
Log "--- Sampling every 2s for 30s (values from GetFeatureValue) ---"
for ($i = 0; $i -lt 15; $i++) {
    $line = "[$i] "
    foreach ($item in $ids) {
        try {
            $r = $om.GetFeatureValue($item.Id)
            $v = if ($r.Value -ne $null) { $r.Value } else { "NULL" }
        } catch { $v = "ERR" }
        $line += ("{0}={1} " -f $item.Name, $v)
    }
    Log $line
    Start-Sleep -Seconds 2
}

Log ""
Log "--- LENOVO_FAN_TABLE_DATA instances ---"
try {
    $tables = Get-WmiObject -Namespace root/WMI -Class LENOVO_FAN_TABLE_DATA
    foreach ($t in $tables) {
        $fid = $t.Fan_Id; $sid = $t.Sensor_ID
        $active = $t.Active
        $speeds = $t.FanTable_Data -join ','
        $temps = $t.SensorTable_Data -join ','
        Log "FanId=$fid SensorId=$sid Active=$active"
        Log "    speeds: $speeds"
        Log "    temps : $temps"
    }
} catch { Log "Table data ERROR: $($_.Exception.Message)" }

Log ""
Log "--- LENOVO_FAN_METHOD.Fan_Get_Table(fan, sensor) ---"
try {
    $fm = Get-WmiObject -Namespace root/WMI -Class LENOVO_FAN_METHOD
    foreach ($pair in @(@(1,1), @(2,5), @(4,4), @(1,5), @(2,1))) {
        try {
            $r = $fm.Fan_Get_Table($pair[0], $pair[1])
            $vals = ($r.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '; '
            Log "Fan_Get_Table($($pair[0]),$($pair[1])) -> $vals"
        } catch { Log "Fan_Get_Table($($pair[0]),$($pair[1])) ERROR: $($_.Exception.Message)" }
    }
} catch { Log "FAN_METHOD ERROR: $($_.Exception.Message)" }

$out | Out-File -FilePath "$PSScriptRoot\verify-llt-api.out.txt" -Encoding utf8
Log ""
Log "Done. Output saved to verify-llt-api.out.txt"
