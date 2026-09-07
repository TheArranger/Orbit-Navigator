[CmdletBinding()]
param(
    [string]$DotNetVersion
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $projectRoot ".tools"
$dotnetRoot = Join-Path $toolRoot "dotnet"
$bootstrapRoot = Join-Path $toolRoot "bootstrap"
$installerPath = Join-Path $bootstrapRoot "dotnet-install.ps1"
$globalJsonPath = Join-Path $projectRoot "global.json"

if (-not $DotNetVersion) {
    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
    $DotNetVersion = [string]$globalJson.sdk.version
}
if ([string]::IsNullOrWhiteSpace($DotNetVersion)) {
    throw "global.json must declare an exact .NET SDK version."
}

New-Item -ItemType Directory -Path $dotnetRoot -Force | Out-Null
New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $installerPath)) {
    Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installerPath
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath `
    -Version $DotNetVersion `
    -Architecture x64 `
    -InstallDir $dotnetRoot `
    -NoPath

if ($LASTEXITCODE -ne 0) {
    throw "Workspace-local .NET SDK installation failed with exit code $LASTEXITCODE."
}

& (Join-Path $dotnetRoot "dotnet.exe") --info
