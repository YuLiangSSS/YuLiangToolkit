using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.BatchRename.ViewModels;
using YuLiangToolkit.Tools.BatchRename.Views;

namespace YuLiangToolkit.Tools.BatchRename;

/// <summary>
/// 批量重命名工具：把一堆文件按同一套规则改名，改之前先给预览，改完还能一键撤销。
/// </summary>
public class BatchRenameTool : ITool
{
    public string Id => "batch-rename";
    public string Name => "批量重命名";
    public string Description => "按同一套规则批量改文件名：查找替换 / 加前后缀 / 自动编号 / 改扩展名 / 大小写，改前预览、改后可撤销";
    public string EmojiIcon => "🏷️";
    public string Category => "文件";

    /// <summary>主页标签：卡片上显示成蓝色小字，点主页的标签即可快速筛选。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "重命名", "改名", "批量", "文件", "序号", "编号", "查找替换", "正则", "扩展名", "撤销",
    };

    public int Order => 40;

    public FrameworkElement CreateView()
        => new BatchRenameView { DataContext = new BatchRenameViewModel() };
}
