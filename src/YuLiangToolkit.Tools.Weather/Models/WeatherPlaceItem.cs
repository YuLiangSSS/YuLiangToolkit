using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Weather.Models;

/// <summary>
/// 收藏的城市（配置里持久化的一项）：名字 + 坐标。
///
/// <para><see cref="Id"/> 用「纬度,经度」四位小数拼出来（见 <c>WeatherMath.MakePlaceId</c>），
/// 不依赖 Open-Meteo 的地名 id —— 同一座城市不管从哪次搜索加进来都是同一个 Id，
/// 当前城市标记、缓存文件名都靠它保持稳定。</para>
/// </summary>
public sealed class WeatherPlaceItem : ObservableObject
{
    private string _id = "";
    private string _name = "";
    private string _subName = "";
    private double _latitude;
    private double _longitude;

    /// <summary>稳定标识：<c>"39.9042,116.4074"</c>。</summary>
    public string Id
    {
        get => _id;
        set => Set(ref _id, value);
    }

    /// <summary>城市主名（搜索结果的地名，例如「北京」）。</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>副标题（国家 / 省份 / 区县，用于区分同名城市），可为空。</summary>
    public string SubName
    {
        get => _subName;
        set => Set(ref _subName, value);
    }

    /// <summary>纬度。</summary>
    public double Latitude
    {
        get => _latitude;
        set => Set(ref _latitude, value);
    }

    /// <summary>经度。</summary>
    public double Longitude
    {
        get => _longitude;
        set => Set(ref _longitude, value);
    }

    /// <summary>界面里的一行文案（副标题为空时只显示主名）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayText => string.IsNullOrWhiteSpace(SubName) ? Name : $"{Name}（{SubName}）";
}
