using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.Games.Tetris.ViewModels;

namespace YuLiangToolkit.Tools.Games.Tetris.Views;

/// <summary>
/// 「俄罗斯方块」页面的代码后置：<b>只做界面层的事</b>（业务全在 <see cref="TetrisViewModel"/> 与
/// <see cref="TetrisEngine"/>）：
/// <list type="number">
///   <item>整页拦键盘（←→ 移动 / ↓ 软降 / ↑ 或 X 顺时针 / Z 逆时针 / 空格硬降 / C 暂存 /
///         Esc·P 暂停 / R 重开 / Enter 开始 / F1 打开游玩说明）；</item>
///   <item>「游玩说明」按钮 / F1 打开说明窗口（打开前自动暂停，免得边读边掉块）；</item>
///   <item>页面 <c>Loaded</c> / <c>Unloaded</c> 时通知 ViewModel（外壳会缓存工具页，切走必须暂停）；</item>
///   <item>窗口失去 / 重新获得焦点（设置里可关的自动暂停）。</item>
/// </list>
/// </summary>
public partial class TetrisView : UserControl
{
    private Window? _host;
    private TetrisViewModel? _viewModel;
    private TetrisHelpWindow? _helpWindow;

    public TetrisView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;

        // 预览块数变了 → 按数量给「接下来」列一个合适高度（竖排每块 34 像素）
        DataContextChanged += (_, _) => HookNextPoolSize();

        // HUD 拖动松手 → 存进配置（见 TetrisViewModel.Hud.cs）
        HudLayer.Moved += (part, x, y) =>
        {
            (DataContext as TetrisViewModel)?.OnHudMovedExternal(part, x, y);
        };
    }

    private TetrisViewModel? ViewModel => DataContext as TetrisViewModel;

    /// <summary>按「预览块数」设置「接下来」列的高度（竖排一列）。</summary>
    private void HookNextPoolSize()
    {
        if (ViewModel is not { } vm) return;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        ApplyNextPoolHeight();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TetrisViewModel.PreviewSlots) or nameof(TetrisViewModel.NextPieces))
            ApplyNextPoolHeight();
    }

    private void ApplyNextPoolHeight()
    {
        var count = Math.Clamp(ViewModel?.PreviewSlots ?? 3, 1, 8);
        NextPool.Height = count * 34;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => _viewModel = e.NewValue as TetrisViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 外壳会缓存工具页，Loaded 可能触发多次：这里只做「可以重复做」的事
        _viewModel = ViewModel;
        _viewModel?.ResumeAfterAway();

        if (_host is not null)
        {
            _host.Deactivated -= OnHostDeactivated;
            _host.Activated -= OnHostActivated;
        }

        _host = Window.GetWindow(this);
        if (_host is not null)
        {
            _host.Deactivated += OnHostDeactivated;
            _host.Activated += OnHostActivated;
        }

        // 整页收键盘：焦点落在页面上，方向键 / 空格才会走本页的 PreviewKeyDown
        Focusable = true;
        Keyboard.Focus(this);

        // 调试 / 截图用：YLT_TETRIS_HELP=1 时打开页面就把说明窗口弹出来（不影响正常使用）
        if (Environment.GetEnvironmentVariable("YLT_TETRIS_HELP") == "1" && _helpWindow is null)
            Dispatcher.BeginInvoke(new Action(ShowHelp), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // ⚠ 先暂停再摘事件：外壳把视图塞进缓存时不会调 OnClosed，
        //   不停表的话切走之后方块还在掉，切回来已经堆满。
        _viewModel?.PauseForAway();

        if (_host is not null)
        {
            _host.Deactivated -= OnHostDeactivated;
            _host.Activated -= OnHostActivated;
            _host = null;
        }
    }

    private void OnHostDeactivated(object? sender, EventArgs e) => _viewModel?.PauseForLostFocus();

    private void OnHostActivated(object? sender, EventArgs e) => _viewModel?.ResumeAfterAway();

    /// <summary>
    /// 键盘映射（预览阶段拦，按钮有焦点时也生效）。
    /// <para>⚠ <c>e.Handled = true</c> 必须给：否则方向键会被外层 ScrollViewer 当成滚动、
    /// 空格会被当成「点当前聚焦的按钮」，同一手会触发两次操作。</para>
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm) return;

        switch (e.Key)
        {
            case Key.Left:
                vm.MoveLeft();
                break;
            case Key.Right:
                vm.MoveRight();
                break;
            case Key.Down:
                vm.SoftDrop();
                break;
            case Key.Up:
            case Key.X:
                vm.Rotate(clockwise: true);
                break;
            case Key.Z:
                vm.Rotate(clockwise: false);
                break;
            case Key.Space:
                if (vm.OverlayVisible) vm.StartOrResume();
                else vm.HardDrop();
                break;
            case Key.C:
                vm.Hold();
                break;
            case Key.P:
            case Key.Escape:
                if (vm.IsGameOver) vm.NewGame();
                else if (vm.IsWaitingToStart) vm.StartOrResume();
                else vm.TogglePause();
                break;
            case Key.R:
                vm.NewGame();
                break;
            case Key.Enter:
                vm.StartOrResume();
                break;
            case Key.F1:
                ShowHelp();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>点「游玩说明」按钮。</summary>
    private void OnHelpClick(object sender, RoutedEventArgs e) => ShowHelp();

    /// <summary>
    /// 打开游玩说明窗口。<b>打开前先暂停</b>：说明是要读的，别让人一边读一边掉块。
    /// 同一个窗口只开一份（重复点按钮 / 连按 F1 不会叠出一堆窗口）。
    /// </summary>
    private void ShowHelp()
    {
        if (_helpWindow is not null)
        {
            _helpWindow.Activate();
            return;
        }

        var wasRunning = _viewModel is { OverlayVisible: false };
        if (wasRunning) _viewModel!.TogglePause();

        var window = new TetrisHelpWindow();
        _helpWindow = window;
        try
        {
            window.Owner ??= Window.GetWindow(this);
            window.ShowDialog();
        }
        finally
        {
            _helpWindow = null;
        }

        // 关掉说明后接着玩（读之前本来就在跑才恢复；原本暂停着就保持暂停）
        if (wasRunning && _viewModel is { IsGameOver: false, IsPaused: true }) _viewModel.TogglePause();

        // 模态窗口关掉后键盘焦点会回到宿主窗口，但工具页可能已经丢了焦点，重新收一次
        Keyboard.Focus(this);
    }
}
