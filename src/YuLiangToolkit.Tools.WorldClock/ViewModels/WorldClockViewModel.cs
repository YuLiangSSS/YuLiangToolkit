using System.Collections.ObjectModel;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.WorldClock.Models;
using YuLiangToolkit.Tools.WorldClock.Services;

namespace YuLiangToolkit.Tools.WorldClock.ViewModels;

/// <summary>
/// 「全球时钟」的 ViewModel：城市列表 + 添加 / 删除（可撤销）/ 锁定 / 拖动排序 / 时间刷新。
///
/// <para><b>刷新节奏</b>：一个 <see cref="DispatcherTimer"/> 每次只等「离下一整秒还差多少」，
/// 到点刷一次再重新对齐 —— 秒数不会跳变，也不用每 200ms 空转。
/// 只在页面可见时跑（<c>Loaded</c> 开 / <c>Unloaded</c> + <see cref="OnClosed"/> 停）。</para>
///
/// <para><b>为什么删除不弹确认框</b>：每删一只都弹一次太烦。改成 6 秒内可撤销，
/// 撤销时按原来的位置放回去（<see cref="UndoDeleteCommand"/>）。</para>
/// </summary>
public sealed class WorldClockViewModel : ToolViewModelBase
{
    /// <summary>撤销窗口（秒）。超过之后「撤销」按钮自动消失。</summary>
    public const int UndoSeconds = 6;

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _undoTimer;

    private WorldClockItem? _lastDeleted;
    private int _lastDeletedIndex;
    private bool _isPageVisible;

    public WorldClockViewModel() : base(WorldClockConfig.ToolId)
    {
        Cfg = LoadConfig<WorldClockConfig>();
        EnsureDefaultCities();

        AddCityCommand = new RelayCommand(p => AddCity(p as ClockCityOption), p => p is ClockCityOption);
        AddOffsetClockCommand = new RelayCommand(_ => AddOffsetClock());
        RemoveClockCommand = new RelayCommand(p => RemoveClock(p as WorldClockItem), p => p is WorldClockItem);
        ToggleLockCommand = new RelayCommand(p => ToggleLock(p as WorldClockItem), p => p is WorldClockItem);
        UndoDeleteCommand = new RelayCommand(_ => UndoDelete(), _ => _lastDeleted is not null);
        MoveUpCommand = new RelayCommand(p => MoveBy(p as WorldClockItem, -1), p => p is WorldClockItem);
        MoveDownCommand = new RelayCommand(p => MoveBy(p as WorldClockItem, 1), p => p is WorldClockItem);
        BringToTopCommand = new RelayCommand(p => BringToTop(p as WorldClockItem), p => p is WorldClockItem);
        SortByTimeCommand = new RelayCommand(_ => SortByTime());
        SortByNameCommand = new RelayCommand(_ => SortByName());
        CopyTimeCommand = new RelayCommand(p => CopyTime(p as WorldClockItem), p => p is WorldClockItem);
        CopyAllCommand = new RelayCommand(_ => CopyAll());
        LockAllCommand = new RelayCommand(_ => SetAllLocked(true));
        UnlockAllCommand = new RelayCommand(_ => SetAllLocked(false));
        AddStarterCitiesCommand = new RelayCommand(_ => AddStarterCities());

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTimerTick();

        _undoTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(UndoSeconds) };
        _undoTimer.Tick += (_, _) => ClearUndo();

        // 列表本身的变化（加 / 删 / 拖排序）也要刷新统计文字
        Cfg.Clocks.CollectionChanged += (_, _) => Ui(RefreshSummary);

        // 设置在右侧卡片里改（或从配置里手改）时，界面要立刻跟着变：
        // 换视图 / 关掉秒针都要重排计时器节奏。
        Cfg.PropertyChanged += OnConfigChanged;

