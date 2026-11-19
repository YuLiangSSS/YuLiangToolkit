using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Weather.ViewModels;
using YuLiangToolkit.Tools.Weather.Views;

namespace YuLiangToolkit.Tools.Weather;

/// <summary>
/// 「天气预报」工具入口：查收藏城市的实时天气与未来预报。
///
/// <para>数据来自 Open-Meteo（免费、无 Key、支持中文城市搜索）：当前温度 / 体感 / 湿度 / 风向风力 /
/// 气压 / 云量 / 降水，未来 7 天与逐小时趋势，日出日落。断网时回退显示上次拉取的缓存
/// （<c>Cache\weather\</c>，走 ToolCache 自动进「清理缓存」）。</para>
/// </summary>
public sealed class WeatherTool : ITool
{
    public string Id => WeatherConfig.ToolId;

    public string Name => "天气预报";

    public string Description =>
        "查城市的实时天气与未来预报：当前温度 / 体感 / 湿度 / 风向风力 / 气压 / 云量，"
        + "未来 7 天与逐小时趋势、日出日落；收藏多城市一键切换，断网自动显示上次缓存。"
        + "数据来自 Open-Meteo 免费接口，无需 API Key。";

    public string EmojiIcon => "⛅";

    public string Category => "生活";

    public IReadOnlyList<string> CategoryPath => new[] { "生活" };

    public IReadOnlyList<string> Tags => new[]
    {
        "天气", "天气预报", "温度", "湿度", "风向", "降水概率", "日出日落", "Open-Meteo",
    };

    /// <summary>主页卡片排序：排在「全球时钟」（165）之后。</summary>
    public int Order => 168;

    public FrameworkElement CreateView()
        => new WeatherView { DataContext = new WeatherViewModel() };
}
