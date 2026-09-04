# A primary-screen grab and a bounded, exclusion-aware PNG diff, for facts that are painted by the
# operating system rather than by the page.
#
# A native tooltip is browser chrome: it is absent from the DOM, from the accessibility tree, and
# from Playwright's own screenshot, which captures the page's compositor output only. So the only way
# to observe one is to grab the screen while a real cursor hover is held.
#
# `Graphics.CopyFromScreen` reads the desktop device context, which is why this file also moves the
# cursor: the move and the grab must happen in one process so the hover is still held when the pixels
# are read. Every mode emits one line of compressed JSON on stdout so a caller reads a value instead
# of parsing prose.
#
# Per-monitor DPI awareness is requested before any graphics call, so the grab is at the monitor's
# native resolution rather than a stretched copy of a virtualised one. The regime that was actually
# obtained is reported as `screenBounds` in every mode; the caller calibrates its own coordinate
# transform against it rather than assuming which one it got.
[CmdletBinding()]
param(
  # Capture mode: where to write the PNG.
  [string]$Out,
  # Move the real cursor to "x,y" (screen coordinates) before capturing.
  [string]$MoveTo,
  # Wait this long after the move and before the grab, so a hover timer can elapse.
  [int]$DwellMs = 0,
  # Diff mode.
  [switch]$Diff,
  [string]$A,
  [string]$B,
  # "x,y,w,h" - the only region compared.
  [string]$Rect,
  # Regions inside $Rect whose pixels are not counted, as "x,y,w,h" groups joined by ";". One
  # parameter carrying every group rather than a repeated one, because a repeated parameter is a
  # binding error and a comma-bearing value is split by the parser before the script sees it.
  [string]$Exclude = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Requested before System.Drawing loads: awareness is a per-process state that the first graphics
# call latches. -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. A host that refuses it stays
# virtualised, which is a coordinate space the caller can still calibrate against, so a failure here
# is not fatal.
$dpi = Add-Type -PassThru -Name CoveProbeDpi -Namespace CoveProbe -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDpiAwarenessContext(System.IntPtr value);
"@
try { [void]$dpi::SetProcessDpiAwarenessContext([System.IntPtr]::new(-4)) } catch { }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

function Get-RectFromText([string]$text) {
  $parts = $text.Split(",")
  if ($parts.Count -ne 4) { throw "A rectangle is 'x,y,w,h'; got '$text'." }
  return [pscustomobject]@{
    x = [int]$parts[0]
    y = [int]$parts[1]
    w = [int]$parts[2]
    h = [int]$parts[3]
  }
}

function Get-ScreenBounds {
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  return [pscustomobject]@{
    x      = $bounds.X
    y      = $bounds.Y
    width  = $bounds.Width
    height = $bounds.Height
  }
}

function Get-Region([string]$path, $rect) {
  $bitmap = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $path))
  try {
    $whole = [System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height)
    $crop = [System.Drawing.Rectangle]::Intersect(
      [System.Drawing.Rectangle]::new($rect.x, $rect.y, $rect.w, $rect.h), $whole)
    if ($crop.Width -le 0 -or $crop.Height -le 0) {
      throw "The requested rectangle $($rect | ConvertTo-Json -Compress) lies outside $path ($($bitmap.Width)x$($bitmap.Height))."
    }
    $region = $bitmap.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
      $data = $region.LockBits(
        [System.Drawing.Rectangle]::new(0, 0, $region.Width, $region.Height),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
      $bytes = New-Object byte[] ($data.Stride * $region.Height)
      [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
      $region.UnlockBits($data)
      return [pscustomobject]@{
        bytes   = $bytes
        stride  = $data.Stride
        width   = $region.Width
        height  = $region.Height
        originX = $crop.X
        originY = $crop.Y
      }
    } finally { $region.Dispose() }
  } finally { $bitmap.Dispose() }
}

function Measure-Uniformity([string]$path) {
  # Sampled rather than exhaustive: the question is only whether the grab captured a blank or locked
  # session, and a uniform image answers that from any grid.
  $bitmap = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $path))
  try {
    $seen = [System.Collections.Generic.HashSet[int]]::new()
    for ($y = 0; $y -lt $bitmap.Height; $y += 16) {
      for ($x = 0; $x -lt $bitmap.Width; $x += 16) {
        [void]$seen.Add($bitmap.GetPixel($x, $y).ToArgb())
        if ($seen.Count -gt 64) { break }
      }
      if ($seen.Count -gt 64) { break }
    }
    return $seen.Count
  } finally { $bitmap.Dispose() }
}

