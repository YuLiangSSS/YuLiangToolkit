using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.OpenWith.ViewModels;
using YuLiangToolkit.Tools.OpenWith.Views;

namespace YuLiangToolkit.Tools.OpenWith;

/// <summary>
/// 「打开方式」：把凉雨工具集登记进 Windows 右键 → 打开方式，提供三种入口
/// （按文件类型自动分流 / 以文本方式 / 以压缩包方式）。真正的注册表逻辑在
/// <c>YuLiangToolkit.Core.Shell.OpenWithRegistration</c>，本工具只负责开关与说明。
/// </summary>
public class OpenWithTool : ITool
{
    public string Id => OpenWithConfig.ToolId;

    public string Name => "打开方式";

    public string Description =>
        "把本工具集登记进右键「打开方式」：3 个入口（按文件类型自动分流 / 以文本方式 / 以压缩包方式），"
        + "PDF、图片、Word、Excel、压缩包、音视频、文本都能自动找到对应工具";

    public string EmojiIcon => "🗂️";

    public string Category => "文件";

    public IReadOnlyList<string> Tags => new[]
    {
        "打开方式", "Open with", "右键", "文件关联", "文本", "压缩包", "自动", "注册表",
    };

    /// <summary>排在文本编辑器（41）/ 压缩包工具（42）后面，同属「文件」类。</summary>
    public int Order => 43;

    public FrameworkElement CreateView() => new OpenWithView
    {
        DataContext = new OpenWithViewModel(),
    };
}
