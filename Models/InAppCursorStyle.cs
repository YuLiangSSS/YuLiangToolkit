using YuLiangToolkit.App.Themes;

namespace YuLiangToolkit.App.Models;

/// <summary>应用内光标实际画哪一套（<c>auto</c> 会先按当前界面主题解析成这里的某一项）。</summary>
public enum InAppCursorKind
{
    /// <summary>经典箭头：深色界面白色箭头 + 深色描边；浅色界面深色箭头 + 浅色描边。</summary>
    Default,

    /// <summary>液态玻璃：半透明玻璃箭头 + 蓝色描边 + 柔光；浅色界面换成深色玻璃。</summary>
    LiquidGlass,

    /// <summary>毛玻璃：奶白磨砂 + 灰蓝描边；浅色界面换成深灰磨砂。</summary>
    FrostedGlass,

    /// <summary>亚克力：深紫 + 淡紫描边；浅色界面颜色再压深一档。</summary>
    Acrylic,

    /// <summary>道系：墨底 + 香槟金描边 / 金点；浅色界面用墨金深色版。</summary>
    Daoji,

    /// <summary>云间天上：白色 + 暖金描边 + 小太阳点；浅色界面换成夜空蓝 + 金边。</summary>
    CloudSky,

    /// <summary>用户上传的图片。</summary>
    Custom,
}

/// <summary>
/// 应用内光标样式清单 / 解析（v0.91.0）：配置只存稳定字符串 Id，<c>auto</c> 跟随当前 <see cref="UiTheme"/>。
/// <para>设置页的下拉框由 <see cref="Definitions"/> / <see cref="ToneDefinitions"/> 生成；渲染层用 <see cref="Resolve"/> 得到实际
/// <see cref="InAppCursorKind"/>，再用 <see cref="Palette"/> 取配色。深浅（v0.97.0 起按界面自动切深色系；v0.98.0 起
/// 设置页的「光标深浅」可以独立强制 <c>light</c> / <c>dark</c>，解析见 <see cref="UseDarkTone"/>）。</para>
/// </summary>
public static class InAppCursorStyle
{
    public const string Auto = "auto";
    public const string Default = "default";
    public const string LiquidGlass = "liquid";
    public const string FrostedGlass = "frosted";
    public const string Acrylic = "acrylic";
    public const string Daoji = "dao";
    public const string CloudSky = "cloud";
    public const string Custom = "custom";

    /// <summary>光标深浅：跟随界面外观模式（默认）。</summary>
    public const string ToneAuto = "auto";

    /// <summary>光标深浅：始终用浅色系（浅色填充 + 深色描边）。</summary>
    public const string ToneLight = "light";

    /// <summary>光标深浅：始终用深色系（深色填充 + 浅色描边）。</summary>
    public const string ToneDark = "dark";

    /// <summary>设置页「光标样式」下拉框里的全部选项（顺序即显示顺序）。</summary>
    public static IReadOnlyList<CursorStyleDefinition> Definitions { get; } = new[]
    {
        new CursorStyleDefinition(Auto, "跟随界面主题", "默认风格用经典箭头；玻璃 / 道系 / 云间天上各用自己的一套，深 / 浅色自动跟外观模式。"),
        new CursorStyleDefinition(Default, "经典箭头", "深色界面白色箭头 + 深色描边；浅色界面换成深色箭头 + 浅色描边，最百搭。"),
        new CursorStyleDefinition(LiquidGlass, "液态玻璃", "半透明玻璃箭头 + 蓝色描边 + 柔光；浅色界面换成深色玻璃。"),
        new CursorStyleDefinition(FrostedGlass, "毛玻璃", "奶白磨砂箭头 + 灰蓝描边；浅色界面换成深灰磨砂。"),
        new CursorStyleDefinition(Acrylic, "亚克力", "深紫底 + 淡紫描边，类似 Win11 亚克力；浅色界面颜色再压深一档。"),
        new CursorStyleDefinition(Daoji, "道系（墨金）", "墨底 + 香槟金描边 / 金点；浅色界面用墨金深色版。"),
        new CursorStyleDefinition(CloudSky, "云间天上", "白色箭头 + 暖金描边 + 小太阳点；浅色界面换成夜空蓝 + 金边。"),
        new CursorStyleDefinition(Custom, "自定义图片", "用你上传的小图片作为光标；可在下面选择历史图片。"),
    };

    /// <summary>设置页「光标深浅」下拉框里的全部选项（顺序即显示顺序）。</summary>
    public static IReadOnlyList<CursorToneDefinition> ToneDefinitions { get; } = new[]
    {
        new CursorToneDefinition(ToneAuto, "跟随界面主题", "默认：浅色界面用深色系（深色箭头 / 深色玻璃 / 墨金底），深色界面用浅色系，跟着外观模式自动切。"),
        new CursorToneDefinition(ToneLight, "浅色系", "始终用浅色填充 + 深色描边，深色背景上更清楚。"),
        new CursorToneDefinition(ToneDark, "深色系", "始终用深色填充 + 浅色描边，浅色 / 亮背景上更清楚。"),
    };

