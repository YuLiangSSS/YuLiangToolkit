using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.OsuMania.Core;

namespace YuLiangToolkit.Tools.OsuMania.ViewModels;

/// <summary>
/// 「osu! 等级计算器」的 ViewModel：拖入 → 后台逐个加载（串行）→ 星数 + 等级表格 → 重新计算。
///
/// <para><b>星数来源</b>：<see cref="OsuBeatmapFile.TryLoad"/> 加载时已经调用
/// <c>StarRatingCalculator.CalculateStarRating</c> 算过一次（与谱面加载时一致），所以直接读
/// <see cref="OsuManiaBeatmap.StarRating"/>；如果它是 0 / NaN（空谱或算法异常），再显式算一次兜底。
/// 等级用 <see cref="LevelCalculator.CalculateLevel(OsuManiaBeatmap)"/> 换算。</para>
///
/// <para><b>线程约定</b>：所有读取都在**同一个后台线程里 for 循环串行**做，绝不并发
/// （OsuParsers 解码器是 static 状态、非线程安全），进度经 <c>Ui(...)</c> 回到界面。</para>
/// </summary>
public sealed class OsuLevelCalculatorViewModel : ToolViewModelBase
{
    private CancellationTokenSource? _calcCts;
    private bool _isHelpVisible;
    private LevelRowItem? _selectedRow;

    public OsuLevelCalculatorViewModel() : base(OsuLevelCalculatorConfig.ToolId)
    {
        Cfg = LoadConfig<OsuLevelCalculatorConfig>();

        Rows = new ObservableCollection<LevelRowItem>();
        Rows.CollectionChanged += (_, _) => RefreshRowState();

        SelectFilesCommand = new RelayCommand(_ => SelectFiles(), _ => !IsBusy);
        ClearRowsCommand = new RelayCommand(_ => ClearRows(), _ => Rows.Count > 0 && !IsBusy);
        RecalculateCommand = new AsyncRelayCommand(_ => RecalculateAsync(), _ => Rows.Count > 0 && !IsBusy);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        ToggleHelpCommand = new RelayCommand(_ => IsHelpVisible = !IsHelpVisible);

        PropertyChanged += OnSelfPropertyChanged;

        HelpText =
            """
            拖入 .osu 文件或文件夹（递归查找），工具会逐个算出：

            · Star Rating（星数）：用 StarRating 算法（Star Rating Rebirth / srcalc 思路的 C# 重写，
              其中数学工具来自 ppy osu!lazer，MIT）。它不是 osu! 官方 mania 难度算法，
              与游戏内显示的 SR 可能有 1 星以上差异；但它是通用标级 —— 社区说的
              「xxy 标级 / xxy rating」就是这套 SR，任何键数都能用、可以直接横向比较。
            · Level（等级）：按上面这套 SR 拟合出来的换算公式
              （星数 2.7626 ~ 10.5542 走 6.9615 × e^(0.1374 × SR)，更低的走 3.6198 × SR，
              更高的走 2.791 × SR + 0.5436）。公式是按凉雨自己的 8K 谱面拟合的，
              **所以等级只对 8K 谱面有参考意义**；其它键数套出来的 Level 没有标定，看 SR 即可。

            注意：
            · 只读谱面，**不会修改或生成任何文件**。
            · 只支持 osu!mania 谱面；非 mania / 损坏 / 键数不在 1~10 的文件会在状态列标红并写明原因。
            · 星数由谱面当前的 OD / 键数 / 音符算出来，所以改了谱面 OD 之后点「重新计算」就能看到新值。
            · 自动算出的等级属于「机器标级」，可能不准（特殊谱面 / 拟合边界），必要时需要人工手动修正。
            · 拖文件夹进来会递归查找所有 .osu（包含子文件夹）。
            """;

        Log($"配置文件：{Cfg.FilePath}");
        Log("用法：拖入 .osu 或文件夹 → 自动计算星数与等级；改了谱面可点「重新计算」");
        if (IsFirstRun) Log("首次运行：已生成默认配置（Config\\osu-level-calculator\\config.json）");

        if (OsuDemoData.Enabled) SeedDemo();
    }

    // ---------- 界面状态 ----------

    public OsuLevelCalculatorConfig Cfg { get; }

    /// <summary>表格行（不持久化）。</summary>
    public ObservableCollection<LevelRowItem> Rows { get; }

    public string RowCountText => $"共 {Rows.Count} 个谱面";

    /// <summary>参数说明面板是否展开。</summary>
    public bool IsHelpVisible
    {
        get => _isHelpVisible;
        set => Set(ref _isHelpVisible, value);
    }

    /// <summary>表格当前选中的行（「打开文件夹」优先用它）。</summary>
    public LevelRowItem? SelectedRow
    {
        get => _selectedRow;
        set => Set(ref _selectedRow, value);
    }

    private string _summaryText = "还没有计算";
    /// <summary>上一次计算的统计（成功 / 失败）。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    /// <summary>参数说明文案（XAML 绑定到只读文本框）。</summary>
    public string HelpText { get; }

    // ---------- 命令 ----------