        RefreshNow();
        RefreshSummary();
        Log($"配置文件：{Cfg.FilePath}");
        Log($"本机时区：{WorldClockTimeZones.LocalDisplayName}（{WorldClockTimeZones.LocalOffsetText(DateTime.UtcNow)}）");        if (IsFirstRun) Log("首次运行：已放入北京 / 东京 / 伦敦 / 纽约，可以随时删掉换成自己的城市");
    }

    // ---------- 配置与列表 ----------

    /// <summary>强类型配置（城市列表、视图、外观选项都在里面，改一下自动落盘）。</summary>
    public WorldClockConfig Cfg { get; }

    /// <summary>城市列表（就是配置里那一个，界面直接绑它）。</summary>
    public ObservableCollection<WorldClockItem> Clocks => Cfg.Clocks;

    // ---------- 命令 ----------

    public RelayCommand AddCityCommand { get; }
    public RelayCommand AddOffsetClockCommand { get; }
    public RelayCommand RemoveClockCommand { get; }
    public RelayCommand ToggleLockCommand { get; }
    public RelayCommand UndoDeleteCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand BringToTopCommand { get; }
    public RelayCommand SortByTimeCommand { get; }
    public RelayCommand SortByNameCommand { get; }
    public RelayCommand CopyTimeCommand { get; }
    public RelayCommand CopyAllCommand { get; }
    public RelayCommand LockAllCommand { get; }
    public RelayCommand UnlockAllCommand { get; }
    public RelayCommand AddStarterCitiesCommand { get; }

    // ---------- 界面状态 ----------

    private string _summaryText = "";

    /// <summary>页头那句「本机时区 UTC+08:00 · 共 N 个城市 · M 个已锁定」。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    private string _undoHint = "";

    /// <summary>「已删除「伦敦」· 撤销」里的那句，空 = 什么也不显示。</summary>
    public string UndoHint
    {
        get => _undoHint;
        private set
        {
            if (Set(ref _undoHint, value)) OnPropertyChanged(nameof(HasUndo));
        }
    }

    /// <summary>现在有没有可撤销的删除（控制撤销条显示）。</summary>
    public bool HasUndo => _lastDeleted is not null && !string.IsNullOrEmpty(UndoHint);

    private bool _hasClocks = true;

    /// <summary>列表里有没有钟（空状态提示用）。</summary>
    public bool HasClocks
    {
        get => _hasClocks;
        private set
        {
            if (Set(ref _hasClocks, value)) OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>空状态（一只钟都没有）。</summary>
    public bool IsEmpty => !HasClocks;

    private string _cityFilter = "";

    /// <summary>添加城市时的搜索关键词（中文 / 英文 / 国家名都行）。</summary>
    public string CityFilter
    {
        get => _cityFilter;
        set
        {
            if (!Set(ref _cityFilter, value ?? "")) return;
            RefreshCityMatches();
        }
    }

    private ObservableCollection<ClockCityOption> _cityMatches = new();

    /// <summary>搜索匹配到的城市（点一下加入）。</summary>
    public ObservableCollection<ClockCityOption> CityMatches
    {
        get => _cityMatches;
        private set => Set(ref _cityMatches, value);
    }

    private string _customName = "";

    /// <summary>「自定义偏移」的城市名（留空就自动写成 UTC+05:30 这种）。</summary>
    public string CustomName
    {
        get => _customName;
        set => Set(ref _customName, value ?? "");
    }

    private string _customOffsetText = "+0";

    /// <summary>「自定义偏移」的输入，接受 +8 / -5:30 / 5.5 / UTC+8 等写法。</summary>
    public string CustomOffsetText
    {
        get => _customOffsetText;
        set
        {
            if (Set(ref _customOffsetText, value ?? "")) OnPropertyChanged(nameof(CustomOffsetPreview));
        }
    }

    /// <summary>自定义偏移输入的实时解释（写成「UTC+05:30 · 比本地快 2 小时 30 分」）。</summary>
    public string CustomOffsetPreview
    {
        get
        {
            var minutes = ClockMath.ParseOffsetMinutes(CustomOffsetText);
            if (minutes is null) return "写法看不懂：可以写 +8 / -5:30 / 5.5 / UTC+8";

            var offset = TimeSpan.FromMinutes(minutes.Value);
            var diff = offset - ClockMath.LocalOffset(DateTime.UtcNow);
            return $"{ClockMath.FormatOffset(offset)} · {ClockMath.FormatDiff(diff)}";
        }
    }

    /// <summary>视图（绑定到配置；下拉框用 <see cref="ViewModeOptions"/>）。</summary>
    public string ViewMode
    {
        get => Cfg.SafeViewMode;
        set
        {
            if (Cfg.SafeViewMode == value) return;
            Cfg.ViewMode = string.Equals(value, WorldClockConfig.ViewList, StringComparison.Ordinal)
                ? WorldClockConfig.ViewList
                : WorldClockConfig.ViewAnalog;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SafeViewMode));
            OnPropertyChanged(nameof(IsAnalogView));
            OnPropertyChanged(nameof(IsListView));
            OnPropertyChanged(nameof(SelectedViewOption));
            RunTimer(needSeconds: IsAnalogView && Cfg.ShowSeconds);
        }
    }

    /// <summary>夹到合法值的视图（配置被手改坏了也不会让界面变成空白）。</summary>
    public string SafeViewMode => Cfg.SafeViewMode;

    /// <summary>当前是不是表盘视图（XAML 用它选模板）。</summary>
    public bool IsAnalogView => SafeViewMode == WorldClockConfig.ViewAnalog;

    /// <summary>当前是不是列表视图。</summary>
    public bool IsListView => !IsAnalogView;

    /// <summary>视图下拉框的选项（存干净的值、显示中文）。</summary>
    public IReadOnlyList<OptionItem<string>> ViewModeOptions { get; } = OptionItem.From(
        (WorldClockConfig.ViewAnalog, "表盘（一面钟墙）"),
        (WorldClockConfig.ViewList, "列表（一行一个城市）"));

    /// <summary>视图下拉框当前项。</summary>
    public OptionItem<string> SelectedViewOption
    {
        get => OptionItem<string>.Find(ViewModeOptions, SafeViewMode) ?? ViewModeOptions[0];
        set
        {
            if (value is null) return;
            ViewMode = value.Value;
        }
    }

    // ---------- 生命周期 ----------

    /// <summary>配置项变了（换视图 / 关秒针 / 改 12·24 小时制…）：界面与计时器跟上。</summary>
    private void OnConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorldClockConfig.ViewMode):
                OnPropertyChanged(nameof(ViewMode));
                OnPropertyChanged(nameof(SafeViewMode));
                OnPropertyChanged(nameof(IsAnalogView));
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(SelectedViewOption));
                RunTimer(needSeconds: IsAnalogView && Cfg.ShowSeconds);
                break;
            case nameof(WorldClockConfig.ShowSeconds):
                RunTimer(needSeconds: IsAnalogView && Cfg.ShowSeconds);
                RefreshNow();
                break;
            case nameof(WorldClockConfig.Hour24):
            case nameof(WorldClockConfig.ShowDate):
            case nameof(WorldClockConfig.ShowOffset):
                RefreshNow();
                break;
        }
    }

    /// <summary>页面挂到界面（Loaded）：开始走秒。</summary>
    public void OnPageLoaded()
    {
        _isPageVisible = true;
        RefreshNow();
        RunTimer(needSeconds: IsAnalogView && Cfg.ShowSeconds);
    }

    /// <summary>页面离开界面（Unloaded）：停表（外壳缓存页面时不占 CPU）。</summary>
    public void OnPageUnloaded()
    {
        _isPageVisible = false;
        _timer.Stop();
    }

    /// <summary>工具被切走 / 关闭：停表 + 停撤销计时（配置由基类落盘）。</summary>
    protected override void OnClosed()
    {
        _isPageVisible = false;
        _timer.Stop();
        _undoTimer.Stop();
    }

    /// <summary>
    /// 计时器节奏：需要秒针时对齐到下一整秒；不需要秒（列表视图 / 关了秒）时 20 秒刷一次就够
    /// —— 反正显示的只有「时:分」，到点再刷也不会晚一分钟。
    /// </summary>
    private void RunTimer(bool needSeconds)
    {
        _timer.Stop();
        if (!_isPageVisible) return;
        _timer.Interval = needSeconds
            ? TimeSpan.FromMilliseconds(ClockMath.DelayToNextSecond(DateTime.UtcNow))
            : TimeSpan.FromSeconds(20);
        _timer.Start();
    }

    private void OnTimerTick()
    {
        RefreshNow();
        // 每秒重新对齐（DispatcherTimer 的 Interval 只在启动时生效，不对齐会越走越飘）
        if (IsAnalogView && Cfg.ShowSeconds)
            _timer.Interval = TimeSpan.FromMilliseconds(ClockMath.DelayToNextSecond(DateTime.UtcNow));
    }

    /// <summary>立刻按「现在」刷新所有钟（计时器 / 页面打开 / 加了城市都调它）。</summary>
    public void RefreshNow()
    {
        var utcNow = DateTime.UtcNow;
        var localOffset = ClockMath.LocalOffset(utcNow);
        var localNow = ClockMath.ToWallClock(utcNow, localOffset);

        foreach (var clock in Clocks)
            RefreshClock(clock, utcNow, localNow, localOffset);

        OnPropertyChanged(nameof(CustomOffsetPreview));
    }

    /// <summary>单独刷新一只钟（新加 / 撤销恢复时用；口径与 <see cref="RefreshNow"/> 一致）。</summary>
    private void RefreshClock(WorldClockItem clock, DateTime utcNow, DateTime localNow, TimeSpan localOffset)
        => clock.Refresh(utcNow, localNow, localOffset,
            Cfg.Hour24, Cfg.ShowSeconds, Cfg.ShowDate, Cfg.ShowOffset, Cfg.ColorByDayNight);

    /// <summary>按当前配置刷一只钟（不必自己算 UTC，调用方懒得管时用）。</summary>
    private void RefreshClockNow(WorldClockItem clock)
    {
        var utcNow = DateTime.UtcNow;
        var localOffset = ClockMath.LocalOffset(utcNow);
        RefreshClock(clock, utcNow, ClockMath.ToWallClock(utcNow, localOffset), localOffset);
    }

    // ---------- 增删 ----------

    /// <summary>加入一个城市库里的城市（已经在列表里就只提示一句，不重复加）。</summary>
    public void AddCity(ClockCityOption? city)
    {
        if (city is null) return;

        var existing = Clocks.FirstOrDefault(c =>
            string.Equals(c.CityName, city.Name, StringComparison.Ordinal)
            && c.CustomOffsetMinutes is null);
        if (existing is not null)
        {
            StatusText = $"「{city.Name}」已经在列表里了";
            return;
        }

        var item = WorldClockItem.FromCity(city);
        Clocks.Add(item);
        RefreshClockNow(item);

        StatusText = $"已添加 {city.Name}（{ClockMath.FormatOffset(ClockMath.Resolve(city.TimeZoneId, null, DateTime.UtcNow).Offset)}）";
        Log($"添加 {city.Name} · {city.TimeZoneId}");
        RefreshSummary();
    }

    /// <summary>按手写的 UTC 偏移加入一只钟（城市库里没有的地方）。</summary>
    public void AddOffsetClock()
    {
        var minutes = ClockMath.ParseOffsetMinutes(CustomOffsetText);
        if (minutes is null)
        {
            StatusText = "偏移写法看不懂：可以写 +8 / -5:30 / 5.5 / UTC+8";
            return;
        }

        var item = WorldClockItem.FromOffset(CustomName, minutes.Value);
        Clocks.Add(item);
        RefreshClockNow(item);

        StatusText = $"已添加 {item.CityName}（{ClockMath.FormatOffset(TimeSpan.FromMinutes(minutes.Value))}）";
        Log($"添加自定义偏移 {item.CityName} · {ClockMath.FormatOffsetMinutes(minutes.Value)}");
        CustomName = "";
        RefreshSummary();
    }

    /// <summary>删除一只钟（锁定的删不掉）。真正删除时记下位置，供撤销用。</summary>
    public void RemoveClock(WorldClockItem? clock)
    {
        if (clock is null) return;
        if (clock.IsLocked)
        {
            StatusText = $"「{clock.CityName}」已锁定，先点锁解锁再删除";
            return;
        }

        var index = Clocks.IndexOf(clock);
        if (index < 0) return;

        Clocks.RemoveAt(index);
        _lastDeleted = clock;
        _lastDeletedIndex = index;
        _undoTimer.Stop();
        _undoTimer.Start();

        UndoHint = $"已删除「{clock.CityName}」";
        StatusText = "已删除，6 秒内可以撤销";
        Log($"删除 {clock.CityName}");
        OnPropertyChanged(nameof(HasUndo));
        RefreshSummary();
    }

    /// <summary>撤销上一次删除：按原位置放回去。</summary>
    public void UndoDelete()
    {
        if (_lastDeleted is not { } clock) return;

        var index = Math.Clamp(_lastDeletedIndex, 0, Clocks.Count);
        Clocks.Insert(index, clock);
        RefreshClockNow(clock);

        StatusText = $"已恢复「{clock.CityName}」";
        Log($"撤销删除 {clock.CityName}");
        ClearUndo();
        RefreshSummary();
    }

    /// <summary>清掉撤销记录（撤销窗口到点 / 撤销完）。</summary>
    private void ClearUndo()
    {
        _undoTimer.Stop();
        _lastDeleted = null;
        UndoHint = "";
        OnPropertyChanged(nameof(HasUndo));
    }

    // ---------- 锁定 ----------

    /// <summary>切换锁定（防手滑：锁上之后 ✕ 不显示、≡ 不能拖）。</summary>
    public void ToggleLock(WorldClockItem? clock)
    {
        if (clock is null) return;
        clock.IsLocked = !clock.IsLocked;
        StatusText = clock.IsLocked ? $"「{clock.CityName}」已锁定" : $"「{clock.CityName}」已解锁";
        RefreshSummary();
    }

    /// <summary>批量锁定 / 解锁。</summary>
    public void SetAllLocked(bool locked)
    {
        foreach (var clock in Clocks) clock.IsLocked = locked;
        StatusText = locked ? "所有钟已锁定" : "所有钟已解锁";
        RefreshSummary();
    }

    // ---------- 排序 ----------

    /// <summary>拖动排序：把 <paramref name="clock"/> 放到第 <paramref name="insertBefore"/> 项之前。</summary>
    public bool MoveTo(WorldClockItem? clock, int insertBefore)
    {
        if (clock is null || clock.IsLocked) return false;
        var from = Clocks.IndexOf(clock);
        if (from < 0) return false;

        var target = ClockMath.ResolveDropIndex(from, insertBefore, Clocks.Count);
        if (!ClockMath.Move(Clocks, from, target)) return false;
        StatusText = $"「{clock.CityName}」位置已调整";
        return true;
    }

    /// <summary>上移 / 下移一格（右键菜单与键盘用）。</summary>
    public void MoveBy(WorldClockItem? clock, int delta)
    {
        if (clock is null || clock.IsLocked) return;
        var from = Clocks.IndexOf(clock);
        if (from < 0) return;
        var target = Math.Clamp(from + delta, 0, Clocks.Count - 1);
        if (ClockMath.Move(Clocks, from, target)) StatusText = $"「{clock.CityName}」位置已调整";
    }

    /// <summary>置顶。</summary>
    public void BringToTop(WorldClockItem? clock)
    {
        if (clock is null || clock.IsLocked) return;
        var from = Clocks.IndexOf(clock);
        if (from <= 0) return;
        if (ClockMath.Move(Clocks, from, 0)) StatusText = $"「{clock.CityName}」已置顶";
    }

    /// <summary>
    /// 按当地时间从早到晚排列（按当前 UTC 偏移比，跟界面显示一致）。
    /// <para>锁定的城市**不参与**排序（用户锁它就是为了它别动），排完仍留在原来的下标上。</para>
    /// </summary>
    public void SortByTime()
    {
        SortKeepingLocked();
        StatusText = "已按当地时间从早到晚排列（锁定的城市位置不变）";
        Log("按时间排序");
    }

    /// <summary>按城市名（拼音 / 中文）排序。</summary>
    public void SortByName()
    {
        var sorted = Clocks.OrderBy(c => c.CityName, StringComparer.CurrentCulture).ToList();
        ApplySortedKeepingLocked(sorted);
        StatusText = "已按城市名排列（锁定的城市位置不变）";
        Log("按名称排序");
    }

    private void SortKeepingLocked()
    {
        var utcNow = DateTime.UtcNow;
        var sorted = Clocks
            .OrderBy(c => ClockMath.Resolve(c.TimeZoneId, c.CustomOffsetMinutes, utcNow).Offset)
            .ThenBy(c => c.CityName, StringComparer.CurrentCulture)
            .ToList();
        ApplySortedKeepingLocked(sorted);
    }

    /// <summary>
    /// 把排好序的列表写回（锁定的城市留在原下标上）。
    /// 抽成一个方法：两种排序共用，「锁定项不动」这条规则只写一遍。
    /// </summary>
    private void ApplySortedKeepingLocked(List<WorldClockItem> sorted)
    {
        var lockedSlots = new List<(int Index, WorldClockItem Item)>();
        for (var i = 0; i < Clocks.Count; i++)
        {
            if (Clocks[i].IsLocked) lockedSlots.Add((i, Clocks[i]));
        }

        var movable = sorted.Where(c => !c.IsLocked).ToList();
        if (movable.Count == 0) return;   // 全锁着：什么都不动

        var lockedSet = lockedSlots.Select(s => s.Item).ToHashSet();
        var cursor = 0;
        var rebuilt = new List<WorldClockItem>(Clocks.Count);
        for (var i = 0; i < Clocks.Count; i++)
        {
            var locked = lockedSlots.FirstOrDefault(s => s.Index == i);
            if (locked.Item is not null && lockedSet.Contains(locked.Item))
            {
                rebuilt.Add(locked.Item);
                continue;
            }
            rebuilt.Add(movable[cursor++]);
        }

        for (var i = 0; i < rebuilt.Count; i++)
        {
            var current = Clocks.IndexOf(rebuilt[i]);
            if (current != i) Clocks.Move(current, i);
        }
    }

    // ---------- 复制 ----------

    /// <summary>复制一只钟的「城市 时间（时差）」到剪贴板。</summary>
    public void CopyTime(WorldClockItem? clock)
    {
        if (clock is null) return;
        var text = $"{clock.CityName} {clock.TimeText}（{clock.OffsetText}）";
        if (TrySetClipboard(text)) StatusText = $"已复制：{text}";
    }

    /// <summary>复制所有钟（一行一个城市，方便贴到聊天 / 文档里）。</summary>
    public void CopyAll()
    {
        if (Clocks.Count == 0) return;
        RefreshNow();
        var lines = Clocks.Select(c => $"{c.CityName}\t{c.DateText}\t{c.TimeText}\t{c.OffsetText}");
        var text = $"本机 {DateTime.Now:yyyy-MM-dd HH:mm:ss}（{WorldClockTimeZones.LocalOffsetText(DateTime.UtcNow)}）"
                   + Environment.NewLine + string.Join(Environment.NewLine, lines);
        if (TrySetClipboard(text)) StatusText = $"已复制 {Clocks.Count} 个城市的时间";
    }

    /// <summary>写剪贴板：失败（被别的程序占用）只提示，不抛异常。</summary>
    private bool TrySetClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "复制失败（剪贴板被占用？）：" + ex.Message;
            return false;
        }
    }

    // ---------- 城市搜索 / 默认城市 ----------

    /// <summary>把搜索框里的关键词重新跑一遍城市库。</summary>
    private void RefreshCityMatches()
        => CityMatches = new ObservableCollection<ClockCityOption>(ClockCatalog.Search(CityFilter));

    /// <summary>空状态时的「一键加入常用城市」。</summary>
    public void AddStarterCities()
    {
        var added = 0;
        foreach (var name in ClockCatalog.DefaultCityNames)
        {
            var city = ClockCatalog.Find(name);
            if (city is null) continue;
            if (Clocks.Any(c => string.Equals(c.CityName, city.Name, StringComparison.Ordinal))) continue;
            Clocks.Add(WorldClockItem.FromCity(city));
            added++;
        }
        RefreshNow();
        RefreshSummary();
        StatusText = added > 0 ? $"已加入 {added} 个常用城市" : "常用城市都已经在列表里了";
    }

    /// <summary>首次运行（配置文件里一只钟都没有、而且确实是第一次）时放入默认城市。</summary>
    private void EnsureDefaultCities()
    {
        RefreshCityMatches();
        if (!IsFirstRun) return;
        if (Clocks.Count > 0) return;
        foreach (var name in ClockCatalog.DefaultCityNames)
        {
            if (ClockCatalog.Find(name) is { } city) Clocks.Add(WorldClockItem.FromCity(city));
        }
    }

    // ---------- 统计 ----------

    private void RefreshSummary()
    {
        HasClocks = Clocks.Count > 0;
        var locked = Clocks.Count(c => c.IsLocked);
        SummaryText = $"本机时区 {WorldClockTimeZones.LocalShortName} · 共 {Clocks.Count} 个城市"
                      + (locked > 0 ? $" · {locked} 个已锁定" : "");
    }
}
