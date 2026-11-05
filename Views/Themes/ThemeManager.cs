using System.Windows;

namespace YuLiangToolkit.App.Themes;

/// <summary>
/// 运行时主题切换器：把 <c>App.xaml</c> 里那个合并的资源字典整体换成
/// <c>Views/Themes/DefaultTheme.xaml</c> / <c>LiquidGlassTheme.xaml</c> /
/// <c>FrostedGlassTheme.xaml</c> / <c>AcrylicTheme.xaml</c>，并在需要时再叠一层
/// **浅色模式 / 配色方案 / 道系风格**的生成 token（见 <see cref="ThemePaletteBuilder"/>）。
///
/// <para><b>为什么换字典就能换皮肤</b>：App.xaml 里的样式只引用「主题 token」（颜色 / 圆角），
/// 所有引用都是 <c>{DynamicResource …}</c>；换掉合并字典后 WPF 会重新解析这些动态资源，
/// 已经打开的窗口（含「主页设置」窗口自己）立即变成另一套 UI，不用重启。</para>
///
/// <para><b>五套字典必须 key 完全一致</b>：缺 key 时 DynamicResource 会悄悄退回控件默认值
/// （例如文字变黑），自检 <c>ThemeChecks</c> 会逐项对比，见 <see cref="RequiredTokenKeys"/>。</para>
///
/// <para>本类同时提供几个**纯函数**（<see cref="SourceFor"/> / <see cref="DisplayName"/> /
/// <see cref="ParseThemeName"/> / <see cref="HasTranslucentSurface"/>），自检直接断言，不碰界面。</para>
/// </summary>
public static class ThemeManager
{
    /// <summary>主题资源字典所在的程序集名（主程序，见 YuLiangTookit.csproj）。</summary>
    private const string AssemblyName = "YuLiangTookit";

    /// <summary>当前生效的主题（<see cref="Apply"/> 之后更新）。</summary>
    public static UiTheme Current { get; private set; } = UiTheme.Default;

    /// <summary>当前实际生效的是不是浅色（<see cref="ThemeMode.System"/> 会先解析成深 / 浅）。</summary>
    public static bool CurrentIsLight { get; private set; }

    /// <summary>当前实际生效的深 / 浅（只有 <see cref="ThemeMode.Dark"/> / <see cref="ThemeMode.Light"/> 两种）。</summary>
    public static ThemeMode CurrentMode => CurrentIsLight ? ThemeMode.Light : ThemeMode.Dark;

    /// <summary>当前默认风格用的配色 Id（非默认主题时为空串）。</summary>
    public static string CurrentPaletteId { get; private set; } = ThemePalette.Default.Id;

    /// <summary>主题真的换掉之后触发（参数是新主题）；用于外壳级、没法绑定的收尾动作。</summary>
    public static event EventHandler<UiTheme>? Changed;

