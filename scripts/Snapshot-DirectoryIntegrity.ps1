[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Root,
    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$snapshots = foreach ($rootPath in $Root) {
    $resolved = [IO.Path]::GetFullPath($rootPath).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Integrity root is missing: $resolved"
    }
    $files = @(Get-ChildItem -LiteralPath $resolved -File -Recurse -Force | Sort-Object FullName)
    [pscustomobject]@{
        Root = $resolved
        FileCount = $files.Count
        Files = @($files | ForEach-Object {
            [pscustomobject]@{
                RelativePath = $_.FullName.Substring($resolved.Length).TrimStart('\')
                Length = $_.Length
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        })
    }
}

$output = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$json = $snapshots | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($output, $json)
Write-Output $output
