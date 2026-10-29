using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Tools.Kps.Models;
using YuLiangToolkit.Tools.Kps.Presets;

namespace YuLiangToolkit.Tools.Kps;

/// <summary>
/// KPS 工具的强类型配置 → <c>Config\kps\config.json</c>（改一下自动落盘，没有保存按钮）。
///
/// <para>结构分几块：<b>键位布局</b>（当前布局 + 每套预设的独立布局 + 自定义预设）、
/// <b>按键外观 / 效果 / 音符效果</b>、<b>统计与持久化计数器</b>、<b>Overlay 窗口</b>、
/// <b>全局热键</b>、<b>键音</b>。</para>
///
/// <para>⚠ 本配置里的用户可见选项都走 <c>SetAndSave</c>：改一下会发 <c>PropertyChanged</c>，
/// 所以主界面设置、预览键位、Overlay 窗口都能即时跟着变（改成自动属性会「改了不刷新」）。
/// 只读计算属性请标 <c>[JsonIgnore]</c>。</para>
/// </summary>
public sealed class KpsConfig : ToolConfig
{
    // ===================== 监听 =====================

    private bool _autoStartListening = true;

    /// <summary>打开工具页面时自动开始监听全局键盘（关掉后可以用「开始监听」按钮 / 热键手动开）。</summary>
    public bool AutoStartListening
    {
        get => _autoStartListening;
        set => SetAndSave(ref _autoStartListening, value);
    }

    // ===================== 键位布局 =====================

    private string _activePresetId = KpsPresets.DefaultId;

    /// <summary>当前生效的预设 Id（内置预设见 <see cref="KpsPresets.BuiltIn"/>；自定义预设存在 <see cref="CustomPresets"/>）。</summary>
    public string ActivePresetId
    {
        get => _activePresetId;
        set => SetAndSave(ref _activePresetId, value);
    }

    private List<KpsKeyRow> _keyRows = KpsPresets.Default.CloneRows();

    /// <summary>当前键位布局（可以直接编辑：加行 / 加键 / 改键 / 改文字 / 图片）。</summary>
    public List<KpsKeyRow> KeyRows
    {
        get => _keyRows;
        set => SetAndSave(ref _keyRows, value ?? new List<KpsKeyRow>());
    }

    /// <summary>
    /// 每套预设各自的布局（切预设不会丢掉之前那套的改动）。
    /// 键 = 预设 Id；值 = 该预设的布局副本。
    /// </summary>
    public Dictionary<string, List<KpsKeyRow>> PresetLayouts { get; set; } = new();

    /// <summary>用户「另存为」的预设（整套布局存这里，切换预设互不覆盖）。</summary>
    public List<KpsPreset> CustomPresets { get; set; } = new();

    // ===================== 按键外观 =====================

    private string _keyIdleColor = "#B80E0E11";

    /// <summary>未按下时的按键背景色（<c>#AARRGGBB</c>；默认深色半透明，和 DmNote 默认观感一致）。</summary>
    public string KeyIdleColor
    {
        get => _keyIdleColor;
        set => SetAndSave(ref _keyIdleColor, value);
    }

    private string _keyPressedColor = "#E6FFFFFF";

    /// <summary>按下时的按键背景色（每个键可单独覆盖；默认白底 + 深色字）。</summary>
    public string KeyPressedColor
    {
        get => _keyPressedColor;
        set => SetAndSave(ref _keyPressedColor, value);
    }

    private string _keyBorderColor = "#24FFFFFF";

    /// <summary>按键边框色。</summary>
    public string KeyBorderColor
    {
        get => _keyBorderColor;
        set => SetAndSave(ref _keyBorderColor, value);
    }

    private string _keyTextColor = "#C7EDEEF2";

    /// <summary>未按下时的按键文字颜色。</summary>
    public string KeyTextColor
    {
        get => _keyTextColor;
        set => SetAndSave(ref _keyTextColor, value);
    }

    private string _keyPressedTextColor = "#E6141418";

    /// <summary>按下时的按键文字颜色（白底配深色字；换暖色底时记得一起改）。</summary>
    public string KeyPressedTextColor
    {
        get => _keyPressedTextColor;
        set => SetAndSave(ref _keyPressedTextColor, value);
    }

    private string _keyCounterColor = "#C7EDEEF2";

    /// <summary>计数器文字颜色。</summary>
    public string KeyCounterColor
    {
        get => _keyCounterColor;
        set => SetAndSave(ref _keyCounterColor, value);
    }

