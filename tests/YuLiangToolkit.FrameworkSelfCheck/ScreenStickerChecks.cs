using System.Text.Json;
using System.Windows;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.ScreenSticker;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「屏幕贴纸」自检：工具元数据 / 配置默认值与夹取 / 最近使用列表（去重·排序·裁剪·清空）/
/// 贴纸记录与「恢复默认设置」的边界 / 贴纸几何（宽度比例 / 放进工作区 / 层叠落点 / 以鼠标为锚点缩放 /
/// 滚轮倍率 / 角点缩放 / 重置宽度 / 夹回屏幕）/ 层级顺序 / 图片格式识别与解码 / 配置 JSON 持久化。
///
/// <para><b>本组共 91 条断言</b>（纯逻辑，不创建窗口、不 new 控件、不碰真实 Data/Config）：
/// 需要 WPF 位图解码的几条放在单独的 STA 线程里跑（做法同 <c>Live2DChecks</c> / <c>WordViewerChecks</c>），
/// 其余全部是 <see cref="StickerGeometry"/> / <see cref="StickerLayerOrder"/> / 配置对象的直接调用。</para>
/// </summary>
internal static class ScreenStickerChecks
{
    /// <summary>自检框架传进来的是 <c>Action&lt;string,bool,string&gt;</c>（detail 必填），这里包一层带默认值的委托。</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    /// <summary>几何换算默认容差（DIP）。</summary>
    private const double Tolerance = 1e-6;

    /// <summary>最小合法 1×1 PNG（68 字节，WIC 能解；自检在临时目录里写出来验证解码链路）。</summary>
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        _ = report;   // 本组不使用汇总回调
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckMetadata(fn);
        CheckConfigDefaults(fn);
        CheckRecentFiles(fn);
        CheckStickerEntries(fn);
        CheckGeometryWidthAndAspect(fn);
        CheckGeometryFitAndPlacement(fn);
        CheckGeometryScaling(fn);
        CheckGeometryResizeAndClamp(fn);
        CheckLayerOrder(fn);
        CheckImageFormats(fn);
        CheckSerialization(fn);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(CheckFn check)
    {
        var tool = new ScreenStickerTool();   // 自检进程没有 WPF Application，构造函数会 no-op（不读盘、不建窗口）

        check("[屏幕贴纸] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "screen-sticker" && tool.Name == "屏幕贴纸"
            && tool.Category == "桌面" && tool.Order == 162,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[屏幕贴纸] 分类路径只有一级「桌面」，Emoji 图标为 📌",
            tool.CategoryPath.Count == 1 && tool.CategoryPath[0] == "桌面" && tool.EmojiIcon == "📌",
            $"{string.Join(" / ", tool.CategoryPath)} / {tool.EmojiIcon}");

        check("[屏幕贴纸] 标签覆盖贴纸 / 桌面 / 图片 / 置顶 / 悬浮 / 最近使用",
            tool.Tags.Contains("贴纸") && tool.Tags.Contains("桌面") && tool.Tags.Contains("图片")
            && tool.Tags.Contains("置顶") && tool.Tags.Contains("悬浮") && tool.Tags.Contains("最近使用"),
            string.Join(" / ", tool.Tags));

        check("[屏幕贴纸] 描述能说明多选 / 拖动 / 缩放 / 层级 / 置顶 / 最近使用",
            tool.Description.Contains("多选") && tool.Description.Contains("拖动")
            && tool.Description.Contains("缩放") && tool.Description.Contains("层级")
            && tool.Description.Contains("置顶") && tool.Description.Contains("最近使用"),
            tool.Description);

        check("[屏幕贴纸] 工具 Id 与 ScreenStickerConfig.ToolId 一致（Data / Config 目录名）",
            tool.Id == ScreenStickerConfig.ToolId, ScreenStickerConfig.ToolId);

        check("[屏幕贴纸] 实现了 ITool 接口（外壳能自动发现）",
            tool is ITool, tool.GetType().FullName ?? "");

        check("[屏幕贴纸] 实现了 IToolCacheSource，最近记录为空时返回空数组（不建配置文件）",
            tool is IToolCacheSource cacheSource && cacheSource.DescribeCacheItems().Count == 0,
            tool is IToolCacheSource ? "缓存项 0 条" : "未实现 IToolCacheSource");
    }

    // ---------------------------------------------------------------- 配置默认值 / 夹取

    private static void CheckConfigDefaults(CheckFn check)
    {
        var config = new ScreenStickerConfig();

        check("[屏幕贴纸] 配置默认值：启动自动恢复开 / 新贴纸不置顶 / 默认宽度 320 / 最近上限 50",
            config.AutoRestoreOnStartup
            && !config.DefaultTopmost
            && Near(config.DefaultWidth, 320)
            && config.RecentLimit == 50
            && config.LastFolder == "",
            $"AutoRestore={config.AutoRestoreOnStartup} Topmost={config.DefaultTopmost} "
            + $"Width={config.DefaultWidth} Limit={config.RecentLimit}");

        check("[屏幕贴纸] 配置默认值：最近使用与贴纸列表都非 null 且为空",
            config.RecentEntries is not null && config.RecentEntries.Count == 0
            && config.Stickers is not null && config.Stickers.Count == 0,
            $"Recent={config.RecentEntries?.Count} Stickers={config.Stickers?.Count}");

        config.RecentLimit = -5;
        var tooSmall = config.RecentLimitEffective;
        config.RecentLimit = 999;
        var tooBig = config.RecentLimitEffective;
        config.RecentLimit = 0;
        var zero = config.RecentLimitEffective;
        config.RecentLimit = 50;
        var normal = config.RecentLimitEffective;
        check("[屏幕贴纸] RecentLimitEffective：夹到 0 ~ 100（−5→0、999→100、0 / 50 原样）",
            tooSmall == 0 && tooBig == 100 && zero == 0 && normal == 50,
            $"{tooSmall} / {tooBig} / {zero} / {normal}");

        config.RecentLimit = 0;
        var recordsWhenZero = config.RecordsRecent;
        config.RecentLimit = 1;
        var recordsWhenOne = config.RecordsRecent;
        check("[屏幕贴纸] RecordsRecent：上限 0 时不记录，上限 > 0 时记录",
            !recordsWhenZero && recordsWhenOne, $"{recordsWhenZero} / {recordsWhenOne}");

        config.DefaultWidth = double.NaN;
        var nanWidth = config.DefaultWidthEffective;
        config.DefaultWidth = 10;
        var tooNarrow = config.DefaultWidthEffective;
        config.DefaultWidth = 9999;
        var tooWide = config.DefaultWidthEffective;
        config.DefaultWidth = 500;
        var legal = config.DefaultWidthEffective;
        check("[屏幕贴纸] DefaultWidthEffective：夹到 48 ~ 2400（NaN→320、10→48、9999→2400）",
            Near(nanWidth, 320) && Near(tooNarrow, 48) && Near(tooWide, 2400) && Near(legal, 500),
            $"{nanWidth} / {tooNarrow} / {tooWide} / {legal}");
    }

