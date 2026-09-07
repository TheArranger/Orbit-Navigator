[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [switch]$BookmarkOnly,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitRow05Native {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
}
"@

function Wait-Until([scriptblock]$Condition, [string]$Failure, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { if (& $Condition) { return } }
        catch [Windows.Automation.ElementNotAvailableException] { }
        catch [System.InvalidOperationException] { }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Main([int]$ProcessId) {
    [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ProcessIdProperty,
            $ProcessId)) |
        Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

function Roots([int]$ProcessId) {
    @([Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ProcessIdProperty,
            $ProcessId)) | Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen })
}

function Find-Exact([Windows.Automation.AutomationElement]$Root, [string]$Name, $Type = $null) {
    $conditions = [Collections.Generic.List[Windows.Automation.Condition]]::new()
    $conditions.Add([Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty, $Name))
    if ($Type) {
        $conditions.Add([Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty, $Type))
    }
    $Root.FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        $(if ($conditions.Count -eq 1) { $conditions[0] } else { [Windows.Automation.AndCondition]::new($conditions.ToArray()) }))
}

function Find-Global([string]$Name, $Type = $null) {
    Find-Exact ([Windows.Automation.AutomationElement]::RootElement) $Name $Type
}

function Invoke([Windows.Automation.AutomationElement]$Element) {
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 350
}

function Set-Value([Windows.Automation.AutomationElement]$Element, [string]$Value) {
    $Element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}

function Capture([Diagnostics.Process]$Process, [string]$Path) {
    $Process.Refresh()
    $rect = [OrbitRow05Native+RECT]::new()
    if (-not [OrbitRow05Native]::GetWindowRect($Process.MainWindowHandle, [ref]$rect)) {
        throw 'Unable to capture the Orbit window.'
    }
    $bounds = [Drawing.Rectangle]::FromLTRB($rect.Left, $rect.Top, $rect.Right, $rect.Bottom)
    $bitmap = [Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($bounds.Location, [Drawing.Point]::Empty, $bounds.Size) }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
    $rect
}

function Difference([string]$OnePath, [string]$TwoPath, [Drawing.Rectangle]$Region) {
    $one = [Drawing.Bitmap]::FromFile($OnePath)
    $two = [Drawing.Bitmap]::FromFile($TwoPath)
    try {
        $clip = [Drawing.Rectangle]::Intersect(
            [Drawing.Rectangle]::new(0, 0, $one.Width, $one.Height), $Region)
        $changed = 0
        for ($y = $clip.Top; $y -lt $clip.Bottom; $y += 2) {
            for ($x = $clip.Left; $x -lt $clip.Right; $x += 2) {
                if ($one.GetPixel($x, $y).ToArgb() -ne $two.GetPixel($x, $y).ToArgb()) { $changed++ }
            }
        }
        [pscustomobject]@{ Region = $clip.ToString(); SampleStride = 2; ChangedSamples = $changed }
    }
    finally { $one.Dispose(); $two.Dispose() }
}

function Start-Orbit([string]$Label) {
    $runId = [Guid]::NewGuid()
    $startedAfter = [DateTime]::UtcNow.AddSeconds(-1)
    Start-Process -FilePath $script:launcher -ArgumentList @(
        '--acceptance-profile-root', $script:profile,
        '--acceptance-run-id', $runId.ToString('D')) | Out-Null
    $cim = $null
    Wait-Until {
        $script:cim = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath) -eq $script:application -and
            $_.CreationDate.ToUniversalTime() -ge $startedAfter -and
            $_.CommandLine -like "*$($runId.ToString('D'))*"
        } | Select-Object -First 1
        $null -ne $script:cim
    } 'The disposable Orbit process did not start.' $TimeoutSeconds
    $process = Get-Process -Id $script:cim.ProcessId
    Wait-Until { $script:root = Main $process.Id; $null -ne $script:root -and $null -ne (Find-Exact $root 'Open new tab') } 'Orbit did not expose its ready controller.' $TimeoutSeconds
    Start-Sleep -Seconds 5
    $attestation = Join-Path $script:profile 'acceptance-root-attestation.json'
    Wait-Until { Test-Path -LiteralPath $attestation } 'Acceptance attestation was not written.' 10
    $record = Get-Content -LiteralPath $attestation -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath($record.Root) -ne $script:profile -or
        [IO.Path]::GetFullPath($record.ExecutablePath) -ne $script:application -or
        [int]$record.ProcessId -ne $process.Id -or $record.RunId -ne $runId.ToString('D')) {
        throw 'Acceptance attestation did not bind the expected root, executable, process, and run.'
    }
    Copy-Item -LiteralPath $attestation -Destination (Join-Path $script:evidence "$Label-attestation.json") -Force
    $process
}

