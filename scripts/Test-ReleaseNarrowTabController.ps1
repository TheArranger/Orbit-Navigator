[CmdletBinding()]
param(
    [string]$ApplicationPath = (Join-Path $PSScriptRoot "..\src\OrbitNavigator.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\OrbitNavigator.App.exe"),
    [string]$DotNetHost = (Join-Path $PSScriptRoot "..\.tools\dotnet\dotnet.exe"),
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OrbitNarrowControllerNative {
  public delegate bool EnumWindowsProc(IntPtr h,IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback,IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint processId);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder text,int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int hgt,bool repaint);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
  public static IntPtr FindVisibleWindow(uint processId,string titlePart) {
    IntPtr found=IntPtr.Zero;
    EnumWindows((h,p) => {
      uint owner; GetWindowThreadProcessId(h,out owner);
      if(owner!=processId || !IsWindowVisible(h)) return true;
      var text=new StringBuilder(GetWindowTextLength(h)+1); GetWindowText(h,text,text.Capacity);
      if(text.ToString().IndexOf(titlePart,StringComparison.OrdinalIgnoreCase)>=0){found=h;return false;}
      return true;
    },IntPtr.Zero);
    return found;
  }
}
"@

function Wait-Until([scriptblock]$Condition,[string]$Failure,[int]$Seconds=$TimeoutSeconds) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { if (& $Condition) { return } } catch [Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 125
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Top-Level([int]$ProcessId,[string]$Name) {
    $condition=[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name))
    [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition) |
        Where-Object { -not $_.Current.IsOffscreen } | Select-Object -First 1
}
function Top-Level-Like([int]$ProcessId,[string]$Pattern) {
    [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId)) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name -like $Pattern } |
        Select-Object -First 1
}
function Native-Owned-Window([int]$ProcessId,[string]$TitlePart) {
    $handle=[OrbitNarrowControllerNative]::FindVisibleWindow([uint32]$ProcessId,$TitlePart)
    if($handle -eq [IntPtr]::Zero){return $null}
    [Windows.Automation.AutomationElement]::FromHandle($handle)
}
function Descendants($Root) {
    @($Root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition))
}
function Named($Root,[string]$Name,[Windows.Automation.ControlType]$Type=$null) {
    Descendants $Root | Where-Object {
        -not $_.Current.IsOffscreen -and $_.Current.Name -eq $Name -and
        ($null -eq $Type -or $_.Current.ControlType -eq $Type)
    } | Select-Object -First 1
}
function Named-Like($Root,[string]$Pattern,[Windows.Automation.ControlType]$Type=$null) {
    Descendants $Root | Where-Object {
        -not $_.Current.IsOffscreen -and $_.Current.Name -like $Pattern -and
        ($null -eq $Type -or $_.Current.ControlType -eq $Type)
    } | Select-Object -First 1
}
function Invoke-Control($Element) {
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Click-Control($Element) {
    $r=$Element.Current.BoundingRectangle
    [void][OrbitNarrowControllerNative]::SetCursorPos([int]($r.Left+$r.Width/2),[int]($r.Top+$r.Height/2))
    [OrbitNarrowControllerNative]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
    [OrbitNarrowControllerNative]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
}
function Rect-Object($Element) {
    $r=$Element.Current.BoundingRectangle
    [pscustomobject]@{Name=$Element.Current.Name;Left=$r.Left;Top=$r.Top;Width=$r.Width;Height=$r.Height;Right=$r.Right;Bottom=$r.Bottom}
}
function Assert-Commands([object[]]$Commands,$Window,[string]$Label,[bool]$ExpectWrap) {
    if ($Commands.Count -ne 4) { throw "$Label did not expose exactly four pinned commands." }
    $windowRect=$Window.Current.BoundingRectangle
    foreach($command in $Commands) {
        if ($command.Width -lt 44 -or $command.Height -lt 44) { throw "$Label command $($command.Name) is below 44 DIPs." }
        if ($command.Left -lt $windowRect.Left-1 -or $command.Top -lt $windowRect.Top-1 -or
            $command.Right -gt $windowRect.Right+1 -or $command.Bottom -gt $windowRect.Bottom+1) {
            throw "$Label command $($command.Name) is clipped outside its owner window. Command=$($command|ConvertTo-Json -Compress) Window=$([pscustomobject]@{Left=$windowRect.Left;Top=$windowRect.Top;Width=$windowRect.Width;Height=$windowRect.Height;Right=$windowRect.Right;Bottom=$windowRect.Bottom}|ConvertTo-Json -Compress)"
        }
    }
    for($i=0;$i -lt $Commands.Count;$i++) {
        for($j=$i+1;$j -lt $Commands.Count;$j++) {
            $width=[Math]::Min($Commands[$i].Right,$Commands[$j].Right)-[Math]::Max($Commands[$i].Left,$Commands[$j].Left)
            $height=[Math]::Min($Commands[$i].Bottom,$Commands[$j].Bottom)-[Math]::Max($Commands[$i].Top,$Commands[$j].Top)
            if ($width -gt 0.5 -and $height -gt 0.5) { throw "$Label commands overlap: $($Commands[$i].Name), $($Commands[$j].Name)." }
        }
    }
    $rows=@($Commands | ForEach-Object {[Math]::Round($_.Top/4)*4} | Sort-Object -Unique)
    if ($ExpectWrap -and $rows.Count -lt 2) { throw "$Label commands did not wrap at the narrow side-rail floor." }
    $rows.Count
}
function Controller-Commands($Root,[bool]$Detached) {
    $more=Named-Like $Root 'More tabs*' ([Windows.Automation.ControlType]::Button)
    $resource=Named $Root 'Browser resources' ([Windows.Automation.ControlType]::Button)
    $dockName=if($Detached){'Dock tab controller'}else{'Pop out tab controller'}
    $dock=Named $Root $dockName ([Windows.Automation.ControlType]::Button)
    if(-not$more-or-not$resource-or-not$dock){throw 'A pinned controller command is missing.'}
    $dockRect=$dock.Current.BoundingRectangle
    $newCandidates=@(Descendants $Root|Where-Object{$_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -eq 'Open new tab' -and -not$_.Current.IsOffscreen})
    if($newCandidates.Count -eq 0){throw 'Open new tab is missing.'}
    $new=$newCandidates|Sort-Object { $r=$_.Current.BoundingRectangle;[Math]::Abs(($r.Left+$r.Width/2)-($dockRect.Left+$dockRect.Width/2))+[Math]::Abs(($r.Top+$r.Height/2)-($dockRect.Top+$dockRect.Height/2)) }|Select-Object -First 1
    @($more,$new,$resource,$dock)|ForEach-Object{Rect-Object $_}
}
function Capture-Window($Window,[string]$Path) {
    $rect=$Window.Current.BoundingRectangle
    $bitmap=[Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
    try {
        $g=[Drawing.Graphics]::FromImage($bitmap)
        try{$g.CopyFromScreen([int]$rect.Left,[int]$rect.Top,0,0,$bitmap.Size)}finally{$g.Dispose()}
        $bitmap.Save($Path,[Drawing.Imaging.ImageFormat]::Png)
    } finally {$bitmap.Dispose()}
}
function Select-Placement($Main,[string]$Placement) {
    Invoke-Control (Named $Main 'Change tab placement' ([Windows.Automation.ControlType]::Button))
    Wait-Until {
        $script:choice=Named ([Windows.Automation.AutomationElement]::RootElement) "Move tabs to $Placement" ([Windows.Automation.ControlType]::MenuItem)
        $null-ne$script:choice
    } "Placement choice $Placement did not appear." 5
    Invoke-Control $script:choice
    Wait-Until {
        $script:main=Top-Level $process.Id 'Orbit Navigator'
        $script:placement=Named $script:main 'Change tab placement' ([Windows.Automation.ControlType]::Button)
        $script:placement.Current.ItemStatus -eq "Tabs: $Placement"
    } "Placement $Placement was not authoritatively accepted."
    $script:main
}

$application=[IO.Path]::GetFullPath($ApplicationPath)
$applicationDll=[IO.Path]::ChangeExtension($application,'.dll')
$dotnet=[IO.Path]::GetFullPath($DotNetHost)
$profile=[IO.Path]::GetFullPath($ProfileRoot)
$acceptance=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if(-not$profile.StartsWith($acceptance+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe profile root.'}
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($EvidenceRoot))|Out-Null
if(Get-CimInstance Win32_Process|Where-Object{$_.CommandLine -and $_.CommandLine.Contains($applicationDll,[StringComparison]::OrdinalIgnoreCase)}){throw 'The selected Release output is already running.'}
$info=[Diagnostics.ProcessStartInfo]::new($dotnet)
$info.UseShellExecute=$false
$info.ArgumentList.Add($applicationDll)
$info.ArgumentList.Add('--acceptance-profile-root');$info.ArgumentList.Add($profile)
$info.ArgumentList.Add('--acceptance-run-id');$info.ArgumentList.Add([Guid]::NewGuid().ToString('D'))
$process=$null
try {
    $process=[Diagnostics.Process]::Start($info)
    Wait-Until {$script:main=Top-Level $process.Id 'Orbit Navigator';$null-ne$script:main-and$null-ne(Named $script:main 'Open new tab')} 'Release window did not become ready.'
    $main=$script:main
    for($i=2;$i-le6;$i++){Invoke-Control (Named $main 'Open new tab' ([Windows.Automation.ControlType]::Button));Wait-Until{$script:main=Top-Level $process.Id 'Orbit Navigator';@(Descendants $script:main|Where-Object{$_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -match ', tab$'}).Count-ge$i}"Tab $i did not appear.";$main=$script:main}
    $main=Select-Placement $main 'Left'
    $leftCommands=Controller-Commands $main $false
    $leftRows=Assert-Commands $leftCommands $main 'Left narrow rail' $true
    $leftCapture=Join-Path $EvidenceRoot 'release-left-narrow.png';Capture-Window $main $leftCapture
    $main=Select-Placement $main 'Right'
    $rightCommands=Controller-Commands $main $false
    $rightRows=Assert-Commands $rightCommands $main 'Right narrow rail' $true
    $rightCapture=Join-Path $EvidenceRoot 'release-right-narrow.png';Capture-Window $main $rightCapture
    Wait-Until {
        $script:main=Top-Level $process.Id 'Orbit Navigator'
        $script:popout=Named $script:main 'Pop out tab controller' ([Windows.Automation.ControlType]::Button)
        $null-ne$script:popout-and$script:popout.Current.IsEnabled
    } 'The pop-out command did not become enabled after placement serialization.'
    $popoutRect=$script:popout.Current.BoundingRectangle
    $hit=[Windows.Automation.AutomationElement]::FromPoint([Windows.Point]::new(
        $popoutRect.Left+$popoutRect.Width/2,$popoutRect.Top+$popoutRect.Height/2))
    $hitPath=[Collections.Generic.List[string]]::new()
    for($node=$hit;$null-ne$node-and$hitPath.Count-lt8;$node=[Windows.Automation.TreeWalker]::RawViewWalker.GetParent($node)){
        $hitPath.Add("$($node.Current.ControlType.ProgrammaticName):$($node.Current.Name)")
    }
    [void][OrbitNarrowControllerNative]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle)
    Click-Control $script:popout
    $detachDeadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        $script:tool=Native-Owned-Window $process.Id 'Tabs'
        if($null-ne$script:tool){break}
        Start-Sleep -Milliseconds 125
    } while([DateTime]::UtcNow-lt$detachDeadline)
    if($null-eq$script:tool){
        $status=Named (Top-Level $process.Id 'Orbit Navigator') 'Tab controller status'
        $windows=@([Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children,
            [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))|
            ForEach-Object{[pscustomobject]@{Name=$_.Current.Name;Offscreen=$_.Current.IsOffscreen;Enabled=$_.Current.IsEnabled;Bounds=$_.Current.BoundingRectangle}})
        throw "Detached native tab controller did not appear. HitPath=$($hitPath -join ' > ') Status=$($status.Current.Name)/$($status.Current.ItemStatus) Windows=$($windows|ConvertTo-Json -Compress)"
    }
    $tool=$script:tool;$h=[IntPtr]$tool.Current.NativeWindowHandle
    [void][OrbitNarrowControllerNative]::MoveWindow($h,120,120,360,400,$true)
    Wait-Until {$script:tool=Native-Owned-Window $process.Id 'Tabs';$r=$script:tool.Current.BoundingRectangle;$r.Width-ge359-and$r.Height-ge399} 'Detached controller did not reach its minimum host bounds.'
    $tool=$script:tool
    $detachedCommands=Controller-Commands $tool $true
    $detachedCapture=Join-Path $EvidenceRoot 'release-detached-min.png';Capture-Window $tool $detachedCapture
    $detachedRows=Assert-Commands $detachedCommands $tool 'Detached controller' $false
    [pscustomobject]@{Result='PASS';Left=[pscustomobject]@{Rows=$leftRows;Commands=$leftCommands;Capture=$leftCapture};Right=[pscustomobject]@{Rows=$rightRows;Commands=$rightCommands;Capture=$rightCapture};Detached=[pscustomobject]@{Rows=$detachedRows;Commands=$detachedCommands;Bounds=$tool.Current.BoundingRectangle;Capture=$detachedCapture};UserProcessTouched=$false}|ConvertTo-Json -Depth 8
}
finally {
    if($process-and-not$process.HasExited){[void]$process.CloseMainWindow();if(-not$process.WaitForExit(8000)){Stop-Process -Id $process.Id -Force}}
}
