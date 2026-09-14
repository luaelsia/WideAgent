# Claude 로고를 좌우로 늘린 모양의 멀티사이즈 .ico 를 만든다.
param(
  [string]$Source = "$PSScriptRoot\claude-logo.png",
  [string]$Out    = "$PSScriptRoot\..\WideAgent.ico",
  [double]$Aspect = 2.0   # 가로:세로 비율. 클수록 납작하게 늘어난다.
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$img = [System.Drawing.Bitmap]::new($Source)

# 1. 투명 여백을 잘라내어 로고 본체만 남긴다
$minX = $img.Width; $minY = $img.Height; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $img.Height; $y++) {
  for ($x = 0; $x -lt $img.Width; $x++) {
    if ($img.GetPixel($x, $y).A -gt 8) {
      if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
      if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
    }
  }
}
if ($maxX -lt 0) { $minX = 0; $minY = 0; $maxX = $img.Width - 1; $maxY = $img.Height - 1 }
$crop = [System.Drawing.Rectangle]::new($minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
Write-Host ("crop: {0},{1} {2}x{3}" -f $crop.X, $crop.Y, $crop.Width, $crop.Height)

# 2. 크기별로 가로를 꽉 채우고 세로를 눌러서 그린다
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
  $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode  = 'HighQualityBicubic'
  $g.SmoothingMode      = 'HighQuality'
  $g.PixelOffsetMode    = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)

  $w = [double]$s
  $h = $w / $Aspect
  $dst = [System.Drawing.RectangleF]::new(0, ($s - $h) / 2.0, $w, $h)
  $g.DrawImage($img, $dst, $crop, [System.Drawing.GraphicsUnit]::Pixel)
  $g.Dispose()

  $ms = [System.IO.MemoryStream]::new()
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,@{ Size = $s; Bytes = $ms.ToArray() }
  $ms.Dispose(); $bmp.Dispose()
}
$img.Dispose()

# 3. ICO 컨테이너로 묶는다 (각 이미지를 PNG로 담는 Vista 이후 형식)
$fs = [System.IO.File]::Create($Out)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
  $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
  $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
  $bw.Write([Byte]0); $bw.Write([Byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$p.Bytes.Length); $bw.Write([UInt32]$offset)
  $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host ("생성: {0} ({1:N0} bytes, {2}개 크기)" -f $Out, (Get-Item $Out).Length, $pngs.Count)