    private double _keyWidth = 66;

    /// <summary>按键宽度（DIP，不含倍数）。</summary>
    public double KeyWidth
    {
        get => _keyWidth;
        set => SetAndSave(ref _keyWidth, Math.Clamp(value, 24, 240));
    }

    private double _keyHeight = 74;

    /// <summary>按键高度（DIP）。</summary>
    public double KeyHeight
    {
        get => _keyHeight;
        set => SetAndSave(ref _keyHeight, Math.Clamp(value, 24, 240));
    }

    private double _keyGap = 6;

    /// <summary>格子间距（DIP）。</summary>
    public double KeyGap
    {
        get => _keyGap;
        set => SetAndSave(ref _keyGap, Math.Clamp(value, 0, 40));
    }

    private double _keyCornerRadius = 10;

    /// <summary>按键圆角（DIP）。</summary>
    public double KeyCornerRadius
    {
        get => _keyCornerRadius;
        set => SetAndSave(ref _keyCornerRadius, Math.Clamp(value, 0, 40));
    }

    private double _keyFontSize = 22;

    /// <summary>按键文字字号。</summary>
    public double KeyFontSize
    {
        get => _keyFontSize;
        set => SetAndSave(ref _keyFontSize, Math.Clamp(value, 8, 72));
    }

    private double _counterFontSize = 11;

    /// <summary>计数器字号。</summary>
    public double CounterFontSize
    {
        get => _counterFontSize;
        set => SetAndSave(ref _counterFontSize, Math.Clamp(value, 6, 32));
    }

    private bool _showKeyCounters = true;

    /// <summary>是否显示每键计数器（总开关）。</summary>
    public bool ShowKeyCounters
    {
        get => _showKeyCounters;
        set => SetAndSave(ref _showKeyCounters, value);
    }

    // ===================== 按下效果 =====================

    private bool _effectScale = true;

    /// <summary>按下时按键缩小一点（0.86 倍左右）。</summary>
    public bool EffectScale
    {
        get => _effectScale;
        set => SetAndSave(ref _effectScale, value);
    }

    private bool _effectGlow = true;

    /// <summary>按下时发光（外发光）。</summary>
    public bool EffectGlow
    {
        get => _effectGlow;
        set => SetAndSave(ref _effectGlow, value);
    }

    private bool _effectFlash;

    /// <summary>按下时闪光（背景闪白）。</summary>
    public bool EffectFlash
    {
        get => _effectFlash;
        set => SetAndSave(ref _effectFlash, value);
    }

    private int _effectDurationMs = 110;

    /// <summary>按下 / 抬起动画时长（毫秒，40~400）。</summary>
    public int EffectDurationMs
    {
        get => _effectDurationMs;
        set => SetAndSave(ref _effectDurationMs, Math.Clamp(value, 40, 400));
    }

    // ===================== 音符效果（DmNote 的ノートイベント） =====================

    private bool _noteEffectEnabled;

    /// <summary>按下时格子里有一道音符滑过（长按持续播放）。</summary>
    public bool NoteEffectEnabled
    {
        get => _noteEffectEnabled;
        set => SetAndSave(ref _noteEffectEnabled, value);
    }

    private string _noteEffectColor = "#FFFFC857";

    /// <summary>音符颜色（<c>#AARRGGBB</c>）。</summary>
    public string NoteEffectColor
    {
        get => _noteEffectColor;
        set => SetAndSave(ref _noteEffectColor, value);
    }

    private double _noteEffectOpacity = 0.7;

    /// <summary>音符透明度（0~1）。</summary>
    public double NoteEffectOpacity
    {
        get => _noteEffectOpacity;
        set => SetAndSave(ref _noteEffectOpacity, Math.Clamp(value, 0, 1));
    }

    private int _noteEffectDurationMs = 700;

    /// <summary>音符滑过一格的时间（毫秒，150~3000；越小越快）。</summary>
    public int NoteEffectDurationMs
    {
        get => _noteEffectDurationMs;
        set => SetAndSave(ref _noteEffectDurationMs, Math.Clamp(value, 150, 3000));
    }

    private double _noteEffectHeight = 0.55;

    /// <summary>音符高度占格高的比例（0.15~0.9）。</summary>
    public double NoteEffectHeight
    {
        get => _noteEffectHeight;
        set => SetAndSave(ref _noteEffectHeight, Math.Clamp(value, 0.15, 0.9));
    }

    private bool _noteEffectReverse;

