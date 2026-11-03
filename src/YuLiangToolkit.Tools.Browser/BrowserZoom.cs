namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// 页面缩放的纯逻辑（<b>不碰界面 / 不碰 WebView2</b>，自检 <c>browser</c> 组直接断言）：
/// 夹取、加减档、百分比文本。<c>BrowserView</c> 把结果写进 WebView2 的 <c>ZoomFactor</c>，
/// Ctrl + 鼠标滚轮的原生缩放也走同一套范围 / 显示规则。
///
/// <para>范围取 <b>25% ~ 500%</b>，按 10% 一档加减（和常见浏览器一致）；越界自动夹回。</para>
/// </summary>
public static class BrowserZoom
{
    /// <summary>最小缩放（25%）。</summary>
    public const double Min = 0.25;

    /// <summary>最大缩放（500%）。</summary>
    public const double Max = 5.0;

    /// <summary>按钮 / 快捷键一次改变的档位（10%）。</summary>
    public const double Step = 0.1;

    /// <summary>把任意缩放值夹回合法范围（非法 / NaN / 无穷返回 100%），保留两位小数。</summary>
    public static double Clamp(double factor)
    {
        if (double.IsNaN(factor) || double.IsInfinity(factor)) return 1.0;
        return Math.Round(Math.Clamp(factor, Min, Max), 2);
    }

    /// <summary>放大一档（夹回合法范围）。</summary>
    public static double In(double factor) => Clamp(Math.Round(Clamp(factor) + Step, 2));

    /// <summary>缩小一档（夹回合法范围）。</summary>
    public static double Out(double factor) => Clamp(Math.Round(Clamp(factor) - Step, 2));

    /// <summary>缩放显示成百分比文本，例如 <c>110%</c>。</summary>
    public static string Format(double factor) => $"{Math.Round(Clamp(factor) * 100)}%";
}
