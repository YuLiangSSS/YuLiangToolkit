# e2e-mini-marquee.ps1 -- regression check for the mini player's scrolling title (v0.55.2)
#
# Why this script exists:
#   The old title never revealed its tail. The TextBlock was put directly inside a Grid,
#   WPF measured it wider than its layout slot and added an automatic *layout clip* to the
#   TextBlock itself. That clip travels with the RenderTransform, so scrolling moved the
#   visible window together with the text and the right part stayed cut forever (changing
#   the scroll speed could never fix it). The fix: fixed-width clipping host -> Canvas ->
#   TextBlock, so the only clip is the one that stays put.
#
# What it does (real app, real rendering -- not just reading TextBlock.Text):
#   1. builds an offline fixture: a playlist with a 40+ char title (0-byte file is enough,
#      the mini player only renders the playlist entry here) and a short title;
#   2. runs the real exe with YLT_MINI_DEMO=1 + YLT_SHOT=... (screenshots go to shots/);
#   3. asserts the layout facts through YLT_MINI_MEASURE dump:
#        - long title: text wider than the clip window, TextBlock has NO layout clip,
#          the clip host has ClipToBounds=true, the animation really moved the text;
#        - short title: ScrollDuration=null and no movement;
#   4. freezes the marquee at the far end (YLT_MINI_SHIFT=end position), takes another
#      real screenshot and verifies by PIXELS that the last characters were painted just
#      before the right edge of the window.
#
# Usage:
#   .\scripts\e2e-mini-marquee.ps1
#   .\scripts\e2e-mini-marquee.ps1 -Exe <path to YuLiangTookit.exe>
#
# Exit code: 0 = all checks passed, 1 = a check failed (details printed).

param(
    [string]$Exe = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $root "bin\Debug\net9.0-windows10.0.19041.0\YuLiangTookit.exe"
}
if (-not (Test-Path $Exe)) {
    throw "exe not found: $Exe (run .\build.ps1 first)"
}
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $root "shots"
}

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$inv = [System.Globalization.CultureInfo]::InvariantCulture
$work = Join-Path $root ".artifacts\e2e-mini-marquee"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$media = Join-Path $work "media"
$homeDir = Join-Path $work "home"
$playlistDir = Join-Path $homeDir "Data\media-player"
New-Item -ItemType Directory -Force -Path $media, $playlistDir, $OutDir | Out-Null

# 40+ ASCII chars: the fixture is about a long *title* in the playlist, not about audio content.
$longTitle = "E2E Mini Player Very Long Title With More Than Thirty Five Characters 2026"
$shortTitle = "E2E Short"
$longPath = Join-Path $media ($longTitle + ".mp3")
$shortPath = Join-Path $media ($shortTitle + ".mp3")
[System.IO.File]::WriteAllBytes($longPath, [byte[]]::new(0))
[System.IO.File]::WriteAllBytes($shortPath, [byte[]]::new(0))
$playlistPath = Join-Path $playlistDir "playlist.json"

function Set-CurrentTrack([string]$path) {
    $state = @{ paths = @($longPath, $shortPath); currentPath = $path; positionSeconds = 0.0 }
    [System.IO.File]::WriteAllText($playlistPath, ($state | ConvertTo-Json -Depth 4),
        ([System.Text.UTF8Encoding]::new($false)))
}

function Clear-YltEnv {
    $names = @("YLT_HOME", "YLT_MINI_DEMO", "YLT_WINDOW_SIZE", "YLT_SHOT", "YLT_SHOT_DELAY_MS",
               "YLT_MINI_MEASURE", "YLT_MINI_SHIFT")
    foreach ($name in $names) { Remove-Item ("Env:" + $name) -ErrorAction SilentlyContinue }
}

