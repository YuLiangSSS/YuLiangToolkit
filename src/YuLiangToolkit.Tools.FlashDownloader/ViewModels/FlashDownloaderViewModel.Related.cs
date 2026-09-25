using System.Collections.ObjectModel;
using System.Windows;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.FlashDownloader.Core;
using YuLiangToolkit.Tools.FlashDownloader.Engine;
using YuLiangToolkit.Tools.FlashDownloader.Web;

namespace YuLiangToolkit.Tools.FlashDownloader.ViewModels;

/// <summary>
/// 三个折叠面板（关联文件下载器 / 自动下载所需文件 / 自由下载工具）+ 目录自动刷新 + 批量下载的执行部分。
/// </summary>
public sealed partial class FlashDownloaderViewModel
{
    private CancellationTokenSource? _watchCts;
    private DateTime _lastSeenWrite = DateTime.MinValue;

    // ================================================================ 关联文件下载器

    /// <summary>选中的游戏是不是一个“可以补文件”的文件夹（不是就禁用面板）。</summary>
    public bool CanUseRelated
        => SelectedGame?.Entry is { IsFolder: true } entry
           && SwfNetPathIni.FromFile(entry.IniFullPath, entry.FolderExt).NetPath.Length > 0;

    private void UpdateRelatedPanel()
    {
        OnPropertyChanged(nameof(CanUseRelated));
        OnPropertyChanged(nameof(SelectedSummary));
        if (!CanUseRelated)
        {
            BatchStatusText = SelectedGame is null
                ? "请先在左边选一个游戏"
                : SelectedGame.Entry.IsFolder
                    ? "这个游戏的 SWFNetPath.ini 里没有网络路径，无法批量下载（换一个游戏，或手工把网络路径填进 ini）"
                    : "只有文件夹形式的游戏才能批量下载（散放的 swf 请先点「装入文件夹」）";
        }
        else
        {
            BatchStatusText = $"网络根地址：{CurrentNetBase()}";
        }
    }

    private string CurrentNetBase()
    {
        var entry = SelectedGame?.Entry;
        if (entry is null || !entry.IsFolder) return "";
        var ini = SwfNetPathIni.FromFile(entry.IniFullPath, entry.FolderExt);
        return GamePageParser.GetParentUrl(ini.NetPath);
    }

    private string CurrentReferer()
    {
        var entry = SelectedGame?.Entry;
        if (entry is null || !entry.IsFolder) return "";
        var ini = SwfNetPathIni.FromFile(entry.IniFullPath, entry.FolderExt);
        return ini.IsNewPage == SwfNetPathIni.NewPageTrue && ini.NewPageUrl.Length > 0
            ? ini.NewPageUrl
            : ini.NetPath;
    }

    private async Task RunRelatedDownloadAsync()
    {
        var entry = SelectedGame?.Entry;
        if (entry is null || !entry.IsFolder)
        {
            ShowError("批量下载", "请先在列表里选一个游戏文件夹");
            return;
        }

        var ini = SwfNetPathIni.FromFile(entry.IniFullPath, entry.FolderExt);
        if (ini.NetPath.Length == 0)
        {
            ShowError("批量下载", "需要先在 SWFNetPath.ini 里填好 swf / 网页在网上的路径");
            return;
        }

        var netBase = GamePageParser.GetParentUrl(ini.NetPath);
        if (netBase.Length == 0)
        {
            ShowError("批量下载", $"网络路径不完整：{ini.NetPath}");
            return;
        }

        var lines = (BatchInput ?? "").Split('\n');
        var (entries, summary) = BatchDownloadPlanner.Plan(
            lines, entry.FullPath, Cfg.SkipExistingFiles, File.Exists);

        RelatedRows.Clear();
        foreach (var e in entries) RelatedRows.Add(new RelatedRow(e));

        if (summary.Total == 0)
        {
            UpdateBatchStatus("您还未填写任何东西");
            return;
        }

        var ready = entries.Count(e => e.Status == BatchLineStatus.Ready);
        if (ready == 0)
        {
            UpdateBatchStatus(summary.Describe("没有需要下载的文件，"));
            return;
        }

        _cts = new CancellationTokenSource();
        IsBatchRunning = true;
        OnPropertyChanged(nameof(IsBatchRunning));
        Log($"===== 批量下载开始：{entry.DisplayText} =====");
        Log($"网络根地址：{netBase}");
        Log($"本次要下 {ready} 个（{summary.Describe()}）");
        StatusText = summary.Describe($"开始下载 {ready} 个，");

        try
        {
            await _related.RunAsync(
                netBase,
                CurrentReferer(),
                entry.FullPath,
                entries,
                summary,
                Cfg.MaxParallel,
                progress => Ui(() => OnBatchProgress(progress, entries)),
                _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("批量下载已取消");
        }
        catch (Exception ex)
        {
            Log($"[批量下载出错] {ex.Message}");
        }
        finally
        {
            IsBatchRunning = false;
            OnPropertyChanged(nameof(IsBatchRunning));
            _cts = null;
            IsBusy = false;
            RefreshGames(false);
            SelectedGame = Games.FirstOrDefault(g => g.Entry.FileOrFolderName == entry.FileOrFolderName);
        }
    }

    private void OnBatchProgress(BatchProgress progress, IReadOnlyList<BatchDownloadEntry> entries)
    {
        // 按顺序把已经处理过的行标成「已下载」（并发下载时顺序不严格，够用了）
        var readyIndexes = new List<int>();
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Status == BatchLineStatus.Ready) readyIndexes.Add(i);
        for (var i = 0; i < readyIndexes.Count && i < progress.Current; i++)
            RelatedRows[readyIndexes[i]].IsDownloaded = true;

        UpdateBatchStatus(progress.CurrentFile.Length > 0
            ? $"{progress.ProgressText} 当前：{progress.CurrentFile}"
            : progress.Describe());

        Progress = progress.Total > 0 ? (double)progress.Current / progress.Total : 0;
        ProgressText = $"{progress.Current} / {progress.Total}";

        if (progress.Finished)
        {
            Log("===== 批量下载结束 =====");
            Log(progress.Describe());
            StatusText = progress.Describe();
        }
        else if (progress.Current > 0 && (progress.Current % 5 == 0 || progress.Current == progress.Total))
        {
            Log(progress.Describe());
        }
    }

