using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.ScreenRecorder.Services;

namespace YuLiangToolkit.Tools.ScreenRecorder.Views;

/// <summary>
/// 「设置快捷键」对话框：在窗口级 <c>PreviewKeyDown</c> 里捕获组合键，按「保存」/ Enter 确认。
///
/// <para><b>交互规则</b>：Esc = 取消（<see cref="Result"/> 为 null）；只按修饰键（Ctrl / Alt / Shift / Win）
/// 只更新预览、不确认；字母 / 数字 / Space 必须配合修饰键，F1~F24 可单独用（规则与
/// <see cref="HotkeySpec.TryFromKey"/> 一致）；无效组合用红字说明原因，不弹 MessageBox。</para>
///
/// <para><b>结果</b>：<see cref="Result"/> 非空且 <c>ShowDialog() == true</c> 表示用户保存成功，
/// 调用方用 <see cref="HotkeySpec.DisplayText"/> 存配置即可。窗口不自己注册热键。</para>
/// </summary>
public partial class HotkeyCaptureWindow : Window
{
    private const string DefaultHint = "F1~F24 可以单独使用；字母/数字需要配合 Ctrl / Alt / Shift / Win；Esc 取消";
    private const string WaitingText = "请按下组合键…";
    private const string ModifierHint = "继续按下主键（F1~F24 / 字母 / 数字 / 空格）…";

    private HotkeySpec? _candidate;

    /// <summary>
    /// 打开对话框。<paramref name="currentText"/> 是当前配置里的热键文字（例如 <c>Ctrl+Shift+F9</c>），
    /// 能解析就预填显示；<c>null</c> / 解析失败时显示「请按下组合键…」。
    /// </summary>
    public HotkeyCaptureWindow(string? currentText = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(currentText)
            && HotkeySpec.TryParse(currentText, out var current)
            && !current.IsEmpty)
        {
            _candidate = current;
        }

        // Lead 会在 ShowDialog 前再设置一次 Owner；这里先给个默认值，判空即可。
        try
        {
            var owner = Application.Current?.MainWindow;
            if (owner is not null && !ReferenceEquals(owner, this)) Owner = owner;
        }
        catch
        {
            // 主窗口已关闭等极端情况：没有 Owner 也能正常显示
        }

        UpdateDisplay();
    }

    /// <summary>用户保存的组合键；取消 / 直接关窗时为 null。</summary>
    public HotkeySpec? Result { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 打开就能直接按键：把键盘焦点收到显示框上（焦点在窗口内，PreviewKeyDown 才稳定触发）
        Activate();
        if (!KeyBox.Focus()) Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt 组合在 WPF 里是 Key.System；输入法 / 死键也换成真正的物理键
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        // Esc：一律取消（不管有没有按修饰键）
        if (key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
            return;
        }

        var modifiers = Keyboard.Modifiers;

        // 只按了修饰键：只更新预览，不确认
        if (IsModifierKey(key))
        {
            e.Handled = true;
            modifiers |= ModifierOfKey(key);
            _candidate = null;
            var preview = HotkeySpec.FormatDisplayText(HotkeySpec.ToWin32Modifiers(modifiers), 0);
            SetKeyText(preview.Length == 0 ? WaitingText : preview, "TextSecondaryBrush");
            SetHint(ModifierHint, "TextSecondaryBrush");
            SaveButton.IsEnabled = false;
            return;
        }

        if (key == Key.None)
        {
            e.Handled = true;
            return;
        }

        // 不按修饰键的 Enter = 「保存」；带修饰键的 Enter 当组合键捕获（例如 Ctrl+Enter）
        if (key == Key.Enter && modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_candidate is { IsEmpty: false }) SaveAndClose();
            else SetHint("请先按下组合键，再按 Enter 保存", "DangerBrush");
            return;
        }

        e.Handled = true;

        if (HotkeySpec.TryFromKey(key, modifiers, out var spec) && !spec.IsEmpty)
        {
            _candidate = spec;
            UpdateDisplay();
            return;
        }

        // 无效组合：把按下的东西显示出来，用红字说明为什么不行
        _candidate = null;
        var win32Modifiers = HotkeySpec.ToWin32Modifiers(modifiers);
        var pressedText = HotkeySpec.FormatDisplayText(win32Modifiers, KeyInterop.VirtualKeyFromKey(key));
        SetKeyText(pressedText.Length == 0 ? WaitingText : pressedText, "DangerBrush");
        SetHint(InvalidReason(key, win32Modifiers), "DangerBrush");
        SaveButton.IsEnabled = false;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveAndClose();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancel();

    private void SaveAndClose()
    {
        var spec = _candidate;
        if (spec is null || spec.IsEmpty) return;
        Result = spec;
        CloseWith(true);
    }

    private void Cancel()
    {
        Result = null;
        CloseWith(false);
    }

    /// <summary>按当前状态刷新显示框 / 提示 / 保存按钮可用性。</summary>
    private void UpdateDisplay()
    {
        if (_candidate is { IsEmpty: false } spec)
        {
            SetKeyText(spec.DisplayText, "AccentBrush");
            SetHint(DefaultHint, "TextSecondaryBrush");
            SaveButton.IsEnabled = true;
            return;
        }

        SetKeyText(WaitingText, "TextSecondaryBrush");
        SetHint(DefaultHint, "TextSecondaryBrush");
        SaveButton.IsEnabled = false;
    }

    /// <summary>设置显示框文字 + 颜色（颜色保持动态资源，换主题立即生效）。</summary>
    private void SetKeyText(string text, string brushKey)
    {
        KeyText.Text = text;
        KeyText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    /// <summary>设置说明文字 + 颜色（无效组合用 DangerBrush 红字）。</summary>
    private void SetHint(string text, string brushKey)
    {
        HintText.Text = text;
        HintText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    /// <summary>关窗：模态时用 DialogResult 让 ShowDialog 返回 true / false；非模态直接 Close。</summary>
    private void CloseWith(bool saved)
    {
        try
        {
            DialogResult = saved;
        }
        catch (InvalidOperationException)
        {
            // 以 Show() 打开（非模态）时不能设置 DialogResult，直接关窗
            Close();
        }
    }

    private static string InvalidReason(Key key, uint win32Modifiers)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return "没有识别到按键，请重新按下组合键";
        if (!HotkeySpec.IsSupportedKey(virtualKey)) return "暂不支持这个按键，请用 F1~F24、A~Z、0~9 或 Space";
        if ((win32Modifiers & HotkeySpec.ModMask) == 0) return "字母 / 数字 / 空格需要配合 Ctrl / Alt / Shift / Win 使用";
        return "这个组合不可用，请换一个组合键";
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;

    private static ModifierKeys ModifierOfKey(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
        Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
        Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
        Key.LWin or Key.RWin => ModifierKeys.Windows,
        _ => ModifierKeys.None,
    };
}
