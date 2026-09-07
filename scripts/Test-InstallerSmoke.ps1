[CmdletBinding()]
param(
    [string]$SetupPath,
    [string]$ExpectedAppId = "{C5104F72-0D7B-4DA9-B59C-BC02DF0478C2}"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Common.ps1")
$projectRoot = Get-OrbitProjectRoot
if (-not $SetupPath) {
    $SetupPath = Join-Path $projectRoot "dist\Setup.exe"
}

$resolvedSetup = (Resolve-Path -LiteralPath $SetupPath).Path
$verificationRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $projectRoot "artifacts\verification"))
$installRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $verificationRoot "installer-smoke"))
$expectedInstallRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $projectRoot "artifacts\verification\installer-smoke"))
if (-not $installRoot.Equals($expectedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected installer smoke target: $installRoot"
}
if (Test-Path -LiteralPath $installRoot) {
    throw "Installer smoke target must not already exist: $installRoot"
}

$setupArguments =
    "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /DIR=`"$installRoot`""
$install = Start-Process `
    -FilePath $resolvedSetup `
    -ArgumentList $setupArguments `
    -Wait `
    -PassThru
if ($install.ExitCode -ne 0) {
    throw "Silent installation failed with exit code $($install.ExitCode)."
}

$installedLauncher = Join-Path $installRoot "Orbit Navigator.exe"
$installedApp = Join-Path $installRoot "app\OrbitNavigator.App.exe"
$installedIdentityIcon = Join-Path $installRoot "OrbitNavigator.ico"
$installedSequence = Join-Path $installRoot "app\assets\loading\sequence-v1"
if (-not (Test-Path -LiteralPath $installedLauncher -PathType Leaf) -or
    -not (Test-Path -LiteralPath $installedApp -PathType Leaf) -or
    -not (Test-Path -LiteralPath $installedIdentityIcon -PathType Leaf)) {
    throw "Installed executable payload is incomplete."
}
$sourceIdentityIcon = Join-Path $projectRoot "assets\branding\orbit-navigator.ico"
if ((Get-FileHash -LiteralPath $sourceIdentityIcon -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $installedIdentityIcon -Algorithm SHA256).Hash) {
    throw "Installed program identity ICO hash does not match."
}
if (@(Get-ChildItem -LiteralPath $installedSequence -File -Filter "*.png").Count -ne 6) {
    throw "Installed loading sequence is incomplete."
}
if (-not (Test-Path -LiteralPath `
    (Join-Path $installRoot "app\OrbitNavigator.Sync.dll") -PathType Leaf)) {
    throw "Installed Sync composition is missing."
}

foreach ($assetName in @(
    "orbit-navigation-field-v2.png",
    "orbit-deep-space-field-v3.png",
    "bookmark-star-v1.png",
    "workspace-star-v1.png",
    "favorite-star-burn-atlas-v2.png",
    "favorite-star-burn-atlas-v3.png",
    "tab-group-star-burn-atlas-v2.png",
    "tab-group-star-burn-atlas-v3.png",
    "orbit-deep-space-base-v4.png",
    "orbit-nebula-veil-v4.png",
    "orbit-near-starfield-v4.png",
    "favorite-star-flare-atlas-v4.png",
    "tab-group-star-flare-atlas-v4.png",
    "orbit-deep-space-base-v5.png",
    "orbit-celestial-elements-atlas-v5.png",
    "favorite-star-solar-atlas-v5.png",
    "workspace-star-solar-atlas-v5.png",
    "orbit-deep-space-base-v6-a.png",
    "orbit-deep-space-base-v6-b.png",
    "orbit-deep-space-base-v6-c.png",
    "orbit-celestial-frames-atlas-v6.png",
    "favorite-star-solar-atlas-v6.png",
    "workspace-star-solar-atlas-v6.png")) {
    $sourceAsset = Join-Path $projectRoot "assets\new-tab\$assetName"
    $installedAsset = Join-Path $installRoot "app\assets\new-tab\$assetName"
    if (-not (Test-Path -LiteralPath $installedAsset -PathType Leaf)) {
        throw "Installed New Tab asset is missing: $assetName"
    }
    if ((Get-FileHash -LiteralPath $sourceAsset -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $installedAsset -Algorithm SHA256).Hash) {
        throw "Installed New Tab asset hash does not match: $assetName"
    }
}