    // ---------------------------------------------------------------- 最近使用

    private static void CheckRecentFiles(CheckFn check)
    {
        var fixedTime = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var a = Path.Combine(SelfCheckScratch.Root, "screen-sticker-recent-a.png");
        var b = Path.Combine(SelfCheckScratch.Root, "screen-sticker-recent-b.png");
        var c = Path.Combine(SelfCheckScratch.Root, "screen-sticker-recent-c.png");
        var d = Path.Combine(SelfCheckScratch.Root, "screen-sticker-recent-d.png");
        var e = Path.Combine(SelfCheckScratch.Root, "screen-sticker-recent-e.png");

        var single = new ScreenStickerConfig();
        single.RememberRecent(a, fixedTime);
        check("[屏幕贴纸] RememberRecent：保存完整路径与时间，并放在列表最前面",
            single.RecentEntries.Count == 1
            && SamePath(single.RecentEntries[0].Path, a)
            && single.RecentEntries[0].LastOpenedUtc == fixedTime,
            single.RecentEntries.Count > 0 ? single.RecentEntries[0].Path : "(空)");

        single.RememberRecent(b, fixedTime.AddMinutes(1));
        single.RememberRecent(a.ToUpperInvariant(), fixedTime.AddMinutes(2));
        check("[屏幕贴纸] RememberRecent：同一路径忽略大小写去重，且重新置顶",
            single.RecentEntries.Count == 2
            && SamePath(single.RecentEntries[0].Path, a)
            && SamePath(single.RecentEntries[1].Path, b),
            string.Join(" | ", single.RecentEntries.Select(entry => entry.Path)));

        var ordered = new ScreenStickerConfig();
        ordered.RememberRecent(a);
        ordered.RememberRecent(b);
        ordered.RememberRecent(c);
        check("[屏幕贴纸] 最近使用顺序：新的在前（c → b → a）",
            ordered.RecentEntries.Count == 3
            && SamePath(ordered.RecentEntries[0].Path, c)
            && SamePath(ordered.RecentEntries[1].Path, b)
            && SamePath(ordered.RecentEntries[2].Path, a),
            string.Join(" → ", ordered.RecentEntries.Select(entry => Path.GetFileName(entry.Path))));

        var limited = new ScreenStickerConfig { RecentLimit = 3 };
        foreach (var path in new[] { a, b, c, d, e }) limited.RememberRecent(path);
        check("[屏幕贴纸] RememberRecent：超过上限自动裁掉最旧的（只留最新 3 条）",
            limited.RecentEntries.Count == 3
            && SamePath(limited.RecentEntries[0].Path, e)
            && SamePath(limited.RecentEntries[1].Path, d)
            && SamePath(limited.RecentEntries[2].Path, c),
            string.Join(" → ", limited.RecentEntries.Select(entry => Path.GetFileName(entry.Path))));

        var noRecords = new ScreenStickerConfig { RecentLimit = 0 };
        noRecords.RememberRecent(a);
        var rememberedWhenZero = noRecords.RecentEntries.Count;
        noRecords.RecentEntries.Add(new RecentFileEntry { Path = ScreenStickerConfig.NormalizePath(b) });
        noRecords.TrimRecentToLimit();
        check("[屏幕贴纸] 最近上限为 0：不记录，并清空已有列表",
            rememberedWhenZero == 0 && noRecords.RecentEntries.Count == 0,
            $"记录后 {rememberedWhenZero} 条 / 裁剪后 {noRecords.RecentEntries.Count} 条");

        var removing = new ScreenStickerConfig();
        removing.RememberRecent(a);
        removing.RememberRecent(b);
        removing.RememberRecent(c);
        removing.RemoveRecent(b.ToUpperInvariant());
        check("[屏幕贴纸] RemoveRecent：只去掉匹配的那一条（忽略大小写），其它保留",
            removing.RecentEntries.Count == 2
            && SamePath(removing.RecentEntries[0].Path, c)
            && SamePath(removing.RecentEntries[1].Path, a)
            && removing.RecentEntries.All(entry => !SamePath(entry.Path, b)),
            string.Join(" → ", removing.RecentEntries.Select(entry => Path.GetFileName(entry.Path))));

        removing.RemoveRecent(Path.Combine(SelfCheckScratch.Root, "screen-sticker-recent-not-there.png"));
        check("[屏幕贴纸] RemoveRecent：路径不在列表里时什么都不做",
            removing.RecentEntries.Count == 2,
            $"{removing.RecentEntries.Count} 条");

        var clearing = new ScreenStickerConfig();
        clearing.RememberRecent(a);
        clearing.ClearRecent();
        clearing.ClearRecent();   // 空列表再清一次也不能报错
        check("[屏幕贴纸] ClearRecent：清空最近使用（空列表重复清空也安全）",
            clearing.RecentEntries.Count == 0,
            $"{clearing.RecentEntries.Count} 条");

        var invalid = new ScreenStickerConfig();
        invalid.RememberRecent(null);
        invalid.RememberRecent("");
        invalid.RememberRecent("   ");
        check("[屏幕贴纸] 最近使用：空 / 空白 / null 路径不会进列表",
            invalid.RecentEntries.Count == 0,
            $"{invalid.RecentEntries.Count} 条");
    }

    // ---------------------------------------------------------------- 贴纸记录 / 恢复默认设置

