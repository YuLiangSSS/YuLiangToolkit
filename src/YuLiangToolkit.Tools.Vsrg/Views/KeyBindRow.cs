using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.Vsrg.Core;
using YuLiangToolkit.Tools.Vsrg.ViewModels;

namespace YuLiangToolkit.Tools.Vsrg.Views;

/// <summary>
/// 一键位的改键按钮：点一下进入「等按键」状态 → 按一个键就绑定。
///
/// <para><b>为什么继承 <see cref="Button"/></b>：它就是「一个能点的方框」，
/// 直接吃项目里现成的 <c>SecondaryButtonStyle</c>（深色主题下的悬停 / 按下态都不用另写模板）；
/// 需要额外做的只有「点一下之后把键盘收过来」和「捕获一个键」。</para>
///
/// <para><b>为什么不复用 Core 的 <c>HotkeyBox</c></b>：那个是给「组合快捷键」用的
/// （要等所有键松开、认 Ctrl / Shift 组合），而音游的轨道键只认**单个键**、按下即生效。</para>
/// </summary>
public sealed class KeyBindRow : Button
{
    /// <summary>绑定的键位行（<see cref="KeyBindingRow"/>）。</summary>
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(KeyBindingRow), typeof(KeyBindRow),
        new PropertyMetadata(null, OnRowChanged));

    /// <summary>构造：能聚焦（要收键盘）、显示成方框。</summary>
    public KeyBindRow()
    {
        Focusable = true;
        MinWidth = 62;
        Height = 30;
        Padding = new Thickness(8, 0, 8, 0);
        FontSize = 12.5;
        Cursor = Cursors.Hand;
        Content = "未设置";
        ToolTip = "点一下，然后按一个键（Esc 取消）";
    }

    /// <summary>键位行。</summary>
    public KeyBindingRow? Row
    {
        get => (KeyBindingRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not KeyBindRow control) return;

        if (e.OldValue is KeyBindingRow old) old.PropertyChanged -= control.OnRowPropertyChanged;
        if (e.NewValue is KeyBindingRow row) row.PropertyChanged += control.OnRowPropertyChanged;

        control.SyncVisual();
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyBindingRow.IsCapturing) or nameof(KeyBindingRow.DisplayText))
            SyncVisual();
    }

    private void SyncVisual()
    {
        Content = Row?.DisplayText ?? "未设置";
        FontWeight = Row?.IsCapturing == true ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <inheritdoc />
    protected override void OnClick()
    {
        base.OnClick();

        if (Row is null) return;

        Row.BeginCapture();
        Focus();
        Keyboard.Focus(this);
    }

    /// <summary>
    /// 捕获中的一个键：绑定它（Esc 取消；修饰键 / Tab 之类忽略，继续等）。
    /// <para>用 <c>PreviewKeyDown</c>（隧道阶段）：这样 Tab / 空格不会被按钮自己的默认行为吃掉。</para>
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (Row is not { IsCapturing: true } row) return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            row.CancelCapture();
            return;
        }

        if (!LaneKeys.IsBindable(e.Key)) return;

        var name = LaneKeys.FromKey(e.Key);
        if (name.Length == 0) return;

        row.CommitCapture(name);
    }
}
