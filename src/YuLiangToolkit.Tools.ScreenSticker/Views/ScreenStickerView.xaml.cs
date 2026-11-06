using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using YuLiangToolkit.Tools.ScreenSticker.ViewModels;

namespace YuLiangToolkit.Tools.ScreenSticker.Views;

/// <summary>
/// 「屏幕贴纸」工具页：选图 / 拖入图片 / 最近使用缩略图 / 桌面贴纸管理 / 设置。
/// <para>页面逻辑全在 <see cref="ScreenStickerViewModel"/>；这里只做 <c>InitializeComponent</c>、
/// 一个 Ctrl+V 快捷键（贴剪贴板图片）和整页拖入（拖放分流规则也写在 ViewModel 里）。</para>
///
/// <para><b>Ctrl+V 为什么挂在宿主窗口上</b>：页面自己不一定有键盘焦点，页内控件收到按键时事件会
/// 冒泡到窗口；按 AGENTS.md 的约定在 <c>Loaded</c> 挂、<c>Unloaded</c> 摘、每次 Loaded 都先摘再挂
/// （外壳会缓存工具视图，Loaded 可能来多次）。焦点在文本框里时不抢，让文本框正常粘文字。</para>
///
/// <para><b>拖入为什么放在根节点</b>：根 <see cref="UserControl"/> 设了 <c>Background="Transparent"</c> +
/// <c>AllowDrop="True"</c>，拖放事件会从子元素冒泡到这里，所以整个工具页都是落点。</para>
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

    // ===================== 拖入图片（可多张，松手即贴） =====================

    /// <summary>拖入悬停：数据里至少有一个能贴的图片才显示「复制」光标，否则显示禁止。</summary>
    private void OnStickerDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = HasDroppableImage(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>
    /// 放下：把拖入的路径交给 ViewModel 过滤 + 批量贴图（与「选择图片…」同一套流程）。
    /// 不支持的路径会被忽略，状态栏 / 日志里会说明。
    /// </summary>
    private void OnStickerDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not ScreenStickerViewModel viewModel) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        try
        {
            // 拖放回调在 UI 线程：直接贴是安全的（Session.AddSticker 内部要 new / Show 贴纸窗口）
            viewModel.AddDroppedImages(paths);
        }
        catch (Exception ex)
        {
            // 拖放回调由原生 OLE 调用，这里把异常抛出去会带崩整个程序
            viewModel.Log("✗ 处理拖入的图片失败：" + ex.Message);
        }
    }

    /// <summary>拖放数据里有没有至少一个能贴的图片文件（拖很多个时只要有一个就能放下）。</summary>
    private bool HasDroppableImage(DragEventArgs e)
    {
        if (DataContext is not ScreenStickerViewModel viewModel) return false;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return false;

        foreach (var path in paths)
        {
            if (viewModel.CanAcceptDrop(path)) return true;
        }
        return false;
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
