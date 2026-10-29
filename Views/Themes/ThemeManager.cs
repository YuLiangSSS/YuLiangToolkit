using System.Windows;

namespace YuLiangToolkit.App.Themes;

/// <summary>
/// 运行时主题切换器：把 <c>App.xaml</c> 里那个合并的资源字典整体换成
/// <c>Views/Themes/DefaultTheme.xaml</c> 或 <c>Views/Themes/LiquidGlassTheme.xaml</c>。
///
/// <para><b>为什么换字典就能换皮肤</b>：App.xaml 里的样式只引用「主题 token」（颜色 / 圆角），
/// 所有引用都是 <c>{DynamicResource …}</c>；换掉合并字典后 WPF 会重新解析这些动态资源，
/// 已经打开的窗口（含「主页设置」窗口自己）立即变成另一套 UI，不用重启。</para>
///
/// <para><b>两套字典必须 key 完全一致</b>：缺 key 时 DynamicResource 会悄悄退回控件默认值
/// （例如文字变黑），自检 <c>ThemeChecks</c> 会逐项对比，见 <see cref="RequiredTokenKeys"/>。</para>
///
/// <para>本类同时提供几个**纯函数**（<see cref="SourceFor"/> / <see cref="DisplayName"/> /
/// <see cref="ParseThemeName"/>），自检直接断言，不碰界面。</para>
/// </summary>
public static class ThemeManager
{
    /// <summary>主题资源字典所在的程序集名（主程序，见 YuLiangTookit.csproj）。</summary>
    private const string AssemblyName = "YuLiangTookit";

    /// <summary>当前生效的主题（<see cref="Apply"/> 之后更新）。</summary>
    public static UiTheme Current { get; private set; } = UiTheme.Default;

    /// <summary>主题真的换掉之后触发（参数是新主题）；用于外壳级、没法绑定的收尾动作。</summary>
    public static event EventHandler<UiTheme>? Changed;

    /// <summary>
    /// 两套主题字典必须都提供的 token（顺序即报错顺序；新增颜色时记得同时：
    /// ① 两套字典各加一行；② 把这个名字加进这里 —— 否则自检的「两套字典 key 一致」检查发现不了）。
    /// </summary>
    public static IReadOnlyList<string> RequiredTokenKeys { get; } = new[]
    {
        // ---- 基础颜色 ----
        "WindowBackgroundBrush", "HeaderBackgroundBrush", "SidebarBackgroundBrush",
        "CardBackgroundBrush", "CardHoverBackgroundBrush", "BorderBrush", "SeparatorBrush",
        "AccentBrush", "AccentHoverBrush", "AccentPressedBrush",
        "TextPrimaryBrush", "TextSecondaryBrush", "FaintTextBrush",
        "DangerBrush", "DangerPressedBrush", "SuccessBrush", "InputBackgroundBrush",
        // ---- 控件细节 ----
        "ControlBorderHoverBrush", "WindowButtonHoverBackgroundBrush", "WindowButtonPressedBackgroundBrush",
        "CheckBoxBorderBrush", "ScrollBarTrackBrush", "ScrollBarThumbBrush", "ScrollBarThumbHoverBrush",
        "SidebarItemHoverBackgroundBrush", "SidebarItemSelectedBackgroundBrush",
        "ListRowHoverBackgroundBrush", "ListRowSelectedBackgroundBrush", "ListRowSelectedBorderBrush",
        "TagChipBackgroundBrush", "TagChipBorderBrush", "TagChipHoverBackgroundBrush",
        "EmojiTileBackgroundBrush", "SliderThumbBackgroundBrush", "SliderThumbBorderBrush",
        "MiniPlayerBackgroundBrush", "MiniPlayerHoverBackgroundBrush", "ShellSurfaceBrush",
        // ---- 按钮 ----
        "SecondaryButtonBackgroundBrush",
        "PrimaryButtonBackgroundBrush", "PrimaryButtonHoverBackgroundBrush",
        "PrimaryButtonPressedBackgroundBrush", "PrimaryButtonBorderBrush",
        // ---- 玻璃高光（默认主题 = 透明） ----
        "GlassHighlightBrush", "WindowEdgeBorderBrush",
    };

