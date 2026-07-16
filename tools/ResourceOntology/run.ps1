#!/usr/bin/env pwsh
# One-command launch: build the Svelte SPA, then run the ASP.NET host which serves
# both the API and the built front-end on a single port. Opens the browser.
#
#   ./run.ps1            build client (if needed) + run
#   ./run.ps1 -Rebuild   force a fresh client build first

param(
    [switch]$Rebuild,
    [int]$Port = 5174
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Push-Location $root
try {
    $wwwroot = Join-Path $root 'server/wwwroot'
    if ($Rebuild -or -not (Test-Path (Join-Path $wwwroot 'index.html'))) {
        Write-Host '==> Building Svelte client...' -ForegroundColor Cyan
        Push-Location (Join-Path $root 'client')
        if (-not (Test-Path 'node_modules')) { npm install }
        npm run build
        Pop-Location
    }

    $url = "http://localhost:$Port"
    Write-Host "==> Starting ASP.NET host at $url" -ForegroundColor Cyan
    Start-Process $url
    Push-Location (Join-Path $root 'server')
    dotnet run --urls $url
    Pop-Location
}
finally {
    Pop-Location
}
