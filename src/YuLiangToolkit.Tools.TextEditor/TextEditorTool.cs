using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.TextEditor.ViewModels;
using YuLiangToolkit.Tools.TextEditor.Views;

namespace YuLiangToolkit.Tools.TextEditor;

/// <summary>
/// 文本编辑器（参考 Zed 的界面组织）：顶部菜单栏 + 多标签 + AvalonEdit 编辑区 + 偏好设置面板。
/// </summary>
public class TextEditorTool : ITool
{
    public string Id => TextEditorConfig.ToolId;

    public string Name => "文本编辑器";

    public string Description =>
        "轻量代码/文本编辑：多标签打开文件，行号显示、语法高亮、查找替换、界面缩放，" +
        "偏好设置里可调字体 / 字号 / 背景颜色（AvalonEdit 内核）";

    public string EmojiIcon => "📝";

    /// <summary>跟批量重命名同属「文件」类（文件类工具排在一起）。</summary>
    public string Category => "文件";

    /// <summary>主页标签：卡片上显示成蓝色小字，点主页的标签即可快速筛选。</summary>
    public IReadOnlyList<string> Tags => new[] { "编辑器", "代码", "文本", "行号", "AvalonEdit" };

    /// <summary>排在批量重命名（40）后面。</summary>
    public int Order => 41;

    public FrameworkElement CreateView()
        => new TextEditorView { DataContext = new TextEditorViewModel() };
}
