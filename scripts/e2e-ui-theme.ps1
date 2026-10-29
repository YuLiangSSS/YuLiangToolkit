param(
    [string]$Exe = "C:\Users\16320\Desktop\Project\YuLiangToolkit\bin\Debug\net9.0-windows\YuLiangTookit.exe",
    [string]$OutDir = "C:\Users\16320\Desktop\Project\YuLiangToolkit\shots"
)

# ============================================================================
# 端到端验证「主页设置」页面（v0.26.0）：
#   1) 界面风格单选（默认 / 液态玻璃）立即生效并写进 Config\shell\config.json；
#   2) 自动效果 / 点击效果勾选可以互相独立（关掉自动拖尾、点击效果仍然勾着）；
#   3) 点击效果可多选；全程不重启、设置页就在主窗口里（面包屑 主页 › 主页设置）。
# ⚠ 会真的打开窗口并自动点控件（抢焦点）；跑之前确认没在干别的。
# 数据根用临时 YLT_HOME，不碰你真实配置；退出码 0 = 全部通过。
# ============================================================================

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ShellSettingsE2EWin32
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr FindVisibleWindow(uint targetPid, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != targetPid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

function Wait-Until([scriptblock]$Probe, [int]$TimeoutMs = 15000, [string]$What = "条件") {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try { $value = & $Probe; if ($value) { return $value } } catch { }
        Start-Sleep -Milliseconds 150
    }
    throw "等待超时：$What"
}

function Find-Control($Window, $ControlType, [string]$Name) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    return $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Select-Radio($Radio) {
    $Radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Toggle-CheckBox($CheckBox) {
    $CheckBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
}

function Read-Config([string]$Root) {
    $path = Join-Path $Root "Config\shell\config.json"
    if (-not (Test-Path $path)) { return $null }
    try { return Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}

function Wait-Config([string]$Root, [scriptblock]$Probe, [int]$TimeoutMs = 10000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $cfg = Read-Config $Root
        if ($null -ne $cfg -and (& $Probe $cfg)) { return $cfg }
        Start-Sleep -Milliseconds 150
    }
    throw "等待超时：配置写入（$Root 的 Config\shell\config.json 没出现期望值）"
}

function Capture-Window([IntPtr]$Hwnd, [string]$Path) {
    if ($Hwnd -eq [IntPtr]::Zero) { throw "拿不到窗口句柄" }
    $rect = New-Object ShellSettingsE2EWin32+RECT
    [void][ShellSettingsE2EWin32]::GetWindowRect($Hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try { [void][ShellSettingsE2EWin32]::PrintWindow($Hwnd, $hdc, 2) }
    finally { $g.ReleaseHdc($hdc); $g.Dispose() }
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Assert([bool]$Ok, [string]$Name, [string]$Detail = "") {
    if (-not $Ok) { throw "断言失败：$Name $Detail" }
    Write-Output "PASS | $Name $Detail"
}

$testHome = Join-Path $env:TEMP ("ylt-shell-settings-e2e-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $testHome | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$env:YLT_HOME = $testHome
$env:YLT_OPEN_SETTINGS = "1"     # 启动后直接进「主页设置」页面
$env:YLT_NO_SINGLE_INSTANCE = "1"
Remove-Item Env:YLT_SHOT, Env:YLT_THEME, Env:YLT_NAV_TOOL, Env:YLT_SHOT_TOOL, Env:YLT_EFFECT_DEMO, Env:YLT_PARTICLE_DEMO -ErrorAction SilentlyContinue

$proc = $null
try {
    $proc = Start-Process $Exe -PassThru
    $pid32 = [uint32]$proc.Id

    $mainHwnd = Wait-Until {
        $h = [ShellSettingsE2EWin32]::FindVisibleWindow($pid32, "YuLiang 工具集")
        if ($h -ne [IntPtr]::Zero) { return $h }
        return $null
    } 20000 "主窗口出现"
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($mainHwnd)

    # 等设置页渲染出来（能按名字找到「液态玻璃（实验）」单选按钮）
    $glassRadio = Wait-Until {
        $r = Find-Control $window ([System.Windows.Automation.ControlType]::RadioButton) "液态玻璃（实验）"
        if ($r) { return $r }
        return $null
    } 15000 "设置页的界面风格单选"
    $defaultRadio = Find-Control $window ([System.Windows.Automation.ControlType]::RadioButton) "默认（深色）"
    Assert ($null -ne $defaultRadio) "主页设置页已打开（在主窗口里，面包屑 主页 › 主页设置）"

    $initial = Wait-Config $testHome { param($cfg) $cfg.uiTheme -eq "Default" }
    Assert ($initial.uiTheme -eq "Default") "默认配置 = 默认主题"

    # ① 切到液态玻璃：立即写配置
    Select-Radio $glassRadio
    $cfg = Wait-Config $testHome { param($cfg) $cfg.uiTheme -eq "LiquidGlass" }
    Assert ($cfg.uiTheme -eq "LiquidGlass") "点「液态玻璃」→ uiTheme 写进配置（不重启就生效）"
    Capture-Window $mainHwnd (Join-Path $OutDir "e2e-shell-settings-glass.png")

    # ② 切回默认
    Select-Radio $defaultRadio
    $cfg = Wait-Config $testHome { param($cfg) $cfg.uiTheme -eq "Default" }
    Assert ($cfg.uiTheme -eq "Default") "点「默认」→ uiTheme 切回默认"

    # ③ 自动效果 / 点击效果互相独立：关掉「粒子拖尾」，勾上「散光扩散」
    $trail = Wait-Until { Find-Control $window ([System.Windows.Automation.ControlType]::CheckBox) "粒子拖尾" } 10000 "粒子拖尾勾选框"
    $dispersion = Find-Control $window ([System.Windows.Automation.ControlType]::CheckBox) "散光扩散"
    Assert ($null -ne $dispersion) "点击效果「散光扩散」勾选框存在（点击效果可多选）"

    $before = Wait-Config $testHome { param($cfg) $cfg.autoEffects -contains "particle-trail" }
    Assert ($before.autoEffects -contains "particle-trail") "初始自动效果含粒子拖尾"

    Toggle-CheckBox $trail
    $cfg = Wait-Config $testHome { param($cfg) -not ($cfg.autoEffects -contains "particle-trail") }
    Assert (-not ($cfg.autoEffects -contains "particle-trail")) "取消「粒子拖尾」→ autoEffects 去掉它（自动效果可以全关）"
    Assert ($cfg.clickEffects -contains "particle-burst") "自动效果关掉后，点击效果「粒子爆炸」仍然保留（两者独立）"

    Toggle-CheckBox $dispersion
    $cfg = Wait-Config $testHome { param($cfg) $cfg.clickEffects -contains "dispersion" }
    Assert ($cfg.clickEffects -contains "dispersion") "勾上「散光扩散」→ clickEffects 多出 dispersion（多选生效）"
    Assert ($cfg.clickEffects -contains "particle-burst") "点击效果可同时勾选多个（粒子爆炸 + 散光扩散并存）"

    Capture-Window $mainHwnd (Join-Path $OutDir "e2e-shell-settings-effects.png")

    Write-Output "E2E OK：设置页换主题 / 自动效果与点击效果独立 / 点击效果多选 → 配置实时写入"
    exit 0
}
finally {
    Remove-Item Env:YLT_HOME, Env:YLT_OPEN_SETTINGS, Env:YLT_NO_SINGLE_INSTANCE -ErrorAction SilentlyContinue
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $testHome -ErrorAction SilentlyContinue
}
