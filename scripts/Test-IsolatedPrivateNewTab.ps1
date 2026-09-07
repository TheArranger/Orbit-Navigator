[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitPrivateAcceptanceNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
"@

function Wait-Until {
    param([scriptblock]$Condition, [string]$Failure, [int]$Seconds = $TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { if (& $Condition) { return } } catch [Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Roots-For([int]$ProcessId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    @([Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen })
}

function Find-Named($Root, [string]$Name) {
    $Root.FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty, $Name))
}

function Invoke-Element($Element) {
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Start-Orbit([string]$Launcher, [string]$Application, [string]$Profile) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Launcher
    $info.WorkingDirectory = Split-Path -Parent $Launcher
    $info.UseShellExecute = $false
    $info.ArgumentList.Add('--acceptance-profile-root')
    $info.ArgumentList.Add($Profile)
    $info.ArgumentList.Add('--acceptance-run-id')
    $info.ArgumentList.Add([Guid]::NewGuid().ToString('D'))
    $launch = [Diagnostics.Process]::Start($info)
    if (-not $launch.WaitForExit(20000) -or $launch.ExitCode -ne 0) {
        throw 'Disposable launcher failed.'
    }
    $script:orbitPid = 0
    Wait-Until {
        $candidate = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'OrbitNavigator.App.exe' -and
            $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath) -eq $Application
        } | Select-Object -First 1
        if ($candidate) { $script:orbitPid = $candidate.ProcessId; return $true }
        $false
    } 'Disposable Orbit process did not start.'
    $process = Get-Process -Id $script:orbitPid
    Wait-Until { (Roots-For $process.Id).Count -eq 1 } 'Disposable normal window did not appear.'
    $process
}

function Stop-Orbit($Process) {
    if (-not $Process -or $Process.HasExited) { return }
    foreach ($root in @(Roots-For $Process.Id)) {
        try { $root.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    }
    if (-not $Process.WaitForExit(10000)) { Stop-Process -Id $Process.Id -Force }
}

function Capture-Window($Root, [string]$Path) {
    $handle = [IntPtr]$Root.Current.NativeWindowHandle
    [void][OrbitPrivateAcceptanceNative]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 500
    $rect = [OrbitPrivateAcceptanceNative+RECT]::new()
    if (-not [OrbitPrivateAcceptanceNative]::GetWindowRect($handle, [ref]$rect)) {
        throw 'Could not read private window bounds.'
    }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
    [pscustomobject]@{
        Width = $rect.Right - $rect.Left
        Height = $rect.Bottom - $rect.Top
        Path = $Path
    }
}

function File-Map([string]$Root) {
    $map = [ordered]@{}
    if (Test-Path -LiteralPath $Root) {
        foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($Root, $file.FullName)
            $map[$relative] = [pscustomobject]@{
                Length = $file.Length
                Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            }
        }
    }
    $map
}

function Persistent-OrbitMap([string]$Root) {
    $filtered = [ordered]@{}
    if (Test-Path -LiteralPath $Root) {
        foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($Root, $file.FullName)
            if ($relative -like 'profiles\data\*browser.workspace-ui*' -or
                $relative -like 'profiles\data\*browser.workspace-presets*' -or
                $relative -like 'profiles\data\*browser.tab-groups*' -or
                $relative -like 'profiles\data\*browser.workspace-artwork*' -or
                $relative -like 'profiles\data\*browser.affiliated-sites*') {
                $filtered[$relative] = [pscustomobject]@{
                    Length = $file.Length
                    Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                }
            }
        }
    }
    $filtered
}

function Compare-Maps($Before, $After) {
    $keys = @($Before.Keys + $After.Keys | Sort-Object -Unique)
    @($keys | Where-Object {
        -not $Before.Contains($_) -or -not $After.Contains($_) -or
        $Before[$_].Length -ne $After[$_].Length -or $Before[$_].Hash -ne $After[$_].Hash
    })
}

$install = [IO.Path]::GetFullPath($InstallRoot)
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$acceptance = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptance + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe acceptance profile path.'
}
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$launcher = Join-Path $install 'Orbit Navigator.exe'
$application = [IO.Path]::GetFullPath((Join-Path $install 'app\OrbitNavigator.App.exe'))
if (-not (Test-Path $launcher) -or -not (Test-Path $application)) { throw 'Disposable payload incomplete.' }

