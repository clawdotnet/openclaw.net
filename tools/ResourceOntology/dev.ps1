#!/usr/bin/env pwsh
# Development mode with hot-reload: starts the ASP.NET API (:5174) and the Vite dev
# server (:5173) together. Vite proxies /api to the API. Open http://localhost:5173.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Test-Path (Join-Path $root 'client/node_modules'))) {
    Write-Host '==> Installing client dependencies...' -ForegroundColor Cyan
    Push-Location (Join-Path $root 'client'); npm install; Pop-Location
}

Write-Host '==> Starting API (:5174) and Vite (:5173)...' -ForegroundColor Cyan
Write-Host '    Open http://localhost:5173' -ForegroundColor Green

$api = Start-Process pwsh -PassThru -ArgumentList @(
    '-NoExit', '-Command', "Set-Location '$root/server'; dotnet watch run --urls http://localhost:5174"
)
$web = Start-Process pwsh -PassThru -ArgumentList @(
    '-NoExit', '-Command', "Set-Location '$root/client'; npm run dev"
)

Write-Host "API pid=$($api.Id)  Vite pid=$($web.Id). Close those windows to stop." -ForegroundColor DarkGray
