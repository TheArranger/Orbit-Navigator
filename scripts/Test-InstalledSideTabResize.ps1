param(
    [Parameter(Mandatory = $true)][string]$LauncherPath,
    [Parameter(Mandatory = $true)][string]$ProfileRoot,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OrbitSideRailNative
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    public static IntPtr FindVisibleWindow(uint processId, string titleSuffix)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((hwnd, _) => {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != processId || !IsWindowVisible(hwnd)) return true;
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            if (!title.ToString().EndsWith(titleSuffix, StringComparison.Ordinal)) return true;
            match = hwnd;
            return false;
        }, IntPtr.Zero);
        return match;
    }
}
'@

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Wait-Until([scriptblock]$condition, [string]$message) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try { if (& $condition) { return } } catch [System.Windows.Automation.ElementNotAvailableException] {}
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $message
}

function Find-Element($root, [string]$name, [bool]$required = $true) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($required -and $null -eq $element) { throw "UI element '$name' was not found." }
    return $element
}

function Find-PrefixElement($root, [string]$prefix) {
    $all = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        if ($element.Current.Name.StartsWith($prefix, [StringComparison]::Ordinal)) { return $element }
    }
    return $null
}

function Invoke-Element($element) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Click-Element($element) {
    $bounds = $element.Current.BoundingRectangle
    Assert-Condition ($bounds.Width -gt 1 -and $bounds.Height -gt 1) `
        "Cannot click '$($element.Current.Name)' because its bounds are empty."
    [OrbitSideRailNative]::SetCursorPos(
        [int]($bounds.Left + ($bounds.Width / 2)),
        [int]($bounds.Top + ($bounds.Height / 2))) | Out-Null
    [OrbitSideRailNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [OrbitSideRailNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Get-MainWindow {
    if ($null -eq $script:process -or $script:process.HasExited) { return $null }
    $script:process.Refresh()
    if ($script:process.MainWindowHandle -eq 0) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle)
}

function Get-ToolWindow {
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($window in $windows) {
        if ($window.Current.ProcessId -eq $script:process.Id -and
            $window.Current.Name.EndsWith('Tabs', [StringComparison]::Ordinal)) {
            return $window
        }
    }
    $handle = [OrbitSideRailNative]::FindVisibleWindow([uint32]$script:process.Id, 'Tabs')
    if ($handle -eq [IntPtr]::Zero) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($handle)
}

function Start-Orbit([Guid]$runId) {
    Start-Process -FilePath $LauncherPath -ArgumentList @(
        '--acceptance-profile-root', $ProfileRoot,
        '--acceptance-run-id', $runId.ToString('D')) | Out-Null
    $script:process = $null
    Wait-Until {
        $script:process = Get-Process -Name 'OrbitNavigator.App' -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
        $null -ne $script:process
    } 'Orbit Navigator did not create a visible main window.'
    Wait-Until {
        $root = Get-MainWindow
        $startup = if ($null -ne $root) { Find-Element $root 'Orbit Navigator startup' $false } else { $null }
        $null -ne $root -and ($null -eq $startup -or $startup.Current.IsOffscreen)
    } 'Orbit Navigator startup did not complete.'
}

function Stop-Orbit {
    if ($null -ne $script:process -and -not $script:process.HasExited) {
        $null = $script:process.CloseMainWindow()
        if (-not $script:process.WaitForExit(5000)) { Stop-Process -Id $script:process.Id -Force }
    }
    $script:process = $null
    Wait-Until { $null -eq (Get-Process -Name 'OrbitNavigator.App' -ErrorAction SilentlyContinue) } `
        'Orbit Navigator did not terminate after the smoke run.'
}

function Set-Placement([ValidateSet('Top','Left','Right')][string]$placement) {
    $root = Get-MainWindow
    Invoke-Element (Find-Element $root 'Change tab placement')
    $names = @("Move tabs to $placement", "Tabs: $placement")
    Wait-Until {
        foreach ($name in $names) {
            $item = Find-Element ([System.Windows.Automation.AutomationElement]::RootElement) $name $false
            if ($null -ne $item -and $item.Current.ProcessId -eq $script:process.Id) {
                Invoke-Element $item
                return $true
            }
        }
        return $false
    } "The $placement placement command was not available."
    Start-Sleep -Milliseconds 400
}

