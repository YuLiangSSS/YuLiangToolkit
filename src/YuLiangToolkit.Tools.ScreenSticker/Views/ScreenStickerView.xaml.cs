using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using YuLiangToolkit.Tools.ScreenSticker.ViewModels;

namespace YuLiangToolkit.Tools.ScreenSticker.Views;

/// <summary>
/// 「屏幕贴纸」工具页：选图 / 最近使用缩略图 / 桌面贴纸管理 / 设置。
/// <para>页面逻辑全在 <see cref="ScreenStickerViewModel"/>；这里只做 <c>InitializeComponent</c>
/// 与一个 Ctrl+V 快捷键（贴剪贴板图片）。</para>
///
/// <para><b>Ctrl+V 为什么挂在宿主窗口上</b>：页面自己不一定有键盘焦点，页内控件收到按键时事件会
/// 冒泡到窗口；按 AGENTS.md 的约定在 <c>Loaded</c> 挂、<c>Unloaded</c> 摘、每次 Loaded 都先摘再挂
/// （外壳会缓存工具视图，Loaded 可能来多次）。焦点在文本框里时不抢，让文本框正常粘文字。</para>
/// </summary>
public partial class ScreenStickerView : UserControl
{
    private Window? _hostWindow;

    public ScreenStickerView()
    {
        InitializeComponent();
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e) => AttachHostWindow();

    private void OnViewUnloaded(object sender, RoutedEventArgs e) => DetachHostWindow();

    /// <summary>挂到当前所在窗口；重复调用先摘再挂。</summary>
    private void AttachHostWindow()
    {
        DetachHostWindow();
        var window = Window.GetWindow(this);
        if (window is null) return;
        _hostWindow = window;
        window.PreviewKeyDown += OnHostPreviewKeyDown;
    }

    private void DetachHostWindow()
    {
        if (_hostWindow is null) return;
        try { _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown; } catch { /* 窗口已关失败忽略 */ }
        _hostWindow = null;
    }

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;

        // 输入框里的 Ctrl+V 留给输入框（最近使用上限 / 日志），不要抢去贴图
        if (Keyboard.FocusedElement is TextBoxBase) return;

        if (DataContext is ScreenStickerViewModel viewModel
            && viewModel.PasteClipboardCommand.CanExecute(null))
        {
            viewModel.PasteClipboardCommand.Execute(null);
            e.Handled = true;
        }
    }
}
