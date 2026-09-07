[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

. (Join-Path $PSScriptRoot "Common.ps1")
$projectRoot = Get-OrbitProjectRoot
$Version = if ($Version) { $Version } else { Get-OrbitProductVersion }
$dotnet = Get-OrbitDotNet
$publishRoot = Join-Path $projectRoot "artifacts\publish"
$launcherOutput = Join-Path $publishRoot "launcher"
$appOutput = Join-Path $publishRoot "app"
$updateRunnerOutput = Join-Path $appOutput "tools"
$rootLauncher = Join-Path $projectRoot "Orbit Navigator.exe"
$rootApp = Join-Path $projectRoot "app"

function Reset-OrbitPublishDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ExpectedPath
    )

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $expected = [System.IO.Path]::GetFullPath($ExpectedPath)
    if (-not $resolved.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset unexpected publish directory: $resolved"
    }

    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
}

Reset-OrbitPublishDirectory -Path $launcherOutput -ExpectedPath (Join-Path $publishRoot "launcher")
Reset-OrbitPublishDirectory -Path $appOutput -ExpectedPath (Join-Path $publishRoot "app")
New-Item -ItemType Directory -Path $updateRunnerOutput -Force | Out-Null
Reset-OrbitPublishDirectory -Path $rootApp -ExpectedPath (Join-Path $projectRoot "app")

& (Join-Path $PSScriptRoot "Publish-Launcher.ps1") -Version $Version -Configuration $Configuration

& $dotnet publish (Join-Path $projectRoot "src\OrbitNavigator.App\OrbitNavigator.App.csproj") `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:Version=$Version `
    --output $appOutput `
    --nologo
Assert-LastExitCode "Application publish"

& $dotnet publish (Join-Path $projectRoot "src\OrbitNavigator.UpdateRunner\OrbitNavigator.UpdateRunner.csproj") `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:Version=$Version `
    --output $updateRunnerOutput `
    --nologo
Assert-LastExitCode "Update runner publish"

Copy-Item -Path (Join-Path $appOutput "*") -Destination $rootApp -Recurse -Force

Write-Host "Root launcher: $rootLauncher"
Write-Host "Application payload: $rootApp"
