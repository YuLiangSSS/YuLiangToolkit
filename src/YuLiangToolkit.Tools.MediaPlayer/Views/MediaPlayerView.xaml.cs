using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.MediaPlayer.ViewModels;

namespace YuLiangToolkit.Tools.MediaPlayer.Views;

/// <summary>
/// 「视频 / 音乐播放器」的界面后台。
///
/// <para>代码后置里只做三件**必须碰界面**的事，其它全在 ViewModel 里：</para>
/// <list type="bullet">
///   <item>把 <c>MediaElement</c> 包成 <see cref="IMediaPlayback"/> 交给 ViewModel（<see cref="OnLoaded"/>）；</item>
///   <item>一个 200ms 的计时器，把播放位置推进 ViewModel（进度条 / 时间文本）；</item>
///   <item>那些 WPF 事件本身：进度条点击拖动、拖放、双击播放、右键上一首下一首、快捷键。</item>
/// </list>
///
/// <para>⚠ 右键「上一首 / 下一首」必须在这里接 <c>MouseRightButtonUp</c>：
/// <c>Button</c> 的 <c>Command</c> 只响应左键，右键连 <c>Click</c> 都不会触发。</para>
/// </summary>
public partial class MediaPlayerView : UserControl
{
    private MediaElementPlayback? _playback;
    private DispatcherTimer? _timer;
    private Window? _hostWindow;
    private bool _seeking;

    public MediaPlayerView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DragOver += OnDragOver;
        Drop += OnDrop;

        // 右键：快退按钮 = 上一首，快进按钮 = 下一首（左键仍然走各自的 Command）
        BackButton.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Vm?.PreviousCommand.Execute(null);
        };
        ForwardButton.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Vm?.NextCommand.Execute(null);
        };
    }

    private MediaPlayerViewModel? Vm => DataContext as MediaPlayerViewModel;

    // ---------------------------------------------------------------- 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // 播放器只包一次（View 重新 Loaded 时沿用同一个 MediaElement）
        _playback ??= new MediaElementPlayback(Media);
        vm.AttachPlayer(_playback);

        // 清空列表这种「不可逆但会心疼」的操作问一句；无头自检里这个钩子是直接返回 true 的
        vm.Confirm = message =>
        {
            var owner = Window.GetWindow(this);
            var result = owner is null
                ? MessageBox.Show(message, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(owner, message, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        };

        _timer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        if (!_timer.IsEnabled)
        {
            _timer.Tick -= OnTimerTick;
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        // 快捷键挂到窗口上：这一页点过任何地方之后空格 / 方向键都能用；切走时摘掉（见 OnUnloaded）
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _hostWindow.PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        // 全屏：ViewModel 只切状态，真正的窗口操作交给外壳（MainWindow 实现了 IFullscreenHost）
        vm.FullscreenChanged -= OnFullscreenChanged;
        vm.FullscreenChanged += OnFullscreenChanged;

        // 视图重建 / 后台播放切回来时，VM 里可能已经是全屏状态 → 跟外壳同步一次
        if (vm.IsFullscreen) OnFullscreenChanged(vm, true);
    }

    /// <summary>全屏状态变了 → 让外壳去藏 / 恢复标题栏、侧边栏、状态栏并把窗口铺满屏幕。</summary>
    private void OnFullscreenChanged(object? sender, bool on)
        => (Window.GetWindow(this) as IFullscreenHost)?.SetFullscreen(on);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();

        if (_hostWindow is not null) _hostWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
        _hostWindow = null;

        if (Vm is { } vm)
        {
            vm.FullscreenChanged -= OnFullscreenChanged;
            vm.ExitFullscreen();   // 切走 / 关工具时别把窗口留在“藏了壳”的全屏状态
        }

        // ⚠ 后台播放时**不能**关播放器：外壳会把这个视图留着，声音要继续
        if (Vm?.KeepAliveOnLeave != true) _playback?.Close();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_seeking) return; // 正在拖进度条时别让计时器把滑块拽回去
        if (Vm is not { } vm || _playback is null || !_playback.IsLoaded) return;
        vm.UpdatePosition(_playback.Position, _playback.Duration);
    }

    // ---------------------------------------------------------------- 进度条

    private void OnSeekMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || _playback is null || !_playback.IsLoaded) return;
        _seeking = true;
        SeekArea.CaptureMouse();
        ApplySeek(e);
    }

    private void OnSeekMouseMove(object sender, MouseEventArgs e)
    {
        if (!_seeking) return;
        ApplySeek(e);
    }

    private void OnSeekMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        ApplySeek(e);
        _seeking = false;
        if (SeekArea.IsMouseCaptured) SeekArea.ReleaseMouseCapture();
    }

    private void OnSeekMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        if (SeekArea.IsMouseCaptured) SeekArea.ReleaseMouseCapture();
    }

    private void ApplySeek(MouseEventArgs e)
    {
        if (Vm is not { } vm) return;
        var width = SeekArea.ActualWidth;
        if (width <= 0) return;
        vm.SeekToRatio(Math.Clamp(e.GetPosition(SeekArea).X / width, 0, 1));
    }

    // ---------------------------------------------------------------- 列表

    /// <summary>双击列表里的一行 = 播这一行（双击空白处不算）。</summary>
    private void OnPlaylistDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source) is null) return;
        vm.PlaySelectedCommand.Execute(null);
    }

    // ---------------------------------------------------------------- 拖放

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        try
        {
            await vm.AddDroppedPathsAsync(paths);
        }
        catch (Exception ex)
        {
            // 拖放回调是 async void，异常没人接，这里兜住，别让程序闪退
            vm.StatusText = $"处理拖进来的文件失败：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 快捷键

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.O)
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) vm.OpenFolderCommand.Execute(null);
                else vm.OpenFilesCommand.Execute(null);
            }
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None) return;

        // 正在输入框 / 下拉框 / 滑块 / 复选框上时别抢键（否则空格、方向键会用不了）
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem or Slider or CheckBox or RadioButton) return;

        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                vm.PlayPauseCommand.Execute(null);
                break;
            case Key.Left:
                e.Handled = true;
                vm.SeekBackwardCommand.Execute(null);
                break;
            case Key.Right:
                e.Handled = true;
                vm.SeekForwardCommand.Execute(null);
                break;
            case Key.Delete:
                e.Handled = true;
                vm.RemoveSelectedCommand.Execute(null);
                break;
            case Key.Enter when vm.SelectedItem is not null:
                e.Handled = true;
                vm.PlaySelectedCommand.Execute(null);
                break;
            case Key.Escape:
                // 先退全屏（全屏时最符合直觉），再关设置面板
                if (vm.IsFullscreen)
                {
                    e.Handled = true;
                    vm.ToggleFullscreen();
                }
                else if (vm.IsSettingsVisible)
                {
                    e.Handled = true;
                    vm.IsSettingsVisible = false;
                }
                break;
            case Key.F11:
                e.Handled = true;
                vm.ToggleFullscreen();
                break;
        }
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>顺着可视化树往上找指定类型的祖先（双击判定用）。</summary>
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = ParentOf(source);
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject source)
    {
        if (source is Visual || source is Visual3D) return VisualTreeHelper.GetParent(source);
        if (source is FrameworkContentElement content) return content.Parent ?? LogicalTreeHelper.GetParent(source);
        return LogicalTreeHelper.GetParent(source);
    }
}
