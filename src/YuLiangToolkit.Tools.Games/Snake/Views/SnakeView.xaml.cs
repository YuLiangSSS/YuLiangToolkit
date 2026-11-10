using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.Games.Snake.ViewModels;

namespace YuLiangToolkit.Tools.Games.Snake.Views;

/// <summary>
/// 「贪吃蛇」界面代码后置：只处理键盘输入（方向键 / WASD 转向、Shift 加速、空格开始），
/// 业务都在 <see cref="SnakeViewModel"/> 里。
/// </summary>
public partial class SnakeView : UserControl
{
    private Window? _window;

    /// <summary>构造。</summary>
    public SnakeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.PreviewKeyDown += OnPreviewKeyDown;
            _window.PreviewKeyUp += OnPreviewKeyUp;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnPreviewKeyDown;
            _window.PreviewKeyUp -= OnPreviewKeyUp;
            _window = null;
        }
    }

    private SnakeViewModel? Vm => DataContext as SnakeViewModel;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        // 输入框里打字时不抢按键
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.Left or Key.A:
                vm.Turn(SnakeDirection.Left);
                e.Handled = true;
                return;
            case Key.Right or Key.D:
                vm.Turn(SnakeDirection.Right);
                e.Handled = true;
                return;
            case Key.Up or Key.W:
                vm.Turn(SnakeDirection.Up);
                e.Handled = true;
                return;
            case Key.Down or Key.S:
                vm.Turn(SnakeDirection.Down);
                e.Handled = true;
                return;
            case Key.LeftShift or Key.RightShift:
                vm.BeginBoost();
                e.Handled = true;
                return;
            case Key.Space:
                vm.StartCommand.Execute(null);
                e.Handled = true;
                return;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) Vm?.EndBoost();
    }
}
