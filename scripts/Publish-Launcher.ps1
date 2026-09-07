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
$launcherOutput = Join-Path $projectRoot "artifacts\publish\launcher"
$rootLauncher = Join-Path $projectRoot "Orbit Navigator.exe"

New-Item -ItemType Directory -Path $launcherOutput -Force | Out-Null

& $dotnet publish (Join-Path $projectRoot "src\OrbitNavigator.Launcher\OrbitNavigator.Launcher.csproj") `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:Version=$Version `
    --output $launcherOutput `
    --nologo
Assert-LastExitCode "Launcher publish"

Copy-Item -LiteralPath (Join-Path $launcherOutput "Orbit Navigator.exe") -Destination $rootLauncher -Force
Write-Host "Root launcher: $rootLauncher"