if ($Diff) {
  if (-not $A -or -not $B -or -not $Rect) { throw "Diff mode needs -A, -B and -Rect." }

  $searched = Get-RectFromText $Rect
  $excluded = @($Exclude.Split(";") | Where-Object { $_ } | ForEach-Object { Get-RectFromText $_ })

  $left = Get-Region $A $searched
  $right = Get-Region $B $searched
  if ($left.width -ne $right.width -or $left.height -ne $right.height) {
    throw "The two images crop to different sizes: $($left.width)x$($left.height) against $($right.width)x$($right.height)."
  }

  $changed = 0
  $minX = [int]::MaxValue; $minY = [int]::MaxValue; $maxX = -1; $maxY = -1
  $bytesA = $left.bytes; $bytesB = $right.bytes
  $strideA = $left.stride; $strideB = $right.stride

  for ($y = 0; $y -lt $left.height; $y++) {
    $rowA = $y * $strideA
    $rowB = $y * $strideB
    $absY = $left.originY + $y
    for ($x = 0; $x -lt $left.width; $x++) {
      $iA = $rowA + $x * 4
      $iB = $rowB + $x * 4
      if ($bytesA[$iA] -eq $bytesB[$iB] -and
          $bytesA[$iA + 1] -eq $bytesB[$iB + 1] -and
          $bytesA[$iA + 2] -eq $bytesB[$iB + 2]) { continue }

      $absX = $left.originX + $x
      $skip = $false
      foreach ($box in $excluded) {
        if ($absX -ge $box.x -and $absX -lt ($box.x + $box.w) -and
            $absY -ge $box.y -and $absY -lt ($box.y + $box.h)) { $skip = $true; break }
      }
      if ($skip) { continue }

      $changed++
      if ($absX -lt $minX) { $minX = $absX }
      if ($absY -lt $minY) { $minY = $absY }
      if ($absX -gt $maxX) { $maxX = $absX }
      if ($absY -gt $maxY) { $maxY = $absY }
    }
  }

  $box = $null
  if ($changed -gt 0) {
    $box = [pscustomobject]@{
      x = $minX; y = $minY; width = ($maxX - $minX + 1); height = ($maxY - $minY + 1)
    }
  }

  [pscustomobject]@{
    mode           = "diff"
    a              = $A
    b              = $B
    searchedRect   = [pscustomobject]@{
      x = $left.originX; y = $left.originY; width = $left.width; height = $left.height
    }
    searchedPixels = ($left.width * $left.height)
    excluded       = $excluded
    changedPixels  = $changed
    boundingBox    = $box
    screenBounds   = (Get-ScreenBounds)
  } | ConvertTo-Json -Compress -Depth 6
  exit 0
}

if (-not $Out) { throw "Capture mode needs -Out, or pass -Diff for a comparison." }

$moved = $null
if ($MoveTo) {
  $point = $MoveTo.Split(",")
  if ($point.Count -ne 2) { throw "A cursor position is 'x,y'; got '$MoveTo'." }
  [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new([int]$point[0], [int]$point[1])
  $moved = [pscustomobject]@{ x = [int]$point[0]; y = [int]$point[1] }
}
if ($DwellMs -gt 0) { Start-Sleep -Milliseconds $DwellMs }

# Read back rather than echoed: a position the system clamped or a pointer another process moved is a
# different hover from the one that was asked for.
$cursor = [System.Windows.Forms.Cursor]::Position
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$directory = Split-Path -Parent $Out
if ($directory -and -not (Test-Path -LiteralPath $directory)) {
  New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
  $graphics.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bounds.Size)
  $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
  $graphics.Dispose()
  $bitmap.Dispose()
}

$file = Get-Item -LiteralPath $Out
$sampled = Measure-Uniformity $Out

[pscustomobject]@{
  mode            = "capture"
  out             = $file.FullName
  bytes           = $file.Length
  width           = $bounds.Width
  height          = $bounds.Height
  requestedCursor = $moved
  cursor          = [pscustomobject]@{ x = $cursor.X; y = $cursor.Y }
  dwellMs         = $DwellMs
  sampledColours  = $sampled
  uniform         = ($sampled -le 1)
  screenBounds    = (Get-ScreenBounds)
} | ConvertTo-Json -Compress -Depth 6
