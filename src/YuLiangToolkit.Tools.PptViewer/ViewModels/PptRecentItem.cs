using System.IO;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.PptViewer.ViewModels;

/// <summary>
/// 「最近打开」下拉里的一条记录（把 <see cref="RecentFileEntry"/> 变成界面能直接绑的显示信息）。
/// <para>文件已经不存在的记录仍然列出来，只标成「文件已不存在」——这样用户知道它曾经在哪儿，
/// 点它只给提示、不会报错（清掉记录用工具条上的「清空」按钮）。</para>
/// </summary>
public sealed class PptRecentItem
{
    public PptRecentItem(string path)
    {
        Path = path;
        IsAvailable = File.Exists(path);
    }

    /// <summary>完整路径。</summary>
    public string Path { get; }

    /// <summary>文件现在还在不在（不在时界面标灰并禁止真正打开）。</summary>
    public bool IsAvailable { get; }

    /// <summary>文件已经不存在（界面标灰用）。</summary>
    public bool IsMissing => !IsAvailable;

    /// <summary>显示用的文件名。</summary>
    public string FileName
    {
        get
        {
            try { return System.IO.Path.GetFileName(Path); }
            catch { return Path; }
        }
    }

    /// <summary>所在文件夹（悬停提示用）。</summary>
    public string FolderText
    {
        get
        {
            try { return System.IO.Path.GetDirectoryName(Path) ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>下拉里显示的一行文字（文件不存在时带提示）。</summary>
    public string DisplayText => IsMissing ? FileName + "（文件已不存在）" : FileName;

    /// <summary>悬停提示：完整路径 + 状态。</summary>
    public string ToolTipText => IsMissing ? Path + Environment.NewLine + "文件已被移动或删除" : Path;
}
