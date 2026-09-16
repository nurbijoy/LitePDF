<#
.SYNOPSIS
    Renders the LitePDF app icon to the PNG sizes the Microsoft Store asks for.

.DESCRIPTION
    Assets/LitePDF.ico tops out at 256 px and Partner Center wants images up to
    2160 px, so the icon is described here as geometry (on the same 256-unit square
    the .ico was drawn on) and rasterised at whatever size is needed.

    Writes assets/store/ - see the README there for which slot each file fills.
    Listing icons are full-bleed (the blue plate already carries ~5% padding).
    Promotional art drops the plate and puts the page on a blue field, because those
    slots are shown large and a small icon floating in empty space looks wrong.
    Tiles put the plate at 66% of the tile so Windows can show its own colour around it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\make-logos.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '..\assets\store' }
Add-Type -AssemblyName System.Drawing

# Colours sampled from Assets/LitePDF.ico so every rendering stays the same icon.
$ColorPlate = [Drawing.Color]::FromArgb(0x0F, 0x6C, 0xBD)   # rounded square
$ColorSheet = [Drawing.Color]::FromArgb(0xFF, 0xFF, 0xFF)   # page
$ColorFold  = [Drawing.Color]::FromArgb(0xC6, 0xDB, 0xF0)   # dog-eared corner
$ColorRule  = [Drawing.Color]::FromArgb(0x7D, 0xA0, 0xC8)   # lines of text
# The plate colour lightened and darkened, for promotional backgrounds.
$ColorTop    = [Drawing.Color]::FromArgb(0x17, 0x7C, 0xD4)
$ColorBottom = [Drawing.Color]::FromArgb(0x0A, 0x52, 0x92)

# The page occupies this box on the 256-unit square; the plate fills 12..244.
$DocLeft = 68.0; $DocTop = 53.0; $DocRight = 187.0; $DocBottom = 203.0

