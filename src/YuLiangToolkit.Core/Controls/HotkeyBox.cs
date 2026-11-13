using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YuLiangToolkit.Core.Input;

namespace YuLiangToolkit.Core.Controls;

/// <summary>
/// 改键输入框：点一下进入捕获 → 按下组合键 → 显示成 <c>Ctrl+U+K</c>。
///
/// <para><b>捕获规则（按项目约定）</b>：</para>
/// <list type="bullet">
///   <item>只认「<b>同时按下</b>」的一组键 —— 分步是录完之后再拆的，不在这里录两步；</item>
///   <item><b>所有键都松开之后才定下来</b>：只要还有键按着就继续识别（所以可以边按边看到预览）；</item>
///   <item>实时预览当前按住的组合，并用红字说明为什么不合法（原因见 <see cref="KeyCombo.Validate"/>）；</item>
///   <item><see cref="Key.Escape"/> 取消、点右侧 ✕ 清空（= 不设置）。</item>
/// </list>
///
/// <para><b>为什么不用独立弹窗</b>：录屏工具以前是弹一个 <c>HotkeyCaptureWindow</c>，
/// 汇总页里十几行每行都弹一次太啰嗦；控件内置捕获可以「点一下就地改」。</para>
///
/// <para>快捷键体系里的 <see cref="KeyCombo"/> 只做数据，这个控件负责交互。</para>
/// </summary>
public sealed class HotkeyBox : Control
{
    /// <summary>捕获状态变化（界面据此换边框色 / 显示提示）。</summary>
    public static readonly DependencyProperty IsCapturingProperty = DependencyProperty.Register(
        nameof(IsCapturing), typeof(bool), typeof(HotkeyBox),
        new PropertyMetadata(false, (d, _) => ((HotkeyBox)d).OnCapturingChanged()));

