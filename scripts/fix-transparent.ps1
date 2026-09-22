# Replace fully-transparent pixels (capture artifact) with the real window background #16181D,
# preserving any genuinely-black opaque pixels (e.g. emoji glyphs).
param(
    [string]$In  = "C:\Users\16320\Desktop\Project\YuLiangTookit\shots\scrollbar-inapp.png",
    [string]$Out = "C:\Users\16320\Desktop\Project\YuLiangTookit\shots\scrollbar-final.png"
)

Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($In)
$replaced = 0
for ($y = 0; $y -lt $bmp.Height; $y++) {
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        $c = $bmp.GetPixel($x, $y)
        if ($c.A -eq 0) { $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, 22, 24, 29)); $replaced++ }
    }
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "replaced $replaced transparent pixels -> saved $Out"
$bmp.Dispose()
