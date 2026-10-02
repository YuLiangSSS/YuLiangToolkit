using YuLiangToolkit.Tools.MapDownloader.Core;

namespace YuLiangToolkit.Tools.MapDownloader.ViewModels;

/// <summary>
/// 「osu! 下图器」的实际动作：换 Token / 搜索（含翻页与"获取全部"）/ 下载。
/// 界面状态在 <see cref="MapDownloaderViewModel"/> 里，这里只管流程。
/// </summary>
public sealed partial class MapDownloaderViewModel
{
    // ---------------------------------------------------------------- Token

    /// <summary>换一个 Access Token（<paramref name="announce"/> = 用户点的，要往状态栏/日志里写话）。</summary>
    private async Task GetTokenAsync(bool announce)
    {
        if (!int.TryParse(Cfg.ClientId.Trim(), out var clientId))
        {
            if (announce) StatusText = "Client ID 必须是数字（去 osu! 的 OAuth 应用页面复制）";
            return;
        }

        if (string.IsNullOrWhiteSpace(Cfg.ClientSecret))
        {
            if (announce) StatusText = "Client Secret 还没填";
            return;
        }

        var previous = IsBusy;
        IsBusy = true;
        try
        {
            if (announce)
            {
                StatusText = "正在获取 Token…";
                Log("向 osu! 换取 Access Token …");
            }

            var result = await _api.GetTokenAsync(clientId, Cfg.ClientSecret);
            if (result.Success)
            {
                Cfg.AccessToken = result.AccessToken ?? "";
                // 原版留了 60 秒余量，这里照旧
                Cfg.TokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, result.ExpiresIn - 60));
                RefreshTokenState();
                StatusText = $"✓ Token 已就绪（{result.ExpiresIn / 3600.0:F1} 小时有效）";
                Log($"✓ Token 获取成功，{Cfg.TokenExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} 到期");
            }
            else
            {
                StatusText = "✗ " + result.ErrorMessage;
                Log("✗ 换 Token 失败：" + result.ErrorMessage);
            }
        }
        finally
        {
            IsBusy = previous;
        }
    }

    private void RefreshTokenState()
    {
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(TokenStatusText));
        OnPropertyChanged(nameof(TokenIsValid));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // ---------------------------------------------------------------- 搜索

    /// <summary>搜索第一页；<paramref name="loadMore"/> = 用游标接着取下一页（原版「加载更多」）。</summary>
    private async Task SearchAsync(bool loadMore)
    {
        if (!IsAuthenticated)
        {
            StatusText = "还没有可用的 Token，先点「获取 Token」";
            return;
        }

        IsBusy = true;
        BeginOperation();
        try
        {
            if (!loadMore)
            {
                Sets.Clear();
                _cursor = "";
                RefreshSelection();
                ResultSummary = "搜索中…";
            }

            StatusText = loadMore ? "加载更多…" : "搜索中…";
            var result = await _api.SearchAsync(Cfg.AccessToken, BuildRequest(loadMore ? _cursor : null), _cts!.Token);

            if (!result.Ok || result.Value is null)
            {
                StatusText = "✗ " + result.Message;
                Log("✗ 搜索失败：" + result.Message);
                ResultSummary = Sets.Count == 0 ? "搜索失败" : ResultSummary;
                return;
            }

            var added = AddSearchResults(result.Value.BeatmapSets);
            _cursor = result.Value.CursorString ?? "";
            TotalAvailable = result.Value.Total;
            UpdateResultSummary();

            StatusText = $"✓ 本次取回 {result.Value.BeatmapSets?.Count ?? 0} 条，收进列表 {added} 条";
            Log($"搜索：取回 {result.Value.BeatmapSets?.Count ?? 0} 条（本地筛选后留下 {added} 条）· 接口共 {result.Value.Total} 条");

            if (!loadMore && added == 0 && (result.Value.BeatmapSets?.Count ?? 0) > 0)
                Log("提示：这一页全被日期 / Key 数筛掉了，放宽筛选条件再试");
        }
        finally
        {
            EndOperation();
            IsBusy = false;
        }
    }

    /// <summary>
    /// 「获取全部」：沿着 cursor 一页页取到没有下一页为止（原版就是这么干的，页间隔 200ms）。
    /// 再点一次 = 停止。
    /// </summary>
    private async Task FetchAllAsync()
    {
        if (IsFetchingAll)
        {
            Cancel();
            return;
        }

        if (!IsAuthenticated)
        {
            StatusText = "还没有可用的 Token，先点「获取 Token」";
            return;
        }

        IsBusy = true;
        IsFetchingAll = true;
        BeginOperation();
        try
        {
            // 还没搜过就先来一页，省得用户先点「搜索」再点「获取全部」
            if (Sets.Count == 0 && _cursor.Length == 0)
            {
                var first = await _api.SearchAsync(Cfg.AccessToken, BuildRequest(null), _cts!.Token);
                if (!first.Ok || first.Value is null)
                {
                    StatusText = "✗ " + first.Message;
                    Log("✗ 获取全部失败：" + first.Message);
                    return;
                }

                AddSearchResults(first.Value.BeatmapSets);
                _cursor = first.Value.CursorString ?? "";
                TotalAvailable = first.Value.Total;
                UpdateResultSummary();
            }

            var pages = 0;
            while (_cursor.Length > 0 && _cts is { IsCancellationRequested: false })
            {
                var next = await _api.SearchAsync(Cfg.AccessToken, BuildRequest(_cursor), _cts.Token);
                if (!next.Ok || next.Value is null)
                {
                    StatusText = "✗ 中断：" + next.Message;
                    Log("✗ 获取全部中断：" + next.Message);
                    break;
                }

                AddSearchResults(next.Value.BeatmapSets);
                _cursor = next.Value.CursorString ?? "";
                pages++;
                StatusText = $"已获取 {Sets.Count} / {TotalAvailable} 条…";
                UpdateResultSummary();
                await Task.Delay(200, _cts.Token);
            }

            var cancelled = _cts?.IsCancellationRequested == true;
            StatusText = cancelled ? $"已停止，共 {Sets.Count} 条" : $"✓ 全部完成，共 {Sets.Count} 条";
            Log($"获取全部：翻 {pages} 页，列表 {Sets.Count} 条{(cancelled ? "（被停止）" : "")}");
        }
        catch (OperationCanceledException)
        {
            StatusText = $"已停止，共 {Sets.Count} 条";
            Log("获取全部：已停止");
        }
        finally
        {
            IsFetchingAll = false;
            EndOperation();
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把接口返回的谱面集按本地筛选（日期 / Key 数）过一遍，收进列表并返回收了几条。
    /// （公开出来是为了让自检能直接喂假数据，不用联网。）
    /// </summary>
    public int AddSearchResults(IEnumerable<BeatmapSet>? sets)
    {
        if (sets is null) return 0;

        var from = BeatmapFilter.ParseDate(Cfg.DateFrom);
        var to = BeatmapFilter.ParseDate(Cfg.DateTo);
        var added = 0;

        foreach (var set in sets)
        {
            if (!BeatmapFilter.Passes(set, from, to, Cfg.KeyCount)) continue;

            var item = new BeatmapSetItem(set);
            // 勾选状态变了就刷新"已选 N / 共 M"和那几个下载按钮（不动界面代码，全在 VM 里）
            item.PropertyChanged += OnItemPropertyChanged;
            Sets.Add(item);
            added++;
        }

        RefreshSelection();
        UpdateResultSummary();
        return added;
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BeatmapSetItem.IsSelected)) RefreshSelection();
    }

    private void UpdateResultSummary()
        => ResultSummary = Sets.Count == 0
            ? "没有符合条件的结果"
            : $"已显示 {Sets.Count} 条" + (TotalAvailable > 0 ? $"，接口共 {TotalAvailable} 条" : "");

    private OsuSearchRequest BuildRequest(string? cursor)
    {
        // 关键词是"边打字边存"的（UpdateSourceTrigger=PropertyChanged），这里再 Trim 一次
        Cfg.LastQuery = Cfg.LastQuery.Trim();

        return new OsuSearchRequest(
            Query: string.IsNullOrWhiteSpace(Cfg.LastQuery) ? null : Cfg.LastQuery,
            Mode: OsuOptions.ToModeValue(Cfg.Mode),
            Status: string.IsNullOrWhiteSpace(Cfg.Status) ? null : Cfg.Status,
            Sort: string.IsNullOrWhiteSpace(Cfg.Sort) ? null : Cfg.Sort,
            IncludeNsfw: Cfg.IncludeNsfw,
            CursorString: string.IsNullOrWhiteSpace(cursor) ? null : cursor);
    }

    // ---------------------------------------------------------------- 下载

    /// <summary>下载；<paramref name="selectedOnly"/> = 只下勾上的，<paramref name="includeVideo"/> = 带视频。</summary>
    public async Task DownloadAsync(bool selectedOnly, bool includeVideo)
    {
        var items = (selectedOnly ? Sets.Where(s => s.IsSelected) : Sets).Select(s => s.ToOszItem()).ToList();
        if (items.Count == 0)
        {
            StatusText = selectedOnly ? "还没勾任何一行" : "列表是空的，先搜一下";
            return;
        }

        ApplyDownloadSettings();
        var folder = Cfg.EffectiveDownloadFolder;

        IsBusy = true;
        BeginOperation();
        ProgressVisible = true;
        Progress = 0;
        ProgressText = $"0 / {items.Count}";
        DownloadSummaryText = "下载中…";
        Log($"开始下载 {items.Count} 个（{(includeVideo ? "含视频 full" : "无视频 novideo")}）→ {folder}");

        try
        {
            var summary = await Downloader.DownloadAsync(
                items,
                includeVideo,
                progress => Ui(() =>
                {
                    Progress = progress.Total == 0 ? 0 : (double)progress.Finished / progress.Total;
                    ProgressText = $"{progress.Finished} / {progress.Total}";
                    DownloadSummaryText = $"成功 {progress.Downloaded} · 跳过 {progress.Skipped} · 失败 {progress.Failed}";
                }),
                _cts!.Token);

            DownloadSummaryText = summary.Describe();
            StatusText = (summary.Failed > 0 || summary.Cancelled ? "⚠ " : "✓ ") + summary.Describe();
            Log((summary.Cancelled ? "⚠ " : "✓ ") + summary.Describe());

            foreach (var error in summary.Errors) Log("  ✗ " + error);
            if (summary.Errors.Count > 0)
                Log($"（失败只列了前 {summary.Errors.Count} 条，完整情况看上面的统计）");
        }
        finally
        {
            EndOperation();
            IsBusy = false;
            ProgressVisible = false;
            Progress = 0;
            ProgressText = "";
        }
    }

    // ---------------------------------------------------------------- 取消

    private void BeginOperation()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }

    private void EndOperation()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