    private static void CheckStickerEntries(CheckFn check)
    {
        var a = Path.Combine(SelfCheckScratch.Root, "screen-sticker-sticker-a.png");
        var b = Path.Combine(SelfCheckScratch.Root, "screen-sticker-sticker-b.png");

        var config = new ScreenStickerConfig();
        var entryA = new StickerEntry { Path = a, Left = 10, Top = 20, Width = 200, Height = 100 };
        var entryB = new StickerEntry { Path = b, Left = 30, Top = 40, Width = 300, Height = 150 };
        config.Stickers.Add(entryA);
        config.Stickers.Add(entryB);

        check("[屏幕贴纸] 贴纸记录：Stickers 顺序 = 层级（后加的在上层）",
            config.Stickers.Count == 2
            && ReferenceEquals(config.Stickers[0], entryA)
            && ReferenceEquals(config.Stickers[1], entryB),
            string.Join(" → ", config.Stickers.Select(entry => Path.GetFileName(entry.Path))));

        check("[屏幕贴纸] FindSticker：按路径命中（忽略大小写、路径规范化）",
            ReferenceEquals(config.FindSticker(a.ToUpperInvariant()), entryA)
            && ReferenceEquals(
                config.FindSticker(Path.Combine(SelfCheckScratch.Root, ".", "screen-sticker-sticker-b.png")),
                entryB),
            "大小写 / 带 . 的路径都能找到");

        check("[屏幕贴纸] FindSticker：找不到 / 空路径 / null 返回 null",
            config.FindSticker(Path.Combine(SelfCheckScratch.Root, "screen-sticker-missing.png")) is null
            && config.FindSticker("") is null
            && config.FindSticker(null) is null);

        config.ClearStickers();
        config.ClearStickers();   // 空列表再清一次也不能报错
        check("[屏幕贴纸] ClearStickers：清空全部贴纸记录（空列表重复清空也安全）",
            config.Stickers.Count == 0, $"{config.Stickers.Count} 条");

        var resetTarget = new ScreenStickerConfig
        {
            AutoRestoreOnStartup = false,
            DefaultTopmost = true,
            DefaultWidth = 111,
            RecentLimit = 2,
        };
        resetTarget.Stickers.Add(new StickerEntry { Path = a });
        resetTarget.Stickers.Add(new StickerEntry { Path = b });
        resetTarget.RememberRecent(a);
        resetTarget.RememberRecent(b);
        resetTarget.RememberRecent(Path.Combine(SelfCheckScratch.Root, "screen-sticker-sticker-c.png"));
        resetTarget.ResetPreferences();

        check("[屏幕贴纸] ResetPreferences：只把偏好项恢复默认（自动恢复开 / 不置顶 / 320 / 上限 50）",
            resetTarget.AutoRestoreOnStartup
            && !resetTarget.DefaultTopmost
            && Near(resetTarget.DefaultWidth, ScreenStickerConfig.DefaultStickerWidth)
            && resetTarget.RecentLimit == ScreenStickerConfig.DefaultRecentLimit
            && resetTarget.RecentLimitEffective == ScreenStickerConfig.DefaultRecentLimit,
            $"AutoRestore={resetTarget.AutoRestoreOnStartup} Topmost={resetTarget.DefaultTopmost} "
            + $"Width={resetTarget.DefaultWidth} Limit={resetTarget.RecentLimit}");

        check("[屏幕贴纸] ResetPreferences：不清桌面贴纸与最近使用（是用户当下的成果，不能一键丢掉）",
            resetTarget.Stickers.Count == 2 && resetTarget.RecentEntries.Count == 2
            && SamePath(resetTarget.Stickers[0].Path, a)
            && SamePath(resetTarget.Stickers[1].Path, b),
            $"贴纸 {resetTarget.Stickers.Count} 张 / 最近 {resetTarget.RecentEntries.Count} 条");
    }

    // ---------------------------------------------------------------- 几何：宽度 / 比例 / 尺寸

    private static void CheckGeometryWidthAndAspect(CheckFn check)
    {
        check("[屏幕贴纸] ClampWidth：NaN / 正负无穷回退默认宽度 320",
            Near(StickerGeometry.ClampWidth(double.NaN), 320)
            && Near(StickerGeometry.ClampWidth(double.PositiveInfinity), 320)
            && Near(StickerGeometry.ClampWidth(double.NegativeInfinity), 320),
            $"{StickerGeometry.ClampWidth(double.NaN)} / {StickerGeometry.ClampWidth(double.PositiveInfinity)} / "
            + $"{StickerGeometry.ClampWidth(double.NegativeInfinity)}");

        check("[屏幕贴纸] ClampWidth：0 / 负数回退默认宽度，fallback 参数生效",
            Near(StickerGeometry.ClampWidth(0), 320)
            && Near(StickerGeometry.ClampWidth(-123), 320)
            && Near(StickerGeometry.ClampWidth(double.NaN, 111), 111),
            $"{StickerGeometry.ClampWidth(0)} / {StickerGeometry.ClampWidth(-123)} / "
            + $"{StickerGeometry.ClampWidth(double.NaN, 111)}");

        check("[屏幕贴纸] ClampWidth：小于 48 夹到 48、大于 2400 夹到 2400、合法值原样",
            Near(StickerGeometry.ClampWidth(10), StickerGeometry.MinWidth)
            && Near(StickerGeometry.ClampWidth(9999), StickerGeometry.MaxWidth)
            && Near(StickerGeometry.ClampWidth(320), 320)
            && Near(StickerGeometry.ClampWidth(20), StickerGeometry.MinWidth)
            && Near(StickerGeometry.ClampWidth(5000), StickerGeometry.MaxWidth),
            $"{StickerGeometry.ClampWidth(10)} / {StickerGeometry.ClampWidth(9999)} / "
            + $"{StickerGeometry.ClampWidth(320)}");

        check("[屏幕贴纸] ClampAspect：NaN / ∞ / 0 / 负数当 1:1",
            Near(StickerGeometry.ClampAspect(double.NaN), 1)
            && Near(StickerGeometry.ClampAspect(double.PositiveInfinity), 1)
            && Near(StickerGeometry.ClampAspect(0), 1)
            && Near(StickerGeometry.ClampAspect(-3), 1),
            $"{StickerGeometry.ClampAspect(double.NaN)} / {StickerGeometry.ClampAspect(double.PositiveInfinity)} / "
            + $"{StickerGeometry.ClampAspect(0)} / {StickerGeometry.ClampAspect(-3)}");

        check("[屏幕贴纸] ClampAspect：比例夹到 0.05 ~ 20",
            Near(StickerGeometry.ClampAspect(0.001), 0.05)
            && Near(StickerGeometry.ClampAspect(100), 20)
            && Near(StickerGeometry.ClampAspect(0.5), 0.5)
            && Near(StickerGeometry.ClampAspect(0.05), 0.05)
            && Near(StickerGeometry.ClampAspect(20), 20),
            $"{StickerGeometry.ClampAspect(0.001)} / {StickerGeometry.ClampAspect(100)} / "
            + $"{StickerGeometry.ClampAspect(0.5)}");

        check("[屏幕贴纸] AspectOf：按图片像素尺寸算「高 / 宽」比例",
            Near(StickerGeometry.AspectOf(100, 50), 0.5)
            && Near(StickerGeometry.AspectOf(3840, 2160), 0.5625)
            && Near(StickerGeometry.AspectOf(1080, 1920), 1920.0 / 1080.0),
            $"{StickerGeometry.AspectOf(100, 50)} / {StickerGeometry.AspectOf(3840, 2160)} / "
            + $"{StickerGeometry.AspectOf(1080, 1920):0.####}");

        check("[屏幕贴纸] AspectOf：像素尺寸取不到 / 非法时按 1:1，极端比例被夹取",
            Near(StickerGeometry.AspectOf(0, 100), 1)
            && Near(StickerGeometry.AspectOf(100, 0), 1)
            && Near(StickerGeometry.AspectOf(double.NaN, 100), 1)
            && Near(StickerGeometry.AspectOf(100, 1e9), 20)
            && Near(StickerGeometry.AspectOf(1e-9, 100), 20),
            $"{StickerGeometry.AspectOf(0, 100)} / {StickerGeometry.AspectOf(100, 1e9)}");

        var size = StickerGeometry.SizeForWidth(320, 0.5);
        var illegalSize = StickerGeometry.SizeForWidth(double.NaN, double.NaN);
        var tinySize = StickerGeometry.SizeForWidth(10, 1);
        check("[屏幕贴纸] SizeForWidth：目标宽度 + 比例算尺寸，宽度先夹 48 ~ 2400",
            Near(size.Width, 320) && Near(size.Height, 160)
            && Near(illegalSize.Width, 320) && Near(illegalSize.Height, 320)
            && Near(tinySize.Width, 48) && Near(tinySize.Height, 48),
            $"{size} / {illegalSize} / {tinySize}");
    }

