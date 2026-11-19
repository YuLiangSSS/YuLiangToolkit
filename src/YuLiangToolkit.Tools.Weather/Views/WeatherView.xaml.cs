using System.Windows.Controls;
using YuLiangToolkit.Tools.Weather.ViewModels;

namespace YuLiangToolkit.Tools.Weather.Views;

/// <summary>
/// 天气预报页面的代码后置：只管页面 Loaded / Unloaded 时让 ViewModel 开 / 停自动刷新，
/// 数据、缓存兜底、搜索逻辑全在 ViewModel 里（自检能无头断言），这里不写业务。
/// </summary>
public partial class WeatherView : UserControl
{
    public WeatherView()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel?.OnPageLoaded();
        Unloaded += (_, _) => ViewModel?.OnPageUnloaded();
    }

    /// <summary>页面的 ViewModel（DataContext 就是它）。</summary>
    private WeatherViewModel? ViewModel => DataContext as WeatherViewModel;
}
