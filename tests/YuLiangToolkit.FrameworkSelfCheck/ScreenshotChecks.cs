using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Screenshot;
using YuLiangToolkit.Tools.Screenshot.Views;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「截图工具」自检：工具元数据 / 快捷键定义 / 配置默认值 / 选区换算 / 输出文件名 /
/// 标注命中测试 / 渲染器在 STA 线程里真画一张图并读回像素。
/// 不抓真实屏幕、不弹覆盖层、不碰剪贴板。
/// </summary>
internal static class ScreenshotChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        _ = report;
        CheckFn c = (name, ok, detail) => check(name, ok, detail);
        CheckMetadata(c);
        CheckShortcuts(c);
        CheckConfig(c);
        CheckGeometry(c);
        CheckFileNames(c);
        CheckAnnotations(c);
        CheckRenderer(c);
    }

    private static void CheckMetadata(CheckFn c)
    {
        var tool = new ScreenshotTool();
        c("[截图] 工具元数据：Id / 名称 / 分类 / 排序",
            tool.Id == "screenshot" && tool.Name == "截图工具"
            && tool.Category == "系统" && tool.Order == 172,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");
        c("[截图] 分类路径为「系统」，Emoji 为 📷",
            tool.CategoryPath.Count == 1 && tool.CategoryPath[0] == "系统" && tool.EmojiIcon == "📷",
            $"{string.Join("/", tool.CategoryPath)} / {tool.EmojiIcon}");
        c("[截图] 标签覆盖截图 / 编辑 / 贴图 / 快捷键 / 历史",
            new[] { "截图", "编辑", "贴图", "快捷键", "历史" }.All(tool.Tags.Contains),
            string.Join(" / ", tool.Tags));
        c("[截图] 实现了 ITool / IShortcutProvider / IShortcutActionHost",
            tool is ITool && tool is IShortcutProvider && tool is IShortcutActionHost,
            tool.GetType().FullName ?? "");
    }

    private static void CheckShortcuts(CheckFn c)
    {
        var tool = new ScreenshotTool();
        var shortcuts = tool.GetShortcuts();
        c("[截图] 声明四条快捷键（截屏 / 截屏并复制 / 重复上次 / 贴图）",
            shortcuts.Count == 4
            && shortcuts.Any(x => x.Id == ScreenshotTool.ShortcutIdCapture)
            && shortcuts.Any(x => x.Id == ScreenshotTool.ShortcutIdCaptureCopy)
            && shortcuts.Any(x => x.Id == ScreenshotTool.ShortcutIdRepeat)
            && shortcuts.Any(x => x.Id == ScreenshotTool.ShortcutIdPaste),
            string.Join(" | ", shortcuts.Select(x => $"{x.Name}={x.DisplayText}")));
        c("[截图] 四条快捷键默认都启用且都有组合",
            shortcuts.All(x => x.Enabled && !x.Primary.IsEmpty),
            string.Join(" | ", shortcuts.Select(x => x.DisplayText)));
        c("[截图] 快捷键不支持分步（截图动作应当单步）",
            shortcuts.All(x => !x.CanSplit && x.Secondary is null),
            string.Join(" | ", shortcuts.Select(x => x.Kind)));
    }

    private static void CheckConfig(CheckFn c)
    {
        var cfg = new ScreenshotConfig();
        c("[截图] 配置默认：自动复制开 / 历史开 / 贴图置顶开 / 放大镜开",
            cfg.AutoCopyOnConfirm && cfg.KeepHistory && cfg.PinTopmost && cfg.MagnifierEnabled,
            $"AutoCopy={cfg.AutoCopyOnConfirm} History={cfg.KeepHistory} Topmost={cfg.PinTopmost} Magnifier={cfg.MagnifierEnabled}");
        c("[截图] 默认四条快捷键非空且总开关开",
            cfg.EnableGlobalHotkeys && cfg.CaptureHotkey.Length > 0 && cfg.CaptureCopyHotkey.Length > 0
            && cfg.RepeatHotkey.Length > 0 && cfg.PasteHotkey.Length > 0,
            $"{cfg.CaptureHotkey} / {cfg.CaptureCopyHotkey} / {cfg.RepeatHotkey} / {cfg.PasteHotkey}");
        c("[截图] 默认贴图组为 3 组，当前组有效",
            cfg.PinGroupNames.Count == 3 && cfg.EffectivePinGroup is >= 0 and < 3,
            string.Join(" / ", cfg.PinGroupNames));
        cfg.HistoryLimit = -8;
        var low = cfg.EffectiveHistoryLimit;
        cfg.HistoryLimit = 99999;
        var high = cfg.EffectiveHistoryLimit;
        c("[截图] HistoryLimit 夹到 0~2000", low == 0 && high == 2000, $"{low} / {high}");
        c("[截图] 额外调色板至少 20 色且去重", ScreenshotColor.ExtendedPalette.Count >= 20
            && ScreenshotColor.ExtendedPalette.Distinct().Count() == ScreenshotColor.ExtendedPalette.Count,
            $"{ScreenshotColor.ExtendedPalette.Count} 色");
        c("[截图] 压缩默认关闭且档位质量正确",
            cfg.CompressionMode == ScreenshotCompression.None && !cfg.CompressionEnabled
            && ScreenshotCompression.QualityFor("light") == 92
            && ScreenshotCompression.QualityFor("standard") == 82
            && ScreenshotCompression.QualityFor("high") == 70,
            $"{cfg.CompressionMode} / {ScreenshotCompression.QualityFor("light")}-{ScreenshotCompression.QualityFor("high")}");

        var region = new ScreenshotPixelRect(-100, 20, 640, 480);
        cfg.RememberRegion(region);
        c("[截图] 上一次区域可写入并读回",
            cfg.LastRegion == region, cfg.LastRegion.ToString());

        cfg.RememberPin(@"C:\Temp\a.png", 2);
        cfg.RememberPin(@"C:\Temp\a.png", 1);
        c("[截图] 贴图记录同一路径去重并更新组",
            cfg.PinRecords.Count == 1 && cfg.GetPinGroup(@"C:\Temp\a.png") == 1,
            string.Join(" | ", cfg.PinRecords.Select(x => $"{x.Path}={x.GroupIndex}")));
    }

    private static void CheckGeometry(CheckFn c)
    {
        var desktop = new ScreenshotPixelRect(-1920, 0, 3840, 1080);
        var mapped = ScreenshotGeometry.MapSelection(new Rect(100, 50, 300, 200), 1920, 540, desktop);
        c("[截图] 选区 DIP → 物理像素（负原点 + 2× 缩放）",
            mapped == new ScreenshotPixelRect(-1720, 100, 600, 400),
            mapped.ToString());

        var back = ScreenshotGeometry.MapSelectionBack(mapped, 1920, 540, desktop);
        c("[截图] 物理选区 → DIP 可往返",
            Near(back.X, 100) && Near(back.Y, 50) && Near(back.Width, 300) && Near(back.Height, 200),
            back.ToString());

        var clamped = ScreenshotGeometry.ClampToCanvas(new Rect(-10, -8, 500, 400), 300, 200);
        c("[截图] 选区夹到画布内", clamped == new Rect(0, 0, 300, 200), clamped.ToString());
        c("[截图] 小于 2px 的选区判为不可用",
            !ScreenshotGeometry.IsUsable(new Rect(0, 0, 1, 80)) && ScreenshotGeometry.IsUsable(new Rect(0, 0, 2, 2)),
            "1px / 2px");
    }

    private static void CheckFileNames(CheckFn c)
    {
        var dir = SelfCheckScratch.MakeDirectory("screenshot-paths");
        var first = ScreenshotGeometry.BuildOutputPath(dir, "截图:测试", "png", new DateTime(2026, 11, 27, 3, 0, 0));
        File.WriteAllText(first, "x");
        var second = ScreenshotGeometry.BuildOutputPath(dir, "截图:测试", "png", new DateTime(2026, 11, 27, 3, 0, 0));
        c("[截图] 输出文件名清理非法字符并自动避让重名",
            Path.GetFileName(first).StartsWith("截图_测试-20261127-030000") && first != second && second.Contains("-1"),
            $"{Path.GetFileName(first)} / {Path.GetFileName(second)}");
        c("[截图] 后缀只接受 png / jpg，其它回退 png",
            ScreenshotGeometry.NormalizeExtension("JPG") == ".jpg"
            && ScreenshotGeometry.NormalizeExtension("jpeg") == ".jpg"
            && ScreenshotGeometry.NormalizeExtension("bmp") == ".png",
            "JPG / jpeg / bmp");
    }

    private static void CheckAnnotations(CheckFn c)
    {
        var rect = ScreenshotAnnotation.FromTwoPoints(ScreenshotDrawTool.Rectangle,
            new Point(10, 10), new Point(110, 60), Colors.Red, 3);
        c("[截图] 矩形标注包围盒正确",
            rect.Bounds == new Rect(10, 10, 100, 50), rect.Bounds.ToString());
        c("[截图] 矩形命中内部 / 不命中外部",
            rect.HitTest(new Point(50, 30)) && !rect.HitTest(new Point(200, 200)), "50,30 / 200,200");

        var line = ScreenshotAnnotation.FromTwoPoints(ScreenshotDrawTool.Line,
            new Point(0, 0), new Point(100, 0), Colors.Blue, 2);
        c("[截图] 直线命中附近 / 远离不命中",
            line.HitTest(new Point(50, 4)) && !line.HitTest(new Point(50, 30)), "50,4 / 50,30");

        var text = ScreenshotAnnotation.TextAt(new Point(0, 0), "测试文字", Colors.Green, 22, false);
        text.SetTextBounds(new Rect(0, 0, 88, 26));
        c("[截图] 文字标注使用测量后的包围盒命中",
            text.HitTest(new Point(20, 10)) && !text.HitTest(new Point(120, 10)), text.Bounds.ToString());

        c("[截图] 颜色解析支持 #RRGGBB / #AARRGGBB，坏值回退红",
            ScreenshotColor.Parse("#FF0000") == Color.FromRgb(255, 0, 0)
            && ScreenshotColor.Parse("#80112233") == Color.FromArgb(0x80, 0x11, 0x22, 0x33)
            && ScreenshotColor.Parse("bad").R == 0xE5,
            "ok");
    }

    private static void CheckRenderer(CheckFn c)
    {
        var done = false;
        var thread = new Thread(() =>
        {
            try
            {
                var bitmap = new WriteableBitmap(200, 100, 96, 96, PixelFormats.Bgra32, null);
                var pixels = new byte[200 * 100 * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = 200; pixels[i + 1] = 40; pixels[i + 2] = 20; pixels[i + 3] = 255;
                }
                bitmap.WritePixels(new Int32Rect(0, 0, 200, 100), pixels, 200 * 4, 0);
                bitmap.Freeze();

                var selection = new Rect(20, 10, 100, 50);
                var region = new ScreenshotPixelRect(20, 10, 100, 50);
                var annotation = ScreenshotAnnotation.FromTwoPoints(ScreenshotDrawTool.Rectangle,
                    new Point(30, 20), new Point(80, 45), Colors.Lime, 3);
                var output = ScreenshotRenderer.Render(bitmap, new ScreenshotPixelRect(0, 0, 200, 100),
                    region, selection, new[] { annotation });
                var outPixels = new byte[output.PixelWidth * output.PixelHeight * 4];
                output.CopyPixels(outPixels, output.PixelWidth * 4, 0);
                var index = ((10 * output.PixelWidth) + 10) * 4; // 标注左上角附近
                var compressed = ScreenshotCompression.Apply(output, ScreenshotCompression.High);
                done = output.PixelWidth == 100 && output.PixelHeight == 50
                       && outPixels[index + 1] > 180 && outPixels[index + 2] < 120
                       && compressed.PixelWidth == 100 && compressed.PixelHeight == 50;
            }
            catch (Exception ex)
            {
                done = false;
                Console.WriteLine("[截图自检] 渲染器异常：" + ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(8000);
        c("[截图] 渲染器在 STA 线程输出正确尺寸并画出标注", done, done ? "100×50" : "渲染失败");
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;
}
