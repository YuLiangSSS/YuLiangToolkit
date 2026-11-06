using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ScreenSticker.Views;

namespace YuLiangToolkit.Tools.ScreenSticker.ViewModels;

/// <summary>
/// 「屏幕贴纸」工具页的 ViewModel：选图（可多选）/ 最近使用缩略图 /
/// 桌面贴纸列表（置顶开关、层级、关闭）/ 偏好设置。
///
/// <para><b>职责边界</b>：贴纸窗口的存活、层级、持久化全在 <see cref="StickerSession"/>；
/// 本类只是它的「管理面板」——不自己 new 窗口、不直接写配置里的贴纸列表，
/// 只调会话的公开方法，然后听 <see cref="StickerSession.StickersChanged"/> 刷新界面。</para>
///
/// <para><b>缩略图为什么异步</b>：最近使用最多 100 条，若在 UI 线程同步解码会明显卡顿；
/// 所以只在后台线程用 <see cref="StickerImage.LoadThumbnail"/> 解码，再通过 <c>Ui(...)</c> 回填。
/// 每次刷新会递增代号，过期的后台任务不再往界面写。</para>
/// </summary>
public sealed class ScreenStickerViewModel : ToolViewModelBase
{
    /// <summary>列表缩略图解码宽度（像素）；缩略图只显示几十像素，解大了纯浪费内存。</summary>
    private const int ListThumbnailWidth = 120;

    /// <summary>贴纸列表缩略图缓存（同一张图重复刷新时不用反复解码）。只在 UI 线程读写。</summary>
    private readonly Dictionary<string, BitmapSource?> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近使用列表的刷新代号：后台解码任务发现代号变了就停手。</summary>
    private int _recentGeneration;

    private bool _suppressRecentRefresh;
    private bool _suppressStickerRefresh;
    private bool _closed;

    public ScreenStickerViewModel() : base(ScreenStickerConfig.ToolId)
    {
        Session = StickerSession.Current;
        Session.EnsureConfig();
        Cfg = LoadConfig<ScreenStickerConfig>();

        ChooseImagesCommand = new RelayCommand(_ => ChooseImages());
        PasteClipboardCommand = new AsyncRelayCommand(_ => PasteClipboardAsync());
        RestoreStickersCommand = new RelayCommand(_ => RestoreStickers());
        ClearRecentCommand = new RelayCommand(_ => ClearRecent());
        SetAllTopmostCommand = new RelayCommand(_ => SetAllTopmost(true));
        ClearAllTopmostCommand = new RelayCommand(_ => SetAllTopmost(false));
        CloseAllStickersCommand = new RelayCommand(_ => CloseAllStickers());
        ResetAllSizesCommand = new RelayCommand(_ => ResetAllSizes());
        ResetAllSpeedsCommand = new RelayCommand(_ => ResetAllSpeeds());
        ResetAllDefaultsCommand = new RelayCommand(_ => ResetAllDefaults());
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());
        OpenConfigFolderCommand = new RelayCommand(_ => OpenConfigFolder());

        Session.StickersChanged += OnStickersChanged;
        Cfg.PropertyChanged += OnConfigChanged;

