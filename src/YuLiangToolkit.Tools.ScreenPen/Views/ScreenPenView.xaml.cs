using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.ScreenPen.ViewModels;

namespace YuLiangToolkit.Tools.ScreenPen.Views;

/// <summary>屏幕画笔工具页。</summary>
public partial class ScreenPenView : UserControl
{
    public ScreenPenView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScreenPenViewModel viewModel) viewModel.OnPageLoaded();
    }
}
