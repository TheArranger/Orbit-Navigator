[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($allowed + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The upgraded fixture root must be inside the disposable Orbit acceptance directory.'
}

$profileId = [Guid]::NewGuid()
$windowId = [Guid]::NewGuid()
$validGroup = [Guid]::NewGuid()
$emptyGroup = [Guid]::NewGuid()
$orphanGroup = [Guid]::NewGuid()
$missingTab = [Guid]::NewGuid()
$tabIds = @(1..16 | ForEach-Object { [Guid]::NewGuid() })
$workspaceOne = [Guid]::NewGuid()
$workspaceTwo = [Guid]::NewGuid()
$staleWindow = [Guid]::NewGuid()

$profilesDirectory = Join-Path $profile 'profiles'
New-Item -ItemType Directory -Path $profilesDirectory -Force | Out-Null
[IO.File]::WriteAllText(
    (Join-Path $profilesDirectory 'default.id'),
    $profileId.ToString('N'))
$storageRoot = Join-Path $profile 'profiles\data'

function New-Identifier([Guid]$Value) {
    [ordered]@{ Value = $Value.ToString('D') }
}

function Write-ProfileEntry(
    [string]$Namespace,
    [string]$Key,
    [byte[]]$Payload) {
    $keyHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($Key))).ToLowerInvariant()
    $directory = Join-Path $storageRoot (Join-Path $profileId.ToString('N') (
        Join-Path 'normal\persistent' $Namespace))
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $bytes = [byte[]]::new(16 + $Payload.Length)
    [Guid]::NewGuid().ToByteArray().CopyTo($bytes, 0)
    $Payload.CopyTo($bytes, 16)
    [IO.File]::WriteAllBytes((Join-Path $directory ($keyHash + '.bin')), $bytes)
}

$tabs = @()
for ($index = 0; $index -lt 16; $index++) {
    $groupId = if ($index -lt 5) {
        New-Identifier $validGroup
    } elseif ($index -eq 5) {
        New-Identifier $orphanGroup
    } else {
        $null
    }
    $title = if ($index -eq 7) { '' } else { 'Legacy Tab {0:d2}' -f ($index + 1) }
    $tabs += [ordered]@{
        TabId = New-Identifier $tabIds[$index]
        Address = $null
        Title = $title
        GroupId = $groupId
    }
}

$groups = @(
    [ordered]@{
        GroupId = New-Identifier $validGroup
        Name = ' Retained Legacy Group '
        IsCollapsed = $true
        ColorToken = 'Violet'
        IsTemporary = $false
        TabOrder = @((New-Identifier $missingTab)) + @(
            $tabIds[0..4] | ForEach-Object { New-Identifier $_ })
    },
    [ordered]@{
        GroupId = New-Identifier $emptyGroup
        Name = 'Empty Legacy Group'
        IsCollapsed = $false
        ColorToken = 'Gold'
        IsTemporary = $true
        TabOrder = @((New-Identifier $missingTab))
    }
)
$session = [ordered]@{
    WindowId = New-Identifier $windowId
    SelectedTabId = New-Identifier $missingTab
    Tabs = $tabs
    Groups = $groups
} | ConvertTo-Json -Depth 20 -Compress
Write-ProfileEntry 'browser.workspace-session' 'primary' (
    [Text.Encoding]::UTF8.GetBytes($session))

# Exercise the upgraded-profile placement regression from an authoritative,
# persisted Detached + Right state rather than relying on a previous run.
$workspacePreferences = [ordered]@{
    TabStripPlacement = 2
    CollapseToActive = $false
    ShowOrbitalGroupPreview = $true
    CompactTabs = $false
    NewTabMode = 0
    PreviewMode = 0
    AffiliatedRailPlacement = 0
    ShowAffiliatedRail = $true
} | ConvertTo-Json -Compress
Write-ProfileEntry 'browser.workspace-ui' 'preferences' (
    [Text.Encoding]::UTF8.GetBytes($workspacePreferences))

