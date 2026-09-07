[CmdletBinding()]
param(
    [string]$InnoCompiler,
    [switch]$RequireInnoCompiler,
    [switch]$RequireWebView2Payload
)

. (Join-Path $PSScriptRoot "Common.ps1")

$projectRoot = Get-OrbitProjectRoot
$payloadPath = Join-Path $projectRoot "installer\payloads\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
$checksumPath = Join-Path $projectRoot "installer\payloads\MicrosoftEdgeWebView2RuntimeInstallerX64.sha256"

$InnoCompiler = Get-OrbitInnoCompiler -ExplicitPath $InnoCompiler

$checks = [System.Collections.Generic.List[object]]::new()
$checks.Add([pscustomobject]@{ Name = 'Inno Setup compiler'; Ready = [bool]($InnoCompiler -and (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)); Detail = $InnoCompiler })

try {
    $verified = Test-OrbitWebView2Payload -PayloadPath $payloadPath -ChecksumPath $checksumPath
    $checks.Add([pscustomobject]@{ Name = 'WebView2 offline runtime'; Ready = $true; Detail = $verified.Sha256 })
} catch {
    $checks.Add([pscustomobject]@{ Name = 'WebView2 offline runtime'; Ready = $false; Detail = $_.Exception.Message })
}

$checks | Format-Table -AutoSize

if ($RequireInnoCompiler -and -not $checks[0].Ready) {
    throw 'Inno Setup compiler is required. Install/stage Inno Setup 6 or later, or pass -InnoCompiler.'
}
if ($RequireWebView2Payload -and -not $checks[1].Ready) {
    throw 'A pinned, Microsoft-signed offline WebView2 runtime is required. Run Acquire-WebView2.ps1 with an independently verified SHA-256.'
}
