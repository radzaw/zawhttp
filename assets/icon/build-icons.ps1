# Renders the ZawHTTP icon (the same drawing as zawhttp.svg) into PNGs, the Windows .ico and the web app icon.
# Uses GDI+ (System.Drawing), so it runs on Windows PowerShell without other tools:
#   powershell -ExecutionPolicy Bypass -File assets/icon/build-icons.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$pngDir = Join-Path $PSScriptRoot 'png'
New-Item -ItemType Directory -Force $pngDir | Out-Null

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 512.0
    $g.ScaleTransform($k, $k)

    # Rounded square with the diagonal gradient.
    $r = 112; $d = 2 * $r
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc(512 - $d, 0, $d, $d, 270, 90)
    $path.AddArc(512 - $d, 512 - $d, $d, $d, 0, 90); $path.AddArc(0, 512 - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 512, 512), ([System.Drawing.Color]::Black), ([System.Drawing.Color]::Black)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.ColorTranslator]::FromHtml('#fb923c'), [System.Drawing.ColorTranslator]::FromHtml('#f43f5e'), [System.Drawing.ColorTranslator]::FromHtml('#be185d'))
    $blend.Positions = @(0.0, 0.55, 1.0)
    $brush.InterpolationColors = $blend
    $g.FillPath($brush, $path)

    function Arrow($color, $x1, $x2, $y, $tipX, $baseX) {
        $pen = New-Object System.Drawing.Pen $color, 44
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, $x1, $y, $x2, $y)
        $head = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF $baseX, ($y - 60)), (New-Object System.Drawing.PointF $tipX, $y), (New-Object System.Drawing.PointF $baseX, ($y + 60)))
        $hp = New-Object System.Drawing.Pen $color, 24
        $hp.LineJoin = 'Round'
        $g.FillPolygon((New-Object System.Drawing.SolidBrush $color), $head)
        $g.DrawPolygon($hp, $head)
    }
    Arrow ([System.Drawing.Color]::White) 112 330 196 400 310
    Arrow ([System.Drawing.ColorTranslator]::FromHtml('#ffe4e6')) 400 182 316 112 202

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$pngs = @{}
foreach ($s in 16, 20, 24, 32, 40, 48, 64, 128, 256, 512) {
    $pngs[$s] = Render $s
    if ($s -notin 20, 40) { [System.IO.File]::WriteAllBytes((Join-Path $pngDir "zawhttp-$s.png"), $pngs[$s]) }
}
[System.IO.File]::WriteAllBytes((Join-Path $root 'src/ZawHTTP/wwwroot/icon-256.png'), $pngs[256])

# Windows icon: PNG-compressed entries (supported since Windows Vista) at the sizes Explorer and the taskbar use.
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 256
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$icoSizes.Count)
$offset = 6 + 16 * $icoSizes.Count
foreach ($s in $icoSizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$s].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $icoSizes) { $w.Write($pngs[$s]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root 'src/ZawHTTP/zawhttp.ico'), $out.ToArray())
Write-Host "Icons written to $pngDir, src/ZawHTTP/zawhttp.ico and src/ZawHTTP/wwwroot/icon-256.png"
