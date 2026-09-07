[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidencePath,
    [string]$SourceGroupName = 'New group',
    [int]$WorkspaceTabCount = 3,
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class OrbitWorkspaceAcceptanceNative {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
}
"@

function Wait-Until([scriptblock]$Condition,[string]$Failure,[int]$Seconds=$TimeoutSeconds) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do { try { if(& $Condition){return} } catch [Windows.Automation.ElementNotAvailableException]{}; Start-Sleep -Milliseconds 125 } while([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Roots([int]$Id) {
    $c=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$Id)
    @([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$c)|?{-not $_.Current.IsOffscreen})
}
function Main([int]$Id) { Roots $Id|?{$_.Current.Name -eq 'Orbit Navigator'}|select -First 1 }
function Find([Windows.Automation.AutomationElement]$Root,[string]$Name,[Windows.Automation.ControlType]$Type=$null) {
    $items=$Root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    @($items|?{$_.Current.Name -eq $Name -and ($null -eq $Type -or $_.Current.ControlType -eq $Type) -and -not $_.Current.IsOffscreen}|select -First 1)
}
function Find-Global([string]$Name,[Windows.Automation.ControlType]$Type) { Find ([Windows.Automation.AutomationElement]::RootElement) $Name $Type }
function Find-ContextItem([string]$Name) {
    $menus=[Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Menu))
    foreach($menu in $menus) {
        if($menu.Current.ClassName -ne 'ContextMenu' -or $menu.Current.IsOffscreen -or $menu.Current.BoundingRectangle.Width -lt 2){continue}
        $item=Find $menu $Name ([Windows.Automation.ControlType]::MenuItem)
        if($item){return $item}
    }
    return $null
}
function Invoke([Windows.Automation.AutomationElement]$Element) { $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function LeftClick([Windows.Automation.AutomationElement]$Element) {
    $b=$Element.Current.BoundingRectangle; if($b.Width -lt 1){throw 'Empty click target.'}
    [void][OrbitWorkspaceAcceptanceNative]::SetCursorPos([int]($b.Left+$b.Width/2),[int]($b.Top+$b.Height/2))
    [OrbitWorkspaceAcceptanceNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [OrbitWorkspaceAcceptanceNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 250
}
function RightClick([Windows.Automation.AutomationElement]$Element) {
    $b=$Element.Current.BoundingRectangle; if($b.Width -lt 1){throw 'Empty right-click target.'}
    $script:process.Refresh(); [void][OrbitWorkspaceAcceptanceNative]::SetForegroundWindow($script:process.MainWindowHandle); Start-Sleep -Milliseconds 150
    [void][OrbitWorkspaceAcceptanceNative]::SetCursorPos([int]($b.Left+$b.Width/2),[int]($b.Top+$b.Height/2))
    [OrbitWorkspaceAcceptanceNative]::mouse_event(8,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [OrbitWorkspaceAcceptanceNative]::mouse_event(16,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 250
}
function Hover([Windows.Automation.AutomationElement]$Element) {
    $b=$Element.Current.BoundingRectangle; if($b.Width -lt 1){throw 'Empty hover target.'}
    $script:process.Refresh(); [void][OrbitWorkspaceAcceptanceNative]::SetForegroundWindow($script:process.MainWindowHandle); Start-Sleep -Milliseconds 150
    [void][OrbitWorkspaceAcceptanceNative]::SetCursorPos([int]($b.Left+$b.Width/2),[int]($b.Top+$b.Height/2)); Start-Sleep -Milliseconds 450
}
function Press-Escape {
    [OrbitWorkspaceAcceptanceNative]::keybd_event(27,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60
    [OrbitWorkspaceAcceptanceNative]::keybd_event(27,0,2,[UIntPtr]::Zero); Start-Sleep -Milliseconds 300
}
function Assert-GroupActionMenu([string]$Route) {
    foreach($name in @('Rename group…','Group color','Save as workspace…')) {
        $candidate=$null
        Wait-Until { $script:candidate=Find-ContextItem $name; $null -ne $script:candidate } "$Route did not expose $name." 5
    }
}
function ExpandMenu([string]$Name) {
    $item=$null; Wait-Until { $script:item=Find-ContextItem $Name; $null -ne $script:item } "Menu item unavailable: $Name" 5
    $script:item.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand();Start-Sleep -Milliseconds 200
}
function Get-GroupButton([Windows.Automation.AutomationElement]$Root,[string]$Name,[int]$Count) {
    $Root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button))|
      ?{-not $_.Current.IsOffscreen -and $_.Current.Name -eq "$Name, $Count tabs" -and $_.Current.BoundingRectangle.Width -ge 44}|select -First 1
}
function StartOrbit([string]$Launcher,[string]$Application,[string]$Profile) {
    $runId=[Guid]::NewGuid();$after=[DateTime]::UtcNow.AddSeconds(-1);Start-Process $Launcher -ArgumentList @('--acceptance-profile-root',$Profile,'--acceptance-run-id',$runId.ToString('D'))|Out-Null;$cim=$null
    Wait-Until { $script:cim=Get-CimInstance Win32_Process|?{$_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $Application -and $_.CreationDate.ToUniversalTime() -ge $after}|select -First 1; $null -ne $script:cim } 'Orbit did not start.'
    $p=Get-Process -Id $script:cim.ProcessId; Wait-Until { $null -ne (Main $p.Id) } 'Orbit window unavailable.'; return $p
}
function StopOrbit([Diagnostics.Process]$P) { if($P -and -not $P.HasExited){try{$r=Main $P.Id;if($r){$r.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()};if(-not$P.WaitForExit(8000)){Stop-Process -Id $P.Id -Force}}catch{if(-not$P.HasExited){Stop-Process -Id $P.Id -Force}}} }
function SelectPreviewNewTab([Windows.Automation.AutomationElement]$Root) {
    $g=$null
    Wait-Until {
        $script:previewRoot=Main $script:process.Id
        $script:g=Get-GroupButton $script:previewRoot $SourceGroupName 5
        if(-not $script:g){$script:g=Get-GroupButton $script:previewRoot 'Research' 5}
        $null -ne $script:g
    } 'Five-tab group unavailable.' 5
    Invoke $script:g; $item=$null; Wait-Until { $script:item=Find-ContextItem 'New Tab'; $null -ne $script:item } 'Group preview did not expose New Tab.' 5; Invoke $script:item
}
function Navigate([Windows.Automation.AutomationElement]$Root,[string]$Address,[string]$Title) {
    $box=$null
    Wait-Until {
        $script:navigationRoot=Main $script:process.Id
        $script:box=if($script:navigationRoot){Find $script:navigationRoot 'Address and search'}else{$null}
        $null -ne $script:box
    } 'Omnibox unavailable after selecting the group tab.' 8
    $script:box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Address);$script:box.SetFocus()
    [OrbitWorkspaceAcceptanceNative]::keybd_event(13,0,0,[UIntPtr]::Zero);[OrbitWorkspaceAcceptanceNative]::keybd_event(13,0,2,[UIntPtr]::Zero)
    Wait-Until { $null -ne (Find (Main $script:process.Id) "$Title, tab" ([Windows.Automation.ControlType]::Button)) } "Title did not project: $Title" 15
}
function Capture([Diagnostics.Process]$P,[string]$Path) {
    $n=[OrbitWorkspaceAcceptanceNative+RECT]::new();$P.Refresh();if(-not[OrbitWorkspaceAcceptanceNative]::GetWindowRect($P.MainWindowHandle,[ref]$n)){throw 'No capture bounds.'}
    $r=[Drawing.Rectangle]::FromLTRB($n.Left,$n.Top,$n.Right,$n.Bottom);$b=[Drawing.Bitmap]::new($r.Width,$r.Height);try{$g=[Drawing.Graphics]::FromImage($b);try{$g.CopyFromScreen($r.Location,[Drawing.Point]::Empty,$r.Size)}finally{$g.Dispose()};$out=[IO.Path]::GetFullPath($Path);[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($out))|Out-Null;$b.Save($out,[Drawing.Imaging.ImageFormat]::Png)}finally{$b.Dispose()}
}
function CaptureDesktop([string]$Path) {
    $r=[Windows.Forms.SystemInformation]::VirtualScreen;$b=[Drawing.Bitmap]::new($r.Width,$r.Height);try{$g=[Drawing.Graphics]::FromImage($b);try{$g.CopyFromScreen($r.Location,[Drawing.Point]::Empty,$r.Size)}finally{$g.Dispose()};$out=[IO.Path]::GetFullPath($Path);[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($out))|Out-Null;$b.Save($out,[Drawing.Imaging.ImageFormat]::Png)}finally{$b.Dispose()}
}

$install=[IO.Path]::GetFullPath($InstallRoot);$launcher=Join-Path $install 'Orbit Navigator.exe';$application=[IO.Path]::GetFullPath((Join-Path $install 'app\OrbitNavigator.App.exe'))
$profile=[IO.Path]::GetFullPath($ProfileRoot);$accept=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\');if(-not$profile.StartsWith($accept+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe profile root.'}
$port=Get-Random -Minimum 53100 -Maximum 53900
$python=@'
import http.server,socketserver,sys
P=int(sys.argv[1]); titles={'/a':'Alpha','/b':'Beta','/c':'Gamma'}
class H(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  if self.path=='/favicon.png':
   import base64; b=base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAIUlEQVR42mNk+M/wn4ECwESJ5lEDRg0YNWDUgFEDBg0AAP//AwC4bQMeLwAAAABJRU5ErkJggg==');self.send_response(200);self.send_header('Content-Type','image/png');self.end_headers();self.wfile.write(b);return
  t=titles.get(self.path,'Workspace Fixture');data=(f'<!doctype html><title>{t}</title><link rel="icon" href="/favicon.png"><h1>{t}</h1>').encode();self.send_response(200);self.send_header('Content-Type','text/html');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
 def log_message(self,*a): pass
socketserver.TCPServer(('127.0.0.1',P),H).serve_forever()
'@
$si=[Diagnostics.ProcessStartInfo]::new();$si.FileName=(Get-Command python -ErrorAction Stop).Source;$si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.ArgumentList.Add('-c');$si.ArgumentList.Add($python);$si.ArgumentList.Add([string]$port);$server=[Diagnostics.Process]::Start($si)
$script:process=$null
try {
  Start-Sleep -Milliseconds 500;if($server.HasExited){throw 'Loopback fixture failed.'};Invoke-WebRequest "http://127.0.0.1:$port/a" -UseBasicParsing|Out-Null
  $script:process=StartOrbit $launcher $application $profile;$root=$null
  Wait-Until { $script:root=Main $process.Id; $null -ne (Get-GroupButton $root $SourceGroupName 5) } 'Five-tab seed group did not restore.'
  foreach($site in @(@('a','Alpha'),@('b','Beta'),@('c','Gamma'))){SelectPreviewNewTab $root;$root=Main $process.Id;Navigate $root "http://127.0.0.1:$port/$($site[0])" $site[1];$root=Main $process.Id}
  # Exercise both real WPF popup ownership paths. First hover the group and
  # right-click its retained header while the preview is open.
  $g=Get-GroupButton $root $SourceGroupName 5; Hover $g
  $previewItem=$null; Wait-Until { $script:previewItem=Find-ContextItem 'New Tab'; $null -ne $script:previewItem } 'Physical header hover did not open the five-tab preview.' 5
  RightClick $g; Assert-GroupActionMenu 'Header right-click';
  $headerCapture=[IO.Path]::ChangeExtension($EvidencePath,'.group-header-context.png'); CaptureDesktop $headerCapture
  Press-Escape

  # Reopen by pointer hover, then right-click the preview surface itself. The
  # product must close the preview and forward to the retained group actions.
  $root=Main $process.Id; $g=Get-GroupButton $root $SourceGroupName 5; Hover $g
  $previewItem=$null; Wait-Until { $script:previewItem=Find-ContextItem 'New Tab'; $null -ne $script:previewItem } 'Second physical hover did not reopen the five-tab preview.' 5
  RightClick $script:previewItem; Assert-GroupActionMenu 'Preview-surface right-click'
  $previewCapture=[IO.Path]::ChangeExtension($EvidencePath,'.preview-surface-context.png'); CaptureDesktop $previewCapture
  Start-Sleep -Milliseconds 350; CaptureDesktop ([IO.Path]::ChangeExtension($EvidencePath,'.group-context.png'))
  $visibleContextItems=@([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::MenuItem))|?{-not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -ge 2}|%{$_.Current.Name})
  [pscustomobject]@{Stage='group-context';PhysicalHover=$true;HeaderRightClickActions=$true;PreviewSurfaceRightClickActions=$true;HeaderCapture=$headerCapture;PreviewCapture=$previewCapture;VisibleMenuItems=$visibleContextItems}|ConvertTo-Json -Compress|Write-Output
  $rename=$null; Wait-Until { $script:rename=Find-ContextItem 'Rename group…'; $null -ne $script:rename } 'Rename command unavailable.' 5
  $renameBefore=[IO.Path]::ChangeExtension($EvidencePath,'.rename-before.png');CaptureDesktop $renameBefore
  [pscustomobject]@{Name=$script:rename.Current.Name;IsEnabled=$script:rename.Current.IsEnabled;IsOffscreen=$script:rename.Current.IsOffscreen;ClassName=$script:rename.Current.ClassName;Bounds=$script:rename.Current.BoundingRectangle.ToString();BeforeCapture=$renameBefore}|ConvertTo-Json -Compress|Write-Output
  LeftClick $script:rename
  CaptureDesktop ([IO.Path]::ChangeExtension($EvidencePath,'.rename-after.png'))
  Start-Sleep -Milliseconds 750
  $renameRoot=$null; Wait-Until { $script:renameRoot=Find-Global 'Rename tab group — Orbit Navigator' ([Windows.Automation.ControlType]::Window); $null -ne $script:renameRoot } 'Rename dialog unavailable.'
  Write-Output 'stage:rename-input';$name=Find $renameRoot 'Tab group name';if(-not$name){throw 'Rename input unavailable.'};$name.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Research');Invoke (Find $renameRoot 'Rename tab group' ([Windows.Automation.ControlType]::Button))
  Wait-Until { $script:root=Main $process.Id; $null -ne (Get-GroupButton $root 'Research' 5) } 'Renamed group did not project.'
  Write-Output 'stage:group-color';$g=Get-GroupButton $root 'Research' 5; RightClick $g; ExpandMenu 'Group color'; $violet=$null; Wait-Until { $script:violet=Find-ContextItem 'Violet'; $null -ne $script:violet } 'Violet group color unavailable.' 5; Invoke $script:violet; Start-Sleep -Milliseconds 500
  $root=Main $process.Id; $g=Get-GroupButton $root 'Research' 5; RightClick $g; $save=$null; Wait-Until { $script:save=Find-ContextItem 'Save as workspace…'; $null -ne $script:save } 'Save-as-workspace unavailable.' 5; Invoke $script:save
  Write-Output 'stage:workspace-dialog';$dialog=$null; Wait-Until { $script:dialog=Find-Global 'Save tab group as workspace — Orbit Navigator' ([Windows.Automation.ControlType]::Window); $null -ne $script:dialog } 'Workspace save dialog unavailable.'
  (Find $dialog 'Workspace name' ([Windows.Automation.ControlType]::Edit)).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Research Set')
  (Find $dialog 'Workspace note' ([Windows.Automation.ControlType]::Edit)).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Disposable acceptance note')
  Write-Output 'stage:workspace-artwork';$art=Find $dialog 'Workspace artwork'; $art.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); $artItem=$null; Wait-Until { $script:artItem=Find-Global 'Use Alpha' ([Windows.Automation.ControlType]::ListItem); $null -ne $script:artItem } 'In-group site artwork was unavailable.' 5; LeftClick $script:artItem
  Invoke (Find $dialog 'Save workspace' ([Windows.Automation.ControlType]::Button))
  Wait-Until { $null -eq (Find-Global 'Save tab group as workspace — Orbit Navigator' ([Windows.Automation.ControlType]::Window)) } 'Workspace dialog did not close.'
  $root=Main $process.Id;$before=@($root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Open new tab'))).Count
  Invoke (Find $root 'Open new tab' ([Windows.Automation.ControlType]::Button));Start-Sleep -Milliseconds 800;$root=Main $process.Id
  $list=Find $root 'Show list view' ([Windows.Automation.ControlType]::Button);if($list){Invoke $list;Start-Sleep -Milliseconds 300}
  $workspaceOpenName = "Open workspace Research Set, $WorkspaceTabCount tabs"
  $open=$null; Wait-Until { $script:open=Find (Main $process.Id) $workspaceOpenName ([Windows.Automation.ControlType]::Button); $null -ne $script:open } 'Saved workspace did not appear on New Tab.' 10
  $preOpenSessionFile=Get-ChildItem -LiteralPath (Join-Path $profile 'profiles\data') -Recurse -File|? FullName -like '*browser.workspace-session*'|select -First 1
  if(-not$preOpenSessionFile){throw 'Authoritative session was unavailable before workspace open.'}
  $preOpenBytes=[IO.File]::ReadAllBytes($preOpenSessionFile.FullName)
  $preOpenJson=[Text.Encoding]::UTF8.GetString($preOpenBytes,16,$preOpenBytes.Length-16)|ConvertFrom-Json
  $preOpenTabIds=@($preOpenJson.Tabs|ForEach-Object{$_.TabId.Value})
  Invoke $script:open
  # Opening a workspace prepares and attaches multiple WebView2 hosts before
  # the coordinator commits the atomic group. Allow the full bounded host
  # preparation budget rather than classifying the still-in-flight operation.
  Wait-Until { $null -ne (Find (Main $process.Id) 'Alpha, tab' ([Windows.Automation.ControlType]::Button)) } 'Workspace did not select its configured first site.' 75
  Capture $process $EvidencePath
  StopOrbit $process;$script:process=$null
  $presetFile=Get-ChildItem -LiteralPath (Join-Path $profile 'profiles\data') -Recurse -File|? FullName -like '*browser.workspace-presets*'|select -First 1
  $sessionFile=Get-ChildItem -LiteralPath (Join-Path $profile 'profiles\data') -Recurse -File|? FullName -like '*browser.workspace-session*'|select -First 1
  if(-not$presetFile -or -not$sessionFile){throw 'Durable workspace/session files were not written.'}
  $presetBytes=[IO.File]::ReadAllBytes($presetFile.FullName);$presetJson=[Text.Encoding]::UTF8.GetString($presetBytes,16,$presetBytes.Length-16)|ConvertFrom-Json
  $sessionBytes=[IO.File]::ReadAllBytes($sessionFile.FullName);$sessionJson=[Text.Encoding]::UTF8.GetString($sessionBytes,16,$sessionBytes.Length-16)|ConvertFrom-Json
  $presetCatalog=if($presetJson.PSObject.Properties['Presets']){@($presetJson.Presets)}else{@($presetJson)}
  $preset=$presetCatalog|? Name -eq 'Research Set'|select -First 1;if(-not$preset){throw 'Research Set missing from durable catalog.'}
  if($preset.Note -ne 'Disposable acceptance note' -or $preset.ColorToken -ne 'Violet' -or $preset.Artwork.Kind -ne 1){throw 'Workspace note/color/site artwork did not persist.'}
  $opened=$sessionJson.Groups|? Name -eq 'Research Set'|select -First 1;if(-not$opened -or -not$opened.IsCollapsed){throw 'Opened workspace was not a new collapsed live group.'}
  $postIds=@($sessionJson.Tabs|ForEach-Object{$_.TabId.Value})
  if(@($preOpenTabIds|Where-Object{$_ -notin $postIds}).Count -ne 0){throw 'Workspace open removed or replaced an existing tab.'}
  $openedTabs=@($sessionJson.Tabs|Where-Object{$_.GroupId.Value -eq $opened.GroupId.Value})
  if($openedTabs.Count -ne $preset.Tabs.Count){throw 'Opened live group does not contain the saved workspace tabs exactly once.'}
  $selected=$sessionJson.Tabs|Where-Object{$_.TabId.Value -eq $sessionJson.SelectedTabId.Value}|Select-Object -First 1
  if(-not$selected -or $selected.Address -ne $preset.Tabs[$preset.FirstTabIndex].Target){throw 'Workspace did not preserve FirstTabIndex selection.'}
  [pscustomobject]@{Renamed=$true;ColorPersisted=$true;SavedWorkspace=$true;NotePersisted=$true;SiteArtworkPersisted=$true;OpenedAsNewCollapsedGroup=$true;FirstSiteSelected=$true;ExistingTabsPreserved=$true;PreOpenTabCount=$preOpenTabIds.Count;PostOpenTabCount=$postIds.Count;OpenedWorkspaceTabCount=$openedTabs.Count;EvidencePath=[IO.Path]::GetFullPath($EvidencePath);PresetFile=$presetFile.FullName;SessionFile=$sessionFile.FullName}|ConvertTo-Json -Depth 4
}
finally { if($script:process){StopOrbit $script:process};if($server -and -not$server.HasExited){$server.Kill($true);$server.WaitForExit(3000)|Out-Null} }