    private void UpdateBatchStatus(string text) => BatchStatusText = text;

    // ================================================================ 自动下载所需文件

    private void ToggleAutoDownload()
    {
        if (AutoDownloadEnabled)
        {
            Server.EndSession();
            AutoDownloadEnabled = false;
            RefreshAutoDownloadText(true);
            StatusText = "已停止自动下载";
            Log(StatusText);
            return;
        }

        var entry = SelectedGame?.Entry;
        if (entry is null || !entry.IsFolder || entry.FolderExt != ".htm")
        {
            ShowError("自动下载所需文件", "请先选中一个「网页游戏」文件夹（名字以 h 结尾的那种）");
            return;
        }

        var ini = SwfNetPathIni.FromFile(entry.IniFullPath, entry.FolderExt);
        if (ini.NetPath.Length == 0)
        {
            ShowError("自动下载所需文件", "这个游戏的 SWFNetPath.ini 里没有网络路径，无法自动下载");
            return;
        }

        var netBase = GamePageParser.GetParentUrl(ini.NetPath);
        if (netBase.Length == 0)
        {
            ShowError("自动下载所需文件", $"网络路径不完整：{ini.NetPath}");
            return;
        }

        if (!Server.IsRunning && !StartServer())
        {
            ShowError("自动下载所需文件", "本地服务器没起来，自动下载需要它（换个起始端口再试）");
            return;
        }

        Server.BeginSession(entry.FileOrFolderName, netBase, CurrentReferer(), entry.GameName + ".htm");
        AutoDownloadEnabled = true;
        RefreshAutoDownloadText(true);
        StatusText = "自动下载已开启：点游戏里的「重玩」或刷新网页就会触发";
        Log(StatusText);
        Log("原理：游戏向本地服务器要文件 → 服务器发现本地没有 → 先去网上把它下下来 → 再答复游戏（所以加载会慢一点）");
    }

    /// <summary>把服务器那边的统计抓过来显示（由下载回调与按钮触发，不依赖界面定时器）。</summary>
    public void RefreshAutoDownloadText(bool force)
    {
        var session = Server.Session;
        if (session is null)
        {
            if (force) AutoDownloadStatus = "未开始";
            return;
        }

        if (!force && !AutoDownloadEnabled) return;
        AutoDownloadStatus = session.DescribeStats();
        var text = session.Describe();
        if (text.Length > 0 || force) AutoDownloadText = text;
    }

    /// <summary>
    /// 服务器在缺文件时回调这里去网上取（赋给 <see cref="FlashWebServer.FileFetcher"/>）。
    /// 本地路径由服务器算好传进来（它才知道游戏文件夹与相对路径），这里只负责下载。
    /// </summary>
    private async Task<FlashWebServer.FetchOutcome> FetchForAutoDownloadAsync(
        FlashWebServer.AutoDownloadFetch request, CancellationToken ct)
    {
        if (request.LocalFullPath.Length == 0) return new FlashWebServer.FetchOutcome(false, false, "本地路径为空");

        var result = await _http.DownloadToFileAsync(
            request.Url, request.LocalFullPath, request.Referer, true, ct);

        // 统计变化后刷新面板（回调在后台线程，交给 Ui() 切回界面线程）
        Ui(() => RefreshAutoDownloadText(false));

        if (result.Ok) return new FlashWebServer.FetchOutcome(true, false, "");
        if (result.Status == FetchStatus.NotFound) return new FlashWebServer.FetchOutcome(false, true, result.Message);
        return new FlashWebServer.FetchOutcome(false, false, result.Message);
    }

