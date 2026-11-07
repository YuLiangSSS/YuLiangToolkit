using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.MemeBoard.Services;

namespace YuLiangToolkit.Tools.MemeBoard.ViewModels;

/// <summary>
/// 「表情包收藏夹」的 ViewModel：收藏网格 + 搜索 + 点击动作（复制到剪贴板 → 粘贴到 QQ）+
/// 设置（动作模式 / 目标会话 / 自动发送延迟 / 粘贴前校验）。
///
/// <para><b>线程</b>：图片解码、QQ 窗口检测、粘贴流程都在后台 <see cref="Task"/> 里跑；
/// 界面集合与状态文本通过 <see cref="ToolViewModelBase.Ui"/> / <see cref="ToolViewModelBase.UiAsync"/>
/// 回到 UI 线程更新。剪贴板读写本身在 UI 线程（WPF 剪贴板要求 STA）。</para>
///
/// <para><b>防重入</b>：单击图片的动作会抢走焦点（切到 QQ），所以 1 秒内重复点击会被忽略，
/// 正在执行时 <see cref="IsActivating"/> 为 true，按钮 / 拖动都禁用。</para>
/// </summary>
public sealed class MemeBoardViewModel : ToolViewModelBase
{
    private const string EmptyFolderHint =
        "还没有收藏任何表情包：点「导入图片…」把本地图片收进来，或复制一张图后点「从剪贴板添加」。"
        + "也支持直接把图片拖进这个页面。";

    private readonly ObservableCollection<MemeItem> _items = new();
    private readonly ObservableCollection<MemeItem> _visibleItems = new();
    private readonly ObservableCollection<QqWindowInfo> _windows = new();

    private CancellationTokenSource? _thumbnailCts;
    private DateTime _lastActivateUtc = DateTime.MinValue;
    private bool _isActivating;
    private bool _viewLoaded;
    private bool _refreshingWindows;
    private string _searchText = "";
    private MemeItem? _selectedItem;
    private QqWindowInfo? _selectedWindow;
    private bool _hasItems;
    private bool _hasVisibleItems;
    private string _statsText = "";
    private string _targetStatusText = "";

    /// <summary>创建 ViewModel 并加载配置 / 收藏列表。</summary>
    public MemeBoardViewModel() : base("meme-board")
    {
        Cfg = LoadConfig<MemeBoardConfig>();
        Cfg.PropertyChanged += OnConfigChanged;

        ImportCommand = new AsyncRelayCommand(_ => ImportFromDialogAsync(), _ => !IsBusy);
        AddFromClipboardCommand = new AsyncRelayCommand(_ => AddFromClipboardAsync(), _ => !IsBusy);
        RefreshCommand = new RelayCommand(_ => RefreshItems());
        DeleteCommand = new RelayCommand(p => DeleteItem(p as MemeItem ?? SelectedItem), _ => SelectedItem is not null && !IsBusy);
        OpenDataFolderCommand = new RelayCommand(_ => Storage.OpenInExplorer());
        RefreshWindowsCommand = new AsyncRelayCommand(_ => RefreshWindowsAsync(), _ => !_refreshingWindows);
        ActivateCommand = new AsyncRelayCommand(p => ActivateItemAsync(p as MemeItem), _ => !IsActivating && !IsBusy);
        CopyOnlyCommand = new AsyncRelayCommand(
            p => ActivateItemAsync(p as MemeItem, MemeActionMode.ClipboardOnly),
            _ => !IsActivating);
        PasteToQqCommand = new AsyncRelayCommand(
            p => ActivateItemAsync(p as MemeItem, MemeActionMode.PasteToQq),
            _ => !IsActivating);
        OpenItemLocationCommand = new RelayCommand(p => OpenItemLocation(p as MemeItem ?? SelectedItem));
        ClearLogCommand = new RelayCommand(_ => ClearLog());

        RefreshItems();
        UpdateTargetStatus(null);

        Log("===== 表情包收藏夹 =====");
        Log($"数据目录：{Storage.Directory}");
        Log($"配置文件：{ConfigPathText}");
        Log($"当前模式：{MemeActionModes.DisplayName(MemeActionModes.Parse(Cfg.ActionMode))}");
        if (IsFirstRun) Log("首次使用：先导入几张图片，再在右侧「目标 QQ 窗口」里选好目标会话。");
    }

