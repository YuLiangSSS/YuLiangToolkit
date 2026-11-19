using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Tools.Weather.Models;

namespace YuLiangToolkit.Tools.Weather;

/// <summary>
/// 「天气预报」的强类型配置 → <c>Config\weather\config.json</c>。
///
/// <para>收藏城市列表就是配置（<see cref="Places"/>）：加 / 删 / 切换当前城市都自动落盘，
/// 下次打开还是上次的画面。单位与逐小时条数改了立即写盘，ViewModel 监听变化后本地重新渲染（不重新联网）。</para>
/// </summary>
public sealed class WeatherConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名）。</summary>
    [JsonIgnore]
    public static string ToolId => "weather";

    // ===================== 城市列表 =====================

    private ObservableCollection<WeatherPlaceItem> _places = new();

    /// <summary>收藏的城市（顺序 = 界面顺序）。首次运行由 ViewModel 放入几个常用城市。</summary>
    public ObservableCollection<WeatherPlaceItem> Places
    {
        get => _places;
        set => Set(ref _places, value ?? new ObservableCollection<WeatherPlaceItem>());
    }

    private string _currentPlaceId = "";

    /// <summary>当前展示的城市 Id（<see cref="WeatherPlaceItem.Id"/>）。</summary>
    public string CurrentPlaceId
    {
        get => _currentPlaceId;
        set
        {
            if (Set(ref _currentPlaceId, value ?? "")) OnPropertyChanged(nameof(CurrentPlace));
        }
    }

    /// <summary>当前展示的城市（Id 对不上时回退列表第一个；列表为空返回 null）。</summary>
    [JsonIgnore]
    public WeatherPlaceItem? CurrentPlace
        => Places.FirstOrDefault(p => p.Id == CurrentPlaceId) ?? Places.FirstOrDefault();

    // ===================== 展示选项 =====================

    private string _temperatureUnit = "celsius";

    /// <summary>温度单位：celsius / fahrenheit（存干净的值；下拉框标签在 XAML 里写）。</summary>
    public string TemperatureUnit
    {
        get => _temperatureUnit;
        set => Set(ref _temperatureUnit, value is "fahrenheit" ? "fahrenheit" : "celsius");
    }

    private string _windSpeedUnit = "kmh";

    /// <summary>风速单位：kmh / ms。</summary>
    public string WindSpeedUnit
    {
        get => _windSpeedUnit;
        set => Set(ref _windSpeedUnit, value is "ms" ? "ms" : "kmh");
    }

    private int _hourCount = 24;

    /// <summary>逐小时预报显示多少个小时（12 / 24 / 48）。</summary>
    public int HourCount
    {
        get => _hourCount;
        set => Set(ref _hourCount, value is 12 or 24 or 48 ? value : 24);
    }

    private int _autoRefreshMinutes;

    /// <summary>自动刷新间隔（分钟）。0 = 不自动刷新；30 / 60 / 180。</summary>
    public int AutoRefreshMinutes
    {
        get => _autoRefreshMinutes;
        set => Set(ref _autoRefreshMinutes, value is 30 or 60 or 180 ? value : 0);
    }
}
