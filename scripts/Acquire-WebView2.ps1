[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern("^[A-Fa-f0-9]{64}$")]
    [string]$ExpectedSha256,
    [Parameter(Mandatory)]
    [ValidatePattern("^https://msedge[.]sf[.]dl[.]delivery[.]mp[.]microsoft[.]com/.+/MicrosoftEdgeWebView2RuntimeInstallerX64[.]exe$")]
    [string]$DownloadUrl
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Common.ps1")
$projectRoot = Split-Path -Parent $PSScriptRoot
$payloadRoot = Join-Path $projectRoot "installer\payloads"
$payloadPath = Join-Path $payloadRoot "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
$checksumPath = Join-Path $payloadRoot "MicrosoftEdgeWebView2RuntimeInstallerX64.sha256"
$temporaryPayloadPath = Join-Path $payloadRoot "MicrosoftEdgeWebView2RuntimeInstallerX64.download"

New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
Remove-Item -LiteralPath $temporaryPayloadPath -Force -ErrorAction SilentlyContinue

try {
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $temporaryPayloadPath

    $actualHash = (Get-FileHash -LiteralPath $temporaryPayloadPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "WebView2 offline installer SHA-256 mismatch."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $temporaryPayloadPath
    if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "Microsoft Corporation") {
        throw "WebView2 offline installer does not have a valid Microsoft Authenticode signature."
    }

    Move-Item -LiteralPath $temporaryPayloadPath -Destination $payloadPath -Force
    Set-Content -LiteralPath $checksumPath -Value $actualHash.ToUpperInvariant() -NoNewline
} finally {
    Remove-Item -LiteralPath $temporaryPayloadPath -Force -ErrorAction SilentlyContinue
}

$verified = Test-OrbitWebView2Payload -PayloadPath $payloadPath -ChecksumPath $checksumPath
Write-Host "Verified WebView2 offline payload: $($verified.Path)"
