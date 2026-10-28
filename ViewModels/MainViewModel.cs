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

/// <summary>侧边栏导航项：首页 / 根层类别 / 根层工具。</summary>
public class NavItem
{
    public string Title { get; }
    public string Emoji { get; }
    public ITool? Tool { get; }

    /// <summary>根层类别节点（工具项 / 首页为 null）。</summary>
    public ToolCatalogNode? Node { get; }

    /// <summary>首页项：既不是工具也不是类别。</summary>
    public bool IsHome => Tool is null && Node is null;

    private readonly Action _navigate;

    public NavItem(string title, string emoji, ITool? tool, Action navigate)
        : this(title, emoji, tool, null, navigate)
    {
    }

    public NavItem(string title, string emoji, ITool? tool, ToolCatalogNode? node, Action navigate)
    {
        Title = title;
        Emoji = emoji;
        Tool = tool;
        Node = node;
        _navigate = navigate;
    }

    public void Navigate() => _navigate();
}

/// <summary>当前页面类型：主页 / 类别页 / 工具页（决定卡片墙、面包屑、侧边栏选中态）。</summary>
public enum ShellPageKind
{
    Home,
    Category,
    Tool,
}

/// <summary>
/// 主窗口视图模型：管理侧边栏、目录树导航（主页 / 类别页 / 工具页）、面包屑、主页搜索排序与设置窗口。
///
/// <para><b>目录结构</b>：工具可以声明任意层级的 <see cref="ITool.CategoryPath"/>，由
/// <see cref="ToolCatalog"/> 建树。主页显示「根层类别卡片 + 根层工具卡片」；
/// 点类别卡片进类别页（可继续下钻子类别）；顶部工具条的可点击面包屑由 <see cref="Breadcrumbs"/> 生成。
/// 没有声明 CategoryPath 的老工具仍然直接挂在主页根层，行为不变。</para>
///
/// <para><b>主页状态存哪</b>：搜索词只留在内存（重启不保留，免得下次打开发现“我的功能不见了”）；
/// 排序方式、每个功能的“上次使用时间”、粒子开关存进 <c>Config\shell\config.json</c>
/// （见 <see cref="ShellConfig"/>，外壳也算一个“功能”，所以同样走 <see cref="ToolConfig"/> 自动落盘）。</para>
/// </summary>
public class MainViewModel : ObservableObject
{
    /// <summary>外壳自己的功能 Id（配置放在 Config\shell\ 下）。</summary>
    private const string ShellToolId = "shell";

    private readonly ToolConfigStore _shellStore;
    private readonly ShellConfig _shell;
    private readonly List<ToolCard> _allCards = new();
    private readonly Dictionary<string, ToolCard> _cardById = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingNav;

    /// <summary>浏览器式导航历史（主页 / 类别页 / 工具页都算一次访问，见 <see cref="ShellLocation"/>）。</summary>
    private readonly NavigationHistory<ShellLocation> _history = new();

    /// <summary>true = 正在按历史后退/前进，此时访问页面不要再往历史里记一条。</summary>
    private bool _replayingHistory;

    /// <summary>目录树根节点（「主页」）。</summary>
    private ToolCatalogNode _catalogRoot = null!;

    /// <summary>类别页 / 主页当前所在的节点。</summary>
    private ToolCatalogNode _currentNode = null!;

    /// <summary>工具页：当前工具与它所属的类别节点。</summary>
    private ITool? _currentTool;
    private ToolCatalogNode _currentToolNode = null!;

    private ShellPageKind _pageKind = ShellPageKind.Home;

    /// <summary>主页设置窗口（同一时间只开一个，关掉后清空）。</summary>
    private ShellSettingsWindow? _settingsWindow;

    public ObservableCollection<NavItem> NavItems { get; } = new();

    /// <summary>全部已发现的功能（未过滤）。</summary>
    public ObservableCollection<ITool> Tools { get; } = new();

    /// <summary>当前页实际展示的工具卡片（已按搜索词过滤 + 按当前排序方式排好）。</summary>
    public ObservableCollection<ToolCard> ToolCards { get; } = new();

