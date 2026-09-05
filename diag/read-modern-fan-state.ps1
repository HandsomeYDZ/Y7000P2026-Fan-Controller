# Read-only WMI snapshot. No EC port access, mode changes, or Set methods.
$ErrorActionPreference = 'Stop'
$system = Get-CimInstance Win32_ComputerSystem
$bios = Get-CimInstance Win32_BIOS
$fanClass = Get-CimClass -Namespace root/WMI -ClassName LENOVO_FAN_METHOD
$gamezone = Get-CimInstance -Namespace root/WMI -ClassName LENOVO_GAMEZONE_DATA
$other = Get-CimInstance -Namespace root/WMI -ClassName LENOVO_OTHER_METHOD
$mode = Invoke-CimMethod -InputObject $gamezone -MethodName GetSmartFanMode
$sensors = @{}
foreach ($entry in @{ CpuRpm = 0x04030001; GpuRpm = 0x04030002; CpuTemp = 0x05040000; GpuTemp = 0x05050000; PchTemp = 0x05010000 }.GetEnumerator()) {
    $sensors[$entry.Key] = (Invoke-CimMethod -InputObject $other -MethodName GetFeatureValue -Arguments @{ IDs = [uint32]$entry.Value }).Value
}
$snapshot = [ordered]@{
    Time = (Get-Date).ToString('o')
    Model = $system.Model
    Bios = $bios.SMBIOSBIOSVersion
    Mode = $mode.Data
    Sensors = $sensors
    FanCapabilities = @(Get-CimInstance -Namespace root/WMI -ClassName LENOVO_CAPABILITY_DATA_00 | Where-Object { $_.IDs -in @(0x04030001,0x04030002,0x04030004) } | ForEach-Object {
        $props = @{}; foreach ($p in $_.CimInstanceProperties) { $props[$p.Name] = $p.Value }; $props
    })
    FanTestData = @(Get-CimInstance -Namespace root/WMI -ClassName LENOVO_FAN_TEST_DATA | ForEach-Object {
        $props = @{}; foreach ($p in $_.CimInstanceProperties) { $props[$p.Name] = $p.Value }; $props
    })
    Methods = @($fanClass.CimClassMethods | ForEach-Object {
        @{ Name = $_.Name; ReturnType = [string]$_.ReturnType; Parameters = @($_.Parameters | ForEach-Object { @{ Name = $_.Name; Type = [string]$_.CimType; Qualifiers = @($_.Qualifiers | ForEach-Object { @{Name=$_.Name; Value=$_.Value} }) } }) }
    })
    Tables = @(Get-CimInstance -Namespace root/WMI -ClassName LENOVO_FAN_TABLE_DATA | Select-Object Mode,Fan_Id,Sensor_ID,FanTable_Len,FanTable_Data,SensorTable_Len,SensorTable_Data,CurrentFanMinSpeed,CurrentFanMaxSpeed,DesignMaxFanSpeedNumber)
}
$json = $snapshot | ConvertTo-Json -Depth 12
$json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'modern-fan-state.json') -Encoding utf8
$json
