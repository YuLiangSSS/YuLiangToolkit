using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Tools.Kps.Models;

namespace YuLiangToolkit.Tools.Kps.Views.Controls;

/// <summary>
/// 键位网格控件：主界面预览和 Overlay 共用。
///
/// <para>骨架版本只有依赖属性（完整渲染由队友实现）：绑定 <see cref="LayoutRows"/>（<see cref="KpsRowView"/> 集合），
/// 每个格子渲染 <see cref="KpsKeyCell"/> 的 Label / IsPressed / CountText / 图片 / 颜色，
/// 并负责按下动画（缩放 / 发光 / 闪光）与音符效果。</para>
/// </summary>
public partial class KeyGridView : UserControl
{
    public static readonly DependencyProperty LayoutRowsProperty = DependencyProperty.Register(
        nameof(LayoutRows), typeof(IEnumerable), typeof(KeyGridView),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(
        nameof(CellWidth), typeof(double), typeof(KeyGridView), new PropertyMetadata(66d));

    public static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(
        nameof(CellHeight), typeof(double), typeof(KeyGridView), new PropertyMetadata(74d));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(KeyGridView), new PropertyMetadata(6d));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(KeyGridView), new PropertyMetadata(10d));

    /// <summary>按键文字字号（不要叫 FontSize：会和 <see cref="System.Windows.Controls.Control.FontSize"/> 撞名）。</summary>
    public static readonly DependencyProperty KeyFontSizeProperty = DependencyProperty.Register(
        nameof(KeyFontSize), typeof(double), typeof(KeyGridView), new PropertyMetadata(22d));

    public static readonly DependencyProperty CounterFontSizeProperty = DependencyProperty.Register(
        nameof(CounterFontSize), typeof(double), typeof(KeyGridView), new PropertyMetadata(11d));

    public static readonly DependencyProperty ShowCountersProperty = DependencyProperty.Register(
        nameof(ShowCounters), typeof(bool), typeof(KeyGridView), new PropertyMetadata(true));

    public static readonly DependencyProperty PressedScaleProperty = DependencyProperty.Register(
        nameof(PressedScale), typeof(double), typeof(KeyGridView), new PropertyMetadata(0.9d));

    public static readonly DependencyProperty GlowEnabledProperty = DependencyProperty.Register(
        nameof(GlowEnabled), typeof(bool), typeof(KeyGridView), new PropertyMetadata(true));

    public static readonly DependencyProperty FlashEnabledProperty = DependencyProperty.Register(
        nameof(FlashEnabled), typeof(bool), typeof(KeyGridView), new PropertyMetadata(false));

    public static readonly DependencyProperty EffectDurationMsProperty = DependencyProperty.Register(
        nameof(EffectDurationMs), typeof(int), typeof(KeyGridView), new PropertyMetadata(110));

    public static readonly DependencyProperty NoteEffectEnabledProperty = DependencyProperty.Register(
        nameof(NoteEffectEnabled), typeof(bool), typeof(KeyGridView), new PropertyMetadata(false));

    public static readonly DependencyProperty NoteEffectColorProperty = DependencyProperty.Register(
        nameof(NoteEffectColor), typeof(Brush), typeof(KeyGridView), new PropertyMetadata(null));

    public static readonly DependencyProperty NoteEffectOpacityProperty = DependencyProperty.Register(
        nameof(NoteEffectOpacity), typeof(double), typeof(KeyGridView), new PropertyMetadata(0.7d));

    public static readonly DependencyProperty NoteEffectDurationMsProperty = DependencyProperty.Register(
        nameof(NoteEffectDurationMs), typeof(int), typeof(KeyGridView), new PropertyMetadata(700));

    public static readonly DependencyProperty NoteEffectHeightProperty = DependencyProperty.Register(
        nameof(NoteEffectHeight), typeof(double), typeof(KeyGridView), new PropertyMetadata(0.55d));

    public static readonly DependencyProperty NoteEffectReverseProperty = DependencyProperty.Register(
        nameof(NoteEffectReverse), typeof(bool), typeof(KeyGridView), new PropertyMetadata(false));

    /// <summary>主界面预览的编辑模式：显示选中框、点击选中格子。</summary>
    public static readonly DependencyProperty IsEditorModeProperty = DependencyProperty.Register(
        nameof(IsEditorMode), typeof(bool), typeof(KeyGridView), new PropertyMetadata(false));

    /// <summary>当前选中的格子（编辑模式画高亮）；由 ViewModel 维护。</summary>
    public static readonly DependencyProperty SelectedCellProperty = DependencyProperty.Register(
        nameof(SelectedCell), typeof(KpsKeyCell), typeof(KeyGridView), new PropertyMetadata(null));

    /// <summary>点格子的命令（参数 = <see cref="KpsKeyCell"/>）；主界面用它选中要编辑的键。</summary>
    public static readonly DependencyProperty CellClickCommandProperty = DependencyProperty.Register(
        nameof(CellClickCommand), typeof(ICommand), typeof(KeyGridView), new PropertyMetadata(null));

    public KeyGridView()
    {
        InitializeComponent();
    }

    /// <summary>行集合（<see cref="KpsRowView"/>）。</summary>
    public IEnumerable? LayoutRows
    {
        get => (IEnumerable?)GetValue(LayoutRowsProperty);
        set => SetValue(LayoutRowsProperty, value);
    }

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    public double CellHeight
    {
        get => (double)GetValue(CellHeightProperty);
        set => SetValue(CellHeightProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double KeyFontSize
    {
        get => (double)GetValue(KeyFontSizeProperty);
        set => SetValue(KeyFontSizeProperty, value);
    }

    public double CounterFontSize
    {
        get => (double)GetValue(CounterFontSizeProperty);
        set => SetValue(CounterFontSizeProperty, value);
    }

    public bool ShowCounters
    {
        get => (bool)GetValue(ShowCountersProperty);
        set => SetValue(ShowCountersProperty, value);
    }

    public double PressedScale
    {
        get => (double)GetValue(PressedScaleProperty);
        set => SetValue(PressedScaleProperty, value);
    }

    public bool GlowEnabled
    {
        get => (bool)GetValue(GlowEnabledProperty);
        set => SetValue(GlowEnabledProperty, value);
    }

    public bool FlashEnabled
    {
        get => (bool)GetValue(FlashEnabledProperty);
        set => SetValue(FlashEnabledProperty, value);
    }

    public int EffectDurationMs
    {
        get => (int)GetValue(EffectDurationMsProperty);
        set => SetValue(EffectDurationMsProperty, value);
    }

    public bool NoteEffectEnabled
    {
        get => (bool)GetValue(NoteEffectEnabledProperty);
        set => SetValue(NoteEffectEnabledProperty, value);
    }

    public Brush? NoteEffectColor
    {
        get => (Brush?)GetValue(NoteEffectColorProperty);
        set => SetValue(NoteEffectColorProperty, value);
    }

    public double NoteEffectOpacity
    {
        get => (double)GetValue(NoteEffectOpacityProperty);
        set => SetValue(NoteEffectOpacityProperty, value);
    }

    public int NoteEffectDurationMs
    {
        get => (int)GetValue(NoteEffectDurationMsProperty);
        set => SetValue(NoteEffectDurationMsProperty, value);
    }

    public double NoteEffectHeight
    {
        get => (double)GetValue(NoteEffectHeightProperty);
        set => SetValue(NoteEffectHeightProperty, value);
    }

    public bool NoteEffectReverse
    {
        get => (bool)GetValue(NoteEffectReverseProperty);
        set => SetValue(NoteEffectReverseProperty, value);
    }

    public bool IsEditorMode
    {
        get => (bool)GetValue(IsEditorModeProperty);
        set => SetValue(IsEditorModeProperty, value);
    }

    public KpsKeyCell? SelectedCell
    {
        get => (KpsKeyCell?)GetValue(SelectedCellProperty);
        set => SetValue(SelectedCellProperty, value);
    }

    public ICommand? CellClickCommand
    {
        get => (ICommand?)GetValue(CellClickCommandProperty);
        set => SetValue(CellClickCommandProperty, value);
    }
}
