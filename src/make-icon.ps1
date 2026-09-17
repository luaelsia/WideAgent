# WideAgent 최종 PNG를 멀티사이즈 .ico로 묶는다.
#
#   powershell -ExecutionPolicy Bypass -File src\make-icon.ps1
#   powershell -ExecutionPolicy Bypass -File src\make-icon.ps1 -Preview

param(
  [string]$Source = "$PSScriptRoot\..\assets\WideAgent.png",
  [string]$Out = "$PSScriptRoot\..\WideAgent.ico",
  [switch]$Preview
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Source)) {
  throw "아이콘 원본 PNG를 찾을 수 없습니다: $Source"
}

$master = [System.Drawing.Bitmap]::FromFile($Source)

function Render([int]$size) {
  $bmp = [System.Drawing.Bitmap]::new(
    $size,
    $size,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

  $attrs = [System.Drawing.Imaging.ImageAttributes]::new()
  $attrs.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
  $dest = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
  $g.DrawImage(
    $master,
    $dest,
    0,
    0,
    $master.Width,
    $master.Height,
    [System.Drawing.GraphicsUnit]::Pixel,
    $attrs)

  $attrs.Dispose()
  $g.Dispose()
  return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($size in $sizes) {
  $bmp = Render $size
  $stream = [System.IO.MemoryStream]::new()
  $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,@{ Size = $size; Bytes = $stream.ToArray() }
  $stream.Dispose()
  $bmp.Dispose()
}

$file = [System.IO.File]::Create($Out)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$pngs.Count)

$offset = 6 + 16 * $pngs.Count
foreach ($png in $pngs) {
  $dimension = if ($png.Size -ge 256) { 0 } else { $png.Size }
  $writer.Write([Byte]$dimension)
  $writer.Write([Byte]$dimension)
  $writer.Write([Byte]0)
  $writer.Write([Byte]0)
  $writer.Write([UInt16]1)
  $writer.Write([UInt16]32)
  $writer.Write([UInt32]$png.Bytes.Length)
  $writer.Write([UInt32]$offset)
  $offset += $png.Bytes.Length
}

foreach ($png in $pngs) {
  $writer.Write($png.Bytes)
}

$writer.Flush()
$writer.Dispose()
$file.Dispose()

Write-Host ("생성: {0} ({1:N0} bytes, {2}개 크기)" -f $Out, (Get-Item $Out).Length, $pngs.Count)

if ($Preview) {
  $previewDir = Split-Path $Out -Parent
  $preview256 = Render 256
  $preview256.Save("$previewDir\icon-preview-256.png", [System.Drawing.Imaging.ImageFormat]::Png)
  $preview256.Dispose()

  $preview16 = Render 16
  $zoom = [System.Drawing.Bitmap]::new(128, 128, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $zoomGraphics = [System.Drawing.Graphics]::FromImage($zoom)
  $zoomGraphics.Clear([System.Drawing.Color]::FromArgb(255, 32, 35, 39))
  $zoomGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $zoomGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $zoomGraphics.DrawImage($preview16, 0, 0, 128, 128)
  $zoomGraphics.Dispose()
  $zoom.Save("$previewDir\icon-preview-16x8.png", [System.Drawing.Imaging.ImageFormat]::Png)
  $preview16.Dispose()
  $zoom.Dispose()

  Write-Host "미리보기: icon-preview-256.png, icon-preview-16x8.png"
}

$master.Dispose()
