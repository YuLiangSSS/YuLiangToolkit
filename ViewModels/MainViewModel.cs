using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.Themes;
using YuLiangToolkit.App.Views;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Effects;
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

/// <summary>当前页面类型：主页 / 类别页 / 工具页 / 主页设置页（决定卡片墙、面包屑、侧边栏选中态）。</summary>
public enum ShellPageKind
{
    Home,
    Category,
    Tool,

    /// <summary>主页设置页（v0.26.0 起从独立小窗改成页面，面包屑「主页 › 主页设置」）。</summary>
    Settings,
}

/// <summary>
/// 主窗口视图模型：管理侧边栏、目录树导航（主页 / 类别页 / 工具页 / 主页设置页）、面包屑、主页搜索排序、光标特效设置。
///
/// <para><b>目录结构</b>：工具可以声明任意层级的 <see cref="ITool.CategoryPath"/>，由
/// <see cref="ToolCatalog"/> 建树。主页显示「根层类别卡片 + 根层工具卡片」；
/// 点类别卡片进类别页（可继续下钻子类别）；顶部工具条的可点击面包屑由 <see cref="Breadcrumbs"/> 生成。
/// 没有声明 CategoryPath 的老工具仍然直接挂在主页根层，行为不变。</para>
///
/// <para><b>主页状态存哪</b>：搜索词只留在内存（重启不保留，免得下次打开发现“我的功能不见了”）；
/// 排序方式、每个功能的“上次使用时间”、界面主题、光标特效（自动 / 点击）存进 <c>Config\shell\config.json</c>
/// （见 <see cref="ShellConfig"/>，外壳也算一个“功能”，所以同样走 <see cref="ToolConfig"/> 自动落盘）。</para>
/// </summary>
public class MainViewModel : ObservableObject
{
    /// <summary>外壳自己的功能 Id（配置放在 Config\shell\ 下）。</summary>
    private const string ShellToolId = "shell";

    private readonly ToolConfigStore _shellStore;
    private readonly ShellConfig _shell;

    /// <summary>
    /// 开发 / 截图用的临时主题覆盖（环境变量 <c>YLT_THEME=liquid-glass</c>）。
    /// 有覆盖时只影响本次运行、**不写配置**；用户在设置里点一次主题就清掉覆盖、改回正常持久化。
    /// </summary>
    private UiTheme? _themeOverride;
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

    /// <summary>设置页里的「自动效果」勾选项（清单驱动，见 <see cref="CursorEffects"/>）。</summary>
    public IReadOnlyList<ShellEffectOption> AutoEffectOptions { get; private set; } = Array.Empty<ShellEffectOption>();

    /// <summary>设置页里的「点击效果」勾选项（可多选；与自动效果互不影响）。</summary>
    public IReadOnlyList<ShellEffectOption> ClickEffectOptions { get; private set; } = Array.Empty<ShellEffectOption>();

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

    /// <summary>当前是不是「主页设置」页（v0.26.0 起设置是页面而不是独立小窗）。</summary>
    public bool IsSettingsPage => _pageKind == ShellPageKind.Settings;

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

    /// <summary>没在搜索、且设置里没关掉标签墙时，才显示标签快捷筛选条（有搜索词时让位给结果）。</summary>
    public bool ShowTagBar => !HasSearchText && ShowHomeTags;

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

    // ---------- 主页标签墙（v0.33.0：3 行 + 横向滚动 / 展开 / 设置里可隐藏） ----------

    /// <summary>
    /// 截图 / 调试用：<c>YLT_HOME_TAGS=hidden</c> 临时隐藏标签墙（看设置里关掉后的样子），
    /// <c>YLT_HOME_TAGS=expanded</c> 临时展开；只影响本次运行、**不写配置**。
    /// </summary>
    private bool? _homeTagsVisibleOverride;

