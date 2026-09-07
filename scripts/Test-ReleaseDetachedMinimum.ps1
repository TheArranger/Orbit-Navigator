[CmdletBinding()]
param(
    [string]$ApplicationPath = (Join-Path $PSScriptRoot "..\src\OrbitNavigator.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\OrbitNavigator.App.exe"),
    [string]$DotNetHost = (Join-Path $PSScriptRoot "..\.tools\dotnet\dotnet.exe"),
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [int]$TabCount = 24,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OrbitDetachedMinimumNative {
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder text, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int width, int height, bool repaint);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT rect);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT point);
  public static IntPtr FindVisibleWindow(uint processId, string titlePart) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, p) => {
      uint owner; GetWindowThreadProcessId(h, out owner);
      if (owner != processId || !IsWindowVisible(h)) return true;
      var text = new StringBuilder(GetWindowTextLength(h) + 1);
      GetWindowText(h, text, text.Capacity);
      if (text.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) >= 0) {
        found = h;
        return false;
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

function Wait-Until([scriptblock]$Condition, [string]$Failure, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { if (& $Condition) { return } } catch [Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 125
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Descendants($Root) {
    @($Root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition))
}

function Named($Root, [string]$Name, [Windows.Automation.ControlType]$Type = $null) {
    Descendants $Root | Where-Object {
        -not $_.Current.IsOffscreen -and $_.Current.Name -eq $Name -and
        ($null -eq $Type -or $_.Current.ControlType -eq $Type)
    } | Select-Object -First 1
}

function Named-Like($Root, [string]$Pattern, [Windows.Automation.ControlType]$Type = $null) {
    Descendants $Root | Where-Object {
        -not $_.Current.IsOffscreen -and $_.Current.Name -like $Pattern -and
        ($null -eq $Type -or $_.Current.ControlType -eq $Type)
    } | Select-Object -First 1
}

function Top-Level([int]$ProcessId, [string]$Name) {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Name))
    [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { -not $_.Current.IsOffscreen } | Select-Object -First 1
}

function Native-Owned-Window([int]$ProcessId, [string]$TitlePart) {
    $handle = [OrbitDetachedMinimumNative]::FindVisibleWindow([uint32]$ProcessId, $TitlePart)
    if ($handle -eq [IntPtr]::Zero) { return $null }
    [Windows.Automation.AutomationElement]::FromHandle($handle)
}

function Invoke-Control($Element) {
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Rect-Object($Element) {
    $rect = $Element.Current.BoundingRectangle
    [pscustomobject]@{
        Name = $Element.Current.Name
        Left = $rect.Left
        Top = $rect.Top
        Width = $rect.Width
        Height = $rect.Height
        Right = $rect.Right
        Bottom = $rect.Bottom
    }
}

function Client-Rect([IntPtr]$Handle) {
    $nativeRect = [OrbitDetachedMinimumNative+RECT]::new()
    if (-not [OrbitDetachedMinimumNative]::GetClientRect($Handle, [ref]$nativeRect)) {
        throw 'Could not read the detached controller client rectangle.'
    }
    $topLeft = [OrbitDetachedMinimumNative+POINT]::new()
    $bottomRight = [OrbitDetachedMinimumNative+POINT]::new()
    $topLeft.X = $nativeRect.Left
    $topLeft.Y = $nativeRect.Top
    $bottomRight.X = $nativeRect.Right
    $bottomRight.Y = $nativeRect.Bottom
    if (-not [OrbitDetachedMinimumNative]::ClientToScreen($Handle, [ref]$topLeft) -or
        -not [OrbitDetachedMinimumNative]::ClientToScreen($Handle, [ref]$bottomRight)) {
        throw 'Could not translate the detached controller client rectangle.'
    }
    [pscustomobject]@{
        Left = [double]$topLeft.X
        Top = [double]$topLeft.Y
        Width = [double]($bottomRight.X - $topLeft.X)
        Height = [double]($bottomRight.Y - $topLeft.Y)
        Right = [double]$bottomRight.X
        Bottom = [double]$bottomRight.Y
    }
}

function Assert-Inside([object]$Rect, [object]$Bounds, [string]$Label) {
    if ($Rect.Left -lt $Bounds.Left - 1 -or $Rect.Top -lt $Bounds.Top - 1 -or
        $Rect.Right -gt $Bounds.Right + 1 -or $Rect.Bottom -gt $Bounds.Bottom + 1) {
        throw "$Label is outside the detached client bounds. Element=$($Rect | ConvertTo-Json -Compress) Client=$($Bounds | ConvertTo-Json -Compress)"
    }
}

function Capture-Window($Window, [string]$Path) {
    $rect = $Window.Current.BoundingRectangle
    $bitmap = [Drawing.Bitmap]::new([int]$rect.Width, [int]$rect.Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen([int]$rect.Left, [int]$rect.Top, 0, 0, $bitmap.Size) }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

$application = [IO.Path]::GetFullPath($ApplicationPath)
$applicationDll = [IO.Path]::ChangeExtension($application, '.dll')
$dotnet = [IO.Path]::GetFullPath($DotNetHost)
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$acceptance = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptance + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe profile root.' }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
if (Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($applicationDll, [StringComparison]::OrdinalIgnoreCase) }) {
    throw 'The selected Release output is already running.'
}

$runId = [Guid]::NewGuid().ToString('D')
$info = [Diagnostics.ProcessStartInfo]::new($dotnet)
$info.UseShellExecute = $false
$info.ArgumentList.Add($applicationDll)
$info.ArgumentList.Add('--acceptance-profile-root')
$info.ArgumentList.Add($profile)
$info.ArgumentList.Add('--acceptance-run-id')
$info.ArgumentList.Add($runId)
$process = $null
try {
    $process = [Diagnostics.Process]::Start($info)
    Wait-Until {
        $script:main = Top-Level $process.Id 'Orbit Navigator'
        $null -ne $script:main -and $null -ne (Named $script:main 'Open new tab' ([Windows.Automation.ControlType]::Button))
    } 'Release window did not become ready.'

    for ($i = 2; $i -le $TabCount; $i++) {
        $main = Top-Level $process.Id 'Orbit Navigator'
        $newTab = Named $main 'Open new tab' ([Windows.Automation.ControlType]::Button)
        if ($null -ne $newTab) {
            Invoke-Control $newTab
        }
        else {
            [void][OrbitDetachedMinimumNative]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle)
            [Windows.Forms.SendKeys]::SendWait('^t')
        }
        Start-Sleep -Milliseconds 90
    }
    Wait-Until {
        $script:main = Top-Level $process.Id 'Orbit Navigator'
        $script:more = Named-Like $script:main 'More tabs*' ([Windows.Automation.ControlType]::Button)
        $null -ne $script:more -and $script:more.Current.Name -match '\d+ hidden'
    } 'Twenty-four tabs did not produce authoritative overflow.'

    $main = $script:main
    $popout = Named $main 'Pop out tab controller' ([Windows.Automation.ControlType]::Button)
    if ($null -eq $popout -or -not $popout.Current.IsEnabled) { throw 'Pop out tab controller is unavailable.' }
    Invoke-Control $popout
    Wait-Until { $script:tool = Native-Owned-Window $process.Id 'Tabs'; $null -ne $script:tool } 'Detached tab controller did not appear.'
    $tool = $script:tool
    $handle = [IntPtr]$tool.Current.NativeWindowHandle
    [void][OrbitDetachedMinimumNative]::MoveWindow($handle, 120, 120, 360, 400, $true)
    Wait-Until {
        $script:tool = Native-Owned-Window $process.Id 'Tabs'
        $bounds = $script:tool.Current.BoundingRectangle
        $bounds.Width -ge 359 -and $bounds.Height -ge 399
    } 'Detached controller did not reach 360x400 outer bounds.'
    $tool = $script:tool
    $handle = [IntPtr]$tool.Current.NativeWindowHandle
    $client = Client-Rect $handle

    $more = Named-Like $tool 'More tabs*' ([Windows.Automation.ControlType]::Button)
    $resources = Named $tool 'Browser resources' ([Windows.Automation.ControlType]::Button)
    $dock = Named $tool 'Dock tab controller' ([Windows.Automation.ControlType]::Button)
    $newTab = Named $tool 'Open new tab' ([Windows.Automation.ControlType]::Button)
    if ($null -eq $more -or $null -eq $resources -or $null -eq $dock -or $null -eq $newTab) {
        throw 'A required detached command is missing.'
    }
    $commands = @($more, $resources, $dock, $newTab) | ForEach-Object { Rect-Object $_ }
    foreach ($command in $commands) {
        Assert-Inside $command $client $command.Name
        if ($command.Width -lt 44 -or $command.Height -lt 44) { throw "$($command.Name) is below the 44-DIP target floor." }
    }

    $newBefore = Rect-Object $newTab
    $scroll = Named $tool 'Tab overflow position' ([Windows.Automation.ControlType]::ScrollBar)
    if ($null -eq $scroll) { throw 'The visible detached tab overflow scrollbar is missing.' }
    $scrollRect = Rect-Object $scroll
    Assert-Inside $scrollRect $client 'Tab overflow position'
    if ($scrollRect.Bottom -gt $newBefore.Top + 1) {
        throw "The tab scrollbar intersects the pinned New Tab row. Scroll=$($scrollRect | ConvertTo-Json -Compress) New=$($newBefore | ConvertTo-Json -Compress)"
    }
    $range = [Windows.Automation.RangeValuePattern]$scroll.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
    $beforeValue = $range.Current.Value
    $minimum = $range.Current.Minimum
    $maximum = $range.Current.Maximum
    $largeChange = $range.Current.LargeChange
    if ($maximum -le $minimum) { throw 'The 24-tab detached scrollbar has no usable range.' }
    $target = if ($beforeValue -ge $maximum - 0.01) {
        [Math]::Max($minimum, $beforeValue - [Math]::Max(1, $largeChange))
    }
    else {
        [Math]::Min($maximum, $beforeValue + [Math]::Max(1, $largeChange))
    }
    $range.SetValue($target)
    Wait-Until {
        $script:tool = Native-Owned-Window $process.Id 'Tabs'
        $script:scroll = Named $script:tool 'Tab overflow position' ([Windows.Automation.ControlType]::ScrollBar)
        $script:range = [Windows.Automation.RangeValuePattern]$script:scroll.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
        [Math]::Abs($script:range.Current.Value - $beforeValue) -gt 0.01
    } 'The detached tab list did not scroll independently.' 10
    $afterValue = $script:range.Current.Value
    $newAfterElement = Named $script:tool 'Open new tab' ([Windows.Automation.ControlType]::Button)
    $newAfter = Rect-Object $newAfterElement
    Assert-Inside $newAfter $client 'Open new tab after scrolling'
    if ([Math]::Abs($newAfter.Left - $newBefore.Left) -gt 1 -or [Math]::Abs($newAfter.Top - $newBefore.Top) -gt 1 -or
        [Math]::Abs($newAfter.Width - $newBefore.Width) -gt 1 -or [Math]::Abs($newAfter.Height - $newBefore.Height) -gt 1) {
        throw "Pinned New Tab moved while the list scrolled. Before=$($newBefore | ConvertTo-Json -Compress) After=$($newAfter | ConvertTo-Json -Compress)"
    }

    $capture = Join-Path $evidence 'release-detached-360x400-24-tabs.png'
    Capture-Window $script:tool $capture
    [pscustomobject]@{
        Result = 'PASS'
        RunId = $runId
        TabCountRequested = $TabCount
        OuterBounds = Rect-Object $script:tool
        ClientBounds = $client
        Commands = $commands
        ScrollBar = [pscustomobject]@{
            Bounds = $scrollRect
            Minimum = $minimum
            Maximum = $maximum
            LargeChange = $largeChange
            ValueBefore = $beforeValue
            ValueAfter = $afterValue
            IndependentScrollProven = $true
        }
        NewTabBeforeScroll = $newBefore
        NewTabAfterScroll = $newAfter
        Capture = $capture
        UserProcessTouched = $false
    } | ConvertTo-Json -Depth 8
}
finally {
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(8000)) { Stop-Process -Id $process.Id -Force }
    }
}
