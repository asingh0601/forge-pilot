<#
.SYNOPSIS
    Generates the VSIX icon and preview image from the ForgePilot logo.

.DESCRIPTION
    The Marketplace and the VS extension manager want two fixed sizes:
      Icon          90 x 90   — the extension list row
      PreviewImage  200 x 200 — the details pane

    Both are produced from one source file so there is a single place to change
    the logo. Run this after replacing assets/logo.png.

    The source is padded to a square before scaling rather than stretched — a
    non-square logo would otherwise come out distorted at both sizes.

.EXAMPLE
    pwsh assets/make-icons.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot 'logo.png'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\ForgePilot.VSExtension\Resources')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) {
    throw "Source logo not found: $Source`nSave the logo there first, then re-run."
}

Add-Type -AssemblyName System.Drawing

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}

$original = [System.Drawing.Image]::FromFile((Resolve-Path $Source))

try {
    # Pad to a square on the longest edge so scaling never distorts.
    $side = [Math]::Max($original.Width, $original.Height)
    $square = New-Object System.Drawing.Bitmap($side, $side)
    $sg = [System.Drawing.Graphics]::FromImage($square)
    try {
        $sg.Clear([System.Drawing.Color]::White)
        $sg.DrawImage($original,
            [int](($side - $original.Width) / 2),
            [int](($side - $original.Height) / 2),
            $original.Width, $original.Height)
    }
    finally { $sg.Dispose() }

    foreach ($target in @(
        @{ Name = 'icon.png';    Size = 90  },
        @{ Name = 'preview.png'; Size = 200 }
    )) {
        $bmp = New-Object System.Drawing.Bitmap($target.Size, $target.Size)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.DrawImage($square, 0, 0, $target.Size, $target.Size)
        }
        finally { $g.Dispose() }

        $path = Join-Path $OutputDirectory $target.Name
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host ("wrote {0}  ({1}x{1})" -f $path, $target.Size)
    }

    $square.Dispose()
}
finally {
    $original.Dispose()
}