    /// <summary>
    /// 主页「快速筛选」标签墙要不要显示（「主页设置 → 主页显示」；读写
    /// <see cref="ShellConfig.ShowHomeTags"/>，自动落盘）。关掉后整块标签墙隐藏，
    /// 搜索框仍然能按标签搜到功能（标签本身还是卡片搜索索引的一部分）。
    /// </summary>
    public bool ShowHomeTags
    {
        get => _homeTagsVisibleOverride ?? _shell.ShowHomeTags;
        set
        {
            // 用户在设置页手动改过 → 以配置为准，清掉截图 / 调试用的临时覆盖
            var hadOverride = _homeTagsVisibleOverride is not null;
            _homeTagsVisibleOverride = null;

            if (_shell.ShowHomeTags == value)
            {
                if (!hadOverride) return;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowTagBar));
                return;
            }

            _shell.ShowHomeTags = value;
            if (!value) IsTagBarExpanded = false;   // 藏起来时顺手收起，免得下次显示直接是一大坨
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowTagBar));
        }
    }

    private bool _isTagBarExpanded;

    /// <summary>
    /// 标签墙当前是不是「展开全部」（只存内存、不写配置：应用启动时默认还是 3 行；
    /// 本次运行里切来切去会记住，回到主页不强制收起）。
    /// </summary>
    public bool IsTagBarExpanded
    {
        get => _isTagBarExpanded;
        private set
        {
            if (!Set(ref _isTagBarExpanded, value)) return;
            OnPropertyChanged(nameof(TagBarToggleText));
            OnPropertyChanged(nameof(TagBarToggleToolTip));
        }
    }

    /// <summary>展开 / 收起按钮的文案（展开 = 铺开全部标签，收起 = 回到 3 行 + 横向滚动）。</summary>
    public string TagBarToggleText => _isTagBarExpanded ? "收起 ▴" : "展开全部 ▾";

    /// <summary>展开 / 收起按钮的鼠标提示。</summary>
    public string TagBarToggleToolTip => _isTagBarExpanded
        ? "收起标签，回到 3 行 + 横向滚动"
        : "一次性铺开全部标签（按原来的换行方式），再点一下收起";

    /// <summary>标签数量文案（标签墙标题行右侧，例如「共 151 个标签」）。</summary>
    public string TagCountText => $"共 {AllTags.Count} 个标签";

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

    // ---------- 光标特效（自动效果 / 点击效果，v0.26.0） ----------
    //
    // 两套特效完全独立、各自可多选（清单见 Core/Effects/CursorEffects.cs）：
    //   自动效果：particle-trail 粒子拖尾 / cursor-glow 鼠标散光；
    //   点击效果：particle-burst 粒子爆炸 / dispersion 散光扩散 / glow-flash 光晕闪烁 / invert-circle 反色圆环。
    // 勾选状态存进 Config\shell\config.json 的 autoEffects / clickEffects（字符串数组）。
    // 设置页按清单自动渲染，以后加新特效不用改设置页 XAML。

    /// <summary>自动效果 Id 列表（逗号分隔，绑给 <c>ParticleOverlay.AutoEffects</c>）。</summary>
    public string AutoEffectIds => CursorEffects.Join(SelectedEffectIds(AutoEffectOptions));

    /// <summary>点击效果 Id 列表（逗号分隔，绑给 <c>ParticleOverlay.ClickEffects</c>）。</summary>
    public string ClickEffectIds => CursorEffects.Join(SelectedEffectIds(ClickEffectOptions));

    /// <summary>
    /// 当前勾选里有没有「粒子类」效果（拖尾 / 爆炸）—— 设置页用它决定「粒子颜色」卡是否可用：
    /// 只开散光 / 闪光时，粒子颜色没有意义，置灰即可。
    /// </summary>
    public bool HasParticleEffectSelected
        => IsEffectSelected(AutoEffectOptions, CursorEffects.ParticleTrail)
           || IsEffectSelected(ClickEffectOptions, CursorEffects.ParticleBurst);

    /// <summary>构建特效勾选项 + 迁移老配置（老版本只有 <c>showParticles</c> 布尔开关）。</summary>
    private void InitializeEffects()
    {
        // 老配置没有 autoEffects / clickEffects 字段时：沿用老的 showParticles 语义
        // （开 = 粒子拖尾 + 粒子爆炸；关 = 两个都空），保证升级后行为不变。
        var fallbackAuto = _shell.ShowParticles
            ? CursorEffects.DefaultSelection(CursorEffectTrigger.Auto)
            : Array.Empty<string>();
        var fallbackClick = _shell.ShowParticles
            ? CursorEffects.DefaultSelection(CursorEffectTrigger.Click)
            : Array.Empty<string>();

        var auto = CursorEffects.Normalize(_shell.AutoEffects, CursorEffectTrigger.Auto, fallbackAuto);
        var click = CursorEffects.Normalize(_shell.ClickEffects, CursorEffectTrigger.Click, fallbackClick);

        // 立刻写回配置：老配置文件在这一步补上两个数组字段（之后不再靠 showParticles 迁移）。
        _shell.AutoEffects = auto.ToList();
        _shell.ClickEffects = click.ToList();

        // 每个特效的参数滑块直接挂到它自己的 ShellEffectOption 上：设置页把滑块嵌在勾选框下面，
        // 只有勾选了该特效才显示（v0.37.3 起不再把所有参数堆在卡片底部）。
        var autoSliders = BuildAutoEffectSliders();
        var clickSliders = BuildClickEffectSliders();

        AutoEffectOptions = BuildEffectOptions(CursorEffectTrigger.Auto, auto, autoSliders);
        ClickEffectOptions = BuildEffectOptions(CursorEffectTrigger.Click, click, clickSliders);
    }

    /// <summary>
    /// 当前特效参数（绑给 <c>ParticleOverlay.Tuning</c>）：任何滑块一改就通知，渲染层立即生效。
    /// 值从配置读，越界 / 手改坏的值由 <see cref="CursorEffectTuning.Normalized"/> 兜住。
    /// </summary>
    public CursorEffectTuning EffectTuning => new CursorEffectTuning(
        _shell.TrailParticleCount,
        _shell.TrailParticleSize,
        _shell.CursorGlowRadius,
        _shell.BurstParticleCount,
        _shell.BurstRange,
        _shell.DispersionRadius,
        _shell.GlowFlashRadius,
        _shell.InvertCircleRadius,
        _shell.InvertCircleOverlapCancels).Normalized();

    /// <summary>
    /// 反色圆环：两个圆重叠时，重叠处是否**再反一次变回正色**（主页设置「反色圆环」参数下面的复选框）。
    /// 写配置并通知渲染层（<see cref="EffectTuning"/>）立即生效；关掉 = 后触发的圆直接盖住先前的。
    /// </summary>
    public bool InvertCircleOverlapCancels
    {
        get => _shell.InvertCircleOverlapCancels;
        set
        {
            if (_shell.InvertCircleOverlapCancels == value) return;
            _shell.InvertCircleOverlapCancels = value;
            OnPropertyChanged();
            NotifyTuningChanged();
        }
    }

    /// <summary>构造一条滑块：值变 → 写配置 + 通知 <see cref="EffectTuning"/> 让渲染层跟着改。</summary>
    private ShellSliderOption Slider(
        string id,
        string effectId,
        string label,
        string description,
        CursorEffectRange range,
        double value,
        string unit,
        Action<double> write)
        => new(id, effectId, label, description, range, value, unit, write);

    /// <summary>自动效果参数：粒子拖尾（数量 / 大小）+ 鼠标散光（半径）。</summary>
    private IReadOnlyList<ShellSliderOption> BuildAutoEffectSliders() => new List<ShellSliderOption>
    {
        Slider("trail-count", CursorEffects.ParticleTrail, "粒子拖尾 · 数量",
            "鼠标移动时每帧冒出的粒子数（个/帧）；调大更密，0.5 一档。",
            CursorEffectTuning.TrailCountRange, _shell.TrailParticleCount, " 个/帧",
            value => { _shell.TrailParticleCount = value; NotifyTuningChanged(); }),
        Slider("trail-size", CursorEffects.ParticleTrail, "粒子拖尾 · 大小",
            "粒子初始直径的倍率（1.0 = 原来的 5px 左右）。",
            CursorEffectTuning.TrailSizeRange, _shell.TrailParticleSize, " ×",
            value => { _shell.TrailParticleSize = value; NotifyTuningChanged(); }),
        Slider("glow-radius", CursorEffects.CursorGlow, "鼠标散光 · 半径",
            "鼠标位置那圈发散光的半径（DIP），调大更明显。",
            CursorEffectTuning.GlowRadiusRange, _shell.CursorGlowRadius, " DIP",
            value => { _shell.CursorGlowRadius = value; NotifyTuningChanged(); }),
    };

    /// <summary>点击效果参数：爆炸（数量 / 范围）、散光扩散半径、光晕闪烁半径、反色圆环半径。</summary>
    private IReadOnlyList<ShellSliderOption> BuildClickEffectSliders() => new List<ShellSliderOption>
    {
        Slider("burst-count", CursorEffects.ParticleBurst, "粒子爆炸 · 数量",
            "一次点击炸开多少颗（颗）；右键按同样比例更多。",
            CursorEffectTuning.BurstCountRange, _shell.BurstParticleCount, " 颗",
            value => { _shell.BurstParticleCount = value; NotifyTuningChanged(); }),
        Slider("burst-range", CursorEffects.ParticleBurst, "粒子爆炸 · 范围",
            "爆炸粒子飞出的距离倍率（1.0 = 原来的射程）。",
            CursorEffectTuning.BurstRangeRange, _shell.BurstRange, " ×",
            value => { _shell.BurstRange = value; NotifyTuningChanged(); }),
        Slider("dispersion-radius", CursorEffects.Dispersion, "散光扩散 · 半径",
            "点击处色散光环扩散到的最大半径（DIP）。",
            CursorEffectTuning.DispersionRadiusRange, _shell.DispersionRadius, " DIP",
            value => { _shell.DispersionRadius = value; NotifyTuningChanged(); }),
        Slider("flash-radius", CursorEffects.GlowFlash, "光晕闪烁 · 半径",
            "点击处那团柔光闪到多大（DIP）。",
            CursorEffectTuning.GlowFlashRadiusRange, _shell.GlowFlashRadius, " DIP",
            value => { _shell.GlowFlashRadius = value; NotifyTuningChanged(); }),
        Slider("invert-radius", CursorEffects.InvertCircle, "反色圆环 · 半径",
            "反色圆形区域的最大半径（DIP，直径 = 2 倍）；默认 150。",
            CursorEffectTuning.InvertRadiusRange, _shell.InvertCircleRadius, " DIP",
            value => { _shell.InvertCircleRadius = value; NotifyTuningChanged(); }),
    };

    /// <summary>滑块改动后通知渲染层（<c>ParticleOverlay.Tuning</c> 绑定的是这个属性）。</summary>
    private void NotifyTuningChanged() => OnPropertyChanged(nameof(EffectTuning));

    /// <summary>按清单建特效勾选项，并把属于它的参数滑块一起挂上（设置页嵌在勾选框下面显示）。</summary>
    private IReadOnlyList<ShellEffectOption> BuildEffectOptions(
        CursorEffectTrigger trigger,
        IReadOnlyList<string> selected,
        IReadOnlyList<ShellSliderOption> sliders)
        => CursorEffects.For(trigger)
            .Select(definition => new ShellEffectOption(
                definition,
                selected.Contains(definition.Id, StringComparer.OrdinalIgnoreCase),
                OnEffectSelectionChanged,
                sliders.Where(slider =>
                        string.Equals(slider.EffectId, definition.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList()))
            .ToList();

    /// <summary>任一特效勾选变化：写配置（自动落盘）+ 通知渲染层与「粒子颜色」可用性。</summary>
    private void OnEffectSelectionChanged()
    {
        _shell.AutoEffects = SelectedEffectIds(AutoEffectOptions).ToList();
        _shell.ClickEffects = SelectedEffectIds(ClickEffectOptions).ToList();

        OnPropertyChanged(nameof(AutoEffectIds));
        OnPropertyChanged(nameof(ClickEffectIds));
        OnPropertyChanged(nameof(HasParticleEffectSelected));
    }

    private static IReadOnlyList<string> SelectedEffectIds(IReadOnlyList<ShellEffectOption> options)
        => CursorEffects.For(options.Count > 0 ? options[0].Definition.Trigger : CursorEffectTrigger.Auto)
            .Where(definition => IsEffectSelected(options, definition.Id))
            .Select(definition => definition.Id)
            .ToList();

    private static bool IsEffectSelected(IReadOnlyList<ShellEffectOption> options, string id)
        => options.Any(option => option.IsSelected
                                 && string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));

    // ---------- 界面风格（第二套 UI：液态玻璃，v0.25.0） ----------

    /// <summary>
    /// 当前界面主题（读写 <see cref="ShellConfig.UiTheme"/>，由 <see cref="ToolConfigStore"/> 自动落盘）。
    /// 真正的换肤由 <see cref="ThemeManager.Apply"/> 完成（替换 App.xaml 里合并的主题字典），
    /// 界面其余部分只用 DynamicResource 跟着变，所以不用重启、设置窗口自己也即时变样。
    /// <para>启动时 App.OnStartup 会在建主窗口之前按它应用一次。</para>
    /// </summary>
    public UiTheme UiTheme => _themeOverride ?? _shell.UiTheme;

    /// <summary>设置窗口「默认」单选按钮（与 <see cref="LiquidGlassThemeSelected"/> 二选一，只处理 true）。</summary>
    public bool DefaultThemeSelected
    {
        get => UiTheme == UiTheme.Default;
        set { if (value) SelectUiTheme(UiTheme.Default); }
    }

    /// <summary>设置窗口「液态玻璃」单选按钮。</summary>
    public bool LiquidGlassThemeSelected
    {
        get => UiTheme == UiTheme.LiquidGlass;
        set { if (value) SelectUiTheme(UiTheme.LiquidGlass); }
    }

    /// <summary>当前是不是液态玻璃主题（主窗口 / 设置窗口用它显示 LiquidBackdrop 流动背景）。</summary>
    public bool IsLiquidGlass => UiTheme == UiTheme.LiquidGlass;

    /// <summary>用户在设置里换主题：清掉调试覆盖 → 写配置（自动落盘）→ 立即切换界面。</summary>
    private void SelectUiTheme(UiTheme theme)
    {
        _themeOverride = null;
        if (_shell.UiTheme != theme) _shell.UiTheme = theme;

        ThemeManager.Apply(theme);

        OnPropertyChanged(nameof(UiTheme));
        OnPropertyChanged(nameof(DefaultThemeSelected));
        OnPropertyChanged(nameof(LiquidGlassThemeSelected));
        OnPropertyChanged(nameof(IsLiquidGlass));
    }

    // ---------- 粒子颜色（v0.22.0） ----------

    /// <summary>
    /// 是否启用自定义粒子颜色（默认关 = 原来的淡紫白）。开启后颜色 1 / 2 才参与取色。
    /// 读写 <see cref="ShellConfig.ParticleCustomColors"/>，自动落盘。
    /// </summary>
    public bool ParticleCustomColorsEnabled
    {
        get => _shell.ParticleCustomColors;
        set
        {
            if (_shell.ParticleCustomColors == value) return;
            _shell.ParticleCustomColors = value;
            OnPropertyChanged();
        }
    }

    /// <summary>颜色 1 是否参与取色（默认开；总开关关着时不起作用）。</summary>
    public bool ParticleColor1Enabled
    {
        get => _shell.ParticleColor1Enabled;
        set
        {
            if (_shell.ParticleColor1Enabled == value) return;
            _shell.ParticleColor1Enabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>颜色 2 是否参与取色（默认开；只勾一种时粒子是单色）。</summary>
    public bool ParticleColor2Enabled
    {
        get => _shell.ParticleColor2Enabled;
        set
        {
            if (_shell.ParticleColor2Enabled == value) return;
            _shell.ParticleColor2Enabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>颜色 1（主页设置里的拾色器双向绑定；存 <c>#RRGGBB</c>）。</summary>
    public Color ParticleColor1Value
    {
        get => ParticleColors.Parse(_shell.ParticleColor1, ParticleColors.Default1);
        set
        {
            var hex = ParticleColors.ToHex(value);
            if (string.Equals(_shell.ParticleColor1, hex, StringComparison.Ordinal)) return;
            _shell.ParticleColor1 = hex;
            OnPropertyChanged();
        }
    }

    /// <summary>颜色 2（主页设置里的拾色器双向绑定；存 <c>#RRGGBB</c>）。</summary>
    public Color ParticleColor2Value
    {
        get => ParticleColors.Parse(_shell.ParticleColor2, ParticleColors.Default2);
        set
        {
            var hex = ParticleColors.ToHex(value);
            if (string.Equals(_shell.ParticleColor2, hex, StringComparison.Ordinal)) return;
            _shell.ParticleColor2 = hex;
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

    /// <summary>标签墙「展开全部 / 收起」（3 行横向滚动 ↔ 原来的换行铺开；只改内存状态，不写配置）。</summary>
    public RelayCommand ToggleTagBarExpandedCommand { get; }

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
        InitializeEffects();

        // 开发 / 截图：YLT_THEME=liquid-glass 临时切到第二套 UI（不写配置；见 ShellConfig.UiTheme）
        _themeOverride = ThemeManager.ParseThemeName(Environment.GetEnvironmentVariable("YLT_THEME"));

        // 截图 / 调试：YLT_HOME_TAGS=hidden 临时隐藏标签墙（验证「设置里关掉」的样子）、
        // =expanded 临时展开（同时强制显示，免得配置里刚好关着标签墙时截图截不到）；只影响本次运行，不写配置。
        switch (Environment.GetEnvironmentVariable("YLT_HOME_TAGS")?.Trim().ToLowerInvariant())
        {
            case "hidden" or "hide" or "off":
                _homeTagsVisibleOverride = false;
                break;
            case "expanded" or "expand" or "open":
                _homeTagsVisibleOverride = true;
                _isTagBarExpanded = true;
                break;
        }

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
        ToggleTagBarExpandedCommand = new RelayCommand(_ => IsTagBarExpanded = !IsTagBarExpanded);
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
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());   // 进「主页设置」页面（v0.26.0 起不再是独立窗口）
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
            case ShellPageKind.Settings:
                GoHome();
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

                case ShellLocationKind.Settings:
                    NavigateToSettings();
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

    /// <summary>点面包屑：主页回主页、类别进类别页、工具打开工具、主页设置进设置页。</summary>
    private void NavigateBreadcrumb(BreadcrumbItem item)
    {
        if (item.IsCurrent) return;
        if (item.IsSettings)
        {
            NavigateToSettings();
            return;
        }
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

    /// <summary>
    /// 重算顶部面包屑：主页永远第一枚；类别页按祖先链；工具页 = 父类别路径 + 工具名（当前项）；
    /// 主页设置页 = 主页 + 主页设置（当前项，v0.26.0）。
    /// </summary>
    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();

        if (_pageKind == ShellPageKind.Settings)
        {
            Breadcrumbs.Add(BreadcrumbItem.ForHome(_catalogRoot, isCurrent: false, isFirst: true));
            Breadcrumbs.Add(BreadcrumbItem.ForSettings(isCurrent: true, isFirst: false));
            return;
        }

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

            case ShellPageKind.Settings:
                CurrentNodeName = "主页设置";
                CurrentNodeEmoji = "⚙";
                CurrentNodeDescription = "外壳设置：界面风格、主页显示、自动 / 点击特效、粒子颜色与窗口行为";
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
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(ShowTagBar));
    }

    // ---------- 主页设置页（v0.26.0 起从独立小窗改成页面） ----------

    /// <summary>
    /// 打开「主页设置」页面（主页右上角 ⚙ / 托盘菜单都走它）。
    /// v0.26.0 起设置是主窗口里的一个页面：面包屑显示「主页 › 主页设置」，后退 / 前进也能回到它。
    /// </summary>
    public void OpenSettings() => NavigateToSettings();

    /// <summary>导航到「主页设置」页（重复进入不新增历史，由 <see cref="RecordHistory"/> 去重）。</summary>
    private void NavigateToSettings()
    {
        if (_pageKind == ShellPageKind.Settings) return;

        CloseCurrentTool();
        _pageKind = ShellPageKind.Settings;
        _currentTool = null;

        // 设置页不是功能列表里的一项：清掉侧边栏选中态，避免看起来像还在某个工具里
        _syncingNav = true;
        SelectedNavItem = null;
        _syncingNav = false;

        CurrentView = new ShellSettingsView { DataContext = this };
        UpdateBreadcrumbs();
        RefreshHeader();
        RecordHistory(ShellLocation.Settings);
        CurrentTitle = "主页设置";
        UpdateMiniPanel();
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