$expectedV3AssetHashes = @{
    "orbit-deep-space-field-v3.png" =
        "B1DE15E1D658E556F19CBD5DBC3CC3B83C8DAF70EE6611ECC52ECD2536B10C7E"
    "favorite-star-burn-atlas-v3.png" =
        "679BE0516E39B2EDC61F7E2FEDC335E2B38AF1512073E23EC6D7D34F376676BD"
    "tab-group-star-burn-atlas-v3.png" =
        "EBF95CC41D58AD76B6B649850B3D38AFA749C63CBE30F47915AC1F6583A94087"
}
foreach ($entry in $expectedV3AssetHashes.GetEnumerator()) {
    $sourceAsset = Join-Path $projectRoot "assets\new-tab\$($entry.Key)"
    if ((Get-FileHash -LiteralPath $sourceAsset -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "Packaged New Tab v3 asset differs from its approved hash: $($entry.Key)"
    }
}

$expectedV5AssetHashes = @{
    "orbit-deep-space-base-v5.png" =
        "5B0759376C6EBFEAEB2264DD70B6BBFDB55254ADACD3E0A2FAD78E775BC0D4EB"
    "orbit-celestial-elements-atlas-v5.png" =
        "023E33DEC6DBCA4D65995AE936AD7A37E5CDE725564C63F8C160010F5C875010"
    "favorite-star-solar-atlas-v5.png" =
        "E5853C35EAAB9BD0D4783C1012C94A516B6041AF942F0990B8FAB436F5E2D459"
    "workspace-star-solar-atlas-v5.png" =
        "4267125F70BD096904893D86B73258FBA48EA9155CD12B42540E59B53550CFB9"
}
foreach ($entry in $expectedV5AssetHashes.GetEnumerator()) {
    $sourceAsset = Join-Path $projectRoot "assets\new-tab\$($entry.Key)"
    if ((Get-FileHash -LiteralPath $sourceAsset -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "Packaged New Tab v5 asset differs from its approved hash: $($entry.Key)"
    }
}

$expectedV6AssetHashes = @{
    "orbit-deep-space-base-v6-a.png" =
        "EA4BC45E1D172BB68BBED7C5B218FAFC40EE9BBBC191C5BACF67DA7308700BC6"
    "orbit-deep-space-base-v6-b.png" =
        "5ACACF0A1E5C0220360EF008F9CE711AF1B3F3E76221E578DC12B98ED09A6BE6"
    "orbit-deep-space-base-v6-c.png" =
        "4225952EFA62BB23012A1DF9E75BC1698C09506B3D1A6AF0B9469054443CDE44"
    "orbit-celestial-frames-atlas-v6.png" =
        "99F79001983406D8333ABD811B80FFC89C0E019D1DD0E137CD0FBC8CA58D3C46"
    "favorite-star-solar-atlas-v6.png" =
        "52B16657F411468BD5C826F3ED9831A34F3F1D16629400E5FA44FFBE364870D8"
    "workspace-star-solar-atlas-v6.png" =
        "714E036528A75EA8040C04432A4F946025F1EA1BB17B919460625993AAD62C83"
}
foreach ($entry in $expectedV6AssetHashes.GetEnumerator()) {
    $sourceAsset = Join-Path $projectRoot "assets\new-tab\$($entry.Key)"
    if ((Get-FileHash -LiteralPath $sourceAsset -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "Packaged New Tab v6 asset differs from its approved hash: $($entry.Key)"
    }
}

$brandingAssetName = "orbit-navigator-program-logo-v2.png"
$sourceBrandingAsset = Join-Path $projectRoot "assets\branding\$brandingAssetName"
$installedBrandingAsset = Join-Path `
    $installRoot `
    "app\assets\branding\$brandingAssetName"
if (-not (Test-Path -LiteralPath $installedBrandingAsset -PathType Leaf)) {
    throw "Installed New Tab branding asset is missing: $brandingAssetName"
}
if ((Get-FileHash -LiteralPath $sourceBrandingAsset -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $installedBrandingAsset -Algorithm SHA256).Hash) {
    throw "Installed New Tab branding asset hash does not match: $brandingAssetName"
}

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $projectRoot "scripts\Test-NormalLaunch.ps1") `
    -LauncherPath $installedLauncher
if ($LASTEXITCODE -ne 0) {
    throw "Installed launcher smoke failed."
}
& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $projectRoot "scripts\Test-WebView2Runtime.ps1") `
    -ApplicationPath $installedApp
if ($LASTEXITCODE -ne 0) {
    throw "Installed WebView2 smoke failed."
}

$uninstaller = Join-Path $installRoot "unins000.exe"
if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
    throw "Uninstaller was not created."
}
$uninstall = Start-Process `
    -FilePath $uninstaller `
    -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" `
    -Wait `
    -PassThru
if ($uninstall.ExitCode -ne 0) {
    throw "Silent uninstall failed with exit code $($uninstall.ExitCode)."
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
while ((Test-Path -LiteralPath $installRoot) -and
    [DateTimeOffset]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 100
}
if (Test-Path -LiteralPath $installRoot) {
    $resolvedAfterUninstall = [System.IO.Path]::GetFullPath($installRoot)
    if (-not $resolvedAfterUninstall.Equals(
        $expectedInstallRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup of unexpected directory: $resolvedAfterUninstall"
    }
    Remove-Item -LiteralPath $resolvedAfterUninstall -Recurse -Force
}

if ($ExpectedAppId -notmatch '^\{[0-9A-Fa-f-]{36}\}$') {
    throw "ExpectedAppId must be a braced GUID."
}
$uninstallKey =
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($ExpectedAppId)_is1"
if (Test-Path -LiteralPath $uninstallKey) {
    throw "Installer smoke left an uninstall registration behind."
}

Write-Host "Offline Setup.exe install, installed-launch, WebView2, payload, and uninstall smoke passed."
