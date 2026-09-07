Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

function Get-OrbitProjectRoot {
    return Split-Path -Parent $PSScriptRoot
}

function Get-OrbitDotNet {
    $projectRoot = Get-OrbitProjectRoot
    $workspaceDotNet = Join-Path $projectRoot ".tools\dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $workspaceDotNet) {
        return $workspaceDotNet
    }

    $systemDotNet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $systemDotNet) {
        throw "No .NET SDK was found. Run scripts\Bootstrap-Toolchain.ps1."
    }

    return $systemDotNet.Source
}

function Get-OrbitProductVersion {
    $projectRoot = Get-OrbitProjectRoot
    $versionFile = Join-Path $projectRoot "Versions\ProductVersion.props"
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        throw "Central product version file is missing: $versionFile"
    }

    [xml]$versionDocument = Get-Content -LiteralPath $versionFile -Raw
    $version = [string]$versionDocument.Project.PropertyGroup.OrbitProductVersion
    if ($version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
        throw "Central product version is invalid: $version"
    }
    return $version
}

function Assert-LastExitCode([string]$Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Test-OrbitWebView2Payload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PayloadPath,
        [Parameter(Mandatory)]
        [string]$ChecksumPath
    )

    if (-not (Test-Path -LiteralPath $PayloadPath -PathType Leaf)) {
        throw "Offline WebView2 payload is missing: $PayloadPath"
    }

    if (-not (Test-Path -LiteralPath $ChecksumPath -PathType Leaf)) {
        throw "Pinned WebView2 SHA-256 file is missing: $ChecksumPath"
    }

    $payload = Get-Item -LiteralPath $PayloadPath
    if ($payload.Length -lt 50MB) {
        throw "Offline WebView2 payload is too small to be the x64 Evergreen Standalone Installer."
    }

    $expectedHash = (Get-Content -LiteralPath $ChecksumPath -Raw).Trim()
    if ($expectedHash -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "Pinned WebView2 SHA-256 file must contain exactly one SHA-256 value."
    }

    $actualHash = (Get-FileHash -LiteralPath $PayloadPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Offline WebView2 payload SHA-256 does not match its pinned value."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $PayloadPath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw "Offline WebView2 payload must have a valid Microsoft Authenticode signature."
    }

    return [pscustomobject]@{
        Path = $PayloadPath
        Sha256 = $actualHash.ToUpperInvariant()
        Signer = $signature.SignerCertificate.Subject
    }
}

function Get-OrbitInnoCompiler {
    [CmdletBinding()]
    param(
        [string]$ExplicitPath
    )

    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Inno Setup compiler was not found at: $ExplicitPath"
        }
        return $ExplicitPath
    }

    $projectRoot = Get-OrbitProjectRoot
    foreach ($workspacePath in @(
        (Join-Path $projectRoot '.tools\Inno Setup 6\ISCC.exe'),
        (Join-Path $projectRoot '.tools\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )) {
        if (Test-Path -LiteralPath $workspacePath -PathType Leaf) {
            return $workspacePath
        }
    }

    $command = Get-Command iscc -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}
