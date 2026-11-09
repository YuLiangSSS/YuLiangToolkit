using System.Windows;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.Tools.Calculator;

/// <summary>
/// 「计算器」工具入口：普通 + 科学计算。表达式解析 / 求值 / 结果格式化全部复用
/// <c>Core/Expressions/</c> 的共享引擎（函数绘图也用同一套），本工具只负责输入、历史记录与配置。
/// </summary>
public sealed class CalculatorTool : ITool
{
    public string Id => CalculatorConfig.ToolId;

    public string Name => "计算器";

    public string Description =>
        "普通计算 + 科学计算：直接输表达式（2+3*4、sin(30)、log(2,8)、5!），边打边看结果；"
        + "大整数 / 阶乘 / 高次幂用任意精度精确计算（100! 也不丢位），三角函数可切角度制 / 弧度制，"
        + "结果精度可调，历史记录一键回填。";

    public string EmojiIcon => "🧮";

    public string Category => "数学";

    public IReadOnlyList<string> Tags => new[] { "数学", "计算器", "科学计算器", "表达式", "三角函数" };

    /// <summary>主页卡片排序：排在「软件升级器」之后，归在数学 / 计算类工具附近。</summary>
    public int Order => 510;

    public FrameworkElement CreateView()
        => new Views.CalculatorView { DataContext = new ViewModels.CalculatorViewModel() };
}
