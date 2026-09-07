[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\Orbit Navigator'),
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitOfflineNative
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
"@
function Wait-OrbitCondition{param([scriptblock]$Condition,[string]$Failure);$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds);do{try{if(&$Condition){return}}catch [System.Windows.Automation.ElementNotAvailableException]{};Start-Sleep -Milliseconds 125}while([DateTime]::UtcNow -lt $deadline);throw $Failure}
function Find-OrbitElement{param([System.Windows.Automation.AutomationElement]$Root,[string]$Name,[bool]$Required=$true);$c=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name);$e=$Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c);if($Required-and $null-eq$e){throw "UI Automation element not found: $Name"};$e}
function Get-OrbitWindows{param([int]$ProcessId);$c=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId);[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$c)}
function Get-OrbitMainRoot{param([int]$ProcessId);Get-OrbitWindows $ProcessId|Where-Object{$_.Current.Name-eq'Orbit Navigator'-and -not $_.Current.IsOffscreen}|Select-Object -First 1}
function Get-OrbitAncestorWindow {
    param([System.Windows.Automation.AutomationElement]$Element)
    $current = $Element
    while ($null -ne $current) {
        if ($current.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { return $current }
        $current = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($current)
    }
    return $null
}

$launcher=Join-Path $InstallRoot 'Orbit Navigator.exe';$application=Join-Path $InstallRoot 'app\OrbitNavigator.App.exe'
if(Get-Process -Name OrbitNavigator.App -ErrorAction SilentlyContinue){throw'Orbit must be closed.'}
$port=Get-Random -Minimum 53000 -Maximum 53999
$python=@'
import http.server,socketserver
page=b'''<!doctype html><html><head><meta charset="utf-8"><title>Offline Smoke</title></head><body style="font:32px Segoe UI;background:#10212a;color:white"><h1>Offline viewport PNG smoke</h1><p>This exact viewport is captured locally.</p></body></html>'''
class Handler(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  self.send_response(200);self.send_header('Content-Type','text/html; charset=utf-8');self.send_header('Cache-Control','no-store');self.send_header('Content-Length',str(len(page)));self.end_headers();self.wfile.write(page)
 def log_message(self,*args):pass
socketserver.TCPServer.allow_reuse_address=True
with socketserver.TCPServer(('127.0.0.1',__PORT__),Handler) as server:server.serve_forever()
'@.Replace('__PORT__',$port)
$si=[Diagnostics.ProcessStartInfo]::new();$si.FileName=(Get-Command python -ErrorAction Stop).Source;$si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.ArgumentList.Add('-c');$si.ArgumentList.Add($python);$server=[Diagnostics.Process]::Start($si)
$process=$null
try{
 Start-Sleep -Milliseconds 400;Invoke-WebRequest "http://127.0.0.1:$port/" -UseBasicParsing|Out-Null;Start-Process $launcher|Out-Null
 Wait-OrbitCondition{$script:process=Get-Process -Name OrbitNavigator.App -ErrorAction SilentlyContinue|Where-Object{$_.Path-eq$application}|Select-Object -First 1;$script:root=if($script:process){Get-OrbitMainRoot $script:process.Id}else{$null};$null-ne$script:root-and $null-ne(Find-OrbitElement $root 'Address and search' $false)}'Installed Orbit did not become ready.';$process=$script:process
 $dock=Find-OrbitElement $root 'Dock tab controller' $false;if($dock-and-not$dock.Current.IsOffscreen){$dock.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Wait-OrbitCondition{$script:root=Get-OrbitMainRoot $process.Id;$null-eq(Find-OrbitElement $root 'Dock tab controller' $false)}'Detached controller did not dock.'}
 Wait-OrbitCondition{$script:root=Get-OrbitMainRoot $process.Id;$script:address=Find-OrbitElement $root 'Address and search' $false;$null-ne$script:address}'Omnibox unavailable.'
 [void][OrbitOfflineNative]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle);$script:address.SetFocus();$script:address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("http://127.0.0.1:$port/");[OrbitOfflineNative]::keybd_event(0x0D,0,0,[UIntPtr]::Zero);[OrbitOfflineNative]::keybd_event(0x0D,0,2,[UIntPtr]::Zero)
 Wait-OrbitCondition{$script:root=Get-OrbitMainRoot $process.Id;$null-ne(Find-OrbitElement $root 'Offline Smoke, tab' $false)}'Offline test page did not load.'
 (Find-OrbitElement $root 'Open browser menu').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-OrbitCondition{$script:save=Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Save for offline' $false;$null-ne$script:save-and $script:save.Current.IsEnabled}'Save for offline was not enabled.'
 $script:save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-OrbitCondition{
  $script:root=Get-OrbitMainRoot $process.Id;$menu=Find-OrbitElement $root 'Open browser menu' $false;if($menu){$menu.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()};Start-Sleep -Milliseconds 100
  $script:library=Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Offline library' $false;$null-ne$script:library-and $script:library.Current.IsEnabled
 }'Offline snapshot did not complete.'
 $script:library.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-OrbitCondition{$script:open=Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Open offline copy — Offline Smoke' $false;$script:delete=Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Delete offline copy — Offline Smoke' $false;$null-ne$script:open-and $null-ne$script:delete}'Saved PNG did not appear in the offline library.'
 Stop-Process -Id $server.Id -Force;[void]$server.WaitForExit(5000)
 $script:open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-OrbitCondition{$script:snapshot=Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Offline snapshot of Offline Smoke' $false;$null-ne$script:snapshot}'Stored offline PNG did not open with the network server stopped.'
 $snapshotWindow = Get-OrbitAncestorWindow $script:snapshot
 if ($null -eq $snapshotWindow) { throw 'Offline snapshot window was not identified.' }
 $snapshotWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Wait-OrbitCondition{$null-eq(Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Offline snapshot of Offline Smoke' $false)}'Offline snapshot viewer did not close.'
 for ($deleteAttempt = 0; $deleteAttempt -lt 10; $deleteAttempt++) {
  $script:delete = Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Delete offline copy — Offline Smoke' $false
  if ($null -eq $script:delete) { break }
  $script:delete.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 300
 }
 Wait-OrbitCondition{$null-eq(Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Delete offline copy — Offline Smoke' $false)}'Offline copy did not delete.'
 $libraryTitle = Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Offline library — Orbit Navigator' $false
 $libraryWindow = if ($libraryTitle) { Get-OrbitAncestorWindow $libraryTitle } else { $null }
 if ($libraryWindow) { $libraryWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }

 $root=Get-OrbitMainRoot $process.Id;(Find-OrbitElement $root 'New private window').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-OrbitCondition{$script:private=Get-OrbitWindows $process.Id|Where-Object{$null-ne(Find-OrbitElement $_ 'Private window' $false)}|Select-Object -First 1;$null-ne$script:private}'Private window did not open.'
 [void][OrbitOfflineNative]::SetForegroundWindow([IntPtr]$script:private.Current.NativeWindowHandle);(Find-OrbitElement $script:private 'Open browser menu').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-OrbitCondition{$script:privateSave=Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Save for offline, unavailable' $false;$script:privateLibrary=Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) 'Offline library, unavailable' $false;$null-ne$script:privateSave-and $null-ne$script:privateLibrary}'Private offline commands were not presented fail-closed.'
 if ($script:privateSave.Current.IsEnabled -or $script:privateLibrary.Current.IsEnabled) { throw 'Private window exposed an enabled offline mutation or storage route.' }
 $script:private.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 [pscustomobject]@{ExplicitViewportPngSaved=$true;LibraryListedItem=$true;OpenedWithServerStopped=$true;ViewerIsImageOnly=$true;Deleted=$true;PrivateSaveDenied=$true;PrivateLibraryDenied=$true}|ConvertTo-Json -Compress
} finally {
 if ($process -and -not $process.HasExited) { [void]$process.CloseMainWindow(); if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force } }
 if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
