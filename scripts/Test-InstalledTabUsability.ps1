[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Orbit Navigator"),
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OrbitInstalledWindowEnumeration
{
    public delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    public static IntPtr FindVisibleWindow(uint processId, string expectedTitleSuffix)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((window, state) =>
        {
            uint candidateProcessId;
            GetWindowThreadProcessId(window, out candidateProcessId);
            if (candidateProcessId != processId || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString().EndsWith(expectedTitleSuffix, StringComparison.Ordinal))
            {
                match = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return match;
    }
}
"@

$launcher = Join-Path $InstallRoot "Orbit Navigator.exe"
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$acceptanceRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptanceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The tab-usability profile must be inside the disposable acceptance root.'
}
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Installed launcher was not found: $launcher"
}

function Assert-OrbitCondition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Wait-OrbitCondition {
    param([scriptblock]$Condition, [string]$FailureMessage)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            if (& $Condition) {
                return
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
            # WPF replaces tab cards when an authoritative projection arrives.
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $FailureMessage
}

function Find-OrbitElement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [bool]$Required = $true
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($Required -and $null -eq $element) {
        throw "UI Automation element was not found: $Name"
    }
    return $element
}

function Invoke-OrbitElement {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Focus-OrbitElement {
    param([System.Windows.Automation.AutomationElement]$Element)
    $shell = New-Object -ComObject WScript.Shell
    Assert-OrbitCondition ($shell.AppActivate($script:appProcess.Id)) "Could not activate the installed Orbit Navigator window."
    Start-Sleep -Milliseconds 150
    $Element.SetFocus()
}

function Get-OrbitWindows {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $all = $desktop.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $windows = @()
    for ($index = 0; $index -lt $all.Count; $index++) {
        $candidate = $all.Item($index)
        if ($candidate.Current.ProcessId -eq $script:appProcess.Id) {
            $windows += $candidate
        }
    }
    return $windows
}

function Get-OrbitMainWindow {
    $window = Get-OrbitWindows | Where-Object { $_.Current.Name -eq "Orbit Navigator" } | Select-Object -First 1
    if ($null -eq $window) {
        throw "The installed Orbit Navigator main window is unavailable."
    }
    return $window
}

function Get-OrbitNamedWindow {
    param([string]$TitleSuffix)
    $window = Get-OrbitWindows | Where-Object {
        $_.Current.Name.EndsWith($TitleSuffix, [StringComparison]::Ordinal)
    } | Select-Object -First 1
    if ($null -ne $window) {
        return $window
    }

    $windowHandle = [OrbitInstalledWindowEnumeration]::FindVisibleWindow(
        [uint32]$script:appProcess.Id,
        $TitleSuffix)
    if ($windowHandle -eq [IntPtr]::Zero) {
        return $null
    }
    return [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
}

function Get-OrbitToolWindow {
    return Get-OrbitNamedWindow "Tabs"
}

function Get-OrbitResourceWindow {
    return Get-OrbitNamedWindow "Browser resources"
}

function Get-OrbitController {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [bool]$Detached
    )
    $name = if ($Detached) { "Detached tab controller" } else { "Browser tab controller" }
    return Find-OrbitElement $Root $name
}

function Get-OrbitElements {
    param([System.Windows.Automation.AutomationElement]$Root)
    return $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
}

function Get-OrbitTabButtons {
    param([System.Windows.Automation.AutomationElement]$Root)
    $all = Get-OrbitElements $Root
    $tabs = @()
    for ($index = 0; $index -lt $all.Count; $index++) {
        $candidate = $all.Item($index)
        if ($candidate.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $candidate.Current.Name -like "*, tab" -and
            -not $candidate.Current.IsOffscreen) {
            $tabs += $candidate
        }
    }
    return $tabs
}

function Get-OrbitTabCount {
    param([System.Windows.Automation.AutomationElement]$Root)
    $visible = (Get-OrbitTabButtons $Root).Count
    $all = Get-OrbitElements $Root
    for ($index = 0; $index -lt $all.Count; $index++) {
        $name = $all.Item($index).Current.Name
        if ($name -match '^More tabs.+?(\d+) hidden$') {
            return $visible + [int]$Matches[1]
        }
    }
    return $visible
}

function Find-OrbitShortcutButton {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [string]$Accelerator
    )
    $all = Get-OrbitElements $Root
    for ($index = 0; $index -lt $all.Count; $index++) {
        $candidate = $all.Item($index)
        if ($candidate.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $candidate.Current.Name -eq $Name -and
            $candidate.Current.AcceleratorKey -eq $Accelerator -and
            -not $candidate.Current.IsOffscreen) {
            return $candidate
        }
    }
    throw "Visible shortcut button was not found: $Name ($Accelerator)"
}

function Get-OrbitSelectedTab {
    param([System.Windows.Automation.AutomationElement]$Root)
    return Get-OrbitTabButtons $Root |
        Where-Object { $_.Current.HelpText.StartsWith("Selected. ", [StringComparison]::Ordinal) } |
        Select-Object -First 1
}

function Get-OrbitCloseButtons {
    param([System.Windows.Automation.AutomationElement]$Root)
    $all = Get-OrbitElements $Root
    $buttons = @()
    for ($index = 0; $index -lt $all.Count; $index++) {
        $candidate = $all.Item($index)
        if ($candidate.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $candidate.Current.Name.StartsWith("Close tab", [StringComparison]::Ordinal) -and
            $candidate.Current.AcceleratorKey -eq "Ctrl+W" -and
            -not $candidate.Current.IsOffscreen) {
            $buttons += $candidate
        }
    }
    return $buttons
}

function Send-OrbitShortcut {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Keys
    )
    $selected = Get-OrbitSelectedTab $Root
    $shell = New-Object -ComObject WScript.Shell
    Assert-OrbitCondition ($shell.AppActivate($script:appProcess.Id)) "Could not activate the installed Orbit Navigator window."
    Start-Sleep -Milliseconds 150
    if ($null -ne $selected) {
        $selected.SetFocus()
        Start-Sleep -Milliseconds 100
    }
    $virtualKey = switch ($Keys) {
        "^t" { [byte]0x54 }
        "^w" { [byte]0x57 }
        default { throw "Unsupported installed-smoke shortcut: $Keys" }
    }
    [OrbitInstalledWindowEnumeration]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [OrbitInstalledWindowEnumeration]::keybd_event($virtualKey, 0, 0, [UIntPtr]::Zero)
    [OrbitInstalledWindowEnumeration]::keybd_event($virtualKey, 0, 2, [UIntPtr]::Zero)
    [OrbitInstalledWindowEnumeration]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
}

