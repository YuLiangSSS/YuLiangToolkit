using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.FlashDownloader.Core;

namespace YuLiangToolkit.Tools.FlashDownloader.ViewModels;

/// <summary>
/// 游戏列表里的一行：包一层 <see cref="GameEntry"/> 并补上界面需要的东西
/// （一眼看懂的类型标签、打开方式、后缀，以及“找不到本体文件”的警告）。
///
/// <para>重写了 <see cref="ToString"/>，否则 UI 自动化 / 读屏软件看到的是类型全名。</para>
/// </summary>
public sealed class GameItem : ObservableObject
{
    public GameItem(GameEntry entry)
    {
        Entry = entry;
        Warning = BuildWarning(entry);
    }

    /// <summary>原始条目。</summary>
    public GameEntry Entry { get; }

    /// <summary>列表主标题（文件夹带尾斜杠）。</summary>
    public string DisplayText => Entry.DisplayText;

    /// <summary>类型标签：<c>SWF</c> / <c>网页</c> / <c>?</c>。</summary>
    public string TypeBadge => !Entry.IsFolder ? "SWF"
        : Entry.FolderExt == ".swf" ? "SWF"
        : Entry.FolderExt == ".htm" ? "网页"
        : "?";

    /// <summary>打开方式。</summary>
    public string OpenModeText => Entry.OpenModeText;

    /// <summary>站点。</summary>
    public string SiteText => Entry.SiteText;

    /// <summary>后缀（<c>a12345</c>）。</summary>
    public string AppendixText => Entry.AppendixText;

    /// <summary>大小。</summary>
    public string SizeText => Entry.SizeText;

    /// <summary>说明列（打开方式 + 大小，或者“旧版命名”警告）。</summary>
    public string DetailText => Entry.DetailText;

    /// <summary>能不能玩（本体文件在不在）。</summary>
    public bool CanPlay => Entry.CanPlay;

    /// <summary>需要提醒用户的问题（空 = 没问题）。</summary>
    public string Warning { get; }

    /// <summary>有没有问题。</summary>
    public bool HasWarning => Warning.Length > 0;

    /// <summary>本地完整路径（给 tooltip 用）。</summary>
    public string FullPath => Entry.FullPath;

    private static string BuildWarning(GameEntry entry)
    {
        var problems = new List<string>();
        if (entry.IsLegacyFolderName)
            problems.Add("旧版命名：请在文件夹名后面补上 s（用 Flash 播放器）或 h（用网页）");
        else if (!entry.CanPlay)
            problems.Add($"文件夹后缀是 {entry.FolderExt}，不支持打开");
        else if (!entry.HasGameFile)
            problems.Add($"找不到游戏本体：{entry.GameFullPath}");

        if (entry.IsFolder && entry.FolderExt != ".unknown")
        {
            var ini = SwfNetPathIni.FromFile(entry.IniFullPath, entry.FolderExt);
            if (ini.HasProblem && !ini.IsMissing) problems.Add($"SWFNetPath.ini：{ini.ErrorText}");
            if (ini.NetPath.Length == 0) problems.Add("SWFNetPath.ini 里没有网络路径，批量下载 / 自动下载会用不了");
        }

        return string.Join("；", problems);
    }

    public override string ToString() => $"{DisplayText}（{TypeBadge} · {DetailText}）";
}
