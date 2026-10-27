using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 演示图片（只给开发 / 截图验收用，<c>YLT_PICTURE_DEMO=1</c> 时生成）。
///
/// <para>为什么不读真实图片：自检和截图机器上不一定有漫画文件夹，而「三种滚动方式的版式对不对」
/// 必须每次都能复现。这里用 <see cref="RenderTargetBitmap"/> 现画几张带大页码的图（宽高比故意不同），
/// 截图里一眼就能看出当前停在第几张、排版有没有按宽高比缩放。</para>
///
/// <para>生成一次就留在 <c>Data\picture-viewer\demo\</c>，下次直接用。</para>
/// </summary>
public static class PictureDemoData
{
    /// <summary>演示图片张数。</summary>
    public const int PageCount = 8;

    private static readonly (Color Background, Color Accent)[] Palette =
    {
        (Color.FromRgb(0x22, 0x2B, 0x3A), Color.FromRgb(0x4C, 0x8D, 0xFF)),
        (Color.FromRgb(0x33, 0x24, 0x2B), Color.FromRgb(0xE5, 0x53, 0x4B)),
        (Color.FromRgb(0x1F, 0x33, 0x2C), Color.FromRgb(0x3F, 0xB9, 0x50)),
        (Color.FromRgb(0x2F, 0x2B, 0x1F), Color.FromRgb(0xD9, 0xA8, 0x3B)),
        (Color.FromRgb(0x26, 0x22, 0x38), Color.FromRgb(0x9A, 0x7B, 0xE8)),
        (Color.FromRgb(0x1D, 0x30, 0x36), Color.FromRgb(0x3F, 0xB9, 0xC8)),
        (Color.FromRgb(0x35, 0x22, 0x22), Color.FromRgb(0xE8, 0x7B, 0x5E)),
        (Color.FromRgb(0x22, 0x30, 0x22), Color.FromRgb(0x7B, 0xC8, 0x5E)),
    };

    /// <summary>
    /// 确保演示图片已经生成（已存在就跳过），返回文件夹路径。
    /// 必须在**界面线程**上调用（<see cref="RenderTargetBitmap"/> / <see cref="FormattedText"/> 要 WPF 渲染管线）。
    /// </summary>
    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        for (var i = 1; i <= PageCount; i++)
        {
            var path = Path.Combine(directory, $"{i:000}.png");
            if (File.Exists(path)) continue;
            try
            {
                CreatePage(path, i);
            }
            catch
            {
                // 生成失败就当没有演示图（截图时显示空状态，不影响真实使用）
            }
        }
        return directory;
    }

    private static void CreatePage(string path, int number)
    {
        // 每三张来一张横的，用来核对「横向模式按高度统一缩放」和纵向模式的差异
        var wide = number % 3 == 0;
        var width = wide ? 1440 : 1000;
        var height = wide ? 1000 : 1440;
        var (background, accent) = Palette[(number - 1) % Palette.Length];

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));

            // 页眉 / 页脚色条，方便看出一张图从哪开始、到哪结束（图片之间没有间隙）
            var accentBrush = new SolidColorBrush(accent);
            dc.DrawRectangle(accentBrush, null, new Rect(0, 0, width, 10));
            dc.DrawRectangle(accentBrush, null, new Rect(0, height - 10, width, 10));

            // 大页码
            var numberText = new FormattedText(
                number.ToString("00", CultureInfo.InvariantCulture),
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                320,
                new SolidColorBrush(accent),
                1.0)
            {
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(numberText, new Point((width - numberText.Width) / 2, height * 0.30));

            // 「第 N 张」与说明
            var titleText = new FormattedText(
                $"第 {number} 张 · 演示图片",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                54,
                Brushes.White,
                1.0)
            {
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(titleText, new Point((width - titleText.Width) / 2, height * 0.14));

            var hintText = new FormattedText(
                wide ? "横向一张（宽 > 高）" : "竖向一张（高 > 宽）",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                36,
                new SolidColorBrush(Color.FromRgb(0xC8, 0xCF, 0xDA)),
                1.0)
            {
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(hintText, new Point((width - hintText.Width) / 2, height * 0.72));

            // 底部一排小圆点：看起来像一页漫画的分格提示，也便于核对缩放是否变形
            var dotBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            for (var i = 0; i < 5; i++)
                dc.DrawEllipse(dotBrush, null, new Point(width * 0.5 - 120 + i * 60, height * 0.82), 14, 14);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