function Get-CommandElements($root) {
    $more = Find-PrefixElement $root 'More tabs'
    return @(
        $more,
        (Find-Element $root 'Open new tab'),
        (Find-Element $root 'Browser resources'),
        (Find-Element $root 'Pop out tab controller')
    )
}

function Get-VisibleTabColumns($root) {
    $all = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $tabs = @($all | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $_.Current.Name.EndsWith(', tab', [StringComparison]::Ordinal) -and
        -not $_.Current.IsOffscreen -and
        $_.Current.BoundingRectangle.Width -gt 1
    })
    return [pscustomobject]@{
        Tabs = $tabs
        Columns = @($tabs | ForEach-Object {
            [Math]::Round($_.Current.BoundingRectangle.Left, 0)
        } | Sort-Object -Unique)
    }
}

function Create-TestTabs([int]$count) {
    for ($index = 0; $index -lt $count; $index++) {
        $script:seedNewTab = $null
        Wait-Until {
            $root = Get-MainWindow
            if ($null -eq $root) { return $false }
            $script:seedNewTab = Find-Element $root 'Open new tab' $false
            $null -ne $script:seedNewTab
        } "Open new tab did not return after seeded tab $index."
        Invoke-Element $script:seedNewTab
        Start-Sleep -Milliseconds 150
    }
    Wait-Until {
        $more = Find-PrefixElement (Get-MainWindow) 'More tabs —'
        $null -ne $more
    } 'The seeded tabs did not produce an overflow projection.'
}

