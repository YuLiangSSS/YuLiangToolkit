using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.OsuMania.Core;

namespace YuLiangToolkit.Tools.OsuMania.ViewModels;

/// <summary>
/// 「Key 转换器 / LN 转换器」两个工具共用的 ViewModel 基类：文件列表 + 拖放/选择 + 批量执行 +
/// 进度 / 日志 / 取消 + 打开输出文件夹 + 参数说明开关。
///
/// <para><b>线程约定</b>：批量执行在 <see cref="Task"/> 的后台线程里跑（<c>OsuFileBatch</c> 内部串行），
/// 进度回调统一经 <see cref="ToolViewModelBase.Ui"/> 切回 UI 线程；解码 / 转换**绝不并发**
/// （OsuParsers 的解码器是 static 状态、非线程安全）。</para>
///
/// <para>子类只需要：① 建配置；② 实现 <see cref="RunBatch"/> 把参数拼成引擎 Options 再调
/// <c>OsuFileBatch.RunKeyConverter</c> / <c>RunLnTransformer</c>；③ 写参数说明文案。</para>
/// </summary>
public abstract class OsuTransformViewModelBase : ToolViewModelBase
{
    private CancellationTokenSource? _cts;
    private string? _lastOutputFolder;
    private bool _isHelpVisible;
    private readonly Queue<OsuFileItem> _probeQueue = new();
    private bool _probing;