    /// <summary>当前页实际展示的类别卡片（主页 = 根层类别；类别页 = 子类别；搜索时为空）。</summary>
    public ObservableCollection<CategoryCard> CategoryCards { get; } = new();

    /// <summary>顶部工具条的可点击面包屑（主页永远有；类别 / 工具层级按路径生成）。</summary>
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();

    /// <summary>所有功能用到的标签（去重排序），用于主页“快速筛选”。</summary>
    public IReadOnlyList<string> AllTags { get; }

    // ---------- 页面 / 标题 ----------

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        private set => Set(ref _currentView, value);
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
    /// <summary>当前页面标题（主页 / 类别名 / 工具名；窗口标题与调试用）。</summary>
    public string CurrentTitle
    {
        get => _currentTitle;
        private set => Set(ref _currentTitle, value);
    }

    // ---------- 浏览器式导航历史（后退 / 前进） ----------

    /// <summary>能不能后退（工具条 ← 按钮的 IsEnabled；主页首次打开时为 false）。</summary>
    public bool CanGoBack => _history.CanGoBack;

    /// <summary>能不能前进（工具条 → 按钮的 IsEnabled；刚正常导航时为 false）。</summary>
    public bool CanGoForward => _history.CanGoForward;

    /// <summary>当前是不是主页。</summary>
    public bool IsHomePage => _pageKind == ShellPageKind.Home;

    /// <summary>当前是不是类别页。</summary>
    public bool IsCategoryPage => _pageKind == ShellPageKind.Category;

    /// <summary>当前是不是工具页。</summary>
    public bool IsToolPage => _pageKind == ShellPageKind.Tool;

    // ---------- 类别页标题区 ----------

    private string _currentNodeName = "工具集主页";
    public string CurrentNodeName
    {
        get => _currentNodeName;
        private set => Set(ref _currentNodeName, value);
    }

    private string _currentNodeEmoji = "🏠";
    public string CurrentNodeEmoji
    {
        get => _currentNodeEmoji;
        private set => Set(ref _currentNodeEmoji, value);
    }

    private string _currentNodeDescription = "";
    public string CurrentNodeDescription
    {
        get => _currentNodeDescription;
        private set
        {
            if (Set(ref _currentNodeDescription, value)) OnPropertyChanged(nameof(HasCurrentNodeDescription));
        }
    }

    /// <summary>当前类别有没有描述（没有就不显示那行，别留一条空白）。</summary>
    public bool HasCurrentNodeDescription => !string.IsNullOrWhiteSpace(_currentNodeDescription);

    private string _currentNodeSummary = "";
    /// <summary>类别页标题右侧的统计文案，例如「共 4 个工具 · 2 个子分类」。</summary>
    public string CurrentNodeSummary
    {
        get => _currentNodeSummary;
        private set => Set(ref _currentNodeSummary, value);
    }

    // ---------- 搜索 ----------

    private string _searchText = "";

    /// <summary>
    /// 搜索词：空格分隔多个关键词（都要命中）。匹配名称 / 分类 / 标签 / 描述 / Id。
    /// 主页有搜索词时在**全部工具**里扁平过滤（被收进类别的工具同样能搜到）；类别页则在本类别子树里过滤。
    /// </summary>
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
    /// <summary>当前页没有可显示的内容（显示空状态提示）。</summary>
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

    private bool _showCategoryCards;
    /// <summary>当前页要不要显示类别卡片区（有类别且不在搜索中）。</summary>
    public bool ShowCategoryCards
    {
        get => _showCategoryCards;
        private set => Set(ref _showCategoryCards, value);
    }

    private bool _showToolCards;
    /// <summary>当前页要不要显示工具卡片区。</summary>
    public bool ShowToolCards
    {
        get => _showToolCards;
        private set => Set(ref _showToolCards, value);
    }

