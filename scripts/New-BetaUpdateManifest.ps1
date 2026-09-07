[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PackagePath,
    [Parameter(Mandatory)] [version]$Version,
    [Parameter(Mandatory)] [long]$ReleaseSequence,
    [Parameter(Mandatory)] [string]$OutputPath,
    [string]$SignerPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) "Orbit Navigator Beta Signer\beta-manifest-signing-key.dpapi.json"),
    [DateTimeOffset]$PublishedAtUtc = [DateTimeOffset]::UtcNow,
    [DateTimeOffset]$ExpiresAtUtc = [DateTimeOffset]::UtcNow.AddDays(14),
    [DateTimeOffset]$RolloutStartUtc = [DateTimeOffset]::UtcNow,
    [DateTimeOffset]$RolloutEndUtc = [DateTimeOffset]::UtcNow.AddDays(2),
    [ValidateRange(0, 10000)] [int]$InitialRolloutBasisPoints = 10000,
    [ValidateRange(0, 10000)] [int]$FinalRolloutBasisPoints = 10000
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

function Write-BigEndianInt32([IO.Stream]$Stream, [int]$Value) {
    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Write-BigEndianInt64([IO.Stream]$Stream, [long]$Value) {
    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Write-LengthPrefixedBytes([IO.Stream]$Stream, [byte[]]$Value) {
    Write-BigEndianInt32 $Stream $Value.Length
    $Stream.Write($Value, 0, $Value.Length)
}

function Write-LengthPrefixedString([IO.Stream]$Stream, [string]$Value) {
    Write-LengthPrefixedBytes $Stream ([Text.Encoding]::UTF8.GetBytes($Value))
}

function Get-CanonicalManifestBytes($Document) {
    $stream = [IO.MemoryStream]::new()
    try {
        Write-LengthPrefixedBytes $stream ([Text.Encoding]::ASCII.GetBytes("OrbitNavigator.UpdateManifest.v1"))
        Write-BigEndianInt32 $stream $Document.schemaVersion
        Write-LengthPrefixedString $stream $Document.channel
        Write-BigEndianInt64 $stream $Document.releaseSequence
        Write-LengthPrefixedString $stream $Document.version
        Write-LengthPrefixedString $stream $Document.packageUri
        Write-LengthPrefixedString $stream $Document.sha256
        Write-BigEndianInt64 $stream $Document.sizeBytes
        $stream.WriteByte($(if ($Document.requiresRestart) { 1 } else { 0 }))
        Write-LengthPrefixedString $stream $Document.publishedAtUtc
        Write-LengthPrefixedString $stream $Document.expiresAtUtc
        Write-LengthPrefixedString $stream $Document.rolloutStartUtc
        Write-LengthPrefixedString $stream $Document.rolloutEndUtc
        Write-BigEndianInt32 $stream $Document.initialRolloutBasisPoints
        Write-BigEndianInt32 $stream $Document.finalRolloutBasisPoints
        Write-LengthPrefixedString $stream $Document.keyId
        return ,$stream.ToArray()
    }
    finally { $stream.Dispose() }
}

$package = (Resolve-Path -LiteralPath $PackagePath).Path
$signer = (Resolve-Path -LiteralPath $SignerPath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$packageInfo = Get-Item -LiteralPath $package
if ($packageInfo.Extension -ne '.exe' -or $packageInfo.Length -le 0) {
    throw "The Beta package must be a non-empty executable."
}
if ($ReleaseSequence -le 0) { throw "ReleaseSequence must be positive." }
if ($ExpiresAtUtc -le $PublishedAtUtc -or $ExpiresAtUtc - $PublishedAtUtc -gt [TimeSpan]::FromDays(31)) {
    throw "The manifest expiry window is invalid."
}
if ($RolloutStartUtc -lt $PublishedAtUtc -or $RolloutEndUtc -lt $RolloutStartUtc -or
    $RolloutEndUtc -gt $ExpiresAtUtc -or $FinalRolloutBasisPoints -lt $InitialRolloutBasisPoints) {
    throw "The rollout schedule is invalid."
}

$signerDocument = Get-Content -LiteralPath $signer -Raw | ConvertFrom-Json
if ($signerDocument.schemaVersion -ne 1 -or
    $signerDocument.algorithm -ne 'ECDSA-P256-SHA256-P1363' -or
    $signerDocument.protection -ne 'DPAPI-CurrentUser') {
    throw "The Beta signer document is unsupported."
}

$fileName = $packageInfo.Name
if ($fileName -notmatch '^OrbitNavigator-[0-9]+\.[0-9]+\.[0-9]+-beta\.[0-9]+\.exe$') {
    throw "The package filename must use OrbitNavigator-X.Y.Z-beta.N.exe."
}
$manifest = [ordered]@{
    schemaVersion = 1
    channel = "beta"
    releaseSequence = $ReleaseSequence
    version = $Version.ToString()
    packageUri = "https://orbit-nav-updater.snap-it.cc/beta/$fileName"
    sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    sizeBytes = $packageInfo.Length
    requiresRestart = $true
    publishedAtUtc = $PublishedAtUtc.ToUniversalTime().ToString("O")
    expiresAtUtc = $ExpiresAtUtc.ToUniversalTime().ToString("O")
    rolloutStartUtc = $RolloutStartUtc.ToUniversalTime().ToString("O")
    rolloutEndUtc = $RolloutEndUtc.ToUniversalTime().ToString("O")
    initialRolloutBasisPoints = $InitialRolloutBasisPoints
    finalRolloutBasisPoints = $FinalRolloutBasisPoints
    keyId = [string]$signerDocument.keyId
    signature = ""
}

$entropy = [Text.Encoding]::UTF8.GetBytes("OrbitNavigator.UpdateManifest.beta.v1")
$privateKey = $null
$ecdsa = [Security.Cryptography.ECDsa]::Create()
try {
    $privateKey = [Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$signerDocument.protectedPkcs8PrivateKeyBase64),
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $bytesRead = 0
    [void]$ecdsa.ImportPkcs8PrivateKey($privateKey, [ref]$bytesRead)
    if ($bytesRead -ne $privateKey.Length) { throw "The protected signer contains trailing data." }
    $actualPublic = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
    if ($actualPublic -cne [string]$signerDocument.publicKeySubjectPublicKeyInfoBase64) {
        throw "The protected signer public key does not match its metadata."
    }
    $canonical = Get-CanonicalManifestBytes $manifest
    try {
        $signature = $ecdsa.SignData(
            $canonical,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        $manifest.signature = [Convert]::ToBase64String($signature)
        if (-not $ecdsa.VerifyData(
            $canonical,
            $signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
            throw "The generated manifest signature did not verify."
        }
    }
    finally {
        if ($canonical) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($canonical) }
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
    $temporary = "$output.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText(
            $temporary,
            ($manifest | ConvertTo-Json -Depth 4 -Compress),
            [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $output -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    [pscustomobject]@{
        ManifestPath = $output
        Channel = "beta"
        ReleaseSequence = $ReleaseSequence
        Version = $Version.ToString()
        KeyId = $manifest.keyId
        PackageSha256 = $manifest.sha256
        ManifestSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    }
}
finally {
    if ($privateKey) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($privateKey) }
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($entropy)
    $ecdsa.Dispose()
}
