using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.Screenshot.ViewModels;

namespace YuLiangToolkit.Tools.Screenshot.Views;

/// <summary>截图工具页面代码后置：只把 Loaded 传给 ViewModel 刷新历史。</summary>
public partial class ScreenshotView : UserControl
{
    public ScreenshotView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as ScreenshotViewModel;
        viewModel?.NotifyOpened();
        viewModel?.OnPageLoaded();
    }
}
