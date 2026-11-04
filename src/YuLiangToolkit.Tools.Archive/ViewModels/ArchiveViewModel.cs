using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Archive;

namespace YuLiangToolkit.Tools.Archive.ViewModels;

/// <summary>解压目标：压缩包旁边（默认）/ 指定目录。</summary>
public enum ArchiveExtractTarget
{
    /// <summary>解压到压缩包所在目录（每个包可再套一层同名子文件夹）。</summary>
    Alongside,

    /// <summary>解压到界面里指定的固定目录。</summary>
    Custom,
}

/// <summary>压缩包输出位置：源旁边（默认）/ 指定目录。</summary>
public enum ArchiveOutputTarget
{
    /// <summary>输出到第一个源文件 / 文件夹旁边。</summary>
    Alongside,

    /// <summary>输出到界面里指定的固定目录。</summary>
    Custom,
}

/// <summary>
/// 「压缩包工具」列表里的一个待解压压缩包。
/// <para>添加后由 ViewModel 在后台线程调 <see cref="ArchiveEngine.Probe"/> 补齐格式 / 内容 / 加密状态，
/// 探测失败只在「状态」列显示红字并写日志，不会弹窗、也不会自动把这一项删掉。</para>
/// </summary>
public sealed class ArchiveListItem : ObservableObject
{
    private string _formatText = "";
    private string _contentText = "读取中…";
    private string _statusText = "读取中…";
    private bool _hasError;
    private string _error = "";

    public ArchiveListItem(string fullPath)
    {
        FullPath = fullPath;
        var fileName = Path.GetFileName(fullPath);
        FileName = string.IsNullOrEmpty(fileName) ? fullPath : fileName;
        _formatText = Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant();

        try { SizeBytes = new FileInfo(fullPath).Length; }
        catch { SizeBytes = 0; }
    }

    /// <summary>压缩包完整路径。</summary>
    public string FullPath { get; }

    /// <summary>界面显示的文件名。</summary>
    public string FileName { get; }

    /// <summary>压缩包文件本身的字节数（用于汇总）。</summary>
    public long SizeBytes { get; }

    /// <summary>格式列（探测前先用扩展名兜底）。</summary>
    public string FormatText
    {
        get => _formatText;
        set => Set(ref _formatText, value);
    }

    /// <summary>内容列：<c>2 个文件 · 123 B · 🔒 加密</c>。</summary>
    public string ContentText
    {
        get => _contentText;
        set => Set(ref _contentText, value);
    }

    /// <summary>状态列：读取中… / 就绪 / 需要密码 / 读取失败 / 已解压 …</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    /// <summary>状态是否为错误（界面据此把状态文字显示成红字）。</summary>
    public bool HasError
    {
        get => _hasError;
        set => Set(ref _hasError, value);
    }

    /// <summary>最近一次失败原因（鼠标悬停时和路径一起显示）。</summary>
    public string Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value)) OnPropertyChanged(nameof(ToolTipText));
        }
    }

    /// <summary>悬停提示：完整路径 + 失败原因。</summary>
    public string ToolTipText => string.IsNullOrWhiteSpace(Error) ? FullPath : FullPath + Environment.NewLine + Error;
}

/// <summary>
/// 「压缩包工具」压缩列表里的一个源（文件或文件夹）。
/// 文件夹的大小在后台用 <see cref="ArchiveEngine.MeasureSources"/> 统计（大文件夹不会卡界面）。
/// </summary>
public sealed class ArchiveSourceItem : ObservableObject
{
    private long _sizeBytes;
    private string _sizeText = "—";
    private string _note = "";
    private bool _isMeasuring;

    public ArchiveSourceItem(string fullPath)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(Name)) Name = fullPath;

        IsDirectory = Directory.Exists(fullPath);
        if (IsDirectory)
        {
            _sizeText = "统计中…";
            _note = "文件夹";
            _isMeasuring = true;
            return;
        }

        try { _sizeBytes = new FileInfo(fullPath).Length; }
        catch { _sizeBytes = 0; }
        _sizeText = FileSize.Format(_sizeBytes);
        _note = "文件";
    }

    /// <summary>文件 / 文件夹完整路径。</summary>
    public string FullPath { get; }

    /// <summary>界面显示名称。</summary>
    public string Name { get; }

    /// <summary>是不是文件夹（决定要不要递归统计大小、以及压缩时保留哪一层）。</summary>
    public bool IsDirectory { get; }

    /// <summary>统计到的字节数（文件 = 自身大小；文件夹 = 递归总和）。</summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set => Set(ref _sizeBytes, value);
    }

    /// <summary>大小列文本。</summary>
    public string SizeText
    {
        get => _sizeText;
        set => Set(ref _sizeText, value);
    }

    /// <summary>备注列：文件 / <c>文件夹 · 12 个文件</c>。</summary>
    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    /// <summary>是否还在后台统计文件夹大小（汇总文字据此提示「统计中」）。</summary>
    public bool IsMeasuring
    {
        get => _isMeasuring;
        set => Set(ref _isMeasuring, value);
    }
}

/// <summary>
/// 「压缩包工具」的 ViewModel：解压列表 / 压缩列表 / 任务进度与日志。
///
/// <para>职责划分：界面只做拖放、密码框同步、多选同步（见 <c>ArchiveView.xaml.cs</c>），
/// 所有业务（添加、探测、解压、压缩、取消、打开输出位置）都在这里。</para>
///
/// <para>线程约定：耗时活（Probe / Extract / Create / MeasureSources）全部 <see cref="Task.Run(Action)"/>
/// 丢到后台，进度回调与后台结果一律用 <see cref="ToolViewModelBase.Ui"/> 切回 UI 线程再改界面状态。</para>
/// </summary>
public sealed class ArchiveViewModel : ToolViewModelBase, IFileLaunchTarget
{
    /// <summary>引擎不可用时（理论上不会）给文件对话框兜底的过滤器。</summary>
    private const string FallbackArchiveFilter =
        "所有支持的压缩包|*.zip;*.7z;*.rar;*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz2;*.tar.xz;*.txz;*.tar.zst;*.gz;*.bz2;*.xz;*.zst;*.ace;*.arc;*.arj;*.lzw|所有文件|*.*";

    /// <summary>扩展名兜底表：拖入时不必为每个文件读文件头（大量文件时快很多）。</summary>
    private static readonly string[] KnownArchiveExtensions =
    {
        ".zip", ".7z", ".rar", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz",
        ".tar.zst", ".tar.lz", ".gz", ".bz2", ".xz", ".zst", ".ace", ".arc", ".arj", ".lzw",
    };

    private readonly DispatcherTimer _reprobeTimer;
    private readonly List<ArchiveListItem> _selectedArchives = new();
    private readonly List<ArchiveSourceItem> _selectedSources = new();

    private CancellationTokenSource? _cts;
    private string? _lastOutputPath;
    private int _taskGeneration;

