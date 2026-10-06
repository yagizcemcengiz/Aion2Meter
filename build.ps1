param([ValidateSet('restore', 'build', 'test', 'all', 'setup')][string]$Action = 'all')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
. (Join-Path $PSScriptRoot 'dev-env.ps1')
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed ($LASTEXITCODE)." }
}
if ($Action -eq 'setup') {
    Invoke-DotNet @('new', 'sln', '--name', 'Aion2Meter', '--format', 'sln')
    Invoke-DotNet @('sln', 'Aion2Meter.sln', 'add', 'src/Aion2Meter.Core/Aion2Meter.Core.csproj', 'src/Aion2Meter.Capture/Aion2Meter.Capture.csproj', 'src/Aion2Meter.Replay/Aion2Meter.Replay.csproj', 'src/Aion2Meter.App/Aion2Meter.App.csproj', 'tests/Aion2Meter.Tests/Aion2Meter.Tests.csproj')
    exit 0
}
if ($Action -in @('restore', 'all')) { Invoke-DotNet @('restore', 'Aion2Meter.sln') }
if ($Action -in @('build', 'all')) { Invoke-DotNet @('build', 'Aion2Meter.sln', '--no-restore') }
if ($Action -in @('test', 'all')) { Invoke-DotNet @('test', 'Aion2Meter.sln', '--no-build', '--no-restore') }
