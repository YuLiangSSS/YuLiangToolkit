using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.Stopwatch.ViewModels;

namespace YuLiangToolkit.Tools.Stopwatch.Views;

/// <summary>
/// 秒表页面代码后置：只做两件界面上的事 ——
/// ① 页面 Loaded / Unloaded 时告诉 ViewModel（决定要不要按帧刷新显示）；
/// ② 窗口激活状态变化时告诉 ViewModel（决定按快捷键时要不要弹通知）。
///
/// <para>计时、快捷键、通知、声音全在 ViewModel / 工具里（自检能无头断言），这里不写业务。</para>
/// </summary>
public partial class StopwatchView : UserControl
{
    public StopwatchView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>页面的 ViewModel（DataContext 就是它）。</summary>
    private StopwatchViewModel? ViewModel => DataContext as StopwatchViewModel;

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

    /// <summary>
    /// 「正在看这个页面」= 窗口在前台 + 本页面在可视树里而且可见。
    /// 只有这时候按快捷键才不弹通知（界面自己就在变，再弹一次是打扰）。
    /// </summary>
    private void UpdateForeground()
    {
        var foreground = IsVisible
                         && Window.GetWindow(this) is { IsActive: true };

        ViewModel?.SetForeground(foreground);
    }
}
