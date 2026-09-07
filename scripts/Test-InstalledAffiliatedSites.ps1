[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Orbit Navigator"),
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [string]$CapturePath = ""
)

$ErrorActionPreference = "Stop"
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$acceptanceRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptanceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Affiliated Sites profile must be inside the disposable acceptance root.'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitAffiliatedCaptureNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
"@

$launcher = (Resolve-Path -LiteralPath (Join-Path $InstallRoot "Orbit Navigator.exe")).Path
$application = (Resolve-Path -LiteralPath (Join-Path $InstallRoot "app\OrbitNavigator.App.exe")).Path
$existing = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq "OrbitNavigator.App.exe" -and
    $_.ExecutablePath -and
    [IO.Path]::GetFullPath($_.ExecutablePath) -eq $application
})
if ($existing.Count -ne 0) {
    throw "Affiliated Sites smoke requires no existing installed App process."
}

function Find-NamedElement {
    param(
        [Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [Windows.Automation.ControlType]$ControlType
    )
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty,
            $Name),
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType))
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

$appProcess = $null
try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($launcher)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $InstallRoot
    $runId = [Guid]::NewGuid()
    $startInfo.ArgumentList.Add('--acceptance-profile-root')
    $startInfo.ArgumentList.Add($profile)
    $startInfo.ArgumentList.Add('--acceptance-run-id')
    $startInfo.ArgumentList.Add($runId.ToString('D'))
    $launcherProcess = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $launcherProcess -or
        -not $launcherProcess.WaitForExit(20000) -or
        $launcherProcess.ExitCode -ne 0) {
        throw "Installed launcher did not complete successfully."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $candidate = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq "OrbitNavigator.App.exe" -and
            $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath) -eq $application
        } | Select-Object -First 1
        if ($candidate) {
            $appProcess = Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
        }
        if ($appProcess -and $appProcess.MainWindowHandle -ne 0) {
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $appProcess -or $appProcess.MainWindowHandle -eq 0) {
        throw "Installed App did not present a visible window."
    }

    Start-Sleep -Seconds 3
    $root = [Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
    $placement = Find-NamedElement $root "Change tab placement" ([Windows.Automation.ControlType]::Button)
    $section = Find-NamedElement $root "Affiliated Sites" ([Windows.Automation.ControlType]::Custom)
    $externalSeparator = [char]0x2014
    $beacon = Find-NamedElement $root ("Open Beacon Spire {0} external site" -f $externalSeparator) ([Windows.Automation.ControlType]::Button)
    $myOrbit = Find-NamedElement $root ("Open My Orbit {0} external site" -f $externalSeparator) ([Windows.Automation.ControlType]::Button)
    $weddingButton = Find-NamedElement $root "Wedding Dreamer" ([Windows.Automation.ControlType]::Button)
    $weddingText = Find-NamedElement $root "Wedding Dreamer" ([Windows.Automation.ControlType]::Text)
    if (-not $placement -or -not $section -or -not $beacon -or -not $myOrbit -or -not $weddingText) {
        throw "A required installed Affiliated Sites or placement element is missing."
    }

    $buttonCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Button)
    $buttons = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $external = @()
    $excluded = @()
    foreach ($button in $buttons) {
        if ($button.Current.Name -like ("Open *{0} external site" -f $externalSeparator)) {
            $external += $button.Current.Name
        }
        if ($button.Current.Name -match "Wedding|First Step|MetaFree|Workflows|Infinity|National Chat") {
            $excluded += $button.Current.Name
        }
    }
    if ($external.Count -ne 2 -or $weddingButton -or $excluded.Count -ne 0) {
        throw "Installed catalog action boundary is not the approved two-link set."
    }

    $scrollItem = $null
    if ($myOrbit.TryGetCurrentPattern(
        [Windows.Automation.ScrollItemPattern]::Pattern,
        [ref]$scrollItem)) {
        $scrollItem.ScrollIntoView()
    }
    $allElements = $root.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $allElements) {
        if ($element.Current.ControlType -ne [Windows.Automation.ControlType]::Pane) {
            continue
        }
        $scroll = $null
        if ($element.TryGetCurrentPattern(
            [Windows.Automation.ScrollPattern]::Pattern,
            [ref]$scroll) -and
            $scroll.Current.VerticallyScrollable -and
            $scroll.Current.VerticalViewSize -lt 90) {
            $scroll.SetScrollPercent(
                [Windows.Automation.ScrollPattern]::NoScroll,
                100)
            break
        }
    }
    Start-Sleep -Milliseconds 700

    if ($CapturePath) {
        $capture = [IO.Path]::GetFullPath($CapturePath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $capture) -Force | Out-Null
        [OrbitAffiliatedCaptureNative]::ShowWindow($appProcess.MainWindowHandle, 9) | Out-Null
        [OrbitAffiliatedCaptureNative]::SetWindowPos(
            $appProcess.MainWindowHandle,
            [IntPtr](-1),
            0,
            0,
            0,
            0,
            0x43) | Out-Null
        [OrbitAffiliatedCaptureNative]::SetForegroundWindow($appProcess.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 300
        $rect = [OrbitAffiliatedCaptureNative+RECT]::new()
        if (-not [OrbitAffiliatedCaptureNative]::GetWindowRect(
            $appProcess.MainWindowHandle,
            [ref]$rect)) {
            throw "Could not resolve installed window bounds."
        }
        $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
            $bitmap.Save($capture, [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
            [OrbitAffiliatedCaptureNative]::SetWindowPos(
                $appProcess.MainWindowHandle,
                [IntPtr](-2),
                0,
                0,
                0,
                0,
                0x43) | Out-Null
        }
    }

    $invoke = $null
    if (-not $myOrbit.TryGetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern,
        [ref]$invoke)) {
        throw "The approved My Orbit card is not invokable."
    }
    $invoke.Invoke()

    $opened = $false
    $addressValue = ""
    $openDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $openDeadline) {
        Start-Sleep -Milliseconds 200
        $currentButtons = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        $tabCount = @($currentButtons | Where-Object { $_.Current.Name -like "*, tab" }).Count
        $address = Find-NamedElement $root "Address and search" ([Windows.Automation.ControlType]::Edit)
        $valuePattern = $null
        if ($address -and $address.TryGetCurrentPattern(
            [Windows.Automation.ValuePattern]::Pattern,
            [ref]$valuePattern)) {
            $addressValue = $valuePattern.Current.Value
        }
        if ($tabCount -ge 2 -and $addressValue -like "https://my-orbit.snap-it.cc/*") {
            $opened = $true
            break
        }
    }
    if (-not $opened) {
        throw "The approved site did not open in a new selected Orbit tab."
    }

    [pscustomobject]@{
        PlacementName = $placement.Current.Name
        PlacementState = $placement.Current.ItemStatus
        PlacementHelp = $placement.Current.HelpText
        CatalogState = $section.Current.ItemStatus
        ActionableSites = $external
        WeddingTextPresent = [bool]$weddingText
        WeddingButtonPresent = [bool]$weddingButton
        ExcludedActionCount = $excluded.Count
        ApprovedTargetOpenedInNewTab = $opened
        SelectedAddress = $addressValue
        CapturePath = if ($CapturePath) { [IO.Path]::GetFullPath($CapturePath) } else { $null }
    } | ConvertTo-Json -Depth 4
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(20000)) {
            Stop-Process -Id $appProcess.Id -Force
        }
    }
}
