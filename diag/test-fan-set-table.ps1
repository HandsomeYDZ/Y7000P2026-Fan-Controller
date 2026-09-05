# Disabled after an unexplained power-off. A flat minimum table is not safe recovery.
throw 'This write test is retired. Run read-modern-fan-state.ps1 for read-only diagnosis. No hardware was changed.'

# Historical script below; deliberately unreachable.
# Controlled test: does Fan_Set_Table actually control fan speed on this machine?
# Flow: remember mode -> switch to Custom(255) -> write test step table ->
#       sample speeds 15s -> write safe low table -> restore original mode.
# Requires Administrator.
# !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
# !! DANGER: On 2026-09-05 a run of this test coincided with a sudden power-off
# !! while multiple Lenovo tools (this script + Lenovo Fan Controller GUI +
# !! Lenovo Legion Toolkit + OEM services) were all reacting to a SmartFanMode
# !! switch. BEFORE RUNNING: close Lenovo Fan Controller, Lenovo Legion
# !! Toolkit and any other fan/EC tool. See HANDOFF.md section 0 and 7.
# !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
$out = @()
function Log($m) { $script:out += $m; Write-Host $m }

Log "=== Fan_Set_Table live test on $env:COMPUTERNAME ==="
Log "Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

function Make-FanTableBytes([int[]]$steps) {
    # 64 bytes: [FSTM=1][FSID=0][FSTL u32 LE=0][10 x u16 LE steps]
    $b = New-Object byte[] 64
    $b[0] = 1
    for ($i = 0; $i -lt 10; $i++) {
        $v = [uint16]$steps[$i]
        $b[6 + $i * 2] = [byte]($v -band 0xFF)
        $b[7 + $i * 2] = [byte](($v -shr 8) -band 0xFF)
    }
    return $b
}

$gz = Get-WmiObject -Namespace root/WMI -Class LENOVO_GAMEZONE_DATA
$fm = Get-WmiObject -Namespace root/WMI -Class LENOVO_FAN_METHOD
$om = Get-WmiObject -Namespace root/WMI -Class LENOVO_OTHER_METHOD

function Get-FanSpeeds {
    $cpu = $om.GetFeatureValue(0x04030001).Value
    $gpu = $om.GetFeatureValue(0x04030002).Value
    $cpuT = $om.GetFeatureValue(0x05040000).Value
    return "CPU fan=$cpu RPM, GPU fan=$gpu RPM, CPU temp=$cpuT C"
}

# 1. Remember original mode
$origMode = $gz.GetSmartFanMode().Data
Log "Original SmartFanMode = $origMode"
Log ("Baseline: " + (Get-FanSpeeds))
Log ""

# 2. Switch to Custom (255)
try {
    $r = $gz.SetSmartFanMode(255)
    Log "SetSmartFanMode(255) called (ret=$($r.ReturnValue))"
} catch { Log "SetSmartFanMode(255) ERROR: $($_.Exception.Message)" }
Start-Sleep -Seconds 2
Log "Mode after switch: $($gz.GetSmartFanMode().Data)"
Log ""

# 3. Write test table: all steps = 8 (index 7 -> 3800 RPM in FanTable_Data)
$testBytes = Make-FanTableBytes @(8,8,8,8,8,8,8,8,8,8)
try {
    $inP = $fm.GetMethodParameters("Fan_Set_Table")
    $inP["FanTable"] = $testBytes
    $r = $fm.InvokeMethod("Fan_Set_Table", $inP, $null)
    Log "Fan_Set_Table(test steps=8) called (ret=$($r.ReturnValue))"
} catch { Log "Fan_Set_Table ERROR: $($_.Exception.Message)" }
Log ""

# 4. Sample speeds for ~20s
Log "--- Sampling 20s after test write (fans should rise toward ~3800 RPM) ---"
for ($i = 0; $i -lt 10; $i++) {
    Log ("[+$($i * 2)s] " + (Get-FanSpeeds))
    Start-Sleep -Seconds 2
}
Log ""

# 5. Write safe low table (step 1 = lowest speed)
$safeBytes = Make-FanTableBytes @(1,1,1,1,1,1,1,1,1,1)
try {
    $inP = $fm.GetMethodParameters("Fan_Set_Table")
    $inP["FanTable"] = $safeBytes
    $r = $fm.InvokeMethod("Fan_Set_Table", $inP, $null)
    Log "Fan_Set_Table(safe steps=1) called (ret=$($r.ReturnValue))"
} catch { Log "Fan_Set_Table(safe) ERROR: $($_.Exception.Message)" }
Start-Sleep -Seconds 2

# 6. Restore original mode
try {
    $r = $gz.SetSmartFanMode($origMode)
    Log "Restored SmartFanMode=$origMode (ret=$($r.ReturnValue))"
} catch { Log "Restore mode ERROR: $($_.Exception.Message)" }
Start-Sleep -Seconds 3
Log ("Final: " + (Get-FanSpeeds))
Log ""

$out | Out-File -FilePath "$PSScriptRoot\test-fan-set-table.out.txt" -Encoding utf8
Log "Done. Output saved to test-fan-set-table.out.txt"
