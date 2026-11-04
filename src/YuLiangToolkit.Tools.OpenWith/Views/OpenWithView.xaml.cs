using System.Windows.Controls;
using YuLiangToolkit.Tools.OpenWith.ViewModels;

namespace YuLiangToolkit.Tools.OpenWith.Views;

/// <summary>「打开方式」页面（界面全部在 XAML 里；这里只做 Loaded 时刷新一次注册表状态）。</summary>
public partial class OpenWithView : UserControl
{
    public OpenWithView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as OpenWithViewModel)?.RefreshState(announce: false);
    }
}
