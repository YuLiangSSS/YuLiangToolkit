using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.App.Themes;

/// <summary>
/// 「默认」风格的配色方案：一套配色给出深色 / 浅色两套种子色，运行时由
/// <see cref="ThemePaletteBuilder"/> 生成完整 token 集。
///
/// <para>只有 <see cref="UiTheme.Default"/> 用这套配色；液态玻璃 / 毛玻璃 / 亚克力有自己的固定配色，
/// 道系（墨金）是独立风格（不走这里）。用户点「自定义」时用 <see cref="CreateCustom"/> 现场生成。</para>
/// </summary>
public sealed class ThemePalette
{
    /// <summary>自定义配色的固定 Id。</summary>
    public const string CustomId = "custom";

    public ThemePalette(
        string id,
        string name,
        string description,
        Color accentDark,
        Color accentLight,
        Color darkBackground,
        Color darkCard,
        Color lightBackground,
        Color lightCard,
        bool isDefault = false,
        bool isCustom = false)
    {
        Id = id;
        Name = name;
        Description = description;
        AccentDark = accentDark;
        AccentLight = accentLight;
        DarkBackground = darkBackground;
        DarkCard = darkCard;
        LightBackground = lightBackground;
        LightCard = lightCard;
        IsDefault = isDefault;
        IsCustom = isCustom;
    }

    /// <summary>稳定 Id（存 <c>Config\shell\config.json</c> 的 <c>paletteId</c>）。</summary>
    public string Id { get; }

    /// <summary>设置页显示名。</summary>
    public string Name { get; }

    /// <summary>设置页一句话说明。</summary>
    public string Description { get; }

    /// <summary>深色模式主色。</summary>
    public Color AccentDark { get; }

    /// <summary>浅色模式主色（一般比深色模式更深一点，保证浅底上看得清）。</summary>
    public Color AccentLight { get; }

    /// <summary>深色模式底色。</summary>
    public Color DarkBackground { get; }

    /// <summary>深色模式卡片色。</summary>
    public Color DarkCard { get; }

    /// <summary>浅色模式底色。</summary>
    public Color LightBackground { get; }

    /// <summary>浅色模式卡片色。</summary>
    public Color LightCard { get; }

    /// <summary>是不是「默认深蓝」（不生成覆盖，直接用 DefaultTheme / 浅色生成器）。</summary>
    public bool IsDefault { get; }

    /// <summary>是不是用户自定义配色（设置页展开三个拾色器）。</summary>
    public bool IsCustom { get; }

    /// <summary>按模式取主色。</summary>
    public Color AccentFor(bool light) => light ? AccentLight : AccentDark;

    /// <summary>按模式取底色。</summary>
    public Color BackgroundFor(bool light) => light ? LightBackground : DarkBackground;

    /// <summary>按模式取卡片色。</summary>
    public Color CardFor(bool light) => light ? LightCard : DarkCard;

    /// <summary>默认深蓝：深色部分 = 原来第一套 UI 的种子色；浅色部分 = 新的浅蓝灰。</summary>
    public static ThemePalette Default { get; } = new(
        "default", "深蓝（默认）", "经典深蓝；浅色模式自动配一套浅蓝灰。",
        Hex("#4C8DFF"), Hex("#2F6BD8"),
        Hex("#16181D"), Hex("#1F242C"),
        Hex("#F5F7FB"), Hex("#FFFFFF"),
        isDefault: true);

