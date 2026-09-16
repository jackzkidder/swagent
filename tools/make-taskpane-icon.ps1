<#
.SYNOPSIS
    Generate the task pane tab icon.

.DESCRIPTION
    SOLIDWORKS shows task pane tabs as a vertical strip of small icons down the
    right edge of the window. Passing an empty bitmap path is accepted, but it
    leaves the tab blank - which makes the add-in genuinely hard to find among
    the stock tabs, and "I cannot see it" is a bad first ninety seconds.

    The glyph is the same faceted cube as the panel's own mark, so the tab and
    the panel read as one product.

    TWO FORMATS, deliberately:

      taskpane-<n>.png   Alpha PNGs at the sizes CreateTaskpaneView3 wants.
                         This is the high-DPI path: SOLIDWORKS picks the size
                         that matches the user's scaling instead of stretching
                         a 16px bitmap into a blurry mess on a 4K screen.

      taskpane.bmp       16x18, magenta-keyed, for CreateTaskpaneView2. Kept as
                         the fallback for when the image list is refused.

    Regenerate with:
      powershell -ExecutionPolicy Bypass -File tools\make-taskpane-icon.ps1
      powershell -ExecutionPolicy Bypass -File tools\make-taskpane-icon.ps1 -Preview
#>
[CmdletBinding()]
param(
    [string]$OutputDir,

    # Also write a large flat render to artifacts\, for looking at.
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'src\SwAgent.AddIn\Assets' }
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }

# The panel's clay accent, one shade per visible face, lit from above.
#
# The value range is deliberately WIDE. A first attempt used three shades a few
# percent apart, which looked correct at 128px and turned into an orange blob at
# 20px - and 20px is the size that actually appears in the task pane strip. At
# tab size the only thing separating the faces is contrast, so contrast is what
# gets tuned, not the seams.
$topFace   = [System.Drawing.Color]::FromArgb(255, 240, 178, 146)
$leftFace  = [System.Drawing.Color]::FromArgb(255, 186,  88,  55)
$rightFace = [System.Drawing.Color]::FromArgb(255, 116,  52,  32)

function New-CubeBitmap {
    param(
        [int]$Size,

        # For the magenta-keyed BMP only: no anti-aliasing anywhere, because
        # softened edges blend into the key colour and survive as pink fringing.
        [switch]$HardEdges
    )

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = if ($HardEdges) {
        [System.Drawing.Drawing2D.SmoothingMode]::None
    } else {
        [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    }
    $g.Clear([System.Drawing.Color]::Transparent)

    try {
        # Unit coordinates, scaled. The cube is a hexagon split into three
        # rhombi meeting at the centre.
        function P([double]$x, [double]$y) {
            New-Object System.Drawing.PointF(($x * $Size), ($y * $Size))
        }

        $top    = P 0.50 0.06
        $upperL = P 0.06 0.30
        $upperR = P 0.94 0.30
        $centre = P 0.50 0.54
        $lowerL = P 0.06 0.72
        $lowerR = P 0.94 0.72
        $bottom = P 0.50 0.96

        $faces = @(
            @{ Points = @($top, $upperR, $centre, $upperL); Colour = $topFace },
            @{ Points = @($upperL, $centre, $bottom, $lowerL); Colour = $leftFace },
            @{ Points = @($upperR, $centre, $bottom, $lowerR); Colour = $rightFace }
        )

        foreach ($face in $faces) {
            $brush = New-Object System.Drawing.SolidBrush $face.Colour
            try { $g.FillPolygon($brush, [System.Drawing.PointF[]]$face.Points) }
            finally { $brush.Dispose() }
        }

        # A hairline between the faces, only where it will not turn to mud.
        # Never on the keyed bitmap: a dark seam anti-aliased against magenta is
        # exactly the fringing this switch exists to avoid.
        if ($Size -ge 32 -and -not $HardEdges) {
            $seam = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 60, 30, 20)), ([float]($Size / 64.0))
            try {
                $g.DrawLine($seam, $centre, $top)
                $g.DrawLine($seam, $centre, $bottom)
                $g.DrawLine($seam, $centre, $upperL)
                $g.DrawLine($seam, $centre, $upperR)
            }
            finally { $seam.Dispose() }
        }
    }
    finally {
        $g.Dispose()
    }

    return $bmp
}