    // ---------------------------------------------------------------- 几何：放进工作区 / 落点 / 恢复位置

    private static void CheckGeometryFitAndPlacement(CheckFn check)
    {
        var workArea = new Rect(0, 0, 1920, 1080);

        var normal = StickerGeometry.FitInside(workArea, 320, StickerGeometry.AspectOf(3840, 2160));
        check("[屏幕贴纸] FitInside：普通图片按目标宽度放进工作区",
            Near(normal.Width, 320) && Near(normal.Height, 180),
            normal.ToString());

        var portrait = StickerGeometry.FitInside(workArea, 320, StickerGeometry.AspectOf(1080, 3840));
        check("[屏幕贴纸] FitInside：巨大竖图按工作区高度 90% 再缩一次，不会超出屏幕",
            portrait.Height <= 1080 * 0.9 + Tolerance
            && Near(portrait.Height, 972)
            && Near(portrait.Width, 273.375)
            && portrait.Width >= StickerGeometry.MinWidth,
            portrait.ToString());

        var capped = StickerGeometry.FitInside(new Rect(0, 0, 800, 600), 2000, 1);
        check("[屏幕贴纸] FitInside：宽 / 高都不超过工作区的 90%",
            capped.Width <= 800 * 0.9 + Tolerance
            && capped.Height <= 600 * 0.9 + Tolerance
            && Near(capped.Width, 540) && Near(capped.Height, 540),
            capped.ToString());

        var zeroArea = StickerGeometry.FitInside(new Rect(0, 0, 0, 0), 320, 1);
        check("[屏幕贴纸] FitInside：工作区为 0 时不崩，仍返回夹取后的尺寸",
            Near(zeroArea.Width, 320) && Near(zeroArea.Height, 320),
            zeroArea.ToString());

        var centered = StickerGeometry.CenterIn(workArea, 320, 180);
        check("[屏幕贴纸] CenterIn：把贴纸放在工作区正中",
            Near(centered.Left, 800) && Near(centered.Top, 450)
            && Near(centered.Width, 320) && Near(centered.Height, 180),
            centered.ToString());

        var negativeArea = new Rect(-1920, -200, 1920, 1200);
        var negativeCenter = StickerGeometry.CenterIn(negativeArea, 320, 180);
        check("[屏幕贴纸] CenterIn：负坐标工作区（左侧副屏）同样正确",
            Near(negativeCenter.Left, -1120) && Near(negativeCenter.Top, 310),
            negativeCenter.ToString());

        var baseSize = new StickerBounds(0, 0, 320, 180);
        var first = StickerGeometry.PlacementForNew(workArea, baseSize, 0);
        var second = StickerGeometry.PlacementForNew(workArea, baseSize, 1);
        check("[屏幕贴纸] PlacementForNew：第一张居中、第二张向右下层叠 28",
            Near(first.Left, 800) && Near(first.Top, 450)
            && Near(second.Left, 800 + StickerGeometry.CascadeOffset)
            && Near(second.Top, 450 + StickerGeometry.CascadeOffset),
            $"{first} / {second}");

        var cycleWrap = StickerGeometry.PlacementForNew(workArea, baseSize, StickerGeometry.CascadeCycle + 1);
        var cycleReset = StickerGeometry.PlacementForNew(workArea, baseSize, StickerGeometry.CascadeCycle);
        check("[屏幕贴纸] PlacementForNew：层叠偏移按 CascadeCycle 循环，不会一路跑到屏幕外",
            Near(cycleWrap.Left, second.Left) && Near(cycleWrap.Top, second.Top)
            && Near(cycleReset.Left, first.Left) && Near(cycleReset.Top, first.Top),
            $"cycle+1={cycleWrap} / cycle={cycleReset}");

        var negativeCount = StickerGeometry.PlacementForNew(workArea, baseSize, -5);
        check("[屏幕贴纸] PlacementForNew：已有数量为负数按 0 处理（不会往反方向错位）",
            Near(negativeCount.Left, first.Left) && Near(negativeCount.Top, first.Top),
            negativeCount.ToString());

        var smallArea = new Rect(0, 0, 400, 300);
        var smallSize = new StickerBounds(0, 0, 320, 240);
        var clampedPlacement = StickerGeometry.PlacementForNew(smallArea, smallSize, 11);
        check("[屏幕贴纸] PlacementForNew：层叠后仍夹在工作区，至少留 48 可见",
            VisibleEnough(clampedPlacement, smallArea)
            && smallArea.Right - clampedPlacement.Left >= StickerGeometry.MinVisible - Tolerance
            && smallArea.Bottom - clampedPlacement.Top >= StickerGeometry.MinVisible - Tolerance,
            clampedPlacement.ToString());

        var screen = new Rect(0, 0, 1920, 1080);
        var offscreen = StickerGeometry.NormalizeStored(-5000, 5000, 320, 0.5, screen);
        check("[屏幕贴纸] NormalizeStored：屏幕外的旧位置被拉回，至少留 48 可见",
            Near(offscreen.Width, 320) && Near(offscreen.Height, 160)
            && Near(offscreen.Left, -272) && Near(offscreen.Top, 1032)
            && VisibleEnough(offscreen, screen),
            offscreen.ToString());

        var broken = StickerGeometry.NormalizeStored(double.NaN, double.NaN, 9999, 1, screen);
        check("[屏幕贴纸] NormalizeStored：NaN 位置 / 超大宽度也变成合法坐标与尺寸",
            Near(broken.Width, 2400) && Near(broken.Height, 2400)
            && !double.IsNaN(broken.Left) && !double.IsNaN(broken.Top)
            && VisibleEnough(broken, screen),
            broken.ToString());

        var negativeWidth = StickerGeometry.NormalizeStored(100, 100, -50, 0.5, screen);
        check("[屏幕贴纸] NormalizeStored：负数宽度回退默认 320，并按比例算高度",
            Near(negativeWidth.Width, 320) && Near(negativeWidth.Height, 160)
            && Near(negativeWidth.Left, 100) && Near(negativeWidth.Top, 100),
            negativeWidth.ToString());
    }