    /// <summary>
    /// 五套主题字典必须都提供的 token（顺序即报错顺序；新增颜色时记得同时：
    /// ① 四套深色字典各加一行；② 把这个名字加进这里 —— 否则自检的「字典 key 一致」检查发现不了；
    /// Daoji / 浅色 / 配色由 <see cref="ThemePaletteBuilder"/> 生成，新增 key 时要保证生成器也覆盖）。
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
        // ---- 背景层 ----
        "BackdropBaseBrush", "BackdropImageScrimBrush", "BackdropNoiseBrush",
        // ---- 背景层的顶部柔光 / 暗角 / 道系装饰（v0.89.0；GlassBackdrop 用它们而不是硬编码） ----
        "BackdropTopLightBrush", "BackdropVignetteBrush", "DaoSilhouetteBrush", "DaoGlowBrush",
    };


    /// <summary>四套深色主题字典必须都提供的圆角 token（Daoji / 浅色 / 配色沿用底字典的圆角或单独覆盖）。</summary>
    public static IReadOnlyList<string> RequiredRadiusKeys { get; } = new[]
    {
        "CardCornerRadius", "ControlCornerRadius",
    };

    /// <summary>主题资源字典的 pack URI（主程序内嵌资源）。道系没有独立字典：以默认深色字典为底，再生成金色 token。</summary>
    public static Uri SourceFor(UiTheme theme)
    {
        var file = theme switch
        {
            UiTheme.LiquidGlass => "LiquidGlassTheme.xaml",
            UiTheme.FrostedGlass => "FrostedGlassTheme.xaml",
            UiTheme.Acrylic => "AcrylicTheme.xaml",
            _ => "DefaultTheme.xaml",   // Default / Daoji（Daoji 在 Apply 里再叠生成 token + 圆角）
        };
        return new Uri($"/{AssemblyName};component/Views/Themes/{file}", UriKind.Relative);
    }

    /// <summary>主题的中文名（设置窗口 / 文档用）。</summary>
    public static string DisplayName(UiTheme theme)
        => theme switch
        {
            UiTheme.LiquidGlass => "液态玻璃",
            UiTheme.FrostedGlass => "毛玻璃",
            UiTheme.Acrylic => "亚克力",
            UiTheme.Daoji => "道系（墨金）",
            _ => "默认",
        };

    /// <summary>一句话说明（设置窗口里显示）。</summary>
    public static string Describe(UiTheme theme)
        => theme switch
        {
            UiTheme.LiquidGlass =>
                "半透明玻璃面板 + 流动色彩光晕，参考 Glassium 的 Liquid Glass 观感；换主题立即生效。",
            UiTheme.FrostedGlass =>
                "奶白磨砂玻璃 + 重模糊背景，安静不花哨、不跑动画；换主题立即生效。",
            UiTheme.Acrylic =>
                "深色亚克力 + 轻微着色与颗粒噪点，类似 Win11 亚克力；换主题立即生效。",
            UiTheme.Daoji =>
                "玄墨底 + 香槟金、远山剪影与金色光晕，参考「道阶表」的暗金风格；支持深 / 浅色。",
            _ => "经典纯色界面，最省 GPU；深 / 浅色跟随上面的「外观模式」。",
        };

    /// <summary>
    /// 这套主题是不是**半透明界面**（能透出后面的背景图 / 光晕）。
    /// 默认主题 / 道系是纯色面板 → false；液态玻璃 / 毛玻璃 / 亚克力 → true。
    /// </summary>
    public static bool HasTranslucentSurface(UiTheme theme)
        => theme is UiTheme.LiquidGlass or UiTheme.FrostedGlass or UiTheme.Acrylic;

    /// <summary>
    /// 这套主题要不要显示窗口最底层的背景层（背景图 / 装饰）。
    /// 液态玻璃 / 毛玻璃 / 亚克力 + 道系要；默认纯色不要。
    /// </summary>
    public static bool UsesBackdrop(UiTheme theme) => theme != UiTheme.Default;

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
            "frostedglass" or "frosted" or "2" or "毛玻璃" or "磨砂" or "磨砂玻璃" => UiTheme.FrostedGlass,
            "acrylic" or "3" or "亚克力" => UiTheme.Acrylic,
            "daoji" or "dao" or "4" or "道系" or "墨金" or "道阶表" => UiTheme.Daoji,
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
    /// 把界面换成指定主题（幂等：主题 / 深浅 / 配色都没变就直接返回 false）。
    /// 必须在 <c>Application.Current</c> 就绪之后调用（App.OnStartup / 主页设置窗口里的切换）。
    /// </summary>
    /// <param name="theme">界面风格（默认 / 液态玻璃 / 毛玻璃 / 亚克力 / 道系）。</param>
    /// <param name="mode">外观模式；<see cref="ThemeMode.System"/> 会按 Windows 当前设置解析。</param>
    /// <param name="palette">默认风格的配色方案（其它风格忽略；null = 默认深蓝）。</param>
    /// <param name="force">true = 即使 key 看起来没变也重建（自定义颜色改了同一个 paletteId 时需要）。</param>
    public static bool Apply(UiTheme theme, ThemeMode mode = ThemeMode.Dark, ThemePalette? palette = null,
        bool force = false)
    {
        var light = ResolveMode(mode) == ThemeMode.Light;
        var paletteId = theme == UiTheme.Default ? palette?.Id ?? ThemePalette.Default.Id : "";
        if (!force && theme == Current && light == CurrentIsLight
            && string.Equals(paletteId, CurrentPaletteId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var app = Application.Current;
        if (app is null)
        {
            // 无界面进程（自检）：只记状态，不动资源。
            Current = theme;
            CurrentIsLight = light;
            CurrentPaletteId = paletteId;
            return false;
        }

        var dictionary = LoadThemeDictionary(theme);

        // 1) 道系：以默认深色字典为底，叠生成的金色 token + 更方的圆角（没有独立 XAML，少维护一份 50+ key 的字典）
        if (theme == UiTheme.Daoji)
        {
            ThemePaletteBuilder.Apply(dictionary, ThemePaletteBuilder.BuildDaoji(light));
            dictionary["CardCornerRadius"] = new CornerRadius(6);
            dictionary["ControlCornerRadius"] = new CornerRadius(4);
        }
        // 2) 默认 + 用户配色：整套 token 由配色生成（深 / 浅各一套）
        else if (theme == UiTheme.Default && palette is { IsDefault: false })
        {
            ThemePaletteBuilder.Apply(dictionary, ThemePaletteBuilder.BuildPalette(palette, light));
        }
        // 3) 浅色模式：默认 / 三种玻璃都在这里换成浅色 token（玻璃的圆角 / 光晕保留）
        else if (light)
        {
            ThemePaletteBuilder.Apply(dictionary,
                theme == UiTheme.Default
                    ? ThemePaletteBuilder.BuildDefaultLight()
                    : ThemePaletteBuilder.BuildLightMode(theme));
        }

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
        CurrentIsLight = light;
        CurrentPaletteId = paletteId;
        Changed?.Invoke(null, theme);
        return true;
    }

    /// <summary>把外观模式解析成实际深 / 浅（<see cref="ThemeMode.System"/> 读 Windows 应用模式）。</summary>
    public static ThemeMode ResolveMode(ThemeMode mode) => SystemThemeMode.Resolve(mode);

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
