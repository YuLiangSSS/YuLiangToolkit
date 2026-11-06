using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.PptViewer.Parsing;

namespace YuLiangToolkit.Tools.PptViewer.Rendering;

/// <summary>
/// 幻灯片渲染器：把 <see cref="Parsing.PptSlide"/> 的纯数据模型画成 WPF 的
/// <see cref="DrawingGroup"/>（矢量，可冻结、可跨线程复用、可直接塞进 <see cref="DrawingImage"/> 显示）。
///
/// <para><b>为什么用 DrawingGroup 而不是控件</b>：同一份绘制结果既能当主视图（按缩放放大），
/// 又能当左侧缩略图（缩到很小）；矢量在任意缩放下都清楚，比给每页生成位图省内存，也方便缓存。</para>
///
/// <para><b>线程</b>：方法本身可以在后台线程调用（结果 <c>Freeze()</c> 后即可跨线程），
/// 但内部解码图片（<c>BitmapImage</c>）也都会 Freeze；界面侧统一在 UI 线程调用最省心。</para>
///
/// <para>渲染约定见 <c>Parsing/PptModel.cs</c> 顶部：坐标已是幻灯片 DIP，颜色 / 字体已解析好。
/// 本渲染器只负责「把值画出来」：不查主题、不改模型、不创建窗口 / 控件。</para>
/// </summary>
public static class PptSlideRenderer
{
    /// <summary>图片解码缓存上限（同一批图片字节只解码一次；超过就整体清空重来）。</summary>
    private const int MaxDecodedImages = 200;

    /// <summary>表格格线颜色（浅灰，属于幻灯片内容，不跟界面主题走）。</summary>
    private static readonly Color TableGridColor = Color.FromRgb(0xB8, 0xBC, 0xC4);

    /// <summary>占位框（不支持形状 / 图片缺失）的浅灰虚线颜色。</summary>
    private static readonly Color PlaceholderColor = Color.FromRgb(0xB0, 0xB4, 0xBB);

    /// <summary>占位框里小字的灰色。</summary>
    private static readonly Color PlaceholderTextColor = Color.FromRgb(0x7A, 0x80, 0x88);

    /// <summary>没边框的直线形状兜底画笔颜色（解析器没解析出 a:ln 时也要看得见连接线）。</summary>
    private static readonly Color DefaultLineColor = Color.FromRgb(0x40, 0x40, 0x40);

    // 共享冻结的画刷 / 画笔（Freezable 冻结后可以跨绘制复用，省对象分配）
    private static readonly Brush WhiteBrush = CreateFrozenBrush(Colors.White);
    private static readonly Brush PlaceholderBorderBrush = CreateFrozenBrush(PlaceholderColor);
    private static readonly Pen TableGridPen = CreateFrozenPen(TableGridColor, 1, null);
    private static readonly Pen PlaceholderDashedPen = CreateFrozenPen(PlaceholderColor, 1.2, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly Pen DefaultLinePen = CreateFrozenPen(DefaultLineColor, 1, null);

    private static readonly Dictionary<string, DashStyle> DashStyleCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解码后的图片缓存：键是模型里的字节数组（引用相等即可，解析器同一次解析复用同一个数组）。</summary>
    private static readonly object ImageCacheLock = new();
    private static readonly Dictionary<byte[], BitmapImage> DecodedImageCache =
        new(ByteArrayReferenceComparer.Instance);

    // ---------------------------------------------------------------- 公共入口

    /// <summary>
    /// 画一张幻灯片，返回幻灯片坐标系（0,0 ~ WidthDip,HeightDip）的绘制结果。
    /// 调用方负责按目标大小缩放显示（<see cref="DrawingImage"/> + <c>Stretch</c>）。
    /// </summary>
    public static DrawingGroup Render(PptPresentation presentation, PptSlide slide)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(slide);

        var width = presentation.WidthDip > 1 ? presentation.WidthDip : PptDefaults.DefaultWidthDip;
        var height = presentation.HeightDip > 1 ? presentation.HeightDip : PptDefaults.DefaultHeightDip;
        var slideRect = new Rect(0, 0, width, height);

        var root = new DrawingGroup();

        // 整页裁剪在 (0,0,W,H) 内：坏文件里形状坐标可能跑到页面外面
        var clip = new RectangleGeometry(slideRect);
        clip.Freeze();
        root.ClipGeometry = clip;

        using (var context = root.Open())
        {
            DrawBackground(context, presentation, slide, slideRect);

            if (slide.Shapes is not null)
            {
                foreach (var shape in slide.Shapes)
                {
                    DrawShape(context, presentation, shape);
                }
            }
        }

        root.Freeze();
        return root;
    }

