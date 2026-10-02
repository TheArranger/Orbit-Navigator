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
    throw "Required release files are missing: $($missing -join ', ')"
}

$license = Get-Content -LiteralPath (Join-Path $projectRoot 'LICENSE') -Raw
$expectedMitLicense = @'
MIT License
Copyright (c) 2026 Orbit Nav Pub
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@
if (($license -replace '\s+', ' ').Trim() -cne
    ($expectedMitLicense -replace '\s+', ' ').Trim()) {
    throw 'LICENSE must contain the unmodified standard MIT terms and Orbit Nav Pub copyright notice.'
}

[xml]$buildProps = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$licenseExpression = $buildProps.SelectSingleNode('//PackageLicenseExpression')
if ($null -eq $licenseExpression -or $licenseExpression.InnerText -cne 'MIT') {
    throw 'Directory.Build.props must advertise the SPDX MIT license expression.'
}

$firstPartyLicenseDocs = @(
    'NOTICE', 'README.md', 'CONTRIBUTING.md', 'GOVERNANCE.md', 'TRADEMARKS.md',
    'SUPPORT.md', 'assets\README.md', 'CODE_SIGNING_POLICY.md',
    'THIRD-PARTY-NOTICES.md', 'docs\open-source\PublicationChecklist.md'
)
foreach ($relativePath in $firstPartyLicenseDocs) {
    $document = Get-Content -LiteralPath (Join-Path $projectRoot $relativePath) -Raw
    if ($document -match 'source-visible proprietary|not licensed for modification|not open source|limited permission covering official') {
        throw "Conflicting first-party license language remains in $relativePath."
    }
}

$signingPolicy = Get-Content -LiteralPath (Join-Path $projectRoot 'CODE_SIGNING_POLICY.md') -Raw
if ($signingPolicy -notmatch 'No public-trust\s+signing provider has approved' -or
    $signingPolicy -notmatch 'no commercial dual licensing' -or
    $signingPolicy -notmatch 'System Libraries') {
    throw 'Signing policy must disclose unapproved status and the separate provider-eligibility gates.'
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
    License = 'MIT'
    RequiredFiles = $required.Count
    LockedProjects = $projects.Count
    SigningStatus = 'Unsigned; no public-trust signing-provider approval'
    ExternalPublicationGates = @(
        'Confirm release rights for every original source and artwork file',
        'Assign public author/reviewer/release-approver identities',
        'Confirm maintainer MFA is enabled',
        'Publish the reviewed release source and retain third-party notices',
        'Obtain signing-provider approval including WebView2 packaging eligibility'
    )
} | ConvertTo-Json -Depth 4
