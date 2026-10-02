[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Orbit Navigator"),
    [string]$CapturePath = "",
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OrbitUpdateSmokeNative
{
    public delegate bool Callback(IntPtr window, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static IntPtr FindVisibleWindow(uint processId, string exactTitle)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((window, state) => {
            uint candidate;
            GetWindowThreadProcessId(window, out candidate);
            if (candidate != processId || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString().Equals(exactTitle, StringComparison.Ordinal)) { match = window; return false; }
            return true;
        }, IntPtr.Zero);
        return match;
    }
}
"@

function Wait-OrbitCondition([scriptblock]$Condition, [string]$Failure) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 150
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw $Failure
}

function Find-NamedElement(
    [System.Windows.Automation.AutomationElement]$Root,
    [string]$Name,
    [System.Windows.Automation.ControlType]$ControlType = $null) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $condition = $nameCondition
    if ($ControlType) {
        $condition = New-Object System.Windows.Automation.AndCondition(
            $nameCondition,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)))
    }
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

$launcher = (Resolve-Path -LiteralPath (Join-Path $InstallRoot "Orbit Navigator.exe")).Path
$application = (Resolve-Path -LiteralPath (Join-Path $InstallRoot "app\OrbitNavigator.App.exe")).Path
$process = $null
try {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $launcher
    $start.WorkingDirectory = $InstallRoot
    $start.UseShellExecute = $false
    $launcherProcess = [Diagnostics.Process]::Start($start)
    if (-not $launcherProcess.WaitForExit(20000) -or $launcherProcess.ExitCode -ne 0) {
        throw "Installed launcher failed."
    }
    $processInfo = Wait-OrbitCondition {
        Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).Equals($application, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
    } "Installed Orbit application did not start."
    $process = Get-Process -Id $processInfo.ProcessId
    $mainHandle = Wait-OrbitCondition {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $process.MainWindowHandle }
    } "Installed Orbit main window did not become visible."
    $main = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)
    (Find-NamedElement $main "Open browser menu").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $settingsItem = Wait-OrbitCondition {
        Find-NamedElement ([System.Windows.Automation.AutomationElement]::RootElement) "Settings"
    } "Settings menu item was not exposed."
    $settingsItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $settingsTitle = "Settings " + [char]0x2014 + " Orbit Navigator"
    $settingsHandle = Wait-OrbitCondition {
        $candidate = [OrbitUpdateSmokeNative]::FindVisibleWindow([uint32]$process.Id, $settingsTitle)
        if ($candidate -ne [IntPtr]::Zero) { $candidate }
    } "Settings window did not become visible."
    $settings = [System.Windows.Automation.AutomationElement]::FromHandle($settingsHandle)
    $automatic = Find-NamedElement $settings `
        "Check for Orbit Navigator updates automatically" `
        ([System.Windows.Automation.ControlType]::CheckBox)
    if (-not $automatic -or $automatic.Current.IsEnabled) {
        throw "The automatic-update status was not exposed as an authoritative read-only setting."
    }
    $automaticToggle = $automatic.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($automaticToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw "Automatic update checks were not enabled."
    }
    if (Find-NamedElement $settings "Allow Beta Updates") {
        throw "The retired Beta channel is still visible."
    }
    $check = Wait-OrbitCondition {
        $candidate = Find-NamedElement $settings "Check for updates" ([System.Windows.Automation.ControlType]::Button)
        if ($candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "The manual update check action was not enabled."
    $download = Find-NamedElement $settings "Download verified update" ([System.Windows.Automation.ControlType]::Button)
    $install = Find-NamedElement $settings "Install verified update..." ([System.Windows.Automation.ControlType]::Button)
    if (-not $download -or -not $install) {
        throw "The verified update actions were not exposed."
    }
    if ($download.Current.IsEnabled -or $install.Current.IsEnabled) {
        throw "Download or install was enabled before an update was verified."
    }

    if ($CapturePath) {
        $capture = [IO.Path]::GetFullPath($CapturePath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $capture) -Force | Out-Null
        $rectangle = New-Object OrbitUpdateSmokeNative+RECT
        if (-not [OrbitUpdateSmokeNative]::GetWindowRect($settingsHandle, [ref]$rectangle)) {
            throw "Could not resolve Settings bounds."
        }
        $bitmap = New-Object Drawing.Bitmap(
            ($rectangle.Right - $rectangle.Left),
            ($rectangle.Bottom - $rectangle.Top))
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try { $graphics.CopyFromScreen($rectangle.Left, $rectangle.Top, 0, 0, $bitmap.Size) }
            finally { $graphics.Dispose() }
            $bitmap.Save($capture, [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bitmap.Dispose() }
    }

    $unexpectedInstaller = Get-Process -Name 'Setup','OrbitNavigator.UpdateRunner' -ErrorAction SilentlyContinue
    if ($unexpectedInstaller) { throw "An installer launched without per-package confirmation." }
    [pscustomobject]@{
        SinglePrimaryChannel = $true
        AutomaticChecksEnabled = $true
        ManualCheckAvailable = $check.Current.IsEnabled
        DownloadRequiresVerifiedManifest = -not $download.Current.IsEnabled
        InstallRequiresStagedPackage = -not $install.Current.IsEnabled
        SilentInstallerLaunched = $false
        CapturePath = if ($CapturePath) { [IO.Path]::GetFullPath($CapturePath) } else { $null }
    } | ConvertTo-Json -Depth 3
}
finally {
    if ($process -and -not $process.HasExited) {
        if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(20000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
