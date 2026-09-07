[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidenceDirectory,
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$ErrorView = 'DetailedView'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitHoverAcceptanceNative
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
"@

function Wait-Until([scriptblock]$Condition, [string]$Failure, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { if (& $Condition) { return } } catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 125
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Get-MainRoot([int]$ProcessId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

function Find-Named([string]$Name, [System.Windows.Automation.ControlType]$Type = $null) {
    $conditions = [System.Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name))
    if ($null -ne $Type) {
        $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type))
    }
    $condition = if ($conditions.Count -eq 1) { $conditions[0] } else {
        [System.Windows.Automation.AndCondition]::new($conditions.ToArray())
    }
    [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-TabButtons([System.Windows.Automation.AutomationElement]$Root) {
    @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name -match ', tab$' })
}

function Get-GroupButton([System.Windows.Automation.AutomationElement]$Root, [int]$Count, [string]$State) {
    $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)) |
        Where-Object {
            -not $_.Current.IsOffscreen -and
            $_.Current.Name -eq "New group, $Count tabs" -and
            $_.Current.ItemStatus -eq ([Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($State))
        } |
        Select-Object -First 1
}

function Invoke-Element([System.Windows.Automation.AutomationElement]$Element) {
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Expand-Menu([string]$Name) {
    Wait-Until { $script:item = Find-Named $Name ([System.Windows.Automation.ControlType]::MenuItem); $null -ne $script:item } "Menu item was not shown: $Name" 5
    $pattern = $script:item.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $pattern.Expand()
    Start-Sleep -Milliseconds 250
}

function RightClick-Element([System.Windows.Automation.AutomationElement]$Element) {
    $bounds = $Element.Current.BoundingRectangle
    if ($bounds.Width -lt 1 -or $bounds.Height -lt 1) { throw 'Cannot right-click an empty UIA target.' }
    [void][OrbitHoverAcceptanceNative]::SetCursorPos(
        [int]($bounds.Left + ($bounds.Width / 2)), [int]($bounds.Top + ($bounds.Height / 2)))
    [OrbitHoverAcceptanceNative]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
    [OrbitHoverAcceptanceNative]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Capture-Window([System.Diagnostics.Process]$Process, [string]$Name) {
    $Process.Refresh()
    $rect = [System.Drawing.Rectangle]::FromLTRB(0, 0, 0, 0)
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitHoverCaptureNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
"@ -ErrorAction SilentlyContinue
    $native = [OrbitHoverCaptureNative+RECT]::new()
    if (-not [OrbitHoverCaptureNative]::GetWindowRect($Process.MainWindowHandle, [ref]$native)) {
        throw 'Could not capture the Orbit window bounds.'
    }
    $rect = [Drawing.Rectangle]::FromLTRB($native.Left, $native.Top, $native.Right, $native.Bottom)
    $bitmap = [Drawing.Bitmap]::new($rect.Width, $rect.Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($rect.Location, [Drawing.Point]::Empty, $rect.Size) }
        finally { $graphics.Dispose() }
        $path = Join-Path $EvidenceDirectory $Name
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        return $path
    } finally { $bitmap.Dispose() }
}

$install = [IO.Path]::GetFullPath($InstallRoot)
$launcher = Join-Path $install 'Orbit Navigator.exe'
$application = Join-Path $install 'app\OrbitNavigator.App.exe'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf) -or
    -not (Test-Path -LiteralPath $application -PathType Leaf)) { throw 'Disposable install is incomplete.' }
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$acceptanceParent = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptanceParent + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ProfileRoot is outside the disposable acceptance parent.'
}
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($EvidenceDirectory)) | Out-Null

$existing = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and
    [IO.Path]::GetFullPath($_.ExecutablePath) -eq $application
})
if ($existing.Count) { throw 'The disposable Orbit App is already running.' }

