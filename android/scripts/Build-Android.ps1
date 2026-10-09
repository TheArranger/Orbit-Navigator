[CmdletBinding()]
param(
    [string]$SdkRoot = 'C:\Android\android_sdk',
    [string]$Gradle = 'C:\Android\gradle-home\wrapper\dists\gradle-8.11.1-bin\bpt9gzteqjrbo1mjrsomdt32c\gradle-8.11.1\bin\gradle.bat',
    [string]$GradleCache = 'C:\Android\gradle-home',
    [switch]$Offline,
    [switch]$IncludeDevelopmentApk
)
$ErrorActionPreference = 'Stop'
$androidRoot = Split-Path -Parent $PSScriptRoot
foreach ($required in @((Join-Path $SdkRoot 'platforms\android-35\android.jar'), (Join-Path $SdkRoot 'build-tools\35.0.0\aapt2.exe'), $Gradle)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing prerequisite: $required. Install the official tooling and explicitly review its licenses; this script does not install or accept licenses." }
}
$previousSdk = $env:ANDROID_HOME
$previousCache = $env:GRADLE_USER_HOME
$previousAndroidUserHome = $env:ANDROID_USER_HOME
try {
    $env:ANDROID_HOME = $SdkRoot
    $env:GRADLE_USER_HOME = $GradleCache
    # Keep tool metadata/debug defaults out of the real user's Android profile.
    $env:ANDROID_USER_HOME = Join-Path $androidRoot '.tools\android-user'
    New-Item -ItemType Directory -Path $env:ANDROID_USER_HOME -Force | Out-Null
    $toolUserHome = Join-Path $androidRoot '.tools\jdk-user'
    New-Item -ItemType Directory -Path (Join-Path $toolUserHome '.android') -Force | Out-Null
    $arguments = @('--no-daemon', '--console=plain', "-Duser.home=$toolUserHome", ':core:check', ':app:assembleRelease', ':app:lintRelease')
    if ($IncludeDevelopmentApk) {
        $debugKey = Join-Path $androidRoot '.tools\debug-signing\orbit-development.keystore'
        if (-not (Test-Path -LiteralPath $debugKey)) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $debugKey) -Force | Out-Null
            # Public standard debug credentials, scoped to this workspace; never a production identity.
            & keytool -genkeypair -keystore $debugKey -storetype PKCS12 -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 3650 -dname 'CN=Orbit Navigator Development,O=Local Unpublished,C=US'
            if ($LASTEXITCODE -ne 0) { throw 'Could not generate task-local development signing key.' }
        }
        $arguments += ':app:assembleDebug', ':app:lintDebug'
    }
    if ($Offline) { $arguments += '--offline' }
    Push-Location $androidRoot
    try {
        & $Gradle @arguments
        if ($LASTEXITCODE -ne 0) { throw "Android build failed ($LASTEXITCODE). No device was installed or changed." }
    } finally { Pop-Location }
} finally {
    $env:ANDROID_HOME = $previousSdk
    $env:GRADLE_USER_HOME = $previousCache
    $env:ANDROID_USER_HOME = $previousAndroidUserHome
}
Write-Output 'Built an unsigned local release APK. No device install, production signing, provider deployment or public upload occurred.'
if ($IncludeDevelopmentApk) { Write-Output 'Also built a development-signed .dev APK using the task-local debug key. This is not a public release.' }