    private string _toolSectionTitle = "全部功能";
    /// <summary>工具卡片区的小标题（全部功能 / 搜索结果 / 工具）。</summary>
    public string ToolSectionTitle
    {
        get => _toolSectionTitle;
        private set => Set(ref _toolSectionTitle, value);
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

    // ---------- 主页设置（粒子开关等） ----------

    /// <summary>
    /// 是否绘制鼠标粒子效果（默认开）。改动直接写进 <c>Config\shell\config.json</c> 的
    /// <see cref="ShellConfig.ShowParticles"/>，由 <see cref="ToolConfigStore"/> 自动落盘。
    /// </summary>
    public bool ParticlesEnabled
    {
        get => _shell.ShowParticles;
        set
        {
            if (_shell.ShowParticles == value) return;
            _shell.ShowParticles = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「点 × 时最小化到托盘」是否选中（主页设置里的两个单选项之一）。
    /// 读写 <see cref="ShellConfig.CloseAction"/>，由 <see cref="ToolConfigStore"/> 自动落盘。
    /// <para>两个单选项各绑一个 bool 属性：WPF 取消选中时会传 false，这里**只处理 true**，
    /// 否则两个属性会互相把对方写回去。</para>
    /// </summary>
    public bool MinimizeToTrayOnClose
    {
        get => _shell.CloseAction == ShellCloseAction.MinimizeToTray;
        set
        {
            if (!value || MinimizeToTrayOnClose) return;
            _shell.CloseAction = ShellCloseAction.MinimizeToTray;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExitOnClose));
        }
    }

    /// <summary>「点 × 时关闭应用」是否选中（默认）。见 <see cref="MinimizeToTrayOnClose"/>。</summary>
    public bool ExitOnClose
    {
        get => _shell.CloseAction == ShellCloseAction.Exit;
        set
        {
            if (!value || ExitOnClose) return;
            _shell.CloseAction = ShellCloseAction.Exit;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MinimizeToTrayOnClose));
        }
    }

    // ---------- 命令 ----------

    public RelayCommand GoHomeCommand { get; }
    public RelayCommand OpenToolCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand SearchTagCommand { get; }

    /// <summary>进某个类别页（参数可为 <see cref="CategoryCard"/> / <see cref="ToolCatalogNode"/> / 类别名）。</summary>
    public RelayCommand NavigateCategoryCommand { get; }

    /// <summary>后退：按访问顺序回到上一个页面（主页 / 类别页 / 工具页都算）。</summary>
    public RelayCommand GoBackCommand { get; }

    /// <summary>前进：回到后退之前所在的页面。</summary>
    public RelayCommand GoForwardCommand { get; }

    /// <summary>
    /// 返回上一级：工具 → 所属类别；类别 → 父类别 / 主页（**只在类别页顶部 / 空状态里用**；
    /// 顶部工具条已经换成浏览器式的后退 / 前进按钮了）。
    /// </summary>
    public RelayCommand NavigateParentCommand { get; }

    /// <summary>点面包屑跳转（参数是 <see cref="BreadcrumbItem"/>）。</summary>
    public RelayCommand NavigateBreadcrumbCommand { get; }

    /// <summary>打开「主页设置」窗口（标题区右上角 ⚙ 按钮）。</summary>
    public RelayCommand OpenSettingsCommand { get; }

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
        NavigateCategoryCommand = new RelayCommand(p =>
        {
            switch (p)
            {
                case CategoryCard card:
                    NavigateToNode(card.Node);
                    break;
                case ToolCatalogNode node:
                    NavigateToNode(node);
                    break;
                case string name when !string.IsNullOrWhiteSpace(name):
                    if (_catalogRoot.FindByPath(new[] { name }) is { IsRoot: false } found) NavigateToNode(found);
                    break;
            }
        });
        GoBackCommand = new RelayCommand(_ => GoBack());
        GoForwardCommand = new RelayCommand(_ => GoForward());
        NavigateParentCommand = new RelayCommand(_ => NavigateParent());
        NavigateBreadcrumbCommand = new RelayCommand(p =>
        {
            if (p is BreadcrumbItem item) NavigateBreadcrumb(item);
        });
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        OpenBackgroundToolCommand = new RelayCommand(_ =>
        {
            if (_keptAliveTool is { } tool) OpenTool(tool);
        });

        BuildCatalog(tools);

        AllTags = ToolFilter.CollectTags(_allCards);

        // 开发调试：YLT_HOME_SEARCH=关键词 可以在启动时预置搜索词（截图验证“筛选后/无结果”状态用）
        var presetSearch = Environment.GetEnvironmentVariable("YLT_HOME_SEARCH");
        if (!string.IsNullOrWhiteSpace(presetSearch)) _searchText = presetSearch;

        var ffmpeg = FFmpegLocator.Find(null);
        StatusText = $"共加载 {Tools.Count} 个工具 · {_catalogRoot.Children.Count} 个类别"
                     + $" · FFmpeg：{(ffmpeg is not null ? "已找到" : "未找到（可在工具内配置）")}"
                     + $" · 数据/配置目录：{AppPaths.Root} · v{AppVersion}";

        GoHome();

        // 截图用：YLT_MINI_DEMO=1 —— 打开播放器（配合 YLT_PLAYER_DEMO 塞几条假列表）再切回主页，
        // 让左下角的迷你条出现（就是“后台播放”的那个效果）
        if (Environment.GetEnvironmentVariable("YLT_MINI_DEMO") is not null) SeedMiniPanelDemo();
    }

    /// <summary>建目录树、卡片索引与侧边栏（顺序：首页 + 根层类别 + 根层工具）。</summary>
    private void BuildCatalog(IEnumerable<ITool> tools)
    {
        var toolList = tools.Where(t => t is not null).ToList();
        _catalogRoot = ToolCatalog.Build(toolList, ShellCategories.All);
        _currentNode = _catalogRoot;
        _currentToolNode = _catalogRoot;

        foreach (var tool in toolList)
        {
            Tools.Add(tool);
            var card = new ToolCard(tool, _shell.GetLastUsed(tool.Id));
            _allCards.Add(card);
            _cardById[tool.Id] = card;
        }

        NavItems.Clear();
        NavItems.Add(new NavItem("首页", "🏠", null, GoHome));
        foreach (var category in _catalogRoot.Children)
        {
            var node = category;
            NavItems.Add(new NavItem(category.Name, category.EmojiIcon, null, node, () => NavigateToNode(node)));
        }
        foreach (var tool in _catalogRoot.Tools)
        {
            var t = tool;
            NavItems.Add(new NavItem(t.Name, t.EmojiIcon, t, () => OpenTool(t)));
        }
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

    // ---------- 导航 ----------

    /// <summary>回主页：显示根层类别卡片 + 根层工具卡片。</summary>
    private void GoHome()
    {
        CloseCurrentTool();
        _pageKind = ShellPageKind.Home;
        _currentNode = _catalogRoot;
        _currentTool = null;
        _currentToolNode = _catalogRoot;

        RefreshToolCards();

        _syncingNav = true;
        SelectedNavItem = NavItems.FirstOrDefault(n => n.IsHome);
        _syncingNav = false;

        CurrentView = new HomeView { DataContext = this };
        UpdateBreadcrumbs();
        RefreshHeader();
        RecordHistory(ShellLocation.Home);
        CurrentTitle = "工具集主页";
        UpdateMiniPanel();
    }

    /// <summary>进类别页（node.IsRoot 时等价于回主页）。任意深度都走这一条。</summary>
    private void NavigateToNode(ToolCatalogNode node)
    {
        if (node.IsRoot)
        {
            GoHome();
            return;
        }

        CloseCurrentTool();
        _pageKind = ShellPageKind.Category;
        _currentNode = node;
        _currentTool = null;
        _currentToolNode = node;

        RefreshToolCards();

        _syncingNav = true;
        SelectedNavItem = FindNavItemForNode(node);
        _syncingNav = false;

        CurrentView = new CategoryView { DataContext = this };
        UpdateBreadcrumbs();
        RefreshHeader();
        RecordHistory(ShellLocation.ForCategory(node));
        CurrentTitle = node.Name;
        UpdateMiniPanel();
    }

    /// <summary>
    /// 返回上一级（**类别页顶部 / 空状态里的按钮**用；顶部工具条已经换成浏览器式后退 / 前进）：
    /// 工具 → 所属类别；类别 → 父类别；主页不动。
    /// </summary>
    private void NavigateParent()
    {
        switch (_pageKind)
        {
            case ShellPageKind.Tool:
                NavigateToNode(_currentToolNode);
                break;
            case ShellPageKind.Category:
                NavigateToNode(_currentNode.Parent ?? _catalogRoot);
                break;
            default:
                GoHome();
                break;
        }
    }

    // ---------- 浏览器式导航历史（后退 / 前进） ----------

    /// <summary>把一次“访问页面”记进历史。重复访问当前页不会新增；在历史中间导航会截断后面的“前进”记录。</summary>
    private void RecordHistory(ShellLocation location)
    {
        if (_replayingHistory) return;      // 按历史回放时不再记一条
        if (!_history.Push(location)) return;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    /// <summary>后退一格：把页面切回历史里的上一个位置（不再往历史里记）。</summary>
    private void GoBack()
    {
        if (!_history.TryGoBack(out var location)) return;
        ReplayHistory(location);
    }

    /// <summary>前进一格：把页面切回历史里的下一个位置。</summary>
    private void GoForward()
    {
        if (!_history.TryGoForward(out var location)) return;
        ReplayHistory(location);
    }

    /// <summary>
    /// 按历史记录重建页面。回放期间 <see cref="RecordHistory"/> 不记新历史；
    /// 工具页不刷新“最近使用”（回看旧页面 ≠ 刚打开这个功能）。
    /// </summary>
    private void ReplayHistory(ShellLocation location)
    {
        _replayingHistory = true;
        try
        {
            switch (location.Kind)
            {
                case ShellLocationKind.Home:
                    GoHome();
                    break;

                case ShellLocationKind.Category when location.Node is { } node:
                    NavigateToNode(node);
                    break;

                case ShellLocationKind.Tool when location.Tool is { } tool:
                    OpenTool(tool, markUsed: false);
                    break;
            }
        }
        finally
        {
            _replayingHistory = false;
        }

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    /// <summary>点面包屑：主页回主页、类别进类别页、工具打开工具。</summary>
    private void NavigateBreadcrumb(BreadcrumbItem item)
    {
        if (item.IsCurrent) return;
        if (item.Tool is { } tool)
        {
            OpenTool(tool);
            return;
        }
        if (item.Node is { } node) NavigateToNode(node);
    }

    /// <summary>
    /// 开发调试 / 截图用：按类别路径直接进类别页（例如 "音游工具" 或 "音游工具/谱面处理"）。
    /// 路径不存在返回 false，不影响正常启动。
    /// </summary>
    public bool NavigateToCategory(IEnumerable<string>? path)
    {
        var node = _catalogRoot.FindByPath(path);
        if (node is null || node.IsRoot) return false;
        NavigateToNode(node);
        return true;
    }

    /// <summary>侧边栏选中态：进入某类别子树时选中它的根类别；根层工具选中自己。</summary>
    private NavItem? FindNavItemForNode(ToolCatalogNode node)
    {
        if (node.IsRoot) return NavItems.FirstOrDefault(n => n.IsHome);
        var rootCategory = node.Breadcrumb.FirstOrDefault(n => !n.IsRoot);
        if (rootCategory is null) return NavItems.FirstOrDefault(n => n.IsHome);
        return NavItems.FirstOrDefault(n => n.Node is not null && ReferenceEquals(n.Node, rootCategory))
               ?? NavItems.FirstOrDefault(n => n.IsHome);
    }

    private NavItem? FindNavItemForTool(ITool tool)
    {
        if (tool.CategoryPath is { Count: > 0 })
        {
            var normalized = ToolCatalog.NormalizePath(tool.CategoryPath);
            var first = normalized.Count > 0 ? _catalogRoot.FindChild(normalized[0]) : null;
            if (first is not null)
            {
                var categoryItem = NavItems.FirstOrDefault(n => n.Node is not null && ReferenceEquals(n.Node, first));
                if (categoryItem is not null) return categoryItem;
            }
        }
        return NavItems.FirstOrDefault(n => ReferenceEquals(n.Tool, tool));
    }

    /// <summary>按当前页面类型、搜索词与排序方式重算「类别卡片 + 工具卡片 + 计数」。</summary>
    private void RefreshToolCards()
    {
        var keywords = ToolFilter.SplitKeywords(_searchText);
        var searching = keywords.Length > 0;

        // 类别卡片：只在「不搜索」时显示当前层的子类别
        CategoryCards.Clear();
        if (!searching)
        {
            var children = _pageKind == ShellPageKind.Category ? _currentNode.Children : _catalogRoot.Children;
            foreach (var child in children) CategoryCards.Add(new CategoryCard(child));
        }

        // 工具卡片：
        //   搜索中 → 主页在全部工具里扁平过滤（分类里的工具也能搜到）；类别页只在本类别子树里过滤；
        //   无搜索 → 主页显示根层工具；类别页显示当前类别下的**直接**工具（子类别用类别卡片继续下钻）。
        IReadOnlyList<ToolCard> visible;
        if (searching)
        {
            var scope = _pageKind == ShellPageKind.Category
                ? CardsOf(_currentNode.AllTools)
                : _allCards;
            visible = ToolFilter.Apply(scope, _searchText, _shell.SortMode);
        }
        else
        {
            var scope = _pageKind == ShellPageKind.Category
                ? CardsOf(_currentNode.Tools)
                : CardsOf(_catalogRoot.Tools);
            visible = ToolFilter.Sort(scope, _shell.SortMode);
        }

        ToolCards.Clear();
        foreach (var card in visible)
        {
            // 只有按“最近使用”排序时才在卡片上显示“x 分钟前用过”
            card.ShowLastUsed = _shell.SortMode == ToolSortMode.RecentlyUsed && card.LastUsed is not null;
            ToolCards.Add(card);
        }

        ShowCategoryCards = CategoryCards.Count > 0;
        ShowToolCards = ToolCards.Count > 0;
        ToolSectionTitle = searching
            ? "搜索结果"
            : (_pageKind == ShellPageKind.Category ? "工具" : "全部功能");

        HasNoResults = ToolCards.Count == 0 && CategoryCards.Count == 0;

        FilterSummary = searching
            ? (_pageKind == ShellPageKind.Category
                ? $"匹配 {ToolCards.Count} / {_currentNode.AllTools.Count} 个工具"
                : $"匹配 {ToolCards.Count} / {_allCards.Count} 个功能")
            : (_pageKind == ShellPageKind.Category
                ? (_currentNode.Children.Count > 0
                    ? $"共 {_currentNode.ToolCount} 个工具 · {_currentNode.Children.Count} 个子分类"
                    : $"共 {_currentNode.ToolCount} 个工具")
                : $"共 {_allCards.Count} 个功能");

        if (searching)
        {
            NoResultHint = $"没有匹配「{_searchText.Trim()}」的功能。"
                           + "试试更短的关键词——名称、分类、标签、描述、Id 都能搜到；"
                           + "也可以用空格组合多个关键词（比如「视频 mp3」）。";
        }
        else if (_pageKind == ShellPageKind.Category)
        {
            NoResultHint = $"「{_currentNode.Name}」下还没有工具。"
                           + "子类别会显示成上面的类别卡片，点进去就能看到里面的工具。";
        }
        else
        {
            NoResultHint = "还没有任何功能。把实现了 ITool 的 dll 放进程序目录或 plugins 目录，重启后就会出现在这里。";
        }
    }

    /// <summary>把一批工具换成它们对应的卡片（按 Id 去重，保持目录树里的顺序）。</summary>
    private IEnumerable<ToolCard> CardsOf(IEnumerable<ITool> tools)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (!_cardById.TryGetValue(tool.Id, out var card)) continue;
            if (!seen.Add(tool.Id)) continue;
            yield return card;
        }
    }

    /// <summary>重算顶部面包屑：主页永远第一枚；类别页按祖先链；工具页 = 父类别路径 + 工具名（当前项）。</summary>
    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();

        if (_pageKind == ShellPageKind.Tool && _currentTool is { } tool)
        {
            var chain = _currentToolNode.Breadcrumb;   // 根 → 所属类别
            for (var i = 0; i < chain.Count; i++)
            {
                var node = chain[i];
                Breadcrumbs.Add(node.IsRoot
                    ? BreadcrumbItem.ForHome(node, isCurrent: false, isFirst: i == 0)
                    : BreadcrumbItem.ForCategory(node, isCurrent: false, isFirst: i == 0));
            }
            Breadcrumbs.Add(BreadcrumbItem.ForTool(tool, isFirst: Breadcrumbs.Count == 0));
            return;
        }

        var nodes = _pageKind == ShellPageKind.Category ? _currentNode.Breadcrumb : _catalogRoot.Breadcrumb;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var isCurrent = i == nodes.Count - 1;
            Breadcrumbs.Add(node.IsRoot
                ? BreadcrumbItem.ForHome(node, isCurrent, isFirst: i == 0)
                : BreadcrumbItem.ForCategory(node, isCurrent, isFirst: i == 0));
        }
    }

    /// <summary>重算页面标题区（类别页 / 主页顶部的 emoji、标题、描述、统计）。</summary>
    private void RefreshHeader()
    {
        switch (_pageKind)
        {
            case ShellPageKind.Category:
                CurrentNodeName = _currentNode.Name;
                CurrentNodeEmoji = _currentNode.EmojiIcon;
                CurrentNodeDescription = _currentNode.Description;
                CurrentNodeSummary = _currentNode.Children.Count > 0
                    ? $"共 {_currentNode.ToolCount} 个工具 · {_currentNode.Children.Count} 个子分类"
                    : $"共 {_currentNode.ToolCount} 个工具";
                break;

            case ShellPageKind.Tool when _currentTool is { } tool:
                CurrentNodeName = tool.Name;
                CurrentNodeEmoji = tool.EmojiIcon;
                CurrentNodeDescription = tool.Description;
                CurrentNodeSummary = "";
                break;

            default:
                CurrentNodeName = "工具集主页";
                CurrentNodeEmoji = "🏠";
                CurrentNodeDescription = "";
                CurrentNodeSummary = $"共 {_allCards.Count} 个功能 · {_catalogRoot.Children.Count} 个类别";
                break;
        }

        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsCategoryPage));
        OnPropertyChanged(nameof(IsToolPage));
        OnPropertyChanged(nameof(ShowTagBar));
    }

    // ---------- 设置窗口 ----------

    /// <summary>
    /// 打开「主页设置」窗口（同一时间只开一个；已经开着就激活它）。
    /// 返回窗口实例，方便 <c>YLT_OPEN_SETTINGS</c> 截图模式直接抓它。
    /// </summary>
    public ShellSettingsWindow? OpenSettings(bool activate = true)
    {
        if (_settingsWindow is { IsLoaded: true } existing)
        {
            if (activate) existing.Activate();
            return existing;
        }

        var window = new ShellSettingsWindow
        {
            DataContext = this,
            Owner = Application.Current?.MainWindow,
            ShowActivated = activate,
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window)) _settingsWindow = null;
        };
        _settingsWindow = window;
        window.Show();
        return window;
    }

    /// <summary>
    /// 关掉「主页设置」窗口（如果有）。主窗口最小化到托盘时调用 ——
    /// 不然设置窗口作为主窗口的从属窗口会单独留在屏幕上。
    /// </summary>
    public void CloseSettingsWindow()
    {
        if (_settingsWindow is { IsLoaded: true } window) window.Close();
    }

    // ---------- 工具切换 / 后台保活 ----------

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

    /// <summary>打开一个工具页。<paramref name="markUsed"/>=false 用于按历史回放（不刷新“最近使用”）。</summary>
    private void OpenTool(ITool tool, bool markUsed = true)
    {
        CloseCurrentTool();
        if (markUsed) MarkToolUsed(tool.Id);

        _pageKind = ShellPageKind.Tool;
        _currentTool = tool;
        _currentToolNode = _catalogRoot.FindByPath(tool.CategoryPath) ?? _catalogRoot;
        _currentNode = _currentToolNode;

        _syncingNav = true;
        SelectedNavItem = FindNavItemForTool(tool);
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

        UpdateBreadcrumbs();
        RefreshHeader();
        RecordHistory(ShellLocation.ForTool(tool));
        CurrentTitle = tool.Name;
        UpdateMiniPanel();
    }
}
