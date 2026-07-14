param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\src\PackagePilot.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PackagePilotNativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr handle);
}
'@

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Bounds,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $arc = [System.Drawing.RectangleF]::new($Bounds.X, $Bounds.Y, $diameter, $diameter)
    $path.AddArc($arc, 180, 90)
    $arc.X = $Bounds.Right - $diameter
    $path.AddArc($arc, 270, 90)
    $arc.Y = $Bounds.Bottom - $diameter
    $path.AddArc($arc, 0, 90)
    $arc.X = $Bounds.X
    $path.AddArc($arc, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-PackagePilotLogo {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $inset = [float]($Size * 0.055)
    $bounds = [System.Drawing.RectangleF]::new($inset, $inset, $Size - (2 * $inset), $Size - (2 * $inset))
    $path = New-RoundedRectanglePath -Bounds $bounds -Radius ([float]($Size * 0.205))
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $bounds,
        [System.Drawing.Color]::FromArgb(255, 77, 91, 234),
        [System.Drawing.Color]::FromArgb(255, 5, 163, 167),
        38.0)
    $graphics.FillPath($gradient, $path)

    $highlightBounds = [System.Drawing.RectangleF]::new(
        [float]($Size * 0.10),
        [float]($Size * 0.08),
        [float]($Size * 0.80),
        [float]($Size * 0.54))
    $highlight = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $highlightBounds,
        [System.Drawing.Color]::FromArgb(72, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
        90.0)
    $graphics.FillPath($highlight, $path)

    $stroke = [float][Math]::Max(1.7, $Size * 0.052)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $top = [System.Drawing.PointF]::new([float]($Size * 0.50), [float]($Size * 0.265))
    $right = [System.Drawing.PointF]::new([float]($Size * 0.755), [float]($Size * 0.405))
    $center = [System.Drawing.PointF]::new([float]($Size * 0.50), [float]($Size * 0.555))
    $left = [System.Drawing.PointF]::new([float]($Size * 0.245), [float]($Size * 0.405))
    $bottom = [System.Drawing.PointF]::new([float]($Size * 0.50), [float]($Size * 0.775))
    $graphics.DrawPolygon($pen, @($top, $right, $center, $left))
    $graphics.DrawLine($pen, $left, [System.Drawing.PointF]::new($left.X, [float]($Size * 0.635)))
    $graphics.DrawLine($pen, [System.Drawing.PointF]::new($left.X, [float]($Size * 0.635)), $bottom)
    $graphics.DrawLine($pen, $right, [System.Drawing.PointF]::new($right.X, [float]($Size * 0.635)))
    $graphics.DrawLine($pen, [System.Drawing.PointF]::new($right.X, [float]($Size * 0.635)), $bottom)
    $graphics.DrawLine($pen, $center, $bottom)

    $flightPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(235, 255, 255, 255), [float]($stroke * 0.66))
    $flightPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $flightPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawBezier(
        $flightPen,
        [System.Drawing.PointF]::new([float]($Size * 0.17), [float]($Size * 0.68)),
        [System.Drawing.PointF]::new([float]($Size * 0.08), [float]($Size * 0.30)),
        [System.Drawing.PointF]::new([float]($Size * 0.50), [float]($Size * 0.12)),
        [System.Drawing.PointF]::new([float]($Size * 0.79), [float]($Size * 0.25)))

    $arrow = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $arrow.AddPolygon(@(
        [System.Drawing.PointF]::new([float]($Size * 0.775), [float]($Size * 0.185)),
        [System.Drawing.PointF]::new([float]($Size * 0.895), [float]($Size * 0.285)),
        [System.Drawing.PointF]::new([float]($Size * 0.745), [float]($Size * 0.305))))
    $graphics.FillPath([System.Drawing.Brushes]::White, $arrow)

    $arrow.Dispose()
    $flightPen.Dispose()
    $pen.Dispose()
    $highlight.Dispose()
    $gradient.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Save-LogoPng {
    param([string]$Name, [int]$Width, [int]$Height, [int]$LogoSize)

    $canvas = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $logo = New-PackagePilotLogo -Size $LogoSize
    $x = [int](($Width - $LogoSize) / 2)
    $y = [int](($Height - $LogoSize) / 2)
    $graphics.DrawImage($logo, $x, $y, $LogoSize, $LogoSize)
    $path = Join-Path $AssetDirectory $Name
    $canvas.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $logo.Dispose()
    $graphics.Dispose()
    $canvas.Dispose()
}

[System.IO.Directory]::CreateDirectory($AssetDirectory) | Out-Null
Save-LogoPng 'Square150x150Logo.scale-200.png' 300 300 276
Save-LogoPng 'Square44x44Logo.scale-200.png' 88 88 82
Save-LogoPng 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 24
Save-LogoPng 'Square44x44Logo.targetsize-48_altform-lightunplated.png' 48 48 48
Save-LogoPng 'StoreLogo.png' 50 50 48
Save-LogoPng 'LockScreenLogo.scale-200.png' 48 48 46
Save-LogoPng 'Wide310x150Logo.scale-200.png' 620 300 220
Save-LogoPng 'SplashScreen.scale-200.png' 1240 600 220

$iconBitmap = New-PackagePilotLogo -Size 256
$handle = $iconBitmap.GetHicon()
try {
    $icon = [System.Drawing.Icon]::FromHandle($handle)
    $stream = [System.IO.File]::Open(
        (Join-Path $AssetDirectory 'AppIcon.ico'),
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $icon.Save($stream)
    }
    finally {
        $stream.Dispose()
        $icon.Dispose()
    }
}
finally {
    [PackagePilotNativeMethods]::DestroyIcon($handle) | Out-Null
    $iconBitmap.Dispose()
}

Write-Host "Generated Package Pilot assets in $AssetDirectory"