    private string _password = "";
    private bool _showPassword;
    private string _encodingKey = "auto";

    private bool _isProgressIndeterminate;
    private bool _hasArchives;
    private string _archiveSummary = "还没有添加压缩包";
    private bool _hasSources;
    private string _sourceSummary = "还没有添加文件或文件夹";

    static ArchiveViewModel()
    {
        // .NET Core 默认不带 GBK / GB18030，旧版压缩包文件名要靠代码页（引擎里也注册了，这里再兜一次底）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ArchiveViewModel() : base("archive")
    {
        Cfg = LoadConfig<ArchiveConfig>();

        // 密码 / 旧版编码变化后，稍等一下再统一重新探测（用户连续敲密码时不要每敲一个字就读一遍压缩包）
        _reprobeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(650),
        };
        _reprobeTimer.Tick += OnReprobeTimerTick;

        AddArchivesCommand = new RelayCommand(_ => AddArchivesViaDialog(), _ => !IsBusy);
        RemoveSelectedArchivesCommand = new RelayCommand(_ => RemoveSelectedArchives(), _ => !IsBusy && _selectedArchives.Count > 0);
        ClearArchivesCommand = new RelayCommand(_ => ClearArchives(), _ => !IsBusy && Archives.Count > 0);
        StartExtractCommand = new AsyncRelayCommand(_ => ExtractAllAsync(), _ => !IsBusy && Archives.Count > 0);
        BrowseExtractDirectoryCommand = new RelayCommand(_ => BrowseExtractDirectory(), _ => !IsBusy);

        AddFilesCommand = new RelayCommand(_ => AddFilesViaDialog(), _ => !IsBusy);
        AddFolderCommand = new RelayCommand(_ => AddFolderViaDialog(), _ => !IsBusy);
        RemoveSelectedSourcesCommand = new RelayCommand(_ => RemoveSelectedSources(), _ => !IsBusy && _selectedSources.Count > 0);
        ClearSourcesCommand = new RelayCommand(_ => ClearSources(), _ => !IsBusy && Sources.Count > 0);
        StartCompressCommand = new AsyncRelayCommand(_ => CreateArchiveAsync(), _ => !IsBusy && Sources.Count > 0);
        BrowseOutputDirectoryCommand = new RelayCommand(_ => BrowseOutputDirectory(), _ => !IsBusy);

        CancelCommand = new RelayCommand(_ => CancelRunningTask(), _ => IsBusy);
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder(), _ => !IsBusy && HasOutputLocation);
        ClearLogCommand = new RelayCommand(_ => { ClearLog(); CommandManager.InvalidateRequerySuggested(); }, _ => LogText.Length > 0);

        // 重建配置时框架会发一次“所有属性都变了”的空通知（自动属性平时不发通知），这里把联动属性一起刷新。
        Cfg.PropertyChanged += OnConfigPropertyChanged;

        UpdateArchiveSummary();
        UpdateSourceSummary();
        UpdateOutputPreview();

