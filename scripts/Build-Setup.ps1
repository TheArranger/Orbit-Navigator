[CmdletBinding()]
param(
    [string]$Version,
    [string]$InnoCompiler
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Common.ps1")
$projectRoot = Split-Path -Parent $PSScriptRoot
$Version = if ($Version) { $Version } else { Get-OrbitProductVersion }
$setupSource = Join-Path $projectRoot "installer\OrbitNavigator.Setup.iss"
$webViewPayload = Join-Path $projectRoot "installer\payloads\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
$webViewChecksum = Join-Path $projectRoot "installer\payloads\MicrosoftEdgeWebView2RuntimeInstallerX64.sha256"

$InnoCompiler = Get-OrbitInnoCompiler -ExplicitPath $InnoCompiler

if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw "Inno Setup compiler not found. Install/stage Inno Setup 6 or later and pass -InnoCompiler if needed."
}

Test-OrbitWebView2Payload -PayloadPath $webViewPayload -ChecksumPath $webViewChecksum | Out-Null

& (Join-Path $PSScriptRoot "Publish.ps1") -Version $Version -Configuration Release
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

& $InnoCompiler "/DMyAppVersion=$Version" $setupSource
if ($LASTEXITCODE -ne 0) { throw "Setup compilation failed with exit code $LASTEXITCODE." }
