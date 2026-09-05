# Reviewed 8-second, 2600 RPM test; complete output is retained even with a hidden UAC process.
$ErrorActionPreference = 'Stop'
try {
    & (Join-Path $PSScriptRoot 'test-normal-mode-rpm.ps1') -Apply *> (Join-Path $PSScriptRoot 'normal-rpm-test.log')
    exit $LASTEXITCODE
} catch {
    $_ | Out-String | Add-Content -LiteralPath (Join-Path $PSScriptRoot 'normal-rpm-test.log')
    exit 1
}
