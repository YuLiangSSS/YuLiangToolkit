using System.Collections.ObjectModel;
using System.Windows;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.Views;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App.ViewModels;

/// <summary>侧边栏导航项。</summary>
public class NavItem
{
    public string Title { get; }
    public string Emoji { get; }
    public ITool? Tool { get; }
    public bool IsHome => Tool is null;

    private readonly Action _navigate;

    public NavItem(string title, string emoji, ITool? tool, Action navigate)
    {
        Title = title;
        Emoji = emoji;
        Tool = tool;
        _navigate = navigate;
    }

    public void Navigate() => _navigate();
}

/// <summary>
/// 主窗口视图模型：管理侧边栏、主页（搜索 + 排序）与工具导航。
///
/// <para>主页状态存哪：搜索词只留在内存（重启不保留，免得下次打开发现“我的功能不见了”）；
/// 排序方式与每个功能的“上次使用时间”存进 <c>Config\shell\config.json</c>（见 <see cref="ShellConfig"/>，
/// 外壳也算一个“功能”，所以同样走 <see cref="ToolConfig"/> 自动落盘）。</para>
/// </summary>
public class MainViewModel : ObservableObject
{
    /// <summary>外壳自己的功能 Id（配置放在 Config\shell\ 下）。</summary>
    private const string ShellToolId = "shell";

    private readonly ToolConfigStore _shellStore;
    private readonly ShellConfig _shell;
    private readonly List<ToolCard> _allCards = new();
    private bool _syncingNav;

    public ObservableCollection<NavItem> NavItems { get; } = new();

    /// <summary>全部已发现的功能（未过滤）。</summary>
    public ObservableCollection<ITool> Tools { get; } = new();

    /// <summary>主页实际展示的卡片（已按搜索词过滤 + 按当前排序方式排好）。</summary>
    public ObservableCollection<ToolCard> ToolCards { get; } = new();

