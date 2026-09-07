[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidenceDirectory,
    [switch]$SkipWorkspaceHover
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitV6AcceptanceNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
}
"@

function Wait-Until([scriptblock]$Condition, [string]$Failure, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { try { if (& $Condition) { return } } catch [Windows.Automation.ElementNotAvailableException] { }; Start-Sleep -Milliseconds 150 } while([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Main([int]$Id) {
    [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
      [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$Id)) |
      Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen } | Select-Object -First 1
}
function Find([Windows.Automation.AutomationElement]$Root,[string]$Name) {
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
      [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name))
}
function FindLike([Windows.Automation.AutomationElement]$Root,[string]$Pattern) {
    $Root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
      Where-Object { $_.Current.Name -like $Pattern } | Select-Object -First 1
}
function Capture([Diagnostics.Process]$Process,[string]$Path) {
    $rect=[OrbitV6AcceptanceNative+RECT]::new();$Process.Refresh();if(-not[OrbitV6AcceptanceNative]::GetWindowRect($Process.MainWindowHandle,[ref]$rect)){throw 'Unable to capture Orbit window.'}
    $bounds=[Drawing.Rectangle]::FromLTRB($rect.Left,$rect.Top,$rect.Right,$rect.Bottom);$bitmap=[Drawing.Bitmap]::new($bounds.Width,$bounds.Height)
    try{$graphics=[Drawing.Graphics]::FromImage($bitmap);try{$graphics.CopyFromScreen($bounds.Location,[Drawing.Point]::Empty,$bounds.Size)}finally{$graphics.Dispose()};$bitmap.Save($Path,[Drawing.Imaging.ImageFormat]::Png)}finally{$bitmap.Dispose()}
}
function Difference([string]$A,[string]$B) {
    $one=[Drawing.Bitmap]::FromFile($A);$two=[Drawing.Bitmap]::FromFile($B)
    try { if($one.Size -ne $two.Size){throw 'Temporal captures have different dimensions.'};$changed=0L;$sampled=0L
      for($y=0;$y -lt $one.Height;$y+=4){for($x=0;$x -lt $one.Width;$x+=4){$sampled++;if($one.GetPixel($x,$y).ToArgb() -ne $two.GetPixel($x,$y).ToArgb()){$changed++}}}
      [pscustomobject]@{SampledPixels=$sampled;ChangedPixels=$changed;ChangedPercent=[Math]::Round(100*$changed/[Math]::Max(1,$sampled),3)}
    } finally {$one.Dispose();$two.Dispose()}
}
function Invoke([Windows.Automation.AutomationElement]$Element){$Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}

$install=[IO.Path]::GetFullPath($InstallRoot);$app=[IO.Path]::GetFullPath((Join-Path $install 'app\OrbitNavigator.App.exe'));$launcher=Join-Path $install 'Orbit Navigator.exe'
$profile=[IO.Path]::GetFullPath($ProfileRoot);$accept=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if(-not $profile.StartsWith($accept+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe disposable profile root.'}
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory);[IO.Directory]::CreateDirectory($evidence)|Out-Null
$process=$null
try {
  $runId=[Guid]::NewGuid();$after=[DateTime]::UtcNow.AddSeconds(-1);Start-Process $launcher -ArgumentList @('--acceptance-profile-root',$profile,'--acceptance-run-id',$runId.ToString('D'))|Out-Null;$cim=$null
  Wait-Until {$script:cim=Get-CimInstance Win32_Process|Where-Object{$_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $app -and $_.CreationDate.ToUniversalTime() -ge $after}|Select-Object -First 1;$null-ne$script:cim} 'Orbit did not start.'
  $process=Get-Process -Id $script:cim.ProcessId;$root=$null;Wait-Until{$script:root=Main $process.Id;$null-ne$script:root -and $null-ne(Find $root 'Open new tab')} 'Orbit window did not become ready.'
  # The controller is exposed before the attached WebView/startup overlay has
  # completed its final focus transition. Let InitialHostReady settle so the
  # first physical/controller action cannot be swallowed by that transition.
  Start-Sleep -Seconds 5
  $root=Main $process.Id
  if($null-eq(Find $root 'Stellar view · switch to Basic')){
    # A restored workspace can contain collapsed New Tab entries whose UIA
    # peers are discoverable but are not selectable from the current viewport.
    # Create an authoritative New Tab instead of invoking a hidden peer. The
    # explicit controller button is more deterministic than sending Ctrl+T to
    # whichever child HWND currently owns keyboard focus.
    Invoke (Find $root 'Open new tab')
  }
  Wait-Until{$script:root=Main $process.Id;($null-ne(Find $root 'Stellar view · switch to Basic')) -or ($null-ne(Find $root 'Basic view · switch to Stellar'))} 'New Tab appearance control did not become ready.'
  $toStellar=Find $root 'Basic view · switch to Stellar';if($toStellar){Invoke $toStellar;Wait-Until{$script:root=Main $process.Id;$null-ne(Find $root 'Stellar view · switch to Basic')} 'Stellar mode did not apply before temporal capture.'}
  $t0=Join-Path $evidence 'v6-temporal-t0.png';$t10=Join-Path $evidence 'v6-temporal-t10.png';Capture $process $t0;Start-Sleep -Seconds 10;Capture $process $t10;$temporal=Difference $t0 $t10
  if($temporal.ChangedPixels -lt 100){throw 'Installed v6 scene did not visibly advance across 10 seconds.'}
  $hover=$null;$h0=$null;$h4=$null
  if(-not$SkipWorkspaceHover){
    # The animated scene can refresh its WPF automation subtree during the
    # ten-second capture. Reacquire the current native root before locating the
    # durable workspace action.
    $root=Main $process.Id
    $workspace=Find $root 'Open Research Set from workspaces hub'
    if(-not$workspace){$workspace=Find $root 'Open workspace Research Set, 3 tabs'}
    if(-not$workspace){throw 'Saved workspace target unavailable for physical hover evidence.'}
    $b=$workspace.Current.BoundingRectangle
    $process.Refresh();[OrbitV6AcceptanceNative]::SetForegroundWindow($process.MainWindowHandle)|Out-Null
    $x=[int]($b.Left+$b.Width/2);$y=[int]($b.Top+$b.Height/2)
    [OrbitV6AcceptanceNative]::SetCursorPos($x-12,$y)|Out-Null;Start-Sleep -Milliseconds 120
    [OrbitV6AcceptanceNative]::SetCursorPos($x,$y)|Out-Null
    [OrbitV6AcceptanceNative]::mouse_event(0x0001,1,0,0,[UIntPtr]::Zero)
    Wait-Until{$script:root=Main $process.Id;$null-ne(Find $root 'Research Set details')} 'Physical workspace hover did not expose readable details.' 8
    $h0=Join-Path $evidence 'v6-hover-freeze-t0.png';$h4=Join-Path $evidence 'v6-hover-freeze-t4.png';Capture $process $h0;Start-Sleep -Seconds 4;Capture $process $h4;$hover=Difference $h0 $h4
  }
  $root=Main $process.Id;$toggle=Find $root 'Stellar view · switch to Basic';Invoke $toggle
  Wait-Until{$script:root=Main $process.Id;$null-ne(Find $root 'Basic view · switch to Stellar')} 'Global Basic mode did not apply.'
  $basic=Join-Path $evidence 'basic-mode.png';Capture $process $basic
  Invoke (Find $root 'Basic view · switch to Stellar');Wait-Until{$script:root=Main $process.Id;$null-ne(Find $root 'Stellar view · switch to Basic')} 'Global Stellar mode did not restore.'
  [pscustomobject]@{TemporalSeconds=10;TemporalDifference=$temporal;PhysicalWorkspaceHover=(-not$SkipWorkspaceHover);HoverDetailsVisible=(-not$SkipWorkspaceHover);HoverCaptureSeconds=$(if($SkipWorkspaceHover){0}else{4});HoverDifference=$hover;BasicModeApplied=$true;StellarModeRestored=$true;Captures=@($t0,$t10,$h0,$h4,$basic)|Where-Object{$_}}|ConvertTo-Json -Depth 5
}
finally {
  if($process -and -not$process.HasExited){[void]$process.CloseMainWindow();if(-not$process.WaitForExit(5000)){Stop-Process -Id $process.Id -Force}}
}
