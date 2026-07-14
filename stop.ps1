# Stop the migration platform stack.
# Usage: .\stop.ps1 [-Down] [-Clean]
#   (no args)  stop containers, keep them and the database volume (fast restart)
#   -Down      remove containers + network (keeps the database volume)
#   -Clean     remove containers + network + DELETE the database volume (fresh start)
param([switch]$Down, [switch]$Clean)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

if ($Clean) {
    Write-Host "Removing containers + network + DELETING the database volume..." -ForegroundColor Yellow
    docker compose down -v
} elseif ($Down) {
    Write-Host "Removing containers + network (database volume preserved)..."
    docker compose down
} else {
    Write-Host "Stopping containers (database volume preserved)..."
    docker compose stop
}
Write-Host "Done." -ForegroundColor Green
