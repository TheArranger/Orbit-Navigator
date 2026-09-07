[CmdletBinding()]
param(
    [string]$ApplicationPath = (Join-Path $PSScriptRoot "..\src\OrbitNavigator.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\OrbitNavigator.App.exe"),
    [string]$DotNetHost = (Join-Path $PSScriptRoot "..\.tools\dotnet\dotnet.exe"),
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitResourceSmokeNative
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
"@

function Wait-OrbitCondition {
    param([scriptblock]$Condition, [string]$Failure)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try { if (& $Condition) { return } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Find-OrbitElement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [bool]$Required = $true)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($Required -and $null -eq $element) { throw "UI Automation element not found: $Name" }
    return $element
}

function Find-OrbitButton {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $typeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $matches = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new($nameCondition, $typeCondition))
    for ($index = 0; $index -lt $matches.Count; $index++) {
        $candidate = $matches.Item($index)
        Write-Verbose ("Resource command candidate: enabled={0}, offscreen={1}, class={2}, id={3}, status={4}" -f `
            $candidate.Current.IsEnabled,
            $candidate.Current.IsOffscreen,
            $candidate.Current.ClassName,
            $candidate.Current.AutomationId,
            $candidate.Current.ItemStatus)
        if ($candidate.Current.IsEnabled -and -not $candidate.Current.IsOffscreen) {
            return $candidate
        }
    }
    return $null
}

function Get-OrbitWindow {
    param([int]$ProcessId, [string]$Name)
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $windows = $desktop.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    for ($index = 0; $index -lt $windows.Count; $index++) {
        $candidate = $windows.Item($index)
        if ($candidate.Current.ProcessId -eq $ProcessId -and $candidate.Current.Name -eq $Name) {
            return $candidate
        }
    }
    return $null
}

function Get-OrbitWindowEnding {
    param([int]$ProcessId, [string]$TitleSuffix)
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $windows = $desktop.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new($processCondition, $windowCondition))
    for ($index = 0; $index -lt $windows.Count; $index++) {
        $candidate = $windows.Item($index)
        if ($candidate.Current.ProcessId -eq $ProcessId -and
            $candidate.Current.Name.EndsWith($TitleSuffix, [StringComparison]::Ordinal)) {
            return $candidate
        }
    }
    return $null
}

$application = [IO.Path]::GetFullPath($ApplicationPath)
$dotnet = [IO.Path]::GetFullPath($DotNetHost)
if (Get-Process -Name "OrbitNavigator.App" -ErrorAction SilentlyContinue) {
    throw "Release resource smoke requires every Orbit Navigator process to be closed."
}

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $dotnet
$startInfo.UseShellExecute = $false
$startInfo.ArgumentList.Add([IO.Path]::ChangeExtension($application, ".dll"))
$process = $null
try {
    $process = [Diagnostics.Process]::Start($startInfo)
    Wait-OrbitCondition {
        $script:main = Get-OrbitWindow $process.Id "Orbit Navigator"
        $script:popOutCommand = if ($null -ne $script:main) {
            Find-OrbitButton $script:main "Pop out tab controller"
        } else {
            $null
        }
        $script:restoredTabs = Get-OrbitWindowEnding $process.Id "Tabs"
        ($null -ne $script:popOutCommand -or $null -ne $script:restoredTabs) -and
            $null -ne (Find-OrbitElement $script:main "Address and search" $false)
    } "The Release browser did not finish startup with its tab-controller command."
    Start-Sleep -Milliseconds 500

    $script:main = Get-OrbitWindow $process.Id "Orbit Navigator"
    Wait-OrbitCondition {
        $script:tabs = Get-OrbitWindowEnding $process.Id "Tabs"
        $script:main = Get-OrbitWindow $process.Id "Orbit Navigator"
        $script:popOutCommand = if ($null -ne $script:main) {
            Find-OrbitButton $script:main "Pop out tab controller"
        } else {
            $null
        }
        $null -ne $script:tabs -or $null -ne $script:popOutCommand
    } "The tab controller did not finish restoring its dock state."
    if ($null -eq $script:tabs) {
        [void][OrbitResourceSmokeNative]::SetForegroundWindow(
            [IntPtr]$script:main.Current.NativeWindowHandle)
        Start-Sleep -Milliseconds 150
        $script:popOutCommand.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-OrbitCondition {
            $script:tabs = Get-OrbitWindowEnding $process.Id "Tabs"
            $null -ne $script:tabs
        } "The detached tab controller did not open for the resource smoke."
    }

    $script:resourceCommand = Find-OrbitButton $script:tabs "Browser resources"
    if ($null -eq $script:resourceCommand) {
        throw "The detached tab controller did not expose Browser resources."
    }
    [void][OrbitResourceSmokeNative]::SetForegroundWindow(
        [IntPtr]$script:tabs.Current.NativeWindowHandle)
    $script:resourceCommand.SetFocus()
    Start-Sleep -Milliseconds 150
    $script:resourceCommand.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:monitor = Get-OrbitWindowEnding $process.Id "Browser resources window"
        $null -ne $script:monitor
    } "The separate Browser resources monitor did not open."

    $cpu = Find-OrbitElement $script:monitor "CPU trend graph"
    $memory = Find-OrbitElement $script:monitor "Memory trend graph"
    Wait-OrbitCondition {
        $script:firstCpu = $cpu.Current.ItemStatus
        $script:firstMemory = $memory.Current.ItemStatus
        $script:firstCpu -like "*Sampling active*" -and $script:firstMemory -like "*Sampling active*"
    } "The monitor did not publish a real active CPU/memory sample."

    $stableCpu = $script:firstCpu
    Start-Sleep -Seconds 2
    $cpuAfterTwoSeconds = $cpu.Current.ItemStatus
    if ($cpuAfterTwoSeconds -ne $stableCpu) {
        throw "The CPU trend changed inside the five-second host cadence."
    }

    $sampleStart = [Diagnostics.Stopwatch]::StartNew()
    Wait-OrbitCondition {
        $script:secondCpu = $cpu.Current.ItemStatus
        $script:secondCpu -ne $stableCpu
    } "A second accepted resource sample did not arrive."
    $sampleStart.Stop()
    if ($sampleStart.Elapsed -lt [TimeSpan]::FromSeconds(2.5)) {
        throw "The second resource update arrived too quickly for the five-second cadence."
    }

    $monitorHandle = $script:monitor.Current.NativeWindowHandle
    $script:monitor.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-OrbitCondition {
        $null -eq (Get-OrbitWindowEnding $process.Id "Browser resources window")
    } "Closing Browser resources did not hide the monitor promptly."
    $process.Refresh()
    if ($process.HasExited) { throw "Hiding Browser resources closed the owner browser." }

    [pscustomobject]@{
        SeparateMonitorOpened = $true
        CpuAndMemoryActive = $true
        StableBetweenSamples = $true
        ConfiguredCadenceSeconds = 5
        ObservedSecondUpdateAfterSeconds = [Math]::Round($sampleStart.Elapsed.TotalSeconds + 2, 2)
        HiddenPromptly = $true
        OwnerRemainedOpen = $true
        MonitorHandle = $monitorHandle
    } | ConvertTo-Json -Compress
}
finally {
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
    }
}
