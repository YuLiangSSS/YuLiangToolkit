using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.TextEditor;

/// <summary>
/// 「文本编辑器」的配置（<c>Config\text-editor\config.json</c>，改一下自动落盘）。
/// 字体 / 字号 / 缩放 / 颜色 / 行号 / 自动换行 / 缩进，以及最近文件与上次会话。
/// </summary>
public sealed class TextEditorConfig : ToolConfig
{
    /// <summary>功能 Id：同时是 <c>Config\text-editor</c> 的文件夹名。</summary>
    public const string ToolId = "text-editor";

    // ---------------- 默认值（自检会逐条断言） ----------------

    /// <summary>默认字体：Windows 自带的等宽 Cascadia Mono，中文回退微软雅黑。</summary>
    public const string DefaultFontName = "Cascadia Mono";

    /// <summary>默认字体回退链（WPF FontFamily 的写法，逗号分隔，找不到的自动跳过）。</summary>
    public const string FontFallbackChain = ", Cascadia Mono, Consolas, Microsoft YaHei UI";

    /// <summary>默认编辑区背景（跟随主题的 WindowBackground 同款深色；可在设置里改）。</summary>
    public const string DefaultBackgroundHex = "#16181D";

    /// <summary>默认编辑区文字颜色。</summary>
    public const string DefaultForegroundHex = "#E8EAF0";

    public const double DefaultFontSize = 14;
    public const double DefaultZoomPercent = 100;
    public const double MinZoomPercent = 25;
    public const double MaxZoomPercent = 400;

    /// <summary>每次缩放步进（菜单 放大 / 缩小 / Ctrl+滚轮，百分比）。</summary>
    public const double ZoomStepPercent = 10;

    public const double MinFontSize = 8;
    public const double MaxFontSize = 36;
    public const int MaxRecentFiles = 10;

    /// <summary>默认行距系数（1.0 = 编辑器默认紧凑行高；1.2 = 稍微松一点，看着不挤）。</summary>
    public const double DefaultLineSpacing = 1.2;

    /// <summary>行距下限（再小就回到 AvalonEdit 默认行高）。</summary>
    public const double MinLineSpacing = 1.0;

    /// <summary>行距上限（行高最多翻倍）。</summary>
    public const double MaxLineSpacing = 2.0;

    /// <summary>行距滑块步进（5%）。</summary>
    public const double LineSpacingStep = 0.05;

    /// <summary>会话恢复最多记的标签数（超出丢弃最前面的）。</summary>
    public const int MaxSessionFiles = 20;

    /// <summary>超过这个大小（字节）拒绝打开，避免把界面卡死。</summary>
    public const long MaxFileBytes = 32 * 1024 * 1024;

    /// <summary>等宽字体预设（设置面板下拉框；系统里没装的 WPF 会自动回退默认字体）。</summary>
    public static readonly string[] FontPresets =
    {
        "Cascadia Mono",
        "Consolas",
        "Courier New",
        "Microsoft YaHei UI",
    };

    /// <summary>缩进宽度预设。</summary>
    public static readonly int[] IndentPresets = { 2, 4, 8 };

    /// <summary>颜色预设（色板小方块；点击一起换背景 + 文字，之后仍可各自微调）。</summary>
    public sealed record ColorPreset(string Name, string Background, string Foreground);

    /// <summary>背景颜色预设（配对的深 / 浅两套）。</summary>
    public static readonly ColorPreset[] BackgroundPresets =
    {
        new("深空（默认）", DefaultBackgroundHex, DefaultForegroundHex),
        new("VSCode 深色", "#1E1E1E", "#D4D4D4"),
        new("纯黑", "#000000", "#CCCCCC"),
        new("纸白", "#FAF7F0", "#333333"),
        new("护眼米黄", "#F5EFDD", "#40382A"),
    };

    // ---------------- 可配置项 ----------------
    // 注意：全部用 Set() 发通知 —— 字号 / 缩放 / 颜色拖一下就要实时刷到编辑器与预览块。

    private string _fontName = DefaultFontName;
    private double _fontSize = DefaultFontSize;
    private double _lineSpacing = DefaultLineSpacing;
    private double _zoomPercent = DefaultZoomPercent;
    private string _backgroundHex = DefaultBackgroundHex;
    private string _foregroundHex = DefaultForegroundHex;
    private int _indentSize = 4;

    /// <summary>字体名（存干净的家族名，拼上 <see cref="FontFallbackChain"/> 用）。</summary>
    public string FontName
    {
        get => _fontName;
        set => Set(ref _fontName, string.IsNullOrWhiteSpace(value) ? DefaultFontName : value.Trim());
    }

    /// <summary>基础字号（8 ~ 36；实际显示字号 = 字号 × 缩放）。</summary>
    public double FontSize
    {
        get => _fontSize;
        set => Set(ref _fontSize, ClampFontSize(value));
    }

    /// <summary>行距系数（1.0 ~ 2.0；1.0 = 默认紧凑行高，1.2 = 稍微松一点）。用 Set() 发通知，拖滑块即时生效。</summary>
    public double LineSpacing
    {
        get => _lineSpacing;
        set => Set(ref _lineSpacing, ClampLineSpacing(value));
    }

    /// <summary>界面缩放百分比（25 ~ 400，Zed 风格：只缩编辑区文字，菜单栏不变）。</summary>
    public double ZoomPercent
    {
        get => _zoomPercent;
        set => Set(ref _zoomPercent, ClampZoomPercent(value));
    }

