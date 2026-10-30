# ASCII-only E2E check for the "batch-rename" rule list drag & drop.
#
# Two scenarios:
#   A) drag the first rule onto the lower half of the third -> order must rotate, app must survive
#   B) drag the first rule and HOVER just below the insertion line for ~2s, then drop
#      -> order must stay unchanged (regression for the indicator-line flicker loop)
#
# Why B matters: the insertion line used to be toggled with Visibility, so showing it grew the row by
# 5px. The pointer then landed on the line itself (outside the card Border), DragOver bubbled up to the
# ListBox fallback ("empty area = drop at the end"), the line jumped to the bottom, the row shrank back,
# and the whole thing oscillated forever.
#
# WARNING: this moves the real mouse cursor and steals focus for a few seconds.

param(
    [string]$Exe = "C:\Users\16320\Desktop\Project\YuLiangTookit\bin\Debug\net9.0-windows10.0.19041.0\YuLiangTookit.exe",
    [string]$Config = "C:\Users\16320\Desktop\Project\YuLiangTookit\Config\batch-rename\config.json"
)

$ErrorActionPreference = "Stop"
Remove-Item Env:YLT_SHOT -ErrorAction SilentlyContinue
$env:YLT_NAV_TOOL = "batch-rename"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class YltMouse {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
    public static void Down() { mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); }
    public static void Up()   { mouse_event(LEFTUP,   0, 0, 0, IntPtr.Zero); }
}
"@

$script:failures = 0

$script:DebugLog = $env:YLT_DRAG_DEBUG
function DebugLog {
    param([string]$Line)
    if (-not $script:DebugLog) { return }
    try { Add-Content -Path $script:DebugLog -Value ("{0} SCRIPT {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $Line) } catch { }
}

function Get-RuleKinds {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return @() }
    try { $json = Get-Content $Path -Raw | ConvertFrom-Json } catch { return @() }
    if (-not $json.rules) { return @() }
    return @($json.rules | ForEach-Object { "$($_.kind)" })
}

function Find-RuleCards {
    param($Window)
    $idCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "RuleList")
    $list = $null
    for ($i = 0; $i -lt 12 -and -not $list; $i++) {
        $list = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
        if (-not $list) { Start-Sleep -Milliseconds 600 }
    }
    if (-not $list) { return @() }

    $itemCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    for ($i = 0; $i -lt 10; $i++) {
        $items = @($list.FindAll([System.Windows.Automation.TreeScope]::Children, $itemCond) |
            Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 })
        if ($items.Count -ge 2) { return $items }
        Start-Sleep -Milliseconds 600
    }
    return @()
}

# Press the handle at $From, glide to $To, hold for $HoverMs, release.
function Invoke-RuleDrag {
    param([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY, [int]$HoverMs = 300, [string]$ShotPath, [int]$ShotX = 0, [int]$ShotY = 0, [int]$ShotW = 100, [int]$ShotH = 100)
    [void][YltMouse]::SetCursorPos($FromX, $FromY)
    Start-Sleep -Milliseconds 250
    [YltMouse]::Down()
    Start-Sleep -Milliseconds 250

    $steps = 24
    for ($i = 1; $i -le $steps; $i++) {
        [void][YltMouse]::SetCursorPos(
            [int]($FromX + ($ToX - $FromX) * $i / $steps),
            [int]($FromY + ($ToY - $FromY) * $i / $steps))
        Start-Sleep -Milliseconds 45
    }
    # stay put so any oscillation has time to show up
    for ($i = 0; $i -lt [Math]::Ceiling($HoverMs / 100); $i++) {
        [void][YltMouse]::SetCursorPos($ToX, $ToY)
        Start-Sleep -Milliseconds 100
    }
    if ($ShotPath) {
        $bmp = [System.Drawing.Bitmap]::new($ShotW, $ShotH)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try { $g.CopyFromScreen($ShotX, $ShotY, 0, 0, [System.Drawing.Size]::new($ShotW, $ShotH)) }
        finally { $g.Dispose() }
        $bmp.Save($ShotPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        "  (captured $ShotPath)"
    }
    [YltMouse]::Up()
    Start-Sleep -Milliseconds 1500
}

# ---------------------------------------------------------------- start app
$saved = New-Object YltMouse+POINT
[void][YltMouse]::GetCursorPos([ref]$saved)

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 7
if ($p.HasExited) { throw "app exited during startup" }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)

$win = $null
for ($try = 0; $try -lt 15 -and -not $win; $try++) {
    foreach ($w in @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))) {
        $r = $w.Current.BoundingRectangle
        if (-not [string]::IsNullOrWhiteSpace($w.Current.Name) -and $r.Width -gt 200 -and $r.Height -gt 200) {
            $win = $w; break
        }
    }
    if (-not $win) { Start-Sleep -Seconds 1 }
}
if (-not $win) { Stop-Process -Id $p.Id -Force; throw "main window not found" }
"window: '$($win.Current.Name)'"
[void][YltMouse]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 800

