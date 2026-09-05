# Recovery for the audited target-RPM interface only; never calls Fan_Set_Table.
$ErrorActionPreference = 'Stop'
$bios = Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
if ($bios.SystemProductName -ne '83F3' -or $bios.BIOSVersion -ne 'Q6CN79WW') { throw 'Unaudited firmware.' }
$other = Get-CimInstance -Namespace root/WMI -ClassName LENOVO_OTHER_METHOD
$errors = @()
foreach ($id in @(0x04030001,0x04030002)) {
    try { Invoke-CimMethod -InputObject $other -MethodName SetFeatureValue -Arguments @{IDs=[uint32]$id; Value=[uint32]0} | Out-Null }
    catch { $errors += $_.Exception.Message }
}
if ($errors.Count) { $errors | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'restore-auto-rpm.log'); exit 1 }
'Automatic control requested for both fans.' | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'restore-auto-rpm.log')
for ($i = 0; $i -lt 5; $i++) {
    Start-Sleep -Seconds 1
    $cpu = (Invoke-CimMethod -InputObject $other -MethodName GetFeatureValue -Arguments @{IDs=[uint32]0x04030001}).Value
    $gpu = (Invoke-CimMethod -InputObject $other -MethodName GetFeatureValue -Arguments @{IDs=[uint32]0x04030002}).Value
    "CPU=$cpu GPU=$gpu" | Add-Content -LiteralPath (Join-Path $PSScriptRoot 'restore-auto-rpm.log')
}
