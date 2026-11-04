using System.Windows;
using System.Windows.Input;

namespace YuLiangToolkit.Tools.ScreenRecorder.Views;

/// <summary>
/// 开始录制前的「3 / 2 / 1」浮窗。它只负责显示数字和取消；倒计时本身由 ViewModel 用
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> 驱动，这样取消能立刻生效、不用等定时器。
///
/// <para>倒计时结束时 ViewModel 会关掉这个窗口，然后才启动 FFmpeg —— 所以倒计时**不会**被录进去。</para>
/// </summary>
public partial class CountdownWindow : Window
{
    private bool _allowClose;

    public CountdownWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;

        // 用户 Alt+F4 时不要直接消失：当作“取消倒计时”，由 ViewModel 收尾并真正关闭窗口。
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            CancelRequested?.Invoke(this, EventArgs.Empty);
        };

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };
    }

    /// <summary>用户点了浮窗 / 按了 Esc（ViewModel 据此取消倒计时）。</summary>
    public event EventHandler? CancelRequested;

    /// <summary>更新当前显示的数字。</summary>
    public void SetNumber(int number) => NumberText.Text = Math.Max(0, number).ToString();

    /// <summary>由 ViewModel 在倒计时结束后真正关闭窗口。</summary>
    public void CloseNow()
    {
        _allowClose = true;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
