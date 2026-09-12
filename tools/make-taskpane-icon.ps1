<#
.SYNOPSIS
    Generate the task pane icon.

.DESCRIPTION
    SOLIDWORKS shows task pane tabs as a vertical strip of small icons down the
    right edge of the window. Passing an empty bitmap path to
    CreateTaskpaneView2 is accepted, but it leaves the tab blank - which makes
    the add-in genuinely hard to find among the stock tabs, and "I cannot see
    it" is a bad first ninety seconds.

    The glyph is a plate with a hole, which is what the product actually makes.

    Regenerate with:
      powershell -ExecutionPolicy Bypass -File tools\make-taskpane-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $OutputPath) {
    # Resolve here rather than in the param default: $PSScriptRoot is not
    # reliably populated while parameter defaults are being bound.
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot  = Split-Path -Parent $scriptDir
    $OutputPath = Join-Path $repoRoot 'src\SwAgent.AddIn\Assets\taskpane.bmp'
}

$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# SOLIDWORKS task pane icons are small; 16x18 matches the stock tabs closely.
$w = 16
$h = 18

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

try {
    # Magenta is the conventional transparency key for SOLIDWORKS toolbar
    # bitmaps, so the tab background shows through instead of a coloured block.
    $key = [System.Drawing.Color]::FromArgb(255, 0, 255)
    $g.Clear($key)

    $plate = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(31, 111, 235))
    $edge  = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(16, 68, 148)), 1

    # The plate: a rounded rectangle sitting slightly inset.
    $rect = New-Object System.Drawing.Rectangle 1, 3, 14, 12
    $g.FillRectangle($plate, $rect)
    $g.DrawRectangle($edge, $rect)

    # The hole, punched through the middle in the key colour so it reads as a
    # hole rather than a dot.
    $holeBrush = New-Object System.Drawing.SolidBrush $key
    $g.FillEllipse($holeBrush, 6, 7, 5, 5)
    $g.DrawEllipse($edge, 6, 7, 5, 5)

    $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
    Write-Host "Wrote $OutputPath ($w x $h)" -ForegroundColor Green
}
finally {
    $g.Dispose()
    $bmp.Dispose()
}
