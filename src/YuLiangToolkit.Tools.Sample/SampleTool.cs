using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Sample.ViewModels;
using YuLiangToolkit.Tools.Sample.Views;

namespace YuLiangToolkit.Tools.Sample;

/// <summary>
/// 示例工具（删掉整个 <c>src\YuLiangToolkit.Tools.Sample</c> 文件夹即可移除，
/// 不影响其它功能）。它的作用是：把 Data / Config 两个轮子跑起来，作为新功能的复制模板。
/// </summary>
public class SampleTool : ITool
{
    public string Id => "sample-framework";
    public string Name => "框架示例";
    public string Description => "Data/Config 轮子演示：选项即时写入 Config，资源文件放进 Data（本项目可直接复制为新功能）";
    public string EmojiIcon => "🧪";
    public string Category => "开发";

    /// <summary>主页标签：卡片上显示成蓝色小字，点主页的标签即可快速筛选。</summary>
    public IReadOnlyList<string> Tags => new[] { "示例", "模板", "Data", "Config", "轮子演示" };

    public int Order => 900;

    public FrameworkElement CreateView()
        => new SampleView { DataContext = new SampleViewModel() };
}
