[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$required = @(
    'LICENSE',
    'NOTICE',
    'README.md',
    'GOVERNANCE.md',
    'CONTRIBUTING.md',
    'SECURITY.md',
    'PRIVACY.md',
    'SUPPORT.md',
    'TRADEMARKS.md',
    'CODE_SIGNING_POLICY.md',
    'THIRD-PARTY-NOTICES.md',
    'assets\README.md',
    'docs\development\ReproducibleBuilds.md',
    'docs\open-source\PublicationChecklist.md',
    '.github\ISSUE_TEMPLATE\bug_report.yml',
    '.github\workflows\build.yml',
    'third-party\Microsoft.Web.WebView2\LICENSE.txt',
    'third-party\Microsoft.Web.WebView2\NOTICE.txt',
    'third-party\dotnet\LICENSE.txt',
    'third-party\dotnet\ThirdPartyNotices.txt'
)

$missing = @($required | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $projectRoot $_) -PathType Leaf)
})
if ($missing.Count -gt 0) {
    throw "Required open-source files are missing: $($missing -join ', ')"
}

$license = Get-Content -LiteralPath (Join-Path $projectRoot 'LICENSE') -Raw
if ($license -notmatch 'Mozilla Public License Version 2\.0') {
    throw 'LICENSE is not the canonical MPL-2.0 license text.'
}

[xml]$buildProps = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$expression = [string]$buildProps.Project.PropertyGroup.PackageLicenseExpression
if ($expression -ne 'MPL-2.0') {
    throw 'Directory.Build.props must declare PackageLicenseExpression MPL-2.0.'
}

$installer = Get-Content -LiteralPath (Join-Path $projectRoot 'installer\OrbitNavigator.Setup.iss') -Raw
if ($installer -notmatch '(?m)^LicenseFile=\.\.\\LICENSE\s*$') {
    throw 'The installer must display the repository LICENSE.'
}

$appProject = Get-Content -LiteralPath (Join-Path $projectRoot 'src\OrbitNavigator.App\OrbitNavigator.App.csproj') -Raw
foreach ($packagedNotice in @('LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'PRIVACY.md')) {
    if ($appProject -notmatch [regex]::Escape($packagedNotice)) {
        throw "The App publish does not include $packagedNotice."
    }
}
if ($appProject -notmatch [regex]::Escape('third-party\**\*')) {
    throw 'The App publish does not include complete third-party notices.'
}

$projects = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src'), (Join-Path $projectRoot 'tests') -Recurse -File -Filter '*.csproj')
$missingLocks = @($projects | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'packages.lock.json') -PathType Leaf)
})
if ($missingLocks.Count -gt 0) {
    throw "NuGet lock files are missing beside: $($missingLocks.FullName -join ', ')"
}

$forbiddenPatterns = @('*.pfx', '*.p12', '*.key', '*.pem', '*.dpapi.json')
$forbidden = foreach ($pattern in $forbiddenPatterns) {
    Get-ChildItem -LiteralPath $projectRoot -Recurse -File -Filter $pattern |
        Where-Object {
            $_.FullName -notlike (Join-Path $projectRoot '.tools\*') -and
            $_.FullName -notlike (Join-Path $projectRoot 'artifacts\*') -and
            $_.FullName -notlike (Join-Path $projectRoot 'dist\*')
        }
}
if (@($forbidden).Count -gt 0) {
    throw 'Potential private signing material exists inside the source tree.'
}

[pscustomobject]@{
    Result = 'PASS'
    License = 'MPL-2.0'
    RequiredFiles = $required.Count
    LockedProjects = $projects.Count
    SigningStatus = 'Not yet enrolled with SignPath Foundation'
    ExternalPublicationGates = @(
        'Confirm public-release rights for every original source and artwork file',
        'Assign public author/reviewer/release-approver identities',
        'Confirm maintainer MFA is enabled',
        'Select and configure donation destinations',
        'Complete the SignPath Foundation application and acceptance'
    )
} | ConvertTo-Json -Depth 4
