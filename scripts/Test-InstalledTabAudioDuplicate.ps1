[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Orbit Navigator"),
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitAudioDuplicateNative
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
"@

function Wait-OrbitCondition {
    param([scriptblock]$Condition, [string]$Failure)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try { if (& $Condition) { return } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 125
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Find-OrbitElement {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name, [bool]$Required = $true)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $item = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($Required -and $null -eq $item) { throw "UI Automation element not found: $Name" }
    $item
}

function Find-OrbitElementLike {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -like $Name } |
        Select-Object -First 1
}

function Get-OrbitTabCount {
    param([System.Windows.Automation.AutomationElement]$Root)
    @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $_.Current.Name -like "*, tab"
        }).Count
}

function Get-OrbitMainRoot {
    param([int]$ProcessId)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

$launcher = Join-Path $InstallRoot "Orbit Navigator.exe"
$application = Join-Path $InstallRoot "app\OrbitNavigator.App.exe"
if (-not (Test-Path $launcher) -or -not (Test-Path $application)) { throw "Installed payload is incomplete." }
if (Get-Process -Name "OrbitNavigator.App" -ErrorAction SilentlyContinue) { throw "Orbit must be closed." }

$port = Get-Random -Minimum 51000 -Maximum 51999
$python = @'
import http.server, socketserver
page = b'''<!doctype html><html><head><meta charset="utf-8"><title>Audio Smoke</title></head>
<body style="font:24px Segoe UI;background:#0b1117;color:white">
<button id="audio" onclick="window.ctx=new AudioContext();window.osc=ctx.createOscillator();window.gain=ctx.createGain();gain.gain.value=.03;osc.connect(gain).connect(ctx.destination);osc.start();this.textContent='Audio running'">Start test audio</button>
</body></html>'''
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.send_header('Content-Type','text/html; charset=utf-8'); self.send_header('Cache-Control','no-store'); self.send_header('Content-Length',str(len(page))); self.end_headers(); self.wfile.write(page)
    def log_message(self,*args): pass
socketserver.TCPServer.allow_reuse_address=True
with socketserver.TCPServer(('127.0.0.1',__PORT__),Handler) as server: server.serve_forever()
'@.Replace('__PORT__', $port)
$serverInfo = [Diagnostics.ProcessStartInfo]::new()
$serverInfo.FileName = (Get-Command python -ErrorAction Stop).Source
$serverInfo.UseShellExecute = $false
$serverInfo.CreateNoWindow = $true
$serverInfo.ArgumentList.Add('-c'); $serverInfo.ArgumentList.Add($python)
$server = [Diagnostics.Process]::Start($serverInfo)
$process = $null
try {
    Start-Sleep -Milliseconds 400
    Invoke-WebRequest "http://127.0.0.1:$port/" -UseBasicParsing | Out-Null
    Start-Process $launcher | Out-Null
    Wait-OrbitCondition {
        $script:process = Get-Process -Name 'OrbitNavigator.App' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $application } | Select-Object -First 1
        $script:root = if ($script:process) { Get-OrbitMainRoot $script:process.Id } else { $null }
        $null -ne $script:root
    } "Installed Orbit did not open."
    $process = $script:process
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $process.Id
        $script:address = Find-OrbitElement $root 'Address and search' $false
        $null -ne $script:address
    } "Installed browser chrome did not become ready."
    $dock = Find-OrbitElement $root 'Dock tab controller' $false
    if ($null -ne $dock -and -not $dock.Current.IsOffscreen) {
        $dock.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-OrbitCondition {
            $script:root = Get-OrbitMainRoot $process.Id
            $script:address = Find-OrbitElement $root 'Address and search' $false
            $null -ne $script:address -and $null -eq (Find-OrbitElement $root 'Dock tab controller' $false)
        } "The restored tab controller did not dock before audio validation."
    }
    [void][OrbitAudioDuplicateNative]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle)
    $script:address.SetFocus()
    $script:address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("http://127.0.0.1:$port/")
    [OrbitAudioDuplicateNative]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero)
    [OrbitAudioDuplicateNative]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $process.Id
        $null -ne (Find-OrbitElement $root 'Audio Smoke, tab' $false)
    } "Audio test page did not reach the authoritative tab title."
    Wait-OrbitCondition {
        $script:startAudio = Find-OrbitElement $root 'Start test audio' $false
        $null -ne $script:startAudio
    } "The WebView audio activation control was not accessible."
    $script:startAudio.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $process.Id
        $script:mute = Find-OrbitElement $root 'Mute tab — Audio Smoke' $false
        $null -ne $script:mute
    } "Authoritative playing-audio state did not expose the mute action."
    $script:mute.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $process.Id
        $script:unmute = Find-OrbitElement $root 'Unmute tab — Audio Smoke' $false
        $null -ne $script:unmute
    } "The identified tab did not report muted state."
    $script:unmute.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $process.Id
        $null -ne (Find-OrbitElement $root 'Mute tab — Audio Smoke' $false)
    } "The identified tab did not report unmuted state."

    $tab = Find-OrbitElement $root 'Audio Smoke, tab'
    $bounds = $tab.Current.BoundingRectangle
    [void][OrbitAudioDuplicateNative]::SetCursorPos([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
    [OrbitAudioDuplicateNative]::mouse_event(0x0008,0,0,0,[UIntPtr]::Zero)
    [OrbitAudioDuplicateNative]::mouse_event(0x0010,0,0,0,[UIntPtr]::Zero)
    Wait-OrbitCondition {
        $script:duplicate = Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Duplicate tab' $false
        $null -ne $script:duplicate -and $script:duplicate.Current.IsEnabled
    } "Duplicate tab was not exposed by the installed tab context menu."
    $before = Get-OrbitTabCount $root
    $script:duplicate.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $process.Id
        (Get-OrbitTabCount $root) -eq ($before + 1)
    } "Duplicate did not add exactly one authoritative tab."
    [pscustomobject]@{
        AudioActivationObserved = $true
        MuteObserved = $true
        UnmuteObserved = $true
        DuplicateAddedExactlyOneTab = $true
        TabCountBefore = $before
        TabCountAfter = Get-OrbitTabCount $root
    } | ConvertTo-Json -Compress
}
finally {
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
    }
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
