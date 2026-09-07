[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Orbit Navigator"),
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [ValidateSet('Keep blocked','Allow once','Allow for this session','Always allow')]
    [string]$ChoiceName = 'Keep blocked',
    [switch]$ExpectExistingPersistentAllow,
    [string]$DiagnosticCapturePath = '',
    [int]$PermissionPort = 0,
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$acceptanceRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptanceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The permission profile must be inside the disposable acceptance root.'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitPermissionNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
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

function Get-OrbitMainRoot {
    param([int]$ProcessId)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

$launcher = Join-Path $InstallRoot 'Orbit Navigator.exe'
$application = Join-Path $InstallRoot 'app\OrbitNavigator.App.exe'
if (Get-Process -Name OrbitNavigator.App -ErrorAction SilentlyContinue) { throw 'Orbit must be closed.' }
$port = if($PermissionPort -gt 0){$PermissionPort}else{Get-Random -Minimum 52000 -Maximum 52999}
if($port -lt 1024 -or $port -gt 65535){throw 'PermissionPort is outside the allowed test range.'}
$python = @'
import http.server, socketserver
page=b'''<!doctype html><html><head><meta charset="utf-8"><title>Permission Smoke</title></head>
<body style="font:24px Segoe UI;background:#0b1117;color:white"><button onclick="navigator.geolocation.getCurrentPosition(()=>this.textContent='allowed',()=>this.textContent='blocked')">Request location permission</button></body></html>'''
class Handler(http.server.BaseHTTPRequestHandler):
  def do_GET(self):
    self.send_response(200);self.send_header('Content-Type','text/html; charset=utf-8');self.send_header('Cache-Control','no-store');self.send_header('Content-Length',str(len(page)));self.end_headers();self.wfile.write(page)
  def log_message(self,*args): pass
socketserver.TCPServer.allow_reuse_address=True
with socketserver.TCPServer(('127.0.0.1',__PORT__),Handler) as server:server.serve_forever()
'@.Replace('__PORT__',$port)
$serverInfo=[Diagnostics.ProcessStartInfo]::new();$serverInfo.FileName=(Get-Command python -ErrorAction Stop).Source;$serverInfo.UseShellExecute=$false;$serverInfo.CreateNoWindow=$true;$serverInfo.ArgumentList.Add('-c');$serverInfo.ArgumentList.Add($python)
$server=[Diagnostics.Process]::Start($serverInfo)
$process=$null
try {
    Start-Sleep -Milliseconds 400
    Invoke-WebRequest "http://127.0.0.1:$port/" -UseBasicParsing|Out-Null
    $runId=[Guid]::NewGuid()
    Start-Process $launcher -ArgumentList @(
        '--acceptance-profile-root',$profile,
        '--acceptance-run-id',$runId.ToString('D'))|Out-Null
    Wait-OrbitCondition {
        $script:process=Get-Process -Name OrbitNavigator.App -ErrorAction SilentlyContinue|Where-Object{$_.Path -eq $application}|Select-Object -First 1
        $script:root=if($script:process){Get-OrbitMainRoot $script:process.Id}else{$null}
        $null -ne $script:root -and $null -ne (Find-OrbitElement $root 'Address and search' $false)
    } 'Installed Orbit did not become ready.'
    $process=$script:process
    $dock=Find-OrbitElement $root 'Dock tab controller' $false
    if($dock -and -not $dock.Current.IsOffscreen){
        $dock.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-OrbitCondition {$script:root=Get-OrbitMainRoot $process.Id;$null -eq (Find-OrbitElement $root 'Dock tab controller' $false)} 'Detached controller did not dock.'
    }
    Wait-OrbitCondition {
        $script:root=Get-OrbitMainRoot $process.Id
        $script:address=Find-OrbitElement $root 'Address and search' $false
        $null -ne $script:address
    } 'The omnibox was not available before permission navigation.'
    [void][OrbitPermissionNative]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle)
    $address=$script:address;$address.SetFocus();$address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("http://127.0.0.1:$port/")
    [OrbitPermissionNative]::keybd_event(0x0D,0,0,[UIntPtr]::Zero);[OrbitPermissionNative]::keybd_event(0x0D,0,2,[UIntPtr]::Zero)
    Wait-OrbitCondition {$script:root=Get-OrbitMainRoot $process.Id;$null -ne (Find-OrbitElement $root 'Request location permission' $false)} 'Permission test page did not render.'
    (Find-OrbitElement $root 'Request location permission').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if($ExpectExistingPersistentAllow){
        Wait-OrbitCondition {
            $script:root=Get-OrbitMainRoot $process.Id
            $null -ne (Find-OrbitElement $root 'allowed' $false)
        } 'Hydrated persistent Orbit rule did not allow the permission request.'
        foreach($name in @('Keep blocked','Allow once','Allow for this session','Always allow')){
            if($null -ne (Find-OrbitElement $root $name $false)){throw 'Hydrated persistent rule incorrectly emitted a fresh actionable prompt.'}
        }
        [pscustomobject]@{ExistingPersistentRuleApplied=$true;PageOutcome='allowed';FreshPromptShown=$false;WebViewProfilePersistence=$false}|ConvertTo-Json -Compress
        return
    }
    Wait-OrbitCondition {
        $script:root=Get-OrbitMainRoot $process.Id
        $script:choice=Find-OrbitElement $root $ChoiceName $false
        $null -ne $script:choice -and $script:choice.Current.IsEnabled
    } "PermissionPromptPresenter did not expose choice: $ChoiceName."
    $bounds=$script:choice.Current.BoundingRectangle
    if($bounds.Width -lt 44 -or $bounds.Height -lt 44){throw 'Permission choice target is smaller than 44 DIPs.'}
    $expectedHelp = switch($ChoiceName) {
        'Keep blocked' {'Safest choice. The site remains blocked.'}
        'Allow once' {'Allows this use once. The site can ask again later.'}
        'Allow for this session' {'Allows this use for the current browsing session.'}
        'Always allow' {'Allows this use on future visits until you change the site permission.'}
    }
    if($script:choice.Current.HelpText -ne $expectedHelp){throw "Permission choice help copy is not the safe allowlisted text for $ChoiceName."}
    $cached=$script:choice
    $cached.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $immediateInert=$false
    $inertDeadline=[DateTime]::UtcNow.AddMilliseconds(750)
    do {
        try {$immediateInert=-not $cached.Current.IsEnabled} catch [System.Windows.Automation.ElementNotAvailableException] {$immediateInert=$true}
        if(-not $immediateInert){Start-Sleep -Milliseconds 25}
    } while(-not $immediateInert -and [DateTime]::UtcNow -lt $inertDeadline)
    if(-not $immediateInert){throw 'Permission choices did not become inert within 750 ms of submission.'}
    try {
        Wait-OrbitCondition {
            $script:root=Get-OrbitMainRoot $process.Id
            $null -eq (Find-OrbitElement $root 'Keep blocked' $false) -and $null -eq (Find-OrbitElement $root 'Permission choice is being applied' $false)
        } 'Handled permission prompt did not close terminally.'
    } catch {
        $root=Get-OrbitMainRoot $process.Id
        $state=@($root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|
            Where-Object {$_.Current.Name -in @('Keep blocked','Allow once','Allow for this session','Always allow','Permission choice is being applied')}|
            ForEach-Object {[pscustomobject]@{Name=$_.Current.Name;Enabled=$_.Current.IsEnabled;Offscreen=$_.Current.IsOffscreen;Status=$_.Current.ItemStatus}})
        if($DiagnosticCapturePath){
            $rect=[OrbitPermissionNative+RECT]::new()
            if([OrbitPermissionNative]::GetWindowRect([IntPtr]$root.Current.NativeWindowHandle,[ref]$rect)){
                $bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
                try{$graphics=[Drawing.Graphics]::FromImage($bitmap);try{$graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)}finally{$graphics.Dispose()};$full=[IO.Path]::GetFullPath($DiagnosticCapturePath);[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))|Out-Null;$bitmap.Save($full)}finally{$bitmap.Dispose()}
            }
        }
        throw "Handled permission prompt did not close terminally. PostClickState=$($state|ConvertTo-Json -Compress)"
    }
    $expectedPageOutcome=if($ChoiceName -eq 'Keep blocked'){'blocked'}else{'allowed'}
    Wait-OrbitCondition {
        $script:root=Get-OrbitMainRoot $process.Id
        $null -ne (Find-OrbitElement $root $expectedPageOutcome $false)
    } "Permission page did not resolve to $expectedPageOutcome after $ChoiceName."
    $replayRejected=$false
    try {$cached.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()} catch {$replayRejected=$true}
    if(-not $replayRejected -and $null -ne (Find-OrbitElement $root 'Keep blocked' $false)){throw 'Cached permission action remained replayable.'}
    [pscustomobject]@{Choice=$ChoiceName;PromptShown=$true;Target44Dip=$true;ImmediateInert=$immediateInert;TerminalClose=$true;ReplayRejectedOrNoPrompt=$true;SafeHelpCopy=$true;PageOutcome=$expectedPageOutcome}|ConvertTo-Json -Compress
}
finally {
    if($process -and -not $process.HasExited){[void]$process.CloseMainWindow();if(-not $process.WaitForExit(5000)){Stop-Process -Id $process.Id -Force}}
    if($server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force}
}
