using YuLiangToolkit.Tools.PptViewer.Parsing;

namespace YuLiangToolkit.Tools.PptViewer.ViewModels;

/// <summary>
/// 查找命中的界面投影：把 <see cref="PptSearchHit"/> 拆成「第几页 / 来源 / 片段」三块，
/// 方便以后挂到查找结果列表上（当前界面只显示命中数，列表数据先备好）。
/// </summary>
public sealed class PptSearchHitItem
{
    public PptSearchHitItem(PptSearchHit hit)
    {
        Hit = hit;
    }

    /// <summary>原始命中记录（页号 / 来源 / 下标 / 片段）。</summary>
    public PptSearchHit Hit { get; }

    /// <summary>第几页（1 开始）。</summary>
    public int SlideIndex => Hit.SlideIndex;

    /// <summary>「第 3 页」。</summary>
    public string PageText => $"第 {Hit.SlideIndex} 页";

    /// <summary>命中来源：「正文」/「备注」。</summary>
    public string SourceText => Hit.Source == PptSearchSource.Notes ? "备注" : "正文";

    /// <summary>片段（已去掉换行，前后可能带省略号）。</summary>
    public string Snippet => Hit.Snippet;

    /// <summary>一行显示文字：「第 3 页 · 正文：……命中…… 」。</summary>
    public string DisplayText => $"{PageText} · {SourceText}：{Snippet}";
}
