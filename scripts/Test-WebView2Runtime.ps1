[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Common.ps1")
$projectRoot = Get-OrbitProjectRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot "app\OrbitNavigator.App.exe"
}

if (-not (Test-Path -LiteralPath $ApplicationPath -PathType Leaf)) {
    throw "Published Orbit Navigator application not found: $ApplicationPath"
}

$verificationRoot = Join-Path $projectRoot "artifacts\verification"
$resultPath = Join-Path $verificationRoot "webview2-runtime-smoke.json"
New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue

$resolvedApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$process = $null
$exitCode = $null
try {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $resolvedApplicationPath
    $startInfo.Arguments = "--webview-smoke"
    $startInfo.WorkingDirectory = Split-Path -Parent $resolvedApplicationPath
    $startInfo.UseShellExecute = $false
    $startInfo.EnvironmentVariables["ORBIT_WEBVIEW_SMOKE_RESULT"] = $resultPath
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "WebView2 runtime smoke process could not be started."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    while (-not $process.HasExited -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        $candidate = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)"
        if ($null -ne $candidate -and
            [System.IO.Path]::GetFullPath($candidate.ExecutablePath) -eq $resolvedApplicationPath) {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        throw "WebView2 runtime smoke timed out."
    }
    $exitCode = $process.ExitCode
} finally {
    if ($null -ne $process) {
        $process.Dispose()
    }
}

if ($exitCode -ne 0) {
    throw "WebView2 runtime smoke failed with exit code $exitCode."
}

if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
    throw "WebView2 runtime smoke did not produce its result file."
}

$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if (-not $result.Success -or
    -not $result.CoreCreated -or
    -not $result.DevToolsDisabled -or
    -not $result.PasswordAutosaveDisabled -or
    -not $result.AutofillDisabled -or
    [string]::IsNullOrWhiteSpace($result.RuntimeVersion)) {
    throw "WebView2 runtime smoke produced an invalid result."
}

Write-Host "WebView2 WPF host smoke passed with runtime $($result.RuntimeVersion)."
