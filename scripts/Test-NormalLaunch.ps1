[CmdletBinding()]
param(
    [string]$LauncherPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Common.ps1")
$projectRoot = Get-OrbitProjectRoot
if (-not $LauncherPath) {
    $LauncherPath = Join-Path $projectRoot "Orbit Navigator.exe"
}

$launcher = (Resolve-Path -LiteralPath $LauncherPath).Path
$application = [System.IO.Path]::GetFullPath(
    (Join-Path (Split-Path -Parent $launcher) "app\OrbitNavigator.App.exe"))
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Published Orbit Navigator application not found: $application"
}

$existing = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq "OrbitNavigator.App.exe" -and
    $_.ExecutablePath -and
    [System.IO.Path]::GetFullPath($_.ExecutablePath) -eq $application
})
if ($existing.Count -ne 0) {
    throw "Normal-launch smoke requires no existing workspace App process."
}

$verificationRoot = Join-Path $projectRoot "artifacts\verification"
$resultPath = Join-Path $verificationRoot "normal-launch-smoke.json"
New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue

$previousResultPath = $env:ORBIT_NORMAL_LAUNCH_RESULT
$appProcess = $null
try {
    $env:ORBIT_NORMAL_LAUNCH_RESULT = $resultPath
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $launcher
    $startInfo.WorkingDirectory = Split-Path -Parent $launcher
    $startInfo.UseShellExecute = $false
    $launcherProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $launcherProcess -or -not $launcherProcess.WaitForExit(20000)) {
        throw "Root launcher did not exit within 20 seconds."
    }
    if ($launcherProcess.ExitCode -ne 0) {
        throw "Root launcher returned $($launcherProcess.ExitCode)."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $candidate = @(Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq "OrbitNavigator.App.exe" -and
            $_.ExecutablePath -and
            [System.IO.Path]::GetFullPath($_.ExecutablePath) -eq $application
        } | Select-Object -First 1)
        if ($candidate.Count -eq 1) {
            $appProcess = Get-Process -Id $candidate[0].ProcessId -ErrorAction SilentlyContinue
        }

        if ($null -ne $appProcess -and (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            $appProcess.Refresh()
            $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            if ($result.Success -and $result.WindowVisible -and
                $result.HostInitialized -and $result.StartupOverlayCleared -and
                $result.StartupSequenceFrameCount -eq 6 -and
                $result.StartupLoadingPhase -eq "Ready" -and
                $appProcess.MainWindowHandle -ne 0) {
                break
            }
        }

        Start-Sleep -Milliseconds 100
    }

    if ($null -eq $appProcess -or $appProcess.HasExited) {
        throw "Normal App process exited before presenting its window."
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Normal launch did not report initialized readiness within 30 seconds."
    }

    $appProcess.Refresh()
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $result.Success -or -not $result.WindowVisible -or
        -not $result.HostInitialized -or -not $result.StartupOverlayCleared -or
        $result.StartupSequenceFrameCount -ne 6 -or
        $result.StartupLoadingPhase -ne "Ready" -or
        $appProcess.MainWindowHandle -eq 0) {
        throw "Normal launch remained headless or failed host initialization."
    }

    if (-not $appProcess.CloseMainWindow()) {
        throw "Normal launch window could not be closed gracefully."
    }
    if (-not $appProcess.WaitForExit(20000)) {
        throw "Normal App did not exit within 20 seconds after its window closed."
    }

    Write-Host "Normal root launch smoke passed with a visible FoundationWindow, initialized host, and cleared six-frame startup overlay."
}
finally {
    $env:ORBIT_NORMAL_LAUNCH_RESULT = $previousResultPath
    if ($null -ne $appProcess -and -not $appProcess.HasExited) {
        $candidate = Get-CimInstance Win32_Process -Filter "ProcessId = $($appProcess.Id)"
        if ($null -ne $candidate -and $candidate.ExecutablePath -and
            [System.IO.Path]::GetFullPath($candidate.ExecutablePath) -eq $application) {
            Stop-Process -Id $appProcess.Id -Force
        }
    }
}
