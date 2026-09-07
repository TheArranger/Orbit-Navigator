[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [string]$GroupName = 'Retained Legacy Group'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitUpgradedPreviewNative {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
}
"@

function Wait-Until([scriptblock]$Condition,[string]$Failure,[int]$Seconds=30) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do { try { if(& $Condition){return} } catch [Windows.Automation.ElementNotAvailableException]{}; Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Find-Name($Root,[string]$Name) {
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name))
}

$install=[IO.Path]::GetFullPath($InstallRoot)
$profile=[IO.Path]::GetFullPath($ProfileRoot)
$allowed=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if(-not $profile.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe profile root.'}
$app=[IO.Path]::GetFullPath((Join-Path $install 'app\OrbitNavigator.App.exe'))
if(Get-CimInstance Win32_Process|Where-Object{$_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $app}){throw 'Unexpected prior disposable Orbit process.'}
$runId=[Guid]::NewGuid()
Start-Process (Join-Path $install 'Orbit Navigator.exe') -ArgumentList @('--acceptance-profile-root',$profile,'--acceptance-run-id',$runId.ToString('D'))|Out-Null
$process=$null
try {
    Wait-Until {
        $candidate=Get-CimInstance Win32_Process|Where-Object{$_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $app}|Select-Object -First 1
        if($candidate){$script:process=Get-Process -Id $candidate.ProcessId}
        $null -ne $script:process
    } 'Disposable App did not start.'
    $main=$null
    Wait-Until {
        $process.Refresh()
        if($process.HasExited){throw "Disposable App exited before UIA: $($process.ExitCode)"}
        if($process.MainWindowHandle -ne [IntPtr]::Zero){
            $script:main=[Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        }
        $null -ne $script:main -and $script:main.Current.Name -eq 'Orbit Navigator'
    } 'Orbit main window was not available.'
    $layout=$null
    Wait-Until {$script:main=[Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle);$script:layout=Find-Name $main 'Change tab placement';$null -ne $script:layout} 'Placement menu unavailable.'
    $layout.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $click=$null
    Wait-Until {
        $script:click=[Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Reveal large-group previews on click'))
        $null -ne $script:click
    } 'Click preference was unavailable.' 5
    $togglePattern=$null
    $selectionPattern=$null
    $toggleSelected=$click.TryGetCurrentPattern(
        [Windows.Automation.TogglePattern]::Pattern,[ref]$togglePattern) -and
        $togglePattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On
    $selectionSelected=$click.TryGetCurrentPattern(
        [Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selectionPattern) -and
        $selectionPattern.Current.IsSelected
    $clickSelected=$click.Current.ItemStatus -eq 'Selected' -or
        $toggleSelected -or $selectionSelected
    [pscustomobject]@{
        Name=$click.Current.Name
        ItemStatus=$click.Current.ItemStatus
        ToggleSelected=$toggleSelected
        SelectionSelected=$selectionSelected
        IsEnabled=$click.Current.IsEnabled
    }|ConvertTo-Json|Set-Content (Join-Path $EvidenceRoot 'upgraded-click-menu-state.json') -Encoding utf8
    if(-not$clickSelected){throw 'Click preview mode was not authoritatively selected after relaunch.'}
    [OrbitUpgradedPreviewNative]::keybd_event(0x1B,0,0,[UIntPtr]::Zero)
    [OrbitUpgradedPreviewNative]::keybd_event(0x1B,0,2,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    $main=[Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $group=Find-Name $main "$GroupName, 5 tabs"
    if(-not $group){throw 'Upgraded five-tab group missing.'}
    $bounds=$group.Current.BoundingRectangle
    [OrbitUpgradedPreviewNative]::SetCursorPos([int]($bounds.Left+$bounds.Width/2),[int]($bounds.Top+$bounds.Height/2))|Out-Null
    Start-Sleep -Milliseconds 1400
    $menus=[Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Menu))
    $visible=@($menus|Where-Object{-not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 1})
    if($visible.Count -ne 0){throw 'Click mode still opened the group preview on hover.'}
    $rect=[OrbitUpgradedPreviewNative+RECT]::new()
    if(-not[OrbitUpgradedPreviewNative]::GetWindowRect($process.MainWindowHandle,[ref]$rect)){throw 'No capture bounds.'}
    $bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    try{$graphics=[Drawing.Graphics]::FromImage($bitmap);try{$graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)}finally{$graphics.Dispose()};[IO.Directory]::CreateDirectory($EvidenceRoot)|Out-Null;$capture=Join-Path $EvidenceRoot 'upgraded-click-authoritative.png';$bitmap.Save($capture,[Drawing.Imaging.ImageFormat]::Png)}finally{$bitmap.Dispose()}
    [pscustomobject]@{Result='PASS';ClickSelectedAfterRelaunch=$clickSelected;HoverSuppressed=$true;Group=$GroupName;ProfileRoot=$profile;Capture=$capture;RunId=$runId.ToString('D')}|ConvertTo-Json
}
finally {
    if($process -and -not $process.HasExited){[void]$process.CloseMainWindow();if(-not$process.WaitForExit(10000)){Stop-Process -Id $process.Id -Force}}
}