    /// <summary>画一张幻灯片并包成可直接绑定的 <see cref="DrawingImage"/>（已冻结）。</summary>
    public static DrawingImage CreateImage(PptPresentation presentation, PptSlide slide)
    {
        var image = new DrawingImage(Render(presentation, slide));
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 清空图片解码缓存（关闭工具 / 换文档时由 ViewModel 调用）：静态缓存会跨文档保留
    /// 最近解码过的图片字节，主动清掉可以保证不再看这份演示文稿时不占内存。
    /// <para>只清「字节数组 → BitmapImage」的缓存；正在显示 / 仍被画刷引用的 BitmapImage
    /// 由 WPF 自己按引用计数管理，不会被提前释放。</para>
    /// </summary>
    public static void ClearImageCache()
    {
        lock (ImageCacheLock) DecodedImageCache.Clear();
    }

    // ---------------------------------------------------------------- 背景

    /// <summary>画幻灯片背景：solid / gradient / picture；没有背景或坏数据时用白底。</summary>
    private static void DrawBackground(
        DrawingContext context,
        PptPresentation presentation,
        PptSlide slide,
        Rect slideRect)
    {
        var brush = CreateFillBrush(slide.Background, slideRect) ?? WhiteBrush;
        context.DrawRectangle(brush, null, slideRect);
    }

    // ---------------------------------------------------------------- 形状分发 / 变换

    /// <summary>
    /// 画一个形状：先把形状内容画进独立 <see cref="DrawingGroup"/>，再按它自己的旋转 / 翻转
    /// 绕自身中心套变换，最后 <c>DrawDrawing</c> 到父级。组合里的子形状也走这里，递归处理。
    /// </summary>
    private static void DrawShape(DrawingContext parentContext, PptPresentation presentation, PptShape? shape)
    {
        if (shape is null) return;

        // 极端坏数据：宽高都 <= 0 时没有可画的面积（直线形状允许一个方向为 0）
        if (shape.Width <= 0.01 && shape.Height <= 0.01) return;

        var content = new DrawingGroup();
        using (var context = content.Open())
        {
            switch (shape)
            {
                case PptTextShape textShape:
                    DrawTextShape(context, textShape);
                    break;

                case PptGeometryShape geometryShape:
                    DrawGeometryShape(context, geometryShape);
                    break;

                case PptPictureShape pictureShape:
                    DrawPictureShape(context, pictureShape);
                    break;

                case PptTableShape tableShape:
                    DrawTableShape(context, tableShape);
                    break;

                case PptGroupShape groupShape:
                    // 子形状坐标已经是幻灯片坐标，直接按自己的包围盒递归画
                    if (groupShape.Children is not null)
                    {
                        foreach (var child in groupShape.Children) DrawShape(context, presentation, child);
                    }

                    break;

                case PptUnsupportedShape unsupportedShape:
                    DrawUnsupportedShape(context, unsupportedShape);
                    break;
            }
        }

        ApplyShapeTransform(content, shape);
        content.Freeze();
        parentContext.DrawDrawing(content);
    }

    /// <summary>给形状内容套上翻转 / 旋转（都绕形状自己的中心）。</summary>
    private static void ApplyShapeTransform(DrawingGroup content, PptShape shape)
    {
        var hasFlip = shape.FlipH || shape.FlipV;
        var hasRotation = Math.Abs(shape.RotationDegrees % 360) > 0.01;
        if (!hasFlip && !hasRotation) return;

        var centerX = shape.X + shape.Width / 2;
        var centerY = shape.Y + shape.Height / 2;

        var group = new TransformGroup();
        if (hasFlip)
        {
            // 先绕中心翻转，再旋转（PPT 的 xfrm 语义：flip 之后 rot）
            group.Children.Add(new ScaleTransform(
                shape.FlipH ? -1 : 1,
                shape.FlipV ? -1 : 1,
                centerX,
                centerY));
        }

        if (hasRotation) group.Children.Add(new RotateTransform(shape.RotationDegrees, centerX, centerY));

        group.Freeze();
        content.Transform = group;
    }

    // ---------------------------------------------------------------- 文本框

    /// <summary>文字形状：先画填充 / 边框，再做富文本排版（run 级字体 / 颜色 / 上下标 / 项目符号）。</summary>
    private static void DrawTextShape(DrawingContext context, PptTextShape shape)
    {
        var rect = SafeRect(shape);
        if (rect.Width <= 0 && rect.Height <= 0) return;

        var fill = CreateFillBrush(shape.Fill, rect);
        var pen = CreatePen(shape.Line);
        if (fill is not null || pen is not null) context.DrawRectangle(fill, pen, rect);

        PptTextLayout.DrawBody(
            context,
            shape.Text,
            rect,
            shape.InsetLeftDip,
            shape.InsetTopDip,
            shape.InsetRightDip,
            shape.InsetBottomDip,
            shape.WrapText,
            shape.AutoFit,
            shape.VerticalAnchor);
    }

    // ---------------------------------------------------------------- 自选图形

    /// <summary>常见自选图形：矩形 / 圆角矩形 / 椭圆 / 直线 / 箭头 / 多边形；其余按矩形画。</summary>
    private static void DrawGeometryShape(DrawingContext context, PptGeometryShape shape)
    {
        var rect = SafeRect(shape);
        if (rect.Width <= 0 && rect.Height <= 0) return;

        var fill = CreateFillBrush(shape.Fill, rect);
        var pen = CreatePen(shape.Line);

        // 直线 / 箭头就算没有 a:ln，也要给一支默认画笔，否则整条线就「消失」了
        var lineBrush = fill ?? CreateFrozenBrush(shape.Line?.Color ?? DefaultLineColor);
        var linePen = pen ?? (shape.Kind is PptGeometryKind.Line or PptGeometryKind.Arrow
            ? CreateFrozenPen(shape.Line?.Color ?? DefaultLineColor, 1, null)
            : null);

        switch (shape.Kind)
        {
            case PptGeometryKind.Rectangle:
            case PptGeometryKind.Unknown:
                context.DrawRectangle(fill, pen, rect);
                break;

            case PptGeometryKind.RoundedRectangle:
            {
                // PowerPoint 默认圆角半径约为短边的 1/6
                var radius = Math.Min(rect.Width, rect.Height) / 6.0;
                context.DrawRoundedRectangle(fill, pen, rect, radius, radius);
                break;
            }

            case PptGeometryKind.Ellipse:
                context.DrawEllipse(fill, pen,
                    new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                    Math.Max(0, rect.Width / 2),
                    Math.Max(0, rect.Height / 2));
                break;

            case PptGeometryKind.Line:
            case PptGeometryKind.Arrow:
            {
                var (start, end) = GetLineEndpoints(rect);
                context.DrawLine(linePen, start, end);
                if (shape.Kind == PptGeometryKind.Arrow)
                    DrawArrowHead(context, start, end, lineBrush);
                break;
            }

            default:
            {
                var points = GetPolygonPoints(shape.Kind, rect);
                if (points.Count >= 3)
                {
                    context.DrawGeometry(fill, pen, CreatePolygonGeometry(points));
                }
                else
                {
                    // 兜底：认不出的 Kind 至少画一个矩形，位置不会凭空消失
                    context.DrawRectangle(fill, pen, rect);
                }

                break;
            }
        }
    }

    /// <summary>
    /// 直线形状的两个端点：水平线 / 竖线都落在包围盒中线上，其它情况走对角线
    /// （方向由解析阶段的 FlipH / FlipV 表达，渲染器在外层变换里处理）。
    /// </summary>
    private static (Point Start, Point End) GetLineEndpoints(Rect rect)
    {
        if (rect.Height <= 1) return (new Point(rect.Left, rect.Top + rect.Height / 2), new Point(rect.Right, rect.Top + rect.Height / 2));
        if (rect.Width <= 1) return (new Point(rect.Left + rect.Width / 2, rect.Top), new Point(rect.Left + rect.Width / 2, rect.Bottom));
        return (new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Bottom));
    }

