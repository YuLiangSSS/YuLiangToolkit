using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.TextConverter;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「文本转换器」自检：每种转换的实际效果、可逆性（转两次回得来）、
/// 非法输入的错误处理、转换注册表、自定义快捷键的数据模型与配置往返。
///
/// <para>纯逻辑：转换是纯函数；<b>不碰剪贴板、不模拟按键</b>（那两件事没法在自检里安全做）。
/// 「选中 → 转换 → 粘贴」这条链路靠人工冒烟验收。</para>
/// </summary>
internal static class TextConverterChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckRegistry(assert);
        CheckCaseConversions(assert);
        CheckStyleConversions(assert);
        CheckEmoji(assert);
        CheckEncoding(assert);
        CheckCleanup(assert);
        CheckShortcuts(assert);
        CheckConfig(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new TextConverterTool();
        check("[文本转换器] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "text-converter" && tool.Name == "文本转换器" && tool.EmojiIcon.Length > 0
            && tool.Category == "文档" && tool.Order == 175 && tool is ITool,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[文本转换器] 标签含文本转换 / 火星文 / Emoji",
            tool.Tags.Contains("文本转换") && tool.Tags.Contains("火星文") && tool.Tags.Contains("Emoji"),
            string.Join(" / ", tool.Tags));

        check("[文本转换器] 描述写明自己绑快捷键、选中就地转换",
            tool.Description.Contains("快捷键") && tool.Description.Contains("选中"),
            tool.Description);
    }

    // ---------------------------------------------------------------- 注册表

    private static void CheckRegistry(CheckFn check)
    {
        check("[文本转换器] 转换种类足够丰富（>= 20 种）",
            TextTransformer.All.Count >= 20, TextTransformer.All.Count.ToString());

        check("[文本转换器] 每个转换的 Id 唯一、名字 / 分组 / 说明都不为空",
            TextTransformer.All.Select(c => c.Id).Distinct().Count() == TextTransformer.All.Count
            && TextTransformer.All.All(c => c.Name.Length > 0 && c.Group.Length > 0 && c.Description.Length > 0),
            string.Join(" / ", TextTransformer.All.Select(c => c.Group).Distinct()));

        check("[文本转换器] 按 Id 能查到转换，查不到返回 null",
            TextTransformer.Find("emoji")?.Name == "转 Emoji"
            && TextTransformer.Find("不存在的") is null
            && TextTransformer.Find(null) is null,
            TextTransformer.Find("emoji")?.Name ?? "null");

        var unknownResult = TextTransformer.Apply("abc", "不存在的");
        check("[文本转换器] 不认识的转换 Id → 原样返回并报错（不抛异常）",
            unknownResult.Error.Length > 0 && unknownResult.Result == "abc",
            unknownResult.Error);
    }

    // ---------------------------------------------------------------- 大小写

    private static void CheckCaseConversions(CheckFn check)
    {
        check("[文本转换器] 全大写 / 全小写",
            Apply("Hello World", "upper") == "HELLO WORLD"
            && Apply("Hello World", "lower") == "hello world",
            Apply("Hello World", "upper"));

        check("[文本转换器] 词首大写（hello world → Hello World）",
            Apply("hello world", "title") == "Hello World",
            Apply("hello world", "title"));

        check("[文本转换器] 句首大写（只把第一个字母大写，其余小写）",
            Apply("hELLO wORLD", "sentence") == "Hello world",
            Apply("hELLO wORLD", "sentence"));

        check("[文本转换器] 反转大小写（aBc → AbC）",
            Apply("aBc", "swapcase") == "AbC",
            Apply("aBc", "swapcase"));
    }

    // ---------------------------------------------------------------- 风格

    private static void CheckStyleConversions(CheckFn check)
    {
        check("[文本转换器] 转简体 / 繁体（爱 → 愛）",
            Apply("我爱你", "traditional") == "我愛你",
            Apply("我爱你", "traditional"));

        check("[文本转换器] 繁体可以再转回简体（可逆）",
            Apply(Apply("爱国", "traditional"), "traditional") == "爱国",
            Apply(Apply("爱国", "traditional"), "traditional"));

        check("[文本转换器] 火星文（我 → 莪、你 → 伱）",
            Apply("我的你", "mars") == "莪嘚伱",
            Apply("我的你", "mars"));

        check("[文本转换器] 圈字（数字 / 字母变圆圈）",
            Apply("a1", "circled") == "ⓐ①",
            Apply("a1", "circled"));

        check("[文本转换器] 全角（ABC123 → ＡＢＣ１２３）",
            Apply("ABC123", "fullwidth") == "ＡＢＣ１２３",
            Apply("ABC123", "fullwidth"));

        check("[文本转换器] 全角可以转回半角（可逆）",
            Apply(Apply("abc", "fullwidth"), "fullwidth") == "abc",
            Apply(Apply("abc", "fullwidth"), "fullwidth"));

        check("[文本转换器] 小号大写字母（ab → ᴀʙ）",
            Apply("ab", "smallcaps") == "ᴀʙ",
            Apply("ab", "smallcaps"));

        check("[文本转换器] 花体字母（ab → 𝓪𝓫）",
            Apply("ab", "script").Length > 2
            && Apply("ab", "script") != "ab",
            Apply("ab", "script"));

        check("[文本转换器] 倒置文字（会整串反转）",
            Apply("ab", "upsidedown") == "qɐ",
            Apply("ab", "upsidedown"));

        check("[文本转换器] 倒置可以再转回来（可逆）",
            Apply(Apply("abc", "upsidedown"), "upsidedown") == "abc",
            Apply(Apply("abc", "upsidedown"), "upsidedown"));
    }

    // ---------------------------------------------------------------- Emoji

    private static void CheckEmoji(CheckFn check)
    {
        check("[文本转换器] 转 Emoji：用户举的例子「牛马 → 🐮🐴」",
            Apply("牛马", "emoji") == "🐮🐴",
            Apply("牛马", "emoji"));

        check("[文本转换器] 转 Emoji：多字词优先（开心 → 😄，不被拆成两个字）",
            Apply("开心", "emoji") == "😄",
            Apply("开心", "emoji"));

        check("[文本转换器] 转 Emoji：对不上的字原样保留（牛马很快乐 → 🐮🐴很快乐）",
            Apply("牛马很快乐", "emoji") == "🐮🐴很快乐",
            Apply("牛马很快乐", "emoji"));

        // 逐字模式不做多字词匹配：「开心」不会被当成 😄，但「心」这个单字能对上 ❤️
        var charByChar = Apply("开心", "emoji-space");
        check("[文本转换器] 逐字 Emoji 模式不匹配多字词（开心 → 开❤️，不是 😄）",
            charByChar == "开❤️" && charByChar != "😄",
            charByChar);

        check("[文本转换器] 逐字 Emoji 模式逐字替换（牛马 → 🐮🐴）",
            Apply("牛马", "emoji-space") == "🐮🐴",
            Apply("牛马", "emoji-space"));

        check("[文本转换器] 空文本转换是安全的（返回空、不报错）",
            TextTransformer.Apply("", "emoji") is { Result: "", Error: "" },
            "空");
    }

    // ---------------------------------------------------------------- 编码

    private static void CheckEncoding(CheckFn check)
    {
        var encoded = Apply("你好", "base64");
        check("[文本转换器] Base64 编码 / 解码往返",
            Apply(encoded, "base64-decode") == "你好",
            $"{encoded} → {Apply(encoded, "base64-decode")}");

        var bad = TextTransformer.Apply("这不是base64!!!", "base64-decode");
        check("[文本转换器] 非法 Base64 给出中文错误（不抛异常）",
            bad.Error.Length > 0 && bad.Result == "这不是base64!!!",
            bad.Error);

        check("[文本转换器] URL 编码 / 解码往返",
            Apply(Apply("你好 world", "url"), "url-decode") == "你好 world",
            Apply("你好 world", "url"));

        check("[文本转换器] 摩斯电码（sos → ... --- ...）",
            Apply("sos", "morse") == "... --- ...",
            Apply("sos", "morse"));

        check("[文本转换器] 摩斯电码：空格变词间隔、连续空白只留一个 /",
            Apply("a  b", "morse") == ".- / -...",
            Apply("a  b", "morse"));

        check("[文本转换器] 同音字替换（牛 → 妞）",
            Apply("牛", "homophone") == "妞",
            Apply("牛", "homophone"));
    }

    // ---------------------------------------------------------------- 清理

    private static void CheckCleanup(CheckFn check)
    {
        check("[文本转换器] 去首尾空格（每行）",
            Apply("  a  \n  b  ", "trim").Replace("\r\n", "\n") == "a\nb",
            Apply("  a  \n  b  ", "trim").Replace("\r\n", "\n"));

        check("[文本转换器] 去掉所有空格",
            Apply("a b  c\td", "no-space") == "abcd",
            Apply("a b  c\td", "no-space"));

        check("[文本转换器] 合并成一行",
            Apply("a\nb\r\nc", "no-newline") == "a b c",
            Apply("a\nb\r\nc", "no-newline"));

        check("[文本转换器] 去掉空行",
            Apply("a\n\n\nb\n  \nc", "no-empty-line").Replace("\r\n", "\n") == "a\nb\nc",
            Apply("a\n\n\nb\n  \nc", "no-empty-line").Replace("\r\n", "\n"));

        check("[文本转换器] 反转文字（按字符倒排）",
            Apply("abc", "reverse") == "cba",
            Apply("abc", "reverse"));

        check("[文本转换器] 字符去重（保留第一次出现）",
            Apply("aabbcc", "dedupe-chars") == "abc",
            Apply("aabbcc", "dedupe-chars"));
    }

    // ---------------------------------------------------------------- 自定义快捷键

    private static void CheckShortcuts(CheckFn check)
    {
        var shortcut = new ConvertShortcut { Hotkey = "Ctrl+Shift+T", ConversionId = "emoji" };

        check("[文本转换器] 快捷键条目：显示转换名 / 摘要 / 状态",
            shortcut.ConversionName == "转 Emoji"
            && shortcut.SummaryText.Contains("Ctrl+Shift+T")
            && shortcut.StateText == "已启用",
            shortcut.SummaryText);

        shortcut.Enabled = false;
        check("[文本转换器] 关掉后状态变「已禁用」",
            shortcut.StateText == "已禁用", shortcut.StateText);

        shortcut.Hotkey = "";
        check("[文本转换器] 没设快捷键时显示「未设置」",
            shortcut.ConversationDisplay == "未设置", shortcut.ConversationDisplay);

        var unknown = new ConvertShortcut { ConversionId = "不存在的" };
        check("[文本转换器] Id 认不出时显示「（未知转换）」",
            unknown.ConversionName == "（未知转换）", unknown.ConversionName);

        check("[文本转换器] 快捷键组合用 Core 的 KeyCombo 解析（Ctrl+Shift+T 合法）",
            KeyCombo.TryParse("Ctrl+Shift+T", out var combo) && combo.IsValidGlobalHotkey,
            combo.ToString());

        check("[文本转换器] 单条快捷键的 Id 自动生成且非空",
            !string.IsNullOrWhiteSpace(new ConvertShortcut().Id),
            "有 Id");
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new TextConverterConfig();
        check("[文本转换器] 配置默认值：还原剪贴板 / 快捷键开 / 默认转换 emoji",
            config.RestoreClipboard && config.ShortcutsEnabled
            && config.LastConversionId == "emoji" && config.Notification.Enabled,
            $"{config.RestoreClipboard} / {config.LastConversionId}");

        var store = YuLiangToolkit.Core.Configuration.ToolConfigStore
            .For(TextConverterConfig.ToolId, "selfcheck.json");
        try
        {
            var saved = store.Load<TextConverterConfig>();
            saved.Input = "测试文本";
            saved.LastConversionId = "mars";
            saved.RestoreClipboard = false;
            saved.Shortcuts.Clear();
            saved.Shortcuts.Add(new ConvertShortcut
            {
                Id = "fixed-id",
                Hotkey = "Ctrl+Alt+E",
                ConversionId = "emoji",
                Enabled = true,
            });

            var reloaded = YuLiangToolkit.Core.Configuration.ToolConfigStore
                .For(TextConverterConfig.ToolId, "selfcheck.json").Load<TextConverterConfig>();

            check("[文本转换器] 配置往返（输入 / 上次转换 / 还原开关）",
                reloaded.Input == "测试文本"
                && reloaded.LastConversionId == "mars"
                && !reloaded.RestoreClipboard,
                $"{reloaded.LastConversionId} / {reloaded.RestoreClipboard}");

            check("[文本转换器] 配置往返（自定义快捷键：组合 + 转换 + 启停）",
                reloaded.Shortcuts.Count == 1
                && reloaded.Shortcuts[0].Hotkey == "Ctrl+Alt+E"
                && reloaded.Shortcuts[0].ConversionId == "emoji"
                && reloaded.Shortcuts[0].Enabled
                && reloaded.EnabledShortcutCount == 1,
                reloaded.Shortcuts.Count > 0
                    ? $"{reloaded.Shortcuts[0].Hotkey} → {reloaded.Shortcuts[0].ConversionName}"
                    : "0 条");
        }
        finally
        {
            store.DeleteFile();
        }
    }

    /// <summary>执行一次转换，只取结果（失败时把错误也带出来方便排查）。</summary>
    private static string Apply(string text, string id)
    {
        var (result, error) = TextTransformer.Apply(text, id);
        return error.Length > 0 ? $"【错误：{error}】" : result;
    }
}
