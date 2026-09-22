# YuLiangToolkit one-click build script (offline: uses local NuGet cache)
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 parses it correctly.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Redirect dotnet CLI user state (first-run sentinel, telemetry...) into the workspace
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-cli-home"
# Use the default NuGet global cache (packages for 8.0.x already present locally)
Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

Write-Host "DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME" -ForegroundColor DarkGray

Push-Location $root
try {
    Write-Host "==> restore" -ForegroundColor Cyan
    dotnet restore YuLiangToolkit.sln -m:1 /nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "restore failed" }

    Write-Host "==> build" -ForegroundColor Cyan
    dotnet build YuLiangToolkit.sln -c Debug --no-restore -m:1 /nodeReuse:false /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    Write-Host "==> OK" -ForegroundColor Green
}
finally {
    Pop-Location
}
