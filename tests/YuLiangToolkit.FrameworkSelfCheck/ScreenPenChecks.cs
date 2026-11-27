using System.Windows.Media;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Tools.ScreenPen;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>「屏幕画笔」自检：元数据 / 快捷键 / 配置与调色板 / 颜色解析 / 覆盖状态协作。</summary>
internal static class ScreenPenChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        _ = report;
        CheckFn c = (name, ok, detail) => check(name, ok, detail);
        CheckMetadata(c);
        CheckShortcuts(c);
        CheckConfig(c);
        CheckColor(c);
        CheckOverlayState(c);
    }

    private static void CheckMetadata(CheckFn c)
    {
        var tool = new ScreenPenTool();
        c("[屏幕画笔] 工具元数据：Id / 名称 / 分类 / 排序",
            tool.Id == "screen-pen" && tool.Name == "屏幕画笔"
            && tool.Category == "桌面" && tool.Order == 173,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");
        c("[屏幕画笔] 分类路径为「桌面」，Emoji 为 ✏️",
            tool.CategoryPath.Count == 1 && tool.CategoryPath[0] == "桌面" && tool.EmojiIcon == "✏️",
            $"{string.Join("/", tool.CategoryPath)} / {tool.EmojiIcon}");
        c("[屏幕画笔] 标签覆盖屏幕批注 / 调色板 / 快捷键 / Epic Pen",
            new[] { "屏幕批注", "调色板", "快捷键", "Epic Pen" }.All(tool.Tags.Contains),
            string.Join(" / ", tool.Tags));
    }

    private static void CheckShortcuts(CheckFn c)
    {
        var tool = new ScreenPenTool();
        var shortcuts = tool.GetShortcuts();
        c("[屏幕画笔] 声明四条快捷键（显示 / 输入 / 清空 / 撤销）",
            shortcuts.Count == 4 && shortcuts.Select(s => s.Id).Distinct().Count() == 4,
            string.Join(" | ", shortcuts.Select(s => s.Id)));
        c("[屏幕画笔] 默认快捷键都有组合且启用",
            shortcuts.All(s => s.Enabled && !s.Primary.IsEmpty),
            string.Join(" | ", shortcuts.Select(s => s.DisplayText)));
        c("[屏幕画笔] 快捷键总开关默认开启", tool.ShortcutsEnabled);
    }

    private static void CheckConfig(CheckFn c)
    {
        var config = new ScreenPenConfig();
        c("[屏幕画笔] 默认调色板至少 20 色且无重复",
            config.PaletteColors.Count >= 20 && config.PaletteColors.Distinct(StringComparer.OrdinalIgnoreCase).Count() == config.PaletteColors.Count,
            $"{config.PaletteColors.Count} 色");
        c("[屏幕画笔] 默认画笔 / 荧光笔粗细在有效范围",
            config.PenThickness is >= 1 and <= 80 && config.HighlighterThickness is >= 4 and <= 80,
            $"{config.PenThickness} / {config.HighlighterThickness}");
        config.PenThickness = 999;
        config.HighlighterThickness = 1;
        c("[屏幕画笔] 粗细会夹到合法范围",
            config.PenThickness == 80 && config.HighlighterThickness == 4,
            $"{config.PenThickness} / {config.HighlighterThickness}");
        c("[屏幕画笔] 默认背景为透明 / 工具栏默认显示",
            config.Backdrop == "none" && config.ShowToolbar,
            $"{config.Backdrop} / {config.ShowToolbar}");
    }

    private static void CheckColor(CheckFn c)
    {
        var red = ScreenPenColor.Parse("#FF3B30");
        var alpha = ScreenPenColor.Parse("#80FF3B30");
        c("[屏幕画笔] 颜色解析支持 #RRGGBB / #AARRGGBB",
            red.R == 0xFF && red.G == 0x3B && red.B == 0x30 && alpha.A == 0x80,
            $"{ScreenPenColor.ToHex(red)} / {ScreenPenColor.ToHex(alpha)}");
        c("[屏幕画笔] 非法颜色值被识别", !ScreenPenColor.IsValid("#xyz") && ScreenPenColor.IsValid("#FF3B30"));
        c("[屏幕画笔] 颜色可以往返为十六进制", ScreenPenColor.Parse(ScreenPenColor.ToHex(alpha)) == alpha);
    }

    private static void CheckOverlayState(CheckFn c)
    {
        var changed = 0;
        EventHandler handler = (_, _) => changed++;
        ScreenOverlayState.ScreenshotActiveChanged += handler;
        try
        {
            ScreenOverlayState.SetScreenshotActive(true);
            var active = ScreenOverlayState.IsScreenshotActive;
            ScreenOverlayState.SetScreenshotActive(false);
            c("[屏幕画笔] 截图覆盖状态可切换并通知", active && !ScreenOverlayState.IsScreenshotActive && changed == 2, $"事件 {changed} 次");
        }
        finally
        {
            ScreenOverlayState.SetScreenshotActive(false);
            ScreenOverlayState.ScreenshotActiveChanged -= handler;
        }

        var tool = new ScreenPenTool();
        c("[屏幕画笔] 初始没有打开覆盖层", !tool.ViewModel.IsOverlayVisible);
    }
}
