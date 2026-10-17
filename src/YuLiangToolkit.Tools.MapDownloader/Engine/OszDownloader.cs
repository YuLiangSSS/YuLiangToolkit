using System.Net.Http;
using YuLiangToolkit.Tools.MapDownloader.Core;

namespace YuLiangToolkit.Tools.MapDownloader.Engine;

/// <summary>下载进度快照（界面直接绑）。</summary>
public readonly record struct OszProgress(int Finished, int Total, int Downloaded, int Skipped, int Failed);

/// <summary>
/// 一批下载的结果。原版只报了"成功 / 失败"，这里把<b>跳过（已存在）</b>单独拆出来 ——
/// 原版把它算进"成功"，于是"再点一次下载"看起来像重新下了一遍。
/// </summary>
public sealed record OszDownloadSummary(
    int Total, int Downloaded, int Skipped, int Failed, bool Cancelled, IReadOnlyList<string> Errors)
{
    /// <summary>真正动过手（成功 + 跳过 + 失败）的条数；取消时它会小于 <see cref="Total"/>。</summary>
    public int Attempted => Downloaded + Skipped + Failed;

    public string Describe()
    {
        var text = $"成功 {Downloaded} · 跳过（已存在）{Skipped} · 失败 {Failed} · 共 {Total}";
        return Cancelled ? "已取消：" + text : text;
    }
}

/// <summary>
/// <c>.osz</c> 并发下载器（原版 <c>BeatmapDownloader</c> 的移植）。
///
/// <para>行为与原版一致：并发 <see cref="Concurrency"/> 个、已存在就跳过、命名 <c>{Id} {标题}.osz</c>、
/// 带上 UA / Accept / Referer、5 分钟超时。三处<b>故意改掉</b>的地方：
/// ① 先下到 <c>xxx.osz.part</c> 再改名落地 —— 原版直接写目标文件，中途断线会在磁盘上留下半个
/// <c>.osz</c>，下次还会被"已存在"跳过，等于永久坏文件；
/// ② 取消不再往外抛异常（原版会把 <c>TaskCanceledException</c> 抛进 async void 事件处理器里）；
/// ③ 跳过单独统计（见 <see cref="OszDownloadSummary"/>）。</para>
/// </summary>
public sealed class OszDownloader
{
    /// <summary>原版写死的并发数。</summary>
    public const int DefaultConcurrency = 5;

    private const int MaxErrorMessages = 20;

    /// <summary>镜像根地址（自检把它指向本地假镜像）。</summary>
    public string MirrorBase { get; set; } = OszNaming.DefaultMirrorBase;

    /// <summary>落地目录。</summary>
    public string DownloadFolder { get; set; } = "";

    /// <summary>并发数（小于 1 按 1 算）。</summary>
    public int Concurrency { get; set; } = DefaultConcurrency;

    /// <summary>单个文件的最大下载时间。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>下载一批；<paramref name="includeVideo"/> = 带视频（<c>full</c>）还是不带（<c>novideo</c>）。</summary>
    public async Task<OszDownloadSummary> DownloadAsync(
        IReadOnlyList<OszItem> items,
        bool includeVideo,
        Action<OszProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        var folder = DownloadFolder;
        var errors = new List<string>();
        var counter = new Counter(items.Count, errors, onProgress);

        if (items.Count == 0)
            return new OszDownloadSummary(0, 0, 0, 0, false, errors);

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            counter.Fail($"建不了下载目录 {folder}：{ex.Message}");
            return counter.ToSummary(cancelled: false);
        }

        using var http = CreateClient();
        using var gate = new SemaphoreSlim(Math.Max(1, Concurrency));
        var running = new List<Task>(items.Count);
        var cancelled = false;

        try
        {
            foreach (var item in items)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                running.Add(RunOneAsync(http, gate, item, includeVideo, folder, counter, ct));
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        // 已经开跑的那几个会把 .part 收拾干净再退出（RunOneAsync 内部不抛）
        await Task.WhenAll(running).ConfigureAwait(false);

        return counter.ToSummary(cancelled || ct.IsCancellationRequested);
    }

    private HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream, */*;q=0.8");
        client.DefaultRequestHeaders.Referrer = new Uri("https://osu.ppy.sh/");
        return client;
    }

    private async Task RunOneAsync(
        HttpClient http, SemaphoreSlim gate, OszItem item, bool includeVideo,
        string folder, Counter counter, CancellationToken ct)
    {
        var part = Path.Combine(folder, OszNaming.FileName(item) + ".part");

        try
        {
            ct.ThrowIfCancellationRequested();

            var target = Path.Combine(folder, OszNaming.FileName(item));
            if (File.Exists(target))
            {
                counter.Skip();
                return;
            }

            var url = OszNaming.BuildUrl(item.SetId, includeVideo, MirrorBase);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                counter.Fail($"{(int)response.StatusCode} {url}");
                return;
            }

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                await source.CopyToAsync(file, 8192, ct).ConfigureAwait(false);
            }

            File.Move(part, target, overwrite: true);
            counter.Done();
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            counter.Cancel();
        }
        catch (Exception ex)
        {
            TryDelete(part);
            counter.Fail(OsuHttp.Describe(ex));
        }
        finally
        {
            gate.Release();
            counter.Report();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就算了 */ }
    }

    /// <summary>线程安全的计数 + 进度回调（下载是并发跑的）。</summary>
    private sealed class Counter(int total, List<string> errors, Action<OszProgress>? onProgress)
    {
        private readonly object _lock = new();
        private int _downloaded, _skipped, _failed, _cancelled, _finished;

        public void Done() => Interlocked.Increment(ref _downloaded);
        public void Skip() => Interlocked.Increment(ref _skipped);
        public void Cancel() => Interlocked.Increment(ref _cancelled);

        public void Fail(string message)
        {
            Interlocked.Increment(ref _failed);
            lock (_lock)
            {
                if (errors.Count < MaxErrorMessages) errors.Add(message);
            }
        }

        /// <summary>
        /// 报一次进度。**计数、取快照、回调必须在同一把锁里**：否则两个下载线程会交错
        /// （A 先自增、B 自增完先回调），外面收到的最后一次回调可能是 Finished=2 的旧快照 ——
        /// 进度看起来"退回去"了，自检也随机挂（这条踩过）。
        /// </summary>
        public void Report()
        {
            lock (_lock)
            {
                _finished++;
                onProgress?.Invoke(new OszProgress(_finished, total, _downloaded, _skipped, _failed));
            }
        }

        public OszDownloadSummary ToSummary(bool cancelled) => new(
            total, _downloaded, _skipped, _failed, cancelled || _cancelled > 0, errors);
    }
}
