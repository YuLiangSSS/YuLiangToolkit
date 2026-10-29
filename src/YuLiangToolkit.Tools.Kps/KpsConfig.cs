using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Tools.Kps.Models;
using YuLiangToolkit.Tools.Kps.Presets;

namespace YuLiangToolkit.Tools.Kps;

/// <summary>
/// KPS 工具的强类型配置 → <c>Config\kps\config.json</c>（改一下自动落盘，没有保存按钮）。
///
/// <para>结构分五块：<b>键位布局</b>（当前布局 + 自定义预设）、<b>按键外观 / 效果</b>、
/// <b>统计与持久化计数器</b>、<b>Overlay 窗口</b>、<b>全局热键</b>、<b>键音</b>。</para>
///
/// <para>⚠ 带 setter 的计算属性请标 <c>[JsonIgnore]</c>（只读属性不会被序列化）。</para>
/// </summary>
public sealed class KpsConfig : ToolConfig
{
    // ===================== 监听 =====================

    /// <summary>打开工具页面时自动开始监听全局键盘（关掉后可以用「开始监听」按钮 / 热键手动开）。</summary>
    public bool AutoStartListening { get; set; } = true;

    // ===================== 键位布局 =====================

    /// <summary>当前生效的预设 Id（内置预设见 <see cref="KpsPresets.BuiltIn"/>；自定义预设存在 <see cref="CustomPresets"/>）。</summary>
    public string ActivePresetId { get; set; } = KpsPresets.DefaultId;

    /// <summary>当前键位布局（可以直接编辑：加行 / 加键 / 改键 / 改文字 / 图片）。</summary>
    public List<KpsKeyRow> KeyRows { get; set; } = KpsPresets.Default.CloneRows();

    /// <summary>用户「另存为」的预设（整套布局存这里，切换预设互不覆盖）。</summary>
    public List<KpsPreset> CustomPresets { get; set; } = new();

    // ===================== 按键外观 =====================

    /// <summary>未按下时的按键背景色（<c>#AARRGGBB</c>；默认深色半透明，和 DmNote 默认观感一致）。</summary>
    public string KeyIdleColor { get; set; } = "#B80E0E11";

    /// <summary>按下时的按键背景色（每个键可单独覆盖；默认白底 + 深色字）。</summary>
    public string KeyPressedColor { get; set; } = "#E6FFFFFF";

    /// <summary>按键边框色。</summary>
    public string KeyBorderColor { get; set; } = "#24FFFFFF";

    /// <summary>未按下时的按键文字颜色。</summary>
    public string KeyTextColor { get; set; } = "#C7EDEEF2";

    /// <summary>按下时的按键文字颜色（白底配深色字；换暖色底时记得一起改）。</summary>
    public string KeyPressedTextColor { get; set; } = "#E6141418";

    /// <summary>计数器文字颜色。</summary>
    public string KeyCounterColor { get; set; } = "#C7EDEEF2";

    /// <summary>按键宽度（DIP，不含倍数）。</summary>
    public double KeyWidth { get; set; } = 66;

    /// <summary>按键高度（DIP）。</summary>
    public double KeyHeight { get; set; } = 74;

    /// <summary>格子间距（DIP）。</summary>
    public double KeyGap { get; set; } = 6;

    /// <summary>按键圆角（DIP）。</summary>
    public double KeyCornerRadius { get; set; } = 10;

    /// <summary>按键文字字号。</summary>
    public double KeyFontSize { get; set; } = 22;

    /// <summary>计数器字号。</summary>
    public double CounterFontSize { get; set; } = 11;

    /// <summary>是否显示每键计数器（总开关；单键还可以在编辑里单独看）。</summary>
    public bool ShowKeyCounters { get; set; } = true;

    // ===================== 按下效果 =====================

    /// <summary>按下时按键缩小一点（0.86 倍左右）。</summary>
    public bool EffectScale { get; set; } = true;

    /// <summary>按下时发光（外发光）。</summary>
    public bool EffectGlow { get; set; } = true;

    /// <summary>按下时闪光（背景闪白）。</summary>
    public bool EffectFlash { get; set; } = false;

    /// <summary>按下 / 抬起动画时长（毫秒，40~400）。</summary>
    public int EffectDurationMs { get; set; } = 110;

    // ===================== 音符效果（DmNote 的 노트 이펙트） =====================

    /// <summary>按下时格子里有一道音符滑过（长按持续播放）。</summary>
    public bool NoteEffectEnabled { get; set; } = false;

    /// <summary>音符颜色（<c>#AARRGGBB</c>）。</summary>
    public string NoteEffectColor { get; set; } = "#FFFFC857";

    /// <summary>音符透明度（0~1）。</summary>
    public double NoteEffectOpacity { get; set; } = 0.7;

    /// <summary>音符滑过一格的时间（毫秒，150~3000；越小越快）。</summary>
    public int NoteEffectDurationMs { get; set; } = 700;

