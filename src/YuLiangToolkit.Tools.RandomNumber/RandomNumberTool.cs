using System.Windows;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.Tools.RandomNumber;

/// <summary>
/// 「随机数生成器」工具入口：按可调的下限 / 上限 / 精确度（步长）生成均匀随机数，
/// 数值用 BigInteger 十进制精确表示，支持超大整数与任意细的小数。
/// </summary>
public sealed class RandomNumberTool : ITool
{
    public string Id => RandomNumberConfig.ToolId;

    public string Name => "随机数生成器";

    public string Description =>
        "按可调下限 / 上限 / 精确度（步长 0.1、0.01…）生成精确随机数："
        + "大整数与超长小数都不丢位，可调个数 / 去重 / 排序 / 分隔符，一键复制结果。";

    public string EmojiIcon => "🎲";

    public string Category => "数学";

    public IReadOnlyList<string> Tags => new[] { "数学", "随机数", "大数", "小数", "抽样" };

    /// <summary>主页卡片排序：夹在「计算器」(510) 与「函数绘图」(520) 之间。</summary>
    public int Order => 515;

    public FrameworkElement CreateView()
        => new Views.RandomNumberView { DataContext = new ViewModels.RandomNumberViewModel() };
}
