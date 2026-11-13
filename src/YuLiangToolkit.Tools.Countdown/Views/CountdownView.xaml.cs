using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.Countdown.ViewModels;

namespace YuLiangToolkit.Tools.Countdown.Views;

/// <summary>
/// 倒计时页代码后置：只做「页面可见 / 窗口在前台」的通知，
/// 计时与到点逻辑都在 ViewModel（自检可断言）。
/// </summary>
public partial class CountdownView : UserControl
{
    public CountdownView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>页面的 ViewModel。</summary>
    private CountdownViewModel? ViewModel => DataContext as CountdownViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel?.OnPageLoaded();
        UpdateForeground();

        if (Window.GetWindow(this) is { } window) window.Activated += OnWindowActivatedChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel?.OnPageUnloaded();

        if (Window.GetWindow(this) is { } window) window.Activated -= OnWindowActivatedChanged;
        ViewModel?.SetForeground(false);
    }

    private void OnWindowActivatedChanged(object? sender, EventArgs e) => UpdateForeground();

    /// <summary>「正在看这个页面」= 窗口在前台 + 本页面可见。</summary>
    private void UpdateForeground()
        => ViewModel?.SetForeground(IsVisible && Window.GetWindow(this) is { IsActive: true });
}
