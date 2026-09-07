[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$OutputPath,
    [string]$PngOutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $projectRoot "assets\branding\orbit-navigator-program-logo-v2.png"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $projectRoot "assets\branding\orbit-navigator.ico"
}
if ([string]::IsNullOrWhiteSpace($PngOutputDirectory)) {
    $PngOutputDirectory = Join-Path $projectRoot "assets\branding\windows"
}

$resolvedSource = [System.IO.Path]::GetFullPath($SourcePath)
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$resolvedPngDirectory = [System.IO.Path]::GetFullPath($PngOutputDirectory)
if (-not (Test-Path -LiteralPath $resolvedSource -PathType Leaf)) {
    throw "Approved identity source was not found: $resolvedSource"
}

$requiredSizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$source = [System.Drawing.Bitmap]::new($resolvedSource)
try {
    if ($source.Width -ne $source.Height -or $source.Width -lt 256) {
        throw "Approved identity source must be a square image of at least 256 pixels."
    }
    if (-not [System.Drawing.Image]::IsAlphaPixelFormat($source.PixelFormat)) {
        throw "Approved identity source must contain an alpha channel."
    }

    function Assert-TransparentIdentityBitmap {
        param(
            [Parameter(Mandatory)]
            [System.Drawing.Bitmap]$Bitmap,
            [Parameter(Mandatory)]
            [string]$Label
        )

        foreach ($point in @(
            [System.Drawing.Point]::new(0, 0),
            [System.Drawing.Point]::new($Bitmap.Width - 1, 0),
            [System.Drawing.Point]::new(0, $Bitmap.Height - 1),
            [System.Drawing.Point]::new($Bitmap.Width - 1, $Bitmap.Height - 1)
        )) {
            if ($Bitmap.GetPixel($point.X, $point.Y).A -ne 0) {
                throw "$Label has an opaque corner at $($point.X),$($point.Y)."
            }
        }

        $transparentPixels = 0
        $visiblePixels = 0
        $partialAlphaPixels = 0
        for ($y = 0; $y -lt $Bitmap.Height; $y++) {
            for ($x = 0; $x -lt $Bitmap.Width; $x++) {
                $alpha = $Bitmap.GetPixel($x, $y).A
                if ($alpha -eq 0) {
                    $transparentPixels++
                }
                else {
                    $visiblePixels++
                    if ($alpha -lt 255) {
                        $partialAlphaPixels++
                    }
                }
            }
        }

        $pixelCount = $Bitmap.Width * $Bitmap.Height
        if ($transparentPixels -lt [Math]::Ceiling($pixelCount * 0.20)) {
            throw "$Label does not retain enough transparent canvas."
        }
        if ($visiblePixels -lt [Math]::Ceiling($pixelCount * 0.10)) {
            throw "$Label is too faint or empty."
        }
        if ($partialAlphaPixels -eq 0) {
            throw "$Label lost all partial-alpha edge pixels."
        }
    }

    function New-IdentityPng {
        param(
            [Parameter(Mandatory)]
            [int]$PixelSize
        )

        $bitmap = [System.Drawing.Bitmap]::new(
            $PixelSize,
            $PixelSize,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $bitmap.SetResolution(96, 96)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::GammaCorrected
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage(
                $source,
                [System.Drawing.Rectangle]::new(0, 0, $PixelSize, $PixelSize),
                0,
                0,
                $source.Width,
                $source.Height,
                [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $graphics.Dispose()
        }

        Assert-TransparentIdentityBitmap -Bitmap $bitmap -Label "$($PixelSize)x$PixelSize identity"
        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return $stream.ToArray()
        }
        finally {
            $stream.Dispose()
            $bitmap.Dispose()
        }
    }

    Assert-TransparentIdentityBitmap -Bitmap $source -Label "Approved identity source"
    New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedOutput) -Force | Out-Null
    New-Item -ItemType Directory -Path $resolvedPngDirectory -Force | Out-Null

    $images = [System.Collections.Generic.List[byte[]]]::new()
    $pngPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($size in $requiredSizes) {
        $pngBytes = [byte[]](New-IdentityPng -PixelSize $size)
        $pngPath = Join-Path $resolvedPngDirectory "orbit-navigator-$size.png"
        [System.IO.File]::WriteAllBytes($pngPath, $pngBytes)
        $images.Add($pngBytes)
        $pngPaths.Add($pngPath)
    }

    $file = [System.IO.File]::Create($resolvedOutput)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$requiredSizes.Count)
        $offset = 6 + (16 * $requiredSizes.Count)
        for ($index = 0; $index -lt $requiredSizes.Count; $index++) {
            $size = $requiredSizes[$index]
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$index].Length
        }
        foreach ($image in $images) {
            $writer.Write($image)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }

    Write-Host "Approved source: $resolvedSource"
    Write-Host "Source SHA-256: $((Get-FileHash -LiteralPath $resolvedSource -Algorithm SHA256).Hash)"
    Write-Host "Orbit Navigator multi-image ICO: $resolvedOutput"
    Write-Host "ICO sizes: $($requiredSizes -join ', ')"
    Write-Host "ICO SHA-256: $((Get-FileHash -LiteralPath $resolvedOutput -Algorithm SHA256).Hash)"
    foreach ($pngPath in $pngPaths) {
        Write-Host "PNG: $pngPath"
        Write-Host "PNG SHA-256: $((Get-FileHash -LiteralPath $pngPath -Algorithm SHA256).Hash)"
    }
}
finally {
    $source.Dispose()
}