$tabControllerLayout = [ordered]@{
    DockState = 1
    DetachedBounds = [ordered]@{
        Left = 120
        Top = 120
        Width = 480
        Height = 560
    }
} | ConvertTo-Json -Compress
Write-ProfileEntry 'browser.tab-controller-layout' 'layout' (
    [Text.Encoding]::UTF8.GetBytes($tabControllerLayout))

$legacyCurrent = @(
    [ordered]@{
        GroupId = New-Identifier $validGroup
        Name = 'Retained Legacy Group'
        IsCollapsed = $true
        TabOrder = @($tabIds[0..4] | ForEach-Object { New-Identifier $_ })
        UpdatedAtUtc = '2026-08-18T18:31:57-07:00'
    }) | ConvertTo-Json -Depth 12 -Compress -AsArray
Write-ProfileEntry 'browser.tab-groups' ('window:{0}' -f $windowId.ToString('N')) (
    [Text.Encoding]::UTF8.GetBytes($legacyCurrent))

$legacyStale = @(
    [ordered]@{
        GroupId = New-Identifier $emptyGroup
        Name = 'Empty Legacy Group'
        IsCollapsed = $false
        TabOrder = @()
        UpdatedAtUtc = '2026-08-18T18:31:57-07:00'
    },
    [ordered]@{
        GroupId = New-Identifier $orphanGroup
        Name = 'Orphan Legacy Group'
        IsCollapsed = $false
        TabOrder = @((New-Identifier $missingTab))
        UpdatedAtUtc = '2026-08-18T18:31:57-07:00'
    }) | ConvertTo-Json -Depth 12 -Compress
Write-ProfileEntry 'browser.tab-groups' ('window:{0}' -f $staleWindow.ToString('N')) (
    [Text.Encoding]::UTF8.GetBytes($legacyStale))

$created = '2026-08-18T18:31:57-07:00'
$legacyWorkspaces = @(
    [ordered]@{
        Id = [ordered]@{
            ProfileId = New-Identifier $profileId
            Value = $workspaceOne.ToString('D')
        }
        Name = 'Legacy My Orbit Workspace'
        GroupName = 'Legacy Research'
        Tabs = @([ordered]@{
            Target = 'https://my-orbit.ping-it.cc/path?q=1'
            DisplayTitle = 'My Orbit legacy'
        })
        CreatedAtUtc = $created
        UpdatedAtUtc = $created
    },
    [ordered]@{
        Id = [ordered]@{
            ProfileId = New-Identifier $profileId
            Value = $workspaceTwo.ToString('D')
        }
        Name = 'Unrelated Workspace'
        GroupName = 'References'
        Tabs = @([ordered]@{
            Target = 'https://example.com/reference'
            DisplayTitle = 'Unrelated origin'
        })
        CreatedAtUtc = $created
        UpdatedAtUtc = $created
    }) | ConvertTo-Json -Depth 20 -Compress
Write-ProfileEntry 'browser.workspace-presets' 'catalog' (
    [Text.Encoding]::UTF8.GetBytes($legacyWorkspaces))

$manifest = [ordered]@{
    ProfileId = $profileId.ToString('D')
    WindowId = $windowId.ToString('D')
    Tabs = 16
    ValidRetainedGroupId = $validGroup.ToString('D')
    ExpectedValidTabIds = @($tabIds[0..4] | ForEach-Object { $_.ToString('D') })
    EmptyGroupId = $emptyGroup.ToString('D')
    OrphanGroupId = $orphanGroup.ToString('D')
    MissingTabId = $missingTab.ToString('D')
    LegacyGroupNamespaceFiles = 2
    LegacyWorkspaceSchema = 'raw-array-v1'
    ExpectedTabStripPlacement = 'Right'
    ExpectedTabControllerState = 'Detached'
    ExpectedMyOrbit = 'https://my-orbit.snap-it.cc/path?q=1'
    ExpectedUnrelated = 'https://example.com/reference'
    SeedFileCount = @(Get-ChildItem -LiteralPath $profile -File -Recurse).Count
}
$output = [IO.Path]::GetFullPath($EvidencePath)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force | Out-Null
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding utf8
$manifest
