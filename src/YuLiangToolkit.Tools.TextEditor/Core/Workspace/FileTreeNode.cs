using System.Collections.ObjectModel;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.TextEditor.Core.Workspace;

/// <summary>
/// 「打开文件夹」左侧文件浏览器的一个节点（纯模型 + 懒加载，自检可直接断言）。
/// <para>文件夹节点先塞一个占位子节点让 TreeView 显示展开箭头；真正展开时才枚举磁盘
/// （<see cref="LoadChildren"/>），大目录不会一打开就卡住。</para>
/// </summary>
public sealed class FileTreeNode : ObservableObject
{
    /// <summary>文件夹图标（Segoe Fluent Icons / MDL2 字体里的字形）。</summary>
    public const string FolderGlyph = "\uE8B7";

    /// <summary>文件图标。</summary>
    public const string FileGlyph = "\uE8A5";

    private bool _isExpanded;
    private bool _isLoaded;

    private FileTreeNode(string name, string fullPath, bool isDirectory, bool isPlaceholder)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsPlaceholder = isPlaceholder;

        Children = new ObservableCollection<FileTreeNode>();
        if (isDirectory && !isPlaceholder)
            Children.Add(new FileTreeNode("加载中…", "", false, isPlaceholder: true));
    }

    /// <summary>节点显示名（文件夹名 / 文件名）。</summary>
    public string Name { get; }

    /// <summary>完整路径（占位节点为空）。</summary>
    public string FullPath { get; }

    /// <summary>是不是文件夹。</summary>
    public bool IsDirectory { get; }

    /// <summary>是不是「加载中…」占位节点（不可点击、不算文件）。</summary>
    public bool IsPlaceholder { get; }

    /// <summary>子节点（文件夹懒加载；文件为空）。</summary>
    public ObservableCollection<FileTreeNode> Children { get; }

    /// <summary>文件夹是否已经枚举过磁盘（避免重复 IO；刷新时重建整棵树）。</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    /// <summary>是否展开（双向绑定 TreeViewItem；展开时由 View 触发 <see cref="LoadChildren"/>）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value)) OnPropertyChanged(nameof(Glyph));
        }
    }

    /// <summary>图标字形（文件夹 / 文件；占位节点不显示）。</summary>
    public string Glyph => IsPlaceholder ? "" : IsDirectory ? FolderGlyph : FileGlyph;

    /// <summary>枚举这一层子节点（文件夹才有效；只枚举一次，失败当空目录）。</summary>
    public void LoadChildren()
    {
        if (!IsDirectory || IsPlaceholder || IsLoaded) return;

        IsLoaded = true;
        Children.Clear();
        foreach (var child in WorkspaceTree.ListChildren(FullPath))
            Children.Add(child);
    }

    /// <summary>建一个文件夹节点。</summary>
    public static FileTreeNode CreateDirectory(string name, string fullPath)
        => new(name, fullPath, isDirectory: true, isPlaceholder: false);

    /// <summary>建一个文件节点。</summary>
    public static FileTreeNode CreateFile(string name, string fullPath)
        => new(name, fullPath, isDirectory: false, isPlaceholder: false);
}

/// <summary>文件浏览器的目录枚举规则（纯逻辑 + 文件 IO，自检直接断言：文件夹在前、按名字不区分大小写排序）。</summary>
public static class WorkspaceTree
{
    /// <summary>打开一个文件夹：不存在 / 没权限返回 <c>null</c>。</summary>
    public static FileTreeNode? TryCreateRoot(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;
        try
        {
            var full = Path.GetFullPath(folderPath.Trim());
            if (!Directory.Exists(full)) return null;

            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = full;   // 根盘符（C:\）没有文件名，就直接显示路径
            return FileTreeNode.CreateDirectory(name, full);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 列出一个目录的子节点：文件夹排前面、文件排后面，各自按名字不区分大小写排序；
    /// 没权限 / 目录消失时返回空表（界面显示空目录，不弹窗）。
    /// </summary>
    public static IReadOnlyList<FileTreeNode> ListChildren(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return Array.Empty<FileTreeNode>();

        var result = new List<FileTreeNode>();
        try
        {
            foreach (var path in Directory.EnumerateDirectories(directory))
            {
                try
                {
                    result.Add(FileTreeNode.CreateDirectory(Path.GetFileName(path), path));
                }
                catch
                {
                    // 单个条目异常跳过
                }
            }

            var folderCount = result.Count;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                try
                {
                    result.Add(FileTreeNode.CreateFile(Path.GetFileName(path), path));
                }
                catch
                {
                    // 单个条目异常跳过
                }
            }

            var folders = result.Take(folderCount)
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var files = result.Skip(folderCount)
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            folders.AddRange(files);
            result = folders;
        }
        catch
        {
            // 目录没权限 / 不存在 → 当空目录
        }

        return result;
    }
}
