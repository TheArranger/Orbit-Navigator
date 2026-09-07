[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$LauncherPath,
    [Parameter(Mandatory)]
    [string]$ApplicationPath,
    [Parameter(Mandatory)]
    [string]$CapturePath
)

$ErrorActionPreference = "Stop"
$launcher = (Resolve-Path -LiteralPath $LauncherPath).Path
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$capture = [System.IO.Path]::GetFullPath($CapturePath)
$result = [System.IO.Path]::ChangeExtension($capture, ".json")
New-Item -ItemType Directory -Path (Split-Path -Parent $capture) -Force | Out-Null

$existing = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq "OrbitNavigator.App.exe" -and
    $_.ExecutablePath -and
    [System.IO.Path]::GetFullPath($_.ExecutablePath) -eq $application
})
if ($existing.Count -ne 0) {
    throw "Capture requires no existing installed App process."
}

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class OrbitCaptureNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint="GetClassLongPtrW")]
    public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);
    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder appId);
}
"@
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$previousResult = $env:ORBIT_NORMAL_LAUNCH_RESULT
$appProcess = $null
try {
    $env:ORBIT_NORMAL_LAUNCH_RESULT = $result
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $launcher
    $startInfo.WorkingDirectory = Split-Path -Parent $launcher
    $startInfo.UseShellExecute = $false
    $launcherProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $launcherProcess -or -not $launcherProcess.WaitForExit(20000)) {
        throw "Launcher did not exit within 20 seconds."
    }
    if ($launcherProcess.ExitCode -ne 0) {
        throw "Launcher returned $($launcherProcess.ExitCode)."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $candidate = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq "OrbitNavigator.App.exe" -and
            $_.ExecutablePath -and
            [System.IO.Path]::GetFullPath($_.ExecutablePath) -eq $application
        } | Select-Object -First 1
        if ($candidate) {
            $appProcess = Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
        }
        if ($appProcess -and $appProcess.MainWindowHandle -ne 0 -and
            (Test-Path -LiteralPath $result -PathType Leaf)) {
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $appProcess -or $appProcess.MainWindowHandle -eq 0) {
        throw "Installed App did not present a visible window."
    }

    Start-Sleep -Seconds 2
    [OrbitCaptureNative]::SetForegroundWindow($appProcess.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 400
    $rect = [OrbitCaptureNative+RECT]::new()
    if (-not [OrbitCaptureNative]::GetWindowRect($appProcess.MainWindowHandle, [ref]$rect)) {
        throw "Could not resolve the installed window bounds."
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($capture, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
    $desktopPath = [System.IO.Path]::ChangeExtension($capture, ".desktop.png")
    $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $desktop = [System.Drawing.Bitmap]::new($screen.Width, $screen.Height)
    $desktopGraphics = [System.Drawing.Graphics]::FromImage($desktop)
    try {
        $desktopGraphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $desktop.Size)
        $desktop.Save($desktopPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $desktopGraphics.Dispose()
        $desktop.Dispose()
    }

    $bigIcon = [OrbitCaptureNative]::SendMessage(
        $appProcess.MainWindowHandle, 0x007F, [IntPtr]1, [IntPtr]0)
    $smallIcon = [OrbitCaptureNative]::SendMessage(
        $appProcess.MainWindowHandle, 0x007F, [IntPtr]0, [IntPtr]0)
    $classIcon = [OrbitCaptureNative]::GetClassLongPtr($appProcess.MainWindowHandle, -14)
    $classSmallIcon = [OrbitCaptureNative]::GetClassLongPtr($appProcess.MainWindowHandle, -34)
    $processHandle = [OrbitCaptureNative]::OpenProcess(0x1000, $false, [uint32]$appProcess.Id)
    $appUserModelId = $null
    $appUserModelIdResult = $null
    if ($processHandle -ne [IntPtr]::Zero) {
        try {
            [uint32]$length = 256
            $builder = [System.Text.StringBuilder]::new([int]$length)
            $appUserModelIdResult = [OrbitCaptureNative]::GetApplicationUserModelId(
                $processHandle, [ref]$length, $builder)
            if ($appUserModelIdResult -eq 0) {
                $appUserModelId = $builder.ToString()
            }
        }
        finally {
            [OrbitCaptureNative]::CloseHandle($processHandle) | Out-Null
        }
    }
    $identityPath = [System.IO.Path]::ChangeExtension($capture, ".identity.json")
    [pscustomobject]@{
        ProcessId = $appProcess.Id
        AppUserModelId = $appUserModelId
        AppUserModelIdResult = $appUserModelIdResult
        WindowBigIconPresent = $bigIcon -ne [IntPtr]::Zero
        WindowSmallIconPresent = $smallIcon -ne [IntPtr]::Zero
        WindowClassIconPresent = $classIcon -ne [IntPtr]::Zero
        WindowClassSmallIconPresent = $classSmallIcon -ne [IntPtr]::Zero
        DesktopCapture = $desktopPath
    } | ConvertTo-Json | Set-Content -LiteralPath $identityPath -Encoding utf8
    Write-Host "Captured installed addressless New Tab: $capture ($width x $height)."
}
finally {
    $env:ORBIT_NORMAL_LAUNCH_RESULT = $previousResult
    if ($appProcess -and -not $appProcess.HasExited) {
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(20000)) {
            Stop-Process -Id $appProcess.Id -Force
        }
    }
}