    /// <summary>几套给默认风格用的预设配色（AI 按最近流行色系挑的，深 / 浅色各一套）。</summary>
    public static IReadOnlyList<ThemePalette> Presets { get; } = new[]
    {
        Default,
        new ThemePalette("teal", "青碧", "青绿主色 + 冷调深底，安静清爽。",
            Hex("#3DD6C4"), Hex("#0E8C80"),
            Hex("#0D1514"), Hex("#15201E"),
            Hex("#F2F8F7"), Hex("#FFFFFF")),
        new ThemePalette("indigo", "靛紫", "偏蓝紫的冷调，适合夜间使用。",
            Hex("#9B8CFF"), Hex("#5B43D6"),
            Hex("#100E1A"), Hex("#191627"),
            Hex("#F6F4FC"), Hex("#FFFFFF")),
        new ThemePalette("crimson", "赤金", "赤红主色 + 暖调深底，和墨金道系是两种暖色。",
            Hex("#E07A5F"), Hex("#B5482F"),
            Hex("#17100F"), Hex("#241816"),
            Hex("#FBF3F2"), Hex("#FFFFFF")),
        new ThemePalette("forest", "森绿", "森林绿 + 深墨底，沉稳不刺眼。",
            Hex("#78D08A"), Hex("#2E7D45"),
            Hex("#0D1710"), Hex("#15261A"),
            Hex("#F2F8F3"), Hex("#FFFFFF")),
        new ThemePalette("sakura", "樱粉", "粉色主色 + 深玫瑰底，浅色模式很轻。",
            Hex("#FF9CC8"), Hex("#C24779"),
            Hex("#171017"), Hex("#241823"),
            Hex("#FCF4F8"), Hex("#FFFFFF")),
        new ThemePalette("graphite", "石墨", "接近中性的灰蓝，低调百搭。",
            Hex("#9AA7BD"), Hex("#45566E"),
            Hex("#121417"), Hex("#1B1F26"),
            Hex("#F4F6F9"), Hex("#FFFFFF")),
    };

    /// <summary>按 Id 找预设；认不出返回 null（调用方回退默认）。</summary>
    public static ThemePalette? Find(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : Presets.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 根据用户选的颜色现场生成一套自定义配色：
    /// 主色会自动按底色校正对比度；卡片色由底色往白色 / 亮部推，保证深浅两种模式都能看。
    /// </summary>
    public static ThemePalette CreateCustom(Color accent, Color darkBackground, Color lightBackground)
    {
        var darkCard = ThemePaletteBuilder.Mix(darkBackground, Colors.White, 0.06);
        var lightCard = ThemePaletteBuilder.Mix(lightBackground, Colors.White, 0.72);
        return new ThemePalette(
            CustomId, "自定义", "自己挑主色 + 深 / 浅底色（其余颜色自动配）。",
            ThemePaletteBuilder.EnsureContrast(accent, darkBackground, 3.0),
            ThemePaletteBuilder.EnsureContrast(accent, lightBackground, 3.0),
            darkBackground, darkCard,
            lightBackground, lightCard,
            isCustom: true);
    }

    private static Color Hex(string value) => ThemePaletteBuilder.FromHex(value, Colors.Transparent);
}

/// <summary>
/// 把配色方案 / 浅色模式 / 道系风格生成成完整的 <see cref="ThemeManager.RequiredTokenKeys"/> 画刷集合，
/// 再覆盖到运行时主题字典上。**所有颜色都在这里算**，主题 XAML 只保留四套深色原始外观。
/// </summary>
public static class ThemePaletteBuilder
{
    /// <summary>把一套 token 覆盖到已加载的主题字典里（只覆盖颜色，不动圆角）。</summary>
    public static void Apply(ResourceDictionary target, IReadOnlyDictionary<string, Brush> tokens)
    {
        foreach (var pair in tokens) target[pair.Key] = pair.Value;
    }

    /// <summary>默认主题的浅色版（不选配色时用）。</summary>
    public static Dictionary<string, Brush> BuildDefaultLight() => BuildSolid(ThemePalette.Default, light: true);

    /// <summary>默认主题 + 指定配色（深 / 浅都行）。</summary>
    public static Dictionary<string, Brush> BuildPalette(ThemePalette palette, bool light)
        => BuildSolid(palette, light);

    /// <summary>液态玻璃 / 毛玻璃 / 亚克力的浅色版（半透明白面板 + 深色文字）。</summary>
    public static Dictionary<string, Brush> BuildLightMode(UiTheme theme) => BuildGlassLight(theme);

    /// <summary>道系（墨金）的深 / 浅两套 token。</summary>
    public static Dictionary<string, Brush> BuildDaoji(bool light) => BuildDao(light);

