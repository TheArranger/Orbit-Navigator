[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Orbit Navigator"),
    [string]$CapturePath = "",
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OrbitAccountSmokeNative
{
    public delegate bool Callback(IntPtr window, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
    public static IntPtr FindVisibleWindow(uint processId, string exactTitle)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((window, state) =>
        {
            uint candidate;
            GetWindowThreadProcessId(window, out candidate);
            if (candidate != processId || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString().Equals(exactTitle, StringComparison.Ordinal))
            {
                match = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return match;
    }
}
"@

function Wait-OrbitCondition {
    param([scriptblock]$Condition, [string]$Failure)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 150
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw $Failure
}

function Find-NamedElement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType = $null
    )
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $condition = $nameCondition
    if ($ControlType) {
        $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
        $condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
    }
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

$launcher = (Resolve-Path -LiteralPath (Join-Path $InstallRoot "Orbit Navigator.exe")).Path
$application = (Resolve-Path -LiteralPath (Join-Path $InstallRoot "app\OrbitNavigator.App.exe")).Path
$process = $null
try {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $launcher
    $startInfo.WorkingDirectory = [IO.Path]::GetFullPath($InstallRoot)
    $startInfo.UseShellExecute = $false
    $launcherProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $launcherProcess -or -not $launcherProcess.WaitForExit(20000)) {
        throw "Installed Orbit launcher did not exit within 20 seconds."
    }
    if ($launcherProcess.ExitCode -ne 0) {
        throw "Installed Orbit launcher returned a nonzero exit code."
    }
    Write-Host "Account smoke: launcher started."
    $processInfo = Wait-OrbitCondition {
        Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq "OrbitNavigator.App.exe" -and $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).Equals($application, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
    } "Installed Orbit application process did not start."
    Write-Host "Account smoke: application process found."
    $process = Get-Process -Id $processInfo.ProcessId
    $mainHandle = Wait-OrbitCondition {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $process.MainWindowHandle } else { $null }
    } "Installed Orbit main window did not become visible."
    Write-Host "Account smoke: main window visible."
    $main = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)
    $menu = Wait-OrbitCondition {
        $currentMain = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)
        Find-NamedElement $currentMain "Open browser menu"
    } "Open browser menu button was not exposed through UI Automation after the main window became ready."
    $menu.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "Account smoke: browser menu opened."
    $settingsItem = Wait-OrbitCondition {
        Find-NamedElement ([System.Windows.Automation.AutomationElement]::RootElement) "Settings"
    } "Settings menu item was not exposed through UI Automation."
    $settingsItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "Account smoke: Settings invoked."
    $settingsTitle = "Settings " + [char]0x2014 + " Orbit Navigator"
    $settingsHandle = Wait-OrbitCondition {
        $candidate = [OrbitAccountSmokeNative]::FindVisibleWindow([uint32]$process.Id, $settingsTitle)
        if ($candidate -ne [IntPtr]::Zero) { $candidate } else { $null }
    } "Settings window did not become visible."
    Write-Host "Account smoke: Settings window visible."
    $settings = [System.Windows.Automation.AutomationElement]::FromHandle($settingsHandle)
    if ($CapturePath) {
        $capture = [IO.Path]::GetFullPath($CapturePath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $capture) -Force | Out-Null
        $rectangle = New-Object OrbitAccountSmokeNative+RECT
        if (-not [OrbitAccountSmokeNative]::GetWindowRect($settingsHandle, [ref]$rectangle)) {
            throw "Could not resolve the Settings window bounds."
        }
        $width = $rectangle.Right - $rectangle.Left
        $height = $rectangle.Bottom - $rectangle.Top
        $bitmap = New-Object Drawing.Bitmap($width, $height)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try { $graphics.CopyFromScreen($rectangle.Left, $rectangle.Top, 0, 0, $bitmap.Size) }
            finally { $graphics.Dispose() }
            $bitmap.Save($capture, [Drawing.Imaging.ImageFormat]::Png)
        } finally { $bitmap.Dispose() }
    }
    $link = Wait-OrbitCondition {
        Find-NamedElement $settings "Link My Orbit account"
    } "Link My Orbit account was not exposed through UI Automation."
    Write-Host "Account smoke: My Orbit account controls found."
    $status = Find-NamedElement $settings "My Orbit account status"
    $linkOnlyCopy = Find-NamedElement $settings "This connects Orbit Navigator to My Orbit for account and device management only. It does not enable tab, history, settings, or browsing-data sync."
    $externalBrowserCopy = Find-NamedElement $settings "Linking opens your default system browser. Orbit Navigator never asks for or displays your My Orbit password, authorization code, or security credentials."
    $localBrowsingCopy = Find-NamedElement $settings "Local browsing remains available whether or not you link an account."
    if (-not $link -or -not $status -or -not $linkOnlyCopy -or -not $externalBrowserCopy -or -not $localBrowsingCopy) {
        throw "My Orbit account Settings controls or security disclosure are incomplete."
    }
    $edits = $settings.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)))
    if ($edits.Count -ne 0) {
        throw "My Orbit account Settings unexpectedly exposes a credential-entry control."
    }
    $syncToggle = Find-NamedElement $settings "Enable My Orbit sync"
    if ($syncToggle) {
        throw "My Orbit account Settings unexpectedly exposes browsing-data sync."
    }
    $statusText = ""
    try {
        $textPattern = $status.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $statusText = $textPattern.DocumentRange.GetText(-1).Trim()
    } catch {
        $statusText = $status.Current.Name
    }
    $link.SetFocus()
    [OrbitAccountSmokeNative]::SetForegroundWindow($settingsHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    [pscustomobject]@{
        AccountSurfaceVisible = -not $link.Current.IsOffscreen
        LinkButtonVisible = -not $link.Current.IsOffscreen
        LinkButtonEnabled = $link.Current.IsEnabled
        CredentialEditCount = $edits.Count
        BrowsingSyncTogglePresent = [bool]$syncToggle
        LinkOnlyDisclosurePresent = [bool]$linkOnlyCopy
        ExternalBrowserDisclosurePresent = [bool]$externalBrowserCopy
        LocalBrowsingDisclosurePresent = [bool]$localBrowsingCopy
        StatusText = $statusText
        CapturePath = if ($CapturePath) { [IO.Path]::GetFullPath($CapturePath) } else { $null }
    } | ConvertTo-Json -Depth 3
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.Refresh()
        if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(20000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(10000) | Out-Null
        }
    }
}
