[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

. (Join-Path $PSScriptRoot "Common.ps1")
$projectRoot = Get-OrbitProjectRoot
$dotnet = Get-OrbitDotNet

& $dotnet build (Join-Path $projectRoot "OrbitNavigator.sln") `
    --configuration $Configuration `
    --nologo
Assert-LastExitCode "Orbit Navigator build"

