# Starts the PersonalAI API and web frontend for local development.
# API   -> http://localhost:5080 (see src/PersonalAI.Api/Properties/launchSettings.json)
# Web   -> http://localhost:3000

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if (-not (Test-Path (Join-Path $root "web\node_modules"))) {
    Write-Host "Installing web dependencies..."
    Start-Process powershell -WorkingDirectory (Join-Path $root "web") -ArgumentList "-NoExit", "-Command", "npm install" -Wait
}

Start-Process powershell -WorkingDirectory $root -ArgumentList "-NoExit", "-Command", "dotnet run --project src/PersonalAI.Api"
Start-Process powershell -WorkingDirectory (Join-Path $root "web") -ArgumentList "-NoExit", "-Command", "npm run dev"

Write-Host "API starting at http://localhost:5080"
Write-Host "Web starting at http://localhost:3000"
