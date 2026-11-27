using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>
/// 把「冻结桌面 + 标注列表」渲染成最终位图。所有标注坐标是覆盖层 DIP 的绝对坐标，
/// 这里按裁剪区域换算成物理像素后绘制，和导出分辨率无关。
/// </summary>
public static class ScreenshotRenderer
{
    /// <summary>渲染选定区域（输出物理像素）。</summary>
    public static BitmapSource Render(
        BitmapSource desktop,
        ScreenshotPixelRect desktopBounds,
        ScreenshotPixelRect region,
        Rect selectionDip,
        IReadOnlyList<ScreenshotAnnotation> annotations)
    {
        if (desktop is null) throw new ArgumentNullException(nameof(desktop));
        if (region.IsEmpty || selectionDip.Width <= 0 || selectionDip.Height <= 0)
            throw new ArgumentException("截图区域为空");

        var pixelWidth = region.Width;
        var pixelHeight = region.Height;
        var scaleX = pixelWidth / selectionDip.Width;
        var scaleY = pixelHeight / selectionDip.Height;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var cropped = new CroppedBitmap(desktop, region.ToCropRect(desktopBounds));
            cropped.Freeze();
            dc.DrawImage(cropped, new Rect(0, 0, pixelWidth, pixelHeight));

            foreach (var annotation in annotations)
            {
                if (annotation.Tool is ScreenshotDrawTool.Eraser or ScreenshotDrawTool.Select
                    or ScreenshotDrawTool.PickColor)
                    continue;

                DrawAnnotation(dc, desktop, desktopBounds, region, annotation, selectionDip, scaleX, scaleY);
            }
        }

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawAnnotation(DrawingContext dc, BitmapSource desktop,
        ScreenshotPixelRect desktopBounds, ScreenshotPixelRect region, ScreenshotAnnotation a, Rect selection, double sx, double sy)
    {
        var brush = new SolidColorBrush(a.Color);
        brush.Freeze();
        var pen = new Pen(brush, Math.Max(1, a.Thickness * (sx + sy) / 2));
        pen.StartLineCap = PenLineCap.Round;
        pen.EndLineCap = PenLineCap.Round;
        pen.LineJoin = PenLineJoin.Round;

        Point P(Point p) => new((p.X - selection.X) * sx, (p.Y - selection.Y) * sy);

        switch (a.Tool)
        {
            case ScreenshotDrawTool.Rectangle:
            case ScreenshotDrawTool.Ellipse:
            {
                if (a.Points.Count < 2) break;
                var p1 = P(a.Points[0]);
                var p2 = P(a.Points[1]);
                var rect = new Rect(p1, p2);
                if (rect.Width <= 0 || rect.Height <= 0) break;
                var fill = a.Filled ? new SolidColorBrush(Color.FromArgb(70, a.Color.R, a.Color.G, a.Color.B)) : null;
                if (fill is not null) fill.Freeze();
                if (a.Tool == ScreenshotDrawTool.Rectangle)
                    dc.DrawRectangle(fill, pen, rect);
                else
                    dc.DrawEllipse(fill, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                        rect.Width / 2, rect.Height / 2);
                break;
            }

            case ScreenshotDrawTool.Line:
            {
                if (a.Points.Count < 2) break;
                dc.DrawLine(pen, P(a.Points[0]), P(a.Points[1]));
                break;
            }

            case ScreenshotDrawTool.Arrow:
            {
                if (a.Points.Count < 2) break;
                var start = P(a.Points[0]);
                var end = P(a.Points[1]);
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1) break;

                dc.DrawLine(pen, start, end);
                var headLength = Math.Clamp(a.Thickness * 4.2, 12, 34);
                var halfWidth = headLength * 0.48;
                var ux = dx / length;
                var uy = dy / length;
                var basePoint = new Point(end.X - ux * headLength, end.Y - uy * headLength);
                var left = new Point(basePoint.X - uy * halfWidth, basePoint.Y + ux * halfWidth);
                var right = new Point(basePoint.X + uy * halfWidth, basePoint.Y - ux * halfWidth);

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(end, true, true);
                    ctx.LineTo(left, true, false);
                    ctx.LineTo(right, true, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(brush, null, geometry);
                break;
            }

            case ScreenshotDrawTool.Pen:
            case ScreenshotDrawTool.Highlighter:
            {
                var points = a.Points.Select(P).ToList();
                if (points.Count < 2) break;

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(points[0], false, false);
                    for (var i = 1; i < points.Count; i++) ctx.LineTo(points[i], true, true);
                }
                geometry.Freeze();

                if (a.Tool == ScreenshotDrawTool.Highlighter)
                {
                    var highlightPen = new Pen(new SolidColorBrush(Color.FromArgb(95, a.Color.R, a.Color.G, a.Color.B)),
                        Math.Max(8, a.Thickness * 4 * (sx + sy) / 2))
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                        LineJoin = PenLineJoin.Round,
                    };
                    dc.DrawGeometry(null, highlightPen, geometry);
                }
                else
                {
                    dc.DrawGeometry(null, pen, geometry);
                }
                break;
            }

