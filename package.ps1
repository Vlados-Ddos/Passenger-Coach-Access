param(
    [string]$Configuration = "Release"
)

$projectRoot = $PSScriptRoot
$buildDirectory = Join-Path $projectRoot "bin\$Configuration"
$stagingDirectory = Join-Path $projectRoot "dist\PassengerCoachAccess"
$archivePath = Join-Path $projectRoot "dist\PassengerCoachAccess-1.32.zip"

if (-not (Test-Path (Join-Path $buildDirectory "PassengerCoachAccess.dll"))) {
    throw "Build the project before packaging it."
}

New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null
Copy-Item (Join-Path $buildDirectory "PassengerCoachAccess.dll") $stagingDirectory -Force
Copy-Item (Join-Path $projectRoot "info.json") $stagingDirectory -Force

if (Test-Path $archivePath) {
    Remove-Item $archivePath -Force
}

Compress-Archive -Path (Join-Path $stagingDirectory "*") -DestinationPath $archivePath
Write-Host "Created $archivePath"