    /// <summary>两套主题字典必须都提供的圆角 token。</summary>
    public static IReadOnlyList<string> RequiredRadiusKeys { get; } = new[]
    {
        "CardCornerRadius", "ControlCornerRadius",
    };

    /// <summary>主题资源字典的 pack URI（主程序内嵌资源）。</summary>
    public static Uri SourceFor(UiTheme theme)
    {
        var file = theme == UiTheme.LiquidGlass ? "LiquidGlassTheme.xaml" : "DefaultTheme.xaml";
        return new Uri($"/{AssemblyName};component/Views/Themes/{file}", UriKind.Relative);
    }

    /// <summary>主题的中文名（设置窗口 / 文档用）。</summary>
    public static string DisplayName(UiTheme theme)
        => theme == UiTheme.LiquidGlass ? "液态玻璃" : "默认";

    /// <summary>一句话说明（设置窗口里显示）。</summary>
    public static string Describe(UiTheme theme)
        => theme == UiTheme.LiquidGlass
            ? "半透明玻璃面板 + 流动色彩光晕，参考 Glassium 的 Liquid Glass 观感；换主题立即生效。"
            : "经典深色纯色界面，最省 GPU；换主题立即生效。";

    /// <summary>
    /// 解析 <c>YLT_THEME</c> / 配置文件里的主题名（宽松：大小写、<c>-</c>/<c>_</c>/空格都无所谓）。
    /// 认不出返回 <c>null</c>，调用方自己决定回退值（配置默认 = <see cref="UiTheme.Default"/>）。
    /// </summary>
    public static UiTheme? ParseThemeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = value.Trim()
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "default" or "0" or "经典" or "默认" or "深色" => UiTheme.Default,
            "liquidglass" or "liquid" or "glass" or "1" or "液态玻璃" or "玻璃" => UiTheme.LiquidGlass,
            _ => null,
        };
    }

    /// <summary>
    /// 加载一套主题字典（不安装；自检用它做 key 对比，<see cref="Apply"/> 也走这里）。
    /// 在无 <c>Application</c> 的进程里也能用（pack URI 带程序集名）。
    /// </summary>
    public static ResourceDictionary LoadThemeDictionary(UiTheme theme)
        => new() { Source = SourceFor(theme) };

    /// <summary>
    /// 把界面换成指定主题（幂等：已经是这个主题就直接返回 false）。
    /// 必须在 <c>Application.Current</c> 就绪之后调用（App.OnStartup / 主页设置窗口里的切换）。
    /// </summary>
    public static bool Apply(UiTheme theme, bool force = false)
    {
        if (!force && theme == Current) return false;

        var app = Application.Current;
        if (app is null)
        {
            // 无界面进程（自检）：只记状态，不动资源。
            Current = theme;
            return false;
        }

        var dictionary = LoadThemeDictionary(theme);
        var merged = app.Resources.MergedDictionaries;
        var index = FindThemeDictionaryIndex(merged);

        if (index >= 0) merged[index] = dictionary;
        else merged.Insert(0, dictionary);

        // 兜底：历史状态里如果混进了第二份主题字典（比如手工改过 App.xaml），把多余的删掉。
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (i == index || (index < 0 && i == 0)) continue;
            if (IsThemeDictionary(merged[i])) merged.RemoveAt(i);
        }

        Current = theme;
        Changed?.Invoke(null, theme);
        return true;
    }

    /// <summary>资源里是不是已经装着一份主题字典（给自检 / 排错用）。</summary>
    public static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString ?? "";
        return source.Contains("Views/Themes/", StringComparison.OrdinalIgnoreCase)
               && source.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindThemeDictionaryIndex(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (IsThemeDictionary(merged[i])) return i;
        }
        return -1;
    }
}