    /// <summary>音符高度占格高的比例（0.15~0.9）。</summary>
    public double NoteEffectHeight { get; set; } = 0.55;

    /// <summary>反向：从下往上滑（默认从上往下）。</summary>
    public bool NoteEffectReverse { get; set; } = false;

    // ===================== 统计 =====================

    /// <summary>KPS 折线图显示最近多少秒（10~120）。</summary>
    public int GraphSeconds { get; set; } = 30;

    /// <summary>只统计当前布局里绑定的键（关 = 键盘上任何键都算进总按键 / KPS）。</summary>
    public bool CountOnlyBoundKeys { get; set; } = false;

    /// <summary>把每键累计计数器存进配置（重启程序后保留；重置统计会一起清零）。</summary>
    public bool PersistKeyCounters { get; set; } = true;

    /// <summary>持久化的每键计数（键 = 虚拟键码字符串，值 = 累计次数）。</summary>
    public Dictionary<string, long> SavedKeyCounters { get; set; } = new();

    // ===================== Overlay 窗口 =====================

    /// <summary>Overlay 是否显示（上次关闭前的状态会记住，但首次运行默认关，和 DmNote 一致）。</summary>
    public bool OverlayEnabled { get; set; } = false;

    /// <summary>锁定 Overlay：鼠标点击穿透（游戏里点不到它），位置 / 大小也改不了（用热键解锁）。</summary>
    public bool OverlayLocked { get; set; } = true;

    /// <summary>Overlay 始终置顶（窗口化 / 无边框全屏游戏有效；独占全屏请自己改无边框）。</summary>
    public bool OverlayTopmost { get; set; } = true;

    /// <summary>Overlay 左边界（DIP；null = 首次居中显示）。</summary>
    public double? OverlayLeft { get; set; }

    /// <summary>Overlay 上边界（DIP；null = 首次居中显示）。</summary>
    public double? OverlayTop { get; set; }

    /// <summary>Overlay 整体缩放（0.4~3.0；未锁定时也可以直接滚轮缩放）。</summary>
    public double OverlayScale { get; set; } = 1.0;

    /// <summary>Overlay 不透明度（0.1~1.0）。</summary>
    public double OverlayOpacity { get; set; } = 1.0;

    /// <summary>Overlay 背景色（默认全透明；<c>#AARRGGBB</c>，可以用半透明黑当底板）。</summary>
    public string OverlayBackgroundColor { get; set; } = "#00000000";

    /// <summary>缩放基准点：TopLeft / TopRight / BottomLeft / BottomRight / Center（滚轮缩放时固定哪个角）。</summary>
    public string OverlayScaleAnchor { get; set; } = "TopLeft";

    /// <summary>Overlay 上显示 KPS 大数字。</summary>
    public bool OverlayShowKps { get; set; } = true;

    /// <summary>Overlay 上显示键位格子。</summary>
    public bool OverlayShowKeys { get; set; } = true;

    /// <summary>Overlay 上显示总按键数。</summary>
    public bool OverlayShowTotal { get; set; } = true;

    /// <summary>Overlay 上显示峰值 KPS。</summary>
    public bool OverlayShowPeak { get; set; } = true;

    /// <summary>Overlay 上显示平均 KPS。</summary>
    public bool OverlayShowAverage { get; set; } = false;

    /// <summary>Overlay 上显示 KPS 折线图。</summary>
    public bool OverlayShowGraph { get; set; } = false;

    /// <summary>Overlay 上显示每键计数器（继承整体外观设置）。</summary>
    public bool OverlayShowCounters { get; set; } = true;

    // ===================== 全局热键（空字符串 = 不注册） =====================

    /// <summary>显示 / 隐藏 Overlay（默认同 DmNote：Ctrl+Shift+O）。</summary>
    public string HotkeyToggleOverlay { get; set; } = "Ctrl+Shift+O";

    /// <summary>锁定 / 解锁 Overlay（解锁后用鼠标拖动、滚轮缩放）。</summary>
    public string HotkeyToggleLock { get; set; } = "Ctrl+Shift+L";

    /// <summary>重置统计与每键计数器。</summary>
    public string HotkeyResetStats { get; set; } = "Ctrl+Shift+R";

    /// <summary>暂停 / 继续监听（暂停后所有键都不计数）。</summary>
    public string HotkeyToggleListening { get; set; } = "Ctrl+Shift+P";

    // ===================== 键音 =====================

    /// <summary>按下按键时播放键音。</summary>
    public bool KeySoundEnabled { get; set; } = false;

    /// <summary>键音音量（0~100）。</summary>
    public int KeySoundVolume { get; set; } = 60;

    /// <summary>自定义键音文件（WAV，绝对路径；空 = 用内置合成点击声）。</summary>
    public string KeySoundPath { get; set; } = "";

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