            case ScreenshotDrawTool.Text:
            {
                if (a.Points.Count == 0 || string.IsNullOrEmpty(a.Text)) break;
                var anchor = P(a.Points[0]);
                var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var formatted = new FormattedText(a.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, Math.Max(8, a.FontSize * sy), brush, 1.0);

                if (a.Filled)
                {
                    dc.DrawRoundedRectangle(
                        new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null,
                        new Rect(anchor.X - 3, anchor.Y - 2, formatted.Width + 8, formatted.Height + 6), 4, 4);
                }
                dc.DrawText(formatted, anchor);
                break;
            }

            case ScreenshotDrawTool.Number:
            {
                if (a.Points.Count == 0) break;
                var anchor = P(a.Points[0]);
                var diameter = Math.Max(14, a.Bounds.Width * (sx + sy) / 2);
                var center = new Point(anchor.X + diameter / 2, anchor.Y + diameter / 2);
                dc.DrawEllipse(brush, null, center, diameter / 2, diameter / 2);
                var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                    FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                var text = new FormattedText(a.Number.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                    Math.Max(8, a.FontSize * sy), Brushes.White, 1.0);
                dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
                break;
            }

            case ScreenshotDrawTool.Mosaic:
            case ScreenshotDrawTool.Blur:
            {
                if (a.Points.Count < 2) break;
                var rectDip = new Rect(a.Points[0], a.Points[1]);
                if (rectDip.Width < 2 || rectDip.Height < 2) break;

                var localRect = new Rect(rectDip.X - selection.X, rectDip.Y - selection.Y,
                    rectDip.Width, rectDip.Height);
                var pixelRect = ScreenshotGeometry.MapSelection(localRect, selection.Width, selection.Height, region);
                if (pixelRect.IsEmpty) break;
                var block = a.Tool == ScreenshotDrawTool.Mosaic ? 14 : 8;
                var sampled = CreateBlockAverage(desktop, pixelRect, block, desktopBounds);
                if (sampled is null) break;

                var dest = new Rect((rectDip.X - selection.X) * sx, (rectDip.Y - selection.Y) * sy,
                    Math.Max(1, rectDip.Width * sx), Math.Max(1, rectDip.Height * sy));
                dc.DrawImage(sampled, dest);
                break;
            }
        }
    }

    /// <summary>
    /// 按块平均降采样：马赛克和模糊都用它。返回降采样后的小图，
    /// 绘制时使用线性插值就能得到平滑模糊；马赛克用较大的块。
    /// </summary>
    private static BitmapSource? CreateBlockAverage(BitmapSource source, ScreenshotPixelRect region, int block,
        ScreenshotPixelRect desktopBounds)
    {
        try
        {
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            var crop = region.ToCropRect(desktopBounds);
            if (crop.Width <= 0 || crop.Height <= 0) return null;

            block = Math.Clamp(block, 2, 64);
            var outW = Math.Max(1, crop.Width / block);
            var outH = Math.Max(1, crop.Height / block);
            var result = new byte[outW * outH * 4];

            for (var by = 0; by < outH; by++)
            {
                var y0 = crop.Y + by * block;
                var y1 = Math.Min(crop.Y + crop.Height, y0 + block);
                for (var bx = 0; bx < outW; bx++)
                {
                    var x0 = crop.X + bx * block;
                    var x1 = Math.Min(crop.X + crop.Width, x0 + block);
                    long b = 0, g = 0, r = 0, a = 0;
                    var count = 0;
                    for (var y = y0; y < y1; y++)
                    {
                        var row = y * stride;
                        for (var x = x0; x < x1; x++)
                        {
                            var i = row + x * 4;
                            b += pixels[i];
                            g += pixels[i + 1];
                            r += pixels[i + 2];
                            a += pixels[i + 3];
                            count++;
                        }
                    }
                    if (count == 0) continue;
                    var o = (by * outW + bx) * 4;
                    result[o] = (byte)(b / count);
                    result[o + 1] = (byte)(g / count);
                    result[o + 2] = (byte)(r / count);
                    result[o + 3] = (byte)(a / count);
                }
            }

            var bitmap = BitmapSource.Create(outW, outH, 96, 96, PixelFormats.Bgra32, null,
                result, outW * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