        Log("拖入压缩包 → 解压列表；拖入文件 / 文件夹 → 压缩列表（RAR 只能解，不能压）。");
        Log("支持解压：ZIP / RAR / 7Z / TAR / TAR.GZ / TAR.BZ2 / TAR.XZ / TAR.ZST / GZ / BZ2 / XZ / ZST 等；创建：ZIP、7Z。");
        if (IsFirstRun) Log("首次运行：已生成默认配置（改一下自动保存；密码不会写进配置文件）。");
    }

    // =====================================================================
    //  配置 / 选项
    // =====================================================================

    /// <summary>强类型配置（<c>Config\archive\config.json</c>，改一下自动保存）。</summary>
    public ArchiveConfig Cfg { get; }

    /// <summary>解压密码（只放内存，不写配置；界面用 PasswordBox 输入，「显示」勾选后可明文核对）。</summary>
    public string Password
    {
        get => _password;
        set
        {
            if (!Set(ref _password, value ?? "")) return;
            ScheduleArchiveReprobe();
        }
    }

    /// <summary>由界面 PasswordBox 同步过来（和 <see cref="Password"/> 等价，只是名字更明确）。</summary>
    public void SetPassword(string? value) => Password = value ?? "";

    /// <summary>密码框是否显示明文（勾选「显示」时用 TextBox 代替 PasswordBox）。</summary>
    public bool ShowPassword
    {
        get => _showPassword;
        set => Set(ref _showPassword, value);
    }

    /// <summary>进度是否为「不确定」（引擎拿不到总量时 <see cref="ArchiveProgress.Percent"/> 返回 -1）。</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => Set(ref _isProgressIndeterminate, value);
    }

    // ---------- 解压目标 ----------

    public IReadOnlyList<OptionItem<ArchiveExtractTarget>> ExtractTargetOptions { get; } = OptionItem.From(
        (ArchiveExtractTarget.Alongside, "压缩包旁边（默认）"),
        (ArchiveExtractTarget.Custom, "指定目录"));

    /// <summary>「解压到」下拉框（值存在 <see cref="ArchiveConfig.ExtractDirectory"/>：空 = 压缩包旁边）。</summary>
    public OptionItem<ArchiveExtractTarget> SelectedExtractTarget
    {
        get => OptionItem<ArchiveExtractTarget>.Find(ExtractTargetOptions, CurrentExtractTarget) ?? ExtractTargetOptions[0];
        set
        {
            if (value is null) return;
            if (value.Value == CurrentExtractTarget)
            {
                OnPropertyChanged();
                return;
            }

            if (value.Value == ArchiveExtractTarget.Alongside)
            {
                ExtractDirectoryText = "";
            }
            else
            {
                var initial = CleanPath(Cfg.LastExtractDirectory);
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) ExtractDirectoryText = initial;
                else BrowseExtractDirectory();
                if (string.IsNullOrWhiteSpace(Cfg.ExtractDirectory))
                {
                    // 用户取消了选目录 → 退回「压缩包旁边」，不留一个空的自定义目标
                    OnPropertyChanged();
                    return;
                }
            }
            OnPropertyChanged();
        }
    }

    /// <summary>解压目录输入框的绑定桥（写法绕一层是为了在「清空路径」时把下拉框同步回「压缩包旁边」）。</summary>
    public string ExtractDirectoryText
    {
        get => Cfg.ExtractDirectory;
        set
        {
            var text = value ?? "";
            if (string.Equals(Cfg.ExtractDirectory, text, StringComparison.Ordinal)) return;
            Cfg.ExtractDirectory = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomExtractDirectory));
            OnPropertyChanged(nameof(SelectedExtractTarget));
        }
    }

    /// <summary>是否用了「指定目录」（决定路径输入框显不显示）。</summary>
    public bool IsCustomExtractDirectory => !string.IsNullOrWhiteSpace(Cfg.ExtractDirectory);

    private ArchiveExtractTarget CurrentExtractTarget =>
        string.IsNullOrWhiteSpace(Cfg.ExtractDirectory) ? ArchiveExtractTarget.Alongside : ArchiveExtractTarget.Custom;

    // ---------- 覆盖策略 ----------

    public IReadOnlyList<OptionItem<OverwriteMode>> OverwriteOptions { get; } = OptionItem.From(
        (OverwriteMode.Rename, "自动改名（推荐，不覆盖）"),
        (OverwriteMode.Skip, "跳过已存在的文件"),
        (OverwriteMode.Overwrite, "直接覆盖（小心）"));

    /// <summary>同名文件处理方式下拉框。</summary>
    public OptionItem<OverwriteMode> SelectedOverwrite
    {
        get => OptionItem<OverwriteMode>.Find(OverwriteOptions, Cfg.Overwrite) ?? OverwriteOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.Overwrite) return;
            Cfg.Overwrite = value.Value;
            OnPropertyChanged();
        }
    }

    // ---------- 旧版文件名编码 ----------

    public IReadOnlyList<OptionItem<string>> LegacyEncodingOptions { get; } = OptionItem.From(
        ("auto", "自动（推荐）"),
        ("gbk", "GBK（旧版中文压缩包）"),
        ("utf8", "UTF-8"));

    /// <summary>
    /// 解压时用的旧版文件名编码（自动 = null / GBK 936 / UTF-8）。
    /// <para>不写进配置：换个压缩包可能就要换编码，不作为长期偏好；选中后会重新探测列表。</para>
    /// </summary>
    public OptionItem<string> SelectedLegacyEncoding
    {
        get => OptionItem<string>.Find(LegacyEncodingOptions, _encodingKey) ?? LegacyEncodingOptions[0];
        set
        {
            if (value is null || _encodingKey == value.Value) return;
            _encodingKey = value.Value;
            OnPropertyChanged();
            ScheduleArchiveReprobe();
        }
    }

    // ---------- 压缩设置 ----------

    public IReadOnlyList<OptionItem<ArchiveFormat>> CreateFormatOptions { get; } = OptionItem.From(
        (ArchiveFormat.Zip, "ZIP（.zip，最通用）"),
        (ArchiveFormat.SevenZip, "7Z（.7z，压缩率更高）"));

    /// <summary>压缩格式（只有 ZIP / 7Z 能创建；RAR 受版权限制只能解）。</summary>
    public OptionItem<ArchiveFormat> SelectedCreateFormat
    {
        get => OptionItem<ArchiveFormat>.Find(CreateFormatOptions, Cfg.CreateFormat) ?? CreateFormatOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.CreateFormat) return;
            Cfg.CreateFormat = value.Value;
            OnPropertyChanged();
            UpdateOutputPreview();
        }
    }

    public IReadOnlyList<OptionItem<CompressionPreset>> CompressionLevelOptions { get; } = OptionItem.From(
        (CompressionPreset.Store, "仅存储（不压缩，最快）"),
        (CompressionPreset.Fastest, "最快"),
        (CompressionPreset.Normal, "标准（推荐）"),
        (CompressionPreset.Maximum, "极限（最慢，最小）"));

    /// <summary>压缩级别下拉框。</summary>
    public OptionItem<CompressionPreset> SelectedCompressionLevel
    {
        get => OptionItem<CompressionPreset>.Find(CompressionLevelOptions, Cfg.CompressionLevel) ?? CompressionLevelOptions[2];
        set
        {
            if (value is null || value.Value == Cfg.CompressionLevel) return;
            Cfg.CompressionLevel = value.Value;
            OnPropertyChanged();
        }
    }

    // ---------- 压缩输出位置 ----------

    public IReadOnlyList<OptionItem<ArchiveOutputTarget>> OutputTargetOptions { get; } = OptionItem.From(
        (ArchiveOutputTarget.Alongside, "源旁边（默认）"),
        (ArchiveOutputTarget.Custom, "指定目录"));

    /// <summary>「输出到」下拉框（值存在 <see cref="ArchiveConfig.OutputDirectory"/>：空 = 源旁边）。</summary>
    public OptionItem<ArchiveOutputTarget> SelectedOutputTarget
    {
        get => OptionItem<ArchiveOutputTarget>.Find(OutputTargetOptions, CurrentOutputTarget) ?? OutputTargetOptions[0];
        set
        {
            if (value is null) return;
            if (value.Value == CurrentOutputTarget)
            {
                OnPropertyChanged();
                return;
            }

            if (value.Value == ArchiveOutputTarget.Alongside)
            {
                OutputDirectoryText = "";
            }
            else
            {
                var initial = CleanPath(Cfg.LastOutputDirectory);
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) OutputDirectoryText = initial;
                else BrowseOutputDirectory();
                if (string.IsNullOrWhiteSpace(Cfg.OutputDirectory))
                {
                    OnPropertyChanged();
                    return;
                }
            }
            OnPropertyChanged();
        }
    }

    /// <summary>输出目录输入框的绑定桥（清空时把下拉框同步回「源旁边」）。</summary>
    public string OutputDirectoryText
    {
        get => Cfg.OutputDirectory;
        set
        {
            var text = value ?? "";
            if (string.Equals(Cfg.OutputDirectory, text, StringComparison.Ordinal)) return;
            Cfg.OutputDirectory = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomOutputDirectory));
            OnPropertyChanged(nameof(SelectedOutputTarget));
            UpdateOutputPreview();
        }
    }

    /// <summary>是否用了「指定目录」（决定路径输入框显不显示）。</summary>
    public bool IsCustomOutputDirectory => !string.IsNullOrWhiteSpace(Cfg.OutputDirectory);

    private ArchiveOutputTarget CurrentOutputTarget =>
        string.IsNullOrWhiteSpace(Cfg.OutputDirectory) ? ArchiveOutputTarget.Alongside : ArchiveOutputTarget.Custom;

    /// <summary>文件名提示：按当前源 / 格式 / 输出目录算出来的目标压缩包路径。</summary>
    public string OutputPathPreview
    {
        get
        {
            if (Sources.Count == 0) return "添加文件 / 文件夹后，这里显示目标压缩包路径";
            try
            {
                var paths = Sources.Select(s => s.FullPath).ToList();
                var format = ArchiveFormats.CanCreate(Cfg.CreateFormat) ? Cfg.CreateFormat : ArchiveFormat.Zip;
                var directory = CleanPath(Cfg.OutputDirectory);
                var suggested = ArchiveEngine.SuggestArchivePath(paths, format, directory);
                return ArchiveFormats.EnsureExtension(suggested, format);
            }
            catch (Exception ex)
            {
                return "输出路径预览失败：" + ex.Message;
            }
        }
    }

    // =====================================================================
    //  列表
    // =====================================================================

    /// <summary>解压列表（压缩包）。</summary>
    public ObservableCollection<ArchiveListItem> Archives { get; } = new();

    /// <summary>压缩列表（文件 / 文件夹）。</summary>
    public ObservableCollection<ArchiveSourceItem> Sources { get; } = new();

    /// <summary>解压列表是否有内容（界面用它在空列表上显示拖放提示）。</summary>
    public bool HasArchives
    {
        get => _hasArchives;
        private set => Set(ref _hasArchives, value);
    }

    /// <summary>解压列表汇总文字。</summary>
    public string ArchiveSummary
    {
        get => _archiveSummary;
        private set => Set(ref _archiveSummary, value);
    }

    /// <summary>压缩列表是否有内容。</summary>
    public bool HasSources
    {
        get => _hasSources;
        private set => Set(ref _hasSources, value);
    }

    /// <summary>压缩列表汇总文字。</summary>
    public string SourceSummary
    {
        get => _sourceSummary;
        private set => Set(ref _sourceSummary, value);
    }

    // =====================================================================
    //  命令
    // =====================================================================

    public RelayCommand AddArchivesCommand { get; }
    public RelayCommand RemoveSelectedArchivesCommand { get; }
    public RelayCommand ClearArchivesCommand { get; }
    public AsyncRelayCommand StartExtractCommand { get; }
    public RelayCommand BrowseExtractDirectoryCommand { get; }

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveSelectedSourcesCommand { get; }
    public RelayCommand ClearSourcesCommand { get; }
    public AsyncRelayCommand StartCompressCommand { get; }
    public RelayCommand BrowseOutputDirectoryCommand { get; }

    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    // =====================================================================
    //  拖放入口（由 ArchiveView.xaml.cs 调用）
    // =====================================================================

    /// <summary>
    /// 处理一次拖放：压缩包 → 解压列表，其它文件 / 文件夹 → 压缩列表。
    /// 不做弹窗；重复项按路径去重。
    /// </summary>
    public void AddDroppedPaths(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return;
        if (IsBusy)
        {
            StatusText = "正在处理任务，等当前任务结束后再拖入";
            return;
        }

        var archivePaths = new List<string>();
        var sourcePaths = new List<string>();
        foreach (var raw in paths)
        {
            var path = NormalizePath(raw);
            if (path is null) continue;

            if (File.Exists(path) && IsArchiveFile(path)) archivePaths.Add(path);
            else if (File.Exists(path) || Directory.Exists(path)) sourcePaths.Add(path);
        }

        if (archivePaths.Count == 0 && sourcePaths.Count == 0)
        {
            StatusText = "没有识别到可用的文件或文件夹";
            return;
        }

        if (archivePaths.Count > 0) AddArchives(archivePaths);
        if (sourcePaths.Count > 0) AddSources(sourcePaths);

        StatusText = (archivePaths.Count > 0, sourcePaths.Count > 0) switch
        {
            (true, true) => $"已添加 {archivePaths.Count} 个压缩包、{sourcePaths.Count} 个待压缩项",
            (true, false) => $"已添加 {archivePaths.Count} 个压缩包",
            _ => $"已添加 {sourcePaths.Count} 个待压缩项",
        };
        Log(StatusText);
    }

    /// <summary>界面把解压列表的选中项同步过来（支持多选移除）。</summary>
    public void UpdateArchiveSelection(IList? selectedItems)
    {
        _selectedArchives.Clear();
        if (selectedItems is not null)
        {
            foreach (var entry in selectedItems)
                if (entry is ArchiveListItem item) _selectedArchives.Add(item);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>界面把压缩列表的选中项同步过来。</summary>
    public void UpdateSourceSelection(IList? selectedItems)
    {
        _selectedSources.Clear();
        if (selectedItems is not null)
        {
            foreach (var entry in selectedItems)
                if (entry is ArchiveSourceItem item) _selectedSources.Add(item);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    // =====================================================================
    //  「打开方式」入口（主程序调用，见 IFileLaunchTarget）
    // =====================================================================

    /// <summary>
    /// 「以压缩包方式打开」（Windows「打开方式」/ 主程序 <see cref="IFileLaunchTarget"/> 调用）：
    /// 把文件当作压缩包加入<b>解压列表</b>（绝不落到「待压缩」源列表），格式 / 加密状态交给后台
    /// <see cref="ArchiveEngine.Probe"/> 填（流程与拖入压缩包完全一致）。
    /// <para>容错约定：路径非法 / 文件不存在 / 正在跑任务都只写状态栏与日志，绝不抛给主程序；
    /// 是不是有效压缩包不在这里预判，交给 Probe 在列表里标「读取失败」。</para>
    /// </summary>
    public void OpenLaunchedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "打开方式：没有收到文件路径";
            Log("✗ 打开方式：没有收到文件路径");
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            StatusText = $"打开方式：路径无效（{ex.Message}）";
            Log($"✗ 打开方式：路径无效（{ex.Message}）");
            return;
        }

        if (!File.Exists(full))
        {
            StatusText = "打开方式：文件不存在或已被删除";
            Log($"✗ 打开方式：文件不存在 {full}");
            return;
        }

        if (IsBusy)
        {
            // 和「正在跑任务时拖入」一样：不排队、不启动第二次探测，只提示等任务结束
            StatusText = "正在处理任务，等当前任务结束后再用压缩包方式打开";
            Log($"（忙着，未能以压缩包方式打开：{full}）");
            return;
        }

        var alreadyInList = Archives.Any(a => string.Equals(a.FullPath, full, StringComparison.OrdinalIgnoreCase));
        try
        {
            // 只走「解压列表」入口：文件永远不进 Sources（待压缩）
            AddArchives(new[] { full });
        }
        catch (Exception ex)
        {
            StatusText = $"以压缩包方式打开失败（{ex.Message}）";
            Log($"✗ 以压缩包方式打开失败（{ex.Message}）");
            return;
        }

        if (alreadyInList)
        {
            // AddArchives 对全是重复项的情况只写状态栏，这里补一条日志
            Log($"以压缩包方式打开：{Path.GetFileName(full)} 已经在解压列表里");
            return;
        }

        StatusText = $"以压缩包方式打开 {Path.GetFileName(full)}（已加入解压列表）";
        Log(StatusText);
    }

    // =====================================================================
    //  添加 / 移除
    // =====================================================================

    private void AddArchivesViaDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要解压的压缩包",
            Filter = BuildArchiveFilter(),
            Multiselect = true,
            CheckFileExists = true,
        };
        var initial = CleanPath(Cfg.LastAddDirectory);
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog() != true) return;
        if (dialog.FileNames.Length > 0) Cfg.LastAddDirectory = Path.GetDirectoryName(dialog.FileNames[0]) ?? "";
        AddArchives(dialog.FileNames);
    }

    private void AddFilesViaDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要压缩的文件",
            Filter = "所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        var initial = LastSourceDirectory();
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog() != true) return;
        AddSources(dialog.FileNames);
    }

    private void AddFolderViaDialog()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要压缩的文件夹",
            Multiselect = false,
        };
        var initial = LastSourceDirectory();
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog() != true) return;
        AddSources(new[] { dialog.FolderName });
    }

    private void BrowseExtractDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择解压目录",
            Multiselect = false,
        };
        var initial = CleanPath(Cfg.ExtractDirectory) ?? CleanPath(Cfg.LastExtractDirectory);
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog() != true) return;
        Cfg.LastExtractDirectory = dialog.FolderName;
        ExtractDirectoryText = dialog.FolderName;
        StatusText = "解压目录：" + dialog.FolderName;
    }

    private void BrowseOutputDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择压缩包输出目录",
            Multiselect = false,
        };
        var initial = CleanPath(Cfg.OutputDirectory) ?? CleanPath(Cfg.LastOutputDirectory);
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog() != true) return;
        Cfg.LastOutputDirectory = dialog.FolderName;
        OutputDirectoryText = dialog.FolderName;
        StatusText = "输出目录：" + dialog.FolderName;
    }

    private void AddArchives(IReadOnlyList<string> paths)
    {
        var added = new List<ArchiveListItem>();
        var skipped = 0;
        foreach (var path in paths)
        {
            if (Archives.Any(a => string.Equals(a.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            var item = new ArchiveListItem(path);
            Archives.Add(item);
            added.Add(item);
        }

        if (added.Count == 0)
        {
            if (skipped > 0) StatusText = $"这 {skipped} 个压缩包已经在列表里了";
            return;
        }

        UpdateArchiveSummary();
        CommandManager.InvalidateRequerySuggested();

        // 每项单独后台探测；失败只写在列表 + 日志里，绝不弹一串 MessageBox
        foreach (var item in added) _ = ProbeArchiveAsync(item);
        if (skipped > 0) Log($"（{skipped} 个重复的压缩包已跳过）");
    }

    private void AddSources(IReadOnlyList<string> paths)
    {
        var added = 0;
        var skipped = 0;
        foreach (var path in paths)
        {
            if (Sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            var item = new ArchiveSourceItem(path);
            Sources.Add(item);
            added++;
            if (item.IsDirectory) _ = MeasureSourceAsync(item);
        }

        if (added > 0)
        {
            UpdateSourceSummary();
            UpdateOutputPreview();
            CommandManager.InvalidateRequerySuggested();
        }
        if (skipped > 0) Log($"（{skipped} 个重复项已跳过）");
    }

    private void RemoveSelectedArchives()
    {
        if (_selectedArchives.Count == 0) return;
        var items = _selectedArchives.ToList();
        foreach (var item in items) Archives.Remove(item);
        _selectedArchives.Clear();
        UpdateArchiveSummary();
        CommandManager.InvalidateRequerySuggested();
        StatusText = $"已移除 {items.Count} 个压缩包";
        Log(StatusText);
    }

    private void ClearArchives()
    {
        if (Archives.Count == 0) return;
        var count = Archives.Count;
        Archives.Clear();
        _selectedArchives.Clear();
        UpdateArchiveSummary();
        CommandManager.InvalidateRequerySuggested();
        StatusText = $"已清空 {count} 个压缩包";
        Log(StatusText);
    }

    private void RemoveSelectedSources()
    {
        if (_selectedSources.Count == 0) return;
        var items = _selectedSources.ToList();
        foreach (var item in items) Sources.Remove(item);
        _selectedSources.Clear();
        UpdateSourceSummary();
        UpdateOutputPreview();
        CommandManager.InvalidateRequerySuggested();
        StatusText = $"已移除 {items.Count} 个待压缩项";
        Log(StatusText);
    }

    private void ClearSources()
    {
        if (Sources.Count == 0) return;
        var count = Sources.Count;
        Sources.Clear();
        _selectedSources.Clear();
        UpdateSourceSummary();
        UpdateOutputPreview();
        CommandManager.InvalidateRequerySuggested();
        StatusText = $"已清空 {count} 个待压缩项";
        Log(StatusText);
    }

    // =====================================================================
    //  探测（异步填「格式 / 内容 / 状态」）
    // =====================================================================

    private async Task ProbeArchiveAsync(ArchiveListItem item)
    {
        var password = CurrentPassword();
        var encoding = CurrentLegacyEncoding();
        var encodingKey = _encodingKey;

        ArchiveProbeResult? result = null;
        string? error = null;
        try
        {
            result = await Task.Run(() => ArchiveEngine.Probe(item.FullPath, password, encoding));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        Ui(() =>
        {
            if (!Archives.Contains(item)) return;

            // 探测过程中用户又改了密码 / 编码：旧结果作废，等重新探测那一轮来填
            if (encodingKey != _encodingKey ||
                !string.Equals(password, CurrentPassword(), StringComparison.Ordinal))
            {
                return;
            }

            if (result is null) { ApplyProbeFailure(item, error ?? "读取失败"); return; }
            ApplyProbeResult(item, result);
        });
    }

    private void ApplyProbeResult(ArchiveListItem item, ArchiveProbeResult result)
    {
        if (!result.Success)
        {
            ApplyProbeFailure(item, string.IsNullOrWhiteSpace(result.Error) ? "不是有效的压缩包" : result.Error!);
            return;
        }

        var shortFormat = ShortFormatText(result.Format);
        if (shortFormat.Length > 0) item.FormatText = shortFormat;

        var content = $"{result.FileCount} 个文件 · {FileSize.Format(result.TotalSize)}";
        if (result.IsEncrypted) content += " · 🔒 加密";
        if (result.EntriesTruncated) content += "（条目过多，已截断）";
        item.ContentText = content;
        item.Error = "";
        item.HasError = false;
        item.StatusText = result.IsEncrypted && CurrentPassword().Length == 0 ? "需要密码" : "就绪";

        var detail = $"{ArchiveFormats.Info(result.Format).DisplayName}，{result.FileCount} 个文件 · {FileSize.Format(result.TotalSize)}"
                     + (result.IsEncrypted ? "，🔒 加密" : "");
        Log($"✓ {item.FileName}：{detail}");
    }

    private void ApplyProbeFailure(ArchiveListItem item, string error)
    {
        // 加密包的清单读不出来时通常只是缺密码：别当成“文件坏了”标红，提示用户填密码即可
        if (CurrentPassword().Length == 0 && error.Contains("密码", StringComparison.Ordinal))
        {
            item.ContentText = "🔒 需要密码";
            item.StatusText = "需要密码";
            item.HasError = false;
            item.Error = error;
            Log($"🔒 {item.FileName}：需要密码（填「密码」后会自动重新读取）");
            return;
        }

        item.ContentText = "—";
        item.StatusText = "读取失败";
        item.HasError = true;
        item.Error = error;
        Log($"✗ {item.FileName}：{error}");
    }

    /// <summary>密码 / 编码变化后延迟统一重探（连续输入时只探测最后一遍）。</summary>
    private void ScheduleArchiveReprobe()
    {
        if (Archives.Count == 0) return;
        _reprobeTimer.Stop();
        _reprobeTimer.Start();
    }

    private void OnReprobeTimerTick(object? sender, EventArgs e)
    {
        _reprobeTimer.Stop();
        ReprobeAllArchives();
    }

    private void ReprobeAllArchives()
    {
        if (Archives.Count == 0) return;
        foreach (var item in Archives.ToList())
        {
            item.ContentText = "读取中…";
            item.StatusText = "读取中…";
            item.HasError = false;
            item.Error = "";
            _ = ProbeArchiveAsync(item);
        }
        Log("密码 / 编码变了，正在重新读取压缩包清单…");
    }

    /// <summary>文件夹大小统计（后台），统计完再回 UI 更新「大小 / 备注」两列。</summary>
    private async Task MeasureSourceAsync(ArchiveSourceItem item)
    {
        (int FileCount, long TotalBytes) measured;
        try
        {
            measured = await Task.Run(() => ArchiveEngine.MeasureSources(new[] { item.FullPath }));
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                if (!Sources.Contains(item)) return;
                item.SizeText = "—";
                item.Note = "统计失败：" + ex.Message;
                item.IsMeasuring = false;
                UpdateSourceSummary();
            });
            return;
        }

        Ui(() =>
        {
            if (!Sources.Contains(item)) return;
            item.SizeBytes = measured.TotalBytes;
            item.SizeText = FileSize.Format(measured.TotalBytes);
            item.Note = $"文件夹 · {measured.FileCount} 个文件";
            item.IsMeasuring = false;
            UpdateSourceSummary();
            OnPropertyChanged(nameof(OutputPathPreview));
        });
    }

    // =====================================================================
    //  解压
    // =====================================================================

    private async Task ExtractAllAsync()
    {
        if (IsBusy) return;
        if (Archives.Count == 0)
        {
            StatusText = "先添加要解压的压缩包";
            return;
        }

        var items = Archives.ToList();
        var password = CurrentPassword();
        var encoding = CurrentLegacyEncoding();
        var generation = ++_taskGeneration;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        BeginBusy($"正在解压 {items.Count} 个压缩包…");
        var stopwatch = Stopwatch.StartNew();
        var succeeded = 0;
        var failed = 0;
        var canceled = false;

        Log($"===== 开始解压：{items.Count} 个压缩包 =====");

        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (token.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var item = items[index];
                var total = items.Count;
                var destination = ResolveExtractRoot(item.FullPath);

                string effective;
                try
                {
                    effective = Cfg.SubfolderPerArchive
                        ? Path.Combine(destination, ArchiveFormats.GetBaseName(item.FullPath))
                        : destination;
                }
                catch
                {
                    effective = destination;
                }

                Log($"→ [{index + 1}/{total}] {item.FileName} → {effective}");
                item.StatusText = "正在解压…";
                item.HasError = false;
                item.Error = "";

                var options = new ArchiveExtractOptions
                {
                    DestinationDirectory = destination,
                    CreateSubfolderPerArchive = Cfg.SubfolderPerArchive,
                    Overwrite = Cfg.Overwrite,
                    Password = password,
                    LegacyEncoding = encoding,
                    PreserveTimestamps = Cfg.PreserveTimestamps,
                };

                var progress = new System.Progress<ArchiveProgress>(p => ReportExtractProgress(generation, index, total, p));

                ArchiveOperationResult result;
                try
                {
                    result = await Task.Run(() => ArchiveEngine.Extract(item.FullPath, options, progress, token));
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    item.StatusText = "已取消";
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    item.StatusText = "解压失败";
                    item.HasError = true;
                    item.Error = ex.Message;
                    Log($"✗ {item.FileName}：{ex.Message}");
                    continue;
                }

                if (result.Canceled)
                {
                    canceled = true;
                    item.StatusText = "已取消";
                    Log($"已取消：{item.FileName}（已解出的文件保留）");
                    break;
                }

                if (result.Success)
                {
                    succeeded++;
                    item.StatusText = "已解压";
                    item.HasError = false;
                    item.Error = "";
                    _lastOutputPath = string.IsNullOrWhiteSpace(result.OutputPath) ? effective : result.OutputPath;

                    var summary = $"{result.FileCount} 个文件 · {FileSize.Format(result.TotalBytes)}";
                    if (result.SkippedCount > 0) summary += $" · 跳过 {result.SkippedCount} 个";
                    Log($"✓ {item.FileName} → {_lastOutputPath}（{summary}，{result.Elapsed.TotalSeconds:0.0} 秒）");
                    LogWarnings(result.Warnings, item.FileName);
                }
                else
                {
                    failed++;
                    item.StatusText = "解压失败";
                    item.HasError = true;
                    item.Error = result.Error ?? "未知错误";
                    Log($"✗ {item.FileName}：{item.Error}");
                    LogWarnings(result.Warnings, item.FileName);
                }

                _lastOutputPath ??= effective;
            }

            IsProgressIndeterminate = false;
            if (canceled)
            {
                StatusText = $"已取消（成功 {succeeded} 个，失败 {failed} 个）";
                Log("===== 已取消 =====");
            }
            else if (failed == 0)
            {
                Progress = 1;
                StatusText = $"解压完成：{succeeded} 个压缩包（用时 {stopwatch.Elapsed.TotalSeconds:0.0} 秒）";
                Log($"===== 解压完成：{succeeded} 个压缩包，{stopwatch.Elapsed.TotalSeconds:0.0} 秒 =====");
            }
            else
            {
                StatusText = $"解压结束：成功 {succeeded} 个，失败 {failed} 个（用时 {stopwatch.Elapsed.TotalSeconds:0.0} 秒）";
                Log($"===== 解压结束：成功 {succeeded} 个，失败 {failed} 个 =====");
            }
        }
        catch (Exception ex)
        {
            StatusText = "解压出错：" + ex.Message;
            Log("✗ 解压出错：" + ex.Message);
        }
        finally
        {
            _taskGeneration++;
            EndBusy();
            _cts?.Dispose();
            _cts = null;
        }

        if (!canceled && succeeded > 0 && Cfg.OpenFolderAfterDone) OpenInExplorer(_lastOutputPath);
    }

    private void ReportExtractProgress(int generation, int index, int total, ArchiveProgress progress)
    {
        if (generation != _taskGeneration) return;
        Ui(() =>
        {
            if (generation != _taskGeneration) return;

            var percent = progress.Percent;
            IsProgressIndeterminate = percent < 0;
            if (percent >= 0 && total > 0) Progress = Math.Clamp((index + percent) / total, 0, 1);

            var parts = new List<string> { $"{index + 1} / {total}" };
            if (progress.FilesTotal > 0) parts.Add($"{progress.FilesDone} / {progress.FilesTotal} 个文件");
            else if (progress.FilesDone > 0) parts.Add($"已处理 {progress.FilesDone} 个文件");

            if (progress.BytesTotal > 0)
                parts.Add($"{FileSize.Format(progress.BytesDone)} / {FileSize.Format(progress.BytesTotal)}");
            else if (progress.BytesDone > 0) parts.Add(FileSize.Format(progress.BytesDone));

            ProgressText = string.Join(" · ", parts);
            if (!string.IsNullOrWhiteSpace(progress.CurrentEntry))
                StatusText = $"正在解压（{index + 1}/{total}）：{progress.CurrentEntry}";
        });
    }

    // =====================================================================
    //  压缩
    // =====================================================================

    private async Task CreateArchiveAsync()
    {
        if (IsBusy) return;
        if (Sources.Count == 0)
        {
            StatusText = "先添加要压缩的文件或文件夹";
            return;
        }

        var sources = Sources.Select(s => s.FullPath).ToList();
        var format = ArchiveFormats.CanCreate(Cfg.CreateFormat) ? Cfg.CreateFormat : ArchiveFormat.Zip;

        string outputPath;
        try
        {
            var directory = CleanPath(Cfg.OutputDirectory);
            outputPath = ArchiveFormats.EnsureExtension(
                ArchiveEngine.SuggestArchivePath(sources, format, directory), format);
        }
        catch (Exception ex)
        {
            StatusText = "计算输出路径失败：" + ex.Message;
            Log("✗ 计算输出路径失败：" + ex.Message);
            return;
        }

        if (File.Exists(outputPath))
        {
            var answer = MessageBox.Show(
                $"输出文件已经存在：{Environment.NewLine}{outputPath}{Environment.NewLine}{Environment.NewLine}要覆盖它吗？",
                "覆盖确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                StatusText = "已取消（没有覆盖已有文件）";
                return;
            }
        }

        var generation = ++_taskGeneration;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        BeginBusy("正在压缩…");
        var stopwatch = Stopwatch.StartNew();
        var created = false;
        Log($"===== 开始压缩：{sources.Count} 个源 → {outputPath} =====");

        try
        {
            var options = new ArchiveCreateOptions
            {
                Format = format,
                Level = Cfg.CompressionLevel,
                IncludeEmptyDirectories = true,
            };
            var progress = new System.Progress<ArchiveProgress>(p => ReportCreateProgress(generation, p));

            var result = await Task.Run(() => ArchiveEngine.Create(outputPath, options, sources, progress, token));

            if (result.Canceled)
            {
                StatusText = "已取消压缩";
                Log("===== 已取消压缩（未完成的压缩包已由引擎清理）=====");
            }
            else if (result.Success)
            {
                created = true;
                Progress = 1;
                IsProgressIndeterminate = false;
                ProgressText = $"{result.FileCount} 个文件 · {FileSize.Format(result.TotalBytes)}";
                _lastOutputPath = string.IsNullOrWhiteSpace(result.OutputPath) ? outputPath : result.OutputPath;

                StatusText = $"压缩完成：{Path.GetFileName(_lastOutputPath)}"
                             + $"（{FileSize.Format(result.TotalBytes)}，用时 {stopwatch.Elapsed.TotalSeconds:0.0} 秒）";
                Log($"✓ 压缩完成：{_lastOutputPath}（{result.FileCount} 个文件 · {FileSize.Format(result.TotalBytes)}）");
                if (result.SkippedCount > 0) Log($"（跳过 {result.SkippedCount} 个无权限 / 无效的条目）");
                LogWarnings(result.Warnings, Path.GetFileName(_lastOutputPath));
            }
            else
            {
                StatusText = "压缩失败：" + (result.Error ?? "未知错误");
                Log("✗ 压缩失败：" + (result.Error ?? "未知错误"));
                LogWarnings(result.Warnings, Path.GetFileName(outputPath));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消压缩";
            Log("===== 已取消压缩 =====");
        }
        catch (Exception ex)
        {
            StatusText = "压缩出错：" + ex.Message;
            Log("✗ 压缩出错：" + ex.Message);
        }
        finally
        {
            _taskGeneration++;
            EndBusy();
            _cts?.Dispose();
            _cts = null;
        }

        if (created && Cfg.OpenFolderAfterDone) OpenInExplorer(_lastOutputPath);
    }

    private void ReportCreateProgress(int generation, ArchiveProgress progress)
    {
        if (generation != _taskGeneration) return;
        Ui(() =>
        {
            if (generation != _taskGeneration) return;

            var percent = progress.Percent;
            IsProgressIndeterminate = percent < 0;
            if (percent >= 0) Progress = percent;

            var parts = new List<string>();
            if (progress.FilesTotal > 0) parts.Add($"{progress.FilesDone} / {progress.FilesTotal} 个文件");
            else if (progress.FilesDone > 0) parts.Add($"已处理 {progress.FilesDone} 个文件");

            if (progress.BytesTotal > 0)
                parts.Add($"{FileSize.Format(progress.BytesDone)} / {FileSize.Format(progress.BytesTotal)}");
            else if (progress.BytesDone > 0) parts.Add(FileSize.Format(progress.BytesDone));

            ProgressText = parts.Count > 0 ? string.Join(" · ", parts) : "正在打包…";
            if (!string.IsNullOrWhiteSpace(progress.CurrentEntry))
                StatusText = $"正在压缩：{progress.CurrentEntry}";
        });
    }

    // =====================================================================
    //  取消 / 打开输出位置
    // =====================================================================

    private void CancelRunningTask()
    {
        if (_cts is null || !IsBusy) return;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* 任务刚好结束 */ }
        StatusText = "正在取消…（当前文件写完就停）";
        Log("已请求取消，正在停止…");
    }

    private void OpenOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputPath) &&
            (File.Exists(_lastOutputPath) || Directory.Exists(_lastOutputPath)))
        {
            OpenInExplorer(_lastOutputPath);
            return;
        }

        var directory = LastKnownOutputDirectory();
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            OpenInExplorer(directory);
            return;
        }

        StatusText = "还没有可打开的输出位置（先解压或压缩一次）";
    }

    /// <summary>「打开输出位置」是否可用（最近一次输出还在，或者配置 / 源能推出一个存在的目录）。</summary>
    public bool HasOutputLocation
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_lastOutputPath) &&
                (File.Exists(_lastOutputPath) || Directory.Exists(_lastOutputPath)))
            {
                return true;
            }
            var directory = LastKnownOutputDirectory();
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory);
        }
    }

    private string? LastKnownOutputDirectory()
    {
        var output = CleanPath(Cfg.OutputDirectory);
        if (!string.IsNullOrEmpty(output)) return output;

        var extract = CleanPath(Cfg.ExtractDirectory);
        if (!string.IsNullOrEmpty(extract)) return extract;

        if (Sources.Count > 0)
        {
            var last = Sources[Sources.Count - 1];
            return last.IsDirectory ? last.FullPath : Path.GetDirectoryName(last.FullPath);
        }
        if (Archives.Count > 0) return Path.GetDirectoryName(Archives[0].FullPath);
        return null;
    }

    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                // 压缩包是文件：在资源管理器里选中它
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            else
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
                else
                    StatusText = "输出目录不存在：" + path;
            }
        }
        catch (Exception ex)
        {
            StatusText = "打开输出位置失败：" + ex.Message;
            Log("✗ 打开输出位置失败：" + ex.Message);
        }
    }

    // =====================================================================
    //  内部工具
    // =====================================================================

    private string ResolveExtractRoot(string archivePath)
    {
        var configured = CleanPath(Cfg.ExtractDirectory);
        if (!string.IsNullOrEmpty(configured)) return configured;

        var directory = Path.GetDirectoryName(archivePath);
        return string.IsNullOrEmpty(directory) ? Environment.CurrentDirectory : directory;
    }

    private string CurrentPassword() => string.IsNullOrWhiteSpace(_password) ? "" : _password;

    private Encoding? CurrentLegacyEncoding() => _encodingKey switch
    {
        "gbk" => Encoding.GetEncoding(936),
        "utf8" => new UTF8Encoding(false),
        _ => null,
    };

    private void BeginBusy(string status)
    {
        IsBusy = true;
        IsProgressIndeterminate = false;
        Progress = 0;
        ProgressText = "0 / 0";
        StatusText = status;
        CommandManager.InvalidateRequerySuggested();
    }

    private void EndBusy()
    {
        IsBusy = false;
        IsProgressIndeterminate = false;
        CommandManager.InvalidateRequerySuggested();
        OnPropertyChanged(nameof(HasOutputLocation));
    }

    private void UpdateArchiveSummary()
    {
        HasArchives = Archives.Count > 0;
        ArchiveSummary = HasArchives
            ? $"共 {Archives.Count} 个 · {FileSize.Format(Archives.Sum(a => a.SizeBytes))}"
            : "还没有添加压缩包";
        OnPropertyChanged(nameof(HasOutputLocation));
    }

    private void UpdateSourceSummary()
    {
        HasSources = Sources.Count > 0;
        if (!HasSources)
        {
            SourceSummary = "还没有添加文件或文件夹";
            return;
        }

        var total = Sources.Sum(s => s.SizeBytes);
        var measuring = Sources.Any(s => s.IsMeasuring);
        SourceSummary = $"共 {Sources.Count} 项 · {FileSize.Format(total)}"
                        + (measuring ? "（正在统计文件夹大小…）" : "");
        OnPropertyChanged(nameof(HasOutputLocation));
    }

    private void UpdateOutputPreview() => OnPropertyChanged(nameof(OutputPathPreview));

    private void LogWarnings(IReadOnlyList<string>? warnings, string itemName)
    {
        if (warnings is null || warnings.Count == 0) return;
        foreach (var warning in warnings.Take(10)) Log($"  · {itemName}：{warning}");
        if (warnings.Count > 10) Log($"  · ……还有 {warnings.Count - 10} 条提示未显示");
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            // 「重建配置」：自动属性平时不发通知，这里把所有联动属性一起刷一遍
            OnPropertyChanged(nameof(SelectedExtractTarget));
            OnPropertyChanged(nameof(ExtractDirectoryText));
            OnPropertyChanged(nameof(IsCustomExtractDirectory));
            OnPropertyChanged(nameof(SelectedOutputTarget));
            OnPropertyChanged(nameof(OutputDirectoryText));
            OnPropertyChanged(nameof(IsCustomOutputDirectory));
            OnPropertyChanged(nameof(SelectedCreateFormat));
            OnPropertyChanged(nameof(SelectedCompressionLevel));
            OnPropertyChanged(nameof(SelectedOverwrite));
            UpdateOutputPreview();
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ArchiveConfig.ExtractDirectory):
                OnPropertyChanged(nameof(ExtractDirectoryText));
                OnPropertyChanged(nameof(IsCustomExtractDirectory));
                OnPropertyChanged(nameof(SelectedExtractTarget));
                break;
            case nameof(ArchiveConfig.OutputDirectory):
                OnPropertyChanged(nameof(OutputDirectoryText));
                OnPropertyChanged(nameof(IsCustomOutputDirectory));
                OnPropertyChanged(nameof(SelectedOutputTarget));
                UpdateOutputPreview();
                break;
            case nameof(ArchiveConfig.CreateFormat):
                OnPropertyChanged(nameof(SelectedCreateFormat));
                UpdateOutputPreview();
                break;
            case nameof(ArchiveConfig.CompressionLevel):
                OnPropertyChanged(nameof(SelectedCompressionLevel));
                break;
            case nameof(ArchiveConfig.Overwrite):
                OnPropertyChanged(nameof(SelectedOverwrite));
                break;
        }
    }

    /// <summary>真正退出（缓存淘汰 / 退出程序）时停掉后台任务和重探计时器。</summary>
    protected override void OnClosed()
    {
        try { _reprobeTimer.Stop(); } catch { /* 退出流程忽略 */ }
        try { _cts?.Cancel(); } catch { /* 退出流程忽略 */ }
        base.OnClosed();
    }

    private string BuildArchiveFilter()
    {
        try
        {
            var filter = ArchiveFormats.BuildOpenFileFilter();
            if (!string.IsNullOrWhiteSpace(filter)) return filter;
        }
        catch
        {
            // 引擎异常时不要连文件对话框都打不开
        }
        return FallbackArchiveFilter;
    }

    private string? LastSourceDirectory()
    {
        if (Sources.Count == 0) return null;
        var last = Sources[Sources.Count - 1];
        return last.IsDirectory ? last.FullPath : Path.GetDirectoryName(last.FullPath);
    }

    private static bool IsArchiveFile(string path)
    {
        if (LooksLikeArchiveExtension(path)) return true;
        try
        {
            return ArchiveFormats.DetectBest(path) != ArchiveFormat.Unknown;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeArchiveExtension(string path)
        => KnownArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var path = raw.Trim().Trim('"');
        if (path.Length == 0) return null;
        try { path = Path.GetFullPath(path); }
        catch { return null; }
        return File.Exists(path) || Directory.Exists(path) ? path : null;
    }

    private static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var cleaned = path.Trim().Trim('"');
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string ShortFormatText(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.SevenZip => "7Z",
        ArchiveFormat.Rar => "RAR",
        ArchiveFormat.Tar => "TAR",
        ArchiveFormat.TarGz => "TAR.GZ",
        ArchiveFormat.TarBz2 => "TAR.BZ2",
        ArchiveFormat.TarXz => "TAR.XZ",
        ArchiveFormat.TarZstd => "TAR.ZST",
        ArchiveFormat.TarLz => "TAR.LZ",
        ArchiveFormat.GZip => "GZ",
        ArchiveFormat.BZip2 => "BZ2",
        ArchiveFormat.Xz => "XZ",
        ArchiveFormat.Zstd => "ZST",
        ArchiveFormat.Ace => "ACE",
        ArchiveFormat.Arc => "ARC",
        ArchiveFormat.Arj => "ARJ",
        ArchiveFormat.Lzw => "LZW",
        _ => "",
    };
}