    // ================================================================== 色彩工具

    /// <summary>解析 <c>#RRGGBB</c> / <c>#AARRGGBB</c>（也接受不带 #、大小写混写）；坏值回退。</summary>
    public static Color FromHex(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            return (Color)ColorConverter.ConvertFromString(text)!;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>颜色 → <c>#RRGGBB</c>（透明度丢弃；配置里的自定义颜色都按不透明处理）。</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>两色线性混合（<paramref name="amount"/> = 0 → a，1 → b）。</summary>
    public static Color Mix(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Ch(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return Color.FromArgb(Ch(a.A, b.A), Ch(a.R, b.R), Ch(a.G, b.G), Ch(a.B, b.B));
    }

    /// <summary>给颜色换透明度。</summary>
    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>相对亮度（WCAG 近似，用于选文字颜色 / 对比度校正）。</summary>
    public static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>WCAG 对比度（1 ~ 21）。</summary>
    public static double Contrast(Color a, Color b)
    {
        var l1 = Luminance(a);
        var l2 = Luminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    /// <summary>在背景上选一个能读的文字色（亮的背景用深墨，暗的背景用亮白）。</summary>
    public static Color ContrastText(Color background)
        => Luminance(background) > 0.45 ? Hex("#1A1F28") : Hex("#EEF1F7");

    /// <summary>把一个前景色往黑 / 白混，直到和背景达到目标对比度（达不到就返回最接近的）。</summary>
    public static Color EnsureContrast(Color foreground, Color background, double target)
    {
        if (Contrast(foreground, background) >= target) return foreground;
        var toWhite = Contrast(Colors.White, background) >= Contrast(Colors.Black, background);
        var adjusted = foreground;
        for (var i = 0; i < 20; i++)
        {
            adjusted = Mix(adjusted, toWhite ? Colors.White : Colors.Black, 0.12);
            if (Contrast(adjusted, background) >= target) return adjusted;
        }
        return adjusted;
    }

    /// <summary>主按钮上永远是白字：把主色压到和白色有足够对比度（大概是深一档的强调色）。</summary>
    public static Color EnsureWhiteReadable(Color accent) => EnsureContrast(accent, Colors.White, 4.0);

    // ================================================================== 默认风格配色

    private static Dictionary<string, Brush> BuildSolid(ThemePalette palette, bool light)
    {
        var background = palette.BackgroundFor(light);
        var card = palette.CardFor(light);
        var accent = EnsureContrast(palette.AccentFor(light), background, 2.8);
        var text = ContrastText(background);
        var tokens = BaseTokens(background, card, accent, text, light, glass: false);

        // 纯色界面没有玻璃高光 / 外圈亮边 / 背景图，占位 token 全透明或纯底色
        tokens["HeaderBackgroundBrush"] = Solid(background);
        tokens["SidebarBackgroundBrush"] = Solid(Mix(background, card, 0.45));
        tokens["SecondaryButtonBackgroundBrush"] = Solid(Colors.Transparent);
        tokens["PrimaryButtonBackgroundBrush"] = Solid(EnsureWhiteReadable(accent));
        tokens["PrimaryButtonHoverBackgroundBrush"] = Solid(Lighten(EnsureWhiteReadable(accent), 0.12));
        tokens["PrimaryButtonPressedBackgroundBrush"] = Solid(Darken(EnsureWhiteReadable(accent), 0.12));
        tokens["PrimaryButtonBorderBrush"] = Solid(Colors.Transparent);
        tokens["GlassHighlightBrush"] = Solid(Colors.Transparent);
        tokens["WindowEdgeBorderBrush"] = Solid(Colors.Transparent);
        tokens["BackdropBaseBrush"] = Solid(background);
        tokens["BackdropImageScrimBrush"] = Solid(Colors.Transparent);
        tokens["BackdropNoiseBrush"] = Solid(Colors.Transparent);
        tokens["BackdropTopLightBrush"] = Solid(Colors.Transparent);
        tokens["BackdropVignetteBrush"] = Solid(Colors.Transparent);
        tokens["DaoSilhouetteBrush"] = Solid(Colors.Transparent);
        tokens["DaoGlowBrush"] = Solid(Colors.Transparent);
        return tokens;
    }

    /// <summary>两种内容色（背景 / 卡片）+ 主色 → 一整套通用 token（深浅通用）。</summary>
    private static Dictionary<string, Brush> BaseTokens(
        Color background, Color card, Color accent, Color text, bool light, bool glass)
    {
        var accentHover = light ? Lighten(accent, 0.12) : Lighten(accent, 0.16);
        var accentPressed = light ? Darken(accent, 0.12) : Darken(accent, 0.14);
        var secondary = Mix(text, background, 0.55);
        var faint = Mix(text, background, 0.30);
        var border = Mix(text, background, light ? 0.22 : 0.18);
        var input = glass ? Solid(WithAlpha(Colors.White, 0xB3)) : Solid(Mix(background, card, 0.35));
        var hover = light ? Darken(card, 0.04) : Lighten(card, 0.07);
        var danger = light ? Hex("#B93B32") : Hex("#E5534B");
        var success = light ? Hex("#2E7D45") : Hex("#3FB950");

        var tokens = new Dictionary<string, Brush>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = Solid(background),
            ["HeaderBackgroundBrush"] = Solid(glass ? WithAlpha(Colors.White, 0xE8) : background),
            ["SidebarBackgroundBrush"] = Solid(glass ? WithAlpha(Colors.White, 0xC4) : Mix(background, card, 0.45)),
            ["CardBackgroundBrush"] = glass ? Solid(WithAlpha(Colors.White, 0xE8)) : Solid(card),
            ["CardHoverBackgroundBrush"] = glass ? Solid(WithAlpha(Colors.White, 0xF4)) : Solid(hover),
            ["BorderBrush"] = Solid(glass ? WithAlpha(Colors.Black, 0x33) : border),
            ["SeparatorBrush"] = Solid(glass ? WithAlpha(Colors.Black, 0x26) : border),
            ["AccentBrush"] = Solid(accent),
            ["AccentHoverBrush"] = Solid(accentHover),
            ["AccentPressedBrush"] = Solid(accentPressed),
            ["TextPrimaryBrush"] = Solid(glass ? Hex("#172033") : text),
            ["TextSecondaryBrush"] = Solid(glass ? Hex("#4E5B70") : secondary),
            ["FaintTextBrush"] = Solid(glass ? Hex("#7A8698") : faint),
            ["DangerBrush"] = Solid(danger),
            ["DangerPressedBrush"] = Solid(Darken(danger, 0.12)),
            ["SuccessBrush"] = Solid(success),
            ["InputBackgroundBrush"] = input,

            ["ControlBorderHoverBrush"] = Solid(Mix(glass ? WithAlpha(Colors.Black, 0x33) : border, accent, 0.45)),
            ["WindowButtonHoverBackgroundBrush"] = Solid(Mix(background, text, 0.10)),
            ["WindowButtonPressedBackgroundBrush"] = Solid(Mix(background, text, 0.16)),
            ["CheckBoxBorderBrush"] = Solid(Mix(glass ? WithAlpha(Colors.Black, 0x44) : border, text, 0.25)),
            ["ScrollBarTrackBrush"] = Solid(Mix(background, text, 0.06)),
            ["ScrollBarThumbBrush"] = Solid(Mix(background, text, 0.20)),
            ["ScrollBarThumbHoverBrush"] = Solid(Mix(background, text, 0.32)),
            ["SidebarItemHoverBackgroundBrush"] = Solid(Mix(background, accent, 0.10)),
            ["SidebarItemSelectedBackgroundBrush"] = Solid(Mix(background, accent, 0.18)),
            ["ListRowHoverBackgroundBrush"] = Solid(Mix(card, accent, 0.08)),
            ["ListRowSelectedBackgroundBrush"] = Solid(Mix(card, accent, 0.16)),
            ["ListRowSelectedBorderBrush"] = Solid(Mix(accent, background, 0.30)),
            ["TagChipBackgroundBrush"] = Solid(Mix(card, accent, 0.12)),
            ["TagChipBorderBrush"] = Solid(Mix(border, accent, 0.45)),
            ["TagChipHoverBackgroundBrush"] = Solid(Mix(card, accent, 0.20)),
            ["EmojiTileBackgroundBrush"] = Solid(hover),
            ["SliderThumbBackgroundBrush"] = Solid(glass ? Hex("#4A5568") : text),
            ["SliderThumbBorderBrush"] = Solid(glass ? WithAlpha(Colors.White, 0xCC) : background),
            ["MiniPlayerBackgroundBrush"] = Solid(glass ? WithAlpha(Colors.White, 0xF2) : card),
            ["MiniPlayerHoverBackgroundBrush"] = Solid(glass ? WithAlpha(Colors.Black, 0x12) : hover),
            ["ShellSurfaceBrush"] = Solid(background),
            ["SecondaryButtonBackgroundBrush"] = Solid(glass ? WithAlpha(Colors.Black, 0x0F) : Colors.Transparent),
            ["PrimaryButtonBackgroundBrush"] = Solid(EnsureWhiteReadable(accent)),
            ["PrimaryButtonHoverBackgroundBrush"] = Solid(Lighten(EnsureWhiteReadable(accent), 0.12)),
            ["PrimaryButtonPressedBackgroundBrush"] = Solid(Darken(EnsureWhiteReadable(accent), 0.12)),
            ["PrimaryButtonBorderBrush"] = Solid(Colors.Transparent),
            ["GlassHighlightBrush"] = Solid(glass ? WithAlpha(Colors.White, 0x33) : Colors.Transparent),
            ["WindowEdgeBorderBrush"] = Solid(glass ? WithAlpha(Colors.Black, 0x33) : Colors.Transparent),
            ["BackdropBaseBrush"] = Solid(background),
            ["BackdropImageScrimBrush"] = Solid(Colors.Transparent),
            ["BackdropNoiseBrush"] = Solid(Colors.Transparent),
            ["BackdropTopLightBrush"] = Solid(Colors.Transparent),
            ["BackdropVignetteBrush"] = Solid(Colors.Transparent),
            ["DaoSilhouetteBrush"] = Solid(Colors.Transparent),
            ["DaoGlowBrush"] = Solid(Colors.Transparent),
        };
        return tokens;
    }

    // ================================================================== 玻璃三套的浅色版

    private static Dictionary<string, Brush> BuildGlassLight(UiTheme theme)
    {
        var (background, accent, baseFrom, baseTo) = theme switch
        {
            UiTheme.LiquidGlass => (Hex("#EDF3FF"), Hex("#2F6FE0"), Hex("#E8F0FF"), Hex("#F7EDFF")),
            UiTheme.FrostedGlass => (Hex("#EEF3F9"), Hex("#3B6E9C"), Hex("#EDF3FA"), Hex("#E1EAF5")),
            _ => (Hex("#F2F0F8"), Hex("#6B4FC8"), Hex("#F3F1F8"), Hex("#E5E0F2")),
        };

        var tokens = BaseTokens(background, Colors.White, accent, Hex("#1A1F28"), light: true, glass: true);
        tokens["WindowBackgroundBrush"] = Solid(background);
        tokens["HeaderBackgroundBrush"] = Solid(WithAlpha(Colors.White, 0xE8));
        tokens["SidebarBackgroundBrush"] = Solid(WithAlpha(Colors.White, 0xC4));
        tokens["CardBackgroundBrush"] = Solid(WithAlpha(Colors.White, 0xE8));
        tokens["CardHoverBackgroundBrush"] = Solid(WithAlpha(Colors.White, 0xF4));
        tokens["InputBackgroundBrush"] = Solid(WithAlpha(Colors.White, 0xB3));
        tokens["BorderBrush"] = Solid(WithAlpha(Colors.Black, 0x33));
        tokens["SeparatorBrush"] = Solid(WithAlpha(Colors.Black, 0x26));
        tokens["GlassHighlightBrush"] = Solid(WithAlpha(Colors.White, 0x33));
        tokens["WindowEdgeBorderBrush"] = Solid(WithAlpha(Colors.Black, 0x33));
        tokens["BackdropBaseBrush"] = Linear(baseFrom, baseTo);
        tokens["BackdropImageScrimBrush"] = Solid(WithAlpha(Colors.White, 0xB8));
        tokens["BackdropNoiseBrush"] = Solid(Colors.Transparent);
        tokens["BackdropTopLightBrush"] = Solid(Colors.Transparent);
        tokens["BackdropVignetteBrush"] = Solid(Colors.Transparent);
        tokens["DaoSilhouetteBrush"] = Solid(Colors.Transparent);
        tokens["DaoGlowBrush"] = Solid(Colors.Transparent);
        return tokens;
    }

    // ================================================================== 道系（墨金）

    private static Dictionary<string, Brush> BuildDao(bool light)
    {
        var background = light ? Hex("#F4F1EA") : Hex("#0C0A07");
        var card = light ? Hex("#F9FFFFFF") : Hex("#F017130D");
        var accent = light ? Hex("#8A6A2F") : Hex("#D6B26A");
        var text = light ? Hex("#241F17") : Hex("#F0E7D6");
        var tokens = BaseTokens(background, card, accent, text, light, glass: false);

        // 道系：细金边、方一点的卡片、暖色悬停；主按钮用更深的铜金配白字
        tokens["HeaderBackgroundBrush"] = Solid(light ? WithAlpha(Colors.White, 0xE8) : Hex("#12100B"));
        tokens["SidebarBackgroundBrush"] = Solid(light ? WithAlpha(Colors.White, 0xCC) : Hex("#0F0D09"));
        tokens["CardBackgroundBrush"] = Solid(card);
        tokens["CardHoverBackgroundBrush"] = Solid(light ? Hex("#FFFFFFFF") : Hex("#F21C1710"));
        tokens["BorderBrush"] = Solid(light ? Hex("#D8CDB8") : Hex("#3B3121"));
        tokens["SeparatorBrush"] = Solid(light ? Hex("#E4DBC9") : Hex("#2B251A"));
        tokens["ControlBorderHoverBrush"] = Solid(light ? Hex("#A88746") : Hex("#6A5733"));
        tokens["TextPrimaryBrush"] = Solid(text);
        tokens["TextSecondaryBrush"] = Solid(light ? Hex("#6E6452") : Hex("#B7A98E"));
        tokens["FaintTextBrush"] = Solid(light ? Hex("#9A8F7C") : Hex("#7C715C"));
        tokens["InputBackgroundBrush"] = Solid(light ? Hex("#FBF9F4") : Hex("#100D09"));
        tokens["WindowButtonHoverBackgroundBrush"] = Solid(light ? Hex("#14000000") : Hex("#1FD6B26A"));
        tokens["WindowButtonPressedBackgroundBrush"] = Solid(light ? Hex("#22000000") : Hex("#33D6B26A"));
        tokens["ScrollBarTrackBrush"] = Solid(light ? Hex("#11000000") : Hex("#14D6B26A"));
        tokens["ScrollBarThumbBrush"] = Solid(light ? Hex("#33000000") : Hex("#3DD6B26A"));
        tokens["ScrollBarThumbHoverBrush"] = Solid(light ? Hex("#55000000") : Hex("#59D6B26A"));
        tokens["SidebarItemHoverBackgroundBrush"] = Solid(light ? Hex("#14A88746") : Hex("#1FD6B26A"));
        tokens["SidebarItemSelectedBackgroundBrush"] = Solid(light ? Hex("#24A88746") : Hex("#33D6B26A"));
        tokens["ListRowHoverBackgroundBrush"] = Solid(light ? Hex("#10A88746") : Hex("#1FD6B26A"));
        tokens["ListRowSelectedBackgroundBrush"] = Solid(light ? Hex("#22A88746") : Hex("#2ED6B26A"));
        tokens["ListRowSelectedBorderBrush"] = Solid(light ? Hex("#88A88746") : Hex("#88D6B26A"));
        tokens["TagChipBackgroundBrush"] = Solid(light ? Hex("#14A88746") : Hex("#1FD6B26A"));
        tokens["TagChipBorderBrush"] = Solid(light ? Hex("#66A88746") : Hex("#66D6B26A"));
        tokens["TagChipHoverBackgroundBrush"] = Solid(light ? Hex("#22A88746") : Hex("#33D6B26A"));
        tokens["EmojiTileBackgroundBrush"] = Solid(light ? Hex("#F0E9DC") : Hex("#211B12"));
        tokens["SliderThumbBackgroundBrush"] = Solid(light ? Hex("#8A6A2F") : Hex("#D6B26A"));
        tokens["SliderThumbBorderBrush"] = Solid(light ? Hex("#FFFFFF") : Hex("#0C0A07"));
        tokens["MiniPlayerBackgroundBrush"] = Solid(light ? Hex("#F7F2E8") : Hex("#17130D"));
        tokens["MiniPlayerHoverBackgroundBrush"] = Solid(light ? Hex("#14000000") : Hex("#22D6B26A"));
        tokens["SecondaryButtonBackgroundBrush"] = Solid(Colors.Transparent);
        var primary = EnsureWhiteReadable(accent);
        tokens["PrimaryButtonBackgroundBrush"] = Solid(primary);
        tokens["PrimaryButtonHoverBackgroundBrush"] = Solid(Lighten(primary, 0.12));
        tokens["PrimaryButtonPressedBackgroundBrush"] = Solid(Darken(primary, 0.12));
        tokens["PrimaryButtonBorderBrush"] = Solid(Colors.Transparent);
        tokens["GlassHighlightBrush"] = Solid(Colors.Transparent);
        tokens["WindowEdgeBorderBrush"] = Solid(light ? Hex("#33A88746") : Hex("#33D6B26A"));

        // 背景层：暖色渐变 + 金色光晕 + 远山剪影（GlassBackdrop 的 DaoLayer 用这两个 token）
        tokens["BackdropBaseBrush"] = Linear(
            light ? Hex("#F8F5EE") : Hex("#070604"),
            light ? Hex("#EDF2F8") : Hex("#120E08"));
        tokens["BackdropImageScrimBrush"] = Solid(light ? WithAlpha(Colors.White, 0xB8) : WithAlpha(Colors.Black, 0x55));
        tokens["BackdropNoiseBrush"] = Solid(Colors.Transparent);
        var gold = Hex("#D6B26A");
        tokens["BackdropTopLightBrush"] = Linear(
            light ? Color.FromArgb(0, 0, 0, 0) : WithAlpha(gold, 0x0A),
            WithAlpha(gold, 0x00));
        tokens["BackdropVignetteBrush"] = light
            ? Solid(Colors.Transparent)
            : Radial(Color.FromArgb(0, 0, 0, 0), WithAlpha(Colors.Black, 0xAA));
        tokens["DaoSilhouetteBrush"] = Solid(light ? Hex("#147F93A8") : Hex("#1C2E2A1F"));
        tokens["DaoGlowBrush"] = Radial(
            light ? WithAlpha(Hex("#C9A227"), 0x1A) : WithAlpha(gold, 0x18),
            WithAlpha(gold, 0x00));
        return tokens;
    }

    // ================================================================== 画刷工厂

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Linear(Color from, Color to)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
        };
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush Radial(Color center, Color edge)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.7,
            RadiusY = 0.7,
            GradientStops = { new GradientStop(center, 0), new GradientStop(edge, 1) },
        };
        brush.Freeze();
        return brush;
    }

    private static Color Lighten(Color color, double amount) => Mix(color, Colors.White, amount);

    private static Color Darken(Color color, double amount) => Mix(color, Colors.Black, amount);

    private static Color Hex(string value) => FromHex(value, Colors.Transparent);
}