    protected OsuTransformViewModelBase(string toolId) : base(toolId)
    {
        Files = new ObservableCollection<OsuFileItem>();
        Files.CollectionChanged += (_, _) => RefreshFileState();

        // 布局 / 进度 / 日志相关的通用状态都已由 ToolViewModelBase 备好；这里只补本工具的命令。
        SelectFilesCommand = new RelayCommand(_ => SelectFiles(), _ => !IsBusy);
        ClearFilesCommand = new RelayCommand(_ => ClearFiles(), _ => Files.Count > 0 && !IsBusy);
        StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => CanStart);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder());
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        ToggleHelpCommand = new RelayCommand(_ => IsHelpVisible = !IsHelpVisible);

        // IsBusy 不是命令参数直接读的（AsyncRelayCommand 自己防重入），但取消 / 开始按钮的可用性要立即刷新。
        PropertyChanged += OnSelfPropertyChanged;
    }

    // ---------- 界面上要绑的东西 ----------

    /// <summary>待处理的 .osu 文件列表（不持久化）。</summary>
    public ObservableCollection<OsuFileItem> Files { get; }

    /// <summary>「共 N 个」文字。</summary>
    public string FileCountText => $"共 {Files.Count} 个文件";

    /// <summary>开始按钮可用：有文件且不在忙。</summary>
    public bool CanStart => Files.Count > 0 && !IsBusy;

    /// <summary>参数说明面板是否展开。</summary>
    public bool IsHelpVisible
    {
        get => _isHelpVisible;
        set => Set(ref _isHelpVisible, value);
    }

    private string _summaryText = "还没有开始";
    /// <summary>上一次批处理的统计（"共 3 个：成功 2 · 跳过 1 · 失败 0"）。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    /// <summary>动作名（"转换"），日志与状态文字里用。</summary>
    protected string ActionName { get; set; } = "转换";

    /// <summary>参数说明（子类在构造函数里填，XAML 直接绑定到文本框）。</summary>
    public string HelpText { get; protected set; } = "";

    // ---------- 命令 ----------

    public RelayCommand SelectFilesCommand { get; }
    public RelayCommand ClearFilesCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ToggleHelpCommand { get; }

    // ---------- 文件列表 ----------

    /// <summary>
    /// 拖放 / 选择文件后调这里：用 <see cref="OsuFileBatch.CollectOsuFiles"/> 展开（文件 + 文件夹递归）
    /// 并按完整路径去重后加入列表。**同步 + 只在 UI 线程调用**（拖放回调本来就发生在 UI 线程）。
    /// </summary>
    public void AddPaths(IEnumerable<string>? paths)
    {
        if (paths is null) return;

        var collected = OsuFileBatch.CollectOsuFiles(paths);
        if (collected.Count == 0)
        {
            StatusText = "没有找到 .osu 文件（可以拖 .osu 文件或文件夹，文件夹会递归查找）";
            Log("拖入的内容里没有 .osu 文件");
            return;
        }

        int duplicate = 0;
        var added = new List<OsuFileItem>();
        foreach (var path in collected)
        {
            if (Files.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                duplicate++;
                continue;
            }

            var row = new OsuFileItem(path);
            Files.Add(row);
            added.Add(row);
        }

        if (added.Count == 0)
        {
            StatusText = $"这 {duplicate} 个文件已经在列表里了";
            return;
        }

        StatusText = $"已添加 {added.Count} 个文件，共 {Files.Count} 个";
        Log($"添加 {added.Count} 个 .osu 文件" + (duplicate > 0 ? $"（去重 {duplicate} 个）" : ""));

        // 逐个串行读键数（不并发，见类注释）
        EnqueueProbe(added);
    }

    /// <summary>清空文件列表（不删磁盘文件）。</summary>
    protected void ClearFiles()
    {
        if (IsBusy) return;

        Files.Clear();
        _lastOutputFolder = null;
        SummaryText = "还没有开始";
        StatusText = "已清空文件列表";
        Log("已清空文件列表");
    }

    private void SelectFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 osu!mania 谱面（.osu）",
            Filter = "osu! 谱面 (*.osu)|*.osu|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;
        AddPaths(dialog.FileNames);
    }

    /// <summary>
    /// 把新加入的行排进「读键数」队列。**整个进程里只允许一个读取循环**：
    /// OsuParsers 的解码器是 static 状态、非线程安全，连续两次拖入不能各起一个后台循环同时 Decode；
    /// 批处理进行中也不读（等这一批结束再读）。
    /// </summary>
    private void EnqueueProbe(IReadOnlyList<OsuFileItem> rows)
    {
        foreach (var row in rows)
        {
            _probeQueue.Enqueue(row);
        }

        if (_probing) return;
        _probing = true;
        _ = ProbeLoopAsync();
    }

    /// <summary>串行探测每个文件能不能读、多少键（后台线程逐个 <c>TryLoad</c>）。</summary>
    private async Task ProbeLoopAsync()
    {
        try
        {
            while (true)
            {
                OsuFileItem? row = null;
                lock (_probeQueue)
                {
                    if (_probeQueue.Count > 0) row = _probeQueue.Dequeue();
                }
                if (row is null) break;

                // 批处理正在跑 → 等它结束再读，避免两处同时调用 OsuParsers 解码。
                while (IsBusy)
                {
                    await Task.Delay(150);
                }

                string path = row.FilePath;

                // 串行：一次 Task.Run 只读一个文件（OsuParsers 解码器非线程安全）。
                var (keys, error) = await Task.Run(() =>
                {
                    if (!OsuBeatmapFile.TryLoad(path, out var file, out var why))
                    {
                        return (0, why);
                    }
                    return (file!.Beatmap.KeyCount, null);
                });

                if (row.State != OsuFileState.Pending)
                {
                    continue; // 已经被批处理改过的行不要覆盖状态
                }

                if (error is not null)
                {
                    row.State = OsuFileState.Failed;
                    row.StatusText = "无法读取";
                    row.Error = error;
                }
                else
                {
                    row.KeyCount = keys;
                    row.State = OsuFileState.Ready;
                    row.StatusText = "就绪";
                }
            }
        }
        finally
        {
            _probing = false;
            // 队列里如果又来了新行（拖入发生在收尾的缝隙里），再起一轮
            if (_probeQueue.Count > 0)
            {
                _probing = true;
                _ = ProbeLoopAsync();
            }
        }
    }

    /// <summary>demo 模式（<c>YLT_OSU_DEMO=1</c>）：生成合成谱面并塞进列表，保证无头截图能看到内容。</summary>
    protected void SeedDemoFiles()
    {
        var paths = OsuDemoData.EnsureDemoBeatmaps(Storage);

        foreach (var path in paths)
        {
            if (Files.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            Files.Add(new OsuFileItem(path));
        }

        // 演示数据只有两个小文件：同步读一遍，保证截图那一刻键数/状态已经显示出来
        foreach (var row in Files)
        {
            if (OsuBeatmapFile.TryLoad(row.FilePath, out var file, out var error))
            {
                row.KeyCount = file!.Beatmap.KeyCount;
                row.State = OsuFileState.Ready;
                row.StatusText = "就绪";
            }
            else
            {
                row.State = OsuFileState.Failed;
                row.StatusText = "无法读取";
                row.Error = error;
            }
        }

        StatusText = $"演示数据：{Files.Count} 个示例谱面（YLT_OSU_DEMO=1，不会自动开始）";
        Log("演示数据：已在 Data\\" + ToolId + "\\demo\\ 生成示例谱面并塞进列表");

        // 截图 / 冒烟用：YLT_OSU_AUTORUN=1 时，等界面加载完自动跑一遍，看看日志与行状态。
        if (OsuDemoData.AutoRun)
        {
            ScheduleAutoRun();
        }
    }

    /// <summary>把「自动跑一遍」排到 Dispatcher 的 Background 队列（此时 View 还没挂完，直接跑会让界面看不到过程）。</summary>
    private void ScheduleAutoRun()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(new Action(() =>
        {
            Log("YLT_OSU_AUTORUN=1：演示模式，自动开始转换一遍");
            _ = StartAsync();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------- 批量执行 ----------

    /// <summary>把当前参数拼成引擎 Options 并跑一整批（在后台线程调用）。</summary>
    protected abstract OsuBatchResult RunBatch(IReadOnlyList<string> paths, IProgress<OsuBatchProgress> progress,
        CancellationToken cancellationToken);

    /// <summary>参数自检：返回中文错误就是参数不合法（默认都合法）。</summary>
    protected virtual string? ValidateOptions() => null;

    /// <summary>子类可重写：批处理完成后的额外动作（例如统计提示）。</summary>
    protected virtual void OnBatchCompleted(OsuBatchResult result)
    {
    }

    private async Task StartAsync()
    {
        if (Files.Count == 0 || IsBusy) return;

        string? invalid = ValidateOptions();
        if (invalid is not null)
        {
            StatusText = invalid;
            Log("参数有误：" + invalid);
            return;
        }

        // 先用 CollectOsuFiles 再展开一次（列表里的文件可能已经被外部改名 / 删掉）
        var paths = OsuFileBatch.CollectOsuFiles(Files.Select(f => f.FilePath).ToList());
        if (paths.Count == 0)
        {
            StatusText = "列表里的 .osu 文件都不在了，请重新拖入";
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        Progress = 0;
        ProgressText = $"0 / {paths.Count}";
        SummaryText = "处理中…";
        ClearLog();
        Log($"===== 开始{ActionName}：{paths.Count} 个文件 =====");

        try
        {
            var progress = new UiProgress(this);
            var result = await Task.Run(() => RunBatch(paths, progress, token), token);

            ReportResult(result);
            OnBatchCompleted(result);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            SummaryText = "已取消";
            Log("===== 已取消 =====");
        }
        catch (Exception ex)
        {
            StatusText = $"{ActionName}失败：{ex.Message}";
            SummaryText = "处理失败";
            Log($"✘ 意外错误（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            RefreshFileState();
        }
    }

    /// <summary>进度回调：经 <see cref="ToolViewModelBase.Ui"/> 切回 UI 线程再改界面状态。</summary>
    private void OnBatchProgress(OsuBatchProgress p)
    {
        if (!IsBusy) return; // 收尾阶段可能还有排队中的旧回调，丢掉

        Progress = p.Total <= 0 ? 0 : (double)p.Processed / p.Total;
        ProgressText = $"{p.Processed} / {p.Total}";
        StatusText = $"正在{ActionName}：{Path.GetFileName(p.FilePath)}（{p.Processed}/{p.Total}）";
    }

    /// <summary>批处理结果 → 文件行状态 + 日志（每行都写清输出路径 / 跳过原因 / 失败原因）。</summary>
    private void ReportResult(OsuBatchResult result)
    {
        var byPath = new Dictionary<string, OsuFileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            byPath[file.FilePath] = file;
        }

        foreach (var item in result.Items)
        {
            if (byPath.TryGetValue(item.FilePath, out var row))
            {
                switch (item.Status)
                {
                    case OsuBatchItemStatus.Converted:
                        row.State = OsuFileState.Converted;
                        row.StatusText = "✔ 已转换";
                        break;
                    case OsuBatchItemStatus.Skipped:
                        row.State = OsuFileState.Skipped;
                        row.StatusText = "✘ 跳过";
                        row.Error = item.Message;
                        break;
                    case OsuBatchItemStatus.Failed:
                        row.State = OsuFileState.Failed;
                        row.StatusText = "✘ 失败";
                        row.Error = item.Message;
                        break;
                }
            }

            switch (item.Status)
            {
                case OsuBatchItemStatus.Converted:
                    Log($"✔ 已{ActionName} → {item.OutputPath}");
                    break;
                case OsuBatchItemStatus.Skipped:
                    Log($"✘ 跳过：{Path.GetFileName(item.FilePath)} — {item.Message}");
                    break;
                case OsuBatchItemStatus.Failed:
                    Log($"✘ 失败：{Path.GetFileName(item.FilePath)} — {item.Message}");
                    break;
            }
        }

        var firstOutput = result.Items.FirstOrDefault(i =>
            i.Status == OsuBatchItemStatus.Converted && !string.IsNullOrEmpty(i.OutputPath));
        _lastOutputFolder = firstOutput?.OutputPath is { Length: > 0 } path ? Path.GetDirectoryName(path) : null;

        SummaryText = result.SummaryText;
        Progress = 1;
        ProgressText = $"{result.Total} / {result.Total}";
        StatusText = result.SummaryText;
        Log($"===== {result.SummaryText} =====");
    }

    private void Cancel()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        StatusText = "正在取消…（当前文件处理完就停）";
        Log("已请求取消");
    }

    // ---------- 打开输出文件夹 ----------

    /// <summary>打开输出文件夹：优先用这次批处理第一个输出文件的目录，没有输出时退到第一个源文件目录。</summary>
    private void OpenOutputFolder()
    {
        string? folder = _lastOutputFolder;
        if (string.IsNullOrEmpty(folder))
        {
            folder = Files.FirstOrDefault()?.Folder;
        }

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            StatusText = "还没有可打开的文件夹：先添加文件并开始转换";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打不开文件夹：" + ex.Message;
        }
    }

    // ---------- 基类内部 ----------

    private void RefreshFileState()
    {
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(CanStart));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(CanStart));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>把 <see cref="IProgress{T}"/> 包装成“一定回到 UI 线程”的回调（OsuFileBatch 在后台线程 Report）。</summary>
    private sealed class UiProgress : IProgress<OsuBatchProgress>
    {
        private readonly OsuTransformViewModelBase _owner;

        public UiProgress(OsuTransformViewModelBase owner) => _owner = owner;

        public void Report(OsuBatchProgress value) => _owner.Ui(() => _owner.OnBatchProgress(value));
    }

    protected override void OnClosed()
    {
        Cancel();
    }
}
