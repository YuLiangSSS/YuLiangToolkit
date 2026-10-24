using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.App.ViewModels;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 类别页：显示当前分类的 emoji / 名称 / 描述 / 工具计数，以及子类别卡片和工具卡片。
/// 任意层级都复用这一个界面（点类别卡片继续下钻，顶部面包屑 / 「← 返回上一级」回到上层）。
///
/// <para>代码后置只做键盘快捷键：Ctrl+F 聚焦搜索框、Esc 清空搜索（切走时自动摘掉钩子）。</para>
/// </summary>
public partial class CategoryView : UserControl
{
    private Window? _hostWindow;

    public CategoryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null) _hostWindow.PreviewKeyDown += OnHostKeyDown;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null) _hostWindow.PreviewKeyDown -= OnHostKeyDown;
        _hostWindow = null;
    }

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel is { HasSearchText: true } vm)
        {
            vm.SearchText = "";
            e.Handled = true;
        }
    }
}