    // ---------------------------------------------------------------- 几何：以鼠标为锚点缩放 / 滚轮倍率

    private static void CheckGeometryScaling(CheckFn check)
    {
        var screen = new Rect(0, 0, 1920, 1080);
        var current = new StickerBounds(100, 100, 200, 100);
        const double AnchorX = 200;
        const double AnchorY = 150;

        var zoomed = StickerGeometry.ScaleAround(current, 1.12, AnchorX, AnchorY, screen);
        check("[屏幕贴纸] ScaleAround：鼠标锚点在缩放前后保持不动（相对位置一致）",
            Near(zoomed.Width, 224) && Near(zoomed.Height, 112)
            && Near((AnchorX - zoomed.Left) / zoomed.Width, 0.5, 1e-9)
            && Near((AnchorY - zoomed.Top) / zoomed.Height, 0.5, 1e-9),
            zoomed.ToString());

        var shrunk = StickerGeometry.ScaleAround(current, 1 / 1.12, AnchorX, AnchorY, screen);
        check("[屏幕贴纸] ScaleAround：滚轮缩小同样以鼠标为不动点",
            Near(shrunk.Width, 200 / 1.12) && Near(shrunk.Height, 100 / 1.12)
            && Near((AnchorX - shrunk.Left) / shrunk.Width, 0.5, 1e-9)
            && Near((AnchorY - shrunk.Top) / shrunk.Height, 0.5, 1e-9),
            shrunk.ToString());

        var invalid = StickerGeometry.ScaleAround(current, double.NaN, AnchorX, AnchorY, screen);
        check("[屏幕贴纸] ScaleAround：非法倍率（NaN / ∞ / 0 / 负数）原样返回",
            StickerGeometry.NearlyEqual(invalid, current)
            && StickerGeometry.NearlyEqual(
                StickerGeometry.ScaleAround(current, double.PositiveInfinity, AnchorX, AnchorY, screen), current)
            && StickerGeometry.NearlyEqual(
                StickerGeometry.ScaleAround(current, 0, AnchorX, AnchorY, screen), current)
            && StickerGeometry.NearlyEqual(
                StickerGeometry.ScaleAround(current, -2, AnchorX, AnchorY, screen), current),
            invalid.ToString());

        var zoomIn = current;
        for (var i = 0; i < 60; i++)
            zoomIn = StickerGeometry.ScaleAround(zoomIn, 1.12, AnchorX, AnchorY, screen);
        var zoomOut = current;
        for (var i = 0; i < 80; i++)
            zoomOut = StickerGeometry.ScaleAround(zoomOut, 1 / 1.12, AnchorX, AnchorY, screen);
        check("[屏幕贴纸] ScaleAround：连续放大 / 缩小最终夹到 48 ~ 2400，不会缩成 0",
            Near(zoomIn.Width, StickerGeometry.MaxWidth) && Near(zoomIn.Height, 1200)
            && Near(zoomOut.Width, StickerGeometry.MinWidth) && Near(zoomOut.Height, 24),
            $"放大后 {zoomIn.Width}×{zoomIn.Height} / 缩小后 {zoomOut.Width}×{zoomOut.Height}");

        var leftScreen = new Rect(-1920, -200, 1920, 1200);
        var local = new StickerBounds(-1000, 100, 200, 100);
        var localZoom = StickerGeometry.ScaleAround(local, 1.12, -900, 150, leftScreen);
        check("[屏幕贴纸] ScaleAround：负坐标屏幕（左侧副屏）锚点不动且仍在可见范围内",
            Near((-900 - localZoom.Left) / localZoom.Width, 0.5, 1e-9)
            && Near((150 - localZoom.Top) / localZoom.Height, 0.5, 1e-9)
            && VisibleEnough(localZoom, leftScreen),
            localZoom.ToString());

        check("[屏幕贴纸] WheelFactor：向上滚一格 = 1.12 倍、两格 = 1.12²、0 = 1",
            Near(StickerGeometry.WheelFactor(120), StickerGeometry.WheelStepFactor)
            && Near(StickerGeometry.WheelFactor(240), Math.Pow(StickerGeometry.WheelStepFactor, 2))
            && Near(StickerGeometry.WheelFactor(0), 1),
            $"{StickerGeometry.WheelFactor(120)} / {StickerGeometry.WheelFactor(240)} / {StickerGeometry.WheelFactor(0)}");

        check("[屏幕贴纸] WheelFactor：向下滚一格 = 1/1.12（两格 = 1/1.12²）",
            Near(StickerGeometry.WheelFactor(-120), 1 / StickerGeometry.WheelStepFactor, 1e-12)
            && Near(StickerGeometry.WheelFactor(-240), Math.Pow(StickerGeometry.WheelStepFactor, -2), 1e-12),
            $"{StickerGeometry.WheelFactor(-120)} / {StickerGeometry.WheelFactor(-240)}");
    }

    // ---------------------------------------------------------------- 几何：角点缩放 / 重置 / 夹回屏幕

