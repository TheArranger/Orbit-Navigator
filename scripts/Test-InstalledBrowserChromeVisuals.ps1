[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Orbit Navigator"),
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [string]$CapturePath = "",
    [string]$ProgressPath = "",
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$acceptanceRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptanceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The visual-smoke profile must be inside the disposable acceptance root.'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitInstalledChromeNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
"@

function Write-OrbitStage {
    param([string]$Stage)
    $entry = "{0:o}|{1}" -f [DateTimeOffset]::Now, $Stage
    Write-Verbose $entry
    if (-not [string]::IsNullOrWhiteSpace($ProgressPath)) {
        $fullPath = [IO.Path]::GetFullPath($ProgressPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
        Add-Content -LiteralPath $fullPath -Value $entry -Encoding utf8
    }
}

function Wait-OrbitCondition {
    param([scriptblock]$Condition, [string]$Failure, [int]$Seconds = $TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { if (& $Condition) { return } } catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Find-OrbitElement {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-OrbitElementLike {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Pattern)
    $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -like $Pattern } |
        Select-Object -First 1
}

function Find-OrbitButtonLike {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Pattern)
    $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $_.Current.Name -like $Pattern
        } |
        Select-Object -First 1
}

function Get-OrbitMainRoot {
    param([int]$ProcessId)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { $_.Current.Name -eq "Orbit Navigator" -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

function Capture-OrbitWindow {
    param([IntPtr]$Handle, [string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $rect = [OrbitInstalledChromeNative+RECT]::new()
    if (-not [OrbitInstalledChromeNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not read the installed Orbit window bounds."
    }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $fullPath = [IO.Path]::GetFullPath($Path)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
        $bitmap.Save($fullPath, [Drawing.Imaging.ImageFormat]::Png)
        return $fullPath
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$applicationPath = Join-Path $InstallRoot "app\OrbitNavigator.App.exe"
$launcherPath = Join-Path $InstallRoot "Orbit Navigator.exe"
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "The installed Orbit Navigator payload is incomplete at $InstallRoot."
}
if (Get-Process -Name "OrbitNavigator.App" -ErrorAction SilentlyContinue) {
    throw "Installed Browser Chrome smoke requires Orbit Navigator to be closed."
}

$faviconBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes(
    (Join-Path $PSScriptRoot "..\assets\branding\windows\orbit-navigator-32.png")))
$faviconToken = [Guid]::NewGuid().ToString("N")
$pythonSource = @'
import base64, http.server, socketserver
favicon = base64.b64decode('__FAVICON__')
favicon_seen = False
page = b'<!doctype html><html><head><meta charset="utf-8"><title>Orbit Chrome Smoke</title><link rel="icon" type="image/png" href="/favicon.png?__FAVICON_TOKEN__"></head><body style="font:24px Segoe UI;background:#0b1117;color:white">Orbit Chrome Smoke</body></html>'
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        global favicon_seen
        print(self.path, flush=True)
        if self.path.startswith('/favicon-status'):
            body = b'1' if favicon_seen else b'0'
            content_type = 'text/plain'
        elif self.path.startswith('/favicon.png'):
            favicon_seen = True
            body = favicon
            content_type = 'image/png'
        else:
            body = page
            content_type = 'text/html; charset=utf-8'
        self.send_response(200)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *args):
        pass
socketserver.TCPServer.allow_reuse_address = True
with socketserver.TCPServer(('127.0.0.1', 48672), Handler) as server:
    server.serve_forever()
'@
$pythonSource = $pythonSource.Replace("__FAVICON__", $faviconBase64)
$pythonSource = $pythonSource.Replace("__FAVICON_TOKEN__", $faviconToken)

$serverInfo = [Diagnostics.ProcessStartInfo]::new()
$serverInfo.FileName = (Get-Command python -ErrorAction Stop).Source
$serverInfo.UseShellExecute = $false
$serverInfo.CreateNoWindow = $true
$serverInfo.RedirectStandardOutput = $true
$serverInfo.RedirectStandardError = $true
$serverInfo.ArgumentList.Add("-c")
$serverInfo.ArgumentList.Add($pythonSource)
$server = [Diagnostics.Process]::Start($serverInfo)
$appProcess = $null
$requestLog = ""
try {
    Write-OrbitStage "server-started"
    Start-Sleep -Milliseconds 500
    if ($server.HasExited) {
        throw "The loopback-only visual test server failed: $($server.StandardError.ReadToEnd())"
    }
    Invoke-WebRequest -Uri "http://127.0.0.1:48672/health" -UseBasicParsing | Out-Null
    Write-OrbitStage "server-ready"
    $runId = [Guid]::NewGuid()
    Start-Process -FilePath $launcherPath -ArgumentList @(
        '--acceptance-profile-root', $profile,
        '--acceptance-run-id', $runId.ToString('D')) | Out-Null
    Wait-OrbitCondition {
        $candidate = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq "OrbitNavigator.App.exe" -and $_.ExecutablePath -eq $applicationPath
        } | Select-Object -First 1
        if ($candidate) { $script:installedProcessId = $candidate.ProcessId; return $true }
        return $false
    } "The installed Orbit Navigator process did not start."
    Write-OrbitStage "app-process-found"
    $appProcess = Get-Process -Id $script:installedProcessId
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $appProcess.Id
        $null -ne $script:root
    } `
        "The installed Orbit Navigator window did not appear."
    Write-OrbitStage "window-visible"
    $root = $script:root
    $windowHandle = [IntPtr]$root.Current.NativeWindowHandle
    [void][OrbitInstalledChromeNative]::SetForegroundWindow($windowHandle)
    Wait-OrbitCondition {
        $startup = Find-OrbitElement $root "Orbit Navigator startup"
        $null -eq $startup -or $startup.Current.IsOffscreen
    } "The startup overlay did not clear."
    Write-OrbitStage "startup-cleared"

    # WPF replaces the startup visual tree when the browser chrome is mounted.
    # Reacquire the HWND root so UIA does not keep querying the detached startup peer.
    $root = Get-OrbitMainRoot $appProcess.Id

    Wait-OrbitCondition {
        $script:omnibox = Find-OrbitElement $root "Address and search"
        $null -ne $script:omnibox
    } "The installed omnibox was not exposed to UI Automation."
    Write-OrbitStage "omnibox-found"
    $omnibox = $script:omnibox
    $valuePattern = $omnibox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue("http://127.0.0.1:48672/")
    $omnibox.SetFocus()
    [OrbitInstalledChromeNative]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero)
    [OrbitInstalledChromeNative]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)

    Wait-OrbitCondition {
        $script:titleElement = Find-OrbitButtonLike $root "Orbit Chrome Smoke*tab*"
        $null -ne $script:titleElement
    } "The page title did not reach the installed tab projection."
    if ($script:titleElement.Current.HelpText -notlike "*127.0.0.1*") {
        throw "The installed tab accessibility metadata did not retain the site label."
    }
    Wait-OrbitCondition {
        (Invoke-WebRequest -Uri "http://127.0.0.1:48672/favicon-status" -UseBasicParsing).Content.Trim() -eq "1"
    } "The installed page did not request its declared favicon." 15
    Write-OrbitStage "title-projected"
    $alreadyCompact = Find-OrbitElement $root "Show tab titles"
    if ($null -ne $alreadyCompact) {
        $alreadyCompact.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-OrbitCondition { $null -ne (Find-OrbitElement $root "Use compact favicon-only tabs") } `
            "The initial titled-tab layout did not restore."
        Wait-OrbitCondition {
            $script:titleElement = Find-OrbitButtonLike $root "Orbit Chrome Smoke*tab*"
            $null -ne $script:titleElement
        } "The titled tab did not restore after compact mode."
    }
    Start-Sleep -Seconds 1
    $normalTabWidth = $script:titleElement.Current.BoundingRectangle.Width
    $screenshot = Capture-OrbitWindow $windowHandle $CapturePath
    Write-OrbitStage "normal-captured"

    $compact = Find-OrbitElement $root "Use compact favicon-only tabs"
    if ($null -eq $compact) { throw "The accessible compact-tab control is missing." }
    $compact.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:showTitles = Find-OrbitElement $root "Show tab titles"
        $null -ne $script:showTitles -and $script:showTitles.Current.ItemStatus -eq "Compact tabs on"
    } "Compact mode was not authoritatively accepted."
    $compactControlBounds = $script:showTitles.Current.BoundingRectangle
    $compactTab = Find-OrbitButtonLike $root "Orbit Chrome Smoke*tab*"
    if ($null -eq $compactTab) { throw "The selected tab disappeared in compact mode." }
    $compactTabWidth = $compactTab.Current.BoundingRectangle.Width
    if ($compactControlBounds.Width -le 0 -or $compactControlBounds.Height -le 0 -or
        $script:showTitles.Current.IsOffscreen -or -not $script:showTitles.Current.IsEnabled) {
        throw "The compact-mode control is not visibly accessible."
    }
    $script:showTitles.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition { $null -ne (Find-OrbitElement $root "Use compact favicon-only tabs") } `
        "The titled tab layout did not restore."
    Write-OrbitStage "compact-restored"

    [pscustomobject]@{
        TabAutomationName = $script:titleElement.Current.Name
        NormalTabWidth = $normalTabWidth
        CompactTabWidth = $compactTabWidth
        CompactControlWidth = $compactControlBounds.Width
        CompactControlHeight = $compactControlBounds.Height
        CompactRestored = $true
        Screenshot = $screenshot
    } | ConvertTo-Json -Compress
}
finally {
    Write-OrbitStage "cleanup-started"
    if ($appProcess -and -not $appProcess.HasExited) {
        [void]$appProcess.CloseMainWindow()
        if (-not $appProcess.WaitForExit(5000)) { Stop-Process -Id $appProcess.Id -Force }
    }
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        [void]$server.WaitForExit(5000)
    }
    if ($server) {
        $requestLog = $server.StandardOutput.ReadToEnd().Trim() -replace "`r?`n", ","
        Write-Output "SERVER_REQUESTS=$requestLog"
        Write-Output "FAVICON_REQUESTED=$($requestLog -match '/favicon.png')"
    }
    Write-OrbitStage "cleanup-complete"
}