# Run the real app once (it captures one screenshot and exits) and return its measure dump.
function Invoke-Shot([string]$tag, [int]$delayMs, [string]$shift) {
    $measure = Join-Path $work ("measure-" + $tag + ".txt")
    $shot = Join-Path $OutDir ("mini-marquee-" + $tag + ".png")
    Clear-YltEnv
    $env:YLT_HOME = $homeDir
    $env:YLT_MINI_DEMO = "1"
    $env:YLT_WINDOW_SIZE = "1280x800"
    $env:YLT_SHOT_DELAY_MS = [string]$delayMs
    $env:YLT_SHOT = $shot
    $env:YLT_MINI_MEASURE = $measure
    if (-not [string]::IsNullOrWhiteSpace($shift)) { $env:YLT_MINI_SHIFT = $shift }

    $proc = Start-Process -FilePath $Exe -WorkingDirectory $root -PassThru
    if (-not $proc.WaitForExit(120000)) {
        try { $proc.Kill() } catch { }
        throw "app did not exit in time: $tag"
    }
    if (-not (Test-Path $measure)) { throw "no measure dump: $measure" }
    if (-not (Test-Path $shot)) { throw "no screenshot: $shot" }
    $bytes = [System.IO.File]::ReadAllBytes($measure)
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

function Measure-Number([string]$dump, [string]$key) {
    $match = [regex]::Match($dump, "(?m)^" + [regex]::Escape($key) + "=([-0-9.]+)")
    if (-not $match.Success) { throw "measure key not found: $key" }
    return [double]::Parse($match.Groups[1].Value, $inv)
}

function Find-AncestorLine([string]$dump, [string]$typeName) {
    $match = [regex]::Match($dump, '(?m)^' + [regex]::Escape($typeName) + ' ".*$')
    if (-not $match.Success) { throw "ancestor line not found: $typeName" }
    return $match.Value
}

# Count pixels brighter than the panel background inside a window-space rectangle.
function Count-BrightPixels([string]$pngPath, [int]$x0, [int]$y0, [int]$width, [int]$height) {
    $uri = [System.Uri]::new($pngPath)
    $decoder = [System.Windows.Media.Imaging.PngBitmapDecoder]::new(
        $uri,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::None,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $bitmap = $decoder.Frames[0]
    $x0 = [Math]::Max(0, [Math]::Min($x0, $bitmap.PixelWidth - 1))
    $y0 = [Math]::Max(0, [Math]::Min($y0, $bitmap.PixelHeight - 1))
    $width = [Math]::Max(1, [Math]::Min($width, $bitmap.PixelWidth - $x0))
    $height = [Math]::Max(1, [Math]::Min($height, $bitmap.PixelHeight - $y0))
    $stride = $bitmap.PixelWidth * 4
    $buffer = New-Object byte[] ($stride * $bitmap.PixelHeight)
    $rect = [System.Windows.Int32Rect]::new(0, 0, $bitmap.PixelWidth, $bitmap.PixelHeight)
    $bitmap.CopyPixels($rect, $buffer, $stride, 0)
    $count = 0
    for ($y = $y0; $y -lt ($y0 + $height); $y++) {
        for ($x = $x0; $x -lt ($x0 + $width); $x++) {
            $i = $y * $stride + $x * 4
            $brightest = [Math]::Max([Math]::Max($buffer[$i], $buffer[$i + 1]), $buffer[$i + 2])
            if ($brightest -gt 140) { $count++ }
        }
    }
    return $count
}

$failures = New-Object System.Collections.Generic.List[string]
$passed = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) {
        $script:passed++
        Write-Host ("PASS  " + $name)
    } else {
        $script:failures.Add($name + $(if ($detail) { "  ::  " + $detail } else { "" }))
        Write-Host ("FAIL  " + $name + $(if ($detail) { "  ::  " + $detail } else { "" })) -ForegroundColor Red
    }
}

