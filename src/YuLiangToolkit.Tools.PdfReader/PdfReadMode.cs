namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 「阅读方式」——只做三种，刚好覆盖用户要的「上下滚 / 左右滚 / 左右两页翻着看」：
///
/// <list type="bullet">
///   <item><see cref="Vertical"/>：纵向 上→下（连续滚动，默认；文档 / 长帖最顺手）；</item>
///   <item><see cref="Horizontal"/>：横向 左→右（连续滚动，把页从左往右排成一条长带）；</item>
///   <item><see cref="Spread"/>：双页翻页（左右两页并排，一次翻过去；适合页漫 / 杂志）。</item>
/// </list>
///
/// <para>枚举按 <b>C# 原名</b>存进配置（<c>"Vertical"</c> / <c>"Horizontal"</c> / <c>"Spread"</c>），
/// 以后加新成员也不会让老配置错位。</para>
/// </summary>
public enum PdfReadMode
{
    /// <summary>纵向：页从上到下排成一条长带，滚轮 / 左键拖动带惯性往下看。</summary>
    Vertical = 0,

    /// <summary>横向：页从左到右排成一条长带，滚轮 / 左键拖动带惯性往右看。</summary>
    Horizontal = 1,

    /// <summary>双页翻页：左右两页并排（一次显示两页），滚轮 / 方向键 / 按钮翻过去。</summary>
    Spread = 2,
}

/// <summary>「阅读方式」的纯逻辑小工具（界面 / 自检都用它，别在各处重复判断）。</summary>
public static class PdfReadModes
{
    /// <summary>是不是连续滚动（纵向 / 横向两种长带）。</summary>
    public static bool IsContinuous(PdfReadMode mode) => Normalize(mode) != PdfReadMode.Spread;

    /// <summary>是不是横向连续滚动（决定长带用水平轴、按视口高度铺页）。</summary>
    public static bool IsHorizontal(PdfReadMode mode) => Normalize(mode) == PdfReadMode.Horizontal;

    /// <summary>是不是双页翻页。</summary>
    public static bool IsSpread(PdfReadMode mode) => Normalize(mode) == PdfReadMode.Spread;

    /// <summary>把任意枚举值（例如手改坏了的配置）归一化到合法值。</summary>
    public static PdfReadMode Normalize(PdfReadMode mode)
        => Enum.IsDefined(typeof(PdfReadMode), mode) ? mode : PdfReadMode.Vertical;

    /// <summary>给人看的名字（下拉框 / 状态栏用）。</summary>
    public static string Describe(PdfReadMode mode) => Normalize(mode) switch
    {
        PdfReadMode.Horizontal => "横向 左 → 右",
        PdfReadMode.Spread => "双页 翻页",
        _ => "纵向 上 → 下",
    };

    /// <summary>一句话解释这个方式怎么看（下拉框下面的小字 / 设置提示）。</summary>
    public static string Hint(PdfReadMode mode) => Normalize(mode) switch
    {
        PdfReadMode.Horizontal => "页从左往右排成一条长带：滚轮往下滚 = 往右看，左键拖动可以甩着看",
        PdfReadMode.Spread => "左右两页并排：滚轮 / ← → / PageUp PageDown / 空格 / 屏幕按钮翻页",
        _ => "页从上往下排成一条长带：滚轮往下滚 = 往后看，左键拖动可以甩着看",
    };
}
