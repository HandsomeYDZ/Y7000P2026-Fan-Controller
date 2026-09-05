# Dumps the complete fan-table WMI structures on this machine.
# Requires Administrator.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
$out = @()
function Log($m) { $script:out += $m; Write-Host $m }

Log "=== Fan table WMI probe on $env:COMPUTERNAME ==="

Log "--- Current SmartFanMode ---"
try {
    $gz = Get-WmiObject -Namespace root/WMI -Class LENOVO_GAMEZONE_DATA
    $r = $gz.GetSmartFanMode()
    $r.Properties | ForEach-Object { Log ("  " + $_.Name + " = " + $_.Value) }
} catch { Log "SmartFanMode ERROR: $($_.Exception.Message)" }
Log ""

Log "--- LENOVO_FAN_TABLE_DATA (all instances, all properties) ---"
try {
    $tables = Get-WmiObject -Namespace root/WMI -Class LENOVO_FAN_TABLE_DATA
    $i = 0
    foreach ($t in $tables) {
        Log "[instance $i]"
        foreach ($p in $t.Properties) {
            $v = $p.Value
            if ($v -is [System.Array]) { $v = '[' + ($v -join ', ') + ']' }
            Log ("  " + $p.Name + " = " + $v)
        }
        $i++
    }
} catch { Log "Table data ERROR: $($_.Exception.Message)" }
Log ""

Log "--- LENOVO_FAN_METHOD.Fan_Get_Table combos ---"
try {
    $fm = Get-WmiObject -Namespace root/WMI -Class LENOVO_FAN_METHOD
    foreach ($fid in @(0,1,2)) {
        foreach ($sid in @(0,1,3,4,5)) {
            try {
                $r = $fm.Fan_Get_Table($fid, $sid)
                $vals = ""
                foreach ($p in $r.Properties) {
                    $v = $p.Value
                    if ($v -is [System.Array]) { $v = '[' + ($v -join ', ') + ']' }
                    $vals += (" " + $p.Name + "=" + $v)
                }
                Log ("Fan_Get_Table($fid,$sid) ->" + $vals)
            } catch { Log ("Fan_Get_Table($fid,$sid) ERROR: " + $_.Exception.Message) }
        }
    }
} catch { Log "FAN_METHOD ERROR: $($_.Exception.Message)" }
Log ""

Log "--- Current fan speeds / temps (GetFeatureValue) ---"
try {
    $om = Get-WmiObject -Namespace root/WMI -Class LENOVO_OTHER_METHOD
    foreach ($id in @(0x04030001, 0x04030002, 0x05040000, 0x05050000)) {
        try {
            $r = $om.GetFeatureValue($id)
            Log ("ID 0x{0:X8} = {1}" -f $id, $r.Value)
        } catch { Log ("ID 0x{0:X8} ERROR: {1}" -f $id, $_.Exception.Message) }
    }
} catch { Log "OTHER_METHOD ERROR: $($_.Exception.Message)" }

$out | Out-File -FilePath "$PSScriptRoot\probe-fan-table.out.txt" -Encoding utf8
Log ""
Log "Done. Output saved to probe-fan-table.out.txt"