function New-RoundedRectPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)
    $d = [single]($R * 2)
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc([single]($X + $W - $d), $Y, $d, $d, 270, 90)
    $p.AddArc([single]($X + $W - $d), [single]($Y + $H - $d), $d, $d, 0, 90)
    $p.AddArc($X, [single]($Y + $H - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Point {
    param([single]$X, [single]$Y)
    return (New-Object Drawing.PointF($X, $Y))
}

# The page on its own: white sheet, folded corner, four lines of text.
function Write-Document {
    param([Drawing.Graphics]$G)

    $sheet = New-Object Drawing.SolidBrush $ColorSheet
    $fold  = New-Object Drawing.SolidBrush $ColorFold
    $rule  = New-Object Drawing.SolidBrush $ColorRule
    try {
        # Page with its top-right corner cut off along the fold.
        $G.FillPolygon($sheet, @(
            (New-Point 68 53), (New-Point 155 53), (New-Point 187 85),
            (New-Point 187 203), (New-Point 68 203)
        ))
        $G.FillPolygon($fold, @(
            (New-Point 155 53), (New-Point 187 85), (New-Point 155 85)
        ))

        $y = 111.0
        foreach ($w in 84, 84, 84, 46) {   # the last line is short
            $line = New-RoundedRectPath 86 ([single]$y) ([single]$w) 11 5.5
            try { $G.FillPath($rule, $line) } finally { $line.Dispose() }
            $y += 24.667
        }
    }
    finally { $sheet.Dispose(); $fold.Dispose(); $rule.Dispose() }
}

# The whole icon: the page on its blue plate.
function Write-Icon {
    param([Drawing.Graphics]$G)

    $plate = New-Object Drawing.SolidBrush $ColorPlate
    try {
        $path = New-RoundedRectPath 12 12 232 232 41
        try { $G.FillPath($plate, $path) } finally { $path.Dispose() }
    }
    finally { $plate.Dispose() }
    Write-Document $G
}

function Save-Png {
    param([Drawing.Bitmap]$Bitmap, [string]$Path)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $Bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    Write-Host ("  {0,-34} {1}x{2}" -f (Split-Path -Leaf $Path), $Bitmap.Width, $Bitmap.Height)
    $Bitmap.Dispose()
}

function New-Canvas {
    param([int]$Width, [int]$Height, [ref]$Graphics)
    $bmp = New-Object Drawing.Bitmap($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([Drawing.Color]::Transparent)
    $Graphics.Value = $g
    return $bmp
}

# The icon on a transparent square (or tile), scaled to $Fill of the shorter side.
function Save-Logo {
    param([string]$Path, [int]$Width, [int]$Height = 0, [double]$Fill = 1.0)
    if ($Height -le 0) { $Height = $Width }

    $g = $null
    $bmp = New-Canvas $Width $Height ([ref]$g)
    try {
        $side = [Math]::Min($Width, $Height) * $Fill
        $g.TranslateTransform([single](($Width - $side) / 2), [single](($Height - $side) / 2))
        $g.ScaleTransform([single]($side / 256.0), [single]($side / 256.0))
        Write-Icon $g
    }
    finally { $g.Dispose() }
    Save-Png $bmp $Path
}

# Promotional art: the page, without its plate, on a blue field.
# $DocFill is the page height as a share of the canvas height; $CenterY places it
# (above the middle for the slots where the Store may drop text over the bottom third).
function Save-Promo {
    param([string]$Path, [int]$Width, [int]$Height, [double]$DocFill = 0.5, [double]$CenterY = 0.5)

    $g = $null
    $bmp = New-Canvas $Width $Height ([ref]$g)
    try {
        $rect = New-Object Drawing.Rectangle(0, 0, $Width, $Height)
        $bg = New-Object Drawing.Drawing2D.LinearGradientBrush(
            $rect, $ColorTop, $ColorBottom, [Drawing.Drawing2D.LinearGradientMode]::Vertical)
        try { $g.FillRectangle($bg, $rect) } finally { $bg.Dispose() }

        $scale = ($Height * $DocFill) / ($DocBottom - $DocTop)
        $g.TranslateTransform(
            [single]($Width / 2.0 - ($DocLeft + $DocRight) / 2.0 * $scale),
            [single]($Height * $CenterY - ($DocTop + $DocBottom) / 2.0 * $scale))
        $g.ScaleTransform([single]$scale, [single]$scale)
        Write-Document $g
    }
    finally { $g.Dispose() }
    Save-Png $bmp $Path
}

$OutDir = [IO.Path]::GetFullPath($OutDir)
Write-Host "Writing logos to $OutDir"

# Store listing, the slots Partner Center offers for an app.
Save-Logo  -Path (Join-Path $OutDir 'AppTileIcon-300x300.png') -Width 300
Save-Promo -Path (Join-Path $OutDir 'BoxArt-1080x1080.png')    -Width 1080 -Height 1080 -DocFill 0.52 -CenterY 0.45
Save-Promo -Path (Join-Path $OutDir 'BoxArt-2160x2160.png')    -Width 2160 -Height 2160 -DocFill 0.52 -CenterY 0.45
Save-Promo -Path (Join-Path $OutDir 'PosterArt-720x1080.png')  -Width 720  -Height 1080 -DocFill 0.44 -CenterY 0.40
Save-Promo -Path (Join-Path $OutDir 'HeroArt-1920x1080.png')   -Width 1920 -Height 1080 -DocFill 0.54 -CenterY 0.45
# Plain icon master, for a web page, a README or a future .ico.
Save-Logo  -Path (Join-Path $OutDir 'LitePDF-1024.png') -Width 1024

# MSIX package assets, at every scale Windows may ask for. Only needed if LitePDF is
# ever packaged as MSIX rather than submitted as an installer.
$msix = Join-Path $OutDir 'msix'
$scales = @{ 'scale-100' = 1.0; 'scale-125' = 1.25; 'scale-150' = 1.5; 'scale-200' = 2.0; 'scale-400' = 4.0 }
$tiles = @(
    @{ Name = 'StoreLogo';         W = 50;  H = 50;  Fill = 1.0  },
    @{ Name = 'Square44x44Logo';   W = 44;  H = 44;  Fill = 1.0  },
    @{ Name = 'Square71x71Logo';   W = 71;  H = 71;  Fill = 0.66 },
    @{ Name = 'Square150x150Logo'; W = 150; H = 150; Fill = 0.66 },
    @{ Name = 'Square310x310Logo'; W = 310; H = 310; Fill = 0.66 },
    @{ Name = 'Wide310x150Logo';   W = 310; H = 150; Fill = 0.66 },
    @{ Name = 'SplashScreen';      W = 620; H = 300; Fill = 0.60 }
)
foreach ($tile in $tiles) {
    foreach ($scale in $scales.Keys | Sort-Object) {
        $f = $scales[$scale]
        Save-Logo -Path (Join-Path $msix "$($tile.Name).$scale.png") -Width ([int][Math]::Ceiling($tile.W * $f)) -Height ([int][Math]::Ceiling($tile.H * $f)) -Fill $tile.Fill
    }
}

# App-list icons Windows picks by target size rather than by scale.
foreach ($size in 16, 24, 32, 48, 256) {
    Save-Logo -Path (Join-Path $msix "Square44x44Logo.targetsize-$size.png") -Width $size
}

Write-Host "Done."
