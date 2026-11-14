using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.Vsrg.Core;
using YuLiangToolkit.Tools.Vsrg.ViewModels;

namespace YuLiangToolkit.Tools.Vsrg.Views;

/// <summary>
/// 游玩时的**自绘轨道画布**：轨道背景 / 判定线 / 下落 note / 面条 / 按键高亮 / 判定弹字，
/// 全部在 <see cref="OnRender"/> 里用 <see cref="DrawingContext"/> 画。
///
/// <para><b>为什么自绘</b>（和俄罗斯方块棋盘、谱面编辑器画布同一个理由）：
/// 一个 note 一个 <c>Border</c> 的话，屏幕上几十个 note 就是几十个 Visual，每帧都要改布局；
/// 自绘只有一个 Visual，绘制量 = 屏幕上那几个矩形，跟谱面总大小无关。</para>
///
/// <para><b>性能要点</b>（改代码时别破坏）：</para>
/// <list type="number">
///   <item>只画「落点时间在可视窗口内」的 note（二分查找起止下标，不遍历全谱）；</item>
///   <item><see cref="Pen"/> / <see cref="Brush"/> 全部缓存并冻结，不在 <c>OnRender</c> 里 new；</item>
///   <item>文字（判定弹字 / 按键字母）用 <see cref="FormattedText"/>，只在需要时造。</item>
/// </list>
///
/// <para><b>颜色</b>：主题色是依赖属性（XAML 用 <c>{DynamicResource}</c> 传 token，换主题立即生效）；
/// 每条轨道的配色是**数据色**（<see cref="LaneColors"/>，深色背景上固定配色最像音游）。</para>
/// </summary>
public sealed class PlayfieldCanvas : FrameworkElement
{
    /// <summary>每条轨道的固定配色（深色背景上都看得清；和 osu!mania 的默认皮肤同色系）。</summary>
    private static readonly Color[] LaneColorValues =
    {
        (Color)ColorConverter.ConvertFromString("#F25F5C"),
        (Color)ColorConverter.ConvertFromString("#FFE066"),
        (Color)ColorConverter.ConvertFromString("#70C1B3"),
        (Color)ColorConverter.ConvertFromString("#5B9CF6"),
        (Color)ColorConverter.ConvertFromString("#B388FF"),
        (Color)ColorConverter.ConvertFromString("#FF9F45"),
        (Color)ColorConverter.ConvertFromString("#4DD0E1"),
        (Color)ColorConverter.ConvertFromString("#F06292"),
        (Color)ColorConverter.ConvertFromString("#AED581"),
        (Color)ColorConverter.ConvertFromString("#CE93D8"),
    };

    /// <summary>判定线在画布高度上的位置（比例：0.85 = 靠近底部）。</summary>
    public const double JudgementLineRatio = 0.85;

    /// <summary>轨道区最宽占画布宽度的比例（键数少时别把 note 拉得跟砖头一样）。</summary>
    private const double MaxPlayfieldWidthRatio = 0.62;

    /// <summary>单条轨道最宽（像素）。</summary>
    private const double MaxLaneWidth = 132;

    /// <summary>单条轨道最窄（像素）。</summary>
    private const double MinLaneWidth = 26;

    private readonly Dictionary<string, Pen> _pens = new();
    private readonly Dictionary<string, Brush> _brushes = new();
    private readonly Dictionary<string, FormattedText> _texts = new();

    // ---------------------------------------------------------------- 依赖属性

    /// <summary>游玩会话（引擎；界面每帧更新它，变化即重绘）。</summary>
    public static readonly DependencyProperty EngineProperty = DependencyProperty.Register(
        nameof(Engine), typeof(GameplayEngine), typeof(PlayfieldCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            static (d, _) => ((PlayfieldCanvas)d).ClearCache()));