$process = $null
try {
    $process = Start-Orbit $launcher $application $profile
    $normalRoot = $null
    Wait-Until {
        $script:normalRoot = Roots-For $process.Id | Where-Object {
            $null -ne (Find-Named $_ 'Search or enter address')
        } | Select-Object -First 1
        $null -ne $script:normalRoot
    } 'Normal New Tab did not become ready.'
    $normalRoot = $script:normalRoot
    $normalHandle = $normalRoot.Current.NativeWindowHandle
    $persistentBefore = Persistent-OrbitMap $profile
    $privateStatusText = 'PRIVATE BROWSING  ·  local session only  ·  sync and saved workspace changes stay off'

    $privateButton = $null
    Wait-Until {
        $script:privateButton = Find-Named $normalRoot 'New private window'
        $null -ne $script:privateButton -and $script:privateButton.Current.IsEnabled
    } 'New private window is unavailable.'
    $privateButton = $script:privateButton
    Invoke-Element $privateButton
    $script:privateRoot = $null
    Wait-Until {
        $script:privateRoot = Roots-For $process.Id | Where-Object {
            $_.Current.NativeWindowHandle -ne $normalHandle -and
            $null -ne (Find-Named $_ $privateStatusText)
        } | Select-Object -First 1
        $null -ne $script:privateRoot
    } 'Private New Tab window did not appear.'
    $privateRoot = $script:privateRoot
    $privateIndicator = Find-Named $privateRoot 'Private window'
    $privateStatus = Find-Named $privateRoot $privateStatusText
    if (-not $privateIndicator -or -not $privateStatus) {
        throw 'Private window did not expose the required privacy identity/status through UIA.'
    }
    $privateCapture = Capture-Window $privateRoot (Join-Path $evidence 'row09-private-new-tab.png')

    $quickView = Find-Named $privateRoot 'Quick View lower-left launcher'
    if ($quickView -and -not $quickView.Current.IsOffscreen) {
        throw 'Quick View launcher was visible in private browsing.'
    }
    $newPrivate = Find-Named $privateRoot 'New private window'
    if (-not $newPrivate -or $newPrivate.Current.IsEnabled) {
        throw 'Private window could open a nested private window.'
    }
    $visualToggle = $privateRoot.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -in @('Stellar view · switch to Basic', 'Basic view · switch to Stellar') } |
        Select-Object -First 1
    if (-not $visualToggle -or $visualToggle.Current.IsEnabled) {
        throw 'Private New Tab appearance mutation was not disabled.'
    }
    $createWorkspace = Find-Named $privateRoot 'Create a workspace'
    if (-not $createWorkspace -or $createWorkspace.Current.IsEnabled) {
        throw 'Private workspace mutation was not disabled.'
    }

    $uiaInventory = $privateRoot.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object {
            [pscustomobject]@{
                Name = $_.Current.Name
                ControlType = $_.Current.ControlType.ProgrammaticName
                Enabled = $_.Current.IsEnabled
                Offscreen = $_.Current.IsOffscreen
                HelpText = $_.Current.HelpText
            }
        } | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) }
    $uiaInventory | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidence 'row09-private-uia.json') -Encoding utf8

    # A private session can still create in-memory tabs, but none may survive disposal.
    $openPrivateTab = Find-Named $privateRoot 'Open new tab'
    if (-not $openPrivateTab -or -not $openPrivateTab.Current.IsEnabled) {
        throw 'Private Open new tab control was unavailable.'
    }
    Invoke-Element $openPrivateTab
    Wait-Until {
        @($privateRoot.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.Condition]::TrueCondition) | Where-Object {
                $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and
                $_.Current.Name -like 'Close tab — *'
            }).Count -ge 2
    } 'Private Open new tab did not create a session-only tab.'
    $privateTabCount = @($privateRoot.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition) | Where-Object {
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and
            $_.Current.Name -like 'Close tab — *'
        }).Count

    $privateRoot.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-Until { (Roots-For $process.Id).Count -eq 1 } 'Private window did not close.' 15
    Wait-Until {
        -not (Test-Path (Join-Path $profile 'webview\private')) -or
        @(Get-ChildItem -LiteralPath (Join-Path $profile 'webview\private') -Recurse -File -ErrorAction SilentlyContinue).Count -eq 0
    } 'Private WebView session files remained after close.' 15
    $persistentAfterFirstClose = Persistent-OrbitMap $profile
    $persistentDiff = Compare-Maps $persistentBefore $persistentAfterFirstClose
    if ($persistentDiff.Count -ne 0) {
        throw "Private session mutated durable browser preference/workspace storage: $($persistentDiff -join ', ')"
    }

    # Reopen: a fresh private session must begin with one empty tab, not the two-tab prior state.
    $normalRoot = (Roots-For $process.Id)[0]
    Invoke-Element (Find-Named $normalRoot 'New private window')
    $script:reopened = $null
    Wait-Until {
        $script:reopened = Roots-For $process.Id | Where-Object {
            $_.Current.NativeWindowHandle -ne $normalHandle -and
            $null -ne (Find-Named $_ $privateStatusText)
        } | Select-Object -First 1
        $null -ne $script:reopened
    } 'Fresh private window did not reopen.'
    $reopenCount = @($script:reopened.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition) | Where-Object {
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and
            $_.Current.Name -like 'Close tab — *'
        }).Count
    if ($reopenCount -ne 1) { throw "Private session state returned after reopen ($reopenCount tabs)." }
    $script:reopened.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-Until { (Roots-For $process.Id).Count -eq 1 } 'Reopened private window did not close.' 15
    Wait-Until {
        -not (Test-Path (Join-Path $profile 'webview\private')) -or
        @(Get-ChildItem -LiteralPath (Join-Path $profile 'webview\private') -Recurse -File -ErrorAction SilentlyContinue).Count -eq 0
    } 'Reopened private session files remained after close.' 15

    [pscustomobject]@{
        Row = 9
        Result = 'PASS'
        PrivateIdentityExposed = $true
        PrivateStatus = $privateStatusText
        PrivateCapture = $privateCapture
        QuickViewVisible = $false
        NestedPrivateEnabled = $false
        WorkspaceMutationEnabled = $false
        AppearanceMutationEnabled = $false
        SessionTabCountBeforeClose = $privateTabCount
        FreshPrivateTabCount = $reopenCount
        DurableStorageDifferences = @($persistentDiff)
        PrivateWebViewFilesAfterClose = 0
        ProfileRoot = $profile
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'row09-result.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $evidence 'row09-result.json')
}
finally {
    if ($process) { Stop-Orbit $process }
}
