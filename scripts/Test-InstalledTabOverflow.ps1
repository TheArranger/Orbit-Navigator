[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Orbit Navigator"),
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [int]$TabCount = 14,
    [switch]$UseExistingCatalog,
    [int]$TimeoutSeconds = 30,
    [string]$CaptureDirectory = ""
)

$ErrorActionPreference = "Stop"
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$acceptanceParent = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptanceParent + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The overflow profile must be inside the disposable Orbit acceptance root.'
}
$runId = [Guid]::NewGuid()
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OrbitOverflowSmokeNative
{
    public delegate bool Callback(IntPtr window, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static IntPtr FindVisibleWindow(uint processId, string titleSuffix)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((window, state) =>
        {
            uint candidateProcessId;
            GetWindowThreadProcessId(window, out candidateProcessId);
            if (candidateProcessId != processId || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString().EndsWith(titleSuffix, StringComparison.Ordinal))
            {
                match = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return match;
    }

    public static void Wheel(int delta)
    {
        mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }
}
"@

function Assert-Orbit([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Wait-Orbit([scriptblock]$Condition, [string]$Message, [int]$Milliseconds = 0) {
    $deadline = if ($Milliseconds -gt 0) {
        [DateTime]::UtcNow.AddMilliseconds($Milliseconds)
    } else {
        [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    }
    do {
        try { if (& $Condition) { return } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 75
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Message
}

function Find-OrbitElement(
    [System.Windows.Automation.AutomationElement]$Root,
    [string]$Name,
    [bool]$Required = $true) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($Required -and $null -eq $element) { throw "UI Automation element was not found: $Name" }
    return $element
}

function Get-OrbitElements([System.Windows.Automation.AutomationElement]$Root) {
    $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
}

function Invoke-Orbit([System.Windows.Automation.AutomationElement]$Element) {
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Capture-OrbitWindow([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($CaptureDirectory)) { return }
    $directory = [System.IO.Path]::GetFullPath($CaptureDirectory)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $main = Get-MainWindow
    $rectangle = New-Object OrbitOverflowSmokeNative+RECT
    $handle = [IntPtr]$main.Current.NativeWindowHandle
    Assert-Orbit ([OrbitOverflowSmokeNative]::GetWindowRect($handle, [ref]$rectangle)) "Could not capture the main window."
    $bitmap = New-Object System.Drawing.Bitmap(
        ($rectangle.Right - $rectangle.Left),
        ($rectangle.Bottom - $rectangle.Top))
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen(
                $rectangle.Left,
                $rectangle.Top,
                0,
                0,
                $bitmap.Size)
        }
        finally { $graphics.Dispose() }
        $bitmap.Save(
            (Join-Path $directory "installed-0.1.10-overflow-$Name.png"),
            [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

function Get-MainWindow {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $windows = $desktop.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    for ($index = 0; $index -lt $windows.Count; $index++) {
        $candidate = $windows.Item($index)
        if ($candidate.Current.ProcessId -eq $script:AppProcess.Id -and
            $candidate.Current.Name -eq "Orbit Navigator") {
            return $candidate
        }
    }
    throw "The Orbit Navigator main window is unavailable."
}

function Get-ToolWindow {
    $handle = [OrbitOverflowSmokeNative]::FindVisibleWindow([uint32]$script:AppProcess.Id, "Tabs")
    if ($handle -eq [IntPtr]::Zero) { return $null }
    [System.Windows.Automation.AutomationElement]::FromHandle($handle)
}

function Get-VisibleTabButtons([System.Windows.Automation.AutomationElement]$Root) {
    $result = @()
    $all = @(Get-OrbitElements $Root)
    for ($index = 0; $index -lt $all.Count; $index++) {
        $candidate = $all[$index]
        if ($candidate.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $candidate.Current.Name -like "*, tab" -and
            -not $candidate.Current.IsOffscreen) {
            $result += $candidate
        }
    }
    $result
}

function Get-VisibleCloseButtons([System.Windows.Automation.AutomationElement]$Root) {
    $result = @()
    $all = @(Get-OrbitElements $Root)
    for ($index = 0; $index -lt $all.Count; $index++) {
        $candidate = $all[$index]
        if ($candidate.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $candidate.Current.Name.StartsWith("Close tab", [StringComparison]::Ordinal) -and
            -not $candidate.Current.IsOffscreen) {
            $result += $candidate
        }
    }
    $result
}

function Get-TabCount([System.Windows.Automation.AutomationElement]$Root) {
    $visible = @(Get-VisibleTabButtons $Root).Count
    $all = @(Get-OrbitElements $Root)
    for ($index = 0; $index -lt $all.Count; $index++) {
        if ($all[$index].Current.Name -match '^More tabs.+?(\d+) hidden$') {
            return $visible + [int]$Matches[1]
        }
    }
    $visible
}

function Get-AuthoritativeTabCount {
    $auditPath = Join-Path $profile 'audit\workspace-state.jsonl'
    if (-not (Test-Path -LiteralPath $auditPath)) {
        throw 'The authoritative acceptance audit is missing.'
    }
    [int]((Get-Content -LiteralPath $auditPath | Select-Object -Last 1 | ConvertFrom-Json).TabCount)
}

function Set-Placement([ValidateSet("top", "left", "right")][string]$Placement) {
    $main = Get-MainWindow
    Invoke-Orbit (Find-OrbitElement $main "Change tab placement")
    $placementName = [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($Placement)
    $labels = @("Move tabs to $placementName", "Tabs: $placementName")
    Wait-Orbit {
        $item = $labels | ForEach-Object {
            Find-OrbitElement ([System.Windows.Automation.AutomationElement]::RootElement) $_ $false
        } | Where-Object { $null -ne $_ } | Select-Object -First 1
        if ($null -eq $item -or $item.Current.ProcessId -ne $script:AppProcess.Id) { return $false }
        Invoke-Orbit $item
        return $true
    } "$($labels -join ' / ') did not open."
    Wait-Orbit {
        $button = Find-OrbitElement (Get-MainWindow) "Change tab placement" $false
        $null -ne $button -and $button.Current.ItemStatus -eq "Tabs: $placementName"
    } "Tab placement did not authoritatively change to $placementName."
}

function Assert-PlacementRefreshAndOverflow([ValidateSet("top", "left", "right")][string]$Placement) {
    $before = Get-AuthoritativeTabCount
    Set-Placement $Placement

    # This deliberately performs no Create Tab action after the placement switch.
    # It guards the exact regression where only a centered More glyph remained.
    Wait-Orbit {
        $main = Get-MainWindow
        @(Get-VisibleTabButtons $main).Count -gt 0 -and
        @(Get-VisibleCloseButtons $main).Count -gt 0
    } "$Placement placement did not immediately restore tab cards and Close controls." 1500
    $main = Get-MainWindow
    Assert-Orbit ((Get-AuthoritativeTabCount) -eq $before) "$Placement changed the authoritative tab count."

    $scrollBar = Find-OrbitElement $main "Tab overflow position"
    Assert-Orbit (-not $scrollBar.Current.IsOffscreen -and $scrollBar.Current.IsEnabled) `
        "$Placement overflow scrollbar is not visible and enabled."
    $range = $scrollBar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    Assert-Orbit ($range.Current.Maximum -gt $range.Current.Minimum) "$Placement scrollbar has no overflow range."

    $barRect = $scrollBar.Current.BoundingRectangle
    $closeButtons = @(Get-VisibleCloseButtons $main)
    Assert-Orbit ($closeButtons.Count -gt 0) "$Placement has no visible Close action."
    Write-Host ("{0} overflow geometry: bar=({1},{2},{3},{4}); closes={5}" -f `
        $Placement,$barRect.Left,$barRect.Top,$barRect.Right,$barRect.Bottom, `
        (($closeButtons | ForEach-Object { $r=$_.Current.BoundingRectangle; "($($r.Left),$($r.Top),$($r.Right),$($r.Bottom))" }) -join ','))
    Capture-OrbitWindow ("{0}-geometry" -f $Placement)
    foreach ($close in $closeButtons) {
        $closeRect = $close.Current.BoundingRectangle
        $intersects = $barRect.Left -lt $closeRect.Right -and
            $barRect.Right -gt $closeRect.Left -and
            $barRect.Top -lt $closeRect.Bottom -and
            $barRect.Bottom -gt $closeRect.Top
        Assert-Orbit (-not $intersects) "$Placement scrollbar overlaps a visible Close action."
        if ($Placement -eq "top") {
            Assert-Orbit ($barRect.Top -ge ($closeRect.Bottom - 1)) "Top scrollbar is not reserved below tab controls."
        } else {
            Assert-Orbit ($barRect.Right -le ($closeRect.Left + 1)) "$Placement scrollbar is not reserved in the left gutter."
        }
    }

    $valueBefore = $range.Current.Value
    $target = $scrollBar.Current.BoundingRectangle
    [OrbitOverflowSmokeNative]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle) | Out-Null
    [OrbitOverflowSmokeNative]::SetCursorPos(
        [int](($target.Left + $target.Right) / 2),
        [int](($target.Top + $target.Bottom) / 2)) | Out-Null
    $wheelDelta = if ($valueBefore -lt $range.Current.Maximum) { -120 } else { 120 }
    1..3 | ForEach-Object {
        [OrbitOverflowSmokeNative]::Wheel($wheelDelta)
        Start-Sleep -Milliseconds 100
    }
    Wait-Orbit {
        $bar = Find-OrbitElement (Get-MainWindow) "Tab overflow position"
        $current = $bar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        $current.Current.Value -ne $valueBefore
    } "$Placement mouse wheel did not advance the overflow viewport." 2500

    $main = Get-MainWindow
    Assert-Orbit (@(Get-VisibleTabButtons $main).Count -gt 0) "$Placement wheel navigation hid every tab card."
    Assert-Orbit (@(Get-VisibleCloseButtons $main).Count -gt 0) "$Placement wheel navigation hid every Close action."
    Capture-OrbitWindow $Placement
    Write-Host "$Placement immediate placement refresh, reserved scrollbar, wheel navigation, and Close separation passed."
}

function Assert-DetachedPlacementDocks(
    [ValidateSet("top", "left", "right")][string]$Placement,
    [switch]$UseRestoredController) {
    if (-not $UseRestoredController) {
        Set-Placement "right"
        Wait-Orbit {
            $main = Get-MainWindow
            $detachCandidate = Find-OrbitElement $main 'Pop out tab controller' $false
            $null -eq (Get-ToolWindow) -and $null -ne $detachCandidate -and
                $detachCandidate.Current.IsEnabled
        } "The docked controller did not settle before the $Placement detach cycle."
        $detach = Find-OrbitElement (Get-MainWindow) 'Pop out tab controller'
        Invoke-Orbit $detach
        Wait-Orbit {
            $candidateTool = Get-ToolWindow
            $null -ne $candidateTool -and
                $null -ne (Find-OrbitElement $candidateTool 'Dock tab controller' $false)
        } `
            "The controller did not detach before the $Placement placement transition."
        Start-Sleep -Milliseconds 250
    }

    $tool = Get-ToolWindow
    Assert-Orbit ($null -ne $tool) `
        "The persisted Detached + Right controller was not restored before the $Placement transition."
    Assert-Orbit ($null -ne (Find-OrbitElement $tool 'Dock tab controller' $false)) `
        "The detached controller was not authoritative before the $Placement transition."

    Set-Placement $Placement
    Wait-Orbit { $null -eq (Get-ToolWindow) } `
        "Choosing $Placement while detached did not dock the native controller."
    Wait-Orbit {
        $main = Get-MainWindow
        $showTabs = Find-OrbitElement $main 'Show tabs' $false
        @(Get-VisibleTabButtons $main).Count -gt 0 -and
            ($null -eq $showTabs -or $showTabs.Current.IsOffscreen)
    } "Choosing $Placement did not restore the docked tab surface."
    Write-Host "Persisted Detached + Right -> $Placement dock/placement transition passed."
}

$launcher = Join-Path $InstallRoot "Orbit Navigator.exe"
$applicationPath = Join-Path $InstallRoot "app\OrbitNavigator.App.exe"
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw "Installed launcher missing: $launcher" }
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) { throw "Installed App missing: $applicationPath" }
if (Get-Process -Name "OrbitNavigator.App" -ErrorAction SilentlyContinue) {
    throw "Close existing Orbit Navigator windows before running overflow smoke."
}

$startedAfter = [DateTime]::UtcNow.AddSeconds(-1)
Start-Process -FilePath $launcher -ArgumentList @(
    '--acceptance-profile-root', $profile,
    '--acceptance-run-id', $runId.ToString('D')) | Out-Null
$script:AppProcess = $null
Write-Host "Launching installed Orbit Navigator for overflow verification."
Wait-Orbit {
    $candidate = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and
        [IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($applicationPath) -and
        $_.CreationDate.ToUniversalTime() -ge $startedAfter
    } | Select-Object -First 1
    if ($candidate) { $script:AppProcess = Get-Process -Id $candidate.ProcessId }
    $null -ne $script:AppProcess
} "The installed Orbit Navigator window did not appear."
$attestationPath = Join-Path $profile 'acceptance-root-attestation.json'
Wait-Orbit {
    if (-not (Test-Path -LiteralPath $attestationPath)) { return $false }
    try {
        $candidateAttestation = Get-Content -Raw -LiteralPath $attestationPath | ConvertFrom-Json
        return $candidateAttestation.RunId -eq $runId.ToString() -and
            [int]$candidateAttestation.ProcessId -eq $script:AppProcess.Id -and
            [IO.Path]::GetFullPath($candidateAttestation.Root) -eq $profile
    }
    catch { return $false }
} 'Acceptance root attestation did not bind the expected run, process, and root.'
$attestation = Get-Content -Raw -LiteralPath $attestationPath | ConvertFrom-Json
Write-Host "Installed Orbit Navigator process is ready."

try {
    Wait-Orbit {
        try { return $null -ne (Get-MainWindow) }
        catch { return $false }
    } "The Orbit Navigator main window did not appear."
    Wait-Orbit {
        $startup = Find-OrbitElement (Get-MainWindow) "Orbit Navigator startup" $false
        $null -eq $startup -or $startup.Current.IsOffscreen
    } "The startup overlay did not clear."
    Write-Host "Startup overlay cleared."
    # The overlay element can be absent briefly before the fully composed chrome
    # enters the UIA tree. Wait for the authoritative browser/controller seams
    # before evaluating restored detached state.
    Wait-Orbit {
        $main = Get-MainWindow
        $null -ne (Find-OrbitElement $main "Open new tab" $false) -and
            $null -ne (Find-OrbitElement $main "Change tab placement" $false)
    } "The browser chrome did not finish composing after startup."
    Wait-Orbit {
        $main = Get-MainWindow
        $null -ne (Get-ToolWindow) -or
            $null -ne (Find-OrbitElement $main "Show tabs" $false) -or
            @(Get-VisibleTabButtons $main).Count -gt 0
    } "The authoritative tab controller host state did not project."
    Start-Sleep -Milliseconds 350
    $tool = Get-ToolWindow
    if ($null -eq $tool) {
        $showTabs = Find-OrbitElement (Get-MainWindow) "Show tabs" $false
        if ($null -ne $showTabs -and -not $showTabs.Current.IsOffscreen -and $showTabs.Current.IsEnabled) {
            Invoke-Orbit $showTabs
            Wait-Orbit { $null -ne (Get-ToolWindow) } "The restored detached tab controller did not become visible."
            $tool = Get-ToolWindow
        }
    }
    Assert-DetachedPlacementDocks "top" -UseRestoredController
    Assert-DetachedPlacementDocks "left"
    Assert-DetachedPlacementDocks "right"

    Wait-Orbit {
        $main = Get-MainWindow
        $null -ne (Find-OrbitElement $main "Open new tab" $false) -and
        (Get-TabCount $main) -gt 0
    } "The authoritative installed tab controller did not finish restoring."

    $main = Get-MainWindow
    $transform = $main.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    if ($transform.Current.CanResize) { $transform.Resize(1200, 800) }
    Start-Sleep -Milliseconds 300
    $initial = Get-TabCount (Get-MainWindow)
    if ($UseExistingCatalog) {
        $authoritativeCount = Get-AuthoritativeTabCount
        Assert-Orbit ($authoritativeCount -ge $TabCount) `
            "The restored authoritative catalog contains fewer than $TabCount tabs."
        Write-Host "Using the restored authoritative $authoritativeCount-tab catalog without mutation."
    }
    else {
        $targetCount = [Math]::Max($TabCount, $initial + 8)
        Write-Host "Creating the many-tab overflow catalog from $initial tab(s)."
        while ((Get-TabCount (Get-MainWindow)) -lt $targetCount) {
            Invoke-Orbit (Find-OrbitElement (Get-MainWindow) "Open new tab")
            Wait-Orbit { (Get-TabCount (Get-MainWindow)) -gt $initial } "A New Tab action did not update the tab catalog."
            $initial = Get-TabCount (Get-MainWindow)
            Write-Host "Tab catalog now contains $initial tab(s)."
        }
    }

    foreach ($placement in @("top", "left", "right")) {
        Assert-PlacementRefreshAndOverflow $placement
    }
    $detach = Find-OrbitElement (Get-MainWindow) 'Pop out tab controller'
    Invoke-Orbit $detach
    Wait-Orbit { $null -ne (Get-ToolWindow) } 'The detached controller did not open.'
    Wait-Orbit {
        $candidateTool = Get-ToolWindow
        $null -ne $candidateTool -and
            $null -ne (Find-OrbitElement $candidateTool 'Open new tab' $false) -and
            $null -ne (Find-OrbitElement $candidateTool 'Dock tab controller' $false)
    } 'The detached controller command row did not become ready.'
    $tool = Get-ToolWindow
    Assert-Orbit ($null -ne (Find-OrbitElement $tool 'Open new tab' $false)) `
        'Detached controller does not expose New Tab.'
    Assert-Orbit ($null -ne (Find-OrbitElement $tool 'Dock tab controller' $false)) `
        'Detached controller does not expose Dock.'
    Assert-Orbit (@(Get-VisibleTabButtons $tool).Count -gt 0) `
        'Detached controller does not expose restored tab rows.'
    Capture-OrbitWindow 'detached'
    Invoke-Orbit (Find-OrbitElement $tool 'Dock tab controller')
    Wait-Orbit { $null -eq (Get-ToolWindow) } 'The detached controller did not dock.'
    Write-Host "Installed Orbit Navigator placement and overflow smoke passed."
}
finally {
    $process = Get-Process -Id $script:AppProcess.Id -ErrorAction SilentlyContinue
    if ($process) {
        [void]$process.CloseMainWindow()
        Start-Sleep -Seconds 2
        $process = Get-Process -Id $script:AppProcess.Id -ErrorAction SilentlyContinue
        if ($process) { $process | Stop-Process -Force }
    }
}