function Assert-OrbitFocusRestored {
    param([string]$Stage)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastName = "<unavailable>"
    $lastProcessId = 0
    do {
        try {
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            $lastName = $focused.Current.Name
            $lastProcessId = $focused.Current.ProcessId
            if ($lastProcessId -eq $script:appProcess.Id -and $lastName -like "*, tab") {
                return
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Stage focus was not restored to the selected tab after close. Last focus: '$lastName' (process $lastProcessId)."
}

function Set-OrbitPlacement {
    param([ValidateSet("top", "left", "right")][string]$Placement)
    $main = Get-OrbitMainWindow
    Invoke-OrbitElement (Find-OrbitElement $main "Change tab placement")
    $placementName = [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($Placement)
    $labels = @("Move tabs to $placementName", "Tabs: $placementName")
    Wait-OrbitCondition {
        $item = $labels | ForEach-Object {
            Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) $_ $false
        } | Where-Object { $null -ne $_ } | Select-Object -First 1
        if ($null -eq $item -or $item.Current.ProcessId -ne $script:appProcess.Id) {
            return $false
        }
        Invoke-OrbitElement $item
        return $true
    } "The $($labels -join ' / ') menu item did not open."
    Start-Sleep -Milliseconds 300
}

function Test-OrbitTabSurface {
    param(
        [string]$Label,
        [scriptblock]$RootProvider
    )
    $root = & $RootProvider
    Wait-OrbitCondition {
        try {
            return $null -ne (Find-OrbitShortcutButton (& $RootProvider) "Open new tab" "Ctrl+T")
        }
        catch {
            return $false
        }
    } "$Label New Tab command did not become visible."
    $root = & $RootProvider
    $newTab = Find-OrbitShortcutButton $root "Open new tab" "Ctrl+T"
    $bounds = $newTab.Current.BoundingRectangle
    Assert-OrbitCondition (-not $newTab.Current.IsOffscreen -and $newTab.Current.IsEnabled) "$Label New Tab is not visible and enabled."
    Assert-OrbitCondition ($bounds.Width -ge 44 -and $bounds.Height -ge 44) "$Label New Tab is smaller than 44 by 44 pixels."
    Assert-OrbitCondition ($newTab.Current.AcceleratorKey -eq "Ctrl+T") "$Label New Tab does not expose Ctrl+T."

    $initial = Get-OrbitTabCount $root
    Focus-OrbitElement $newTab
    Invoke-OrbitElement $newTab
    Wait-OrbitCondition { (Get-OrbitTabCount (& $RootProvider)) -eq ($initial + 1) } "$Label New Tab click did not create a tab."
    $root = & $RootProvider
    Assert-OrbitCondition ($null -ne (Get-OrbitSelectedTab $root)) "$Label click-created tab was not selected."

    $close = Get-OrbitCloseButtons $root | Select-Object -Last 1
    Assert-OrbitCondition ($null -ne $close -and $close.Current.IsEnabled -and -not $close.Current.IsOffscreen) "$Label selected tab Close is not visible and enabled."
    Assert-OrbitCondition ($close.Current.AcceleratorKey -eq "Ctrl+W") "$Label Close does not expose Ctrl+W."
    Focus-OrbitElement $close
    Invoke-OrbitElement $close
    Wait-OrbitCondition { (Get-OrbitTabCount (& $RootProvider)) -eq $initial } "$Label visible Close did not close the tab."
    Assert-OrbitFocusRestored "$Label visible Close"

    $root = & $RootProvider
    Send-OrbitShortcut $root "^t"
    Wait-OrbitCondition { (Get-OrbitTabCount (& $RootProvider)) -eq ($initial + 1) } "$Label Ctrl+T did not create a tab."
    $root = & $RootProvider
    Assert-OrbitCondition ($null -ne (Get-OrbitSelectedTab $root)) "$Label Ctrl+T-created tab was not selected."
    Send-OrbitShortcut $root "^w"
    Wait-OrbitCondition { (Get-OrbitTabCount (& $RootProvider)) -eq $initial } "$Label Ctrl+W did not close the selected tab."
    Assert-OrbitFocusRestored "$Label Ctrl+W"

    $root = & $RootProvider
    $soleClose = Get-OrbitCloseButtons $root | Select-Object -First 1
    Assert-OrbitCondition ($null -ne $soleClose -and -not $soleClose.Current.IsEnabled) "$Label did not preserve sole-tab close safety."
    Write-Host "$Label New Tab, Ctrl+T, Close, Ctrl+W, focus restore, and sole-tab safety passed."
}

$existing = Get-Process -Name "OrbitNavigator.App" -ErrorAction SilentlyContinue
if ($existing) {
    throw "Close existing Orbit Navigator windows before running the installed usability smoke."
}

$runId = [Guid]::NewGuid()
Start-Process -FilePath $launcher -ArgumentList @(
    '--acceptance-profile-root', $profile,
    '--acceptance-run-id', $runId.ToString('D')) | Out-Null
$script:appProcess = $null
Wait-OrbitCondition {
    $script:appProcess = Get-Process -Name "OrbitNavigator.App" -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
    $null -ne $script:appProcess
} "The installed Orbit Navigator window did not appear."

try {
    Wait-OrbitCondition {
        $startup = Find-OrbitElement (Get-OrbitMainWindow) "Orbit Navigator startup" $false
        $null -eq $startup -or $startup.Current.IsOffscreen
    } "The installed Orbit Navigator startup overlay did not clear."
    $restoredTool = Get-OrbitToolWindow
    if ($null -ne $restoredTool) {
        Invoke-OrbitElement (Find-OrbitElement $restoredTool "Dock tab controller")
        Wait-OrbitCondition {
            $null -ne (Find-OrbitElement (Get-OrbitMainWindow) "Pop out tab controller" $false)
        } `
            "The restored detached controller did not dock."
    }

    $popOut = $null
    Wait-OrbitCondition {
        $popOut = Find-OrbitElement (Get-OrbitMainWindow) "Pop out tab controller" $false
        $null -ne $popOut -or $null -ne (Get-OrbitToolWindow)
    } "The Pop out tab controller command did not become available."
    $popOut = Find-OrbitElement (Get-OrbitMainWindow) "Pop out tab controller" $false
    if ($null -ne $popOut) {
        Invoke-OrbitElement $popOut
    }
    Wait-OrbitCondition { $null -ne (Get-OrbitToolWindow) } "The tab controller did not pop out."
    Test-OrbitTabSurface "Detached default" { Get-OrbitToolWindow }

    $tool = Get-OrbitToolWindow
    $transform = $tool.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    Assert-OrbitCondition $transform.Current.CanResize "The detached tab controller is not resizable."
    $transform.Resize(360, 400)
    Start-Sleep -Milliseconds 300
    Test-OrbitTabSurface "Detached narrow" { Get-OrbitToolWindow }

    $transform.Resize(480, 560)
    Start-Sleep -Milliseconds 300
    $tool = Get-OrbitToolWindow
    $resources = Find-OrbitElement $tool "Browser resources"
    Focus-OrbitElement $resources
    Invoke-OrbitElement $resources
    Wait-OrbitCondition {
        $monitor = Get-OrbitResourceWindow
        if ($null -eq $monitor) { return $false }
        $graph = Find-OrbitElement $monitor "CPU trend graph" $false
        $null -ne $graph -and -not $graph.Current.IsOffscreen
    } "Browser resources did not open in its separate monitor window."
    Assert-OrbitCondition ($null -eq (Find-OrbitElement (Get-OrbitToolWindow) "CPU trend graph" $false)) `
        "The detached tab controller retained an embedded resource panel."
    $monitor = Get-OrbitResourceWindow
    $monitorHandle = $monitor.Current.NativeWindowHandle
    foreach ($historyName in @("CPU trend graph", "Memory trend graph")) {
        $historyElement = Find-OrbitElement $monitor $historyName
        Assert-OrbitCondition (-not $historyElement.Current.IsOffscreen) "$historyName is not visible in Browser resources."
    }
    $monitorPattern = $monitor.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $monitorPattern.Close()
    Wait-OrbitCondition { $null -eq (Get-OrbitResourceWindow) } "Browser resources did not hide for reuse."
    Invoke-OrbitElement (Find-OrbitElement (Get-OrbitToolWindow) "Browser resources")
    Wait-OrbitCondition { $null -ne (Get-OrbitResourceWindow) } "Browser resources did not reopen."
    Assert-OrbitCondition ((Get-OrbitResourceWindow).Current.NativeWindowHandle -eq $monitorHandle) `
        "Browser resources did not reuse the owner window's single monitor instance."
    (Get-OrbitResourceWindow).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-OrbitCondition { $null -eq (Get-OrbitResourceWindow) } "Browser resources did not hide before docking."
    Invoke-OrbitElement (Find-OrbitElement (Get-OrbitToolWindow) "Dock tab controller")
    Wait-OrbitCondition { $null -eq (Get-OrbitToolWindow) } "The detached controller did not dock."
    Assert-OrbitCondition ($null -ne (Find-OrbitElement (Get-OrbitMainWindow) "Open new tab" $false)) "Dock did not restore the main tab controller."
    Wait-OrbitCondition {
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $focused.Current.ProcessId -eq $script:appProcess.Id -and
        ($focused.Current.Name -like "*, tab" -or
         $focused.Current.Name -eq "Pop out tab controller" -or
         $focused.Current.Name -eq "Show tabs")
    } "Dock did not restore the origin, selected tab, or Show tabs focus."
    Write-Host "Pop out, Browser resources, and Dock passed."

    foreach ($placement in @("top", "left", "right")) {
        Set-OrbitPlacement $placement
        Test-OrbitTabSurface "Docked $placement" { Get-OrbitMainWindow }
    }
    Set-OrbitPlacement "top"
    $main = Get-OrbitMainWindow
    $mainTransform = $main.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    $mainTransform.Resize(640, 480)
    Start-Sleep -Milliseconds 300
    Test-OrbitTabSurface "Docked top narrow" { Get-OrbitMainWindow }

    $dockedResources = Find-OrbitElement (Get-OrbitMainWindow) "Browser resources"
    Invoke-OrbitElement $dockedResources
    Wait-OrbitCondition { $null -ne (Get-OrbitResourceWindow) } `
        "Docked Browser resources did not open the separate monitor."
    Assert-OrbitCondition ((Get-OrbitResourceWindow).Current.NativeWindowHandle -eq $monitorHandle) `
        "Docked and detached Resources did not converge on the same monitor instance."
    (Get-OrbitResourceWindow).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-OrbitCondition { $null -eq (Get-OrbitResourceWindow) } "Docked Browser resources did not hide."
    Assert-OrbitCondition ($null -eq (Find-OrbitElement (Get-OrbitMainWindow) "CPU trend graph" $false)) `
        "The main browser retained the legacy embedded resource popup after the monitor hid."
    Write-Host "Installed Orbit Navigator tab usability smoke passed."
}
finally {
    if ($null -ne $script:appProcess -and -not $script:appProcess.HasExited) {
        $null = $script:appProcess.CloseMainWindow()
        if (-not $script:appProcess.WaitForExit(5000)) {
            Stop-Process -Id $script:appProcess.Id -Force
        }
    }
}
