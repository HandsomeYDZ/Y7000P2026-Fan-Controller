# Run the compiled application adapter's read-only probe with the bundled SDK.
$ErrorActionPreference = 'Stop'
& 'C:\Users\Legion-Desktop\dotnet\dotnet.exe' (Join-Path $PSScriptRoot '..\tests\FanSafety.Tests\bin\Release\net8.0-windows\FanSafety.Tests.dll') --probe *> (Join-Path $PSScriptRoot 'verify-target-adapter.log')
exit $LASTEXITCODE
