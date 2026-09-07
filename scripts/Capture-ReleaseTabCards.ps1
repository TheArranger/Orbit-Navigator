[CmdletBinding()]
param(
    [string]$ApplicationPath = (Join-Path $PSScriptRoot "..\src\OrbitNavigator.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\OrbitNavigator.App.exe"),
    [string]$DotNetHost = (Join-Path $PSScriptRoot "..\.tools\dotnet\dotnet.exe"),
    [string]$CaptureDirectory = (Join-Path $PSScriptRoot "..\artifacts\verification\release-tab-cards"),
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitReleaseTabCaptureNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
}
"@

function Wait-OrbitCondition {
    param([scriptblock]$Condition, [string]$Failure)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try { if (& $Condition) { return } } catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Find-OrbitElement {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name, [bool]$Required = $true)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($Required -and $null -eq $element) { throw "UI Automation element not found: $Name" }
    return $element
}

function Find-VisibleOrbitElement {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $matches = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name))
    for ($index = 0; $index -lt $matches.Count; $index++) {
        $candidate = $matches.Item($index)
        if (-not $candidate.Current.IsOffscreen) { return $candidate }
    }
    return $null
}

function Get-OrbitMainRoot {
    param([int]$ProcessId)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { $_.Current.Name -eq "Orbit Navigator" -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

function Get-OrbitSelectedTab {
    param([System.Windows.Automation.AutomationElement]$Root)
    $elements = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $elements | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $_.Current.HelpText -like "Selected.*" -and
        $_.Current.Name -like "*New Tab*"
    } | Select-Object -First 1
}

function Capture-OrbitWindow {
    param([IntPtr]$Handle, [string]$Path)
    $rect = [OrbitReleaseTabCaptureNative+RECT]::new()
    if (-not [OrbitReleaseTabCaptureNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not read Orbit window bounds."
    }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Set-OrbitPlacement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [ValidateSet("Top", "Left", "Right")][string]$Placement)
    $button = Find-OrbitElement $Root "Change tab placement"
    if ($button.Current.ItemStatus -eq "Tabs: $Placement") { return }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:placementItem = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Move tabs to $Placement" `
            $false
        $null -ne $script:placementItem -and $script:placementItem.Current.IsEnabled
    } "The $Placement placement menu item did not appear."
    $script:placementItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition { $button.Current.ItemStatus -eq "Tabs: $Placement" } `
        "The $Placement placement was not accepted."
}

$application = [IO.Path]::GetFullPath($ApplicationPath)
$dotnet = [IO.Path]::GetFullPath($DotNetHost)
$captures = [IO.Path]::GetFullPath($CaptureDirectory)
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Release application not found: $application"
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Bundled .NET host not found: $dotnet"
}
if (Get-Process -Name "OrbitNavigator.App" -ErrorAction SilentlyContinue) {
    throw "Release capture requires every Orbit Navigator process to be closed."
}
[IO.Directory]::CreateDirectory($captures) | Out-Null

$applicationAssembly = [IO.Path]::ChangeExtension($application, ".dll")
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $dotnet
$startInfo.UseShellExecute = $false
$startInfo.ArgumentList.Add($applicationAssembly)
$process = [Diagnostics.Process]::Start($startInfo)
try {
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $process.Id
        if ($null -eq $script:root) { return $false }
        $script:windowHandle = [IntPtr]$script:root.Current.NativeWindowHandle
        $script:placementButton = Find-OrbitElement $root "Change tab placement" $false
        $null -ne $script:placementButton
    } "The Release browser chrome did not become ready."
    Start-Sleep -Seconds 2
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $script:root = Get-OrbitMainRoot $process.Id
        $dockController = Find-VisibleOrbitElement $script:root "Dock tab controller"
        if ($null -eq $dockController) { break }
        $dockController.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-OrbitCondition {
            $script:root = Get-OrbitMainRoot $process.Id
            $visiblePopOut = Find-VisibleOrbitElement $script:root "Pop out tab controller"
            $visibleDock = Find-VisibleOrbitElement $script:root "Dock tab controller"
            $null -ne $visiblePopOut -and $null -eq $visibleDock
        } "The detached controller did not dock before tab-card capture."
        Start-Sleep -Seconds 1
    }
    if ($null -ne (Find-VisibleOrbitElement $script:root "Dock tab controller")) {
        throw "The tab controller did not remain docked for visual capture."
    }
    [void][OrbitReleaseTabCaptureNative]::SetForegroundWindow($windowHandle)
    $windowPattern = $root.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    if ($windowPattern.Current.CanResize) { $windowPattern.Resize(1200, 800) }
    Start-Sleep -Milliseconds 500

    $dpi = [OrbitReleaseTabCaptureNative]::GetDpiForWindow($windowHandle)
    $scale = $dpi / 96.0
    $results = @()
    foreach ($placement in @("Top", "Left", "Right")) {
        Set-OrbitPlacement $root $placement
        Wait-OrbitCondition {
            $script:selectedTab = Get-OrbitSelectedTab $root
            $script:visibleClose = Find-VisibleOrbitElement $root "Close tab — New Tab"
            $null -ne $script:selectedTab -and -not $script:selectedTab.Current.IsOffscreen -and
                $null -ne $script:visibleClose
        } "The selected New Tab card and Close target did not appear for $Placement."
        $tabRect = $script:selectedTab.Current.BoundingRectangle
        $close = $script:visibleClose
        $closeRect = $close.Current.BoundingRectangle
        $newTabTextCount = @($script:selectedTab.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty,
                "New Tab"))).Count
        if ($newTabTextCount -ne 1) {
            throw "$Placement selected card exposes New Tab $newTabTextCount times instead of once."
        }
        $tabDipWidth = $tabRect.Width / $scale
        $tabDipHeight = $tabRect.Height / $scale
        $closeDipWidth = $closeRect.Width / $scale
        $closeDipHeight = $closeRect.Height / $scale
        if ($tabDipWidth -lt 44 -or $tabDipHeight -lt 44) {
            throw "$Placement tab selector is smaller than 44 DIPs."
        }
        if ($closeDipWidth -lt 44 -or $closeDipHeight -lt 44) {
            throw "$Placement Close target is smaller than 44 DIPs."
        }
        $capturePath = Join-Path $captures ("release-tab-card-{0}-{1}dpi.png" -f $placement.ToLowerInvariant(), $dpi)
        Capture-OrbitWindow $windowHandle $capturePath
        $results += [pscustomobject]@{
            Placement = $placement
            Dpi = $dpi
            ScalePercent = [Math]::Round($scale * 100)
            TabAutomationName = $script:selectedTab.Current.Name
            TabHelpText = $script:selectedTab.Current.HelpText
            TabTargetWidthDip = [Math]::Round($tabDipWidth, 1)
            TabTargetHeightDip = [Math]::Round($tabDipHeight, 1)
            CloseTargetWidthDip = [Math]::Round($closeDipWidth, 1)
            CloseTargetHeightDip = [Math]::Round($closeDipHeight, 1)
            NewTabTextCount = $newTabTextCount
            CapturePath = $capturePath
        }
    }
    Set-OrbitPlacement $root "Top"
    $results | ConvertTo-Json -Depth 4
}
finally {
    if (-not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
    }
}