    /// <summary>反向：从下往上滑（默认从上往下）。</summary>
    public bool NoteEffectReverse
    {
        get => _noteEffectReverse;
        set => SetAndSave(ref _noteEffectReverse, value);
    }

    // ===================== 统计 =====================

    private int _graphSeconds = 30;

    /// <summary>KPS 折线图显示最近多少秒（10~120）。</summary>
    public int GraphSeconds
    {
        get => _graphSeconds;
        set => SetAndSave(ref _graphSeconds, Math.Clamp(value, 10, 120));
    }

    private bool _countOnlyBoundKeys;

    /// <summary>只统计当前布局里绑定的键（关 = 键盘上任何键都算进总按键 / KPS）。</summary>
    public bool CountOnlyBoundKeys
    {
        get => _countOnlyBoundKeys;
        set => SetAndSave(ref _countOnlyBoundKeys, value);
    }

    private bool _persistKeyCounters = true;

    /// <summary>把每键累计计数器存进配置（重启程序后保留；重置统计会一起清零）。</summary>
    public bool PersistKeyCounters
    {
        get => _persistKeyCounters;
        set => SetAndSave(ref _persistKeyCounters, value);
    }

    /// <summary>持久化的每键计数（键 = 虚拟键码字符串，值 = 累计次数）。</summary>
    public Dictionary<string, long> SavedKeyCounters { get; set; } = new();

    // ===================== Overlay 窗口 =====================

    private bool _overlayEnabled;