    private static void CheckGeometryResizeAndClamp(CheckFn check)
    {
        var screen = new Rect(0, 0, 1920, 1080);

        check("[屏幕贴纸] WidthDeltaForCorner：横向位移主导时直接用 dx",
            Near(StickerGeometry.WidthDeltaForCorner(40, 10, 200, 100), 40)
            && Near(StickerGeometry.WidthDeltaForCorner(-40, 10, 200, 100), -40)
            && Near(StickerGeometry.WidthDeltaForCorner(10, 10, 200, 100), 10),
            $"{StickerGeometry.WidthDeltaForCorner(40, 10, 200, 100)} / "
            + $"{StickerGeometry.WidthDeltaForCorner(-40, 10, 200, 100)}");

        check("[屏幕贴纸] WidthDeltaForCorner：竖向位移主导时按宽 / 高折算成横向增量",
            Near(StickerGeometry.WidthDeltaForCorner(5, 20, 200, 100), 40)
            && Near(StickerGeometry.WidthDeltaForCorner(-5, -20, 200, 100), -40)
            && Near(StickerGeometry.WidthDeltaForCorner(1, 10, 200, 0), 0)
            && Near(StickerGeometry.WidthDeltaForCorner(1, 10, 200, double.NaN), 0),
            $"{StickerGeometry.WidthDeltaForCorner(5, 20, 200, 100)} / "
            + $"{StickerGeometry.WidthDeltaForCorner(1, 10, 200, 0)}");

        var start = new StickerBounds(100, 100, 200, 100);
        var resized = StickerGeometry.ResizeFromCorner(start, 50, 10, screen);
        check("[屏幕贴纸] ResizeFromCorner：左上角不动、宽度随横向拖动变化、高度等比",
            Near(resized.Left, 100) && Near(resized.Top, 100)
            && Near(resized.Width, 250) && Near(resized.Height, 125),
            resized.ToString());

        var resizedVertical = StickerGeometry.ResizeFromCorner(start, 0, 50, screen);
        check("[屏幕贴纸] ResizeFromCorner：纯竖向拖动也能等比缩放",
            Near(resizedVertical.Left, 100) && Near(resizedVertical.Top, 100)
            && Near(resizedVertical.Width, 300) && Near(resizedVertical.Height, 150),
            resizedVertical.ToString());

        var resizedLarge = StickerGeometry.ResizeFromCorner(start, 100000, 0, screen);
        // 起点宽 200，拖 −152 → 48（正好到最小宽度）；再往左拖属于非法宽度，ClampWidth 会回退默认 320。
        var resizedSmall = StickerGeometry.ResizeFromCorner(start, -152, 0, screen);
        var resizedNegative = StickerGeometry.ResizeFromCorner(start, -100000, 0, screen);
        check("[屏幕贴纸] ResizeFromCorner：宽度夹到 48 ~ 2400，图片比例保持（非法宽度回退默认）",
            Near(resizedLarge.Width, StickerGeometry.MaxWidth) && Near(resizedLarge.Height, 1200)
            && Near(resizedSmall.Width, StickerGeometry.MinWidth) && Near(resizedSmall.Height, 24)
            && Near(resizedNegative.Width, ScreenStickerConfig.DefaultStickerWidth)
            && Near(resizedNegative.Height, 160),
            $"{resizedLarge.Width}×{resizedLarge.Height} / {resizedSmall.Width}×{resizedSmall.Height} / "
            + $"{resizedNegative.Width}×{resizedNegative.Height}");

        var centerCurrent = new StickerBounds(100, 100, 200, 100);
        var reset = StickerGeometry.ResetWidthKeepingCenter(centerCurrent, 320, 0.5, screen);
        check("[屏幕贴纸] ResetWidthKeepingCenter：宽度重置成默认 320、中心点不动",
            Near(reset.Width, 320) && Near(reset.Height, 160)
            && Near(reset.Left + reset.Width / 2, 200)
            && Near(reset.Top + reset.Height / 2, 150),
            reset.ToString());

        var resetTall = StickerGeometry.ResetWidthKeepingCenter(centerCurrent, 200, 2, screen);
        check("[屏幕贴纸] ResetWidthKeepingCenter：按新比例重算高度，中心点仍然不动",
            Near(resetTall.Width, 200) && Near(resetTall.Height, 400)
            && Near(resetTall.Left + resetTall.Width / 2, 200)
            && Near(resetTall.Top + resetTall.Height / 2, 150),
            resetTall.ToString());

        var tiny = StickerGeometry.ClampToBounds(new StickerBounds(50, 60, 10, 0), screen);
        check("[屏幕贴纸] ClampToBounds：尺寸先夹到合法范围（宽度 48、高度按宽度补）",
            Near(tiny.Width, 48) && Near(tiny.Height, 48)
            && Near(tiny.Left, 50) && Near(tiny.Top, 60),
            tiny.ToString());

        var topLeftOff = StickerGeometry.ClampToBounds(new StickerBounds(-10000, -10000, 200, 100), screen);
        check("[屏幕贴纸] ClampToBounds：拖到左上屏幕外也至少留 48 见方可抓",
            Near(topLeftOff.Left, -152) && Near(topLeftOff.Top, -52)
            && VisibleEnough(topLeftOff, screen),
            topLeftOff.ToString());

        var bottomRightOff = StickerGeometry.ClampToBounds(new StickerBounds(10000, 10000, 200, 100), screen);
        check("[屏幕贴纸] ClampToBounds：拖到右下屏幕外同样拉回",
            Near(bottomRightOff.Left, 1872) && Near(bottomRightOff.Top, 1032)
            && VisibleEnough(bottomRightOff, screen),
            bottomRightOff.ToString());

        var nanBounds = StickerGeometry.ClampToBounds(new StickerBounds(double.NaN, double.NaN, 200, 100), screen);
        check("[屏幕贴纸] ClampToBounds：NaN 位置不会写回窗口，回退到最小可见位置",
            Near(nanBounds.Left, -152) && Near(nanBounds.Top, -52),
            nanBounds.ToString());

        var noScreen = StickerGeometry.ClampToBounds(new StickerBounds(123, 456, 10, 0), new Rect(0, 0, 0, 0));
        check("[屏幕贴纸] ClampToBounds：屏幕矩形为 0 宽度时不崩，尺寸仍然夹取",
            Near(noScreen.Left, 123) && Near(noScreen.Top, 456)
            && Near(noScreen.Width, 48) && Near(noScreen.Height, 48),
            noScreen.ToString());

        check("[屏幕贴纸] NearlyEqual：容差内相等、超出容差不等（恢复布局判断用）",
            StickerGeometry.NearlyEqual(new StickerBounds(10, 20, 30, 40), new StickerBounds(10.3, 20, 30, 40))
            && !StickerGeometry.NearlyEqual(new StickerBounds(10, 20, 30, 40), new StickerBounds(11, 20, 30, 40))
            && !StickerGeometry.NearlyEqual(new StickerBounds(10, 20, 30, 40), new StickerBounds(10, 20, 31, 40)),
            "0.3 差算相等 / 1.0 差算不等");
    }

    // ---------------------------------------------------------------- 层级顺序