# CreateTaskpaneView3 takes this set; SOLIDWORKS chooses by display scaling.
$sizes = @(20, 32, 40, 64, 96, 128)
$written = @()

foreach ($size in $sizes) {
    $bmp = New-CubeBitmap -Size $size
    try {
        $path = Join-Path $OutputDir "taskpane-$size.png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $written += $path
    }
    finally { $bmp.Dispose() }
}

# ---- the 16x18 fallback, for CreateTaskpaneView2 -------------------------
# Magenta is the conventional transparency key for SOLIDWORKS toolbar bitmaps,
# so the tab background shows through instead of a coloured block.
$key = [System.Drawing.Color]::FromArgb(255, 255, 0, 255)
$fallback = New-Object System.Drawing.Bitmap(16, 18)
$fg = [System.Drawing.Graphics]::FromImage($fallback)

# NO anti-aliasing here, and none when compositing. Anti-aliased edges blend
# with the magenta key, and SOLIDWORKS only treats EXACT key pixels as
# transparent - so every softened edge pixel survives as pink fringing around
# the glyph. Hard edges are the whole point of a keyed bitmap.
$fg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
$fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$fg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy

try {
    $fg.Clear($key)
    $cube = New-CubeBitmap -Size 16 -HardEdges
    try { $fg.DrawImage($cube, 0, 1, 16, 16) } finally { $cube.Dispose() }

    $bmpPath = Join-Path $OutputDir 'taskpane.bmp'
    $fallback.Save($bmpPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $written += $bmpPath
}
finally {
    $fg.Dispose()
    $fallback.Dispose()
}

foreach ($w in $written) { Write-Host "  $w" }
Write-Host "Wrote $($written.Count) icon file(s)." -ForegroundColor Green

if ($Preview) {
    # A strip of every size on a neutral ground, for judging the small ones.
    $artifacts = Join-Path $repoRoot 'artifacts'
    if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Path $artifacts -Force | Out-Null }

    $stripW = 460
    $stripH = 200
    $strip = New-Object System.Drawing.Bitmap($stripW, $stripH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($strip)
    $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor

    try {
        $sg.Clear([System.Drawing.Color]::FromArgb(255, 240, 240, 240))
        $font = New-Object System.Drawing.Font('Segoe UI', 9)
        $label = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 90, 90, 90))

        # Big render first, then each real size at 1:1 beside it.
        $big = New-CubeBitmap -Size 128
        try { $sg.DrawImage($big, 16, 24, 128, 128) } finally { $big.Dispose() }
        $sg.DrawString('128px', $font, $label, 16, 158)

        $x = 176
        foreach ($size in @(20, 32, 40, 64)) {
            $b = New-CubeBitmap -Size $size
            try { $sg.DrawImage($b, $x, (24 + (64 - $size)), $size, $size) } finally { $b.Dispose() }
            $sg.DrawString("$($size)px", $font, $label, $x, 96)
            $x += $size + 28
        }

        # And the 16x18 fallback as SOLIDWORKS would show it, key colour and all.
        $fb = [System.Drawing.Image]::FromFile((Join-Path $OutputDir 'taskpane.bmp'))
        try { $sg.DrawImage($fb, 176, 130, 16, 18) } finally { $fb.Dispose() }
        $sg.DrawString('16x18 bmp fallback (magenta = transparent)', $font, $label, 200, 132)

        $font.Dispose()
        $label.Dispose()

        $previewPath = Join-Path $artifacts 'taskpane-icon-preview.png'
        $strip.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "Preview: $previewPath" -ForegroundColor Cyan
    }
    finally {
        $sg.Dispose()
        $strip.Dispose()
    }
}