    public RelayCommand SelectFilesCommand { get; }
    public RelayCommand ClearRowsCommand { get; }
    public AsyncRelayCommand RecalculateCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ToggleHelpCommand { get; }

    // ---------- 添加文件 ----------

    /// <summary>
    /// 拖放 / 选择文件后调这里：展开（文件 + 文件夹递归）、去重、加入表格并在后台开始计算。
    /// 计算期间不接受新文件（OsuParsers 解码不能并发，保证同一时刻只有一个读取循环）。
    /// </summary>
    public void AddPaths(IEnumerable<string>? paths)
    {
        if (IsBusy)
        {
            StatusText = "正在计算，请等这一轮完成后再添加文件";
            return;
        }

        if (paths is null) return;

        var collected = OsuFileBatch.CollectOsuFiles(paths);
        if (collected.Count == 0)
        {
            StatusText = "没有找到 .osu 文件（可以拖 .osu 文件或文件夹，文件夹会递归查找）";
            Log("拖入的内容里没有 .osu 文件");
            return;
        }

        int duplicate = 0;
        var added = new List<LevelRowItem>();
        foreach (var path in collected)
        {
            if (Rows.Any(r => string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                duplicate++;
                continue;
            }

            var row = new LevelRowItem(path);
            Rows.Add(row);
            added.Add(row);
        }

        if (added.Count == 0)
        {
            StatusText = $"这 {duplicate} 个文件已经在列表里了";
            return;
        }

        Cfg.LastFolder = added[0].Folder;
        StatusText = $"已添加 {added.Count} 个文件，开始计算…" + (duplicate > 0 ? $"（去重 {duplicate} 个）" : "");
        Log($"添加 {added.Count} 个 .osu 文件" + (duplicate > 0 ? $"（去重 {duplicate} 个）" : ""));

        _ = CalculateAsync(added);
    }

    private void SelectFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 osu!mania 谱面（.osu）",
            Filter = "osu! 谱面 (*.osu)|*.osu|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(Cfg.LastFolder) ? Cfg.LastFolder : "",
        };

