# Start the full M365 Migration Platform stack (postgres + api + web) in Docker.
# Usage: .\start.ps1 [-Build]
param([switch]$Build)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

# --- Preflight: is Docker running? -------------------------------------------
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "[X] Docker is not installed or not on PATH." -ForegroundColor Red
    Write-Host "    Install Docker Desktop and start it, then re-run .\start.ps1"
    exit 1
}
try { docker info *> $null } catch {
    Write-Host "[X] Docker is installed but the engine isn't running." -ForegroundColor Red
    Write-Host "    Start Docker Desktop (whale icon), wait for 'Engine running', then re-run .\start.ps1"
    exit 1
}

Write-Host "Starting the migration platform (postgres + api + web)..." -ForegroundColor Cyan
Write-Host "First run builds the images and can take a few minutes."
if ($Build) { docker compose up -d --build } else { docker compose up -d }

Write-Host -NoNewline "Waiting for the API to become healthy"
$healthy = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        Invoke-WebRequest -Uri "http://localhost:5000/health/live" -UseBasicParsing -TimeoutSec 3 *> $null
        $healthy = $true; break
    } catch { Write-Host -NoNewline "."; Start-Sleep -Seconds 3 }
}

if ($healthy) {
    Write-Host " OK" -ForegroundColor Green
    Write-Host ""
    Write-Host "Ready!" -ForegroundColor Green
    Write-Host "   Web:      http://localhost:3000"
    Write-Host "   API:      http://localhost:5000   (Swagger: http://localhost:5000/swagger)"
    Write-Host "   Postgres: localhost:5432"
    Write-Host ""
    Write-Host "   Sign in with the dev login: admin / MigrationAdmin123!"
    Write-Host "   Stop with .\stop.ps1   -   status with .\status.ps1"
} else {
    Write-Host ""
    Write-Host "The API didn't report healthy yet. It may still be starting - check 'docker compose logs -f api'." -ForegroundColor Yellow
}
