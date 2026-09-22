using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Sample.ViewModels;

/// <summary>
/// 示例功能的 ViewModel —— <b>新功能照这个文件抄即可</b>。
/// 演示了三件事：
/// <list type="number">
///   <item>选项即时持久化：强类型 <see cref="SampleConfig"/>（Checkbox / Textbox / 下拉框 / 滑块）与
///         动态 <see cref="DynamicToolConfig"/>（不建类也能存）；</item>
///   <item>功能数据文件夹：<c>Data\sample-framework\</c> 的列目录 / 导入 / 写入 / 删除；</item>
///   <item>通用交互：忙碌状态、进度、日志、取消、跨线程调度（都来自 <see cref="ToolViewModelBase"/>）。</item>
/// </list>
/// </summary>
public sealed class SampleViewModel : ToolViewModelBase
{
    private CancellationTokenSource? _cts;
    private StorageFile? _selectedFile;

    public SampleViewModel() : base("sample-framework") // Id 即 Data/Config 下的小文件夹名
    {
        Cfg = LoadConfig<SampleConfig>();   // → Config\sample-framework\config.json（改一下就存）
        Dyn = LoadDynamicConfigWithDefaults(new Dictionary<string, object?>  // 声明过的键会补默认值并落盘
        {
            ["quickFlag"] = false,          // 键缺失时；绑 CheckBox 第一次点击才不会“没反应”
            ["quickNote"] = "",
        });                                 // → Config\sample-framework\options.json

        RefreshFilesCommand = new RelayCommand(_ => RefreshFiles());
        ImportFileCommand = new RelayCommand(_ => ImportFile());
        GenerateFileCommand = new RelayCommand(_ => GenerateFile());
        OpenDataFolderCommand = new RelayCommand(_ => Storage.OpenInExplorer());
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());
        RevealSelectedCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedFile is not null);
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected(), _ => SelectedFile is not null);
        SimulateTaskCommand = new AsyncRelayCommand(_ => SimulateTaskAsync());
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);

        // 配置一变就刷新 JSON 预览：直观看到“界面改一下 → 配置立刻变了”。
        // Saved 事件在后台保存完成后触发（自动属性也能收到），所以预览始终和磁盘一致。
        Cfg.PropertyChanged += OnConfigChanged;    // 需要联动的计算属性走这条
        Dyn.PropertyChanged += OnConfigChanged;
        ConfigStore.Saved += (_, _) => Ui(UpdatePreview);

        SeedSampleFileIfEmpty();
        RefreshFiles();
        UpdatePreview();

        Log($"数据目录：{Storage.Directory}");
        Log($"配置文件：{Cfg.FilePath}、{Dyn.FilePath}");
        if (IsFirstRun) Log("首次运行：已生成默认配置文件（可放心手改，改坏了会自动备份并回退默认值）");
    }

    // ---------- 配置 ----------

    /// <summary>强类型配置（推荐的写法）。</summary>
    public SampleConfig Cfg { get; }

    /// <summary>动态键值配置（不想建类时用；XAML 里 <c>{Binding Dyn[键名]}</c> 直接绑定）。</summary>
    public DynamicToolConfig Dyn { get; }

    private string _configPreview = "";
    /// <summary>当前两份配置文件的 JSON 实时预览（证明选项是即时落盘的）。</summary>
    public string ConfigPreview
    {
        get => _configPreview;
        private set => Set(ref _configPreview, value);
    }

    /// <summary>下拉框选项：Value 才是存进配置的值，Label 只是显示文本。</summary>
    public IReadOnlyList<OptionItem<string>> ModeOptions { get; } =
        OptionItem.From(("标准", "标准"), ("高速", "高速（跳过校验）"), ("极限", "极限压缩"));

    /// <summary>把 <see cref="OptionItem{T}"/> 与配置里的值对上的桥接属性。</summary>
    public OptionItem<string> SelectedMode
    {
        get => OptionItem<string>.Find(ModeOptions, Cfg.Mode) ?? ModeOptions[0];
        set
        {
            if (value is null) return;
            Cfg.Mode = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>配置类里 Mode 变了要让下拉框也跟着刷新。</summary>
    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SampleConfig.Mode)) OnPropertyChanged(nameof(SelectedMode));
        UpdatePreview();
    }

    private void UpdatePreview()
        => ConfigPreview =
            $"// {Cfg.FilePath}{Environment.NewLine}{Cfg.ToJson()}"
            + $"{Environment.NewLine}{Environment.NewLine}// {Dyn.FilePath}{Environment.NewLine}{Dyn.ToJson()}";

    // ---------- 数据文件夹 ----------

    /// <summary><c>Data\sample-framework\</c> 里的文件列表。</summary>
    public ObservableCollection<StorageFile> Files { get; } = new();

    public StorageFile? SelectedFile
    {
        get => _selectedFile;
        set => Set(ref _selectedFile, value);
    }

    private string _dataSummary = "";
    public string DataSummary
    {
        get => _dataSummary;
        private set => Set(ref _dataSummary, value);
    }

    // ---------- 命令 ----------

    public RelayCommand RefreshFilesCommand { get; }
    public RelayCommand ImportFileCommand { get; }
    public RelayCommand GenerateFileCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand RevealSelectedCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public AsyncRelayCommand SimulateTaskCommand { get; }
    public RelayCommand CancelCommand { get; }

    // ---------- 实现 ----------

    private void RefreshFiles()
    {
        Files.Clear();
        foreach (var file in Storage.List()) Files.Add(file);
        DataSummary = $"{Files.Count} 个文件 · 共 {FileSize.Format(Storage.TotalSize())}";
    }

    private void ImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要放进 Data 文件夹的文件",
            Filter = "所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        var imported = Storage.Import(dialog.FileName); // 重名自动加 (1)
        if (imported is null)
        {
            StatusText = "导入失败（文件可能被占用或没有写入权限）";
            return;
        }

        RefreshFiles();
        SelectedFile = Files.FirstOrDefault(f => f.RelativePath == imported.RelativePath);
        StatusText = $"已导入：{imported.RelativePath}（{imported.SizeText}）";
        Log($"导入 {dialog.FileName} → {imported.FullPath}");
    }

    private void GenerateFile()
    {
        var name = $"生成的数据-{DateTime.Now:HHmmss}.txt";
        var content = $"""
            这是「框架示例」写进 Data 文件夹的内容。
            时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}
            备注（来自 Config 的 Note 选项）：{Cfg.Note}
            """;
        var path = Storage.WriteText(name, content);
        if (path is null) { StatusText = "写入失败"; return; }

        RefreshFiles();
        StatusText = $"已生成 {name}";
        Log($"写入 {path}");
    }

    private void RevealSelected()
    {
        if (SelectedFile is null) return;
        Storage.RevealInExplorer(SelectedFile.RelativePath);
    }

    private void DeleteSelected()
    {
        if (SelectedFile is null) return;
        var confirm = MessageBox.Show(
            $"确定从数据文件夹删除「{SelectedFile.Name}」？", "删除确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var relative = SelectedFile.RelativePath;
        if (Storage.Delete(relative))
        {
            StatusText = $"已删除 {relative}";
            Log($"删除 {relative}");
        }
        else
        {
            StatusText = "删除失败";
        }
        SelectedFile = null;
        RefreshFiles();
    }

    /// <summary>演示耗时任务的正确写法：IsBusy / Progress / Log / 取消，全程不阻塞界面。</summary>
    private async Task SimulateTaskAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Progress = 0;
        Log($"===== 模拟任务开始（重复 {Math.Clamp(Cfg.Repeat, 1, 20)} 次，模式 {Cfg.Mode}）=====");

        try
        {
            var total = Math.Clamp(Cfg.Repeat, 1, 20);
            for (var i = 1; i <= total; i++)
            {
                token.ThrowIfCancellationRequested();

                // 后台线程干活；Log/StatusText 等界面状态由基类 Ui() 自动切回 UI 线程
                await Task.Run(() => Thread.Sleep(250), token);

                Progress = (double)i / total;
                ProgressText = $"{i} / {total}";
                StatusText = $"处理中 {i}/{total} · 阈值 {Cfg.Threshold:0.00}";
                Log($"第 {i} 步完成");
            }
            StatusText = "模拟任务完成";
            Log("===== 模拟任务完成 =====");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            Log("===== 已取消 =====");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            Progress = 0;
        }
    }

    /// <summary>首次运行（数据文件夹还空着）时放一个示例文件，方便看到列表效果。</summary>
    private void SeedSampleFileIfEmpty()
    {
        if (Storage.List().Count > 0) return;
        Storage.WriteText("示例数据.txt", $"""
            Data 文件夹示例文件（可直接删除）。
            这个文件由「框架示例」工具在首次运行时写入：{DateTime.Now:yyyy-MM-dd HH:mm:ss}

            约定：每个功能独占一个以「工具 Id」命名的小文件夹，
            例如本工具的文件就在 Data\sample-framework\ 下。
            想在代码里读写它，用 ToolStorage.For("sample-framework") 即可（见 代码记录.md）。
            """);
    }
}