try {
    # ---------------- 1. long title, animation running ----------------
    Set-CurrentTrack $longPath
    $dump = Invoke-Shot -tag "animated" -delayMs 1200 -shift ""
    $textW = Measure-Number $dump "TitleText.ActualWidth"
    $clipW = Measure-Number $dump "TitleClip.ActualWidth"
    $shiftX = Measure-Number $dump "TitleShift.X"
    $textLine = Find-AncestorLine $dump "TextBlock"
    $clipLine = Find-AncestorLine $dump "Grid"
    $duration = "null"
    $durationMatch = [regex]::Match($dump, "(?m)^ScrollDuration=(\S+)")
    if ($durationMatch.Success) { $duration = $durationMatch.Groups[1].Value }

    Check "long title: text is wider than the clip window (marquee is needed)" `
        ($textW -gt ($clipW + 1)) ("text=" + $textW + " / window=" + $clipW)
    Check "long title: TitleText has NO layout clip (the v0.55.2 root cause)" `
        ($textLine -match "layoutClip=null") $textLine
    Check "long title: clip window is a fixed ClipToBounds host" `
        (($clipLine -match "clipToBounds=True") -and ($clipW -gt 0)) $clipLine
    Check "long title: marquee duration is not null" ($duration -ne "null") $duration
    Check "long title: animation actually moved the text after 1.2 s" `
        ($shiftX -lt -1) ("TitleShift.X=" + $shiftX)
    Check "long title: screenshot saved" (Test-Path (Join-Path $OutDir "mini-marquee-animated.png"))

    # ---------------- 2. long title frozen at the far end: tail must be painted ----------------
    $endShift = -1.0 * ($textW - $clipW + 3.0)   # +3 px overshoot, same as MiniPlayerPanel.OvershootPixels
    $endDump = Invoke-Shot -tag "end" -delayMs 1200 -shift $endShift.ToString($inv)
    $endShiftActual = Measure-Number $endDump "TitleShift.X"
    Check "end position: TitleShift.X matches the frozen end offset" `
        ([Math]::Abs($endShiftActual - $endShift) -lt 0.5) ("expected=" + $endShift + " actual=" + $endShiftActual)

    $windowLine = [regex]::Match($endDump, "(?m)^window=([0-9.]+)x([0-9.]+) TitleClipInWindow=\(([0-9.]+),([0-9.]+)\) ([0-9.]+)x([0-9.]+)")
    if (-not $windowLine.Success) { throw "window/TitleClip rect line not found in measure dump" }
    $clipX = [int][double]::Parse($windowLine.Groups[3].Value, $inv)
    $clipY = [int][double]::Parse($windowLine.Groups[4].Value, $inv)
    $clipWidth = [int][double]::Parse($windowLine.Groups[5].Value, $inv)
    $clipHeight = [int][double]::Parse($windowLine.Groups[6].Value, $inv)
    # The last glyph ends ~3 px before the right edge of the clip window -> ink shows up in this band.
    $bandX = $clipX + $clipWidth - 18
    $bandWidth = 16
    $bright = Count-BrightPixels (Join-Path $OutDir "mini-marquee-end.png") $bandX $clipY $bandWidth $clipHeight
    Check "end position: the tail of the title is really painted at the right edge (pixel check)" `
        ($bright -gt 3) ("bright pixels in tail band=" + $bright + " (band x=" + $bandX + ", w=" + $bandWidth + ")")
    Check "end position: screenshot saved" (Test-Path (Join-Path $OutDir "mini-marquee-end.png"))

    # ---------------- 3. short title: fits -> no scroll at all ----------------
    Set-CurrentTrack $shortPath
    $shortDump = Invoke-Shot -tag "short" -delayMs 1200 -shift ""
    $shortShift = Measure-Number $shortDump "TitleShift.X"
    $shortClipW = Measure-Number $shortDump "TitleClip.ActualWidth"
    $shortTextW = Measure-Number $shortDump "TitleText.ActualWidth"
    Check "short title: fits inside the window, nothing scrolls" `
        (($shortTextW -le $shortClipW) -and ([Math]::Abs($shortShift) -lt 0.01)) `
        ("text=" + $shortTextW + " / window=" + $shortClipW + " / shift=" + $shortShift)
    $shortDurationMatch = [regex]::Match($shortDump, "(?m)^ScrollDuration=(\S+)")
    Check "short title: ScrollDuration is null" `
        ($shortDurationMatch.Success -and $shortDurationMatch.Groups[1].Value -eq "null")
    Check "short title: screenshot saved" (Test-Path (Join-Path $OutDir "mini-marquee-short.png"))
}
finally {
    Clear-YltEnv
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host ("=== mini marquee e2e FAILED: " + $failures.Count + " failed / " + $passed + " passed ===") -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host ("  - " + $failure) -ForegroundColor Red }
    Write-Host ("screenshots: " + $OutDir + "    measure dumps: " + $work)
    exit 1
}
Write-Host ("=== mini marquee e2e PASSED: " + $passed + " checks ===") -ForegroundColor Green
Write-Host ("screenshots: " + $OutDir + "    measure dumps: " + $work)
exit 0
