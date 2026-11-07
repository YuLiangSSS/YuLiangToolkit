using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.MemeBoard;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「表情包收藏夹」自检：工具元数据 / 动作模式解析与安全回退 / 会话名归一与精确匹配 /
/// 目标窗口解析（多窗口优先级）/ 随机发送延迟夹取 / 图片扩展名 / 剪贴板导入文件名 /
/// 配置默认值与 JSON 往返。
///
/// <para><b>全部是纯逻辑</b>：不枚举真实窗口、不碰剪贴板、不调 Win32 / UIA、不操作 QQ，
/// 也不写仓库 Data/Config（配置往返在 <c>YLT_HOME</c> 指向的临时数据根里走 ToolConfigStore）。</para>
/// </summary>
internal static class MemeBoardChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckActionModes(assert);
        CheckChatMatching(assert);
        CheckResolveTarget(assert);
        CheckTiming(assert);
        CheckFiles(assert);
        CheckWindowInfo(assert);
        CheckConfig(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new MemeBoardTool();
        check("[表情包收藏夹] 工具 Id / 名称 / 分类 / 排序符合约定，且实现了 ITool",
            tool.Id == "meme-board" && tool.Name == "表情包收藏夹"
            && tool.Category == "系统" && tool.Order == 45
            && tool is ITool,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[表情包收藏夹] 主页图标非空、标签含「表情包」「QQ」「剪贴板」",
            tool.EmojiIcon.Length > 0
            && tool.Tags.Contains("表情包") && tool.Tags.Contains("收藏")
            && tool.Tags.Contains("QQ") && tool.Tags.Contains("剪贴板"),
            string.Join(" / ", tool.Tags) + " · " + tool.EmojiIcon);

        check("[表情包收藏夹] 描述写明 QQ / 剪贴板 / 自动发送 / 仅复制，能当主页搜索文案",
            tool.Description.Contains("QQ") && tool.Description.Contains("剪贴板")
            && tool.Description.Contains("自动发送") && tool.Description.Contains("表情包"),
            tool.Description);
    }

    // ---------------------------------------------------------------- 动作模式

    private static void CheckActionModes(CheckFn check)
    {
        check("[表情包收藏夹] Parse：paste / PASTE / 首尾空白都解析成「粘贴到 QQ」",
            MemeActionModes.Parse("paste") == MemeActionMode.PasteToQq
            && MemeActionModes.Parse("PASTE") == MemeActionMode.PasteToQq
            && MemeActionModes.Parse(" Paste ") == MemeActionMode.PasteToQq,
            "paste 组");

        check("[表情包收藏夹] Parse：clipboard / CLIPBOARD / 首尾空白都解析成「仅复制」",
            MemeActionModes.Parse("clipboard") == MemeActionMode.ClipboardOnly
            && MemeActionModes.Parse("CLIPBOARD") == MemeActionMode.ClipboardOnly
            && MemeActionModes.Parse(" clipboard ") == MemeActionMode.ClipboardOnly,
            "clipboard 组");

        check("[表情包收藏夹] Parse：null / 空 / 纯空白 / 未知值 / 中文一律安全回退「仅复制」",
            MemeActionModes.Parse(null) == MemeActionMode.ClipboardOnly
            && MemeActionModes.Parse("") == MemeActionMode.ClipboardOnly
            && MemeActionModes.Parse("   ") == MemeActionMode.ClipboardOnly
            && MemeActionModes.Parse("paste-to-qq") == MemeActionMode.ClipboardOnly
            && MemeActionModes.Parse("粘贴") == MemeActionMode.ClipboardOnly
            && MemeActionModes.Parse("PasteToQq") == MemeActionMode.ClipboardOnly,
            "未知值不碰 QQ");

        check("[表情包收藏夹] ToConfigValue：两个模式各自映射到固定的干净配置值",
            MemeActionModes.ToConfigValue(MemeActionMode.PasteToQq) == "paste"
            && MemeActionModes.ToConfigValue(MemeActionMode.ClipboardOnly) == "clipboard"
            && MemeActionModes.Parse(MemeActionModes.ToConfigValue(MemeActionMode.PasteToQq))
                == MemeActionMode.PasteToQq
            && MemeActionModes.Parse(MemeActionModes.ToConfigValue(MemeActionMode.ClipboardOnly))
                == MemeActionMode.ClipboardOnly,
            $"{MemeActionModes.PasteToQqValue} / {MemeActionModes.ClipboardOnlyValue}");

        check("[表情包收藏夹] DisplayName：两种模式有不同中文说明，覆盖「QQ」与「仅复制」",
            MemeActionModes.DisplayName(MemeActionMode.PasteToQq).Contains("QQ")
            && MemeActionModes.DisplayName(MemeActionMode.ClipboardOnly).Contains("仅复制")
            && MemeActionModes.DisplayName(MemeActionMode.PasteToQq)
                != MemeActionModes.DisplayName(MemeActionMode.ClipboardOnly),
            $"{MemeActionModes.DisplayName(MemeActionMode.PasteToQq)} / {MemeActionModes.DisplayName(MemeActionMode.ClipboardOnly)}");
    }

    // ---------------------------------------------------------------- 会话名归一 / 匹配

    private static void CheckChatMatching(CheckFn check)
    {
        check("[表情包收藏夹] Normalize：去掉首尾与中间空白，CJK 保留",
            ChatTargetMatcher.Normalize("  测试 群  ") == "测试群"
            && ChatTargetMatcher.Normalize("测试群\n\t") == "测试群"
            && ChatTargetMatcher.Normalize("测 试 群") == "测试群",
            ChatTargetMatcher.Normalize("  测试 群  "));

        check("[表情包收藏夹] Normalize：去掉 emoji / 标点装饰，数字保留",
            ChatTargetMatcher.Normalize("🌸测试群🌺") == "测试群"
            && ChatTargetMatcher.Normalize("测试群（2）") == "测试群2"
            && ChatTargetMatcher.Normalize("【测试群】🎉") == "测试群",
            ChatTargetMatcher.Normalize("🌸测试群🌺"));

        check("[表情包收藏夹] Normalize：null / 空 / 纯空白返回空串；ASCII 保留大小写",
            ChatTargetMatcher.Normalize(null) == ""
            && ChatTargetMatcher.Normalize("") == ""
            && ChatTargetMatcher.Normalize("   ") == ""
            && ChatTargetMatcher.Normalize("🐟") == ""
            && ChatTargetMatcher.Normalize("Test Group") == "TestGroup",
            "空值安全");

        check("[表情包收藏夹] IsMatch：装饰 / 空白不同但归一后相同即为 true",
            ChatTargetMatcher.IsMatch("🌸测试群🌺", "测试群")
            && ChatTargetMatcher.IsMatch("测试 群", "测试群")
            && ChatTargetMatcher.IsMatch("【测试群】", "🌸测试群🌺"),
            "装饰不影响匹配");

        check("[表情包收藏夹] IsMatch：忽略大小写（ASCII），中文照常精确匹配",
            ChatTargetMatcher.IsMatch("TestGroup", "testgroup")
            && ChatTargetMatcher.IsMatch("TEST GROUP", "testgroup")
            && ChatTargetMatcher.IsMatch("测试群", "测试群"),
            "大小写");

        check("[表情包收藏夹] IsMatch：只做精确匹配，前后缀不同 / 空值 / null 都不算命中",
            !ChatTargetMatcher.IsMatch("测试群2", "测试群")
            && !ChatTargetMatcher.IsMatch("测试", "测试群")
            && !ChatTargetMatcher.IsMatch("测试群", "")
            && !ChatTargetMatcher.IsMatch("", "测试群")
            && !ChatTargetMatcher.IsMatch(null, "测试群")
            && !ChatTargetMatcher.IsMatch("测试群", null)
            && !ChatTargetMatcher.IsMatch("", ""),
            "绝不模糊包含");
    }

    // ---------------------------------------------------------------- 目标窗口解析

    private static void CheckResolveTarget(CheckFn check)
    {
        var single = Win("测试群", handle: 100, title: "测试群", minimized: false);

        check("[表情包收藏夹] Resolve：窗口列表为 null / 空时返回 null",
            ChatTargetMatcher.Resolve(null, "测试群") is null
            && ChatTargetMatcher.Resolve(Array.Empty<QqWindowInfo>(), "测试群") is null);

        check("[表情包收藏夹] Resolve：没保存目标（空 / 纯空白）返回 null，即使只有一个窗口也不猜",
            ChatTargetMatcher.Resolve(new[] { single }, "") is null
            && ChatTargetMatcher.Resolve(new[] { single }, null) is null
            && ChatTargetMatcher.Resolve(new[] { single }, "   ") is null,
            "空目标不猜");

        check("[表情包收藏夹] Resolve：会话名精确命中时返回同一个窗口",
            ChatTargetMatcher.Resolve(new[] { single }, "测试群") is { } hit
            && hit.Handle == single.Handle,
            $"handle={single.Handle}");

        var minimized = Win("测试群", handle: 300, title: "测试群", minimized: true);
        var normal = Win("测试群", handle: 400, title: "测试群", minimized: false);
        check("[表情包收藏夹] Resolve：多个同名窗口时优先非最小化的",
            ChatTargetMatcher.Resolve(new[] { minimized, normal }, "测试群") is { } preferNormal
            && preferNormal.Handle == normal.Handle
            && ChatTargetMatcher.Resolve(new[] { normal, minimized }, "测试群") is { } preferNormal2
            && preferNormal2.Handle == normal.Handle,
            "非最小化优先");

        var normalHigh = Win("测试群", handle: 900, title: "测试群", minimized: false);
        var normalLow = Win("测试群", handle: 500, title: "测试群", minimized: false);
        check("[表情包收藏夹] Resolve：都是非最小化时按句柄升序取小的（结果稳定）",
            ChatTargetMatcher.Resolve(new[] { normalHigh, normalLow }, "测试群") is { } byHandle
            && byHandle.Handle == normalLow.Handle);

        var minHigh = Win("测试群", handle: 800, title: "测试群", minimized: true);
        var minLow = Win("测试群", handle: 600, title: "测试群", minimized: true);
        check("[表情包收藏夹] Resolve：都最小化时仍按句柄升序取小的",
            ChatTargetMatcher.Resolve(new[] { minHigh, minLow }, "测试群") is { } minHandle
            && minHandle.Handle == minLow.Handle);

        check("[表情包收藏夹] Resolve：会话名只差前后缀（测试群2 / 测试）时不命中，返回 null",
            ChatTargetMatcher.Resolve(new[] { Win("测试群2"), Win("测试") }, "测试群") is null);

        var skipNoName = ChatTargetMatcher.Resolve(new[] { Win(""), Win("测试群") }, "测试群");
        check("[表情包收藏夹] Resolve：没有会话名（读不到）的窗口被跳过，不会误当目标",
            skipNoName is not null && skipNoName.ChatName == "测试群",
            skipNoName?.ChatName ?? "null");
    }

    // ---------------------------------------------------------------- 随机发送延迟

    private static void CheckTiming(CheckFn check)
    {
        check("[表情包收藏夹] ClampDelayMs：低于下界（负 / 0 / 199）夹到 200ms",
            MemeTiming.ClampDelayMs(int.MinValue) == 200
            && MemeTiming.ClampDelayMs(-1) == 200
            && MemeTiming.ClampDelayMs(0) == 200
            && MemeTiming.ClampDelayMs(199) == 200
            && MemeTiming.ClampDelayMs(200) == 200,
            $"[{MemeTiming.MinDelayMs}, {MemeTiming.MaxDelayMs}]");

        check("[表情包收藏夹] ClampDelayMs：高于上界（10001 / int.MaxValue）夹到 10000ms",
            MemeTiming.ClampDelayMs(10_000) == 10_000
            && MemeTiming.ClampDelayMs(10_001) == 10_000
            && MemeTiming.ClampDelayMs(int.MaxValue) == 10_000);

        check("[表情包收藏夹] NormalizeRange：上下限填反会自动交换",
            MemeTiming.NormalizeRange(1500, 500) == (500, 1500)
            && MemeTiming.NormalizeRange(5000, 1000) == (1000, 5000)
            && MemeTiming.NormalizeRange(500, 500) == (500, 500));

        check("[表情包收藏夹] NormalizeRange：各自先夹取再保证 Min <= Max",
            MemeTiming.NormalizeRange(0, 99_999) == (MemeTiming.MinDelayMs, MemeTiming.MaxDelayMs)
            && MemeTiming.NormalizeRange(99_999, 0) == (MemeTiming.MinDelayMs, MemeTiming.MaxDelayMs),
            "越界参数");

        var random = new Random(20250101);
        var inRange = true;
        var minSeen = int.MaxValue;
        var maxSeen = int.MinValue;
        for (var i = 0; i < 300; i++)
        {
            var delay = MemeTiming.PickDelayMs(500, 1500, random);
            if (delay < 500 || delay > 1500) inRange = false;
            minSeen = Math.Min(minSeen, delay);
            maxSeen = Math.Max(maxSeen, delay);
        }
        check("[表情包收藏夹] PickDelayMs：固定 Random 连续 300 次都落在 [min, max] 内（闭区间）",
            inRange && minSeen >= 500 && maxSeen <= 1500,
            $"{minSeen} ~ {maxSeen}");

        check("[表情包收藏夹] PickDelayMs：min == max 时直接返回该值；反转区间也能正确取",
            MemeTiming.PickDelayMs(700, 700, random) == 700
            && MemeTiming.PickDelayMs(700, 700) == 700
            && MemeTiming.PickDelayMs(1500, 500, new Random(7)) is >= 500 and <= 1500);

        check("[表情包收藏夹] PickDelayMs：越界输入也会先夹到 200~10000ms 再取",
            MemeTiming.PickDelayMs(-100, 99_999, new Random(11)) is >= MemeTiming.MinDelayMs
                and <= MemeTiming.MaxDelayMs);
    }

    // ---------------------------------------------------------------- 图片文件规则

    private static void CheckFiles(CheckFn check)
    {
        check("[表情包收藏夹] IsSupportedImage：png / jpg / jpeg / gif / bmp 都支持",
            MemeFiles.IsSupportedImage("a.png") && MemeFiles.IsSupportedImage("b.jpg")
            && MemeFiles.IsSupportedImage("c.jpeg") && MemeFiles.IsSupportedImage("d.gif")
            && MemeFiles.IsSupportedImage("e.bmp"));

        check("[表情包收藏夹] IsSupportedImage：大小写与首尾空白不影响判断",
            MemeFiles.IsSupportedImage("A.PNG") && MemeFiles.IsSupportedImage("B.JpG")
            && MemeFiles.IsSupportedImage(@"C:\表情包\测试.GIF")
            && MemeFiles.IsSupportedImage("  C:\\表情包\\带 空格.BMP  "));

        check("[表情包收藏夹] IsSupportedImage：webp / txt / 无扩展名 / 尾部点都不支持",
            !MemeFiles.IsSupportedImage("a.webp") && !MemeFiles.IsSupportedImage("b.WebP")
            && !MemeFiles.IsSupportedImage("a.txt")
            && !MemeFiles.IsSupportedImage(@"C:\表情包\Makefile")
            && !MemeFiles.IsSupportedImage(@"C:\表情包\name."));

        check("[表情包收藏夹] IsSupportedImage：null / 空 / 纯空白返回 false",
            !MemeFiles.IsSupportedImage(null)
            && !MemeFiles.IsSupportedImage("")
            && !MemeFiles.IsSupportedImage("   "));

        check("[表情包收藏夹] ClipboardImportName：剪贴板-<8位日期>-<6位时间>.png",
            MemeFiles.ClipboardImportName(new DateTime(2025, 1, 2, 3, 4, 5)) == "剪贴板-20250102-030405.png"
            && Regex.IsMatch(MemeFiles.ClipboardImportName(DateTime.Now), @"^剪贴板-\d{8}-\d{6}\.png$"),
            MemeFiles.ClipboardImportName(new DateTime(2025, 1, 2, 3, 4, 5)));

        check("[表情包收藏夹] ClipboardImportName：不同秒生成不同文件名，不会互相覆盖",
            MemeFiles.ClipboardImportName(new DateTime(2025, 1, 2, 3, 4, 5))
                != MemeFiles.ClipboardImportName(new DateTime(2025, 1, 2, 3, 4, 6))
            && MemeFiles.FileDialogFilter.Contains("*.png")
            && MemeFiles.FileDialogFilter.Contains("*.gif")
            && MemeFiles.FileDialogFilter.Contains("*.bmp"));
    }

    // ---------------------------------------------------------------- 窗口下拉文案

    private static void CheckWindowInfo(CheckFn check)
    {
        var separate = Win("测试群", title: "测试群");
        check("[表情包收藏夹] QqWindowInfo：独立聊天窗口的 DisplayText 含会话名 + 「QQ 窗口」",
            separate.HasChatName && separate.DisplayText.Contains("测试群")
            && separate.DisplayText.Contains("QQ 窗口")
            && !separate.DisplayText.Contains("主窗口"),
            separate.DisplayText);

        var main = Win("测试群", title: "QQ");
        check("[表情包收藏夹] QqWindowInfo：QQ 主窗口（标题固定「QQ」）显示「QQ 主窗口」",
            main.DisplayText.Contains("测试群") && main.DisplayText.Contains("QQ 主窗口"),
            main.DisplayText);

        var minimized = Win("测试群", title: "测试群", minimized: true);
        check("[表情包收藏夹] QqWindowInfo：最小化窗口的 DisplayText 带「已最小化」",
            minimized.DisplayText.Contains("测试群") && minimized.DisplayText.Contains("已最小化"),
            minimized.DisplayText);

        var unknown = Win("", title: "QQ");
        check("[表情包收藏夹] QqWindowInfo：读不到会话名时显示「未识别会话」并退回窗口标题",
            !unknown.HasChatName && unknown.DisplayText.Contains("未识别会话")
            && unknown.DisplayText.Contains("QQ"),
            unknown.DisplayText);
    }

    // ---------------------------------------------------------------- 配置 / JSON

    private static void CheckConfig(CheckFn check)
    {
        var config = new MemeBoardConfig();
        check("[表情包收藏夹] 配置默认值：粘贴模式 / 空目标 / 自动发送开 / 0.5~1.5 秒 / 校验开",
            config.ActionMode == MemeActionModes.PasteToQqValue
            && config.TargetChat == "" && config.AutoSend
            && config.SendDelayMinMs == 500 && config.SendDelayMaxMs == 1500
            && config.VerifyTargetChat,
            $"{config.ActionMode} / {config.SendDelayMinMs}~{config.SendDelayMaxMs}");

        check("[表情包收藏夹] 配置计算属性：IsPasteToQq / !IsClipboardOnly / !HasTargetChat / 延迟文案",
            config.IsPasteToQq && !config.IsClipboardOnly && !config.HasTargetChat
            && config.DelayRangeText.Contains("0.5") && config.DelayRangeText.Contains("1.5")
            && config.DelayRangeText.Contains("随机"),
            config.DelayRangeText);

        config.ActionMode = " PASTE ";
        var upperPaste = config.ActionMode;
        config.ActionMode = "   ";
        var blankMode = config.ActionMode;
        config.ActionMode = "PasteToQq";
        var unknownMode = config.ActionMode;
        check("[表情包收藏夹] 配置 ActionMode setter：未知 / 空白规范成 clipboard，大小写空白规范成 paste",
            upperPaste == "paste" && blankMode == "clipboard" && unknownMode == "clipboard"
            && config.IsClipboardOnly && !config.IsPasteToQq,
            $"{upperPaste} / {blankMode} / {unknownMode}");

        var delayConfig = new MemeBoardConfig();
        delayConfig.SendDelayMinMs = 50;
        var clampedMin = delayConfig.SendDelayMinMs;
        delayConfig.SendDelayMinMs = 3000;
        check("[表情包收藏夹] 配置延迟：下限夹到 200ms；下限超过上限时上限跟着抬高",
            clampedMin == 200 && delayConfig.SendDelayMinMs == 3000
            && delayConfig.SendDelayMaxMs == 3000,
            $"{clampedMin} / max={delayConfig.SendDelayMaxMs}");

        delayConfig.SendDelayMaxMs = 1000;
        check("[表情包收藏夹] 配置延迟：上限夹到 10000ms；上限低于下限时下限跟着压低",
            delayConfig.SendDelayMaxMs == 1000 && delayConfig.SendDelayMinMs == 1000);

        var secondConfig = new MemeBoardConfig();
        secondConfig.DelayMinSeconds = 1.2;
        var secondsRoundTrip = secondConfig.SendDelayMinMs == 1200
            && secondConfig.DelayMinSeconds == 1.2;
        secondConfig.DelayMaxSeconds = 99;
        check("[表情包收藏夹] 配置延迟秒属性往返：1.2 秒 → 1200ms；超范围秒数按 10000ms 夹取",
            secondsRoundTrip && secondConfig.SendDelayMaxMs == 10_000,
            $"min={secondConfig.SendDelayMinMs} / max={secondConfig.SendDelayMaxMs}");

        var chatConfig = new MemeBoardConfig();
        var beforeChat = chatConfig.HasTargetChat;
        chatConfig.TargetChat = "测试群";
        check("[表情包收藏夹] 配置 TargetChat：空 = 未选（HasTargetChat false），填了会话名 = 已选",
            !beforeChat && chatConfig.HasTargetChat && chatConfig.TargetChat == "测试群");

        var jsonConfig = new MemeBoardConfig
        {
            ActionMode = "clipboard",
            TargetChat = "测试群",
            AutoSend = false,
            SendDelayMinMs = 800,
            SendDelayMaxMs = 2200,
            VerifyTargetChat = false,
        };
        var json = JsonFileStore.Serialize(jsonConfig);
        check("[表情包收藏夹] 配置 JSON：只落盘持久化字段，[JsonIgnore] 计算属性不出现",
            json.Contains("\"actionMode\"") && json.Contains("\"targetChat\"")
            && json.Contains("\"autoSend\"") && json.Contains("\"sendDelayMinMs\"")
            && json.Contains("\"sendDelayMaxMs\"") && json.Contains("\"verifyTargetChat\"")
            && !json.Contains("isPasteToQq", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("isClipboardOnly", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("delayMinSeconds", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("delayRangeText", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("hasTargetChat", StringComparison.OrdinalIgnoreCase),
            "camelCase");

        var restored = JsonSerializer.Deserialize<MemeBoardConfig>(json, JsonFileStore.Options);
        check("[表情包收藏夹] 配置 JSON 往返：模式 / 目标 / 自动发送 / 延迟 / 校验都保留，中文不转义",
            restored is not null && restored.ActionMode == "clipboard"
            && restored.TargetChat == "测试群" && !restored.AutoSend
            && restored.SendDelayMinMs == 800 && restored.SendDelayMaxMs == 2200
            && !restored.VerifyTargetChat && restored.IsClipboardOnly
            && json.Contains("测试群"),
            restored is null ? "反序列化为 null" : $"{restored.ActionMode} / {restored.TargetChat}");

        // 配置持久化：在自检临时数据根（YLT_HOME）里走一遍 ToolConfigStore 的 Load → SaveNow → 读回
        var store = ToolConfigStore.For("meme-board");
        var loaded = store.Load<MemeBoardConfig>();
        var pathOk = Path.GetFileName(Path.GetDirectoryName(loaded.FilePath)!) == "meme-board"
                     && Path.GetFileName(loaded.FilePath) == "config.json";
        loaded.ActionMode = "clipboard";
        loaded.TargetChat = "测试群";
        loaded.AutoSend = false;
        loaded.SendDelayMinMs = 800;
        loaded.SendDelayMaxMs = 2200;
        loaded.VerifyTargetChat = false;
        loaded.SaveNow();
        var reread = JsonFileStore.Read<MemeBoardConfig>(loaded.FilePath);
        check("[表情包收藏夹] 配置持久化：落在 Config\\meme-board\\config.json，SaveNow 后能读回",
            pathOk && reread is not null
            && reread.ActionMode == "clipboard" && reread.TargetChat == "测试群"
            && !reread.AutoSend && reread.SendDelayMinMs == 800 && reread.SendDelayMaxMs == 2200
            && !reread.VerifyTargetChat,
            loaded.FilePath);

        // 还原成默认，避免影响下一轮 / 其它检查
        loaded.ActionMode = MemeActionModes.PasteToQqValue;
        loaded.TargetChat = "";
        loaded.AutoSend = true;
        loaded.SendDelayMinMs = 500;
        loaded.SendDelayMaxMs = 1500;
        loaded.VerifyTargetChat = true;
        loaded.SaveNow();
        store.StopWatching();
    }

    // ---------------------------------------------------------------- 测试数据

    /// <summary>造一个 QQ 窗口数据（不涉及任何真实句柄 / 系统调用）。</summary>
    private static QqWindowInfo Win(
        string chatName,
        int handle = 1,
        string title = "测试群",
        bool minimized = false)
        => new(new IntPtr(handle), 1000, "QQ", title, chatName, minimized);
}