        RefreshRecentItems();
        RefreshStickers();
        StatusText = "就绪：可以选图 / 把图片拖进来 / 按 Ctrl+V 贴剪贴板图片，也可以点左侧的最近使用再贴一张";
    }

    // ===================== 会话 / 配置 =====================

    /// <summary>后台会话（贴纸窗口都归它管）。</summary>
    public StickerSession Session { get; }

    /// <summary>本功能的配置（与会话用的是同一个实例）。</summary>
    public ScreenStickerConfig Cfg { get; }

    // ===================== 集合 =====================

    /// <summary>最近使用的图片（新 → 旧，最多 100 条）。</summary>
    public ObservableCollection<RecentStickerItem> RecentItems { get; } = new();

    /// <summary>当前桌面上的贴纸（顺序与窗口层级一致，0 最靠下）。</summary>
    public ObservableCollection<StickerListItem> Stickers { get; } = new();

    /// <summary>最近使用是不是空的（空状态提示用）。</summary>
    public bool HasRecentItems => RecentItems.Count > 0;

    /// <summary>桌面贴纸是不是空的（空状态提示用）。</summary>
    public bool HasStickers => Stickers.Count > 0;

    // ===================== 汇总文本 =====================

    /// <summary>顶部工具条上的贴纸数量提示。</summary>
    public string StickerSummaryText
    {
        get
        {
            var visible = Session.Windows.Count;
            // 恢复记录里包含「已经显示着」的那些，真正还能恢复的是差额。
            var restorable = Math.Max(0, Session.SavedStickerCount - visible);
            return restorable > 0
                ? $"桌面 {visible} 张 · 可恢复 {restorable} 张"
                : $"桌面 {visible} 张";
        }
    }

    /// <summary>贴纸卡片标题旁的数量。</summary>
    public string StickerCountText => $"共 {Session.Windows.Count} 张";

    /// <summary>最近使用卡片的副标题（含当前上限）。</summary>
    public string RecentSummaryText => Cfg.RecordsRecent
        ? $"最近 {RecentItems.Count} 条 · 上限 {Cfg.RecentLimitEffective}"
        : "最近使用已关闭（上限 0）";

    /// <summary>最近使用为空时的提示语。</summary>
    public string RecentEmptyText =>
        "还没有最近使用的图片。\n贴过的图片会自动出现在这里，点一下就能再贴一张。";

    /// <summary>贴纸列表为空时的提示语。</summary>
    public string StickerEmptyText =>
        "桌面上还没有贴纸。\n点上方「选择图片…」，或者从左侧最近使用里点一张。";

    // ===================== 命令 =====================

    /// <summary>选图（支持多选）并逐张贴到桌面。</summary>
    public RelayCommand ChooseImagesCommand { get; }

    /// <summary>把剪贴板里的图片（截屏 / 网页复制的位图，或复制的图片文件，可多张）贴到桌面。</summary>
    public AsyncRelayCommand PasteClipboardCommand { get; }

    /// <summary>按配置恢复上次的贴纸。</summary>
    public RelayCommand RestoreStickersCommand { get; }

    /// <summary>清空最近使用记录（不删本地图片）。</summary>
    public RelayCommand ClearRecentCommand { get; }

    /// <summary>把桌面上所有贴纸设为置顶。</summary>
    public RelayCommand SetAllTopmostCommand { get; }

    /// <summary>取消所有贴纸的置顶。</summary>
    public RelayCommand ClearAllTopmostCommand { get; }

    /// <summary>关闭全部贴纸（弹一次确认）。</summary>
    public RelayCommand CloseAllStickersCommand { get; }

    /// <summary>全部贴纸恢复默认大小（图片原始尺寸）。</summary>
    public RelayCommand ResetAllSizesCommand { get; }

    /// <summary>全部贴纸恢复默认 GIF 播放速度。</summary>
    public RelayCommand ResetAllSpeedsCommand { get; }

    /// <summary>全部贴纸恢复默认状态（大小 + GIF 速度 + 置顶；确认后执行）。</summary>
    public RelayCommand ResetAllDefaultsCommand { get; }

    /// <summary>恢复默认偏好设置（不动贴纸与最近使用记录）。</summary>
    public RelayCommand ResetSettingsCommand { get; }

    /// <summary>打开配置文件夹。</summary>
    public RelayCommand OpenConfigFolderCommand { get; }

    // ===================== 设置（包装成属性，方便「恢复默认设置」后刷新界面） =====================

    /// <summary>启动程序时是否自动恢复上次的贴纸。</summary>
    public bool AutoRestoreOnStartup
    {
        get => Cfg.AutoRestoreOnStartup;
        set
        {
            if (Cfg.AutoRestoreOnStartup == value) return;
            Cfg.AutoRestoreOnStartup = value;
            OnPropertyChanged();
        }
    }

    /// <summary>新贴纸是否默认置顶。</summary>
    public bool DefaultTopmost
    {
        get => Cfg.DefaultTopmost;
        set
        {
            if (Cfg.DefaultTopmost == value) return;
            Cfg.DefaultTopmost = value;
            OnPropertyChanged();
        }
    }

    /// <summary>新贴纸（GIF）的默认播放速度（0.25× ~ 3×）。</summary>
    public double DefaultGifSpeed
    {
        get => Cfg.DefaultGifSpeedEffective;
        set
        {
            var speed = StickerTiming.ClampSpeed(value);
            if (Math.Abs(Cfg.DefaultGifSpeed - speed) < 0.001) return;
            Cfg.DefaultGifSpeed = speed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DefaultGifSpeedText));
        }
    }

    /// <summary>默认播放速度旁边的文本（例如 1× / 1.5×）。</summary>
    public string DefaultGifSpeedText => StickerTiming.FormatSpeed(DefaultGifSpeed);

    /// <summary>最近使用上限的输入文本（0 ~ 100；文本输入，避免绑 int 时删空报错）。</summary>
    public string RecentLimitText
    {
        get => Cfg.RecentLimit.ToString();
        set
        {
            if (!int.TryParse((value ?? "").Trim(), out var limit))
            {
                // 输入非法：把输入框顶回当前生效值（不弹窗，符合工具页“静默纠错”的习惯）
                OnPropertyChanged();
                return;
            }

            limit = Math.Clamp(limit, 0, ScreenStickerConfig.MaxRecentLimit);
            if (Cfg.RecentLimit != limit)
            {
                Cfg.RecentLimit = limit;
                Cfg.TrimRecentToLimit();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(RecentSummaryText));
        }
    }

    // ===================== 顶部工具条：选图 / 恢复 =====================

    private void ChooseImages()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要贴到桌面的图片（可多选）",
            Filter = StickerImage.DialogFilter,
            Multiselect = true,
            CheckFileExists = true,
        };

        // 起始目录：上次选图 / 贴图所在的文件夹（配置里存着；目录没了就不用）
        var lastFolder = Cfg.LastFolder;
        if (!string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder))
        {
            dialog.InitialDirectory = lastFolder;
        }

        if (dialog.ShowDialog() != true) return;

        AddStickerFiles(dialog.FileNames);
    }

    /// <summary>
    /// 把一批图片文件逐张贴到桌面——「选择图片…」与「拖入图片」共用这一条流程
    /// （记住上次所在文件夹、批量期间暂停列表刷新、逐张 <see cref="StickerSession.AddSticker"/>、
    /// 汇总失败原因写进状态栏与日志），避免两处各写一份。
    /// <para>⚠ 必须在 UI / STA 线程调用：<see cref="StickerSession.AddSticker"/> 内部会 new 并 Show 贴纸窗口。</para>
    /// </summary>
    /// <param name="files">
    /// 图片文件路径；可以包含不支持的格式（会按「贴图失败」逐张汇总，不抛异常）。
    /// 过滤规则由调用方决定：选图对话框用 <see cref="StickerImage.DialogFilter"/>，
    /// 拖入走 <see cref="AddDroppedImages"/> 的 <see cref="CanAcceptDrop"/> 预过滤。
    /// </param>
    public void AddStickerFiles(IReadOnlyList<string>? files)
    {
        if (files is null || files.Count == 0) return;

        // 记住这次选图 / 拖入所在的文件夹（下次打开对话框还在这里）
        try
        {
            var folder = Path.GetDirectoryName(files[0]);
            if (!string.IsNullOrWhiteSpace(folder)) Cfg.LastFolder = folder;
        }
        catch
        {
            // 路径异常就沿用旧值，不影响贴图
        }

        var added = 0;
        var failures = new List<string>();

        // 批量操作期间先别反复刷新列表（AddSticker 每成功一张都会发 StickersChanged）
        _suppressRecentRefresh = true;
        _suppressStickerRefresh = true;
        try
        {
            foreach (var path in files)
            {
                if (Session.AddSticker(path) is not null)
                {
                    added++;
                }
                else
                {
                    failures.Add($"{Path.GetFileName(path)}：{Session.LastError ?? "未知原因"}");
                }
            }
        }
        finally
        {
            _suppressRecentRefresh = false;
            _suppressStickerRefresh = false;
        }

        RefreshRecentItems();
        RefreshStickers();

        if (Session.LastNotice is { Length: > 0 } notice) Log("提示：" + notice);

        if (added > 0) Log($"已贴 {added} 张图片到桌面");
        foreach (var failure in failures) Log("贴图失败：" + failure);

        StatusText = failures.Count == 0
            ? $"已贴 {added} 张图片"
            : $"已贴 {added} 张，失败 {failures.Count} 张（{failures[0]}）";
    }

    // ===================== 拖入图片（整页放下即贴） =====================

    /// <summary>
    /// 拖入的路径能不能直接贴到桌面：文件存在 + 后缀是支持的图片
    /// （见 <see cref="StickerImage.Extensions"/>，忽略大小写）。
    /// 拖文件夹 / 非图片 / 不存在的路径都返回 false；路径非法等异常也吞掉返回 false。
    /// </summary>
    public bool CanAcceptDrop(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path)
                && File.Exists(path)
                && StickerImage.IsSupportedExtension(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 处理「从资源管理器拖进工具页」的文件：先滤出真正能贴的图片，再走与「选择图片…」
    /// 相同的批量贴图流程（<see cref="AddStickerFiles"/>）。
    /// 过滤后为空时只改状态栏 / 日志，不贴任何图、不弹窗。
    /// <para>⚠ 拖放回调在 UI 线程，这里直接调 <see cref="StickerSession.AddSticker"/> 是安全的。</para>
    /// </summary>
    /// <param name="paths">拖入的全部路径（可以含文件夹 / 非图片 / 不存在的路径，会被忽略）。</param>
    public void AddDroppedImages(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            StatusText = "拖入的内容里没有文件";
            return;
        }

        var files = new List<string>();
        foreach (var path in paths)
        {
            if (CanAcceptDrop(path)) files.Add(path);
        }

        if (files.Count == 0)
        {
            StatusText = $"拖入的 {paths.Count} 个文件里没有支持的图片（支持 png / jpg / bmp / gif / tif / ico / webp）";
            Log($"忽略拖入的 {paths.Count} 个文件：没有支持的图片格式");
            return;
        }

        if (files.Count < paths.Count)
        {
            Log($"拖入的 {paths.Count} 个文件里有 {paths.Count - files.Count} 个不是支持的图片，已忽略");
        }

        AddStickerFiles(files);

        // 批量贴图的状态提示只覆盖贴上的那些；这里补一句「忽略了几张」，用户才知道没漏
        if (files.Count < paths.Count)
        {
            StatusText += $"（拖入的 {paths.Count} 个文件里有 {paths.Count - files.Count} 个不是支持的图片，已忽略）";
        }
    }

    /// <summary>
    /// 从剪贴板贴图：剪贴板里可能是「一张位图」（截图 / 网页复制），也可能是「复制的多个图片文件」。
    /// 读取与落盘在 <see cref="StickerSession.PasteClipboardAsync"/> 里完成，这里只负责状态提示和刷新列表。
    /// </summary>
    private async Task PasteClipboardAsync()
    {
        var result = await Session.PasteClipboardAsync();

        RefreshRecentItems();
        RefreshStickers();

        if (Session.LastNotice is { Length: > 0 } notice) Log("提示：" + notice);

        if (result.Error is not null)
        {
            StatusText = result.Error;
            Log("从剪贴板贴图失败：" + result.Error);
            return;
        }

        if (result.Added == 0 && result.Failed == 0)
        {
            StatusText = "剪贴板里没有图片：先复制一张图片，或在资源管理器里复制图片文件（可多选）";
            return;
        }

        if (result.Added == 0)
        {
            StatusText = $"剪贴板里的 {result.Failed} 张图片没能贴到桌面";
            Log($"从剪贴板贴图失败：{result.Failed} 张（原因见上方状态提示）");
            return;
        }

        StatusText = result.Failed > 0
            ? $"已从剪贴板贴了 {result.Added} 张，失败 {result.Failed} 张（{Session.LastError}）"
            : $"已从剪贴板贴了 {result.Added} 张图片";
        Log(result.Failed > 0
            ? $"从剪贴板贴到桌面：{result.Added} 张成功、{result.Failed} 张失败"
            : $"从剪贴板贴到桌面：{result.Added} 张");
    }

    private void RestoreStickers()
    {
        var restored = Session.RestoreFromConfig();
        RefreshStickers();

        if (restored > 0)
        {
            StatusText = $"已恢复 {restored} 张贴纸";
            Log($"已按保存的记录恢复 {restored} 张贴纸");
            return;
        }

        if (Session.Windows.Count > 0)
        {
            StatusText = "没有需要恢复的贴纸（保存的记录都已经在桌面上了）";
        }
        else
        {
            StatusText = "没有可恢复的贴纸（保存的图片可能已经不存在）";
        }
    }

    // ===================== 最近使用 =====================

    /// <summary>按配置重建最近使用列表，并在后台逐张解缩略图。</summary>
    private void RefreshRecentItems()
    {
        if (_suppressRecentRefresh) return;

        var generation = ++_recentGeneration;
        RecentItems.Clear();

        var entries = Cfg.RecentEntries ?? new List<RecentFileEntry>();
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) continue;
            RecentItems.Add(new RecentStickerItem(entry, AddRecentImage, RemoveRecentItem, RevealRecentItem));
        }

        OnPropertyChanged(nameof(HasRecentItems));
        UpdateSummary();

        if (RecentItems.Count == 0) return;

        // 后台解码：一张一张来，界面不会被卡住；过期的刷新（代号变了）直接放弃
        var items = RecentItems.ToList();
        Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (_closed || generation != _recentGeneration) return;

                var available = StickerImage.CanOpen(item.Path);
                var thumbnail = available ? StickerImage.LoadThumbnail(item.Path) : null;

                Ui(() =>
                {
                    if (_closed || generation != _recentGeneration) return;
                    item.UpdateThumbnail(thumbnail, available);
                });
            }
        });
    }

    /// <summary>点最近使用里的一项：把这张图贴到桌面（AddSticker 会顺手把它记到最近列表最前面）。</summary>
    private void AddRecentImage(RecentStickerItem? item)
    {
        if (item is null) return;

        if (Session.AddSticker(item.Path) is null)
        {
            if (!File.Exists(item.Path)) item.MarkUnavailable();
            StatusText = $"贴到桌面失败：{Session.LastError ?? "未知原因"}";
            Log($"贴图失败：{item.Path}（{Session.LastError ?? "未知原因"}）");
            return;
        }

        StatusText = $"已贴到桌面：{item.FileName}";
    }

    private void RemoveRecentItem(RecentStickerItem? item)
    {
        if (item is null) return;

        Cfg.RemoveRecent(item.Path);
        StatusText = $"已从最近使用里移除：{item.FileName}";
        Log($"移除最近使用记录：{item.Path}");
    }

    private void RevealRecentItem(RecentStickerItem? item)
    {
        if (item is null) return;

        if (!File.Exists(item.Path))
        {
            item.MarkUnavailable();
            StatusText = "图片已经不存在，没法在资源管理器里显示";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.Path}\"",
                UseShellExecute = true,
            });
            StatusText = $"已在资源管理器中显示：{item.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = "打开资源管理器失败";
            Log($"在资源管理器中显示失败：{ex.Message}");
        }
    }

    private void ClearRecent()
    {
        Cfg.ClearRecent();
        RefreshRecentItems();
        StatusText = "已清空最近使用记录（不会删除本地图片）";
        Log("已清空最近使用记录");
    }

    // ===================== 贴纸列表 =====================

    /// <summary>按会话里的窗口列表刷新贴纸集合（能对上的就地更新，避免整表重建导致闪烁）。</summary>
    private void RefreshStickers()
    {
        if (_suppressStickerRefresh) return;

        var windows = Session.Windows;
        var sameOrder = Stickers.Count == windows.Count;
        if (sameOrder)
        {
            for (var i = 0; i < windows.Count; i++)
            {
                if (!ReferenceEquals(Stickers[i].Window, windows[i]))
                {
                    sameOrder = false;
                    break;
                }
            }
        }

        if (sameOrder)
        {
            foreach (var item in Stickers) item.SyncFromWindow();
        }
        else
        {
            Stickers.Clear();
            foreach (var window in windows) Stickers.Add(CreateStickerItem(window));
        }

        OnPropertyChanged(nameof(HasStickers));
        UpdateSummary();
    }

    private StickerListItem CreateStickerItem(StickerWindow window)
    {
        var item = new StickerListItem(window, Session, MoveStickerUp, MoveStickerDown, CloseSticker);
        LoadStickerThumbnail(item);
        return item;
    }

    private void LoadStickerThumbnail(StickerListItem item)
    {
        var path = item.Path;
        if (path.Length == 0) return;

        if (_thumbnailCache.TryGetValue(path, out var cached))
        {
            item.SetThumbnail(cached);
            return;
        }

        Task.Run(() =>
        {
            var thumbnail = StickerImage.LoadThumbnail(path, ListThumbnailWidth);
            Ui(() =>
            {
                _thumbnailCache[path] = thumbnail;
                if (!_closed && Stickers.Contains(item)) item.SetThumbnail(thumbnail);
            });
        });
    }

    private void MoveStickerUp(StickerListItem? item)
    {
        if (item is null) return;

        if (!Session.MoveStickerUp(item.Window))
        {
            StatusText = $"「{item.FileName}」已经在最上层";
            return;
        }

        StatusText = $"已上移：「{item.FileName}」";
    }

    private void MoveStickerDown(StickerListItem? item)
    {
        if (item is null) return;

        if (!Session.MoveStickerDown(item.Window))
        {
            StatusText = $"「{item.FileName}」已经在最下层";
            return;
        }

        StatusText = $"已下移：「{item.FileName}」";
    }

    private void CloseSticker(StickerListItem? item)
    {
        if (item is null) return;

        Session.RemoveSticker(item.Window);
        StatusText = $"已关闭贴纸：{item.FileName}";
        Log($"关闭贴纸：{item.Path}");
    }

    private void SetAllTopmost(bool topmost)
    {
        var windows = Session.Windows.ToList();
        if (windows.Count == 0)
        {
            StatusText = "桌面上还没有贴纸";
            return;
        }

        _suppressStickerRefresh = true;
        try
        {
            foreach (var window in windows) Session.SetTopmost(window, topmost);
        }
        finally
        {
            _suppressStickerRefresh = false;
        }

        RefreshStickers();
        StatusText = topmost
            ? $"已把 {windows.Count} 张贴纸全部设为置顶"
            : $"已取消 {windows.Count} 张贴纸的置顶";
        Log(StatusText);
    }

    private void CloseAllStickers()
    {
        var count = Session.Windows.Count;
        if (count == 0)
        {
            StatusText = "桌面上还没有贴纸";
            return;
        }

        var confirm = MessageBox.Show(
            $"确定关闭全部 {count} 张贴纸？\n\n"
            + "关闭后会同时清空保存的贴纸记录（图片文件不会被删除），下次启动不会自动恢复。",
            "关闭全部贴纸",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        Session.CloseAllStickers();
        RefreshStickers();
        StatusText = "已关闭全部贴纸";
        Log("已关闭全部贴纸并清空保存记录");
    }

    // ===================== 恢复默认（全部贴纸） =====================

    private void ResetAllSizes()
    {
        if (Session.Windows.Count == 0)
        {
            StatusText = "桌面上还没有贴纸";
            return;
        }

        var count = Session.ResetAllStickerSizes();
        RefreshStickers();
        StatusText = $"已把 {count} 张贴纸恢复默认大小（图片原始尺寸）";
        Log(StatusText);
    }

    private void ResetAllSpeeds()
    {
        if (Session.Windows.Count == 0)
        {
            StatusText = "桌面上还没有贴纸";
            return;
        }

        var count = Session.ResetAllStickerSpeeds();
        RefreshStickers();
        StatusText = $"已把 {count} 张贴纸的 GIF 播放速度恢复默认";
        Log(StatusText);
    }

    private void ResetAllDefaults()
    {
        var count = Session.Windows.Count;
        if (count == 0)
        {
            StatusText = "桌面上还没有贴纸";
            return;
        }

        var confirm = MessageBox.Show(
            $"确定把全部 {count} 张贴纸恢复默认？\n\n"
            + "大小 / GIF 播放速度 / 置顶都会恢复成默认，图片和当前位置不变。",
            "恢复默认贴纸",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        Session.ResetAllStickerDefaults();
        RefreshStickers();
        StatusText = $"已把 {count} 张贴纸全部恢复默认（位置未变）";
        Log(StatusText);
    }

    // ===================== 设置 =====================

    private void ResetSettings()
    {
        Cfg.ResetPreferences();
        Cfg.SaveNow();   // 恢复默认是用户点出来的明确动作，立刻落盘，不等防抖

        // ResetPreferences 只改持久化字段、不发通知；这里手动把界面刷新一遍
        OnPropertyChanged(nameof(AutoRestoreOnStartup));
        OnPropertyChanged(nameof(DefaultTopmost));
        OnPropertyChanged(nameof(DefaultGifSpeed));
        OnPropertyChanged(nameof(DefaultGifSpeedText));
        OnPropertyChanged(nameof(RecentLimitText));
        OnPropertyChanged(nameof(RecentSummaryText));

        StatusText = "已恢复默认设置（桌面上的贴纸与最近使用记录未改动）";
        Log("已恢复默认设置");
    }

    private void OpenConfigFolder()
    {
        ConfigStore.OpenInExplorer();
        StatusText = $"已打开配置文件夹：{ConfigStore.Directory}";
    }

    // ===================== 事件 / 生命周期 =====================

    private void OnStickersChanged(object? sender, EventArgs e) => Ui(RefreshStickers);

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScreenStickerConfig.RecentEntries)) Ui(RefreshRecentItems);
    }

    private void UpdateSummary()
    {
        OnPropertyChanged(nameof(StickerSummaryText));
        OnPropertyChanged(nameof(StickerCountText));
        OnPropertyChanged(nameof(RecentSummaryText));
    }

    /// <summary>页面关闭（切走）时退订事件、停掉后台解码，并立刻落盘贴纸状态。</summary>
    protected override void OnClosed()
    {
        _closed = true;
        _recentGeneration++;

        try { Session.StickersChanged -= OnStickersChanged; } catch { /* 退出流程忽略 */ }
        try { Cfg.PropertyChanged -= OnConfigChanged; } catch { /* 退出流程忽略 */ }

        Session.SaveNow();
    }
}
