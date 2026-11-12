using System.Windows;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.Tools.NumberConverter;

/// <summary>
/// 「数字转换器」工具入口：一个数字转成中文大小写 / 人民币金额 / 英文念法 / 罗马数字 /
/// 进制 / 千分位 / 科学计数法等常见写法，也能反过来把中文金额读回数字。
/// </summary>
public sealed class NumberConverterTool : ITool
{
    public string Id => NumberConverterConfig.ToolId;

    public string Name => "数字转换器";

    public string Description =>
        "一个数字转成各种写法：中文小写 / 大写（壹拾贰圆）、人民币金额大写、"
        + "英文念法（one thousand / a thousand、one hundred and twenty-three）、序数词、"
        + "罗马数字、千分位、二·八·十六进制、科学计数法、逐位念号码；"
        + "反过来也能把「壹拾贰圆叁角肆分」读回 12.34。";

    public string EmojiIcon => "🔢";

    public string Category => "数学";

    public IReadOnlyList<string> Tags => new[]
    {
        "数学", "转换", "金额", "大写", "人民币", "英文念法", "罗马数字", "进制", "千分位", "支票",
    };

    /// <summary>主页卡片排序：紧跟在「随机数生成器」(515) 之后。</summary>
    public int Order => 516;

    public FrameworkElement CreateView()
        => new Views.NumberConverterView { DataContext = new ViewModels.NumberConverterViewModel() };
}