    private static void CheckLayerOrder(CheckFn check)
    {
        var list = CreateLayerList();
        var moved = StickerLayerOrder.MoveUp(list, list[1]);
        check("[屏幕贴纸] 层级 MoveUp：中间元素上移一层（换位后返回 true）",
            moved && IsLayerSequence(list, "a", "c", "b"),
            DescribeLayer(list));

        list = CreateLayerList();
        moved = StickerLayerOrder.MoveUp(list, list[2]);
        check("[屏幕贴纸] 层级 MoveUp：已经在最上层返回 false，顺序不变",
            !moved && IsLayerSequence(list, "a", "b", "c"),
            DescribeLayer(list));

        list = CreateLayerList();
        moved = StickerLayerOrder.MoveDown(list, list[1]);
        check("[屏幕贴纸] 层级 MoveDown：中间元素下移一层（换位后返回 true）",
            moved && IsLayerSequence(list, "b", "a", "c"),
            DescribeLayer(list));

        list = CreateLayerList();
        moved = StickerLayerOrder.MoveDown(list, list[0]);
        check("[屏幕贴纸] 层级 MoveDown：已经在最下层返回 false，顺序不变",
            !moved && IsLayerSequence(list, "a", "b", "c"),
            DescribeLayer(list));

        list = CreateLayerList();
        moved = StickerLayerOrder.MoveToTop(list, list[0]);
        check("[屏幕贴纸] 层级 MoveToTop：挪到列表末尾（最上层）并返回 true",
            moved && IsLayerSequence(list, "b", "c", "a"),
            DescribeLayer(list));

        list = CreateLayerList();
        moved = StickerLayerOrder.MoveToTop(list, list[2]);
        check("[屏幕贴纸] 层级 MoveToTop：已经是最上层返回 false",
            !moved && IsLayerSequence(list, "a", "b", "c"),
            DescribeLayer(list));

        list = CreateLayerList();
        moved = StickerLayerOrder.MoveToBottom(list, list[2]);
        check("[屏幕贴纸] 层级 MoveToBottom：挪到列表开头（最下层）并返回 true",
            moved && IsLayerSequence(list, "c", "a", "b"),
            DescribeLayer(list));

        list = CreateLayerList();
        moved = StickerLayerOrder.MoveToBottom(list, list[0]);
        check("[屏幕贴纸] 层级 MoveToBottom：已经是最下层返回 false",
            !moved && IsLayerSequence(list, "a", "b", "c"),
            DescribeLayer(list));

        list = CreateLayerList();
        check("[屏幕贴纸] 层级 IndexOf：按对象身份找下标，找不到返回 −1",
            StickerLayerOrder.IndexOf(list, list[1]) == 1
            && StickerLayerOrder.IndexOf(list, new StickerEntry()) == -1,
            $"{StickerLayerOrder.IndexOf(list, list[1])} / {StickerLayerOrder.IndexOf(list, new StickerEntry())}");

        list = CreateLayerList();
        var untouched = new StickerEntry();
        check("[屏幕贴纸] 层级操作：列表里没有的对象一律返回 false（不会误删 / 误插）",
            !StickerLayerOrder.MoveUp(list, untouched)
            && !StickerLayerOrder.MoveDown(list, untouched)
            && !StickerLayerOrder.MoveToTop(list, untouched)
            && !StickerLayerOrder.MoveToBottom(list, untouched)
            && IsLayerSequence(list, "a", "b", "c"),
            DescribeLayer(list));

        list = CreateLayerList();
        var originals = list.ToArray();
        StickerLayerOrder.MoveToTop(list, originals[0]);
        StickerLayerOrder.MoveDown(list, originals[2]);
        StickerLayerOrder.MoveToBottom(list, originals[1]);
        check("[屏幕贴纸] 层级操作后列表元素不丢、不重复（只是换顺序）",
            list.Count == 3
            && originals.All(entry => list.Contains(entry))
            && list.Distinct().Count() == 3,
            DescribeLayer(list));
    }

    // ---------------------------------------------------------------- 图片格式 / 解码

    private static void CheckImageFormats(CheckFn check)
    {
        check("[屏幕贴纸] IsSupportedExtension：常见图片后缀都支持（忽略大小写）",
            StickerImage.IsSupportedExtension("a.png")
            && StickerImage.IsSupportedExtension("a.jpg")
            && StickerImage.IsSupportedExtension("a.JPG")
            && StickerImage.IsSupportedExtension("a.jpeg")
            && StickerImage.IsSupportedExtension("a.bmp")
            && StickerImage.IsSupportedExtension("a.gif")
            && StickerImage.IsSupportedExtension("a.webp")
            && StickerImage.IsSupportedExtension("a.tif")
            && StickerImage.IsSupportedExtension("a.tiff")
            && StickerImage.IsSupportedExtension("a.ico"),
            string.Join(" / ", StickerImage.Extensions));

        check("[屏幕贴纸] IsSupportedExtension：其它后缀 / 无后缀 / 空 / null 返回 false",
            !StickerImage.IsSupportedExtension("a.txt")
            && !StickerImage.IsSupportedExtension("a.gifx")
            && !StickerImage.IsSupportedExtension("no-extension")
            && !StickerImage.IsSupportedExtension("")
            && !StickerImage.IsSupportedExtension("   ")
            && !StickerImage.IsSupportedExtension(null),
            "txt / gifx / 无后缀 / 空串 / null 都不支持");

        check("[屏幕贴纸] 文件对话框过滤器包含已支持的后缀与「所有文件」",
            StickerImage.DialogFilter.Contains("*.png")
            && StickerImage.DialogFilter.Contains("*.webp")
            && StickerImage.DialogFilter.Contains("所有文件"),
            StickerImage.DialogFilter);

        var directory = SelfCheckScratch.MakeDirectory("screen-sticker-images");
        var pngPath = Path.Combine(directory, "pixel.png");
        var badPath = Path.Combine(directory, "broken.png");
        var missingPath = Path.Combine(directory, "missing.png");
        var written = true;
        try
        {
            File.WriteAllBytes(pngPath, Convert.FromBase64String(OnePixelPngBase64));
            File.WriteAllBytes(badPath, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 });
        }
        catch
        {
            written = false;
        }

        check("[屏幕贴纸] 自检前置：临时目录里能写出 1×1 PNG 与坏文件", written, directory);

        var canOpenPng = false;
        var pngSizeKnown = false;
        var pngWidth = 0;
        var pngHeight = 0;
        var pngLoaded = false;
        var thumbnailLoaded = false;
        var canOpenBad = true;
        var badSizeKnown = true;
        var badLoaded = true;
        var canOpenMissing = true;
        var nullArgumentsOk = false;