    /// <summary>当前组合（文本形式，例如 <c>Ctrl+F9</c>；空串 = 未设置）。</summary>
    public static readonly DependencyProperty ComboTextProperty = DependencyProperty.Register(
        nameof(ComboText), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnVisualInputChanged));

    /// <summary>捕获过程中实时预览的文本（没在捕获时为空）。</summary>
    public static readonly DependencyProperty PreviewTextProperty = DependencyProperty.Register(
        nameof(PreviewText), typeof(string), typeof(HotkeyBox), new PropertyMetadata("", OnVisualInputChanged));

    /// <summary>错误 / 提示文字（红字显示在框下面）。</summary>
    public static readonly DependencyProperty HintTextProperty = DependencyProperty.Register(
        nameof(HintText), typeof(string), typeof(HotkeyBox), new PropertyMetadata(""));

    /// <summary>未设置时显示的占位文字。</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(HotkeyBox),
        new PropertyMetadata("点一下设置快捷键", OnVisualInputChanged));

    /// <summary>组合被确认（用户松开所有键、且组合合法）。</summary>
    public event EventHandler<KeyCombo>? ComboCommitted;

    /// <summary>用户点了清空。</summary>
    public event EventHandler? ComboCleared;

    private KeyCombo _candidate = KeyCombo.Empty;
    private readonly HashSet<int> _held = new();
    private DispatcherTimer? _hoverTimer;

    /// <summary>构造：可聚焦（要接收键盘事件），带一个透明底板好点中。</summary>
    static HotkeyBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HotkeyBox),
            new FrameworkPropertyMetadata(typeof(HotkeyBox)));
    }

    /// <inheritdoc />
    public HotkeyBox()
    {
        Focusable = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Cursor = Cursors.Hand;
        MinHeight = 28;
        MinWidth = 120;
    }

    /// <summary>正在捕获。</summary>
    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        private set => SetValue(IsCapturingProperty, value);
    }

    /// <summary>当前组合文本（双向绑定用）。</summary>
    public string ComboText
    {
        get => (string)GetValue(ComboTextProperty);
        set => SetValue(ComboTextProperty, value);
    }

    /// <summary>任一依赖属性变了都重新算「框里显示什么」（模板加载、外部改值都走这里）。</summary>
    private static void OnVisualInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HotkeyBox)d).RefreshVisual();

    /// <summary>捕获中的实时预览。</summary>
    public string PreviewText
    {
        get => (string)GetValue(PreviewTextProperty);
        private set => SetValue(PreviewTextProperty, value);
    }

    /// <summary>提示 / 错误文字。</summary>
    public string HintText
    {
        get => (string)GetValue(HintTextProperty);
        private set => SetValue(HintTextProperty, value);
    }

    /// <summary>未设置时显示的占位文字。</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>能不能捕获（表格里「整工具禁用」时关掉）。</summary>
    public static readonly DependencyProperty IsCaptureEnabledProperty = DependencyProperty.Register(
        nameof(IsCaptureEnabled), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(true));

    /// <summary>能不能捕获（表格里「整工具禁用」时关掉；控件模板的触发器绑它）。</summary>
    public bool IsCaptureEnabled
    {
        get => (bool)GetValue(IsCaptureEnabledProperty);
        set => SetValue(IsCaptureEnabledProperty, value);
    }

    // ================================================================== 交互

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsCaptureEnabled) return;

        Focus();
        BeginCapture();
        e.Handled = true;
    }

    /// <summary>进入捕获状态。</summary>
    public void BeginCapture()
    {
        if (IsCapturing) return;

        _held.Clear();
        _candidate = KeyCombo.Empty;
        PreviewText = "";
        HintText = "请按下要设置的组合键（同时按住；全部松开后确定）· Esc 取消";
        IsCapturing = true;
    }

    /// <summary>退出捕获状态（取消）。</summary>
    public void CancelCapture()
    {
        if (!IsCapturing) return;
        IsCapturing = false;
        _held.Clear();
        _candidate = KeyCombo.Empty;
        PreviewText = "";
        HintText = "";
    }

    private void OnCapturingChanged()
    {
        if (IsCapturing)
        {
            CaptureKeyboard();
            return;
        }

        ReleaseKeyboard();
        RefreshVisual();
    }

    private void CaptureKeyboard()
    {
        try { Keyboard.AddKeyDownHandler(this, OnCaptureKeyDown); } catch { /* 忽略 */ }
        try { Keyboard.AddKeyUpHandler(this, OnCaptureKeyUp); } catch { /* 忽略 */ }
    }

    private void ReleaseKeyboard()
    {
        try { Keyboard.RemoveKeyDownHandler(this, OnCaptureKeyDown); } catch { /* 忽略 */ }
        try { Keyboard.RemoveKeyUpHandler(this, OnCaptureKeyUp); } catch { /* 忽略 */ }
    }

    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsCapturing) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;

        if (key == Key.Escape)
        {
            CancelCapture();
            e.Handled = true;
            return;
        }

        // 单独的修饰键：记下来、只更新预览，不当作主键
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return;

        _held.Add(virtualKey);
        _candidate = KeyCombo.FromKeys(_held);

        PreviewText = _candidate.ToString();
        HintText = _candidate.Validate() is { Length: > 0 } error && _candidate.Modifiers.Count == 0
            ? error
            : "还按着键…全部松开后确定（Esc 取消）";

        e.Handled = true;
    }

    private void OnCaptureKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsCapturing) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0) _held.Remove(virtualKey);

        e.Handled = true;

        // 约定：所有键都松开才算录完
        if (_held.Count > 0) return;

        Commit();
    }

    /// <summary>所有键松开后收尾：合法就提交，不合法就停在捕获状态并说明原因。</summary>
    private void Commit()
    {
        var combo = _candidate;
        _candidate = KeyCombo.Empty;

        if (combo.IsEmpty)
        {
            // 只按了一下 Esc 之外的修饰键然后松开：继续等，不算取消
            PreviewText = "";
            HintText = "请按下要设置的组合键（同时按住；全部松开后确定）· Esc 取消";
            return;
        }

        var error = combo.Validate();
        if (error.Length > 0)
        {
            PreviewText = combo.ToString();
            HintText = error + "（可以再按一次重录，Esc 取消）";
            return;
        }

        ComboText = combo.ToString();
        CancelCapture();
        ComboCommitted?.Invoke(this, combo);
        RefreshVisual();
    }

    /// <summary>清空（= 这条快捷键不设置）。</summary>
    public void ClearCombo()
    {
        ComboText = "";
        CancelCapture();
        ComboCleared?.Invoke(this, EventArgs.Empty);
        RefreshVisual();
    }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // 模板里的 ✕ 按钮：ResourceDictionary 里不能写事件处理器（MC6024），只能在代码里挂
        if (GetTemplateChild("Clear") is Button clear)
        {
            clear.Click -= OnClearClick;
            clear.Click += OnClearClick;
        }

        // 模板刚建好时把当前值算一遍（之前 RefreshVisual 调过，但那时还没模板，值没地方显示）
        RefreshVisual();
    }

    /// <summary>模板里 ✕ 按钮的点击。</summary>
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!IsCaptureEnabled) return;
        ClearCombo();
    }

    private void RefreshVisual()
    {
        // 捕获中显示预览，否则显示已保存的值；都没有就显示占位文字
        DisplayText = IsCapturing
            ? (PreviewText.Length > 0 ? PreviewText : "按下组合键…")
            : (string.IsNullOrWhiteSpace(ComboText) ? Placeholder : ComboText);
    }

    /// <summary>框里显示的文字（样板的 TextBlock 绑它）。</summary>
    public static readonly DependencyProperty DisplayTextProperty = DependencyProperty.Register(
        nameof(DisplayText), typeof(string), typeof(HotkeyBox), new PropertyMetadata(""));

    /// <summary>框里显示的文字（只读绑定：捕获中显示预览、否则显示已保存的组合或占位文字）。</summary>
    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        private set => SetValue(DisplayTextProperty, value);
    }

    /// <summary>焦点 / 鼠标进入时自动开始捕获（点一下就录，不用再点第二下）。</summary>
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!IsCaptureEnabled || IsCapturing) return;

        _hoverTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (IsMouseOver && IsCaptureEnabled && !IsCapturing) HintText = "点一下开始设置";
        };
        _hoverTimer.Start();
    }

    /// <summary>离开就撤掉提示（捕获中不撤）。</summary>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsCapturing) HintText = "";
    }

    /// <summary>失焦即取消捕获（避免捕获状态卡住、把整窗口的按键都吃掉）。</summary>
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsCapturing) CancelCapture();
    }

    /// <summary>控件被卸载时一定要摘掉键盘处理器（否则会泄漏 + 影响别的控件）。</summary>
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (VisualParent is null) CancelCapture();
    }
}
