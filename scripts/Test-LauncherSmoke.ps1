[CmdletBinding()]
param(
    [int]$ExpectedExitCode = -1
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $projectRoot "Orbit Navigator.exe"
$application = Join-Path $projectRoot "app\OrbitNavigator.App.exe"

if ($ExpectedExitCode -lt 0) {
    $ExpectedExitCode = if (Test-Path -LiteralPath $application) { 0 } else { 2 }
}

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Root launcher does not exist. Run Publish-Launcher.ps1 first."
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $launcher
$startInfo.Arguments = "--launcher-smoke"
$startInfo.UseShellExecute = $false
$process = [System.Diagnostics.Process]::Start($startInfo)
if (-not $process.WaitForExit(20000)) {
    $process.Kill()
    throw "Launcher smoke timed out after 20 seconds."
}
if ($process.ExitCode -ne $ExpectedExitCode) {
    throw "Launcher smoke returned $($process.ExitCode); expected $ExpectedExitCode."
}

Write-Host "Launcher smoke passed with exit code $ExpectedExitCode."
