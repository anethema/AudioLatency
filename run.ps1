$ErrorActionPreference = 'Stop'
$portableExe = Join-Path $PSScriptRoot 'artifacts\AudioLatency-win-x64\AudioLatency.exe'
$developmentExe = Join-Path $PSScriptRoot 'src\AudioLatency.App\bin\Release\net8.0-windows\AudioLatency.exe'
if (Test-Path -LiteralPath $portableExe) {
    Start-Process -FilePath $portableExe
} elseif (Test-Path -LiteralPath $developmentExe) {
    Start-Process -FilePath $developmentExe
} else {
    throw 'Build the app first: .\build.ps1 -Publish'
}
