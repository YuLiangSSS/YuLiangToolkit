using System.Windows;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.Tools.FunctionPlot;

/// <summary>
/// 「函数绘图」工具入口：同一张坐标图上同时画多条函数曲线，每条可以单独设颜色、显示开关与定义域；
/// 鼠标在图上移动会实时报出光标 x 与每个可见函数的 y，还能输入 x 求值或按当前范围自动取 10 个点。
///
/// <para>表达式解析 / 采样复用 Core 的共享表达式引擎（<c>Core/Expressions</c>），
/// 与「计算器」共用同一套语法；绘图界面是本工程自绘的 <c>PlotCanvas</c>。</para>
/// </summary>
public sealed class FunctionPlotTool : ITool
{
    public string Id => "function-plot";

    public string Name => "函数绘图";

    public string Description =>
        "在同一张坐标图上同时画多个函数，每条可单独设颜色、显示与定义域；"
        + "鼠标移到图上自动列出各函数在该 x 的值，也能输入 x 求值或按当前范围自动取 10 个点。";

    public string EmojiIcon => "📈";

    public string Category => "数学";

    public IReadOnlyList<string> Tags => new[] { "数学", "函数", "绘图", "图像", "坐标系" };

    /// <summary>主页根层卡片，紧跟「计算器」（510）后面。</summary>
    public int Order => 520;

    public FrameworkElement CreateView()
        => new Views.FunctionPlotView { DataContext = new ViewModels.FunctionPlotViewModel() };
}
