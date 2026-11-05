using YuLiangToolkit.App.Themes;

namespace YuLiangToolkit.App.Models;

/// <summary>应用内光标实际画哪一套（<c>auto</c> 会先按当前界面主题解析成这里的某一项）。</summary>
public enum InAppCursorKind
{
    /// <summary>经典白色箭头 + 深色描边（默认风格 / 自动时默认主题用）。</summary>
    Default,

    /// <summary>液态玻璃：半透明白 + 蓝色描边 + 柔光。</summary>
    LiquidGlass,

    /// <summary>毛玻璃：奶白 + 灰蓝描边。</summary>
    FrostedGlass,

    /// <summary>亚克力：深紫 + 淡紫描边。</summary>
    Acrylic,

    /// <summary>道系：墨色 + 香槟金描边 / 金点。</summary>
    Daoji,

    /// <summary>云间天上：白色 + 暖金描边 + 小太阳点。</summary>
    CloudSky,

    /// <summary>用户上传的图片。</summary>
    Custom,
}

/// <summary>
/// 应用内光标样式清单 / 解析（v0.91.0）：配置只存稳定字符串 Id，<c>auto</c> 跟随当前 <see cref="UiTheme"/>。
/// <para>设置页的下拉框由 <see cref="Definitions"/> 生成；渲染层用 <see cref="Resolve"/> 得到实际 <see cref="InAppCursorKind"/>。</para>
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

    /// <summary>设置页下拉框里的全部选项（顺序即显示顺序）。</summary>
    public static IReadOnlyList<CursorStyleDefinition> Definitions { get; } = new[]
    {
        new CursorStyleDefinition(Auto, "跟随界面主题", "默认风格用经典箭头；玻璃 / 道系 / 云间天上各用自己的一套。"),
        new CursorStyleDefinition(Default, "经典箭头", "白色箭头 + 深色描边，最百搭。"),
        new CursorStyleDefinition(LiquidGlass, "液态玻璃", "半透明玻璃箭头 + 蓝色描边 + 柔光。"),
        new CursorStyleDefinition(FrostedGlass, "毛玻璃", "奶白磨砂箭头 + 灰蓝描边，安静。"),
        new CursorStyleDefinition(Acrylic, "亚克力", "深紫底 + 淡紫描边，类似 Win11 亚克力。"),
        new CursorStyleDefinition(Daoji, "道系（墨金）", "墨底 + 香槟金描边 / 金点。"),
        new CursorStyleDefinition(CloudSky, "云间天上", "白色箭头 + 暖金描边 + 小太阳点。"),
        new CursorStyleDefinition(Custom, "自定义图片", "用你上传的小图片作为光标；可在下面选择历史图片。"),
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
}

/// <summary>应用内光标下拉框的一项。</summary>
public sealed record CursorStyleDefinition(string Id, string Name, string Description);
