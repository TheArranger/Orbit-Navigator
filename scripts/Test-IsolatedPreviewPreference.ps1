[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidencePath,
    [string]$GroupName = 'New group',
    [switch]$AlreadyClick,
    [switch]$SkipInitialHover,
    [switch]$VerifyPrivateNoPersistence,
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitPreviewPreferenceNative {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
  [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
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

function Root-For([int]$ProcessId) {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($process) {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            try {
                $main = [Windows.Automation.AutomationElement]::FromHandle(
                    $process.MainWindowHandle)
                if ($main -and $main.Current.Name -eq 'Orbit Navigator' -and
                    -not $main.Current.IsOffscreen) {
                    return $main
                }
            }
            catch [Windows.Automation.ElementNotAvailableException] { }
        }
    }
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

function Roots-For([int]$ProcessId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    @([Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen })
}

function Find-Visible([string]$Name, [Windows.Automation.ControlType]$Type) {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Name),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, $Type))
    [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Descendants, $condition) |
        Where-Object { -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

function Visible-Menus {
    @([Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Menu)) |
        Where-Object {
            $_.Current.ClassName -eq 'ContextMenu' -and -not $_.Current.IsOffscreen -and
            $_.Current.BoundingRectangle.Width -gt 1 -and
            $_.Current.BoundingRectangle.Height -gt 1
        })
}

function Start-Orbit([string]$Launcher, [string]$Application, [string]$Profile) {
    if ([string]::IsNullOrWhiteSpace($Profile)) {
        throw 'The disposable acceptance profile root is required for every launch.'
    }
    $resolvedProfile = [IO.Path]::GetFullPath($Profile)
    $acceptanceRoot = [IO.Path]::GetFullPath(
        (Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
    if (-not $resolvedProfile.StartsWith(
        $acceptanceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The launch profile is outside the disposable acceptance root.'
    }
    $existing = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and
        [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
            $Application, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($existing.Count -ne 0) {
        throw 'A prior disposable Orbit process is still present.'
    }
    $runId = [Guid]::NewGuid()
    $startedAfter = [DateTimeOffset]::UtcNow.AddSeconds(-1)
    Start-Process -FilePath $Launcher -ArgumentList @(
        '--acceptance-profile-root', $resolvedProfile,
        '--acceptance-run-id', $runId.ToString('D')) | Out-Null
    $cim = $null
    Wait-Until {
        $script:cim = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
                $Application, [StringComparison]::OrdinalIgnoreCase) -and
            $_.CreationDate.ToUniversalTime() -ge $startedAfter.UtcDateTime
        } | Select-Object -First 1
        $null -ne $script:cim
    } 'Disposable Orbit App did not start.'
    $process = Get-Process -Id $script:cim.ProcessId
    $attestationPath = Join-Path $resolvedProfile 'acceptance-root-attestation.json'
    $attestation = $null
    Wait-Until {
        if (-not (Test-Path -LiteralPath $attestationPath)) { return $false }
        try {
            $candidate = Get-Content -LiteralPath $attestationPath -Raw | ConvertFrom-Json
            $candidateRoot = [IO.Path]::GetFullPath([string]$candidate.Root)
            $candidateExecutable = [IO.Path]::GetFullPath([string]$candidate.ExecutablePath)
            $candidateStartedAt = [DateTimeOffset]::Parse([string]$candidate.StartedAtUtc)
            if ([string]$candidate.RunId -ne $runId.ToString('D') -or
                [int]$candidate.ProcessId -ne $process.Id -or
                -not $candidateRoot.Equals($resolvedProfile, [StringComparison]::OrdinalIgnoreCase) -or
                -not $candidateExecutable.Equals($Application, [StringComparison]::OrdinalIgnoreCase) -or
                $candidateStartedAt -lt $startedAfter) {
                return $false
            }
            $script:attestation = $candidate
            return $true
        }
        catch { return $false }
    } 'Acceptance root attestation did not bind the expected run, process, executable, and profile.'
    $script:LastStartRunId = $runId.ToString('D')
    $script:LastStartAttestation = $script:attestation
    Wait-Until { $process.Refresh(); $null -ne (Root-For $process.Id) } 'Orbit main window was not available.'
    return $process
}

function Save-StartAttestation([string]$Label) {
    if ($null -eq $script:LastStartAttestation) {
        throw "No acceptance attestation is available for $Label."
    }
    $evidence = [IO.Path]::GetFullPath($EvidencePath)
    $directory = [IO.Path]::GetDirectoryName($evidence)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $stem = [IO.Path]::GetFileNameWithoutExtension($evidence)
    $path = Join-Path $directory "$stem.$Label-attestation.json"
    $script:LastStartAttestation | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

function Stop-Orbit([Diagnostics.Process]$Process) {
    if ($null -eq $Process -or $Process.HasExited) { return }
    try {
        $root = Root-For $Process.Id
        if ($root) { $root.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
        if (-not $Process.WaitForExit(8000)) { Stop-Process -Id $Process.Id -Force }
    } catch { if (-not $Process.HasExited) { Stop-Process -Id $Process.Id -Force } }
}

function Group-Button([Windows.Automation.AutomationElement]$Root) {
    $Root.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button)) |
        Where-Object {
            -not $_.Current.IsOffscreen -and $_.Current.Name -eq "$GroupName, 5 tabs" -and
            $_.Current.BoundingRectangle.Width -ge 44 -and $_.Current.BoundingRectangle.Height -ge 44
        } |
        Select-Object -First 1
}

function Invoke-Element([Windows.Automation.AutomationElement]$Element) {
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Escape-Menus {
    $visible = Visible-Menus | Select-Object -First 1
    if ($visible) {
        $firstAction = $visible.FindAll(
            [Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::MenuItem)) |
            Where-Object { $_.Current.IsEnabled -and -not $_.Current.IsOffscreen } |
            Select-Object -First 1
        if ($firstAction) {
            try { $firstAction.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
        }
    }
    [OrbitPreviewPreferenceNative]::keybd_event(0x1B,0,0,[UIntPtr]::Zero)
    [OrbitPreviewPreferenceNative]::keybd_event(0x1B,0,2,[UIntPtr]::Zero)
    [void][OrbitPreviewPreferenceNative]::SetCursorPos(900,700)
    [OrbitPreviewPreferenceNative]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
    [OrbitPreviewPreferenceNative]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    Wait-Until { (Visible-Menus).Count -eq 0 } 'A context menu did not close before the next interaction.' 3
}

function Hover-Group([Windows.Automation.AutomationElement]$Group) {
    if ($process -and -not $process.HasExited) {
        $process.Refresh()
        [void][OrbitPreviewPreferenceNative]::ShowWindow($process.MainWindowHandle, 5)
        [void][OrbitPreviewPreferenceNative]::SetForegroundWindow($process.MainWindowHandle)
    }
    $bounds = if ($Group) { $Group.Current.BoundingRectangle } else { [Windows.Rect]::Empty }
    if ($bounds.IsEmpty -or [double]::IsInfinity($bounds.Left) -or [double]::IsInfinity($bounds.Top) -or
        [double]::IsNaN($bounds.Left) -or [double]::IsNaN($bounds.Top)) {
        $liveRoot = Root-For $process.Id
        $Group = Group-Button $liveRoot
        if (-not $Group) { throw 'The live five-tab group was unavailable for pointer hover.' }
        $bounds = $Group.Current.BoundingRectangle
    }
    if ($bounds.IsEmpty -or [double]::IsInfinity($bounds.Left) -or [double]::IsInfinity($bounds.Top) -or
        [double]::IsNaN($bounds.Left) -or [double]::IsNaN($bounds.Top)) {
        throw 'The live five-tab group returned invalid UIA bounds.'
    }
    [void][OrbitPreviewPreferenceNative]::SetCursorPos(
        [Math]::Max(0, [int]$bounds.Left - 96), [int]($bounds.Top + $bounds.Height/2))
    Start-Sleep -Milliseconds 250
    [void][OrbitPreviewPreferenceNative]::SetCursorPos(
        [int]($bounds.Left + $bounds.Width/2), [int]($bounds.Top + $bounds.Height/2))
    Start-Sleep -Milliseconds 250
}

function Select-ClickMode([Windows.Automation.AutomationElement]$Root) {
    $layout = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty, 'Change tab placement'))
    if (-not $layout) { throw 'Tab placement/preferences button was not visible.' }
    Invoke-Element $layout
    $click = $null
    Wait-Until { $script:click = Find-Visible 'Reveal large-group previews on click' ([Windows.Automation.ControlType]::MenuItem); $null -ne $script:click } 'Click preview preference was not available.' 5
    Invoke-Element $script:click
    Start-Sleep -Milliseconds 700
}

function Assert-ClickSelected([Windows.Automation.AutomationElement]$Root) {
    $layout = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty, 'Change tab placement'))
    Invoke-Element $layout
    $click = $null
    Wait-Until { $script:click = Find-Visible 'Reveal large-group previews on click' ([Windows.Automation.ControlType]::MenuItem); $null -ne $script:click } 'Click preview preference was not available after save.' 5
    if ($script:click.Current.ItemStatus -ne 'Selected') { throw 'Click preview preference was not authoritative after save/reload.' }
    Escape-Menus
}

function Assert-HoverSuppressed([Windows.Automation.AutomationElement]$Root) {
    [void][OrbitPreviewPreferenceNative]::SetCursorPos(900,700)
    Wait-Until { (Visible-Menus).Count -eq 0 } 'A stale menu was visible before Click-mode hover verification.' 3
    # Applying the authoritative preference rebuilds the controller surface. Do
    # not query the stale UIA root handed to this assertion; reacquire the live
    # window and group after the render has settled.
    $group = $null
    Wait-Until {
        $script:root = Root-For $process.Id
        $script:group = Group-Button $script:root
        $null -ne $script:group
    } 'Persisted five-tab group was not restored.' 5
    Hover-Group $group
    Start-Sleep -Milliseconds 1200
    if ((Visible-Menus).Count -ne 0) { throw 'Click mode still opened the group preview on pointer hover.' }
}

function Capture([Diagnostics.Process]$Process) {
    $native = [OrbitPreviewPreferenceNative+RECT]::new()
    $Process.Refresh()
    if (-not [OrbitPreviewPreferenceNative]::GetWindowRect($Process.MainWindowHandle, [ref]$native)) { throw 'Could not read window bounds.' }
    $rect = [Drawing.Rectangle]::FromLTRB($native.Left,$native.Top,$native.Right,$native.Bottom)
    $bitmap = [Drawing.Bitmap]::new($rect.Width,$rect.Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($rect.Location,[Drawing.Point]::Empty,$rect.Size) } finally { $graphics.Dispose() }
        $path = [IO.Path]::GetFullPath($EvidencePath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

$install = [IO.Path]::GetFullPath($InstallRoot)
$launcher = Join-Path $install 'Orbit Navigator.exe'
$application = [IO.Path]::GetFullPath((Join-Path $install 'app\OrbitNavigator.App.exe'))
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$acceptance = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptance + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe acceptance profile.' }

$process = $null
try {
    $process = Start-Orbit $launcher $application $profile
    $initialAttestationPath = Save-StartAttestation 'initial'
    $initialRunId = $script:LastStartRunId
    $root = Root-For $process.Id
    $group = $null
    Wait-Until { $script:root = Root-For $process.Id; $script:group = Group-Button $root; $null -ne $script:group } 'Five-tab hover fixture was not restored.'
    Start-Sleep -Milliseconds 1000
    if (-not $AlreadyClick) {
        if (-not $SkipInitialHover) {
            Hover-Group $group
            Wait-Until { (Visible-Menus).Count -ge 1 } 'Default Hover mode did not open the five-tab preview.' 5
            Escape-Menus
            [void][OrbitPreviewPreferenceNative]::SetCursorPos(900,700)
        }
        Select-ClickMode $root
        $root = Root-For $process.Id
    }
    Assert-ClickSelected $root
    Assert-HoverSuppressed $root
    Stop-Orbit $process
    $process = $null

    $process = Start-Orbit $launcher $application $profile
    $relaunchAttestationPath = Save-StartAttestation 'relaunch'
    $relaunchRunId = $script:LastStartRunId
    $root = Root-For $process.Id
    Wait-Until { $script:root = Root-For $process.Id; $null -ne (Group-Button $root) } 'Five-tab group was not restored after preference relaunch.'
    Start-Sleep -Milliseconds 1000
    Assert-ClickSelected $root
    Assert-HoverSuppressed $root
    Capture $process
    $privateNoPersistence = $null
    if ($VerifyPrivateNoPersistence) {
        $preferenceFile = Get-ChildItem -LiteralPath (Join-Path $profile 'profiles\data') -Recurse -File |
            Where-Object FullName -Like '*browser.workspace-ui*' | Select-Object -First 1
        if (-not $preferenceFile) { throw 'Normal workspace preference record was not present.' }
        $beforeHash = (Get-FileHash -LiteralPath $preferenceFile.FullName -Algorithm SHA256).Hash
        $normalHandle = $root.Current.NativeWindowHandle
        $privateButton = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::NameProperty, 'New private window'))
        if (-not $privateButton) { throw 'New private window command was unavailable.' }
        Invoke-Element $privateButton
        $privateRoot = $null
        Wait-Until {
            $script:privateRoot = Roots-For $process.Id |
                Where-Object { $_.Current.NativeWindowHandle -ne $normalHandle } |
                Select-Object -First 1
            $null -ne $script:privateRoot
        } 'Private window was not created.'
        $layout = $privateRoot.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::NameProperty, 'Change tab placement'))
        if ($layout) {
            Invoke-Element $layout
            $hoverItem = $null
            Wait-Until { $script:hoverItem = Find-Visible 'Reveal large-group previews on hover' ([Windows.Automation.ControlType]::MenuItem); $null -ne $script:hoverItem } 'Private preview preference menu was not exposed.' 5
            if ($hoverItem.Current.IsEnabled) { Invoke-Element $hoverItem }
        }
        Start-Sleep -Milliseconds 700
        $privateRoot.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        Wait-Until { (Roots-For $process.Id).Count -eq 1 } 'Private window did not close.' 8
        $afterHash = (Get-FileHash -LiteralPath $preferenceFile.FullName -Algorithm SHA256).Hash
        if ($afterHash -ne $beforeHash) { throw 'Private workspace preference interaction mutated normal persistent storage.' }
        $root = Root-For $process.Id
        Assert-ClickSelected $root
        $privateNoPersistence = $true
    }
    [pscustomobject]@{
        InitialHoverWorked = -not $AlreadyClick -and -not $SkipInitialHover
        ClickOverrideSaved = $true
        HoverSuppressedInClickMode = $true
        ClickOverrideSurvivedRelaunch = $true
        PrivateDidNotPersist = $privateNoPersistence
        EvidencePath = [IO.Path]::GetFullPath($EvidencePath)
        ProfileRoot = $profile
        InitialRunId = $initialRunId
        InitialAttestationPath = $initialAttestationPath
        RelaunchRunId = $relaunchRunId
        RelaunchAttestationPath = $relaunchAttestationPath
    } | ConvertTo-Json -Depth 3
}
finally {
    if ($process) { Stop-Orbit $process }
}
