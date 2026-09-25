# YuLiangToolkit one-click publish script.
# Builds a Release copy into dist\YuLiangToolkit (framework-dependent by default).
# Data\ and Config\ are created next to the published exe on first run; this script only
# drops the README files there (never the runtime *.json configs).
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 parses it correctly.
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "dist\YuLiangToolkit",
    [switch]$SelfContained,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-cli-home"
Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

Push-Location $root
try {
    $dotnetArgs = @(
        "publish", "YuLiangTookit.csproj",
        "-c", $Configuration,
        "-o", $OutputDir,
        "-m:1", "/nodeReuse:false"
    )
    if ($SelfContained) {
        $dotnetArgs += @("-r", $Runtime, "--self-contained", "true")
        Write-Host "==> self-contained ($Runtime): the folder runs without a .NET install" -ForegroundColor DarkGray
    }

    Write-Host "==> dotnet publish ($Configuration)" -ForegroundColor Cyan
    dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    # Ship the two README files next to the exe. Runtime state (Config\*\*.json, Data\*\)
    # is NOT copied - the app writes its own docs on first run (see RuntimeReadme).
    New-Item -ItemType Directory -Path (Join-Path $OutputDir "Data"), (Join-Path $OutputDir "Config") -Force | Out-Null
    foreach ($pair in @(@("Data\README.md", "Data"), @("Config\README.md", "Config"))) {
        $src = Join-Path $root $pair[0]
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $OutputDir $pair[1]) -Force
        }
    }

    $full = (Resolve-Path $OutputDir).Path
    Write-Host ""
    Write-Host "==> OK" -ForegroundColor Green
    Write-Host "    folder : $full"
    Write-Host "    exe    : $full\YuLiangTookit.exe"
    Write-Host "    Data / Config will be created next to the exe on first run."
}
finally {
    Pop-Location
}
