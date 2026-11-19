using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Weather.Models;
using YuLiangToolkit.Tools.Weather.Services;

namespace YuLiangToolkit.Tools.Weather.ViewModels;

/// <summary>下拉框选项：存干净的值（写进配置），显示中文标签。
/// ToString 直接返回标签 —— 外壳的 ComboBox 模板不一定应用 DisplayMemberPath，用 ToString 兜底两处都显示对。</summary>
public sealed record ChoiceOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 「天气预报」的 ViewModel：城市收藏与切换 / 搜索添加 / 拉取与渲染 / 断网缓存兜底 / 自动刷新。
///
/// <para><b>数据流</b>：拉取成功 → 原始 JSON 存内存（<c>_lastJson</c>）+ 写
/// <c>Cache\weather\last-{城市Id}.json</c> → 组装展示模型渲染。切换单位 / 逐小时条数
/// 用 <c>_lastJson</c> 本地重新渲染（不重新联网）；切换城市或点刷新才真的发请求。
/// 请求失败 → 找缓存文件兜底渲染，并在状态栏标注「缓存数据」。</para>
///
/// <para><b>节奏</b>：自动刷新由 <see cref="DispatcherTimer"/> 按配置的间隔跑，
/// 只在页面可见时开（Loaded 开 / Unloaded + <see cref="OnClosed"/> 停）。</para>
/// </summary>
public sealed class WeatherViewModel : ToolViewModelBase
{
    private readonly OpenMeteoClient _client = new();
    private readonly ToolCache _cache = ToolCache.For(WeatherConfig.ToolId);
    private readonly DispatcherTimer _autoTimer;

    /// <summary>当前城市最近一次成功响应的原始 JSON（切单位 / 条数时本地重渲染用）。</summary>
    private string _lastJson = "";

    /// <summary>当前城市最近一次成功拉取的时间说明（本地重渲染时沿用，避免「缓存数据」字样残留）。</summary>
    private string _lastFetchedText = "尚未拉取";

    public WeatherViewModel() : base(WeatherConfig.ToolId)
    {
        Cfg = LoadConfig<WeatherConfig>();
        if (IsFirstRun && Cfg.Places.Count == 0) EnsureDefaultPlaces();

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        SearchCommand = new AsyncRelayCommand(_ => SearchAsync());
        AddPlaceCommand = new AsyncRelayCommand(p => AddPlaceAsync(p as OpenMeteoPlace), p => p is OpenMeteoPlace);
        SelectPlaceCommand = new AsyncRelayCommand(p => SelectPlaceAsync(p as WeatherPlaceItem), p => p is WeatherPlaceItem);
        RemovePlaceCommand = new AsyncRelayCommand(p => RemovePlaceAsync(p as WeatherPlaceItem), p => p is WeatherPlaceItem);
        OpenCacheFolderCommand = new RelayCommand(_ => _cache.OpenInExplorer());

        _autoTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(30) };
        _autoTimer.Tick += (_, _) => _ = RefreshAsync();

