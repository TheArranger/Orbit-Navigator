[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Common.ps1")

$projectRoot = Get-OrbitProjectRoot
$setupPath = Join-Path $projectRoot "installer\OrbitNavigator.Setup.iss"
$shortcutsPath = Join-Path $projectRoot "installer\includes\Shortcuts.iss"
$setup = Get-Content -LiteralPath $setupPath -Raw
$shortcuts = Get-Content -LiteralPath $shortcutsPath -Raw

if ($setup -notmatch '(?im)^DisableFinishedPage\s*=\s*yes\s*$') {
    throw "Interactive installs must close immediately after successful completion."
}

$launchTaskPattern =
    '(?im)^Name:\s*"launchapp";[^\r\n]*Description:\s*"Launch Orbit Navigator after installation"'
if ($shortcuts -notmatch $launchTaskPattern) {
    throw "The installer must expose an optional launch-after-installation task."
}

$launchEntry = [regex]::Match(
    $shortcuts,
    '(?im)^Filename:\s*"\{app\}\\Orbit Navigator\.exe";[^\r\n]*\r?$')
if (-not $launchEntry.Success) {
    throw "The installer launch entry is missing."
}

if ($launchEntry.Value -notmatch '(?i)Tasks:\s*launchapp(?:;|\s|$)') {
    throw "The launch entry must be controlled by the launchapp task."
}
if ($launchEntry.Value -notmatch '(?i)Flags:[^\r\n]*\bnowait\b' -or
    $launchEntry.Value -notmatch '(?i)Flags:[^\r\n]*\bskipifsilent\b') {
    throw "The launch entry must not hold Setup open or launch during silent installs."
}
if ($launchEntry.Value -match '(?i)\bpostinstall\b') {
    throw "A postinstall launch would force Setup to retain the Finished page."
}

Write-Host "Installer completion lifecycle definition passed."
