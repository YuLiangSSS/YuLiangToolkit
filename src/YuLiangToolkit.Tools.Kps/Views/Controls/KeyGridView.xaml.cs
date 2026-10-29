using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using YuLiangToolkit.Tools.Kps.Models;

namespace YuLiangToolkit.Tools.Kps.Views.Controls;

/// <summary>
/// 键位网格控件：主界面预览和 Overlay 共用。
///
/// <para>按 <see cref="LayoutRows"/>（元素是 <see cref="KpsRowView"/>）逐行竖排、行内逐格横排渲染
/// <see cref="KpsKeyCell"/>：格子宽 = <see cref="CellWidth"/> × <see cref="WidthUnits"/>、高 = <see cref="CellHeight"/>，
/// 内容 = 按键文字（可叠图片）+ 底部计数器；颜色 / 图片 / 计数通过单元格自己的
/// <see cref="INotifyPropertyChanged"/> 增量刷新，<b>只有布局类属性变化才重建视觉树</b>。</para>
///
/// <para><b>动画</b>：每格各自负责按下缩放 / 外发光 / 闪光 / 音符条，全部用 WPF 动画（不订阅
/// <c>CompositionTarget.Rendering</c>），长按期间音符条由 <c>RepeatBehavior.Forever</c> 循环。
/// 锁定 Overlay 的鼠标穿透是窗口级行为，这里的格子<b>非编辑模式不拦截任何鼠标事件</b>。</para>
///
/// <para><b>尺寸</b>：控件自身不写死 Width/Height（Overlay 用 SizeToContent 按内容量尺寸），
/// 网格在控件内水平居中；行间距 / 格间距都用 <see cref="Gap"/>。</para>
/// </summary>
public partial class KeyGridView : UserControl
{
    /// <summary>编辑模式下选中格子的高亮边框色（亮蓝）。</summary>
    private static readonly Brush SelectedBorderBrush = CreateFrozenBrush(Color.FromRgb(0x4D, 0xA3, 0xFF));

