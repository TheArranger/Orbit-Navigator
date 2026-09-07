[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidencePath,
    [int]$TimeoutSeconds = 75
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitUpgradedWorkspaceNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

function Wait-Until([scriptblock]$Condition,[string]$Failure,[int]$Seconds=$TimeoutSeconds) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { if(& $Condition){return} } catch [Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 150
    } while([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Root-For([int]$ProcessId) {
    $condition=[Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId)
    [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,$condition) |
        Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

function Find-Named($Root,[string]$Name,[Windows.Automation.ControlType]$Type=$null) {
    $Root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
        Where-Object {
            $_.Current.Name -eq $Name -and -not $_.Current.IsOffscreen -and
            ($null -eq $Type -or $_.Current.ControlType -eq $Type)
        } |
        Select-Object -First 1
}

function Invoke-Element($Element) {
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Capture($Root,[string]$Path) {
    $handle=[IntPtr]$Root.Current.NativeWindowHandle
    [void][OrbitUpgradedWorkspaceNative]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 400
    $native=[OrbitUpgradedWorkspaceNative+RECT]::new()
    if(-not[OrbitUpgradedWorkspaceNative]::GetWindowRect($handle,[ref]$native)){throw 'Window capture bounds unavailable.'}
    $rect=[Drawing.Rectangle]::FromLTRB($native.Left,$native.Top,$native.Right,$native.Bottom)
    $bitmap=[Drawing.Bitmap]::new($rect.Width,$rect.Height)
    try {
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        try {$graphics.CopyFromScreen($rect.Location,[Drawing.Point]::Empty,$rect.Size)} finally {$graphics.Dispose()}
        $full=[IO.Path]::GetFullPath($Path)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))|Out-Null
        $bitmap.Save($full,[Drawing.Imaging.ImageFormat]::Png)
        $full
    } finally {$bitmap.Dispose()}
}

function Read-Session([string]$Profile) {
    $file=Get-ChildItem -LiteralPath (Join-Path $Profile 'profiles\data') -Recurse -File |
        Where-Object FullName -Like '*browser.workspace-session*' | Select-Object -First 1
    if(-not$file){throw 'Authoritative upgraded session file is unavailable.'}
    $bytes=[IO.File]::ReadAllBytes($file.FullName)
    [Text.Encoding]::UTF8.GetString($bytes,16,$bytes.Length-16)|ConvertFrom-Json
}

$install=[IO.Path]::GetFullPath($InstallRoot)
$profile=[IO.Path]::GetFullPath($ProfileRoot)
$allowed=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if(-not$profile.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe disposable upgraded profile.'}
$launcher=Join-Path $install 'Orbit Navigator.exe'
$application=[IO.Path]::GetFullPath((Join-Path $install 'app\OrbitNavigator.App.exe'))
$before=Read-Session $profile
$beforeIds=@($before.Tabs|ForEach-Object{$_.TabId.Value})
$process=$null
try {
    $runId=[Guid]::NewGuid()
    $started=[DateTimeOffset]::UtcNow.AddSeconds(-1)
    Start-Process -FilePath $launcher -ArgumentList @(
        '--acceptance-profile-root',$profile,
        '--acceptance-run-id',$runId.ToString('D'))|Out-Null
    $cim=$null
    Wait-Until {
        $script:cim=Get-CimInstance Win32_Process|Where-Object{
            $_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).Equals($application,[StringComparison]::OrdinalIgnoreCase) -and
            $_.CreationDate.ToUniversalTime() -ge $started.UtcDateTime
        }|Select-Object -First 1
        $null-ne$script:cim
    } 'Disposable upgraded Orbit process did not start.'
    $process=Get-Process -Id $script:cim.ProcessId
    $attestationPath=Join-Path $profile 'acceptance-root-attestation.json'
    Wait-Until {
        if(-not(Test-Path -LiteralPath $attestationPath)){return $false}
        $candidate=Get-Content -LiteralPath $attestationPath -Raw|ConvertFrom-Json
        $candidate.RunId -eq $runId.ToString('D') -and [int]$candidate.ProcessId -eq $process.Id -and
        [IO.Path]::GetFullPath([string]$candidate.Root).Equals($profile,[StringComparison]::OrdinalIgnoreCase)
    } 'Upgraded workspace launch was not attested to its disposable root.'
    $root=$null
    Wait-Until {$script:root=Root-For $process.Id;$null-ne$script:root -and $null-ne(Find-Named $script:root 'Show list view')} 'Upgraded New Tab workspace surface did not become ready.'
    [void][OrbitUpgradedWorkspaceNative]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle)
    $list=Find-Named $root 'Show list view' ([Windows.Automation.ControlType]::Button)
    if($list){Invoke-Element $list;Start-Sleep -Milliseconds 400;$root=Root-For $process.Id}
    $open=$null
    Wait-Until {$script:root=Root-For $process.Id;$script:open=Find-Named $script:root 'Open workspace Legacy My Orbit Workspace, 1 tabs' ([Windows.Automation.ControlType]::Button);$null-ne$script:open} 'Migrated My Orbit workspace was not visible.'
    Invoke-Element $script:open
    Wait-Until {$script:root=Root-For $process.Id;$null-ne(Find-Named $script:root 'Legacy Research, 1 tabs' ([Windows.Automation.ControlType]::Button))} 'Migrated workspace did not open as a collapsed live group.'
    $capture=Capture $script:root $EvidencePath
}
finally {
    if($process -and -not$process.HasExited){
        $root=Root-For $process.Id
        if($root){$root.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()}
        if(-not$process.WaitForExit(8000)){Stop-Process -Id $process.Id -Force}
    }
}

$after=Read-Session $profile
$afterIds=@($after.Tabs|ForEach-Object{$_.TabId.Value})
$missing=@($beforeIds|Where-Object{$_ -notin $afterIds})
if($missing.Count-ne0){throw 'Migrated workspace open removed existing upgraded-profile tabs.'}
$group=@($after.Groups)|Where-Object Name -eq 'Legacy Research'|Select-Object -First 1
if(-not$group -or -not$group.IsCollapsed -or @($group.TabOrder).Count-ne1){throw 'Migrated workspace group was not persisted as one collapsed live group.'}
$selected=@($after.Tabs)|Where-Object{$_.TabId.Value-eq$after.SelectedTabId.Value}|Select-Object -First 1
if(-not$selected -or $selected.Address-ne'https://my-orbit.snap-it.cc/path?q=1' -or $selected.GroupId.Value-ne$group.GroupId.Value){throw 'Migrated workspace did not select its configured first site.'}
[pscustomobject]@{
    AttestedRunId=$runId.ToString('D')
    AttestedProcessId=$process.Id
    ExistingTabsPreserved=$true
    PreOpenTabCount=$beforeIds.Count
    PostOpenTabCount=$afterIds.Count
    NewCollapsedGroup='Legacy Research'
    FirstSiteSelected=$selected.Address
    EvidencePath=$capture
}|ConvertTo-Json -Depth 4
