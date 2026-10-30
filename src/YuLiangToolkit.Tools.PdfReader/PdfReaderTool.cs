using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PdfReader.ViewModels;
using YuLiangToolkit.Tools.PdfReader.Views;

namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 「PDF 阅读器」——只做三件简单的事：
///
/// <list type="number">
///   <item><b>上下滚动</b>：所有页从上到下排成一条长带，滚轮 / 左键拖动连续看；</item>
///   <item><b>左右滚动</b>：所有页从左到右排成一条长带，滚轮 / 左键拖动连续看；</item>
///   <item><b>双页翻页</b>：左右两页并排，一次翻过去（滚轮 / 方向键 / 屏幕按钮）。</item>
/// </list>
///
/// <para>两种滚动模式下按住左键拖动，松手都会按甩动速度**继续滑一段**（惯性）；
/// PDF 解码用系统自带的 <c>Windows.Data.Pdf</c>，不引入第三方 PDF 库。</para>
/// </summary>
public class PdfReaderTool : ITool
{
    public string Id => PdfReaderConfig.ToolId;

    public string Name => "PDF 阅读器";

    public string Description => "简单 PDF 阅读器：上下 / 左右连续滚动，双页并排翻页阅读，左键拖动带惯性";

    public string EmojiIcon => "📄";

    public string Category => "媒体";

    public IReadOnlyList<string> Tags => new[]
    {
        "PDF", "文档", "阅读器", "阅读", "滚动", "上下滚动", "左右滚动", "连续滚动",
        "双页", "翻页", "两页", "阅读模式", "拖拽", "惯性", "甩动",
    };

    public int Order => 17;

    public FrameworkElement CreateView() => new PdfReaderView
    {
        DataContext = new PdfReaderViewModel(),
    };
}