function Stop-Orbit([Diagnostics.Process]$Process) {
    if (-not $Process -or $Process.HasExited) { return }
    try {
        $root = Main $Process.Id
        if ($root) { $root.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
        if (-not $Process.WaitForExit(10000)) { Stop-Process -Id $Process.Id -Force }
    }
    catch { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue }
}

function Show-NewTab([Diagnostics.Process]$Process) {
    $root = Main $Process.Id
    if (-not (Find-Exact $root 'Stellar view · switch to Basic') -and
        -not (Find-Exact $root 'Basic view · switch to Stellar')) {
        Invoke (Find-Exact $root 'Open new tab')
    }
    Wait-Until {
        $script:root = Main $Process.Id
        $null -ne (Find-Exact $root 'Stellar view · switch to Basic') -or
        $null -ne (Find-Exact $root 'Basic view · switch to Stellar')
    } 'New Tab did not become ready.' 20
    $toStellar = Find-Exact $root 'Basic view · switch to Stellar'
    if ($toStellar) { Invoke $toStellar }
    Wait-Until { $script:root = Main $Process.Id; $null -ne (Find-Exact $root 'Stellar view · switch to Basic') } 'Stellar mode did not become active.' 10
}

function Move-To([Windows.Automation.AutomationElement]$Element, [Diagnostics.Process]$Process) {
    $bounds = $Element.Current.BoundingRectangle
    if ($bounds.Width -lt 1 -or $bounds.Height -lt 1) { throw 'The hover target has no visible bounds.' }
    $Process.Refresh(); [OrbitRow05Native]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
    $x = [int]($bounds.Left + $bounds.Width / 2); $y = [int]($bounds.Top + $bounds.Height / 2)
    [OrbitRow05Native]::SetCursorPos($x - 14, $y) | Out-Null; Start-Sleep -Milliseconds 120
    [OrbitRow05Native]::SetCursorPos($x, $y) | Out-Null
    [OrbitRow05Native]::mouse_event(0x0001, 1, 0, 0, [UIntPtr]::Zero)
}

$install = [IO.Path]::GetFullPath($InstallRoot)
$script:launcher = Join-Path $install 'Orbit Navigator.exe'
$script:application = [IO.Path]::GetFullPath((Join-Path $install 'app\OrbitNavigator.App.exe'))
$script:profile = [IO.Path]::GetFullPath($ProfileRoot)
$acceptance = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $script:profile.StartsWith($acceptance + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe profile root.' }
$script:evidence = [IO.Path]::GetFullPath($EvidenceRoot)
[IO.Directory]::CreateDirectory($script:evidence) | Out-Null
$process = $null
try {
    # Add a real durable bookmark through the product's bookmark manager.
    $process = Start-Orbit 'bookmark-seed'
    Show-NewTab $process
    $root = Main $process.Id
    if (-not (Find-Exact $root 'Open Row Five Bookmark from bookmarks hub')) {
        Invoke (Find-Exact $root 'Manage bookmarks')
        $dialog = $null
        Wait-Until { $script:dialog = Find-Global 'Bookmarks — Orbit Navigator' ([Windows.Automation.ControlType]::Window); $null -ne $script:dialog } 'Bookmark manager did not open.' 10
        Invoke (Find-Exact $dialog 'Add bookmark')
        Set-Value (Find-Exact $dialog 'Bookmark address or URL') 'https://my-orbit.snap-it.cc/'
        Set-Value (Find-Exact $dialog 'Bookmark title') 'Row Five Bookmark'
        Set-Value (Find-Exact $dialog 'Bookmark note') 'Row five local bookmark note'
        Invoke (Find-Exact $dialog 'Save bookmark')
        Start-Sleep -Seconds 1
        Invoke (Find-Exact $dialog 'Close bookmarks')
        Wait-Until { $script:root = Main $process.Id; $null -ne (Find-Exact $root 'Open Row Five Bookmark from bookmarks hub') } 'Saved bookmark did not reach the New Tab hub.' 15
    }
    Stop-Orbit $process; $process = $null

    # Relaunch to prove the workspace and bookmark are durable, then gather only row-5 evidence.
    $process = Start-Orbit 'row05'
    Show-NewTab $process
    $root = Main $process.Id
    if ($BookmarkOnly) {
        $bookmark = Find-Exact $root 'Open Row Five Bookmark from bookmarks hub'
        if (-not $bookmark) { throw 'Durable bookmark was not present after same-root relaunch.' }
        $bookmarkFile = Get-ChildItem -LiteralPath (Join-Path $script:profile 'profiles\data') -Recurse -File |
            Where-Object FullName -like '*browser.bookmarks*' | Select-Object -First 1
        if (-not $bookmarkFile) { throw 'Durable bookmark storage was not written.' }
        $raw = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($bookmarkFile.FullName))
        $stored = $raw.Substring($raw.IndexOf('[')) | ConvertFrom-Json
        $storedBookmark = $stored | Where-Object Title -eq 'Row Five Bookmark' | Select-Object -First 1
        if (-not $storedBookmark -or $storedBookmark.Note -ne 'Row five local bookmark note') {
            throw 'Durable bookmark note did not survive storage and same-root relaunch.'
        }
        $storageHashBeforePrivate = (Get-FileHash -LiteralPath $bookmarkFile.FullName -Algorithm SHA256).Hash

        Move-To $bookmark $process
        Wait-Until { $script:root = Main $process.Id; $null -ne (Find-Exact $root 'Row Five Bookmark details') } 'Bookmark hover did not expose the exact detail peer.' 8
        $bookmarkDetail = Find-Exact $root 'Row Five Bookmark details'
        $bookmarkHelp = $bookmarkDetail.Current.HelpText
        if ($bookmarkDetail.Current.ItemStatus -ne 'Bookmark details' -or $bookmarkDetail.Current.IsKeyboardFocusable -or
            $bookmarkHelp -notlike '*https://my-orbit.snap-it.cc/*' -or $bookmarkHelp -notlike '*Row five local bookmark note*' -or
            $null -eq (Find-Exact $root 'Row five local bookmark note')) {
            throw 'Bookmark detail card/UIA did not expose the durable safe URL and note.'
        }
        $pattern = $null
        if ($bookmarkDetail.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { throw 'Bookmark detail surface exposed an interaction pattern.' }
        $capture = Join-Path $script:evidence 'bookmark-note-hover.png'; $windowRect = Capture $process $capture
        [OrbitRow05Native]::SetCursorPos($windowRect.Right - 20, $windowRect.Bottom - 20) | Out-Null
        Wait-Until { $script:root = Main $process.Id; $null -eq (Find-Exact $root 'Row Five Bookmark details') } 'Bookmark detail did not close on pointer leave.' 8

        $normalHandle = $root.Current.NativeWindowHandle
        Invoke (Find-Exact $root 'New private window')
        $privateRoot = $null
        Wait-Until {
            $script:privateRoot = Roots $process.Id | Where-Object {
                $_.Current.NativeWindowHandle -ne $normalHandle -and $null -ne (Find-Exact $_ 'Manage bookmarks')
            } | Select-Object -First 1
            $null -ne $script:privateRoot
        } 'Private window did not expose a New Tab bookmark route.' 15
        Invoke (Find-Exact $privateRoot 'Manage bookmarks')
        $dialog = $null
        Wait-Until { $script:dialog = Find-Global 'Bookmarks — Orbit Navigator' ([Windows.Automation.ControlType]::Window); $null -ne $script:dialog } 'Private bookmark manager did not open.' 10
        $privateAdd = Find-Exact $dialog 'Add bookmark'
        if (-not $privateAdd -or $privateAdd.Current.IsEnabled) { throw 'Private bookmark mutation was not visibly denied.' }
        $privateCapture = Join-Path $script:evidence 'private-bookmark-denied.png'; $null = Capture $process $privateCapture
        Invoke (Find-Exact $dialog 'Close bookmarks')
        $privateRoot.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        Wait-Until { (Roots $process.Id).Count -eq 1 } 'Private window did not close.' 10
        $storageHashAfterPrivate = (Get-FileHash -LiteralPath $bookmarkFile.FullName -Algorithm SHA256).Hash
        if ($storageHashAfterPrivate -ne $storageHashBeforePrivate) { throw 'Private bookmark interaction mutated normal durable storage.' }

        [pscustomobject]@{
            Passed = $true
            Durable = [pscustomobject]@{ File = $bookmarkFile.FullName; Sha256 = $storageHashBeforePrivate; Note = $storedBookmark.Note; SameRootRelaunch = $true }
            Hover = [pscustomobject]@{ Name = 'Row Five Bookmark details'; ItemStatus = $bookmarkDetail.Current.ItemStatus; HelpText = $bookmarkHelp; VisibleNoteText = $true; KeyboardFocusable = $false; InvokePattern = $false; LeaveClosed = $true }
            Private = [pscustomobject]@{ AddBookmarkEnabled = $false; StorageHashUnchanged = $true }
            Captures = @($capture, $privateCapture)
        } | ConvertTo-Json -Depth 6 | Tee-Object -FilePath (Join-Path $script:evidence 'bookmark-only-result.json')
        return
    }
    $workspace = Find-Exact $root 'Open Research Set from workspaces hub'
    $bookmark = Find-Exact $root 'Open Row Five Bookmark from bookmarks hub'
    if (-not $workspace -or -not $bookmark) { throw 'Durable workspace and bookmark were not both present after relaunch.' }

    $ready0 = Join-Path $script:evidence 'ready-t0.png'; $ready10 = Join-Path $script:evidence 'ready-t10.png'
    $windowRect = Capture $process $ready0; Start-Sleep -Seconds 10; $null = Capture $process $ready10
    $full = [Drawing.Rectangle]::new(0, 0, $windowRect.Right - $windowRect.Left, $windowRect.Bottom - $windowRect.Top)
    $readyDiff = Difference $ready0 $ready10 $full
    if ($readyDiff.ChangedSamples -lt 100) { throw 'Ready-state v6 motion did not visibly advance.' }

    $root = Main $process.Id; $workspace = Find-Exact $root 'Open Research Set from workspaces hub'; Move-To $workspace $process
    Wait-Until { $script:root = Main $process.Id; $null -ne (Find-Exact $root 'Research Set details') } 'Workspace hover did not expose the exact detail peer.' 8
    $detail = Find-Exact $root 'Research Set details'
    $workspaceHelp = $detail.Current.HelpText
    if ($detail.Current.ItemStatus -ne 'Workspace details' -or $detail.Current.IsKeyboardFocusable -or
        $workspaceHelp -notlike '*Disposable acceptance note*' -or $workspaceHelp -notlike '*http://127.0.0.1:*') {
        throw 'Workspace detail UIA semantics or safe URL/note help text were incomplete.'
    }
    $pattern = $null
    if ($detail.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { throw 'Workspace detail surface exposed an interaction pattern.' }
    $hover0 = Join-Path $script:evidence 'workspace-hover-t0.png'; $hover1 = Join-Path $script:evidence 'workspace-hover-t1.png'
    $null = Capture $process $hover0; Start-Sleep -Milliseconds 1100; $null = Capture $process $hover1
    $workspaceStar = Find-Exact (Main $process.Id) 'Workspace star with orbiting selected image'
    $starBounds = $workspaceStar.Current.BoundingRectangle
    $starRegion = [Drawing.Rectangle]::new(
        [int]($starBounds.Left - $windowRect.Left), [int]($starBounds.Top - $windowRect.Top),
        [int]$starBounds.Width, [int]$starBounds.Height)
    $starDiff = Difference $hover0 $hover1 $starRegion
    $quietRegion = [Drawing.Rectangle]::new(160, 150, 360, 220)
    $quietDiff = Difference $hover0 $hover1 $quietRegion
    if ($starDiff.ChangedSamples -lt 20) { throw 'Central solar artwork did not continue while detail was visible.' }
    if ($quietDiff.ChangedSamples -gt 20) { throw 'Non-stellar scene continued moving while detail was visible.' }

    [OrbitRow05Native]::SetCursorPos($windowRect.Right - 20, $windowRect.Bottom - 20) | Out-Null
    Wait-Until { $script:root = Main $process.Id; $null -eq (Find-Exact $root 'Research Set details') } 'Workspace detail did not close on pointer leave.' 8
    $afterLeave = Join-Path $script:evidence 'workspace-after-leave.png'; $null = Capture $process $afterLeave
    $root = Main $process.Id; $workspace = Find-Exact $root 'Open Research Set from workspaces hub'; $workspace.SetFocus()
    Wait-Until { $script:root = Main $process.Id; $null -ne (Find-Exact $root 'Research Set details') } 'Keyboard focus did not restore workspace details.' 8
    $openNew = Find-Exact $root 'Open new tab'; $openNew.SetFocus()
    Wait-Until { $script:root = Main $process.Id; $null -eq (Find-Exact $root 'Research Set details') } 'Detail did not close after focus left the workspace.' 8

    $root = Main $process.Id; $bookmark = Find-Exact $root 'Open Row Five Bookmark from bookmarks hub'; Move-To $bookmark $process
    Wait-Until { $script:root = Main $process.Id; $null -ne (Find-Exact $root 'Row Five Bookmark details') } 'Bookmark hover did not expose the exact detail peer.' 8
    $bookmarkDetail = Find-Exact $root 'Row Five Bookmark details'
    $bookmarkHelp = $bookmarkDetail.Current.HelpText
    if ($bookmarkDetail.Current.ItemStatus -ne 'Bookmark details' -or $bookmarkDetail.Current.IsKeyboardFocusable -or
        $bookmarkHelp -notlike '*https://my-orbit.snap-it.cc/*' -or $bookmarkHelp -notlike '*Row five local bookmark note*') {
        throw 'Bookmark detail UIA semantics or safe URL/note help text were incomplete.'
    }
    $pattern = $null
    if ($bookmarkDetail.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { throw 'Bookmark detail surface exposed an interaction pattern.' }
    $bookmarkCapture = Join-Path $script:evidence 'bookmark-hover.png'; $null = Capture $process $bookmarkCapture

    [pscustomobject]@{
        Passed = $true
        Workspace = [pscustomobject]@{ Name = 'Research Set details'; ItemStatus = $detail.Current.ItemStatus; HelpText = $workspaceHelp; KeyboardFocusable = $detail.Current.IsKeyboardFocusable; InvokePattern = $false }
        Bookmark = [pscustomobject]@{ Name = 'Row Five Bookmark details'; ItemStatus = $bookmarkDetail.Current.ItemStatus; HelpText = $bookmarkHelp; KeyboardFocusable = $bookmarkDetail.Current.IsKeyboardFocusable; InvokePattern = $false }
        LeaveClosed = $true; KeyboardFocusRestoredDetails = $true; FocusDepartureClosed = $true
        ReadyTemporal = $readyDiff; CentralStarDuringFreeze = $starDiff; QuietNonStellarDuringFreeze = $quietDiff
        Captures = @($ready0, $ready10, $hover0, $hover1, $afterLeave, $bookmarkCapture)
    } | ConvertTo-Json -Depth 6 | Tee-Object -FilePath (Join-Path $script:evidence 'result.json')
}
finally {
    if ($process) { Stop-Orbit $process }
}
