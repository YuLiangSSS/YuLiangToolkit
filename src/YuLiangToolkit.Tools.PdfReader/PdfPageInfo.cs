namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 一页 PDF 的原始尺寸。单位是 <b>DIP</b>（96 DPI 基准），直接来自
/// <c>Windows.Data.Pdf.PdfPage.Size</c>，排版只用它的宽高比，跟页面实际多少像素无关。
///
/// <para>纯数据：不引用任何 WinRT 类型，自检可以直接 new 出来喂给排版函数。</para>
/// </summary>
public sealed record PdfPageInfo(int Index, double WidthDip, double HeightDip)
{
    /// <summary>宽高都合法（大于 0 且不是 NaN）。坏数据退回默认竖版比例，排版不会除以 0。</summary>
    public bool HasValidSize
        => WidthDip > 0 && HeightDip > 0
           && !double.IsNaN(WidthDip) && !double.IsNaN(HeightDip)
           && !double.IsInfinity(WidthDip) && !double.IsInfinity(HeightDip);

    /// <summary>宽 / 高（横向页面 &gt; 1，竖向页面 &lt; 1）；坏数据按 A4 竖版算。</summary>
    public double AspectRatio => HasValidSize ? WidthDip / HeightDip : 2.0 / 3.0;
}