    /// <summary>编辑区背景颜色（<c>#RRGGBB</c> / <c>#AARRGGBB</c>）。</summary>
    public string BackgroundHex
    {
        get => _backgroundHex;
        set => Set(ref _backgroundHex, value?.Trim() ?? DefaultBackgroundHex);
    }

    /// <summary>编辑区文字颜色。</summary>
    public string ForegroundHex
    {
        get => _foregroundHex;
        set => Set(ref _foregroundHex, value?.Trim() ?? DefaultForegroundHex);
    }

    private bool _showLineNumbers = true;
    private bool _wordWrap;
    private bool _convertTabsToSpaces = true;
    private bool _showMarkdownPreview = true;
    private bool _autoSave = true;

    /// <summary>
    /// 自动保存（默认开）：编辑区一有改动，停手约 0.9 秒就自动写回原文件，不用按 Ctrl+S。
    /// 无标题标签没有文件路径，仍需先 Ctrl+S 选一次位置；文件被外部程序改过时自动暂停保护。
    /// </summary>
    public bool AutoSave
    {
        get => _autoSave;
        set => Set(ref _autoSave, value);
    }

    /// <summary>是否显示行号列（默认开，写代码看行数）。用 Set() 发通知：菜单勾选 / 编辑器同步联动。</summary>
    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set => Set(ref _showLineNumbers, value);
    }

    /// <summary>
    /// 打开 Markdown 文件时是否自动显示右侧/下方的「实际效果预览」窗（默认开）。
    /// 用 Set() 发通知：菜单勾选与预览窗显隐联动。
    /// </summary>
    public bool ShowMarkdownPreview
    {
        get => _showMarkdownPreview;
        set => Set(ref _showMarkdownPreview, value);
    }

    /// <summary>自动换行（同样要通知菜单勾选与编辑器）。</summary>
    public bool WordWrap
    {
        get => _wordWrap;
        set => Set(ref _wordWrap, value);
    }

    /// <summary>Tab 键输入空格（关掉 = 输入真实 \t）。</summary>
    public bool ConvertTabsToSpaces
    {
        get => _convertTabsToSpaces;
        set => Set(ref _convertTabsToSpaces, value);
    }

    /// <summary>缩进宽度（1 ~ 8 个空格 / 一个制表符的显示宽度）。</summary>
    public int IndentSize
    {
        get => _indentSize;
        set => Set(ref _indentSize, ClampIndentSize(value));
    }

    /// <summary>最近打开的文件（新打开的排最前，最多 <see cref="MaxRecentFiles"/> 条）。</summary>
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>上次关闭时打开着的有文件路径的标签（会话恢复用；无标题草稿不记）。</summary>
    public List<string> SessionFiles { get; set; } = new();

    /// <summary>上次会话激活的标签序号（越界由 <see cref="ClampSessionIndex"/> 夹回）。</summary>
    public int SessionActiveIndex { get; set; }

    /// <summary>是否恢复上次会话。</summary>
    public bool RememberSession { get; set; } = true;

    // ---------------- 纯函数（自检直接断言） ----------------

    /// <summary>字号夹取：非法值 / 越界都回到合法范围（8 ~ 36）。</summary>
    public static double ClampFontSize(double value)
        => !double.IsFinite(value) ? DefaultFontSize : Math.Clamp(value, MinFontSize, MaxFontSize);

    /// <summary>行距夹取：非法值回默认 1.2，越界夹回 1.0 ~ 2.0，按 5% 取整。</summary>
    public static double ClampLineSpacing(double value)
    {
        if (!double.IsFinite(value)) return DefaultLineSpacing;

        // 用「20 步 = 1.0」做整数取整，避免 0.05 是无限二进制小数导致 1.2 → 1.2000000000000002
        const double stepsPerUnit = 20;   // 1 / LineSpacingStep
        var steps = Math.Round(value * stepsPerUnit, MidpointRounding.AwayFromZero);
        return Math.Clamp(steps / stepsPerUnit, MinLineSpacing, MaxLineSpacing);
    }

    /// <summary>缩放夹取：非法值回 100%，越界夹回 25 ~ 400，四舍五入到 0.5%。</summary>
    public static double ClampZoomPercent(double value)
    {
        if (!double.IsFinite(value)) return DefaultZoomPercent;
        var rounded = Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;
        return Math.Clamp(rounded, MinZoomPercent, MaxZoomPercent);
    }

    /// <summary>按一步缩放（放大 <c>direction &gt; 0</c> / 缩小 <c>&lt; 0</c>），到顶不再变。</summary>
    public static double StepZoomPercent(double current, double direction)
    {
        if (direction > 0) return ClampZoomPercent(current + ZoomStepPercent);
        if (direction < 0) return ClampZoomPercent(current - ZoomStepPercent);
        return ClampZoomPercent(current);
    }

    /// <summary>缩进宽度夹取（1 ~ 8）。</summary>
    public static int ClampIndentSize(int value)
        => value is >= 1 and <= 8 ? value : 4;

    /// <summary>会话激活序号夹回合法范围（空列表 → 0）。</summary>
    public static int ClampSessionIndex(int index, int count)
    {
        if (count <= 0) return 0;
        return Math.Clamp(index, 0, count - 1);
    }

    /// <summary>拼出 WPF 用的 FontFamily 字符串：用户选的字体在前，等宽回退链兜底。</summary>
    public static string BuildFontFamily(string fontName)
    {
        var name = string.IsNullOrWhiteSpace(fontName) ? DefaultFontName : fontName.Trim();
        return name + FontFallbackChain;
    }
}