    // ================================================================ 自由下载工具

    private async Task RunFreeDownloadAsync()
    {
        var url = (FreeUrl ?? "").Trim();
        var name = (FreeName ?? "").Trim();
        if (url.Length == 0) { FreeStatus = "请填写网址"; return; }
        if (name.Length == 0) { FreeStatus = "请填写要保存的文件名（含扩展名）"; return; }

        Cfg.LastFreeUrl = url;
        Cfg.LastFreeReferer = FreeReferer ?? "";
        Cfg.LastFreeName = name;

        Library.EnsureCreated();
        FreeStatus = "下载中…";
        IsBusy = true;
        try
        {
            var result = await _free.DownloadAsync(url, FreeReferer ?? "", Library.Root, name, CancellationToken.None);
            if (result.Ok)
            {
                FreeStatus = $"下载成功：{name}（在游戏目录里）";
                Log($"自由下载成功：{url} → {Path.Combine(Library.Root, name)}");
                RefreshGames(false);
            }
            else
            {
                FreeStatus = $"下载失败：{result.Message}";
                Log($"自由下载失败：{url} —— {result.Message}");
            }
        }
        finally { IsBusy = false; }
    }

    // ================================================================ 打开站点

    private void OpenSelectedGamePage()
    {
        var entry = SelectedGame?.Entry;
        var url = entry is null
            ? ""
            : entry.Appendix.Length > 0
                ? GamePageParser.AppendixToUrl(entry.Appendix)?.Url ?? ""
                : GameSites.HomePage(entry.Site);
        if (url.Length == 0)
        {
            StatusText = "这个游戏没有可识别的站点后缀，打不开游戏页";
            return;
        }
        OpenUrl(url);
    }

    // ================================================================ 目录监视（别的程序往里丢文件也能看到）

    /// <summary>启动一个轻量后台轮询：游戏目录有变化就刷新列表（不弹窗、不抢焦点）。</summary>
    private void StartFolderWatcher()
    {
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        _lastSeenWrite = LastWriteOf(GamesDirectory);

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2500, token);
                }
                catch (OperationCanceledException) { return; }

                var stamp = LastWriteOf(GamesDirectory);
                if (stamp <= _lastSeenWrite) continue;
                _lastSeenWrite = stamp;
                Ui(() =>
                {
                    if (IsBatchRunning || IsBusy) return; // 下载中列表由流程自己刷新，别打架
                    RefreshGames(false);
                });
            }
        }, token);
    }

    private static DateTime LastWriteOf(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return DateTime.MinValue;
            var newest = Directory.GetLastWriteTimeUtc(directory);
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var t = Directory.GetLastWriteTimeUtc(sub);
                if (t > newest) newest = t;
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var t = File.GetLastWriteTimeUtc(file);
                if (t > newest) newest = t;
            }
            return newest;
        }
        catch { return DateTime.MinValue; }
    }

    // ================================================================ 小工具

    /// <summary>弹一个错误提示（自检 / 无界面环境下只写日志，不弹窗）。</summary>
    private void ShowError(string title, string message)
    {
        Log($"[{title}] {message}");
        StatusText = message;
        if (Application.Current is null) return;
        Ui(() => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning));
    }
}

/// <summary>关联文件下载器列表里的一行。</summary>
public sealed class RelatedRow : ObservableObject
{
    private bool _isDownloaded;

    public RelatedRow(BatchDownloadEntry entry) => Entry = entry;

    /// <summary>规划结果。</summary>
    public BatchDownloadEntry Entry { get; }

    /// <summary>原始输入。</summary>
    public string Input => Entry.Input;

    /// <summary>规范化后的相对路径。</summary>
    public string RelativePath => Entry.RelativePath.Length > 0 ? Entry.RelativePath : Entry.Input;

    /// <summary>本地路径（给 tooltip 用）。</summary>
    public string LocalPath => Entry.LocalPath;

    /// <summary>这一行的状态文本。</summary>
    public string StatusText => Entry.Status switch
    {
        BatchLineStatus.Ready => _isDownloaded ? "已下载" : "待下载",
        BatchLineStatus.InvalidPath => "不规范",
        BatchLineStatus.Duplicate => "重复",
        BatchLineStatus.AlreadyExists => "已存在",
        _ => "—",
    };

    /// <summary>下载完了没有（界面上画个 ✓）。</summary>
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set
        {
            if (Set(ref _isDownloaded, value)) OnPropertyChanged(nameof(StatusText));
        }
    }

    public override string ToString() => $"{RelativePath}（{StatusText}）";
}
