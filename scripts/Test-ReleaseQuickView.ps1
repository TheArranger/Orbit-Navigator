[CmdletBinding()]
param(
    [string]$ApplicationPath = (Join-Path $PSScriptRoot "..\src\OrbitNavigator.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\OrbitNavigator.App.exe"),
    [string]$DotNetHost = (Join-Path $PSScriptRoot "..\.tools\dotnet\dotnet.exe"),
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [switch]$SkipOfflineLibrary,
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$acceptanceRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptanceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Quick View profile must be inside the disposable acceptance root.'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitQuickViewSmokeNative
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
        catch [InvalidOperationException] { }
        Start-Sleep -Milliseconds 125
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

function Type-OrbitValue {
    param([System.Windows.Automation.AutomationElement]$Element, [string]$Value)
    $Element.SetFocus()
    Start-Sleep -Milliseconds 150
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
}

function Count-OrbitTabs {
    param([System.Windows.Automation.AutomationElement]$Root)
    @($Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $_.Current.Name -like "*, tab" -and
            $_.Current.HelpText -like "*Press Enter to switch*"
        }).Count
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

function Get-OrbitRect {
    param([System.Windows.Automation.AutomationElement]$Element)
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

function Get-QuickViewSurfaceEvidence {
    param(
        [System.Windows.Automation.AutomationElement]$CloseButton,
        [System.Windows.Automation.AutomationElement]$Owner
    )
    $ownerRect = Get-OrbitRect $Owner
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $chain = [Collections.Generic.List[object]]::new()
    $candidates = [Collections.Generic.List[object]]::new()
    $node = $CloseButton
    for ($index = 0; $null -ne $node -and $index -lt 16; $index++) {
        $rect = Get-OrbitRect $node
        $entry = [pscustomobject]@{
            Name = $node.Current.Name
            ControlType = $node.Current.ControlType.ProgrammaticName
            Rect = $rect
        }
        $chain.Add($entry)
        if ($rect.Width -ge 200 -and $rect.Height -ge 180 -and
            $rect.Width -lt $ownerRect.Width - 8 -and $rect.Height -lt $ownerRect.Height - 8) {
            $candidates.Add($entry)
        }
        $node = $walker.GetParent($node)
    }
    if ($candidates.Count -eq 0) {
        throw "Quick View popup bounds were not present in the UIA ancestor chain: $($chain | ConvertTo-Json -Depth 5 -Compress)"
    }
    $surface = $candidates | Sort-Object { $_.Rect.Width * $_.Rect.Height } -Descending | Select-Object -First 1
    [pscustomobject]@{ Bounds = $surface.Rect; AncestorChain = @($chain) }
}

function Save-OrbitCapture {
    param([System.Windows.Automation.AutomationElement]$Element, [string]$Path)
    $rect = $Element.Current.BoundingRectangle
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
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$dotnet = [IO.Path]::GetFullPath($DotNetHost)
$applicationDll = [IO.Path]::ChangeExtension($application, ".dll")
$sameOutputProcesses = @(Get-CimInstance Win32_Process | Where-Object {
    $_.CommandLine -and $_.CommandLine.Contains($applicationDll, [StringComparison]::OrdinalIgnoreCase)
})
if ($sameOutputProcesses.Count) {
    throw "The selected Release output is already running."
}
$port = Get-Random -Minimum 50000 -Maximum 50999
$pythonSource = @'
import http.server, socketserver
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        quick = self.path.startswith('/quick')
        title = ('Quick View Enter' if self.path.startswith('/quick/enter') else
                 'Quick View Button' if self.path.startswith('/quick/button') else
                 'Quick View Page' if quick else 'Orbit Quick View Host')
        marker = ('Quick View Enter content' if self.path.startswith('/quick/enter') else
                  'Quick View Button content' if self.path.startswith('/quick/button') else
                  'Ephemeral Quick View content' if quick else 'Normal browser content')
        page = ('<!doctype html><html><head><meta charset="utf-8"><title>' + title +
                '</title></head><body style="font:24px Segoe UI;background:#0b1117;color:white">' +
                marker + '</body></html>').encode()
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(page)))
        self.end_headers()
        self.wfile.write(page)
    def log_message(self, *args): pass
socketserver.TCPServer.allow_reuse_address = True
with socketserver.TCPServer(('127.0.0.1', __PORT__), Handler) as server: server.serve_forever()
'@.Replace("__PORT__", $port)
$serverInfo = [Diagnostics.ProcessStartInfo]::new()
$serverInfo.FileName = (Get-Command python -ErrorAction Stop).Source
$serverInfo.UseShellExecute = $false
$serverInfo.CreateNoWindow = $true
$serverInfo.ArgumentList.Add("-c")
$serverInfo.ArgumentList.Add($pythonSource)
$server = [Diagnostics.Process]::Start($serverInfo)
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $dotnet
$startInfo.UseShellExecute = $false
    $startInfo.ArgumentList.Add($applicationDll)
$runId = [Guid]::NewGuid()
$startInfo.ArgumentList.Add('--acceptance-profile-root')
$startInfo.ArgumentList.Add($profile)
$startInfo.ArgumentList.Add('--acceptance-run-id')
$startInfo.ArgumentList.Add($runId.ToString('D'))
$process = $null
try {
    Start-Sleep -Milliseconds 400
    if ($server.HasExited) { throw "Loopback Quick View server failed to start." }
    Invoke-WebRequest -Uri "http://127.0.0.1:$port/" -UseBasicParsing | Out-Null
    $process = [Diagnostics.Process]::Start($startInfo)
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $process.Id
        if ($null -eq $script:root) { return $false }
        $script:windowHandle = [IntPtr]$script:root.Current.NativeWindowHandle
        $null -ne (Find-OrbitElement $root "Address and search" $false)
    } "The Release browser chrome did not become ready."
    $dockController = Find-OrbitElement $root "Dock tab controller" $false
    if ($null -ne $dockController -and -not $dockController.Current.IsOffscreen) {
        $dockController.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-OrbitCondition {
            $dock = Find-OrbitElement $root "Dock tab controller" $false
            $null -eq $dock -or $dock.Current.IsOffscreen
        } "The detached controller did not dock before Quick View validation."
        $root = Get-OrbitMainRoot $process.Id
        Wait-OrbitCondition {
            $script:address = Find-OrbitElement $root "Address and search" $false
            $null -ne $script:address
        } "The omnibox did not return after docking the controller."
    }
    [void][OrbitQuickViewSmokeNative]::SetForegroundWindow($windowHandle)
    $root = Get-OrbitMainRoot $process.Id
    Wait-OrbitCondition {
        $script:address = Find-OrbitElement $root "Address and search" $false
        $null -ne $script:address
    } "The omnibox was not available before normal-site navigation."
    Type-OrbitValue $script:address "http://127.0.0.1:$port/"
    Wait-OrbitCondition {
        $null -ne (Find-OrbitElement $root "Normal browser content" $false) -or
            $null -ne (Find-OrbitElement $root "Orbit Quick View Host, tab" $false)
    } "The normal host page did not load."
    $tabsBefore = Count-OrbitTabs $root

    if (-not $SkipOfflineLibrary) {
        (Find-OrbitElement $root "Open browser menu").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-OrbitCondition {
            $script:offlineSave = Find-OrbitElement `
                ([System.Windows.Automation.AutomationElement]::RootElement) `
                "Save for offline" `
                $false
            $script:offlineLibrary = Find-OrbitElement `
                ([System.Windows.Automation.AutomationElement]::RootElement) `
                "Offline library" `
                $false
            $null -ne $script:offlineSave -and $script:offlineSave.Current.IsEnabled -and
                $null -ne $script:offlineLibrary -and $script:offlineLibrary.Current.IsEnabled
        } "Offline Reading commands were not enabled for the selected normal website."
        $script:offlineLibrary.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-OrbitCondition {
            $script:offlineWindow = Find-OrbitElement `
                ([System.Windows.Automation.AutomationElement]::RootElement) `
                "Offline library — Orbit Navigator" `
                $false
            $null -ne $script:offlineWindow -and -not $script:offlineWindow.Current.IsOffscreen
        } "The host-owned Offline library window did not open."
        $script:offlineWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    }

    [void][OrbitQuickViewSmokeNative]::SetForegroundWindow($windowHandle)
    $root = Get-OrbitMainRoot $process.Id
    Wait-OrbitCondition {
        $script:anchor = Find-OrbitElement $root "Submit Quick View search or address" $false
        $null -ne $script:anchor
    } "Quick View did not remain available after the Offline library closed."
    $anchor = $script:anchor
    $anchor.SetFocus()
    Wait-OrbitCondition {
        $script:quickSearch = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Quick View search or address" `
            $false
        $null -ne $script:quickSearch -and -not $script:quickSearch.Current.IsOffscreen
    } "The Quick View search field did not expand on keyboard focus."
    $script:quickSearch.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).SetValue("http://127.0.0.1:$port/quick")
    Wait-OrbitCondition {
        $script:quickSearch.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq
            "http://127.0.0.1:$port/quick"
    } "The Quick View query did not reach the WPF search field."
    $anchor.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:quickClose = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Close Quick View" `
            $false
        $null -ne $script:quickClose -and -not $script:quickClose.Current.IsOffscreen
    } "Quick View did not open its ephemeral surface."
    Wait-OrbitCondition {
        $null -ne (Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Ephemeral Quick View content" `
            $false)
    } "The Quick View WebView did not render the requested page."
    $ownerBounds = Get-OrbitRect $root
    $surfaceEvidence = Get-QuickViewSurfaceEvidence $script:quickClose $root
    $initialQuickViewBounds = $surfaceEvidence.Bounds
    $initialWidthRatio = $initialQuickViewBounds.Width / $ownerBounds.Width
    $initialHeightRatio = $initialQuickViewBounds.Height / $ownerBounds.Height
    if ($initialWidthRatio -lt 0.27 -or $initialHeightRatio -lt 0.27) {
        throw "Quick View did not open at the 30 percent baseline. Owner=$($ownerBounds | ConvertTo-Json -Compress) QuickView=$($initialQuickViewBounds | ConvertTo-Json -Compress)"
    }
    $initialCapture = Join-Path $evidence 'quick-view-initial-30-percent.png'
    Save-OrbitCapture $root $initialCapture
    $tabsOpen = Count-OrbitTabs $root
    if ($tabsOpen -ne $tabsBefore) { throw "Quick View incorrectly entered the authoritative tab catalog." }

    $quickSearch = Find-OrbitElement `
        ([System.Windows.Automation.AutomationElement]::RootElement) `
        "Quick View search or address"
    $initialQuickShellRuntimeId = [string]::Join(',', $quickSearch.GetRuntimeId())
    $quickSearch.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue(
        "http://127.0.0.1:$port/quick/enter")
    $quickSearch.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Wait-OrbitCondition {
        $script:enterTitle = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Quick View Enter" `
            $false
        $null -ne $script:enterTitle
    } "Physical Enter did not navigate the existing Quick View host."
    $enterCapture = Join-Path $evidence 'quick-view-after-physical-enter.png'
    Save-OrbitCapture $root $enterCapture
    $quickSearch = Find-OrbitElement `
        ([System.Windows.Automation.AutomationElement]::RootElement) `
        "Quick View search or address"
    Wait-OrbitCondition {
        $script:quickSearch = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Quick View search or address" `
            $false
        $null -ne $script:quickSearch -and
            $script:quickSearch.Current.ItemStatus -eq "Quick View loaded 127.0.0.1."
    } "Quick View did not report the accepted Enter navigation."
    $quickShellAfterEnter = Find-OrbitElement `
        ([System.Windows.Automation.AutomationElement]::RootElement) `
        "Quick View search or address"
    if ([string]::Join(',', $quickShellAfterEnter.GetRuntimeId()) -ne $initialQuickShellRuntimeId) {
        throw "Enter navigation replaced the Quick View presentation host."
    }

    $quickSearch.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue(
        "http://127.0.0.1:$port/quick/button")
    $submitQuickView = Find-OrbitElement `
        ([System.Windows.Automation.AutomationElement]::RootElement) `
        "Submit Quick View search or address"
    $submitQuickView.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:buttonTitle = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Quick View Button" `
            $false
        $null -ne $script:buttonTitle
    } "The visible Quick View search button did not navigate the existing host."
    $buttonCapture = Join-Path $evidence 'quick-view-after-search-button.png'
    Save-OrbitCapture $root $buttonCapture
    $quickShellAfterButton = Find-OrbitElement `
        ([System.Windows.Automation.AutomationElement]::RootElement) `
        "Quick View search or address"
    if ([string]::Join(',', $quickShellAfterButton.GetRuntimeId()) -ne $initialQuickShellRuntimeId) {
        throw "Search-button navigation replaced the Quick View presentation host."
    }
    $quickSearch = Find-OrbitElement `
        ([System.Windows.Automation.AutomationElement]::RootElement) `
        "Quick View search or address"
    Wait-OrbitCondition {
        $script:quickSearch = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Quick View search or address" `
            $false
        $null -ne $script:quickSearch -and
            $script:quickSearch.Current.ItemStatus -eq "Quick View loaded 127.0.0.1."
    } "Quick View did not report the accepted search-button navigation."
    $focusRetainedAfterNavigation = $script:quickSearch.Current.HasKeyboardFocus
    if (-not $focusRetainedAfterNavigation) {
        throw "Quick View did not restore focus to its Orbit search field after navigation."
    }

    (Find-OrbitElement `
        ([System.Windows.Automation.AutomationElement]::RootElement) `
        "Close Quick View").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $close = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Close Quick View" `
            $false
        $null -eq $close -or $close.Current.IsOffscreen
    } "Quick View did not detach and close."

    $anchor = Find-OrbitElement $root "Submit Quick View search or address"
    $anchor.SetFocus()
    Wait-OrbitCondition {
        $script:freshSearch = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Quick View search or address" `
            $false
        $null -ne $script:freshSearch -and -not $script:freshSearch.Current.IsOffscreen
    } "Quick View did not return ready for a fresh use."
    $script:freshSearch.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).SetValue("http://127.0.0.1:$port/quick/fresh")
    $script:freshSearch.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Wait-OrbitCondition {
        $close = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Close Quick View" `
            $false
        $null -ne $close -and -not $close.Current.IsOffscreen
    } "The fresh Quick View host did not reopen."
    Wait-OrbitCondition {
        $script:freshContent = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Quick View Page" `
            $false
        $null -ne $script:freshContent
    } "The fresh Quick View host did not render a fresh page."
    $freshCapture = Join-Path $evidence 'quick-view-fresh-reopen.png'
    Save-OrbitCapture $root $freshCapture
    (Find-OrbitElement `
        ([System.Windows.Automation.AutomationElement]::RootElement) `
        "Expand to normal tab").GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-OrbitCondition {
        $script:root = Get-OrbitMainRoot $process.Id
        $close = Find-OrbitElement `
            ([System.Windows.Automation.AutomationElement]::RootElement) `
            "Close Quick View" `
            $false
        ($null -eq $close -or $close.Current.IsOffscreen) -and
            (Count-OrbitTabs $root) -eq ($tabsBefore + 1)
    } "Quick View did not expand by address reload into exactly one authoritative tab."

    [pscustomobject]@{
        NormalTabCount = $tabsBefore
        TabCountWhileQuickViewOpen = $tabsOpen
        EphemeralContentRendered = $true
        FirstCloseDetached = $true
        FreshSecondHostOpened = $true
        KeyboardEnterOpenedSecondHost = $true
        OpenHostRuntimeId = $initialQuickShellRuntimeId
        EnterNavigatedExistingHost = $true
        SearchButtonNavigatedExistingHost = $true
        NavigationFocusRetained = $focusRetainedAfterNavigation
        NavigationStatus = "Quick View loaded 127.0.0.1."
        OwnerBounds = $ownerBounds
        InitialQuickViewBounds = $initialQuickViewBounds
        InitialQuickViewAncestorChain = $surfaceEvidence.AncestorChain
        InitialWidthRatioAgainstOwner = $initialWidthRatio
        InitialHeightRatioAgainstOwner = $initialHeightRatio
        InitialBaselineAtLeast27Percent = $true
        InitialCapture = $initialCapture
        EnterCapture = $enterCapture
        SearchButtonCapture = $buttonCapture
        FreshReopenCapture = $freshCapture
        ExpandedToNormalTab = $true
        ExpansionTransfer = "AddressReloadOnly"
        OfflineSaveAvailableOnNormalSite = -not $SkipOfflineLibrary
        OfflineLibraryWindowOpened = -not $SkipOfflineLibrary
    } | ConvertTo-Json -Compress
}
finally {
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
    }
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
