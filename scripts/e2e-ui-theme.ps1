param(
    [string]$Exe = "C:\Users\16320\Desktop\Project\YuLiangToolkit\bin\Debug\net9.0-windows\YuLiangTookit.exe",
    [string]$OutDir = "C:\Users\16320\Desktop\Project\YuLiangToolkit\shots"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ThemeE2EWin32
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

function Wait-Until([scriptblock]$Probe, [int]$TimeoutMs = 10000, [string]$What = "条件") {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $last = $null
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try { $last = & $Probe; if ($last) { return $last } } catch { $last = $null }
        Start-Sleep -Milliseconds 150
    }
    throw "等待超时：$What（最后一次=$last）"
}

function Find-Radio($Window, [string]$NamePattern) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::RadioButton)
    $radios = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($r in $radios) {
        try { if ($r.Current.Name -like $NamePattern) { return $r } } catch { }
    }
    return $null
}

function Select-Radio($Radio) {
    $pattern = $Radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}

function Capture-Window([IntPtr]$Hwnd, [string]$Path) {
    if ($Hwnd -eq [IntPtr]::Zero) { throw "拿不到窗口句柄" }
    $rect = New-Object ThemeE2EWin32+RECT
    [void][ThemeE2EWin32]::GetWindowRect($Hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try {
        # PW_RENDERFULLCONTENT=2：即使被别的窗口挡住也能按窗口自身内容渲染（WPF 可用）
        [void][ThemeE2EWin32]::PrintWindow($Hwnd, $hdc, 2)
    } finally {
        $g.ReleaseHdc($hdc)
        $g.Dispose()
    }
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return "$w x $h"
}

function Read-ThemeConfig([string]$Root) {
    $cfgPath = Join-Path $Root "Config\shell\config.json"
    if (-not (Test-Path $cfgPath)) { return $null }
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $json = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($json.PSObject.Properties.Name -contains "uiTheme") { return $json.uiTheme }
        } catch { }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

$TestHome = Join-Path $env:TEMP ("ylt-theme-e2e-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $TestHome | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$env:YLT_HOME = $TestHome
$env:YLT_OPEN_SETTINGS = "1"
$env:YLT_NO_SINGLE_INSTANCE = "1"
Remove-Item Env:YLT_SHOT, Env:YLT_THEME, Env:YLT_NAV_TOOL, Env:YLT_SHOT_TOOL -ErrorAction SilentlyContinue

$proc = $null
try {
    $proc = Start-Process $Exe -PassThru
    $pid32 = [uint32]$proc.Id

    # 设置窗口是 Owner 窗口：UIA 的 RootElement 子项有时看不到它，直接从 Win32 句柄拿 AutomationElement
    $settingsHwnd = Wait-Until {
        $h = [ThemeE2EWin32]::FindVisibleWindow($pid32, "主页设置")
        if ($h -ne [IntPtr]::Zero) { return $h }
        return $null
    } 20000 "设置窗口出现"
    $mainHwnd = Wait-Until {
        $h = [ThemeE2EWin32]::FindVisibleWindow($pid32, "YuLiang 工具集")
        if ($h -ne [IntPtr]::Zero) { return $h }
        return $null
    } 20000 "主窗口出现"
    "窗口句柄: 设置=$settingsHwnd 主窗口=$mainHwnd"

    $settings = [System.Windows.Automation.AutomationElement]::FromHandle($settingsHwnd)
    Start-Sleep -Milliseconds 600

    $glassRadio = Find-Radio $settings "*液态玻璃*"
    $defaultRadio = Find-Radio $settings "*默认*"
    if ($null -eq $glassRadio -or $null -eq $defaultRadio) { throw "找不到主题单选按钮" }
    "单选按钮: 液态玻璃=[$($glassRadio.Current.Name)] / 默认=[$($defaultRadio.Current.Name)]"

    # ① 运行中切到液态玻璃（不重启）
    Select-Radio $glassRadio
    Start-Sleep -Milliseconds 1800
    $theme1 = Read-ThemeConfig $TestHome
    "切到液态玻璃后配置 uiTheme = $theme1"
    [void](Capture-Window $settingsHwnd (Join-Path $OutDir "e2e-switched-glass-settings.png"))
    if ($mainHwnd -ne [IntPtr]::Zero) { [void](Capture-Window $mainHwnd (Join-Path $OutDir "e2e-switched-glass-main.png")) }

    # ② 切回默认
    Select-Radio $defaultRadio
    Start-Sleep -Milliseconds 1800
    $theme2 = Read-ThemeConfig $TestHome
    "切回默认后配置 uiTheme = $theme2"
    [void](Capture-Window $settingsHwnd (Join-Path $OutDir "e2e-switched-default-settings.png"))
    if ($mainHwnd -ne [IntPtr]::Zero) { [void](Capture-Window $mainHwnd (Join-Path $OutDir "e2e-switched-default-main.png")) }

    if ($theme1 -ne "LiquidGlass") { throw "第一次切换没有写进配置：$theme1" }
    if ($theme2 -ne "Default") { throw "第二次切换没有写进配置：$theme2" }

    "E2E OK：设置窗口内切换主题 -> 配置实时写入 -> 截图已生成"
    Get-ChildItem (Join-Path $OutDir "e2e-switched-*.png") | Select-Object Name, Length
} finally {
    Remove-Item Env:YLT_HOME, Env:YLT_OPEN_SETTINGS, Env:YLT_NO_SINGLE_INSTANCE -ErrorAction SilentlyContinue
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $TestHome -ErrorAction SilentlyContinue
}