    /// <summary>当前播放位置（毫秒；每帧变，触发重绘）。</summary>
    public static readonly DependencyProperty PositionMsProperty = DependencyProperty.Register(
        nameof(PositionMs), typeof(double), typeof(PlayfieldCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>note 从屏幕顶落到判定线的时间（毫秒）。</summary>
    public static readonly DependencyProperty ApproachMsProperty = DependencyProperty.Register(
        nameof(ApproachMs), typeof(double), typeof(PlayfieldCanvas),
        new FrameworkPropertyMetadata(900.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>判定弹字（判完一帧就变）。</summary>
    public static readonly DependencyProperty JudgementTextProperty = DependencyProperty.Register(
        nameof(JudgementText), typeof(string), typeof(PlayfieldCanvas),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>判定线下面的按键字母（空格分隔，例如 <c>A S D F</c>）。</summary>
    public static readonly DependencyProperty KeyLabelsProperty = DependencyProperty.Register(
        nameof(KeyLabels), typeof(string), typeof(PlayfieldCanvas),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender,
            static (d, _) => ((PlayfieldCanvas)d).ClearCache()));

    /// <summary>轨道背景色。</summary>
    public static readonly DependencyProperty LaneBackgroundBrushProperty = RegisterBrush(nameof(LaneBackgroundBrush));

    /// <summary>轨道分隔线色。</summary>
    public static readonly DependencyProperty LaneLineBrushProperty = RegisterBrush(nameof(LaneLineBrush));

    /// <summary>判定线颜色。</summary>
    public static readonly DependencyProperty JudgementLineBrushProperty = RegisterBrush(nameof(JudgementLineBrush));

    /// <summary>判定文字颜色。</summary>
    public static readonly DependencyProperty JudgementTextBrushProperty = RegisterBrush(nameof(JudgementTextBrush));

    /// <summary>按键字母颜色。</summary>
    public static readonly DependencyProperty KeyLabelBrushProperty = RegisterBrush(nameof(KeyLabelBrush));

    private static DependencyProperty RegisterBrush(string name)
        => DependencyProperty.Register(name, typeof(Brush), typeof(PlayfieldCanvas),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                static (d, _) => ((PlayfieldCanvas)d).ClearCache()));

    /// <summary>玩会话。</summary>
    public GameplayEngine? Engine
    {
        get => (GameplayEngine?)GetValue(EngineProperty);
        set => SetValue(EngineProperty, value);
    }

    /// <summary>当前时间（毫秒）。</summary>
    public double PositionMs
    {
        get => (double)GetValue(PositionMsProperty);
        set => SetValue(PositionMsProperty, value);
    }

    /// <summary>note 从屏幕顶到判定线的时间（毫秒）。</summary>
    public double ApproachMs
    {
        get => (double)GetValue(ApproachMsProperty);
        set => SetValue(ApproachMsProperty, value);
    }

    /// <summary>判定弹字。</summary>
    public string JudgementText
    {
        get => (string)GetValue(JudgementTextProperty);
        set => SetValue(JudgementTextProperty, value);
    }

    /// <summary>判定线下面的按键字母（空格分隔）。</summary>
    public string KeyLabels
    {
        get => (string)GetValue(KeyLabelsProperty);
        set => SetValue(KeyLabelsProperty, value);
    }

    /// <summary>轨道背景色。</summary>
    public Brush? LaneBackgroundBrush
    {
        get => (Brush?)GetValue(LaneBackgroundBrushProperty);
        set => SetValue(LaneBackgroundBrushProperty, value);
    }

    /// <summary>轨道分隔线色。</summary>
    public Brush? LaneLineBrush
    {
        get => (Brush?)GetValue(LaneLineBrushProperty);
        set => SetValue(LaneLineBrushProperty, value);
    }

    /// <summary>判定线颜色。</summary>
    public Brush? JudgementLineBrush
    {
        get => (Brush?)GetValue(JudgementLineBrushProperty);
        set => SetValue(JudgementLineBrushProperty, value);
    }

    /// <summary>判定文字颜色。</summary>
    public Brush? JudgementTextBrush
    {
        get => (Brush?)GetValue(JudgementTextBrushProperty);
        set => SetValue(JudgementTextBrushProperty, value);
    }

    /// <summary>按键字母颜色。</summary>
    public Brush? KeyLabelBrush
    {
        get => (Brush?)GetValue(KeyLabelBrushProperty);
        set => SetValue(KeyLabelBrushProperty, value);
    }

    /// <summary>构造：裁到边界内 + 像素对齐。</summary>
    public PlayfieldCanvas()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;    // 游玩区的键盘由页面统一收，画布不吃鼠标
    }

    // ---------------------------------------------------------------- 绘制

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        var engine = Engine;
        var laneCount = engine?.LaneCount ?? 4;

        // 轨道区：居中、限宽、留边
        var maxWidth = Math.Min(width * MaxPlayfieldWidthRatio, laneCount * MaxLaneWidth);
        var laneWidth = Math.Max(MinLaneWidth, maxWidth / Math.Max(1, laneCount));
        var playfieldWidth = laneWidth * laneCount;
        var left = (width - playfieldWidth) / 2;
        var lineY = height * JudgementLineRatio;

        DrawLanes(dc, engine, left, laneWidth, laneCount, height, lineY);
        DrawNotes(dc, engine, left, laneWidth, lineY, height, width);
        DrawJudgementLine(dc, left, playfieldWidth, lineY);
        DrawKeyLabels(dc, left, laneWidth, laneCount, height);
        DrawJudgementText(dc, lineY, playfieldWidth);
    }

    /// <summary>画轨道底色 + 分隔线 + 按下的高亮。</summary>
    private void DrawLanes(DrawingContext dc, GameplayEngine? engine, double left, double laneWidth,
        int laneCount, double height, double lineY)
    {
        var background = LaneBackgroundBrush;
        var lineBrush = LaneLineBrush;
        var linePen = lineBrush is null ? null : GetPen(lineBrush, 1);

        for (var lane = 0; lane < laneCount; lane++)
        {
            var x = left + lane * laneWidth;
            var rect = new Rect(x, 0, laneWidth, height);

            if (background is not null) dc.DrawRectangle(background, null, rect);

            // 按下的高亮：判定线附近一块半透明的轨道色
            if (engine is not null && engine.IsLaneHeld(lane))
            {
                var laneBrush = GetBrush(LaneColor(lane), 0.30);
                dc.DrawRectangle(laneBrush, null, new Rect(x, lineY - height * 0.55, laneWidth, height * 0.55));
            }

            if (linePen is not null && lane > 0) dc.DrawLine(linePen, new Point(x, 0), new Point(x, height));
        }

        if (linePen is not null)
        {
            dc.DrawLine(linePen, new Point(left, 0), new Point(left, height));
            dc.DrawLine(linePen, new Point(left + laneWidth * laneCount, 0),
                new Point(left + laneWidth * laneCount, height));
        }
    }

    /// <summary>画落在可视窗口内的 note / 面条。</summary>
    private void DrawNotes(DrawingContext dc, GameplayEngine? engine, double left, double laneWidth,
        double lineY, double height, double width)
    {
        if (engine is null) return;

        var chart = engine.Chart;
        var notes = chart.Notes;
        if (notes.Count == 0) return;

        var approach = Math.Max(1, ApproachMs);
        var now = PositionMs;

        // 可视窗口：判定线以下留一点（判完的 note 还要往上飞一小段），上面最多 ApproachMs
        var minTime = now - approach * 0.12;
        var maxTime = now + approach * 1.02;

        var noteHeight = Math.Clamp(laneWidth * 0.22, 6, 18);
        var bodyWidth = Math.Max(3, laneWidth * 0.34);
        var headWidth = Math.Max(6, laneWidth * 0.82);

        var start = LowerBound(notes, minTime);
        for (var i = start; i < notes.Count; i++)
        {
            var note = notes[i];
            if (note.StartMs > maxTime) break;

            var headY = lineY - (note.StartMs - now) / approach * (lineY + noteHeight);
            if (headY < -noteHeight * 4) continue;

            var lane = Math.Clamp(note.Column, 0, engine.LaneCount - 1);
            var laneLeft = left + lane * laneWidth;
            var laneCenter = laneLeft + laneWidth / 2;

            if (engine.IsHeadResolved(i)) continue;     // 判过的普通键不再画（面条的尾巴还要画）

            var color = LaneColor(lane);
            var fill = GetBrush(color, 1);

            if (note.IsLongNote)
            {
                // 尾巴的落点：正在保持的面条身体从「尾巴位置」画到当前头部位置
                var tailY = lineY - (note.EndMs - now) / approach * (lineY + noteHeight);
                var bodyTop = Math.Min(tailY, headY);
                var bodyBottom = headY;

                if (engine.IsHoldingLongNote(i))
                {
                    // 按住中：身体只画到判定线（按下的部分已经吃掉了）
                    bodyBottom = Math.Min(headY, lineY);
                    bodyTop = Math.Min(tailY, bodyBottom);
                }

                if (bodyBottom > bodyTop)
                {
                    var bodyBrush = GetBrush(color, 0.55);
                    dc.DrawRectangle(bodyBrush, null, new Rect(
                        laneCenter - bodyWidth / 2, bodyTop, bodyWidth, bodyBottom - bodyTop));
                }

                if (tailY > 0 && tailY < height) DrawNoteHead(dc, fill, laneCenter, tailY, headWidth, noteHeight, 0.75);
            }

            if (headY > -noteHeight && headY < height + noteHeight)
            {
                DrawNoteHead(dc, fill, laneCenter, headY, headWidth, noteHeight, 1);
            }
        }
    }

    /// <summary>画一个 note 的头（圆角矩形 + 一点高光）。</summary>
    private static void DrawNoteHead(DrawingContext dc, Brush brush, double centerX, double centerY,
        double noteWidth, double noteHeight, double opacity)
    {
        if (opacity < 1 && brush is SolidColorBrush solid)
        {
            var faded = new SolidColorBrush(solid.Color) { Opacity = opacity };
            faded.Freeze();
            brush = faded;
        }

        var rect = new Rect(centerX - noteWidth / 2, centerY - noteHeight / 2, noteWidth, noteHeight);
        dc.DrawRoundedRectangle(brush, null, rect, noteHeight * 0.3, noteHeight * 0.3);
    }

    /// <summary>画判定线。</summary>
    private void DrawJudgementLine(DrawingContext dc, double left, double playfieldWidth, double lineY)
    {
        var brush = JudgementLineBrush;
        if (brush is null) return;

        var pen = GetPen(brush, 2);
        dc.DrawLine(pen, new Point(left, lineY), new Point(left + playfieldWidth, lineY));
    }

    /// <summary>画判定线下面的按键字母（哪条轨按哪个键）。</summary>
    private void DrawKeyLabels(DrawingContext dc, double left, double laneWidth,
        int laneCount, double height)
    {
        var brush = KeyLabelBrush;
        if (brush is null) return;

        var labels = (KeyLabels ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0) return;

        var fontSize = Math.Clamp(laneWidth * 0.24, 10, 20);
        var y = Math.Min(height - fontSize * 1.6, height * JudgementLineRatio + fontSize * 1.4);

        for (var lane = 0; lane < laneCount && lane < labels.Length; lane++)
        {
            var text = GetText($"k:{labels[lane]}:{fontSize:0}", labels[lane], brush, fontSize);
            dc.DrawText(text, new Point(left + lane * laneWidth + (laneWidth - text.Width) / 2, y));
        }
    }

    /// <summary>画判定弹字（判定线下面一点，居中）。</summary>
    private void DrawJudgementText(DrawingContext dc, double lineY, double playfieldWidth)
    {
        var text = JudgementText;
        if (string.IsNullOrEmpty(text)) return;

        var brush = JudgementTextBrush;
        if (brush is null) return;

        var formatted = GetText("judge:" + text, text, brush, 26);
        dc.DrawText(formatted, new Point(
            (ActualWidth - formatted.Width) / 2,
            Math.Min(ActualHeight - formatted.Height - 4, lineY + 12)));
    }

    // ---------------------------------------------------------------- 缓存

    private Color LaneColor(int lane) => LaneColorValues[Math.Abs(lane) % LaneColorValues.Length];

    private Brush GetBrush(Color color, double opacity)
    {
        var key = $"{color}|{opacity:0.##}";
        if (_brushes.TryGetValue(key, out var cached)) return cached;

        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }

    private Pen GetPen(Brush brush, double thickness)
    {
        var key = $"{brush.GetHashCode()}|{thickness:0.##}";
        if (_pens.TryGetValue(key, out var cached)) return cached;

        var pen = new Pen(brush, thickness);
        pen.Freeze();
        _pens[key] = pen;
        return pen;
    }

    private FormattedText GetText(string key, string text, Brush brush, double size)
    {
        if (_texts.TryGetValue(key, out var cached)) return cached;

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        _texts[key] = formatted;
        return formatted;
    }

    /// <summary>主题画刷变了：清掉派生缓存（Pen / 文字）。</summary>
    private void ClearCache()
    {
        _pens.Clear();
        _texts.Clear();
    }

    /// <summary>第一个 <c>StartMs &gt;= time</c> 的下标（二分）。</summary>
    private static int LowerBound(IReadOnlyList<ChartNote> notes, double time)
    {
        var low = 0;
        var high = notes.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (notes[mid].StartMs < time) low = mid + 1;
            else high = mid;
        }
        return low;
    }
}