    /// <summary>所有功能用到的标签（去重排序），用于主页“快速筛选”。</summary>
    public IReadOnlyList<string> AllTags { get; }

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        private set => Set(ref _currentView, value);
    }

    private bool _backVisible;
    public bool BackVisible
    {
        get => _backVisible;
        private set => Set(ref _backVisible, value);
    }

    private NavItem? _selectedNavItem;
    public NavItem? SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (Set(ref _selectedNavItem, value) && !_syncingNav) value?.Navigate();
        }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private string _currentTitle = "工具集主页";
    public string CurrentTitle
    {
        get => _currentTitle;
        set => Set(ref _currentTitle, value);
    }

    // ---------- 搜索 ----------

    private string _searchText = "";

    /// <summary>搜索词：空格分隔多个关键词（都要命中）。匹配名称 / 分类 / 标签 / 描述 / Id。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            OnPropertyChanged(nameof(HasSearchText));
            OnPropertyChanged(nameof(ShowTagBar));
            RefreshToolCards();
        }
    }

    /// <summary>搜索框里有没有内容（用来显示“清空”按钮）。</summary>
    public bool HasSearchText => !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>没在搜索时才显示标签快捷筛选条（有搜索词时让位给结果）。</summary>
    public bool ShowTagBar => !HasSearchText;

    private string _filterSummary = "";
    /// <summary>搜索框右侧计数，例如 "匹配 2 / 12 个功能"。</summary>
    public string FilterSummary
    {
        get => _filterSummary;
        private set => Set(ref _filterSummary, value);
    }

    private bool _hasNoResults;
    /// <summary>搜索无结果（显示空状态提示）。</summary>
    public bool HasNoResults
    {
        get => _hasNoResults;
        private set => Set(ref _hasNoResults, value);
    }

    private string _noResultHint = "";
    public string NoResultHint
    {
        get => _noResultHint;
        private set => Set(ref _noResultHint, value);
    }

    // ---------- 排序 ----------

    /// <summary>排序下拉框选项。</summary>
    public IReadOnlyList<OptionItem<ToolSortMode>> SortOptions { get; } = OptionItem.From(
        (ToolSortMode.Default, "默认顺序"),
        (ToolSortMode.RecentlyUsed, "最近使用"),
        (ToolSortMode.NameAsc, "名称 A→Z"),
        (ToolSortMode.NameDesc, "名称 Z→A"),
        (ToolSortMode.Category, "按分类"));

    /// <summary>当前排序（改一下即写入 Config\shell\config.json，重启后保持）。</summary>
    public OptionItem<ToolSortMode> SelectedSort
    {
        get => OptionItem<ToolSortMode>.Find(SortOptions, _shell.SortMode) ?? SortOptions[0];
        set
        {
            if (value is null || value.Value == _shell.SortMode) return;
            _shell.SortMode = value.Value;   // 属性变更 → 框架自动落盘
            OnPropertyChanged();
            RefreshToolCards();
        }
    }

    // ---------- 命令 ----------

    public RelayCommand GoHomeCommand { get; }
    public RelayCommand OpenToolCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand SearchTagCommand { get; }

    /// <summary>点左下角迷你条 → 回到那个正在后台播放的工具。</summary>
    public RelayCommand OpenBackgroundToolCommand { get; }

    // ---------- 左下角迷你条（后台播放） ----------

    private FrameworkElement? _keptAliveView;
    private ITool? _keptAliveTool;
    private IBackgroundTool? _keptAliveBackground;
    private object? _miniPanelSource;
    private bool _miniPanelVisible;

    /// <summary>迷你条绑定用的对象（通常是那个工具的 ViewModel）；没有后台工具时为 null。</summary>
    public object? MiniPanelSource
    {
        get => _miniPanelSource;
        private set => Set(ref _miniPanelSource, value);
    }

    /// <summary>迷你条要不要显示。</summary>
    public bool MiniPanelVisible
    {
        get => _miniPanelVisible;
        private set => Set(ref _miniPanelVisible, value);
    }

    public MainViewModel(IEnumerable<ITool> tools)
    {
        _shellStore = ToolConfigStore.For(ShellToolId);
        _shell = _shellStore.Load<ShellConfig>();

        GoHomeCommand = new RelayCommand(_ => GoHome());
        OpenToolCommand = new RelayCommand(p =>
        {
            if (p is ITool t) OpenTool(t);
            else if (p is ToolCard c) OpenTool(c.Tool);
        });
        ClearSearchCommand = new RelayCommand(_ => SearchText = "");
        SearchTagCommand = new RelayCommand(p =>
        {
            if (p is string tag && !string.IsNullOrWhiteSpace(tag)) SearchText = tag;
        });
        OpenBackgroundToolCommand = new RelayCommand(_ =>
        {
            if (_keptAliveTool is { } tool) OpenTool(tool);
        });

        NavItems.Add(new NavItem("首页", "🏠", null, GoHome));
        foreach (var t in tools)
        {
            Tools.Add(t);
            NavItems.Add(new NavItem(t.Name, t.EmojiIcon, t, () => OpenTool(t)));
        }

        foreach (var tool in Tools)
        {
            _allCards.Add(new ToolCard(tool, _shell.GetLastUsed(tool.Id)));
        }

        AllTags = ToolFilter.CollectTags(_allCards);

        // 开发调试：YLT_HOME_SEARCH=关键词 可以在启动时预置搜索词（截图验证“筛选后/无结果”状态用）
        var presetSearch = Environment.GetEnvironmentVariable("YLT_HOME_SEARCH");
        if (!string.IsNullOrWhiteSpace(presetSearch)) _searchText = presetSearch;

        RefreshToolCards();

        var ffmpeg = FFmpegLocator.Find(null);
        StatusText = $"共加载 {Tools.Count} 个工具 · FFmpeg：{(ffmpeg is not null ? "已找到" : "未找到（可在工具内配置）")}"
                     + $" · 数据/配置目录：{AppPaths.Root} · v{AppVersion}";
        GoHome();

        // 截图用：YLT_MINI_DEMO=1 —— 打开播放器（配合 YLT_PLAYER_DEMO 塞几条假列表）再切回主页，
        // 让左下角的迷你条出现（就是“后台播放”的那个效果）
        if (Environment.GetEnvironmentVariable("YLT_MINI_DEMO") is not null) SeedMiniPanelDemo();
    }

    /// <summary>只给截图/冒烟用：开一下播放器再回主页，制造“正在后台播放”的状态。</summary>
    private void SeedMiniPanelDemo()
    {
        var player = Tools.FirstOrDefault(t => string.Equals(t.Id, "media-player", StringComparison.OrdinalIgnoreCase));
        if (player is null) return;
        OpenTool(player);
        GoHome();
    }

    /// <summary>程序版本（取自程序集，别再写死在状态栏里）。</summary>
    private static string AppVersion
        => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>按当前搜索词与排序方式重算主页卡片列表（逻辑在 <see cref="ToolFilter"/>，可脱离界面自检）。</summary>
    private void RefreshToolCards()
    {
        var matched = ToolFilter.Apply(_allCards, _searchText, _shell.SortMode);

        ToolCards.Clear();
        foreach (var card in matched)
        {
            // 只有按“最近使用”排序时才在卡片上显示“x 分钟前用过”
            card.ShowLastUsed = _shell.SortMode == ToolSortMode.RecentlyUsed && card.LastUsed is not null;
            ToolCards.Add(card);
        }

        HasNoResults = ToolCards.Count == 0;
        FilterSummary = HasSearchText
            ? $"匹配 {ToolCards.Count} / {_allCards.Count} 个功能"
            : $"共 {_allCards.Count} 个功能";

        NoResultHint = $"没有匹配「{_searchText.Trim()}」的功能。"
                       + "试试更短的关键词——名称、分类、标签、描述、Id 都能搜到；"
                       + "也可以用空格组合多个关键词（比如「视频 mp3」）。";
    }

    /// <summary>
    /// 切换功能前通知上一个工具：落盘未保存的配置、停止后台任务。
    /// <para>如果它说自己要「后台继续」（<see cref="IBackgroundTool"/>），就**把视图留着不销毁**
    /// —— 视图一销毁，里面的播放器也就没了；左下角的迷你条会接管暂停 / 换歌。</para>
    /// </summary>
    private void CloseCurrentTool()
    {
        if (CurrentView is not FrameworkElement fe || fe.DataContext is not ToolViewModelBase vm) return;

        vm.NotifyClosed();

        if (vm is IBackgroundTool background && background.KeepAliveOnLeave)
        {
            KeepAliveView(fe, vm, background);
            return;
        }

        ClearKeptAlive();
    }

    /// <summary>留住一个后台视图（只留一份；旧的直接丢掉，里面的播放器会随之释放）。</summary>
    private void KeepAliveView(FrameworkElement view, ToolViewModelBase vm, IBackgroundTool background)
    {
        if (ReferenceEquals(_keptAliveView, view)) return;

        ClearKeptAlive();

        _keptAliveView = view;
        _keptAliveTool = Tools.FirstOrDefault(t => t.Id == vm.ToolId);
        _keptAliveBackground = background;
        background.BackgroundStateChanged -= OnBackgroundStateChanged;
        background.BackgroundStateChanged += OnBackgroundStateChanged;

        UpdateMiniPanel();
    }

    /// <summary>放掉后台视图的引用（不主动停播放：要么它已经回到前台，要么让它自然释放）。</summary>
    private void ClearKeptAlive()
    {
        if (_keptAliveBackground is not null)
        {
            _keptAliveBackground.BackgroundStateChanged -= OnBackgroundStateChanged;
        }

        _keptAliveView = null;
        _keptAliveTool = null;
        _keptAliveBackground = null;
        UpdateMiniPanel();
    }

    private void OnBackgroundStateChanged(object? sender, EventArgs e) => UpdateMiniPanel();

    /// <summary>
    /// 迷你条只在「后台工具不在前台」且「它自己说该显示」时出现。
    /// 前台 / 后台的判定用视图实例比较，不需要工具自己汇报。
    /// </summary>
    private void UpdateMiniPanel()
    {
        var show = _keptAliveView is not null
                   && _keptAliveBackground is not null
                   && !ReferenceEquals(CurrentView, _keptAliveView)
                   && _keptAliveBackground.ShowMiniPanel;

        MiniPanelSource = show ? _keptAliveView!.DataContext : null;
        MiniPanelVisible = show;
    }

    /// <summary>记一笔“这个功能刚被用过”（“最近使用”排序与卡片提示都靠它）。</summary>
    private void MarkToolUsed(string toolId)
    {
        _shell.MarkUsed(toolId);   // 字典改动会被 ToolConfigStore 的后台内容比对捡到并落盘
        var card = _allCards.FirstOrDefault(c => c.Id == toolId);
        if (card is not null) card.LastUsed = DateTime.Now;
    }

    private void GoHome()
    {
        CloseCurrentTool();
        RefreshToolCards();   // 从功能页返回时刷新“最近使用”的顺序
        _syncingNav = true;
        SelectedNavItem = NavItems.FirstOrDefault(n => n.IsHome);
        _syncingNav = false;
        CurrentView = new HomeView { DataContext = this };
        BackVisible = false;
        CurrentTitle = "工具集主页";
        UpdateMiniPanel();
    }

    private void OpenTool(ITool tool)
    {
        CloseCurrentTool();
        MarkToolUsed(tool.Id);
        _syncingNav = true;
        SelectedNavItem = NavItems.FirstOrDefault(n => n.Tool == tool);
        _syncingNav = false;

        // 之前被“后台保活”的正是这个工具的视图 → 原样挂回去：播放不断，列表 / 设置状态都还在
        FrameworkElement? reused = null;
        if (_keptAliveView is { } kept && _keptAliveTool?.Id == tool.Id)
        {
            reused = kept;
            ClearKeptAlive();
        }

        CurrentView = reused ?? tool.CreateView();
        (reused?.DataContext as ToolViewModelBase)?.NotifyOpened();   // 恢复后台自动保存

        BackVisible = true;
        CurrentTitle = tool.Name;
        UpdateMiniPanel();
    }
}
