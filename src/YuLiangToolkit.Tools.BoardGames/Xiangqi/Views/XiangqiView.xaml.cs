using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.BoardGames.Xiangqi.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi.Views;

/// <summary>
/// 象棋界面代码后置：只做一件必须碰到可视树的事 —— 把自绘棋盘的点击转给 ViewModel。
/// 其余全部走绑定（命令都在 ViewModel 里）；窗口尺寸、页面滚动位置都不碰（由外壳 / 用户决定）。
/// </summary>
public partial class XiangqiView : UserControl
{
    private XiangqiViewModel? _vm;

    /// <summary>造一个象棋视图。</summary>
    public XiangqiView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => HookBoard();

    private void OnUnloaded(object sender, RoutedEventArgs e) => UnhookBoard();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookBoard();
        _vm = e.NewValue as XiangqiViewModel;
        if (IsLoaded) HookBoard();
    }

    private void HookBoard()
    {
        if (BoardView is null) return;
        _vm = DataContext as XiangqiViewModel;
        BoardView.PointClicked -= OnBoardPointClicked;
        BoardView.PointClicked += OnBoardPointClicked;
    }

    private void UnhookBoard()
    {
        if (BoardView is null) return;
        BoardView.PointClicked -= OnBoardPointClicked;
    }

    private void OnBoardPointClicked(int x, int y) => _vm?.HandleBoardClick(x, y);
}