        // 单位 / 条数改动 → 本地重渲染（不重新联网）；自动间隔改动 → 重排计时器
        Cfg.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(WeatherConfig.TemperatureUnit):
                case nameof(WeatherConfig.WindSpeedUnit):
                case nameof(WeatherConfig.HourCount):
                    RebuildLocally();
                    break;
                case nameof(WeatherConfig.AutoRefreshMinutes):
                    RestartAutoTimer();
                    break;
            }
        };

        Log($"配置文件：{Cfg.FilePath}");
        Log($"缓存目录：{_cache.Directory}");
        if (IsFirstRun) Log("首次运行：已放入北京 / 上海 / 广州 / 成都，可在右侧搜索添加自己的城市");
    }

    // ---------- 配置与界面状态 ----------

    /// <summary>强类型配置（城市列表、单位、自动刷新都在里面，改一下自动落盘）。</summary>
    public WeatherConfig Cfg { get; }

    private WeatherNowDisplay? _now;

    /// <summary>「当前天气」卡片（null = 还没有数据，界面显示空状态提示）。</summary>
    public WeatherNowDisplay? Now
    {
        get => _now;
        private set => Set(ref _now, value);
    }

    private IReadOnlyList<HourForecast> _hours = [];

    /// <summary>逐小时预报（整体替换渲染）。</summary>
    public IReadOnlyList<HourForecast> Hours
    {
        get => _hours;
        private set => Set(ref _hours, value);
    }

    private IReadOnlyList<DayForecast> _days = [];

    /// <summary>7 天预报（整体替换渲染）。</summary>
    public IReadOnlyList<DayForecast> Days
    {
        get => _days;
        private set => Set(ref _days, value);
    }

    private ObservableCollection<OpenMeteoPlace> _searchResults = [];

    /// <summary>地名搜索结果（点一条加入收藏）。</summary>
    public ObservableCollection<OpenMeteoPlace> SearchResults
    {
        get => _searchResults;
        private set
        {
            if (Set(ref _searchResults, value)) OnPropertyChanged(nameof(HasSearchResults));
        }
    }

    /// <summary>有没有搜索结果（控制结果区的显示）。</summary>
    public bool HasSearchResults => SearchResults.Count > 0;

    private string _searchText = "";

    /// <summary>搜索框内容（回车 / 点按钮触发搜索）。</summary>
    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value);
    }

    // ---------- 命令 ----------

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand AddPlaceCommand { get; }
    public AsyncRelayCommand SelectPlaceCommand { get; }
    public AsyncRelayCommand RemovePlaceCommand { get; }
    public RelayCommand OpenCacheFolderCommand { get; }

    // ---------- 生命周期 ----------

    /// <summary>页面挂到界面（Loaded）：按配置开自动刷新；还没数据就先拉一次。</summary>
    public void OnPageLoaded()
    {
        RestartAutoTimer();
        if (Now is null) _ = RefreshAsync();
    }

    /// <summary>页面离开界面（Unloaded）：停自动刷新（外壳缓存页面时不空转）。</summary>
    public void OnPageUnloaded() => _autoTimer.Stop();

    /// <summary>工具被切走 / 关闭：停自动刷新（配置由基类落盘）。</summary>
    protected override void OnClosed() => _autoTimer.Stop();

    /// <summary>按配置重排自动刷新计时器（0 = 关）。</summary>
    private void RestartAutoTimer()
    {
        _autoTimer.Stop();
        if (Cfg.AutoRefreshMinutes <= 0) return;
        _autoTimer.Interval = TimeSpan.FromMinutes(Cfg.AutoRefreshMinutes);
        _autoTimer.Start();
    }

    // ---------- 拉取与渲染 ----------

    /// <summary>拉当前城市的天气；失败时尝试缓存兜底（状态栏注明）。</summary>
    private async Task RefreshAsync()
    {
        var place = Cfg.CurrentPlace;
        if (place is null)
        {
            StatusText = "先在右侧添加一个城市，再刷新天气";
            return;
        }

        IsBusy = true;
        StatusText = $"正在获取「{place.Name}」的天气…";
        try
        {
            var (forecast, rawJson) = await _client.GetForecastRawAsync(place.Latitude, place.Longitude).ConfigureAwait(true);
            _lastJson = rawJson;
            _lastFetchedText = $"更新于 {DateTime.Now:HH:mm}";
            WriteCached(place.Id, rawJson, DateTime.Now);
            Render(place, forecast);
            StatusText = $"已获取「{place.Name}」（{forecast.Timezone}）";
        }
        catch (Exception ex)
        {
            if (TryLoadCached(place.Id, out var cached) && await RenderFromCacheAsync(place, cached!))
            {
                StatusText = $"联网失败，已显示 {_lastFetchedText} 的缓存数据（{ShortReason(ex)}）";
            }
            else
            {
                StatusText = $"获取天气失败：{ShortReason(ex)}";
            }
            Log($"[失败] {place.Name}：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>渲染一次天气响应（三块展示模型整体替换）。</summary>
    private void Render(WeatherPlaceItem place, OpenMeteoForecast forecast)
    {
        Now = WeatherMath.BuildNow(forecast, place.Name, _lastFetchedText, Cfg.TemperatureUnit);
        Hours = WeatherMath.BuildHours(forecast, Cfg.TemperatureUnit, Cfg.HourCount);
        Days = WeatherMath.BuildDays(forecast, Cfg.TemperatureUnit);
        Log($"[{_lastFetchedText}] {place.Name}：{Now.ConditionText} {Now.TemperatureText}，体感 {Now.FeelsText}");
    }

    /// <summary>用内存里最近一次原始 JSON 本地重渲染（切单位 / 逐小时条数时调用，不联网）。</summary>
    private async void RebuildLocally()
    {
        var place = Cfg.CurrentPlace;
        if (place is null || string.IsNullOrEmpty(_lastJson)) return;

        try
        {
            var forecast = await _client.ParseForecastAsync(_lastJson);
            Render(place, forecast);
        }
        catch (Exception ex)
        {
            // JSON 已在上次成功时校验过，走到这里基本不可能；兜底提示即可
            Log($"本地重渲染失败：{ex.Message}");
        }
    }

    /// <summary>缓存兜底：解析缓存 JSON 并渲染。返回 false = 缓存也读不动。</summary>
    private async Task<bool> RenderFromCacheAsync(WeatherPlaceItem place, CachedForecast cached)
    {
        try
        {
            var forecast = await _client.ParseForecastAsync(cached.Json);
            _lastJson = cached.Json;
            _lastFetchedText = string.IsNullOrWhiteSpace(cached.FetchedAtText) ? "之前" : cached.FetchedAtText;
            Render(place, forecast);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 缓存（Cache\weather\，主页设置「清理缓存」覆盖这里） ----------

    /// <summary>缓存文件名（城市 Id 做后缀，一座城市一个文件）。</summary>
    private string CachedPath(string placeId) => _cache.PathOf($"last-{placeId}.json");

    /// <summary>把拉取成功的原始响应写进缓存；失败静默（缓存只是兜底，不能影响主流程）。</summary>
    private void WriteCached(string placeId, string json, DateTime fetchedAt)
    {
        try
        {
            _cache.EnsureCreated();
            var payload = new CachedForecast { PlaceId = placeId, FetchedAtText = fetchedAt.ToString("MM-dd HH:mm"), Json = json };
            File.WriteAllText(CachedPath(placeId), JsonSerializer.Serialize(payload, OpenMeteoClient.JsonOptions));
        }
        catch (Exception ex)
        {
            Log($"写缓存失败（不影响使用）：{ex.Message}");
        }
    }

    /// <summary>读缓存兜底；没有 / 读不动返回 false。</summary>
    private bool TryLoadCached(string placeId, out CachedForecast? cached)
    {
        cached = null;
        try
        {
            var path = CachedPath(placeId);
            if (!File.Exists(path)) return false;
            cached = JsonSerializer.Deserialize<CachedForecast>(File.ReadAllText(path), OpenMeteoClient.JsonOptions);
            return cached is not null && !string.IsNullOrWhiteSpace(cached.Json);
        }
        catch
        {
            return false;
        }
    }

    // ---------- 搜索与城市管理 ----------

    /// <summary>搜索城市（接口按相关性排序，同名列大城市优先靠前）。</summary>
    private async Task SearchAsync()
    {
        var query = SearchText?.Trim() ?? "";
        if (query.Length == 0)
        {
            StatusText = "输入城市名再搜索，例如「北京」或「New York」";
            return;
        }

        IsBusy = true;
        StatusText = $"正在搜索「{query}」…";
        try
        {
            var results = await _client.SearchPlacesAsync(query, 10).ConfigureAwait(true);
            SearchResults = [.. results.OrderByDescending(p => p.Population ?? 0)];
            StatusText = results.Count == 0
                ? $"没搜到「{query}」，换个说法再试（中文名或拼音 / 英文名都可以）"
                : $"找到 {results.Count} 个结果，点一条加入收藏";
            Log(results.Count == 0
                ? $"搜索「{query}」无结果"
                : $"搜索「{query}」：{string.Join("、", results.Take(5).Select(p => p.Name))}…");
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败：{ShortReason(ex)}";
            Log($"搜索失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把搜索结果加入收藏并切为当前城市（已收藏的同城直接切换）。</summary>
    private async Task AddPlaceAsync(OpenMeteoPlace? place)
    {
        if (place is null) return;

        var id = WeatherMath.MakePlaceId(place.Latitude, place.Longitude);
        var existing = Cfg.Places.FirstOrDefault(p => p.Id == id);
        if (existing is not null)
        {
            SearchText = "";
            SearchResults.Clear();
            await SelectPlaceAsync(existing);
            return;
        }

        var item = new WeatherPlaceItem
        {
            Id = id,
            Name = place.Name,
            SubName = JoinSubName(place),
            Latitude = place.Latitude,
            Longitude = place.Longitude,
        };
        Cfg.Places.Add(item);
        Cfg.CurrentPlaceId = id;
        SearchText = "";
        SearchResults.Clear();
        Log($"已添加收藏：{item.DisplayText}");
        await RefreshAsync();
    }

    /// <summary>切换当前城市并刷新（已是当前城市且有数据时跳过，避免重复请求）。</summary>
    private async Task SelectPlaceAsync(WeatherPlaceItem? place)
    {
        if (place is null || (Cfg.CurrentPlaceId == place.Id && Now is not null)) return;
        Cfg.CurrentPlaceId = place.Id;
        await RefreshAsync();
    }

    /// <summary>移除收藏；删的是当前城市时自动切到列表第一个（没有就清空画面）。</summary>
    private async Task RemovePlaceAsync(WeatherPlaceItem? place)
    {
        if (place is null) return;
        Cfg.Places.Remove(place);
        Log($"已移除：{place.DisplayText}");

        if (Cfg.CurrentPlaceId == place.Id)
        {
            Cfg.CurrentPlaceId = Cfg.Places.FirstOrDefault()?.Id ?? "";
            if (Cfg.CurrentPlace is not null)
            {
                await RefreshAsync();
            }
            else
            {
                Now = null;
                Hours = [];
                Days = [];
                _lastJson = "";
                StatusText = "收藏列表空了，搜索添加一个城市吧";
            }
        }
    }

    // ---------- 下拉选项（干净值 + 中文标签，SelectedValuePath=Value 绑配置） ----------

    /// <summary>自动刷新间隔选项。</summary>
    public IReadOnlyList<ChoiceOption<int>> RefreshChoices { get; } =
    [
        new(0, "不自动刷新"),
        new(30, "每 30 分钟"),
        new(60, "每 1 小时"),
        new(180, "每 3 小时"),
    ];

    /// <summary>逐小时条数选项。</summary>
    public IReadOnlyList<ChoiceOption<int>> HourChoices { get; } =
    [
        new(12, "12 小时"),
        new(24, "24 小时"),
        new(48, "48 小时"),
    ];

    /// <summary>温度单位选项。</summary>
    public IReadOnlyList<ChoiceOption<string>> TempUnitChoices { get; } =
    [
        new("celsius", "摄氏 °C"),
        new("fahrenheit", "华氏 °F"),
    ];

    /// <summary>风速单位选项。</summary>
    public IReadOnlyList<ChoiceOption<string>> WindUnitChoices { get; } =
    [
        new("kmh", "千米/小时 km/h"),
        new("ms", "米/秒 m/s"),
    ];

    // ---------- 小工具 ----------

    /// <summary>把「国家 · 省 · 市」拼成一行副标题（去空段、去重复段）。</summary>
    private static string JoinSubName(OpenMeteoPlace place)
    {
        var segments = new[] { place.Country, place.Admin1, place.Admin2 }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct()
            .ToList();
        return string.Join(" · ", segments);
    }

    /// <summary>异常信息压成一行短文案（HttpRequestException 带完整栈，界面只留主因）。</summary>
    private static string ShortReason(Exception ex) => ex.Message.Split('\n')[0].Trim();

    /// <summary>首次运行放入的默认城市（坐标与 Open-Meteo 地理编码返回一致）。</summary>
    private void EnsureDefaultPlaces()
    {
        var defaults = new[]
        {
            new WeatherPlaceItem { Id = "39.9042,116.4074", Name = "北京", SubName = "中国 · 北京市", Latitude = 39.9042, Longitude = 116.4074 },
            new WeatherPlaceItem { Id = "31.2304,121.4737", Name = "上海", SubName = "中国 · 上海市", Latitude = 31.2304, Longitude = 121.4737 },
            new WeatherPlaceItem { Id = "23.1291,113.2644", Name = "广州", SubName = "中国 · 广东省", Latitude = 23.1291, Longitude = 113.2644 },
            new WeatherPlaceItem { Id = "30.5728,104.0668", Name = "成都", SubName = "中国 · 四川省", Latitude = 30.5728, Longitude = 104.0668 },
        };
        foreach (var item in defaults) Cfg.Places.Add(item);
        Cfg.CurrentPlaceId = defaults[0].Id;
    }
}
