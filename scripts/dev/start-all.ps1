# start-all.ps1 — Start all development services
param(
    [switch]$AgentOnly,
    [switch]$DesktopOnly
)

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $DesktopOnly) {
    Write-Host "[1/2] Starting Agent Service (.NET)..." -ForegroundColor Cyan
    Start-Process powershell -ArgumentList "-NoExit", "-Command", `
        "cd '$root\apps\agent-service'; dotnet run" `
        -WindowStyle Normal
    Start-Sleep -Seconds 3
}

if (-not $AgentOnly) {
    Write-Host "[2/2] Starting Desktop (Tauri dev)..." -ForegroundColor Cyan
    Start-Process powershell -ArgumentList "-NoExit", "-Command", `
        "cd '$root\apps\desktop'; pnpm tauri dev" `
        -WindowStyle Normal
}

Write-Host "`nAll services started. Close the opened windows to stop." -ForegroundColor Green