# ---------------------------------------------------------------- scenario A
""
"=== A) drag rule #1 onto the lower half of rule #3 ==="
$before = Get-RuleKinds -Path $Config
"order before : $($before -join ',')"

$cards = Find-RuleCards -Window $win
"visible cards: $($cards.Count)"
if ($cards.Count -lt 3) { Stop-Process -Id $p.Id -Force; throw "not enough rule cards visible" }

$src = $cards[0].Current.BoundingRectangle
$dst = $cards[2].Current.BoundingRectangle
"In  card#1 row: ($([int]$src.Left),$([int]$src.Top)) $([int]$src.Width)x$([int]$src.Height)"
"In  card#3 row: ($([int]$dst.Left),$([int]$dst.Top)) $([int]$dst.Width)x$([int]$dst.Height)"

# handle = ~32px transparent strip on the left of the row
$fx = [int]($src.Left + 16); $fy = [int]($src.Top + 20)
$tx = [int]($dst.Left + 200); $ty = [int]($dst.Top + $dst.Height - 6)   # below the middle => "after"
"drag ($fx,$fy) -> ($tx,$ty)"
Invoke-RuleDrag -FromX $fx -FromY $fy -ToX $tx -ToY $ty -HoverMs 900 -ShotPath "C:\Users\16320\Desktop\Project\YuLiangTookit\shots\hold-A.png" -ShotX ([int]$dst.Left) -ShotY ([int]$src.Top) -ShotW ([int]$dst.Width) -ShotH 220

if ($p.HasExited) { "FAIL A: the app crashed during the drag"; exit 1 }
$after = Get-RuleKinds -Path $Config
$expected = @(@($before[1..2]) + @($before[0]) + @($before[3..($before.Count - 1)]) | Where-Object { $_ })
"order after  : $($after -join ',')"
"order expect : $($expected -join ',')"
if (($after -join ',') -eq ($expected -join ',')) { "PASS A - drag reordered and the app survived" }
else { "FAIL A - order did not change as expected"; $script:failures++ }

# ---------------------------------------------------------------- scenario B
""
"=== B) drag rule #1, hover just BELOW its insertion line for ~2s, then drop ==="
$beforeB = Get-RuleKinds -Path $Config
"order before : $($beforeB -join ',')"

$cards = Find-RuleCards -Window $win
$src = $cards[0].Current.BoundingRectangle
# The insertion line for "after card#1" sits in the row's bottom strip.
# $src.Height is the whole row (card + both line placeholders), so bottom-2 is right on that line.
$hx = [int]($src.Left + 200); $hy = [int]($src.Top + $src.Height - 2)
"row: ($([int]$src.Left),$([int]$src.Top)) $([int]$src.Width)x$([int]$src.Height) -> hover at ($hx,$hy)"
Invoke-RuleDrag -FromX ([int]($src.Left + 16)) -FromY ([int]($src.Top + 20)) -ToX $hx -ToY $hy -HoverMs 2000

