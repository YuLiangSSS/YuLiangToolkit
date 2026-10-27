using System.Globalization;

namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 「图片边框」的样式：从内到外三层 —— **内边框（描边）→ 填充（底色带）→ 外边框（描边）**，
/// 三层都能调粗细，三种颜色都是 ARGB（0~255，含透明度）。颜色存成 <c>#AARRGGBB</c> 字符串，手改配置也看得懂。
///
/// <para>纯逻辑（解析 / 夹取 / 拼颜色）都在这里，自检直接断言；界面只负责把颜色画成 Brush 和排版。</para>
/// </summary>
public sealed record PictureBorderStyle(
    bool Enabled,
    int InnerSize,
    int FillSize,
    int OuterSize,
    string InnerColor,
    string FillColor,
    string OuterColor)
{
    /// <summary>内 / 外边框最大粗细（像素）。</summary>
    public const int MaxBorderSize = 32;

    /// <summary>填充最大宽度（像素）。</summary>
    public const int MaxFillSize = 128;

    /// <summary>默认内边框颜色（贴着图的深色描边）。</summary>
    public const string DefaultInnerColor = "#FF0E1014";

    /// <summary>默认填充颜色（比页面底色亮的一条灰带，用来隔开两张图）。</summary>
    public const string DefaultFillColor = "#FF39414F";

    /// <summary>默认外边框颜色（更亮一点的描边，深色背景下也能看清）。</summary>
    public const string DefaultOuterColor = "#FF5B6474";

    /// <summary>默认内边框粗细。</summary>
    public const int DefaultInnerSize = 1;

    /// <summary>默认填充宽度。</summary>
    public const int DefaultFillSize = 6;

    /// <summary>默认外边框粗细。</summary>
    public const int DefaultOuterSize = 1;

    /// <summary>默认样式（设置面板「恢复默认」也会回到这一套）。</summary>
    public static PictureBorderStyle Default { get; } = new(
        true,
        DefaultInnerSize,
        DefaultFillSize,
        DefaultOuterSize,
        DefaultInnerColor,
        DefaultFillColor,
        DefaultOuterColor);

    /// <summary>单侧多出来的总厚度 = 内 + 填充 + 外（关掉边框时是 0）。</summary>
    public int ExtraPerSide
        => Enabled ? ClampBorder(InnerSize) + ClampFill(FillSize) + ClampBorder(OuterSize) : 0;

    /// <summary>两个方向加起来多出来的总尺寸（布局时每张图要加这么多）。</summary>
    public int TotalExtra => ExtraPerSide * 2;

    /// <summary>有没有任何一层真的要画。</summary>
    public bool HasAnyBorder => ExtraPerSide > 0;

    /// <summary>从配置读出生效样式（坏值会夹取 / 回退到默认，界面不会画崩）。</summary>
    public static PictureBorderStyle FromConfig(PictureViewerConfig config) => new(
        config.BorderEnabled,
        ClampBorder(config.BorderInnerSize),
        ClampFill(config.BorderFillSize),
        ClampBorder(config.BorderOuterSize),
        NormalizeColor(config.BorderInnerColor, DefaultInnerColor),
        NormalizeColor(config.BorderFillColor, DefaultFillColor),
        NormalizeColor(config.BorderOuterColor, DefaultOuterColor));

    /// <summary>内 / 外边框粗细夹到 0 ~ 32。</summary>
    public static int ClampBorder(int value) => Math.Clamp(value, 0, MaxBorderSize);

    /// <summary>填充宽度夹到 0 ~ 128。</summary>
    public static int ClampFill(int value) => Math.Clamp(value, 0, MaxFillSize);

    /// <summary>颜色字符串规范化成 <c>#AARRGGBB</c>；解析不了就用默认色。</summary>
    public static string NormalizeColor(string? text, string fallback)
        => TryParseArgb(text, out var argb) ? ToHex(argb) : fallback;

    /// <summary>
    /// 解析颜色：支持 <c>#AARRGGBB</c> / <c>#RRGGBB</c> / <c>AARRGGBB</c> / <c>RRGGBB</c>
    /// （不带 alpha 时按 255 处理）；大小写都行。
    /// </summary>
    public static bool TryParseArgb(string? text, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var hex = text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        else if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];

        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8) return false;

        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb);
    }

    /// <summary>把 ARGB 拼成 <c>#AARRGGBB</c>。</summary>
    public static string ToHex(uint argb) => $"#{argb:X8}";

    /// <summary>拆出 A/R/G/B（都是 0~255）。</summary>
    public static (int R, int G, int B, int A) Split(uint argb)
        => ((int)(argb >> 16) & 0xFF, (int)(argb >> 8) & 0xFF, (int)argb & 0xFF, (int)(argb >> 24) & 0xFF);

    /// <summary>按 RGBA（0~255，超出夹住）拼回 ARGB。</summary>
    public static uint Compose(int r, int g, int b, int a)
        => ((uint)ClampByte(a) << 24)
           | ((uint)ClampByte(r) << 16)
           | ((uint)ClampByte(g) << 8)
           | (uint)ClampByte(b);

    /// <summary>RGBA → <c>#AARRGGBB</c>。</summary>
    public static string FromRgba(int r, int g, int b, int a) => ToHex(Compose(r, g, b, a));

    private static int ClampByte(int value) => Math.Clamp(value, 0, 255);
}