    /// <summary>Overlay 是否显示（上次关闭前的状态会记住，但首次运行默认关，和 DmNote 一致）。</summary>
    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set => SetAndSave(ref _overlayEnabled, value);
    }

    private bool _overlayLocked = true;

    /// <summary>锁定 Overlay：鼠标点击穿透（游戏里点不到它），位置 / 大小也改不了（用热键解锁）。</summary>
    public bool OverlayLocked
    {
        get => _overlayLocked;
        set => SetAndSave(ref _overlayLocked, value);
    }

    private bool _overlayTopmost = true;

    /// <summary>Overlay 始终置顶（窗口化 / 无边框全屏游戏有效；独占全屏请自己改无边框）。</summary>
    public bool OverlayTopmost
    {
        get => _overlayTopmost;
        set => SetAndSave(ref _overlayTopmost, value);
    }

    private double? _overlayLeft;

    /// <summary>Overlay 左边界（DIP；null = 首次显示在工作区右下角）。</summary>
    public double? OverlayLeft
    {
        get => _overlayLeft;
        set => SetAndSave(ref _overlayLeft, value);
    }

    private double? _overlayTop;

    /// <summary>Overlay 上边界（DIP；null = 首次显示在工作区右下角）。</summary>
    public double? OverlayTop
    {
        get => _overlayTop;
        set => SetAndSave(ref _overlayTop, value);
    }

    private double _overlayScale = 1.0;

    /// <summary>Overlay 整体缩放（0.4~3.0；未锁定时也可以直接滚轮缩放）。</summary>
    public double OverlayScale
    {
        get => _overlayScale;
        set => SetAndSave(ref _overlayScale, Math.Clamp(value, 0.4, 3.0));
    }

    private double _overlayOpacity = 1.0;

    /// <summary>Overlay 不透明度（0.1~1.0）。</summary>
    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set => SetAndSave(ref _overlayOpacity, Math.Clamp(value, 0.1, 1.0));
    }

    private string _overlayBackgroundColor = "#00000000";

    /// <summary>Overlay 背景色（默认全透明；<c>#AARRGGBB</c>，可以用半透明黑当底板）。</summary>
    public string OverlayBackgroundColor
    {
        get => _overlayBackgroundColor;
        set => SetAndSave(ref _overlayBackgroundColor, value);
    }

    private string _overlayScaleAnchor = "TopLeft";

    /// <summary>缩放基准点：TopLeft / TopRight / BottomLeft / BottomRight / Center（滚轮缩放时固定哪个角）。</summary>
    public string OverlayScaleAnchor
    {
        get => _overlayScaleAnchor;
        set => SetAndSave(ref _overlayScaleAnchor, value);
    }

    private bool _overlayShowKps = true;

    /// <summary>Overlay 上显示 KPS 大数字。</summary>
    public bool OverlayShowKps
    {
        get => _overlayShowKps;
        set => SetAndSave(ref _overlayShowKps, value);
    }

    private bool _overlayShowKeys = true;

    /// <summary>Overlay 上显示键位格子。</summary>
    public bool OverlayShowKeys
    {
        get => _overlayShowKeys;
        set => SetAndSave(ref _overlayShowKeys, value);
    }

    private bool _overlayShowTotal = true;

    /// <summary>Overlay 上显示总按键数。</summary>
    public bool OverlayShowTotal
    {
        get => _overlayShowTotal;
        set => SetAndSave(ref _overlayShowTotal, value);
    }

    private bool _overlayShowPeak = true;

    /// <summary>Overlay 上显示峰值 KPS。</summary>
    public bool OverlayShowPeak
    {
        get => _overlayShowPeak;
        set => SetAndSave(ref _overlayShowPeak, value);
    }

    private bool _overlayShowAverage;

    /// <summary>Overlay 上显示平均 KPS。</summary>
    public bool OverlayShowAverage
    {
        get => _overlayShowAverage;
        set => SetAndSave(ref _overlayShowAverage, value);
    }

    private bool _overlayShowGraph;

    /// <summary>Overlay 上显示 KPS 折线图。</summary>
    public bool OverlayShowGraph
    {
        get => _overlayShowGraph;
        set => SetAndSave(ref _overlayShowGraph, value);
    }

    private bool _overlayShowCounters = true;

    /// <summary>Overlay 上显示每键计数器（继承整体外观设置）。</summary>
    public bool OverlayShowCounters
    {
        get => _overlayShowCounters;
        set => SetAndSave(ref _overlayShowCounters, value);
    }

    // ===================== 全局热键（空字符串 = 不注册） =====================

    private string _hotkeyToggleOverlay = "Ctrl+Shift+O";

    /// <summary>显示 / 隐藏 Overlay（默认同 DmNote：Ctrl+Shift+O）。</summary>
    public string HotkeyToggleOverlay
    {
        get => _hotkeyToggleOverlay;
        set => SetAndSave(ref _hotkeyToggleOverlay, value);
    }

    private string _hotkeyToggleLock = "Ctrl+Shift+L";

    /// <summary>锁定 / 解锁 Overlay（解锁后用鼠标拖动、滚轮缩放）。</summary>
    public string HotkeyToggleLock
    {
        get => _hotkeyToggleLock;
        set => SetAndSave(ref _hotkeyToggleLock, value);
    }

    private string _hotkeyResetStats = "Ctrl+Shift+R";

    /// <summary>重置统计与每键计数器。</summary>
    public string HotkeyResetStats
    {
        get => _hotkeyResetStats;
        set => SetAndSave(ref _hotkeyResetStats, value);
    }

    private string _hotkeyToggleListening = "Ctrl+Shift+P";

    /// <summary>暂停 / 继续监听（暂停后所有键都不计数）。</summary>
    public string HotkeyToggleListening
    {
        get => _hotkeyToggleListening;
        set => SetAndSave(ref _hotkeyToggleListening, value);
    }

    // ===================== 键音 =====================

    private bool _keySoundEnabled;

    /// <summary>按下按键时播放键音。</summary>
    public bool KeySoundEnabled
    {
        get => _keySoundEnabled;
        set => SetAndSave(ref _keySoundEnabled, value);
    }

    private int _keySoundVolume = 60;

    /// <summary>键音音量（0~100）。</summary>
    public int KeySoundVolume
    {
        get => _keySoundVolume;
        set => SetAndSave(ref _keySoundVolume, Math.Clamp(value, 0, 100));
    }

    private string _keySoundPath = "";

    /// <summary>自定义键音文件（WAV 绝对路径；空 = 用内置合成点击声）。</summary>
    public string KeySoundPath
    {
        get => _keySoundPath;
        set => SetAndSave(ref _keySoundPath, value ?? "");
    }

    // ===================== 计算属性（不写进 JSON） =====================

    /// <summary>当前预设名（界面标题 / Overlay 上显示用）。</summary>
    [JsonIgnore]
    public string ActivePresetName
    {
        get
        {
            var builtIn = KpsPresets.Find(ActivePresetId);
            if (builtIn is not null) return builtIn.Name;
            var custom = CustomPresets.FirstOrDefault(p =>
                string.Equals(p.Id, ActivePresetId, StringComparison.OrdinalIgnoreCase));
            return custom?.Name ?? "自定义";
        }
    }

    /// <summary>有没有自定义预设（决定「删除预设」按钮可用）。</summary>
    [JsonIgnore]
    public bool HasCustomPresets => CustomPresets.Count > 0;
}