        if (dialog.ShowDialog() != true) return;
        AddPaths(dialog.FileNames);
    }

    private void ClearRows()
    {
        if (IsBusy) return;

        Rows.Clear();
        SelectedRow = null;
        SummaryText = "还没有计算";
        StatusText = "已清空表格";
        Log("已清空表格");
    }

    // ---------- 计算 ----------

    /// <summary>重新计算表格里的全部谱面（例如手改过谱面 OD 之后）。</summary>
    private async Task RecalculateAsync()
    {
        if (Rows.Count == 0 || IsBusy) return;

        foreach (var row in Rows)
        {
            row.Reset();
        }

        Log("重新计算全部谱面…");
        await CalculateAsync(Rows.ToList(), isRecalculate: true);
    }

    private async Task CalculateAsync(IReadOnlyList<LevelRowItem> rows, bool isRecalculate = false)
    {
        if (rows.Count == 0) return;

        _calcCts = new CancellationTokenSource();
        var token = _calcCts.Token;

        IsBusy = true;
        Progress = 0;
        ProgressText = $"0 / {rows.Count}";
        SummaryText = "计算中…";
        StatusText = $"正在计算星数 / 等级：{rows.Count} 个文件…";

        try
        {
            var progress = new UiProgress(this, rows.Count);

            // 关键：同一个后台线程里 for 循环串行读取（OsuParsers 解码器非线程安全）。
            var results = await Task.Run(() =>
            {
                var list = new List<LoadResult>(rows.Count);
                for (int i = 0; i < rows.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    list.Add(LoadOne(rows[i].FilePath));
                    progress.Report(i + 1);
                }
                return list;
            }, token);

            for (int i = 0; i < rows.Count && i < results.Count; i++)
            {
                Apply(rows[i], results[i]);
            }

            int failed = rows.Count(r => r.IsFailed);
            int ok = rows.Count - failed;

            SummaryText = $"成功 {ok} · 失败 {failed}（共 {rows.Count} 个谱面）";
            Progress = 1;
            ProgressText = $"{rows.Count} / {rows.Count}";
            StatusText = isRecalculate ? $"重新计算完成：{SummaryText}" : $"计算完成：{SummaryText}";
            Log($"===== {StatusText} =====");

            foreach (var row in rows)
            {
                Log(row.IsFailed
                    ? $"✘ 失败：{row.FileName} — {row.Error}"
                    : $"✔ {row.FileName} → SR {row.StarRatingText} / Level {row.LevelText} / {row.KeyCountText} / OD {row.OdText}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消计算";
            SummaryText = "已取消";
            Log("===== 已取消 =====");
        }
        catch (Exception ex)
        {
            StatusText = "计算失败：" + ex.Message;
            SummaryText = "计算失败";
            Log($"✘ 意外错误（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _calcCts = null;
            RefreshRowState();
        }
    }

    /// <summary>读一个谱面并算 SR / Level（纯数据，不碰 UI；在后台线程调用）。</summary>
    private static LoadResult LoadOne(string path)
    {
        if (!OsuBeatmapFile.TryLoad(path, out var file, out var error) || file is null)
        {
            return new LoadResult(false, error ?? "谱面加载失败。");
        }

        var beatmap = file.Beatmap;

        // 加载时已算过星数（OsuBeatmapFile.TryLoad 内部调用 StarRatingCalculator）；
        // 0 / NaN / 无穷大视为异常，按源码注释的兜底方式再显式算一次。
        double starRating = beatmap.StarRating;
        if (double.IsNaN(starRating) || double.IsInfinity(starRating) || starRating <= 0)
        {
            try
            {
                starRating = beatmap.CalculateStarRating();
            }
            catch
            {
                starRating = 0;
            }
        }

        double level = LevelCalculator.CalculateLevel(starRating);

        return new LoadResult(
            Ok: true,
            Error: null,
            StarRating: starRating,
            Level: level,
            OverallDifficulty: beatmap.OverallDifficulty,
            KeyCount: beatmap.KeyCount,
            Title: beatmap.Metadata.Title,
            Difficulty: beatmap.Metadata.Difficulty,
            Artist: beatmap.Metadata.Artist,
            Creator: beatmap.Metadata.Creator);
    }

    private static void Apply(LevelRowItem row, LoadResult result)
    {
        if (!result.Ok)
        {
            row.Apply(false, result.Error, 0, 0, 0, 0, "", "", "", "");
            return;
        }

        row.Apply(true, null, result.StarRating, result.Level, result.OverallDifficulty,
            result.KeyCount, result.Title, result.Difficulty, result.Artist, result.Creator);
    }

    private void Cancel()
    {
        if (_calcCts is null || _calcCts.IsCancellationRequested) return;
        _calcCts.Cancel();
        StatusText = "正在取消…";
        Log("已请求取消计算");
    }

    // ---------- 打开文件夹 ----------

    private void OpenFolder()
    {
        string? folder = SelectedRow?.Folder ?? Rows.FirstOrDefault()?.Folder;
        if (string.IsNullOrEmpty(folder) && Directory.Exists(Cfg.LastFolder))
        {
            folder = Cfg.LastFolder;
        }

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            StatusText = "还没有可打开的文件夹：先拖入谱面";
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

    // ---------- demo 数据 ----------

    /// <summary>demo 模式：生成示例谱面并同步算好（文件很小），保证无头截图能看到完整表格。</summary>
    private void SeedDemo()
    {
        var paths = OsuDemoData.EnsureDemoBeatmaps(Storage, includeInvalid: true);

        var rows = new List<LevelRowItem>();
        foreach (var path in paths)
        {
            if (Rows.Any(r => string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var row = new LevelRowItem(path);
            Rows.Add(row);
            rows.Add(row);
        }

        foreach (var row in rows)
        {
            Apply(row, LoadOne(row.FilePath));
        }

        foreach (var row in rows)
        {
            Log(row.IsFailed
                ? $"✘ 失败：{row.FileName} — {row.Error}"
                : $"✔ {row.FileName} → SR {row.StarRatingText} / Level {row.LevelText} / {row.KeyCountText} / OD {row.OdText}");
        }

        int failed = rows.Count(r => r.IsFailed);
        SummaryText = $"成功 {rows.Count - failed} · 失败 {failed}（共 {rows.Count} 个谱面）";
        Progress = 1;
        ProgressText = $"{rows.Count} / {rows.Count}";
        StatusText = $"演示数据：{Rows.Count} 个示例谱面（YLT_OSU_DEMO=1）";
        Log("演示数据：已在 Data\\osu-level-calculator\\demo\\ 生成示例谱面（含一张故意不是 mania 的失败示例）");
    }

    // ---------- 内部类型 / 小工具 ----------

    /// <summary>一个谱面的读取结果（纯数据，跨线程返回没问题）。</summary>
    private sealed record LoadResult(
        bool Ok,
        string? Error,
        double StarRating = 0,
        double Level = 0,
        double OverallDifficulty = 0,
        int KeyCount = 0,
        string Title = "",
        string Difficulty = "",
        string Artist = "",
        string Creator = "");

    /// <summary>统计文字 / 命令可用性的刷新。</summary>
    private void RefreshRowState()
    {
        OnPropertyChanged(nameof(RowCountText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy))
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>把进度报告切回 UI 线程（后台线程 Report）。</summary>
    private sealed class UiProgress : IProgress<int>
    {
        private readonly OsuLevelCalculatorViewModel _owner;
        private readonly int _total;
        private int _last;

        public UiProgress(OsuLevelCalculatorViewModel owner, int total)
        {
            _owner = owner;
            _total = total;
        }

        public void Report(int value)
        {
            _owner.Ui(() =>
            {
                if (!_owner.IsBusy || value == _last) return;
                _last = value;
                _owner.Progress = _total <= 0 ? 0 : (double)value / _total;
                _owner.ProgressText = $"{value} / {_total}";
                _owner.StatusText = $"正在计算星数 / 等级… {value} / {_total}";
            });
        }
    }

    protected override void OnClosed()
    {
        Cancel();
    }
}