    // ---------------------------------------------------------------- 配置

    /// <summary>强类型配置（动作模式 / 目标会话 / 自动发送 / 延迟 / 校验）。</summary>
    public MemeBoardConfig Cfg { get; }

    /// <summary>自动发送随机等待下限（秒，界面滑块直接绑这个）。</summary>
    public double DelayMinSeconds
    {
        get => Cfg.DelayMinSeconds;
        set
        {
            if (!double.IsFinite(value)) return;
            Cfg.DelayMinSeconds = value;
        }
    }

    /// <summary>自动发送随机等待上限（秒，界面滑块直接绑这个）。</summary>
    public double DelayMaxSeconds
    {
        get => Cfg.DelayMaxSeconds;
        set
        {
            if (!double.IsFinite(value)) return;
            Cfg.DelayMaxSeconds = value;
        }
    }

    /// <summary>「随机 0.5~1.5 秒后发送」这句说明。</summary>
    public string DelayRangeText => Cfg.DelayRangeText;

    /// <summary>配置变化时同步界面上由配置派生的属性。</summary>
    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MemeBoardConfig.DelayMinSeconds):
            case nameof(MemeBoardConfig.DelayMaxSeconds):
            case nameof(MemeBoardConfig.DelayRangeText):
            case nameof(MemeBoardConfig.SendDelayMinMs):
            case nameof(MemeBoardConfig.SendDelayMaxMs):
                OnPropertyChanged(nameof(DelayMinSeconds));
                OnPropertyChanged(nameof(DelayMaxSeconds));
                OnPropertyChanged(nameof(DelayRangeText));
                break;
            case nameof(MemeBoardConfig.TargetChat):
                UpdateTargetStatus(_windows.ToList());
                break;
        }
    }

    // ---------------------------------------------------------------- 收藏列表

    /// <summary>收藏夹里的全部图片（按修改时间从新到旧）。</summary>
    public ObservableCollection<MemeItem> Items => _items;

    /// <summary>搜索过滤后实际显示在网格里的图片。</summary>
    public ObservableCollection<MemeItem> VisibleItems => _visibleItems;

    /// <summary>搜索关键字（空 = 显示全部）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            ApplyFilter();
        }
    }

    /// <summary>当前选中的图片（删除 / 打开所在位置作用于它）。</summary>
    public MemeItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>收藏夹里有没有图片（决定是否显示空状态提示）。</summary>
    public bool HasItems
    {
        get => _hasItems;
        private set => Set(ref _hasItems, value);
    }

    /// <summary>过滤后有没有图片（有收藏但搜不到时也显示提示）。</summary>
    public bool HasVisibleItems
    {
        get => _hasVisibleItems;
        private set => Set(ref _hasVisibleItems, value);
    }

    /// <summary>空状态 / 搜索无结果时的提示文字。</summary>
    public string EmptyHint { get; private set; } = EmptyFolderHint;

    /// <summary>顶部统计文字（张数 + 占用空间）。</summary>
    public string StatsText
    {
        get => _statsText;
        private set => Set(ref _statsText, value);
    }

    /// <summary>是否正在执行「单击图片」的动作（防重入）。</summary>
    public bool IsActivating
    {
        get => _isActivating;
        private set
        {
            if (!Set(ref _isActivating, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---------------------------------------------------------------- QQ 目标窗口

    /// <summary>检测到的 QQ 聊天窗口列表（右侧下拉框的数据源）。</summary>
    public ObservableCollection<QqWindowInfo> Windows => _windows;

    /// <summary>下拉框里选中的目标窗口；选中后会把会话名保存进配置。</summary>
    public QqWindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (!Set(ref _selectedWindow, value)) return;
            if (value is not null && value.HasChatName
                && !string.Equals(Cfg.TargetChat, value.ChatName, StringComparison.Ordinal))
            {
                Cfg.TargetChat = value.ChatName;
                Log($"目标会话已设为「{value.ChatName}」");
            }
            UpdateTargetStatus(_windows.ToList());
        }
    }

    /// <summary>目标会话状态提示（已检测到 / 未检测到 / 还没选）。</summary>
    public string TargetStatusText
    {
        get => _targetStatusText;
        private set => Set(ref _targetStatusText, value);
    }

    /// <summary>根据当前窗口列表刷新目标状态提示。</summary>
    private void UpdateTargetStatus(IReadOnlyList<QqWindowInfo>? windows)
    {
        var saved = (Cfg.TargetChat ?? "").Trim();
        if (saved.Length == 0)
        {
            TargetStatusText = "还没有选择目标会话：先在 QQ 里切到目标群 / 会话，点「刷新检测」，再在下拉框里选它。";
            return;
        }

        var match = ChatTargetMatcher.Resolve(windows, saved);
        TargetStatusText = match is not null
            ? $"目标会话「{saved}」已检测到（{match.DisplayText}）。"
            : $"目标会话「{saved}」当前未检测到：请先打开该会话，或点「刷新检测」。";
    }

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand AddFromClipboardCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public AsyncRelayCommand RefreshWindowsCommand { get; }
    public AsyncRelayCommand ActivateCommand { get; }
    public AsyncRelayCommand CopyOnlyCommand { get; }
    public AsyncRelayCommand PasteToQqCommand { get; }
    public RelayCommand OpenItemLocationCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>
    /// 工具页每次显示时调用：<b>重新订阅配置事件</b>（<see cref="OnClosed"/> 会退订）、
    /// 重新检测 QQ 窗口；从别的页面切回来时还会重新扫描数据文件夹并重启缩略图加载。
    /// </summary>
    public void OnViewLoaded()
    {
        // ① 每次显示都重新挂配置事件：先 -= 再 +=，避免重复订阅，也保证切回来之后
        //    滑块 / 自动发送 / 目标会话变化仍能刷新界面上的派生属性。
        Cfg.PropertyChanged -= OnConfigChanged;
        Cfg.PropertyChanged += OnConfigChanged;

        // ② 切回页面时重新扫描数据文件夹（外部新增 / 删除的图片能出现，缩略图加载也会重启）；
        //    第一次显示时构造函数已经扫过一遍，不重复做重活。
        if (_viewLoaded) RefreshItems();
        _viewLoaded = true;

        // ③ 每次显示都重新检测 QQ 窗口（RefreshWindowsAsync 内部有 _refreshingWindows 防重入）。
        _ = RefreshWindowsAsync();
    }

    /// <summary>切走 / 关闭：停止缩略图加载、退订配置事件，避免缓存对象泄漏。</summary>
    protected override void OnClosed()
    {
        Cfg.PropertyChanged -= OnConfigChanged;
        try { _thumbnailCts?.Cancel(); } catch (ObjectDisposedException) { /* 已释放 */ }
    }

    // ---------------------------------------------------------------- 导入 / 增删

    /// <summary>弹多选文件对话框导入图片。</summary>
    private async Task ImportFromDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入表情包图片（可多选）",
            Filter = MemeFiles.FileDialogFilter,
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        await ImportPathsCoreAsync(dialog.FileNames);
    }

    /// <summary>
    /// 导入一批图片（对话框 / 拖入共用）：复制进 <c>Data\meme-board\images\</c>（重名自动加序号），
    /// 然后刷新网格。全程后台拷贝，不卡界面。
    /// </summary>
    public async Task ImportPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0) return;
        await ImportPathsCoreAsync(paths);
    }

    private async Task ImportPathsCoreAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return;

        var candidates = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0) return;

        IsBusy = true;
        try
        {
            var (imported, skipped) = await Task.Run(() => CopyImagesIntoStorage(candidates));
            if (imported > 0) RefreshItems();

            StatusText = imported > 0
                ? $"已导入 {imported} 张图片" + (skipped > 0 ? $"，跳过 {skipped} 个" : "")
                : "没有导入任何图片（只支持 png / jpg / jpeg / gif / bmp）";
            Log(imported > 0
                ? $"导入完成：新增 {imported} 张" + (skipped > 0 ? $"，跳过 {skipped} 个" : "")
                : "导入失败：没有可用的图片文件");
        }
        catch (Exception ex)
        {
            StatusText = "导入失败：" + ex.Message;
            Log("✗ 导入失败：" + ex);
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>后台线程：把外部图片拷进数据文件夹，返回（成功数, 跳过数）。</summary>
    private (int Imported, int Skipped) CopyImagesIntoStorage(IReadOnlyList<string> paths)
    {
        var imported = 0;
        var skipped = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!MemeFiles.IsSupportedImage(path)) { skipped++; continue; }
                var file = Storage.Import(path, "images/" + Path.GetFileName(path));
                if (file is null) skipped++;
                else imported++;
            }
            catch
            {
                skipped++;
            }
        }
        return (imported, skipped);
    }

    /// <summary>从剪贴板添加一张图片（PNG 存进数据文件夹）。</summary>
    private async Task AddFromClipboardAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            BitmapSource? image = null;
            await UiAsync(() =>
            {
                image = ClipboardImageService.TryGetImage();
                return Task.CompletedTask;
            });
            if (image is null)
            {
                StatusText = "剪贴板里没有图片";
                Log("剪贴板里没有图片（先截图或复制一张图片再试）");
                return;
            }

            // 编码成 PNG 保存（在 UI 线程做，避免冻结位图跨线程再处理的兼容问题）
            var bytes = ClipboardImageService.EncodePng(image);
            if (bytes is null || bytes.Length == 0)
            {
                StatusText = "剪贴板图片编码失败";
                Log("✗ 剪贴板图片编码失败");
                return;
            }

            var fileName = MemeFiles.ClipboardImportName(DateTime.Now);
            var path = Storage.WriteBytes("images/" + fileName, bytes);
            if (path is null)
            {
                StatusText = "保存剪贴板图片失败";
                Log("✗ 保存剪贴板图片失败（数据文件夹不可写？）");
                return;
            }

            RefreshItems();
            StatusText = $"已从剪贴板添加：{fileName}";
            Log($"从剪贴板添加：{fileName}（{FileSize.Format(bytes.Length)}）");
        }
        catch (Exception ex)
        {
            StatusText = "从剪贴板添加失败：" + ex.Message;
            Log("✗ 从剪贴板添加失败：" + ex);
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>重新扫描数据文件夹里的图片并刷新网格。</summary>
    public void RefreshItems()
    {
        IReadOnlyList<StorageFile> files;
        try { files = Storage.List("*"); }
        catch { files = Array.Empty<StorageFile>(); }

        var images = files
            .Where(file => MemeFiles.IsSupportedImage(file.FullPath))
            .OrderByDescending(file => file.LastWriteTime)
            .ToList();

        _items.Clear();
        foreach (var file in images) _items.Add(new MemeItem(file));

        ApplyFilter();
        _ = LoadThumbnailsAsync();
    }

    /// <summary>按搜索关键字重建可见列表，并刷新统计 / 空状态提示。</summary>
    private void ApplyFilter()
    {
        var keyword = (_searchText ?? "").Trim();
        _visibleItems.Clear();
        foreach (var item in _items)
        {
            if (keyword.Length == 0 || item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                _visibleItems.Add(item);
        }

        HasItems = _items.Count > 0;
        HasVisibleItems = _visibleItems.Count > 0;
        EmptyHint = _items.Count == 0
            ? EmptyFolderHint
            : $"没有匹配「{keyword}」的图片，换个关键字试试。";

        if (_selectedItem is not null && !_visibleItems.Contains(_selectedItem))
            SelectedItem = null;

        var totalBytes = _items.Sum(item => item.SizeBytes);
        StatsText = _items.Count == 0
            ? "还没有收藏任何图片"
            : $"共 {_items.Count} 张图片 · {FileSize.Format(totalBytes)}";
        OnPropertyChanged(nameof(EmptyHint));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>后台逐张加载缩略图，加载好一张就回填一张（后台解码，UI 线程赋值）。</summary>
    private async Task LoadThumbnailsAsync()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        var token = _thumbnailCts.Token;
        var snapshot = _items.ToList();

        foreach (var item in snapshot)
        {
            if (token.IsCancellationRequested) return;
            if (item.HasThumbnail) continue;

            BitmapSource? thumbnail;
            try
            {
                thumbnail = await Task.Run(() => MemeItem.LoadThumbnail(item.FullPath), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            Ui(() =>
            {
                // 扫描期间列表可能已经刷新过：不在当前列表里就不改
                if (_items.Contains(item)) item.ApplyThumbnail(thumbnail);
            });
        }
    }

    /// <summary>从收藏夹删除一张图片（带确认框，会真正删除数据文件）。</summary>
    private void DeleteItem(MemeItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return;

        var answer = MessageBox.Show(
            $"从收藏夹删除「{item.Name}」？{Environment.NewLine}{Environment.NewLine}"
            + "图片文件会从数据文件夹里真正删除，无法撤销。",
            "删除表情包",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        if (Storage.Delete(item.RelativePath))
        {
            if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
            RefreshItems();
            StatusText = $"已删除「{item.Name}」";
            Log($"删除：{item.Name}（{item.RelativePath}）");
        }
        else
        {
            StatusText = "删除失败（文件可能被占用）";
            Log($"✗ 删除失败：{item.RelativePath}");
        }
    }

    /// <summary>在资源管理器中显示一张收藏图片。</summary>
    private void OpenItemLocation(MemeItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        if (!Storage.RevealInExplorer(item.RelativePath))
            Log($"✗ 打开所在位置失败：{item.FullPath}");
    }

    // ---------------------------------------------------------------- 点击动作

    /// <summary>
    /// 单击图片的完整动作：复制到剪贴板 →（按模式）检测 QQ 窗口 → 解析目标会话 → 粘贴 → 自动发送。
    /// </summary>
    /// <param name="item">要处理的图片。</param>
    /// <param name="overrideMode">临时指定动作模式（右键「仅复制这张 / 粘贴到 QQ」用）；null = 用配置。</param>
    private async Task ActivateItemAsync(MemeItem? item, MemeActionMode? overrideMode = null)
    {
        // 右键菜单的命令不带参数，退回当前选中项；单击时参数就是点中的那一张
        item ??= SelectedItem;
        if (item is null || IsActivating) return;

        // 1 秒内重复点击直接忽略：动作会把焦点切去 QQ，连点容易误发
        var now = DateTime.UtcNow;
        if ((now - _lastActivateUtc).TotalMilliseconds < 1000) return;
        _lastActivateUtc = now;

        IsActivating = true;
        try
        {
            var mode = overrideMode ?? MemeActionModes.Parse(Cfg.ActionMode);

            // ① 复制到剪贴板：必须在 UI 线程从文件解码后再 SetImage。
            // 实测后台线程解码 + Freeze 的 BitmapSource，跨进程（QQ）粘贴会 CLIPBRD_E_BAD_DATA；
            // UI 线程新建 BitmapImage 再 SetImage 才能被 QQ 正常粘贴（见 ClipboardImageService.TrySetImageFromFile）。
            var copied = false;
            var copyError = "";
            await UiAsync(() =>
            {
                copied = ClipboardImageService.TrySetImageFromFile(item.FullPath, out copyError);
                return Task.CompletedTask;
            });
            if (!copied)
            {
                StatusText = "复制到剪贴板失败：" + copyError;
                Log("✗ 复制到剪贴板失败：" + copyError);
                return;
            }

            StatusText = $"已复制「{item.Name}」到剪贴板";
            Log($"已复制到剪贴板：{item.Name}");

            // ② 仅复制模式：到此结束，完全不碰 QQ
            if (mode == MemeActionMode.ClipboardOnly)
            {
                Log("当前为「仅复制到剪贴板」模式，未操作 QQ");
                return;
            }

            // ③ 还没选目标会话：图片留在剪贴板，提示手动粘贴
            var savedChat = (Cfg.TargetChat ?? "").Trim();
            if (savedChat.Length == 0)
            {
                StatusText = "图片已在剪贴板（还没选择目标会话）";
                Log("还没选择目标 QQ 会话：图片已复制到剪贴板，可手动 Ctrl+V；请到右侧下拉框里选目标会话。");
                return;
            }

            // ④ 重新检测 QQ 窗口并解析目标
            Log("正在检测 QQ 窗口…");
            var windows = await Task.Run(QqWindowService.FindChatWindows);
            Ui(() => ApplyWindows(windows));

            var target = ChatTargetMatcher.Resolve(windows, savedChat);
            if (target is null)
            {
                StatusText = $"图片已在剪贴板：未找到目标会话「{savedChat}」";
                Log($"未找到目标会话（请先打开 {savedChat} 或点「刷新检测」）；图片仍在剪贴板。");
                return;
            }

            Log($"命中目标窗口：{target.DisplayText}");

            // ⑤ 后台执行 QQ 粘贴流程（配置在本线程先取快照，避免跨线程读配置对象）
            var verify = Cfg.VerifyTargetChat;
            var autoSend = Cfg.AutoSend;
            var minDelay = Cfg.SendDelayMinMs;
            var maxDelay = Cfg.SendDelayMaxMs;
            StatusText = "正在粘贴到 QQ…";

            var result = await Task.Run(() => QqPasteService.Paste(
                target,
                savedChat,
                verify,
                autoSend,
                minDelay,
                maxDelay,
                Log));

            StatusText = result.Message;
            Log(result.Message);
        }
        catch (Exception ex)
        {
            StatusText = "操作失败：" + ex.Message;
            Log("✗ 操作失败：" + ex);
        }
        finally
        {
            IsActivating = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---------------------------------------------------------------- QQ 窗口刷新

    /// <summary>重新检测 QQ 聊天窗口并刷新下拉框 / 目标状态提示。</summary>
    private async Task RefreshWindowsAsync()
    {
        if (_refreshingWindows) return;
        _refreshingWindows = true;
        TargetStatusText = "正在检测 QQ 窗口…";

        try
        {
            var windows = await Task.Run(QqWindowService.FindChatWindows);
            Ui(() => ApplyWindows(windows));

            if (windows.Count == 0)
            {
                Log("没有检测到 QQ 聊天窗口（请先登录 QQ 并打开一个聊天会话）。");
            }
            else
            {
                Log($"检测到 {windows.Count} 个 QQ 聊天窗口。");
                if (string.IsNullOrWhiteSpace(Cfg.TargetChat))
                    Log("请到右侧「目标 QQ 窗口」下拉框里选一个会话作为粘贴目标。");
                else if (ChatTargetMatcher.Resolve(windows, Cfg.TargetChat) is null)
                    Log($"目标会话「{Cfg.TargetChat}」当前没检测到：请先在 QQ 里打开它，再刷新检测。");
            }
        }
        catch (Exception ex)
        {
            TargetStatusText = "检测 QQ 窗口失败：" + ex.Message;
            Log("✗ 检测 QQ 窗口失败：" + ex);
        }
        finally
        {
            _refreshingWindows = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>用新检测到的窗口列表刷新下拉框，并尽量恢复选中项（必须在 UI 线程调用）。</summary>
    private void ApplyWindows(IReadOnlyList<QqWindowInfo>? windows)
    {
        var list = windows ?? Array.Empty<QqWindowInfo>();

        _windows.Clear();
        foreach (var window in list) _windows.Add(window);

        var match = string.IsNullOrWhiteSpace(Cfg.TargetChat)
            ? null
            : ChatTargetMatcher.Resolve(list, Cfg.TargetChat);

        // 这里直接改字段 + 通知（走属性 setter 会把 Cfg.TargetChat 再写一遍，没必要）
        _selectedWindow = match;
        OnPropertyChanged(nameof(SelectedWindow));
        UpdateTargetStatus(list);
    }
}