    /// <summary>把配置里的字符串归一化；不认识的值回退到 <c>auto</c>。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Auto;
        var id = value.Trim().ToLowerInvariant();
        foreach (var definition in Definitions)
            if (string.Equals(definition.Id, id, StringComparison.Ordinal)) return definition.Id;
        return Auto;
    }

    /// <summary>把光标深浅配置归一化；不认识的值回退到 <c>auto</c>。</summary>
    public static string NormalizeTone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ToneAuto;
        var id = value.Trim().ToLowerInvariant();
        foreach (var definition in ToneDefinitions)
            if (string.Equals(definition.Id, id, StringComparison.Ordinal)) return definition.Id;
        return ToneAuto;
    }

    /// <summary>
    /// 当前该不该用**深色系**填充（v0.98.0）：<c>auto</c> = 跟界面深浅（浅色界面用深色系）；
    /// <c>light</c> / <c>dark</c> = 用户强制指定，与界面主题无关。设置页的「光标深浅」走这里。
    /// </summary>
    public static bool UseDarkTone(string? configuredTone, bool isLightTheme)
        => NormalizeTone(configuredTone) switch
        {
            ToneLight => false,
            ToneDark => true,
            _ => isLightTheme,
        };

    /// <summary>按主题映射到 auto 的实际样式。</summary>
    public static InAppCursorKind KindForTheme(UiTheme theme) => theme switch
    {
        UiTheme.LiquidGlass => InAppCursorKind.LiquidGlass,
        UiTheme.FrostedGlass => InAppCursorKind.FrostedGlass,
        UiTheme.Acrylic => InAppCursorKind.Acrylic,
        UiTheme.Daoji => InAppCursorKind.Daoji,
        UiTheme.CloudSky => InAppCursorKind.CloudSky,
        _ => InAppCursorKind.Default,
    };

    /// <summary>解析实际光标样式：<c>auto</c> 跟主题；<c>custom</c> 没图片时回退到主题样式。</summary>
    public static InAppCursorKind Resolve(string? configured, UiTheme theme, bool hasCustomImage)
    {
        var id = Normalize(configured);
        if (id == Auto) return KindForTheme(theme);
        if (id == Custom) return hasCustomImage ? InAppCursorKind.Custom : KindForTheme(theme);
        return id switch
        {
            LiquidGlass => InAppCursorKind.LiquidGlass,
            FrostedGlass => InAppCursorKind.FrostedGlass,
            Acrylic => InAppCursorKind.Acrylic,
            Daoji => InAppCursorKind.Daoji,
            CloudSky => InAppCursorKind.CloudSky,
            _ => InAppCursorKind.Default,
        };
    }

    /// <summary>
    /// 取内置箭头在当前界面深浅下的一套配色（v0.97.0）。
    /// <para><paramref name="useDarkTone"/> = true → 用**深色系**（深色填充 + 浅色描边，浅色底上才看得清）；
    /// false → 用浅色系（浅色填充 + 深色描边，深色底上才看得清）。两档都保留每套主题自己的强调色。</para>
    /// <para>该用哪一档由设置页的「光标深浅」决定，见 <see cref="UseDarkTone"/>（v0.98.0 起可以独立于界面主题强制指定）。</para>
    /// <para><see cref="InAppCursorKind.Custom"/> 不画箭头（用上传的图片），这里回退经典箭头，
    /// 保证调用方永远能拿到一套合法配色。</para>
    /// </summary>
    public static InAppCursorPalette Palette(InAppCursorKind kind, bool useDarkTone)
        => useDarkTone ? DarkTone(kind) : LightTone(kind);

    /// <summary>深色界面用的浅色系（v0.91.0 原配色，深色界面上看得清）。</summary>
    private static InAppCursorPalette LightTone(InAppCursorKind kind) => kind switch
    {
        InAppCursorKind.LiquidGlass => new InAppCursorPalette("#D9FFFFFF", "#7FB8FF", "#8FB4FF", true),
        InAppCursorKind.FrostedGlass => new InAppCursorPalette("#F5FAFF", "#7A8B9C", "#9FB4C8", false),
        InAppCursorKind.Acrylic => new InAppCursorPalette("#E62A2140", "#A98BFF", "#D7C7FF", false),
        InAppCursorKind.Daoji => new InAppCursorPalette("#F2E4C2", "#2A2116", "#D6B26A", false),
        InAppCursorKind.CloudSky => new InAppCursorPalette("#FFFFFF", "#A57C45", "#D9C08A", false),
        _ => new InAppCursorPalette("#FFFFFF", "#1B2430", "#4C8DFF", false),
    };

    /// <summary>浅色界面用的深色系（v0.97.0 新增：深色填充 + 浅色描边，浅色底上看得清）。</summary>
    private static InAppCursorPalette DarkTone(InAppCursorKind kind) => kind switch
    {
        InAppCursorKind.LiquidGlass => new InAppCursorPalette("#E61A2638", "#9CC6FF", "#7FB8FF", true),
        InAppCursorKind.FrostedGlass => new InAppCursorPalette("#FF2B3644", "#DCE6F2", "#8FB4C8", false),
        InAppCursorKind.Acrylic => new InAppCursorPalette("#F0241C38", "#D7C7FF", "#A98BFF", false),
        InAppCursorKind.Daoji => new InAppCursorPalette("#FF241D12", "#D6B26A", "#D6B26A", false),
        InAppCursorKind.CloudSky => new InAppCursorPalette("#FF2C3A55", "#E2B65C", "#FFC94D", false),
        _ => new InAppCursorPalette("#FF232B36", "#F4F7FB", "#4C8DFF", false),
    };
}

/// <summary>
/// 应用内光标的一套配色（v0.97.0）：填充 / 描边 / 强调点都是 <c>#AARRGGBB</c> 字符串，
/// <see cref="Glow"/> = 是否加柔光（只有液态玻璃有）。渲染层解析成画刷，自检直接断言色值。
/// </summary>
public sealed record InAppCursorPalette(string Fill, string Stroke, string Dot, bool Glow);

/// <summary>应用内光标「光标深浅」下拉框的一项（v0.98.0）。</summary>
public sealed record CursorToneDefinition(string Id, string Name, string Description);

/// <summary>应用内光标下拉框的一项。</summary>
public sealed record CursorStyleDefinition(string Id, string Name, string Description);