        // WPF 位图解码放到独立 STA 线程里（主线程是 MTA；做法同 Live2DChecks）：
        // 只解 1×1 PNG，不创建窗口、不显示到界面。
        var failure = RunOnSta(() =>
        {
            canOpenPng = StickerImage.CanOpen(pngPath);
            var pixelSize = StickerImage.ReadPixelSize(pngPath);
            pngSizeKnown = pixelSize is not null;
            pngWidth = pixelSize?.Width ?? 0;
            pngHeight = pixelSize?.Height ?? 0;
            pngLoaded = StickerImage.Load(pngPath) is not null;
            thumbnailLoaded = StickerImage.LoadThumbnail(pngPath) is not null;
            canOpenBad = StickerImage.CanOpen(badPath);
            badSizeKnown = StickerImage.ReadPixelSize(badPath) is not null;
            badLoaded = StickerImage.Load(badPath) is not null;
            canOpenMissing = StickerImage.CanOpen(missingPath);
            nullArgumentsOk = StickerImage.Load(null) is null
                && StickerImage.Load("") is null
                && !StickerImage.CanOpen(null);
        });

        check("[屏幕贴纸] 图片解码在独立 STA 线程里跑完且没有抛异常",
            failure is null, failure?.Message ?? "ok");

        check("[屏幕贴纸] 合法 1×1 PNG：CanOpen=true、ReadPixelSize=(1, 1)",
            canOpenPng && pngSizeKnown && pngWidth == 1 && pngHeight == 1,
            $"CanOpen={canOpenPng} 尺寸={pngWidth}×{pngHeight}");

        check("[屏幕贴纸] 合法 PNG：Load / LoadThumbnail 返回非 null 位图",
            pngLoaded && thumbnailLoaded,
            $"Load={pngLoaded} Thumbnail={thumbnailLoaded}");

        check("[屏幕贴纸] 坏文件（随机字节的 .png）：CanOpen=false、ReadPixelSize=null、Load=null",
            !canOpenBad && !badSizeKnown && !badLoaded,
            $"CanOpen={canOpenBad} 尺寸存在={badSizeKnown} Load={badLoaded}");

        check("[屏幕贴纸] 不存在 / null / 空路径：CanOpen=false、Load=null（不抛异常）",
            !canOpenMissing && nullArgumentsOk,
            $"CanOpen(不存在)={canOpenMissing} 空参数安全={nullArgumentsOk}");
    }

    // ---------------------------------------------------------------- 配置序列化

    private static void CheckSerialization(CheckFn check)
    {
        var recentPath = SelfCheckScratch.MakeFile("screen-sticker-serialize.png");
        var config = new ScreenStickerConfig
        {
            LastFolder = @"C:\pictures",
            RecentLimit = 7,
            AutoRestoreOnStartup = false,
            DefaultTopmost = true,
            DefaultWidth = 500,
        };
        config.RememberRecent(recentPath);
        config.Stickers.Add(new StickerEntry
        {
            Path = ScreenStickerConfig.NormalizePath(recentPath),
            Left = 12,
            Top = 34,
            Width = 300,
            Height = 200,
            Topmost = true,
        });

        var json = config.ToJson();
        check("[屏幕贴纸] 配置 JSON 包含全部持久化字段（camelCase）",
            json.Contains("\"lastFolder\"")
            && json.Contains("\"recentLimit\"")
            && json.Contains("\"recentEntries\"")
            && json.Contains("\"stickers\"")
            && json.Contains("\"autoRestoreOnStartup\"")
            && json.Contains("\"defaultTopmost\"")
            && json.Contains("\"defaultWidth\""),
            json.Length > 240 ? json[..240] + "…" : json);

        check("[屏幕贴纸] 配置 JSON 不写 [JsonIgnore] 计算属性",
            !json.Contains("recentLimitEffective")
            && !json.Contains("recordsRecent")
            && !json.Contains("defaultWidthEffective"),
            "recentLimitEffective / recordsRecent / defaultWidthEffective 都不在 JSON 里");

        var parsed = JsonSerializer.Deserialize<ScreenStickerConfig>(json, JsonFileStore.Options);
        check("[屏幕贴纸] 配置 JSON 往返：设置 / 最近使用 / 贴纸位置都不丢",
            parsed is not null
            && parsed.RecentLimit == 7
            && !parsed.AutoRestoreOnStartup
            && parsed.DefaultTopmost
            && Near(parsed.DefaultWidth, 500)
            && parsed.RecentEntries.Count == 1
            && parsed.Stickers.Count == 1
            && Near(parsed.Stickers[0].Left, 12)
            && Near(parsed.Stickers[0].Top, 34)
            && Near(parsed.Stickers[0].Width, 300)
            && parsed.Stickers[0].Topmost,
            parsed is null ? "反序列化为 null" : $"贴纸 {parsed.Stickers.Count} 张 / 最近 {parsed.RecentEntries.Count} 条");
    }

    // ---------------------------------------------------------------- 辅助

    private static bool Near(double actual, double expected, double tolerance = Tolerance)
        => double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance;

    /// <summary>两个路径规范化后是否相同（Windows 忽略大小写）。</summary>
    private static bool SamePath(string? left, string? right)
        => string.Equals(
            ScreenStickerConfig.NormalizePath(left),
            ScreenStickerConfig.NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>贴纸至少要有 <see cref="StickerGeometry.MinVisible"/> 见方留在工作区里（可抓回来）。</summary>
    private static bool VisibleEnough(StickerBounds bounds, Rect screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return false;
        var visibleWidth = Math.Min(bounds.Left + bounds.Width, screen.Right) - Math.Max(bounds.Left, screen.Left);
        var visibleHeight = Math.Min(bounds.Top + bounds.Height, screen.Bottom) - Math.Max(bounds.Top, screen.Top);
        return visibleWidth >= Math.Min(StickerGeometry.MinVisible, bounds.Width) - Tolerance
            && visibleHeight >= Math.Min(StickerGeometry.MinVisible, bounds.Height) - Tolerance;
    }

    /// <summary>层级自检用的小列表（a 最靠下 → c 最靠上）。</summary>
    private static List<StickerEntry> CreateLayerList()
        => new()
        {
            new StickerEntry { Path = "a" },
            new StickerEntry { Path = "b" },
            new StickerEntry { Path = "c" },
        };

    private static bool IsLayerSequence(IReadOnlyList<StickerEntry> list, params string[] paths)
        => list.Count == paths.Length
           && list.Select(entry => entry.Path).SequenceEqual(paths);

    private static string DescribeLayer(IEnumerable<StickerEntry> list)
        => string.Join(" → ", list.Select(entry => entry.Path));

    /// <summary>在独立 STA 线程里跑一段（WPF 位图解码用）；返回异常或 null，超时不阻塞整组自检。</summary>
    private static Exception? RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            return new TimeoutException("图片解码 STA 线程 60 秒未结束");
        return failure;
    }
}
