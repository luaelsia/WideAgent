# WideAgent 아이콘을 그려서 멀티사이즈 .ico 로 묶는다.
#
# 외부 이미지를 쓰지 않고 도형으로 직접 그린다. 남의 로고를 가져다 쓰지 않으려는
# 목적도 있고, 크기마다 새로 그리는 편이 축소본보다 작은 크기에서 또렷하기 때문이다.
#
#   powershell -ExecutionPolicy Bypass -File src\make-icon.ps1
#   powershell -ExecutionPolicy Bypass -File src\make-icon.ps1 -Preview
#
# 모양: 양쪽 벽을 좌우로 밀어내는 화살표. 대화창을 넓힌다는 뜻이다.

param(
  [string]$Out     = "$PSScriptRoot\..\WideAgent.ico",
  [string]$Color   = "#4F8EF7",   # 밝은 파랑. 밝은 작업 표시줄과 어두운 쪽 모두에서 보인다
  [switch]$Preview                # 확인용 PNG(256px, 16px 확대본)도 같이 낸다
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$accent = [System.Drawing.ColorTranslator]::FromHtml($Color)

# 한 변이 $s 인 정사각형에 아이콘을 그린다. 모든 좌표는 0~1 비율로 잡고 $s 를 곱한다.
function Draw([System.Drawing.Graphics]$g, [double]$s) {
  $g.SmoothingMode   = 'AntiAlias'
  $g.PixelOffsetMode = 'HighQuality'

  $brush = [System.Drawing.SolidBrush]::new($accent)

  # 좌우 벽. 화살표가 밀어내는 대상이다.
  $wallW = 0.12 * $s
  $wallT = 0.14 * $s
  $wallH = $s - 2 * $wallT
  $r = [Math]::Max(1.0, 0.045 * $s)
  foreach ($x in @((0.07 * $s), ($s - 0.07 * $s - $wallW))) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($x, $wallT, 2*$r, 2*$r, 180, 90)
    $path.AddArc($x + $wallW - 2*$r, $wallT, 2*$r, 2*$r, 270, 90)
    $path.AddArc($x + $wallW - 2*$r, $wallT + $wallH - 2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc($x, $wallT + $wallH - 2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
    $path.Dispose()
  }

  # 가운데 양방향 화살표. 작은 크기에서 뭉개지지 않도록 두껍게 잡는다.
  # 16px 에서는 몸통을 얇게 잡는다. 두꺼우면 촉과 뭉쳐서 아령처럼 보인다.
  $mid   = 0.5 * $s
  $shaft = (&{ if ($s -le 24) { 0.05 } else { 0.075 } }) * $s   # 몸통 두께의 절반
  $head  = 0.22 * $s           # 촉의 높이 절반
  $tipL  = 0.23 * $s
  $tipR  = $s - 0.23 * $s
  $baseL = $tipL + 0.15 * $s
  $baseR = $tipR - 0.15 * $s

  $g.FillRectangle($brush, [System.Drawing.RectangleF]::new(
      $baseL - 0.02 * $s, $mid - $shaft, ($baseR - $baseL) + 0.04 * $s, 2 * $shaft))

  $g.FillPolygon($brush, @(
      [System.Drawing.PointF]::new($tipL,  $mid),
      [System.Drawing.PointF]::new($baseL, $mid - $head),
      [System.Drawing.PointF]::new($baseL, $mid + $head)))
  $g.FillPolygon($brush, @(
      [System.Drawing.PointF]::new($tipR,  $mid),
      [System.Drawing.PointF]::new($baseR, $mid - $head),
      [System.Drawing.PointF]::new($baseR, $mid + $head)))

  $brush.Dispose()
}

function Render([int]$s) {
  $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Transparent)
  Draw $g ([double]$s)
  $g.Dispose()
  return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
  $bmp = Render $s
  $ms = [System.IO.MemoryStream]::new()
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,@{ Size = $s; Bytes = $ms.ToArray() }
  $ms.Dispose(); $bmp.Dispose()
}

# ICO 컨테이너로 묶는다 (각 이미지를 PNG로 담는 Vista 이후 형식)
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

if ($Preview) {
  $dir = Split-Path $Out -Parent
  (Render 256).Save("$dir\icon-preview-256.png", [System.Drawing.Imaging.ImageFormat]::Png)

  # 16px 실물을 8배로 확대해 붙인다. 작은 크기에서 뭉개지는지 보려는 것이다.
  $small = Render 16
  $zoom = [System.Drawing.Bitmap]::new(128, 128, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $zg = [System.Drawing.Graphics]::FromImage($zoom)
  $zg.InterpolationMode = 'NearestNeighbor'
  $zg.PixelOffsetMode = 'Half'
  $zg.DrawImage($small, 0, 0, 128, 128)
  $zg.Dispose()
  $zoom.Save("$dir\icon-preview-16x8.png", [System.Drawing.Imaging.ImageFormat]::Png)
  $small.Dispose(); $zoom.Dispose()
  Write-Host "미리보기: icon-preview-256.png, icon-preview-16x8.png"
}
