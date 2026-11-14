using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Vsrg.ViewModels;

namespace YuLiangToolkit.Tools.Vsrg.Views;

/// <summary>
/// 「VSRG」页面的代码后置：**只做界面层的事**（业务全在 <see cref="VsrgViewModel"/> 与 Core 的
/// 判定 / 解析 / 导入里）：
/// <list type="number">
///   <item>整页拦键盘（1~10 轨键位 / Esc·P 暂停）—— 游玩时要做到「按一下就判一下」，
///         必须在这里挡掉默认行为（否则空格 / 方向键会被按钮或滚动条吃掉）；</item>
///   <item>把 <c>.osz</c> 拖进窗口就导入（整页都是落点）；</item>
///   <item>全屏请求转给外壳的 <see cref="IFullscreenHost"/>（MainWindow 已实现）；</item>
///   <item>页面 <c>Loaded</c> / <c>Unloaded</c> 与窗口焦点变化时暂停（外壳会缓存工具页）。</item>
/// </list>
/// </summary>
public partial class VsrgView : UserControl
{
    private Window? _hostWindow;
    private IFullscreenHost? _fullscreenHost;
    private VsrgViewModel? _viewModel;

    /// <summary>构造。</summary>
    public VsrgView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private VsrgViewModel? Vm => DataContext as VsrgViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FullscreenRequested -= OnFullscreenRequested;
        }

        _viewModel = e.NewValue as VsrgViewModel;

        if (_viewModel is not null)
        {
            _viewModel.FullscreenRequested += OnFullscreenRequested;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 整页收键盘：焦点落在页面上，键位才会走本页的 PreviewKeyDown
        Focusable = true;
        Keyboard.Focus(this);

        _fullscreenHost = Window.GetWindow(this) as IFullscreenHost;

        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.Deactivated -= OnHostDeactivated;
            _hostWindow.Deactivated += OnHostDeactivated;
            _hostWindow.Activated -= OnHostActivated;
            _hostWindow.Activated += OnHostActivated;
        }

        // 切回来时如果 VM 还认为自己在游玩（外壳缓存了视图），帧循环要重新起
        Vm?.ResumeAfterAway();

        // 截图 / 调试验收：YLT_VSRG_PAGE=settings|play|result 时直接切到那个页面
        Vm?.ApplyDebugPage();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // ⚠ 先停再摘事件：外壳把视图塞进缓存时不会调 NotifyClosed，
        //   不停表的话切走之后判定还在跑（切回来已经漏完一整首）。
        Vm?.PauseForAway();

        if (_hostWindow is not null)
        {
            _hostWindow.Deactivated -= OnHostDeactivated;
            _hostWindow.Activated -= OnHostActivated;
            _hostWindow = null;
        }
    }

    private void OnHostDeactivated(object? sender, EventArgs e) => Vm?.PauseForLostFocus();

    private void OnHostActivated(object? sender, EventArgs e) => Vm?.ResumeAfterFocus();

    /// <summary>全屏请求：交给外壳（没找到外壳就什么也不做 —— 独立窗口里也能正常玩）。</summary>
    private void OnFullscreenRequested(object? sender, bool on)
    {
        _fullscreenHost ??= Window.GetWindow(this) as IFullscreenHost;
        _fullscreenHost?.SetFullscreen(on);
    }

    /// <summary>
    /// 键盘按下：游玩时先给 VM（哪个键打哪一轨由它按配置决定），吃掉的不再往下传。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (vm.HandleKeyDown(e.Key))
        {
            e.Handled = true;
            return;
        }

        // 设置页正在改键：由键位控件自己处理（这里不抢）
        if (Keyboard.FocusedElement is KeyBindRow) return;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (vm.HandleKeyUp(e.Key)) e.Handled = true;
    }

    /// <summary>拖进来：只认 .osz（其它类型显示禁止光标）。</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasOsz(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>放下：把 .osz 交给 VM 导入（支持一次拖多个）。</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Vm is not { } vm) return;

        var files = GetDroppedFiles(e.Data);
        if (files.Count == 0) return;

        Keyboard.Focus(this);
        await vm.ImportFilesAsync(files);
    }

    private static bool HasOsz(IDataObject? data)
        => GetDroppedFiles(data).Any(f => Path.GetExtension(f).Equals(".osz", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> GetDroppedFiles(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();

        try
        {
            return data.GetData(DataFormats.FileDrop) is string[] paths
                ? paths
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
