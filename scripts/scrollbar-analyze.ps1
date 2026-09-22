# Analyze the in-app capture for custom scrollbar evidence:
#  thumb color #3A414D (58,65,77) forming a vertical strip at the right edge,
#  on window background #16181D (22,24,29), with no default light track.
param(
    [string]$Path = "C:\Users\16320\Desktop\Project\YuLiangTookit\shots\scrollbar-inapp.png"
)

Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Path)
Write-Output "image: $($bmp.Width) x $($bmp.Height)"

$thumbPts = New-Object System.Collections.Generic.List[object]
$thumbYMin = 999999; $thumbYMax = -1; $thumbXMin = 999999; $thumbXMax = -1
$edgeBg = 0
for ($y = 0; $y -lt $bmp.Height; $y++) {
    for ($x = [Math]::Max(0, $bmp.Width - 16); $x -lt $bmp.Width; $x++) {
        $c = $bmp.GetPixel($x, $y)
        $dThumb = [Math]::Abs($c.R-58) + [Math]::Abs($c.G-65) + [Math]::Abs($c.B-77)
        if ($dThumb -le 12) {
            if ($x -lt $thumbXMin) { $thumbXMin = $x }
            if ($x -gt $thumbXMax) { $thumbXMax = $x }
            if ($y -lt $thumbYMin) { $thumbYMin = $y }
            if ($y -gt $thumbYMax) { $thumbYMax = $y }
        }
        $dBg = [Math]::Abs($c.R-22) + [Math]::Abs($c.G-24) + [Math]::Abs($c.B-29)
        if ($dBg -le 10) { $edgeBg++ }
    }
}
Write-Output ("thumb pixels in right 16px: x {0}..{1}, y {2}..{3} (height {4}px)" -f $thumbXMin, $thumbXMax, $thumbYMin, $thumbYMax, ($thumbYMax - $thumbYMin + 1))
Write-Output ("window-bg (#16181D) pixels in right 16px: {0}" -f $edgeBg)

# sample the pixel columns at thumb x range and 6px further left (inside content)
$sampleX = [Math]::Max(0, $thumbXMin - 6)
$row = [Math]::Max(0, [Math]::Min($bmp.Height - 1, ($thumbYMin + $thumbYMax) / 2))
$c1 = $bmp.GetPixel($sampleX, [int]$row)
$c2 = $bmp.GetPixel([int](($thumbXMin + $thumbXMax)/2), [int]$row)
Write-Output ("content pixel at x=$sampleX,y=$([int]$row): #{0:X2}{1:X2}{2:X2}" -f $c1.R, $c1.G, $c1.B)
Write-Output ("thumb pixel at x=$([int](($thumbXMin+$thumbXMax)/2)),y=$([int]$row): #{0:X2}{1:X2}{2:X2}" -f $c2.R, $c2.G, $c2.B)

# any light-gray default scrollbar pixels anywhere (#F0F0F0 track)?
$light = 0
for ($y = 0; $y -lt $bmp.Height; $y += 3) {
    for ($x = 0; $x -lt $bmp.Width; $x += 3) {
        $c = $bmp.GetPixel($x, $y)
        if ($c.R -gt 220 -and $c.G -gt 220 -and $c.B -gt 220) { $light++ }
    }
}
Write-Output ("light-gray (#F0F0F0-ish) pixels sampled: {0}" -f $light)

# full-image thumb-color scan to confirm the strip is only at the right edge
$globalThumb = 0
for ($y = 0; $y -lt $bmp.Height; $y += 2) {
    for ($x = 0; $x -lt $bmp.Width; $x += 2) {
        $c = $bmp.GetPixel($x, $y)
        $d = [Math]::Abs($c.R-58) + [Math]::Abs($c.G-65) + [Math]::Abs($c.B-77)
        if ($d -le 12) { $globalThumb++ }
    }
}
Write-Output ("thumb-color pixels sampled image-wide: {0}" -f $globalThumb)
$bmp.Dispose()
Write-Output "=== done ==="
