param([switch]$Publish)

$ErrorActionPreference = 'Stop'
$localSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }

# The local SDK is optional; a normal .NET 8 SDK installation works as well.
& $dotnetExe build (Join-Path $PSScriptRoot 'AudioLatency.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $dotnetExe run --project (Join-Path $PSScriptRoot 'tests\AudioLatency.Core.Tests') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }

if ($Publish) {
    & $dotnetExe publish (Join-Path $PSScriptRoot 'src\AudioLatency.App') -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o (Join-Path $PSScriptRoot 'artifacts\AudioLatency-win-x64')
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
