[CmdletBinding()]
param(
    [string]$KeyId = "beta-2026-01",
    [string]$DestinationDirectory = (Join-Path ([Environment]::GetFolderPath('Desktop')) "Orbit Navigator Beta Signer")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

if ([string]::IsNullOrWhiteSpace($KeyId) -or $KeyId.Length -gt 64 -or
    $KeyId -notmatch '^[a-z0-9][a-z0-9.-]+$') {
    throw "The signing key identifier is invalid."
}

$directory = [IO.Path]::GetFullPath($DestinationDirectory)
$keyPath = Join-Path $directory "beta-manifest-signing-key.dpapi.json"
if (Test-Path -LiteralPath $keyPath) {
    throw "A Beta signing key already exists at the protected destination. It was not overwritten."
}

New-Item -ItemType Directory -Path $directory -Force | Out-Null
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$aclResult = & icacls.exe $directory /inheritance:r /grant:r "$($identity.Name):(OI)(CI)F" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Could not restrict the temporary signer directory ACL: $aclResult"
}
$unexpectedRules = (Get-Acl -LiteralPath $directory).Access | Where-Object {
    $_.IdentityReference.Value -ne $identity.Name
}
if ($unexpectedRules) {
    throw "The temporary signer directory contains an unexpected access rule."
}

$entropy = [Text.Encoding]::UTF8.GetBytes("OrbitNavigator.UpdateManifest.beta.v1")
$privateKey = $null
$ecdsa = [Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.GenerateKey([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $privateKey = $ecdsa.ExportPkcs8PrivateKey()
    $publicKey = $ecdsa.ExportSubjectPublicKeyInfo()
    $protectedKey = [Security.Cryptography.ProtectedData]::Protect(
        $privateKey,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $document = [ordered]@{
        schemaVersion = 1
        keyId = $KeyId
        algorithm = "ECDSA-P256-SHA256-P1363"
        protection = "DPAPI-CurrentUser"
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        publicKeySubjectPublicKeyInfoBase64 = [Convert]::ToBase64String($publicKey)
        protectedPkcs8PrivateKeyBase64 = [Convert]::ToBase64String($protectedKey)
    }
    $json = $document | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText($keyPath, $json, [Text.UTF8Encoding]::new($false))

    [pscustomobject]@{
        KeyId = $KeyId
        ProtectedKeyPath = $keyPath
        PublicKeySubjectPublicKeyInfoBase64 = [Convert]::ToBase64String($publicKey)
        Protection = "DPAPI CurrentUser; usable only by the current Windows account"
    }
}
finally {
    if ($privateKey) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($privateKey)
    }
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($entropy)
    if ($ecdsa) { $ecdsa.Dispose() }
}