function Assert-SideRail([ValidateSet('Left','Right')][string]$placement) {
    $root = Get-MainWindow
    $windowBounds = $root.Current.BoundingRectangle
    $handle = Find-Element $root 'Resize tab panel'
    Assert-Condition (-not $handle.Current.IsOffscreen) "$placement resize handle is offscreen."
    Assert-Condition ($handle.Current.HelpText -like '*Drag*') "$placement resize handle lacks accessible drag guidance."
    $commands = Get-CommandElements $root
    foreach ($command in $commands) {
        $bounds = $command.Current.BoundingRectangle
        Assert-Condition (-not $command.Current.IsOffscreen) "$placement command '$($command.Current.Name)' is offscreen."
        Assert-Condition ($bounds.Width -ge 44 -and $bounds.Height -ge 44) `
            "$placement command '$($command.Current.Name)' is smaller than 44 by 44 pixels."
        Assert-Condition ($bounds.Left -ge $windowBounds.Left -and $bounds.Right -le $windowBounds.Right -and
            $bounds.Top -ge $windowBounds.Top -and $bounds.Bottom -le $windowBounds.Bottom) `
            "$placement command '$($command.Current.Name)' is clipped by the window."
    }
    for ($i = 0; $i -lt $commands.Count; $i++) {
        for ($j = $i + 1; $j -lt $commands.Count; $j++) {
            $firstBounds = $commands[$i].Current.BoundingRectangle
            $secondBounds = $commands[$j].Current.BoundingRectangle
            $intersectionWidth = [Math]::Min($firstBounds.Right, $secondBounds.Right) -
                [Math]::Max($firstBounds.Left, $secondBounds.Left)
            $intersectionHeight = [Math]::Min($firstBounds.Bottom, $secondBounds.Bottom) -
                [Math]::Max($firstBounds.Top, $secondBounds.Top)
            Assert-Condition (-not ($intersectionWidth -gt 0.5 -and $intersectionHeight -gt 0.5)) `
                "$placement commands overlap: '$($commands[$i].Current.Name)' $firstBounds and '$($commands[$j].Current.Name)' $secondBounds."
        }
    }
    return [pscustomobject]@{
        Placement = $placement
        Window = $windowBounds.ToString()
        Handle = $handle.Current.BoundingRectangle.ToString()
        Commands = @($commands | ForEach-Object {
            [pscustomobject]@{ Name = $_.Current.Name; Bounds = $_.Current.BoundingRectangle.ToString() }
        })
    }
}

function Drag-ResizeHandle([ValidateSet('Left','Right')][string]$placement, [int]$delta) {
    $root = Get-MainWindow
    $handle = Find-Element $root 'Resize tab panel'
    $before = $handle.Current.BoundingRectangle
    $x = [int]($before.Left + ($before.Width / 2))
    $y = [int]($before.Top + [Math]::Min(120, $before.Height / 2))
    [OrbitSideRailNative]::SetCursorPos($x, $y) | Out-Null
    [OrbitSideRailNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [OrbitSideRailNative]::SetCursorPos($x + $delta, $y) | Out-Null
    Start-Sleep -Milliseconds 200
    [OrbitSideRailNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    $after = (Find-Element (Get-MainWindow) 'Resize tab panel').Current.BoundingRectangle
    $actualDelta = $after.Left - $before.Left
    if ($placement -eq 'Left') {
        Assert-Condition ($actualDelta -gt 40) 'Dragging the left resize handle did not widen the tab panel.'
    } else {
        $effectiveWidth = (Get-MainWindow).Current.BoundingRectangle.Right - $after.Left
        Assert-Condition ($actualDelta -lt -40 -or $effectiveWidth -ge 470) `
            'Dragging the right resize handle neither widened the tab panel nor retained its maximum width.'
    }
    return [pscustomobject]@{ Before = $before.ToString(); After = $after.ToString(); DeltaX = $actualDelta }
}

function Save-Screenshot([string]$path) {
    Save-ElementScreenshot (Get-MainWindow) $path
}

function Save-ElementScreenshot($element, [string]$path) {
    $bounds = $element.Current.BoundingRectangle
    $bitmap = [Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen([int]$bounds.Left, [int]$bounds.Top, 0, 0, $bitmap.Size) }
        finally { $graphics.Dispose() }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

$resolvedLauncher = [IO.Path]::GetFullPath($LauncherPath)
$resolvedProfile = [IO.Path]::GetFullPath($ProfileRoot)
$resolvedEvidence = [IO.Path]::GetFullPath($EvidenceRoot)
Assert-Condition ([IO.File]::Exists($resolvedLauncher)) 'Installed launcher does not exist.'
Assert-Condition ($resolvedProfile.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) `
    'The smoke profile must be inside the temporary directory.'
Assert-Condition ($resolvedEvidence.StartsWith([IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\verification')), [StringComparison]::OrdinalIgnoreCase)) `
    'Evidence must be written beneath the project verification root.'
New-Item -ItemType Directory -Path $resolvedProfile -Force | Out-Null
New-Item -ItemType Directory -Path $resolvedEvidence -Force | Out-Null
Assert-Condition ($null -eq (Get-Process -Name 'OrbitNavigator.App' -ErrorAction SilentlyContinue)) `
    'Close Orbit Navigator before running the installed side-tab smoke.'

$runId = [Guid]::NewGuid()
$result = $null
try {
    Start-Orbit $runId
    Create-TestTabs 23
    $mainTransform = (Get-MainWindow).GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    Assert-Condition $mainTransform.Current.CanResize 'The main browser window is not resizable.'
    $mainTransform.Resize(1200, 600)
    Start-Sleep -Milliseconds 400

    Invoke-Element (Find-Element (Get-MainWindow) 'Pop out tab controller')
    Wait-Until { $null -ne (Get-ToolWindow) } 'The detached tab controller did not open.'
    $detached = Get-ToolWindow
    $detachedDefaultColumns = Get-VisibleTabColumns $detached
    Assert-Condition ($detachedDefaultColumns.Columns.Count -ge 3) `
        'The default detached controller did not form three tab columns.'
    Save-ElementScreenshot $detached (Join-Path $resolvedEvidence 'installed-detached-three-columns.png')
    $toolTransform = $detached.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    Assert-Condition $toolTransform.Current.CanResize 'The detached tab controller is not resizable.'
    $toolTransform.Resize(360, 560)
    Start-Sleep -Milliseconds 400
    $detachedNarrowColumns = Get-VisibleTabColumns (Get-ToolWindow)
    Assert-Condition ($detachedNarrowColumns.Columns.Count -eq 2) `
        'The narrowed detached controller did not adapt to exactly two tab columns.'
    Save-ElementScreenshot (Get-ToolWindow) (Join-Path $resolvedEvidence 'installed-detached-two-columns.png')
    Invoke-Element (Find-Element (Get-ToolWindow) 'Dock tab controller')
    Wait-Until { $null -eq (Get-ToolWindow) } 'The detached controller did not dock after verification.'

    Set-Placement 'Left'
    $left = Assert-SideRail 'Left'
    $leftDrag = Drag-ResizeHandle 'Left' 170
    $leftAfter = Assert-SideRail 'Left'
    $leftColumns = Get-VisibleTabColumns (Get-MainWindow)
    Assert-Condition ($leftColumns.Columns.Count -ge 2) `
        'Widening the left tab panel did not create at least two visible tab columns.'
    Save-Screenshot (Join-Path $resolvedEvidence 'installed-left-resized.png')

    Set-Placement 'Right'
    $right = Assert-SideRail 'Right'
    $rightDrag = Drag-ResizeHandle 'Right' -200
    $rightAfter = Assert-SideRail 'Right'
    $rightColumns = Get-VisibleTabColumns (Get-MainWindow)
    Assert-Condition ($rightColumns.Columns.Count -ge 3) `
        'Widening the right tab panel did not create at least three visible tab columns.'
    $persistedHandle = (Find-Element (Get-MainWindow) 'Resize tab panel').Current.BoundingRectangle
    $persistedWindow = (Get-MainWindow).Current.BoundingRectangle
    $persistedWidth = $persistedWindow.Right - $persistedHandle.Left
    Save-Screenshot (Join-Path $resolvedEvidence 'installed-right-resized.png')

    Set-Placement 'Top'
    Assert-Condition ($null -eq (Find-Element (Get-MainWindow) 'Resize tab panel' $false) -or
        (Find-Element (Get-MainWindow) 'Resize tab panel' $false).Current.IsOffscreen) `
        'The side-tab resize handle remained visible in Top placement.'
    Set-Placement 'Right'
    Stop-Orbit

    Start-Orbit $runId
    Wait-Until {
        $root = Get-MainWindow
        $null -ne $root -and $null -ne (Find-Element $root 'Resize tab panel' $false)
    } 'The persisted side-tab resize handle did not appear after relaunch.'
    $reloaded = Assert-SideRail 'Right'
    $reloadedHandle = (Find-Element (Get-MainWindow) 'Resize tab panel').Current.BoundingRectangle
    $reloadedWindow = (Get-MainWindow).Current.BoundingRectangle
    $reloadedWidth = $reloadedWindow.Right - $reloadedHandle.Left
    Assert-Condition ([Math]::Abs($reloadedWidth - $persistedWidth) -le 3) `
        'The resized side-tab width did not persist across a same-profile relaunch.'

    $result = [pscustomobject]@{
        Result = 'PASS'
        RunId = $runId
        ProfileRoot = $resolvedProfile
        Left = $left
        LeftDrag = $leftDrag
        LeftAfter = $leftAfter
        Right = $right
        RightDrag = $rightDrag
        RightAfter = $rightAfter
        LeftVisibleColumns = $leftColumns.Columns.Count
        RightVisibleColumns = $rightColumns.Columns.Count
        DetachedDefaultVisibleColumns = $detachedDefaultColumns.Columns.Count
        DetachedNarrowVisibleColumns = $detachedNarrowColumns.Columns.Count
        ReloadedRight = $reloaded
        PersistedHandle = $persistedHandle.ToString()
        ReloadedHandle = $reloadedHandle.ToString()
        PersistedPanelWidth = $persistedWidth
        ReloadedPanelWidth = $reloadedWidth
    }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $resolvedEvidence 'installed-side-tab-resize.json') -Encoding utf8
    $result | ConvertTo-Json -Depth 8
}
finally {
    Stop-Orbit
}