    /// <summary>音符条默认颜色（调用方没给 <see cref="NoteEffectColor"/> 时用）。</summary>
    private static readonly Brush DefaultNoteBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xC8, 0x57));

    /// <summary>按下闪光色（白）。</summary>
    private static readonly Brush FlashBrush = CreateFrozenBrush(Colors.White);

    /// <summary>图片上方的文字衬底（半透明黑，保证标签可读）。</summary>
    private static readonly Brush ImageLabelBackdropBrush = CreateFrozenBrush(Color.FromArgb(0x8C, 0x00, 0x00, 0x00));

    /// <summary>外发光效果按颜色缓存（冻结后多格共享，避免每格 new 一个 Effect 拖慢渲染）。</summary>
    private static readonly Dictionary<Color, DropShadowEffect> GlowCache = new();

    private readonly List<KeyCellPresenter> _presenters = new();
    private readonly List<INotifyCollectionChanged> _observedCollections = new();

    // ============================== 依赖属性（名字保持骨架约定） ==============================

    /// <summary>行集合（元素为 <see cref="KpsRowView"/>；支持 <see cref="INotifyCollectionChanged"/> 自动重建）。</summary>
    public static readonly DependencyProperty LayoutRowsProperty = DependencyProperty.Register(
        nameof(LayoutRows), typeof(IEnumerable), typeof(KeyGridView),
        new PropertyMetadata(null, OnLayoutRowsChanged));

    /// <summary>标准格子宽度（DIP；实际宽度还要乘 <see cref="KpsKeyCell.WidthUnits"/>）。</summary>
    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(
        nameof(CellWidth), typeof(double), typeof(KeyGridView),
        new PropertyMetadata(66d, OnLayoutMetricsChanged));

    /// <summary>格子高度（DIP）。</summary>
    public static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(
        nameof(CellHeight), typeof(double), typeof(KeyGridView),
        new PropertyMetadata(74d, OnLayoutMetricsChanged));

    /// <summary>行间距 / 格间距（DIP）。</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(KeyGridView),
        new PropertyMetadata(6d, OnLayoutMetricsChanged));

    /// <summary>按键圆角（DIP）。</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(KeyGridView),
        new PropertyMetadata(10d, OnCellStyleChanged));

    /// <summary>按键文字字号（不要叫 FontSize：会和 <see cref="System.Windows.Controls.Control.FontSize"/> 撞名）。</summary>
    public static readonly DependencyProperty KeyFontSizeProperty = DependencyProperty.Register(
        nameof(KeyFontSize), typeof(double), typeof(KeyGridView),
        new PropertyMetadata(22d, OnCellStyleChanged));

    /// <summary>计数器字号。</summary>
    public static readonly DependencyProperty CounterFontSizeProperty = DependencyProperty.Register(
        nameof(CounterFontSize), typeof(double), typeof(KeyGridView),
        new PropertyMetadata(11d, OnCellStyleChanged));

    /// <summary>是否显示每键计数器（还要 && 格子自己的 ShowCounter）。</summary>
    public static readonly DependencyProperty ShowCountersProperty = DependencyProperty.Register(
        nameof(ShowCounters), typeof(bool), typeof(KeyGridView),
        new PropertyMetadata(true, OnCellStyleChanged));

    /// <summary>按下时的缩放比例（&lt; 1 才生效；1 = 不缩放）。</summary>
    public static readonly DependencyProperty PressedScaleProperty = DependencyProperty.Register(
        nameof(PressedScale), typeof(double), typeof(KeyGridView),
        new PropertyMetadata(0.9d, OnCellStyleChanged));

    /// <summary>按下时是否加外发光。</summary>
    public static readonly DependencyProperty GlowEnabledProperty = DependencyProperty.Register(
        nameof(GlowEnabled), typeof(bool), typeof(KeyGridView),
        new PropertyMetadata(true, OnCellStyleChanged));

    /// <summary>按下时是否闪一下白光。</summary>
    public static readonly DependencyProperty FlashEnabledProperty = DependencyProperty.Register(
        nameof(FlashEnabled), typeof(bool), typeof(KeyGridView),
        new PropertyMetadata(false, OnCellStyleChanged));

    /// <summary>按下 / 抬起动画时长（毫秒）。</summary>
    public static readonly DependencyProperty EffectDurationMsProperty = DependencyProperty.Register(
        nameof(EffectDurationMs), typeof(int), typeof(KeyGridView),
        new PropertyMetadata(110, OnCellStyleChanged));

    /// <summary>是否启用音符效果（按下时格子里有一道音符条滑过）。</summary>
    public static readonly DependencyProperty NoteEffectEnabledProperty = DependencyProperty.Register(
        nameof(NoteEffectEnabled), typeof(bool), typeof(KeyGridView),
        new PropertyMetadata(false, OnCellStyleChanged));

    /// <summary>音符条颜色（null = 内置金黄色）。</summary>
    public static readonly DependencyProperty NoteEffectColorProperty = DependencyProperty.Register(
        nameof(NoteEffectColor), typeof(Brush), typeof(KeyGridView),
        new PropertyMetadata(null, OnCellStyleChanged));

    /// <summary>音符条不透明度（0~1）。</summary>
    public static readonly DependencyProperty NoteEffectOpacityProperty = DependencyProperty.Register(
        nameof(NoteEffectOpacity), typeof(double), typeof(KeyGridView),
        new PropertyMetadata(0.7d, OnCellStyleChanged));

    /// <summary>音符条滑过一格的时间（毫秒；长按期间循环）。</summary>
    public static readonly DependencyProperty NoteEffectDurationMsProperty = DependencyProperty.Register(
        nameof(NoteEffectDurationMs), typeof(int), typeof(KeyGridView),
        new PropertyMetadata(700, OnCellStyleChanged));

    /// <summary>音符条高度占格高的比例（0.15~0.9 比较合适）。</summary>
    public static readonly DependencyProperty NoteEffectHeightProperty = DependencyProperty.Register(
        nameof(NoteEffectHeight), typeof(double), typeof(KeyGridView),
        new PropertyMetadata(0.55d, OnCellStyleChanged));

    /// <summary>音符条方向：true = 从下往上（默认从上往下）。</summary>
    public static readonly DependencyProperty NoteEffectReverseProperty = DependencyProperty.Register(
        nameof(NoteEffectReverse), typeof(bool), typeof(KeyGridView),
        new PropertyMetadata(false, OnCellStyleChanged));

    /// <summary>主界面预览的编辑模式：显示选中框、点击选中格子。</summary>
    public static readonly DependencyProperty IsEditorModeProperty = DependencyProperty.Register(
        nameof(IsEditorMode), typeof(bool), typeof(KeyGridView),
        new PropertyMetadata(false, OnInteractivityChanged));

    /// <summary>当前选中的格子（编辑模式画高亮）；由 ViewModel 维护。</summary>
    public static readonly DependencyProperty SelectedCellProperty = DependencyProperty.Register(
        nameof(SelectedCell), typeof(KpsKeyCell), typeof(KeyGridView),
        new PropertyMetadata(null, OnInteractivityChanged));

    /// <summary>点格子的命令（参数 = <see cref="KpsKeyCell"/>）；主界面用它选中要编辑的键。</summary>
    public static readonly DependencyProperty CellClickCommandProperty = DependencyProperty.Register(
        nameof(CellClickCommand), typeof(ICommand), typeof(KeyGridView),
        new PropertyMetadata(null));

    public KeyGridView()
    {
        InitializeComponent();
        Loaded += OnControlLoaded;
        Unloaded += OnControlUnloaded;
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

    // ============================== DP 变化回调 ==============================

    private static void OnLayoutRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KeyGridView)d).Rebuild();

    private static void OnLayoutMetricsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KeyGridView)d).Rebuild();

    private static void OnCellStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KeyGridView)d).RefreshPresenters();

    private static void OnInteractivityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KeyGridView)d).RefreshInteractivity();

    // ============================== 生命周期 / 重建 ==============================

    private void OnControlLoaded(object sender, RoutedEventArgs e) => Rebuild();

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        // 被切走 / 关闭时摘掉对格子的事件订阅，避免 ViewModel 里的格子把已关闭的视图拽住
        DetachCollections();
        DetachPresenters();
    }

    /// <summary>按当前 <see cref="LayoutRows"/> 重建整个网格（只在布局类属性 / 行集合变化时调用）。</summary>
    private void Rebuild()
    {
        DetachCollections();
        DetachPresenters();
        RowsHost.Children.Clear();

        if (LayoutRows is not { } rows) return;

        var rowIndex = 0;
        foreach (var item in rows)
        {
            if (item is not KpsRowView row) continue;

            // 行内格子集合如果本身是可观察集合，也在变化时重建（List 原地改不会通知，靠绑定属性 raise）
            if (row.Cells is INotifyCollectionChanged cellsChanged) AttachCollection(cellsChanged);

            var gap = SafeClamp(Gap, 0, 200, 6);
            var rowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, rowIndex == 0 ? 0 : gap, 0, 0),
            };

            var columnIndex = 0;
            foreach (var cell in row.Cells)
            {
                var presenter = new KeyCellPresenter(this, cell, columnIndex == 0 ? 0 : gap);
                _presenters.Add(presenter);
                rowPanel.Children.Add(presenter);
                columnIndex++;
            }

            RowsHost.Children.Add(rowPanel);
            rowIndex++;
        }

        if (LayoutRows is INotifyCollectionChanged observed) AttachCollection(observed);
    }

    /// <summary>样式类属性变化：不重建视觉树，只把新样式刷到现有格子（避免打断正在播的按下动画）。</summary>
    private void RefreshPresenters()
    {
        foreach (var presenter in _presenters) presenter.UpdateFromOwner();
    }

    /// <summary>编辑模式 / 选中格子变化：刷新光标与选中描边。</summary>
    private void RefreshInteractivity()
    {
        foreach (var presenter in _presenters)
        {
            presenter.UpdateSelection();
            presenter.UpdateInteractivity();
        }
    }

    /// <summary>配置可能被手改坏（NaN / 无穷）：夹到合法范围并给兜底值，绝不让布局抛异常。</summary>
    private static double SafeClamp(double value, double min, double max, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Clamp(value, min, max);

    // ============================== 集合订阅 ==============================

    private void AttachCollection(INotifyCollectionChanged collection)
    {
        if (_observedCollections.Contains(collection)) return;
        collection.CollectionChanged += OnObservedCollectionChanged;
        _observedCollections.Add(collection);
    }

    private void DetachCollections()
    {
        foreach (var collection in _observedCollections) collection.CollectionChanged -= OnObservedCollectionChanged;
        _observedCollections.Clear();
    }

    private void OnObservedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void DetachPresenters()
    {
        foreach (var presenter in _presenters) presenter.Detach();
        _presenters.Clear();
    }

    /// <summary>点格子时的编辑行为（只有编辑模式才接管鼠标；Overlay 锁定靠窗口级 WS_EX_TRANSPARENT）。</summary>
    internal void HandleCellClick(KpsKeyCell cell)
    {
        if (!IsEditorMode) return;
        var command = CellClickCommand;
        if (command is not null)
        {
            if (command.CanExecute(cell)) command.Execute(cell);
            return;
        }
        // 没接命令时兜底：至少把选中格子设上，主界面预览能看到高亮
        SelectedCell = cell;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>外发光效果（按颜色缓存 + 冻结；没有按下格子时不会有人拿它，性能开销只在按下的键上）。</summary>
    private static DropShadowEffect GetGlowEffect(Brush? pressedBrush)
    {
        var color = pressedBrush is SolidColorBrush solid ? solid.Color : Color.FromRgb(0xFF, 0xC8, 0x57);
        if (GlowCache.TryGetValue(color, out var cached)) return cached;

        var effect = new DropShadowEffect
        {
            Color = color,
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0.85,
            RenderingBias = RenderingBias.Performance,
        };
        effect.Freeze();
        GlowCache[color] = effect;
        return effect;
    }

    // ============================== 单个格子 ==============================

    /// <summary>
    /// 一个按键格子的视觉与动画：
    /// <list type="bullet">
    ///   <item>背景 / 边框 / 文字颜色从 <see cref="KpsKeyCell"/> 的 Display* 属性来；</item>
    ///   <item>按下：ScaleTransform 缩放（<see cref="PressedScale"/>）、可选外发光 / 白光闪光；</item>
    ///   <item>音符效果：格子内部一条圆角音符条按 <see cref="NoteEffectDurationMs"/> 循环滑过（<see cref="NoteEffectReverse"/> 反向）；</item>
    ///   <item>编辑模式：高亮描边 + 点击回调 <see cref="HandleCellClick"/>。</item>
    /// </list>
    /// </summary>
    private sealed class KeyCellPresenter : Border
    {
        private readonly KeyGridView _owner;
        private readonly KpsKeyCell _cell;
        private readonly Grid _content;
        private readonly Image _image;
        private readonly Border _labelBackdrop;
        private readonly TextBlock _labelText;
        private readonly TextBlock _counterText;
        private readonly Border _flash;
        private readonly Border _noteBar;
        private readonly TranslateTransform _noteTransform;
        private readonly ScaleTransform _scale;

        public KeyCellPresenter(KeyGridView owner, KpsKeyCell cell, double leftMargin)
        {
            _owner = owner;
            _cell = cell;

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Background = cell.DisplayBackground;
            BorderBrush = cell.BorderBrush;
            BorderThickness = new Thickness(1);
            Margin = new Thickness(leftMargin, 0, 0, 0);

            _scale = new ScaleTransform(1, 1);
            RenderTransform = _scale;
            RenderTransformOrigin = new Point(0.5, 0.5);

            _image = new Image
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(3),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

            _labelText = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            _labelBackdrop = new Border
            {
                Child = _labelText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
            };
            _counterText = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };

            _flash = new Border
            {
                Background = FlashBrush,
                Opacity = 0,
                IsHitTestVisible = false,
            };

            _noteTransform = new TranslateTransform();
            _noteBar = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(5, 0, 5, 0),
                Background = DefaultNoteBrush,
                CornerRadius = new CornerRadius(4),
                Opacity = 0.7,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                RenderTransform = _noteTransform,
            };

            // 图层顺序（下 → 上）：图片、闪光、音符条、标签、计数器
            _content = new Grid { ClipToBounds = true, IsHitTestVisible = false };
            _content.Children.Add(_image);
            _content.Children.Add(_flash);
            _content.Children.Add(_noteBar);
            _content.Children.Add(_labelBackdrop);
            _content.Children.Add(_counterText);
            Child = _content;

            cell.PropertyChanged += OnCellPropertyChanged;
            MouseLeftButtonDown += OnMouseLeftButtonDown;

            UpdateFromOwner();
        }

        /// <summary>从控件的依赖属性 / 格子状态整体刷新（重建时和样式类 DP 变化时调用）。</summary>
        public void UpdateFromOwner()
        {
            var cellWidth = SafeClamp(_owner.CellWidth, 1, 2000, 66);
            var cellHeight = SafeClamp(_owner.CellHeight, 1, 2000, 74);
            var width = cellWidth * Math.Max(1, _cell.WidthUnits);
            var height = cellHeight;
            Width = width;
            Height = height;

            var radius = SafeClamp(_owner.CornerRadius, 0, Math.Min(width, height) / 2, 10);
            CornerRadius = new CornerRadius(radius);
            _flash.CornerRadius = new CornerRadius(radius);

            _labelText.MaxWidth = Math.Max(10, width - 10);
            _labelText.FontSize = SafeClamp(_owner.KeyFontSize, 1, 400, 22);
            _counterText.FontSize = SafeClamp(_owner.CounterFontSize, 1, 200, 11);
            _counterText.MaxWidth = Math.Max(10, width - 8);

            _noteBar.Opacity = SafeClamp(_owner.NoteEffectOpacity, 0, 1, 0.7);

            UpdateContent();
            UpdateSelection();
            UpdateInteractivity();

            // 格子当前正被按住：重建 / 样式刷新后把按下视觉重新贴上去（不播动画，避免闪一下）
            ApplyPressedVisual(animate: false);
        }

        /// <summary>解绑格子事件、停掉动画（重建 / 控件卸载时调用）。</summary>
        public void Detach()
        {
            _cell.PropertyChanged -= OnCellPropertyChanged;
            MouseLeftButtonDown -= OnMouseLeftButtonDown;
            StopNoteEffect();
            Effect = null;
        }

        /// <summary>刷新选中描边（编辑模式 && （SelectedCell 是它 || 它自己的 IsSelected））。</summary>
        public void UpdateSelection()
        {
            var selected = _owner.IsEditorMode
                           && (ReferenceEquals(_owner.SelectedCell, _cell) || _cell.IsSelected);
            BorderThickness = new Thickness(selected ? 2 : 1);
            BorderBrush = selected ? SelectedBorderBrush : _cell.BorderBrush;
        }

        /// <summary>编辑模式下鼠标手型；非编辑模式清掉光标值（继承上层，Overlay 拖动时是 SizeAll）。</summary>
        public void UpdateInteractivity()
        {
            if (_owner.IsEditorMode) Cursor = Cursors.Hand;
            else ClearValue(CursorProperty);
        }

        // ---------- 内容 / 颜色 ----------

        private void UpdateContent()
        {
            Background = _cell.DisplayBackground;
            _labelText.Foreground = _cell.DisplayForeground;
            _counterText.Foreground = _cell.CounterBrush;

            _image.Source = _cell.Image;
            var hasImage = _cell.HasImage && _cell.Image is not null;
            _image.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;

            var hasLabel = !string.IsNullOrEmpty(_cell.Label);
            _labelText.Text = hasLabel ? _cell.Label : "—";
            // 有图片时：标签为空 / 只有占位符「—」就让图片当主角，别再叠一个没意义的符号
            var showLabel = hasLabel && !(hasImage && _cell.Label == "—");
            _labelBackdrop.Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed;
            _labelBackdrop.Background = hasImage ? ImageLabelBackdropBrush : Brushes.Transparent;
            _labelBackdrop.Padding = hasImage ? new Thickness(3, 1, 3, 1) : new Thickness(0);

            UpdateCounter();
            UpdateSelection();
        }

        private void UpdateCounter()
        {
            var text = _cell.CountText;
            // 计数 > 0 时兜底显示：KpsKeyCell.ShowCounter 由 ApplyStyle 按「当时的 Count」算，
            // 计数是之后才涨上来的（没人会为每个键再 ApplyStyle 一次），只认 ShowCounter 会永远不显示计数器
            var visible = _owner.ShowCounters
                          && (_cell.ShowCounter || _cell.Count > 0)
                          && !string.IsNullOrEmpty(text);
            _counterText.Text = text;
            _counterText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- 按下视觉 / 动画 ----------

        private void ApplyPressedVisual(bool animate)
        {
            var pressed = _cell.IsPressed;

            Background = _cell.DisplayBackground;
            _labelText.Foreground = _cell.DisplayForeground;

            // 外发光：只给按下的格子挂 Effect（冻结的共享 Effect，不 new）
            Effect = pressed && _owner.GlowEnabled ? GetGlowEffect(_cell.PressedBrush) : null;

            // 缩放
            var target = pressed ? PressedScaleTarget() : 1.0;
            var duration = TimeSpan.FromMilliseconds(SafeClamp(_owner.EffectDurationMs, 1, 5000, 110));
            if (animate)
            {
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, duration) { EasingFunction = ease });
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, duration) { EasingFunction = ease });
            }
            else
            {
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _scale.ScaleX = target;
                _scale.ScaleY = target;
            }

            if (pressed && _owner.FlashEnabled) PlayFlash();
            UpdateNoteEffect(pressed);
        }

        /// <summary>按下缩放目标值：PressedScale 合法（&lt;1）才缩，否则 1。</summary>
        private double PressedScaleTarget()
        {
            var scale = _owner.PressedScale;
            if (double.IsNaN(scale) || scale >= 1 || scale < 0.2) return 1;
            return scale;
        }

        private void PlayFlash()
        {
            var ms = SafeClamp(_owner.EffectDurationMs, 40, 5000, 110);
            var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.3)));
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
            _flash.BeginAnimation(OpacityProperty, frames, HandoffBehavior.SnapshotAndReplace);
        }

        // ---------- 音符效果 ----------

        private void UpdateNoteEffect(bool pressed)
        {
            if (pressed && _owner.NoteEffectEnabled) StartNoteEffect();
            else StopNoteEffect();
        }

        private void StartNoteEffect()
        {
            var cellHeight = Height;
            if (double.IsNaN(cellHeight) || cellHeight <= 0) return;

            var barHeight = Math.Max(3, SafeClamp(_owner.NoteEffectHeight, 0.08, 1, 0.55) * cellHeight);
            _noteBar.Height = barHeight;
            _noteBar.CornerRadius = new CornerRadius(barHeight / 2);
            _noteBar.Background = _owner.NoteEffectColor ?? DefaultNoteBrush;
            _noteBar.Opacity = SafeClamp(_owner.NoteEffectOpacity, 0.05, 1, 0.7);

            // 从上往下（默认）：-barHeight → cellHeight；反向：cellHeight → -barHeight
            var from = _owner.NoteEffectReverse ? cellHeight : -barHeight;
            var to = _owner.NoteEffectReverse ? -barHeight : cellHeight;
            var durationMs = SafeClamp(_owner.NoteEffectDurationMs, 80, 10000, 700);

            _noteBar.Visibility = Visibility.Visible;
            _noteTransform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void StopNoteEffect()
        {
            _noteTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _noteBar.Visibility = Visibility.Collapsed;
        }

        // ---------- 事件 ----------

        private void OnCellPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(KpsKeyCell.IsPressed):
                    ApplyPressedVisual(animate: true);
                    break;
                case nameof(KpsKeyCell.DisplayBackground):
                    Background = _cell.DisplayBackground;
                    break;
                case nameof(KpsKeyCell.DisplayForeground):
                    _labelText.Foreground = _cell.DisplayForeground;
                    break;
                case nameof(KpsKeyCell.Label):
                case nameof(KpsKeyCell.Image):
                case nameof(KpsKeyCell.HasImage):
                    UpdateContent();
                    break;
                case nameof(KpsKeyCell.Count):
                case nameof(KpsKeyCell.CountText):
                case nameof(KpsKeyCell.ShowCounter):
                    UpdateCounter();
                    break;
                case nameof(KpsKeyCell.BorderBrush):
                    UpdateSelection();
                    break;
                case nameof(KpsKeyCell.CounterBrush):
                    _counterText.Foreground = _cell.CounterBrush;
                    break;
                case nameof(KpsKeyCell.IsSelected):
                    UpdateSelection();
                    break;
                case nameof(KpsKeyCell.PressedBrush):
                    if (_cell.IsPressed) Effect = _owner.GlowEnabled ? GetGlowEffect(_cell.PressedBrush) : null;
                    break;
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_owner.IsEditorMode) return;   // 非编辑模式不拦鼠标（Overlay 点击穿透靠窗口级透明）
            e.Handled = true;
            _owner.HandleCellClick(_cell);
        }
    }
}