    /// <summary>在直线终点画一个实心箭头（用直线颜色填充）。</summary>
    private static void DrawArrowHead(DrawingContext context, Point start, Point end, Brush brush)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 4) return;

        var ux = dx / length;
        var uy = dy / length;
        var headLength = Math.Clamp(length * 0.22, 6, 18);
        var headWidth = headLength * 0.55;

        // 终点往回退 headLength 作为箭头底边中心，再向两侧各偏 headWidth/2
        var baseX = end.X - ux * headLength;
        var baseY = end.Y - uy * headLength;
        var normalX = -uy;
        var normalY = ux;

        var points = new List<Point>
        {
            end,
            new(baseX + normalX * headWidth / 2, baseY + normalY * headWidth / 2),
            new(baseX - normalX * headWidth / 2, baseY - normalY * headWidth / 2),
        };

        context.DrawGeometry(brush, null, CreatePolygonGeometry(points));
    }

    /// <summary>按几何类型算多边形顶点（三角形 / 菱形 / 平行四边形 / 梯形 / 五边形 / 六边形 / 星形）。</summary>
    private static List<Point> GetPolygonPoints(PptGeometryKind kind, Rect rect)
    {
        var left = rect.Left;
        var top = rect.Top;
        var width = rect.Width;
        var height = rect.Height;
        var right = rect.Right;
        var bottom = rect.Bottom;
        var centerX = left + width / 2;
        var centerY = top + height / 2;

        return kind switch
        {
            PptGeometryKind.Triangle => new List<Point>
            {
                new(centerX, top),
                new(right, bottom),
                new(left, bottom),
            },

            PptGeometryKind.Diamond => new List<Point>
            {
                new(centerX, top),
                new(right, centerY),
                new(centerX, bottom),
                new(left, centerY),
            },

            PptGeometryKind.Parallelogram => new List<Point>
            {
                new(left + width * 0.22, top),
                new(right, top),
                new(right - width * 0.22, bottom),
                new(left, bottom),
            },

            PptGeometryKind.Trapezoid => new List<Point>
            {
                new(left + width * 0.22, top),
                new(right - width * 0.22, top),
                new(right, bottom),
                new(left, bottom),
            },

            PptGeometryKind.Pentagon => GetRegularPolygonPoints(centerX, centerY, width / 2, height / 2, 5),
            PptGeometryKind.Hexagon => new List<Point>
            {
                new(left + width * 0.25, top),
                new(right - width * 0.25, top),
                new(right, centerY),
                new(right - width * 0.25, bottom),
                new(left + width * 0.25, bottom),
                new(left, centerY),
            },

            PptGeometryKind.Star => GetStarPoints(centerX, centerY, width / 2, height / 2, 5),

            _ => new List<Point>(),
        };
    }

    /// <summary>正多边形顶点（顶点朝上，按包围椭圆缩放，4:3 的框里也不会变形）。</summary>
    private static List<Point> GetRegularPolygonPoints(
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        int sides)
    {
        var points = new List<Point>(sides);
        var step = Math.PI * 2 / sides;
        var start = -Math.PI / 2;   // 从正上方开始
        for (var index = 0; index < sides; index++)
        {
            var angle = start + step * index;
            points.Add(new Point(
                centerX + radiusX * Math.Cos(angle),
                centerY + radiusY * Math.Sin(angle)));
        }

        return points;
    }

    /// <summary>五角星顶点：外顶点 / 内顶点交替，内半径取 0.382（五角星比例）。</summary>
    private static List<Point> GetStarPoints(
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        int points)
    {
        var result = new List<Point>(points * 2);
        var innerRatio = 0.382;
        var step = Math.PI / points;
        var start = -Math.PI / 2;
        for (var index = 0; index < points * 2; index++)
        {
            var ratio = index % 2 == 0 ? 1.0 : innerRatio;
            var angle = start + step * index;
            result.Add(new Point(
                centerX + radiusX * ratio * Math.Cos(angle),
                centerY + radiusY * ratio * Math.Sin(angle)));
        }

        return result;
    }

    /// <summary>把顶点列表闭合成冻结的 <see cref="StreamGeometry"/>。</summary>
    private static StreamGeometry CreatePolygonGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: true);
            if (points.Count > 1) context.PolyLineTo(points.Skip(1).ToArray(), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------- 图片

    /// <summary>图片形状：解码缓存 + srcRect 裁剪；缺字节 / 解码失败画浅灰虚线框 + 「图片缺失」。</summary>
    private static void DrawPictureShape(DrawingContext context, PptPictureShape shape)
    {
        var rect = SafeRect(shape);
        if (rect.Width <= 0.5 || rect.Height <= 0.5) return;

        var bitmap = DecodeImage(shape.ImageBytes);
        if (bitmap is null)
        {
            DrawMissingPlaceholder(context, rect, "图片缺失");
            return;
        }

        var cropLeft = ClampRatio(shape.CropLeft);
        var cropTop = ClampRatio(shape.CropTop);
        var cropRight = ClampRatio(shape.CropRight);
        var cropBottom = ClampRatio(shape.CropBottom);

        // 没裁剪直接 DrawImage（质量最好，也最少分配）
        if (cropLeft <= 0.0001 && cropTop <= 0.0001 && cropRight <= 0.0001 && cropBottom <= 0.0001)
        {
            context.DrawImage(bitmap, rect);
            return;
        }

        // 有裁剪：用 ImageBrush 的 Viewbox 取原图中间那块，再拉伸填满形状
        var viewWidth = Math.Max(0.001, 1 - cropLeft - cropRight);
        var viewHeight = Math.Max(0.001, 1 - cropTop - cropBottom);

        var brush = new ImageBrush(bitmap)
        {
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect(cropLeft, cropTop, viewWidth, viewHeight),
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        brush.Freeze();

        context.DrawRectangle(brush, null, rect);
    }

    /// <summary>把 0 ~ 1 的裁剪比例夹进合法范围（坏文件里可能给负数或 >1）。</summary>
    private static double ClampRatio(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Clamp(value, 0, 1);

    // ---------------------------------------------------------------- 表格

    /// <summary>
    /// 表格：按 ColumnWidthsDip / RowHeightsDip 定位每个格子，画单元格填充 + 文字 + 浅灰格线。
    /// 合并单元格（ColSpan / RowSpan）按矩形合并，被盖住的格子（IsCovered）跳过。
    /// </summary>
    private static void DrawTableShape(DrawingContext context, PptTableShape shape)
    {
        var rect = SafeRect(shape);
        if (rect.Width <= 0.5 || rect.Height <= 0.5) return;

        var rows = shape.Rows;
        if (rows is null || rows.Count == 0) return;

        var columnCount = shape.ColumnWidthsDip?.Count ?? 0;
        if (columnCount == 0) columnCount = rows.Max(row => row?.Cells?.Count ?? 0);
        if (columnCount == 0) return;

        // 列宽 / 行高：解析器给的是 DIP；总和和包围盒对不上时按比例缩放到包围盒（PPT 表格一般正好等宽）
        var columnWidths = NormalizeSizes(shape.ColumnWidthsDip, columnCount, rect.Width);
        var rowHeights = NormalizeSizes(
            rows.Select(row => row?.HeightDip ?? 0).ToList(),
            rows.Count,
            rect.Height);

        var columnEdges = BuildEdges(rect.X, columnWidths);
        var rowEdges = BuildEdges(rect.Y, rowHeights);
        var rowCount = rows.Count;

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row?.Cells is null) continue;

            for (var columnIndex = 0; columnIndex < row.Cells.Count && columnIndex < columnCount; columnIndex++)
            {
                var cell = row.Cells[columnIndex];
                if (cell is null || cell.IsCovered) continue;

                var spanColumns = Math.Clamp(cell.ColSpan, 1, columnCount - columnIndex);
                var spanRows = Math.Clamp(cell.RowSpan, 1, rowCount - rowIndex);
                var cellRect = new Rect(
                    columnEdges[columnIndex],
                    rowEdges[rowIndex],
                    Math.Max(0, columnEdges[columnIndex + spanColumns] - columnEdges[columnIndex]),
                    Math.Max(0, rowEdges[rowIndex + spanRows] - rowEdges[rowIndex]));

                if (cellRect.Width <= 0.5 || cellRect.Height <= 0.5) continue;

                var fill = CreateFillBrush(cell.Fill, cellRect);
                if (fill is not null) context.DrawRectangle(fill, null, cellRect);

                if (cell.Text is { HasText: true })
                {
                    // 单元格内边距比文本框小一点；垂直对齐默认居中（模型默认值）
                    PptTextLayout.DrawBody(
                        context,
                        cell.Text,
                        cellRect,
                        insetLeft: 4,
                        insetTop: 2,
                        insetRight: 4,
                        insetBottom: 2,
                        wrapText: true,
                        PptAutoFit.None,
                        cell.VerticalAnchor);
                }
            }
        }

        // 格线：先画竖线再画横线，1px 浅灰（表格没有斜线，够展示结构即可）
        var top = rowEdges[0];
        var bottom = rowEdges[^1];
        foreach (var x in columnEdges)
        {
            context.DrawLine(TableGridPen, new Point(x, top), new Point(x, bottom));
        }

        var left = columnEdges[0];
        var right = columnEdges[^1];
        foreach (var y in rowEdges)
        {
            context.DrawLine(TableGridPen, new Point(left, y), new Point(right, y));
        }
    }

    /// <summary>
    /// 列宽 / 行高数组规范化：个数补齐、非法值归零，总和和包围盒不一致时整体按比例缩放。
    /// </summary>
    private static List<double> NormalizeSizes(IReadOnlyList<double>? sizes, int count, double total)
    {
        var result = new List<double>(count);
        var sum = 0.0;
        for (var index = 0; index < count; index++)
        {
            var value = sizes is not null && index < sizes.Count && sizes[index] > 0 ? sizes[index] : 0;
            result.Add(value);
            sum += value;
        }

        if (sum <= 0.01)
        {
            // 完全没有有效尺寸：平均分
            var average = total / Math.Max(1, count);
            for (var index = 0; index < result.Count; index++) result[index] = average;
            return result;
        }

        var factor = Math.Abs(sum - total) > 0.5 && total > 0.5 ? total / sum : 1.0;
        for (var index = 0; index < result.Count; index++) result[index] *= factor;
        return result;
    }

    /// <summary>把「尺寸数组」累加成「边界坐标数组」（比尺寸多一个末尾元素）。</summary>
    private static double[] BuildEdges(double start, IReadOnlyList<double> sizes)
    {
        var edges = new double[sizes.Count + 1];
        edges[0] = start;
        for (var index = 0; index < sizes.Count; index++) edges[index + 1] = edges[index] + sizes[index];
        return edges;
    }

    // ---------------------------------------------------------------- 不支持形状 / 占位

    /// <summary>不支持 / 只占位的形状：浅灰虚线框 + 居中灰色小字（KindLabel）。</summary>
    private static void DrawUnsupportedShape(DrawingContext context, PptUnsupportedShape shape)
    {
        var rect = SafeRect(shape);
        if (rect.Width <= 1 || rect.Height <= 1) return;

        DrawDashedPlaceholderBox(context, rect);

        var label = string.IsNullOrWhiteSpace(shape.KindLabel) ? "不支持的内容" : shape.KindLabel;
        var fontSize = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.14, 10, 16);
        PptTextLayout.DrawCenteredLabel(context, label, rect, fontSize, PlaceholderTextColor);
    }

    /// <summary>图片缺失：浅灰虚线框 + 居中「图片缺失」。</summary>
    private static void DrawMissingPlaceholder(DrawingContext context, Rect rect, string label)
    {
        DrawDashedPlaceholderBox(context, rect);

        var fontSize = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.12, 9, 14);
        PptTextLayout.DrawCenteredLabel(context, label, rect, fontSize, PlaceholderTextColor);
    }

    /// <summary>浅灰虚线框（不支持形状 / 图片缺失共用）。</summary>
    private static void DrawDashedPlaceholderBox(DrawingContext context, Rect rect)
    {
        context.DrawRectangle(null, PlaceholderDashedPen, rect);

        // 框里再铺一层极淡的底，深色背景上也能看出这里「有东西」
        var background = new SolidColorBrush(Color.FromArgb(0x14, 0xB0, 0xB4, 0xBB));
        background.Freeze();
        context.DrawRectangle(background, null, rect);
    }

    // ---------------------------------------------------------------- 填充 / 线条

    /// <summary>
    /// 把 <see cref="PptFill"/> 变成画刷：纯色 / 双色渐变 / 图片填充（拉伸铺满）。
    /// 返回 null = 不填充（透明）。
    /// </summary>
    private static Brush? CreateFillBrush(PptFill? fill, Rect targetRect)
    {
        if (fill is null || fill.Kind == PptFillKind.None) return null;

        switch (fill.Kind)
        {
            case PptFillKind.Solid:
                return CreateFrozenBrush(fill.Color);

            case PptFillKind.Gradient:
            {
                // 模型约定：0° = 从左到右，90° = 从上到下（顺时针）。
                // WPF 的 LinearGradientBrush 用 StartPoint / EndPoint 表达方向，这里换算成穿过中心的一条线。
                var radians = fill.GradientAngleDegrees * Math.PI / 180.0;
                var directionX = Math.Cos(radians);
                var directionY = Math.Sin(radians);
                var start = new Point(0.5 - directionX * 0.5, 0.5 - directionY * 0.5);
                var end = new Point(0.5 + directionX * 0.5, 0.5 + directionY * 0.5);

                var gradient = new LinearGradientBrush(fill.Color, fill.Color2, start, end)
                {
                    MappingMode = BrushMappingMode.RelativeToBoundingBox,
                };
                gradient.Freeze();
                return gradient;
            }

            case PptFillKind.Picture:
            {
                var bitmap = DecodeImage(fill.PictureBytes);
                if (bitmap is null) return null;

                var imageBrush = new ImageBrush(bitmap)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                };
                imageBrush.Freeze();
                return imageBrush;
            }

            default:
                return null;
        }
    }

    /// <summary>把 <see cref="PptLine"/> 变成冻结的画笔；null = 无边框。</summary>
    private static Pen? CreatePen(PptLine? line)
    {
        if (line is null) return null;

        var width = line.WidthDip > 0 ? line.WidthDip : 1;
        return CreateFrozenPen(line.Color, width, MapDashStyle(line.Dash));
    }

    /// <summary>OOXML 的 <c>a:prstDash</c> 值 → WPF 的 <see cref="DashStyle"/>；实线返回 null。</summary>
    private static DashStyle? MapDashStyle(string? dash)
    {
        if (string.IsNullOrWhiteSpace(dash)) return null;

        var key = dash.Trim().ToLowerInvariant();
        switch (key)
        {
            case "solid":
                return null;
            case "dot":
            case "sysdot":
                return DashStyles.Dot;
            case "dash":
            case "sysdash":
                return DashStyles.Dash;
            case "dashdot":
            case "sysdashdot":
                return DashStyles.DashDot;
            case "dashdotdot":
            case "sysdashdotdot":
                return DashStyles.DashDotDot;
            case "lgdash":
                return GetCachedDashStyle("lgdash", new double[] { 6, 3 });
            case "lgdashdot":
                return GetCachedDashStyle("lgdashdot", new double[] { 6, 2, 1, 2 });
            case "lgdashdotdot":
                return GetCachedDashStyle("lgdashdotdot", new double[] { 6, 2, 1, 2, 1, 2 });
            default:
                // 认不出的值（长十六进制 / 自定义）按虚线兜底，至少能看出「这不是实线」
                return DashStyles.Dash;
        }
    }

    /// <summary>自定义虚线段样式缓存（DashStyle 是 Freezable，冻结后可复用）。</summary>
    private static DashStyle GetCachedDashStyle(string key, double[] dashes)
    {
        lock (DashStyleCache)
        {
            if (DashStyleCache.TryGetValue(key, out var cached)) return cached;
        }

        var style = new DashStyle(dashes, 0);
        style.Freeze();

        lock (DashStyleCache) DashStyleCache[key] = style;
        return style;
    }

    /// <summary>造一支冻结的画笔。</summary>
    private static Pen CreateFrozenPen(Color color, double width, DashStyle? dashStyle)
    {
        var pen = new Pen(CreateFrozenBrush(color), Math.Max(0.1, width))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        if (dashStyle is not null) pen.DashStyle = dashStyle;
        pen.Freeze();
        return pen;
    }

    /// <summary>造一个冻结的纯色画刷。</summary>
    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---------------------------------------------------------------- 图片解码缓存

    /// <summary>
    /// 解码图片字节（带缓存）：<c>CacheOption = OnLoad</c> 让 WPF 立刻读进内存，
    /// 不依赖原来的 MemoryStream；解码后 Freeze，可跨线程 / 多次绘制复用。
    /// 同一组字节数组（引用相同）只解码一次，最多缓存 <see cref="MaxDecodedImages"/> 张。
    /// </summary>
    private static BitmapImage? DecodeImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;

        lock (ImageCacheLock)
        {
            if (DecodedImageCache.TryGetValue(bytes, out var cached)) return cached;
        }

        BitmapImage? bitmap = null;
        try
        {
            bitmap = new BitmapImage();
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }

            bitmap.Freeze();
        }
        catch
        {
            // 损坏 / 不支持的图片格式：返回 null，由调用方画「图片缺失」占位框
            return null;
        }

        lock (ImageCacheLock)
        {
            // 图片过多时整体清空（文档级缓存，一次清空比维护 LRU 简单，重新解码也不贵）
            if (DecodedImageCache.Count >= MaxDecodedImages) DecodedImageCache.Clear();
            DecodedImageCache[bytes] = bitmap;
        }

        return bitmap;
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>按「字节数组引用」比较的字典键（图片字节只在同一次解析里复用同一个数组）。</summary>
    private sealed class ByteArrayReferenceComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayReferenceComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => ReferenceEquals(x, y);

        public int GetHashCode(byte[] obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>把形状的 X/Y/Width/Height 变成安全矩形（宽高取非负，坏数据不抛异常）。</summary>
    private static Rect SafeRect(PptShape shape)
        => new(shape.X, shape.Y, Math.Max(0, shape.Width), Math.Max(0, shape.Height));
}
