# Show the status of the migration platform stack.
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

try { docker info *> $null } catch {
    Write-Host "[X] Docker engine isn't running. Start Docker Desktop, then re-run .\status.ps1" -ForegroundColor Red
    exit 1
}

Write-Host "Containers:" -ForegroundColor Cyan
docker compose ps
Write-Host ""
Write-Host "API readiness (http://localhost:5000/health/ready):" -ForegroundColor Cyan
try {
    $resp = Invoke-WebRequest -Uri "http://localhost:5000/health/ready" -UseBasicParsing -TimeoutSec 3
    $d = $resp.Content | ConvertFrom-Json
    Write-Host "   overall: $($d.status)"
    foreach ($c in $d.checks) { Write-Host "   - $($c.name): $($c.status)" }
} catch {
    Write-Host "   (API not reachable - it may be starting, stopped, or unhealthy. Try 'docker compose logs -f api')"
}
