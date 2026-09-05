# Copies the firmware tables Windows already cached in the registry to files.
# Does not execute AML, load a driver, scan EC RAM, or access I/O ports.
$ErrorActionPreference = 'Stop'
$acpiOutput = Join-Path $PSScriptRoot 'acpi'
New-Item -ItemType Directory -Path $acpiOutput -Force | Out-Null
Get-ChildItem 'HKLM:\HARDWARE\ACPI' -Recurse | ForEach-Object {
    $acpiKey = Get-Item -LiteralPath $_.PSPath
    foreach ($acpiValueName in $acpiKey.GetValueNames()) {
        $acpiBytes = $acpiKey.GetValue($acpiValueName)
        if ($acpiBytes -is [byte[]] -and $acpiBytes.Length -ge 36) {
            $acpiSignature = [Text.Encoding]::ASCII.GetString($acpiBytes,0,4)
            if ($acpiSignature -in @('DSDT','SSDT')) {
                $acpiPart = $_.Name.Split('\')[3]
                [IO.File]::WriteAllBytes((Join-Path $acpiOutput ($acpiPart + '.aml')), $acpiBytes)
            }
        }
    }
}
Get-ChildItem -LiteralPath $acpiOutput | Select-Object Name,Length
