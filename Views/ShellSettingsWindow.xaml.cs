using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 「主页设置」窗口（模型窗口：Owner = 主窗口、居中、不显示在任务栏）。
///
/// <para>目前四组设置：</para>
/// <list type="bullet">
///   <item>「界面风格」：默认（深色）/ 液态玻璃 二选一（绑定 <c>MainViewModel.DefaultThemeSelected</c> /
///   <c>LiquidGlassThemeSelected</c>）；点一下由 <c>ThemeManager.Apply</c> 立即换肤，本窗口自己也跟着变（见 AGENTS 2.20）；</item>
///   <item>「外观」：鼠标粒子效果开关（绑定 <c>MainViewModel.ParticlesEnabled</c>）；</item>
///   <item>「粒子颜色」：启用自定义颜色 + 两种颜色（可分别启用；只启用一种 = 单色粒子），
///   颜色用 <see cref="ColorPickerButton"/> 选；</item>
///   <item>「窗口与托盘」：点 × 时「关闭应用 / 最小化到托盘」（绑定 <c>MinimizeToTrayOnClose</c> / <c>ExitOnClose</c>）。</item>
/// </list>
/// <para>四组都自动存进 <c>Config\shell\config.json</c>。以后加设置项直接在这个 XAML 里加卡片即可。</para>
///
/// <para>无边框窗口的拖拽 / 关闭和主窗口同一套做法；<c>Esc</c> 关窗；
/// 另带一个 <see cref="CaptureTo"/> 供 <c>YLT_OPEN_SETTINGS</c> 截图模式使用。</para>
/// </summary>
public partial class ShellSettingsWindow : Window
{
    public ShellSettingsWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>标题栏手动拖拽（点了关闭按钮就别拖）。</summary>
    private void OnTitleBarMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualAncestor<System.Windows.Controls.Button>(source) is not null)
        {
            return;
        }
        try { DragMove(); } catch { /* 拖动过程中被中断等情况忽略 */ }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T match) return match;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 开发调试用：把本窗口渲染成 PNG 并退出程序（由 App 的 <c>YLT_OPEN_SETTINGS</c> + <c>YLT_SHOT</c> 组合触发）。
    /// 与 MainWindow.CaptureTo 同一套思路：等布局稳定再截，截完关掉整个程序。
    /// </summary>
    public void CaptureTo(string path)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            await Task.Delay(1200);
            try
            {
                var w = (int)Math.Max(1, ActualWidth);
                var h = (int)Math.Max(1, ActualHeight);
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                if (Content is Visual visual) rtb.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(path);
                encoder.Save(fs);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { /* 忽略 */ }
            }
            App.RequestExit();   // 截图模式强制退出（不受「关闭=最小化到托盘」影响）
        });
    }
}
