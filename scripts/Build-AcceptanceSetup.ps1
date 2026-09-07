[CmdletBinding()]
param(
    [string]$Version = "0.1.18-acceptance",
    [string]$InnoCompiler
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Common.ps1")
$projectRoot = Get-OrbitProjectRoot
$dotnet = Get-OrbitDotNet
$InnoCompiler = Get-OrbitInnoCompiler -ExplicitPath $InnoCompiler
if (-not $InnoCompiler) { throw "Inno Setup compiler is required." }

$publishRoot = Join-Path $projectRoot "artifacts\publish"
$launcherOutput = Join-Path $publishRoot "launcher"
$appOutput = Join-Path $publishRoot "app"
$toolsOutput = Join-Path $appOutput "tools"
$acceptanceRoot = Join-Path $projectRoot "artifacts\acceptance"
$distRoot = Join-Path $acceptanceRoot "dist"

function Reset-ExactDirectory([string]$Path, [string]$Expected) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $expectedResolved = [IO.Path]::GetFullPath($Expected)
    if (-not $resolved.Equals($expectedResolved, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset unexpected directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
}

Reset-ExactDirectory $launcherOutput (Join-Path $projectRoot "artifacts\publish\launcher")
Reset-ExactDirectory $appOutput (Join-Path $projectRoot "artifacts\publish\app")
New-Item -ItemType Directory -Path $toolsOutput -Force | Out-Null
Reset-ExactDirectory $distRoot (Join-Path $projectRoot "artifacts\acceptance\dist")

& $dotnet publish (Join-Path $projectRoot "src\OrbitNavigator.Launcher\OrbitNavigator.Launcher.csproj") `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:Version=$Version --output $launcherOutput --nologo
Assert-LastExitCode "Acceptance launcher publish"
& $dotnet publish (Join-Path $projectRoot "src\OrbitNavigator.App\OrbitNavigator.App.csproj") `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:Version=$Version --output $appOutput --nologo
Assert-LastExitCode "Acceptance App publish"
& $dotnet publish (Join-Path $projectRoot "src\OrbitNavigator.UpdateRunner\OrbitNavigator.UpdateRunner.csproj") `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:Version=$Version --output $toolsOutput --nologo
Assert-LastExitCode "Acceptance update runner publish"

$setupSource = Join-Path $projectRoot "installer\OrbitNavigator.Setup.iss"
$acceptanceAppId = "{{C5104F72-0D7B-4DA9-B59C-BC02DF0478C2}"
& $InnoCompiler "/DMyAppVersion=$Version" "/DMyAppId=$acceptanceAppId" `
    "/O$distRoot" "/FSetup-Acceptance" $setupSource
Assert-LastExitCode "Acceptance Setup compilation"

Write-Output (Join-Path $distRoot "Setup-Acceptance.exe")
