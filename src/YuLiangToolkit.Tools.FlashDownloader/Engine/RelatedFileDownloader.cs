using YuLiangToolkit.Tools.FlashDownloader.Core;

namespace YuLiangToolkit.Tools.FlashDownloader.Engine;

/// <summary>批量下载过程中的进度快照（界面 500ms 取一次就够，不用每下一行都刷）。</summary>
/// <param name="Summary">统计（总数 / 成功 / 404 / 失败…）。</param>
/// <param name="Current">正在处理的第几个（从 1 开始）。</param>
/// <param name="Total">要下载的总数。</param>
/// <param name="CurrentFile">正在下载哪一个。</param>
/// <param name="Finished">整批结束了没有。</param>
/// <param name="Cancelled">是不是被用户取消的。</param>
public sealed record BatchProgress(
    BatchDownloadSummary Summary,
    int Current,
    int Total,
    string CurrentFile,
    bool Finished,
    bool Cancelled)
{
    /// <summary>进度文本，例如「下载第 3/12 个…」。</summary>
    public string ProgressText => Finished
        ? "下载完成"
        : Cancelled ? "已取消"
        : Total == 0 ? "没有要下载的文件"
        : $"下载第 {Current}/{Total} 个…";

    /// <summary>统计文本（原始工具状态栏那一句）。</summary>
    public string Describe() => Summary.Describe(Finished ? "下载完成，" : Cancelled ? "取消成功，" : "");
}

/// <summary>
/// 关联文件批量下载：把用户在文本框里粘贴的一堆相对路径补进游戏文件夹。
/// 对应原始 Python 项目的 <c>RelatedDownloader.thread_batchDownload</c> + <c>GameDownloader.relateDownload</c>。
///
/// <para>关键在于 <c>Referer</c>：游戏站对 swf 做了防盗链，必须带上「上一级页面」才给下。</para>
/// </summary>
public sealed class RelatedFileDownloader
{
    private readonly FlashHttp _http;

    public RelatedFileDownloader(FlashHttp http) => _http = http;

    /// <summary>
    /// 下载一批文件。
    /// </summary>
    /// <param name="netBaseUrl">swf / 网页所在目录的网址（必须以 <c>/</c> 结尾）。</param>
    /// <param name="referer">上一级页面（防盗链用）。</param>
    /// <param name="localFolder">游戏文件夹（相对路径按它展开）。</param>
    /// <param name="entries">已经规划好的条目（只下载 <see cref="BatchLineStatus.Ready"/> 的）。</param>
    /// <param name="summary">统计对象（会被就地累加）。</param>
    /// <param name="maxParallel">并发数。</param>
    /// <param name="onProgress">进度回调。</param>
    public async Task<BatchDownloadSummary> RunAsync(
        string netBaseUrl,
        string referer,
        string localFolder,
        IReadOnlyList<BatchDownloadEntry> entries,
        BatchDownloadSummary summary,
        int maxParallel = 4,
        Action<BatchProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        var queue = entries.Where(e => e.Status == BatchLineStatus.Ready).ToList();
        var parallel = Math.Clamp(maxParallel, 1, 16);
        var done = 0;

        onProgress?.Invoke(new BatchProgress(summary, 0, queue.Count, "", false, false));

        if (queue.Count == 0)
        {
            onProgress?.Invoke(new BatchProgress(summary, 0, 0, "", true, false));
            return summary;
        }

        using var gate = new SemaphoreSlim(parallel);
        var tasks = queue.Select(async entry =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var url = GamePageParser.JoinBatchNetPath(netBaseUrl, entry.RelativePath);
                var status = FetchStatus.Failed;
                if (url.Length == 0)
                {
                    lock (summary) summary.Failed++;
                }
                else
                {
                    var result = await _http.DownloadToFileAsync(url, entry.LocalPath, referer, true, ct)
                        .ConfigureAwait(false);
                    status = result.Status;
                    lock (summary)
                    {
                        switch (result.Status)
                        {
                            case FetchStatus.Ok: summary.Success++; break;
                            case FetchStatus.NotFound: summary.NotFound++; break;
                            default: summary.Failed++; break;
                        }
                    }
                    if (result.Status == FetchStatus.NotFound) TryDelete(entry.LocalPath);
                }

                var index = Interlocked.Increment(ref done);
                BatchProgress snapshot;
                lock (summary)
                {
                    snapshot = new BatchProgress(summary, index, queue.Count, entry.RelativePath, false, false);
                }
                onProgress?.Invoke(snapshot);
                return status;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var cancelled = false;
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        onProgress?.Invoke(new BatchProgress(summary, done, queue.Count, "", true, cancelled));
        return summary;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}

/// <summary>
/// 「自由下载工具」：给一个网址 + 上一级网址 + 文件名，直接抓下来。
/// 用于下载非支持站点（或支持站点里没法自动解析的文件）。
/// 对应原始 Python 项目的 <c>ToolDownloader</c>。
/// </summary>
public sealed class FreeFileDownloader
{
    private readonly FlashHttp _http;

    public FreeFileDownloader(FlashHttp http) => _http = http;

    /// <summary>下载到指定目录，文件名由调用方给（含扩展名）。</summary>
    public async Task<FetchResult> DownloadAsync(
        string url,
        string referer,
        string targetDirectory,
        string fileName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return FetchResult.Failed("请填写网址");
        if (!GamePageParser.IsGoodFileName(fileName))
            return FetchResult.Failed("文件名不规范，不应出现 .. 和 ? * \" < > | :");

        string target;
        try
        {
            Directory.CreateDirectory(targetDirectory);
            target = Path.Combine(targetDirectory, fileName);
        }
        catch (Exception ex)
        {
            return FetchResult.Failed($"无法写入目标目录：{ex.Message}");
        }

        var result = await _http.DownloadToFileAsync(url.Trim(), target, referer?.Trim(), false, ct)
            .ConfigureAwait(false);
        if (result.Ok) return FetchResult.FromBytes(result.Content ?? Array.Empty<byte>());
        TryDelete(target);
        return result;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
