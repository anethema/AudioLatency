$ErrorActionPreference = 'Stop'
$artifactsDirectory = Join-Path $PSScriptRoot 'artifacts'
# Side-by-side builds let an existing measurement window keep its unsaved reference.
$latestPortable = Get-ChildItem -Path (Join-Path $artifactsDirectory 'AudioLatency-win-x64*\AudioLatency.exe') `
    -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$portableExe = if ($latestPortable) { $latestPortable.FullName } else { Join-Path $artifactsDirectory 'AudioLatency-win-x64\AudioLatency.exe' }
$developmentExe = Join-Path $PSScriptRoot 'src\AudioLatency.App\bin\Release\net8.0-windows\AudioLatency.exe'
if (Test-Path -LiteralPath $portableExe) {
    Start-Process -FilePath $portableExe
} elseif (Test-Path -LiteralPath $developmentExe) {
    Start-Process -FilePath $developmentExe
} else {
    throw 'Build the app first: .\build.ps1 -Publish'
}
