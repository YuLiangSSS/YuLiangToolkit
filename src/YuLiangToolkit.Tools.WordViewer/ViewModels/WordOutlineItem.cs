using System.Windows;

namespace YuLiangToolkit.Tools.WordViewer.ViewModels;

/// <summary>
/// 大纲侧栏里的一行（对应 <see cref="Parsing.WordHeading"/>，已经算好缩进量，XAML 直接绑 <c>Padding</c>）。
/// <para>点一行就跳到正文对应段落 —— 跳转由 View 的代码后置调用
/// <c>WordViewerViewModel.BringBlockIntoView(BlockIndex)</c> 完成。</para>
/// </summary>
public sealed class WordOutlineItem
{
    /// <summary>从解析出来的标题建一行。</summary>
    public WordOutlineItem(Parsing.WordHeading heading)
        : this(heading.Level, heading.Text, heading.BlockIndex)
    {
    }

    /// <summary>直接按级别 / 文字 / 块下标建一行（自检和界面的空标题都用它）。</summary>
    public WordOutlineItem(int level, string text, int blockIndex)
    {
        Level = Math.Clamp(level, 1, 6);
        Text = string.IsNullOrWhiteSpace(text) ? "（无标题）" : text.Trim();
        BlockIndex = blockIndex;
    }

    /// <summary>标题级别（1 ~ 6），越大缩进越深。</summary>
    public int Level { get; }

    /// <summary>标题文字（空标题显示「（无标题）」，保证这一行点得到）。</summary>
    public string Text { get; }

    /// <summary>对应正文块在 <c>WordDocument.Blocks</c> 里的下标。</summary>
    public int BlockIndex { get; }

    /// <summary>缩进：每深一级往右 14 DIP（同时留出按钮自身的左右内边距）。</summary>
    public Thickness IndentPadding => new(6 + (Level - 1) * 14, 5, 6, 5);

    /// <summary>级别提示（鼠标悬停时显示）。</summary>
    public string LevelText => $"{Level} 级标题";
}