$process = $null
try {
    $runId = [Guid]::NewGuid()
    $startedAfter = [DateTime]::UtcNow.AddSeconds(-1)
    Start-Process -FilePath $launcher -ArgumentList @(
        '--acceptance-profile-root', $profile,
        '--acceptance-run-id', $runId.ToString('D')) | Out-Null
    $cim = $null
    Wait-Until {
        $script:cim = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath) -eq $application -and
            $_.CreationDate.ToUniversalTime() -ge $startedAfter
        } | Select-Object -First 1
        $null -ne $script:cim
    } 'Disposable Orbit App did not start.'
    $process = Get-Process -Id $cim.ProcessId
    $root = $null
    Wait-Until { $process.Refresh(); $script:root = Get-MainRoot $process.Id; $null -ne $script:root } 'Orbit main window was not exposed to UI Automation.'
    $newTab = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Open new tab'))
    if ($null -eq $newTab) { throw 'Open new tab was not visible.' }
    for ($index = 2; $index -le 6; $index++) {
        Invoke-Element $newTab
        Wait-Until { $script:root = Get-MainRoot $process.Id; (Get-TabButtons $root).Count -eq $index } "Tab $index was not created."
        $newTab = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'Open new tab'))
    }

    $tabs = Get-TabButtons $root
    RightClick-Element $tabs[0]
    Expand-Menu 'Tab group'
    $create = $null
    Wait-Until { $script:create = Find-Named 'Create new group with this tab' ([System.Windows.Automation.ControlType]::MenuItem); $null -ne $script:create } 'Create-group command was not exposed.' 5
    Invoke-Element $create
    Wait-Until { $script:root = Get-MainRoot $process.Id; $null -ne (Get-GroupButton $root 1 'expanded') } 'The first temporary group was not created.'

    foreach ($count in 2..4) {
        $tabs = $null
        Wait-Until { $script:root = Get-MainRoot $process.Id; $script:tabs = Get-TabButtons $script:root; $script:tabs.Count -ge 1 } 'No visible tab was available to add to the group.' 5
        $tabs = $script:tabs
        RightClick-Element $tabs[-1]
        Expand-Menu 'Tab group'
        Expand-Menu 'Move tab to group'
        $destination = $null
        Wait-Until { $script:destination = Find-Named 'New group' ([System.Windows.Automation.ControlType]::MenuItem); $null -ne $script:destination } 'The group destination was not exposed.' 5
        Invoke-Element $destination
        Wait-Until { $script:root = Get-MainRoot $process.Id; $null -ne (Get-GroupButton $root $count 'expanded') } "The temporary group did not reach $count tabs."
    }

    $group = Get-GroupButton $root 4 'expanded'
    Invoke-Element $group
    Wait-Until { $script:root = Get-MainRoot $process.Id; $script:group = Get-GroupButton $root 4 'collapsed'; $null -ne $script:group } 'The four-tab group did not collapse.'
    $bounds = $group.Current.BoundingRectangle
    [void][OrbitHoverAcceptanceNative]::SetCursorPos([int]($bounds.Left + $bounds.Width/2), [int]($bounds.Top + $bounds.Height/2))
    Wait-Until {
        $script:root = Get-MainRoot $process.Id
        @(Get-TabButtons $root | Where-Object { $_.Current.ItemStatus -eq 'Hover preview' }).Count -ge 1
    } 'Hover did not reveal the collapsed 1-4 tab group inline.' 5
    $smallCapture = Capture-Window $process 'row01-small-group-hover.png'

    [void][OrbitHoverAcceptanceNative]::SetCursorPos(900, 700)
    Start-Sleep -Milliseconds 400
    $tabs = $null
    Wait-Until { $script:root = Get-MainRoot $process.Id; $script:tabs = Get-TabButtons $script:root; $script:tabs.Count -ge 1 } 'No visible fifth tab was available to add to the group.' 5
    $tabs = $script:tabs
    RightClick-Element $tabs[-1]
    Expand-Menu 'Tab group'
    Expand-Menu 'Move tab to group'
    $destination = $null
    Wait-Until { $script:destination = Find-Named 'New group' ([System.Windows.Automation.ControlType]::MenuItem); $null -ne $script:destination } 'The group destination was not exposed for the fifth tab.' 5
    Invoke-Element $destination
    Wait-Until { $script:root = Get-MainRoot $process.Id; $script:group = Get-GroupButton $root 5 'collapsed'; $null -ne $script:group } 'The large group did not reach five collapsed tabs.'
    $bounds = $group.Current.BoundingRectangle
    [void][OrbitHoverAcceptanceNative]::SetCursorPos([int]($bounds.Left + $bounds.Width/2), [int]($bounds.Top + $bounds.Height/2))
    Wait-Until {
        $menus = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Menu))
        @($menus | Where-Object { -not $_.Current.IsOffscreen }).Count -ge 1
    } 'Hover did not open the canonical five-tab preview dropdown.' 5
    $largeCapture = Capture-Window $process 'row01-large-group-hover.png'

    [pscustomobject]@{
        CleanDefaultHover = $true
        SmallGroupInlinePreview = $true
        LargeGroupDropdownPreview = $true
        SmallCapture = $smallCapture
        LargeCapture = $largeCapture
        ProfileRoot = $profile
    } | ConvertTo-Json -Depth 4
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try {
            $root = Get-MainRoot $process.Id
            if ($null -ne $root) {
                $root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
            }
            if (-not $process.WaitForExit(8000)) { Stop-Process -Id $process.Id -Force }
        } catch { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force } }
    }
}