if ($p.HasExited) { "FAIL B: the app crashed while hovering on the line"; exit 1 }
$afterB = Get-RuleKinds -Path $Config
"order after  : $($afterB -join ',')"
if (($afterB -join ',') -eq ($beforeB -join ',')) {
    "PASS B - hovering just below the line did not move the rule to the end (no flicker loop)"
}
else { "FAIL B - the drop target ran away to the end (flicker loop still there)"; $script:failures++ }

# ---------------------------------------------------------------- scenario C
# Hover into the 2px margin bands around each insertion line and drop there.
# With the bug these bands are not covered by any hit-testable child, so the drag falls through to the
# ListBox fallback ("empty area => drop at the end") and the dragged rule jumps to the very bottom.
$rowDiag = ""
foreach ($case in @(
        @{ Name = "bottom band of row0 (dy=rowH-3)"; Dy = "rowH-3" },
        @{ Name = "bottom band of row0 (dy=rowH-2)"; Dy = "rowH-2" },
        @{ Name = "top band of row0 (dy=3)";         Dy = "3" },
        @{ Name = "on the line itself (dy=rowH-1)";  Dy = "rowH-1" },
        @{ Name = "middle of card (dy=20)";          Dy = "20" }
    )) {

    $before = Get-RuleKinds -Path $Config
    $cards = Find-RuleCards -Window $win
    if ($cards.Count -lt 2) { "FAIL C: cards vanished"; $script:failures++; break }
    $row = $cards[0].Current.BoundingRectangle
    $rowH = [int]$row.Height
    $dy = switch ($case.Dy) { "rowH-3" { $rowH - 3 } "rowH-2" { $rowH - 2 } "rowH-1" { $rowH - 1 } default { [int]$case.Dy } }
    $ty = [int]$row.Top + $dy
    DebugLog "MARK sweep $($case.Name)"

    "  --- $($case.Name): drop at y=$ty ---"
    Invoke-RuleDrag -FromX ([int]($row.Left + 16)) -FromY ([int]($row.Top + 20)) `
                    -ToX ([int]($row.Left + 400)) -ToY $ty -HoverMs 900

    if ($p.HasExited) { "FAIL C [$($case.Name)]: the app crashed"; $script:failures++; break }

    $after = Get-RuleKinds -Path $Config
    "      order: $($after -join ',')"
    if (($after -join ',') -eq ($before -join ',')) {
        "      PASS - dropping here did not move the rule to the end"
    }
    else {
        "      FAIL - the rule JUMPED TO THE END (dead band fell through to the ListBox fallback)"
        $script:failures++
        # put it back so the next case starts from a known state
        $moved = $after[0]
        $orig = @($before | Where-Object { $_ -ne $moved })
        $fixed = @($moved) + @($before[0..($before.Count - 1)] | Where-Object { $_ -ne $moved })
        $restored = $false
        for ($attempt = 0; $attempt -lt 6 -and -not $restored; $attempt++) {
            $cards = Find-RuleCards -Window $win
            if ($cards.Count -lt 2) { break }
            $last = $cards[$cards.Count - 1].Current.BoundingRectangle
            $first = $cards[0].Current.BoundingRectangle
            Invoke-RuleDrag -FromX ([int]($last.Left + 16)) -FromY ([int]($last.Top + 20)) `
                            -ToX ([int]($first.Left + 400)) -ToY ([int]$first.Top + 20) -HoverMs 300
            $now = Get-RuleKinds -Path $Config
            if (($now -join ',') -eq ($before -join ',')) { $restored = $true }
        }
        "      (restore attempted: $(if ($restored) { 'ok' } else { 'gave up' }))"
    }
}
# ---------------------------------------------------------------- cleanup
[void][YltMouse]::SetCursorPos($saved.X, $saved.Y)
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

""
if ($script:failures -eq 0) { "RESULT: PASS"; exit 0 }
"RESULT: FAIL ($script:failures scenario(s))"
exit 1
