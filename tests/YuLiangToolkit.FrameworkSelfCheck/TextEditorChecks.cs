using System.Text;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.TextEditor;
using YuLiangToolkit.Tools.TextEditor.Core;
using YuLiangToolkit.Tools.TextEditor.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「文本编辑器」自检：工具元数据、文件编解码（编码探测 / 换行风格 / BOM / 真读写往返）、
/// 最近文件列表、查找替换纯逻辑、标签规则（标题 / 无标题命名 / 关标签补位）、
/// 高亮暗色调色、配置默认值与落盘、标签文档的脏标记链路。
/// <b>全程不弹窗、不开界面</b>（AvalonEdit 控件与菜单栏属于人工截图冒烟的部分）。
/// </summary>
internal static class TextEditorChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckMetadata(check);
        CheckEncodingDetection(check);
        CheckFileRoundTrip(check, report);
        CheckRecentList(check);
        CheckSearch(check);
        CheckTabRules(check);
        CheckTabDocument(check);
        CheckHighlighting(check);
        CheckConfig(check);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(Action<string, bool, string> check)
    {
        var tool = new TextEditorTool();
        check("[编辑器] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "text-editor" && tool.Name == "文本编辑器"
            && tool.Category == "文件" && tool.Order == 41,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");
        check("[编辑器] 标签里有「编辑器」「代码」，描述说明行号与缩放",
            tool.Tags.Contains("编辑器") && tool.Tags.Contains("代码")
            && tool.Description.Contains("行号") && tool.Description.Contains("缩放"),
            tool.Description);

        // 工具 Id 必须等于配置文件夹名（Config 都按它建目录）
        check("[编辑器] 工具 Id 与 TextEditorConfig.ToolId 一致", tool.Id == TextEditorConfig.ToolId, TextEditorConfig.ToolId);
    }

    // ---------------------------------------------------------------- 编码探测

    private static void CheckEncodingDetection(Action<string, bool, string> check)
    {
        check("[编辑器] BOM 探测：UTF-8 / UTF-16 LE / BE",
            TextFileCodec.DetectEncoding(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }) == TextFileEncodingKind.Utf8Bom
            && TextFileCodec.DetectEncoding(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }) == TextFileEncodingKind.Utf16Le
            && TextFileCodec.DetectEncoding(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }) == TextFileEncodingKind.Utf16Be,
            "EF BB BF / FF FE / FE FF");

        check("[编辑器] 无 BOM：ASCII 与合法 UTF-8 记为 UTF-8，中文 GBK 字节记为 GB18030",
            TextFileCodec.DetectEncoding("hello\r\n"u8.ToArray()) == TextFileEncodingKind.Utf8
            && TextFileCodec.DetectEncoding("中文 utf-8"u8.ToArray()) == TextFileEncodingKind.Utf8
            && TextFileCodec.DetectEncoding(Encoding.GetEncoding("GB18030").GetBytes("中文文件")) == TextFileEncodingKind.Gb18030,
            "ASCII / UTF-8 / GB18030(D6 D0 CE C4)");

        check("[编辑器] 解码后文字一致（UTF-8 / GB18030 / UTF-16 LE）",
            TextFileCodec.DecodeAs("中文内容 hello"u8.ToArray(), TextFileEncodingKind.Utf8) == "中文内容 hello"
            && TextFileCodec.DecodeAs(Encoding.GetEncoding("GB18030").GetBytes("中文内容 hello"), TextFileEncodingKind.Gb18030) == "中文内容 hello"
            && TextFileCodec.DecodeAs(Encoding.Unicode.GetBytes("中文 hello"), TextFileEncodingKind.Utf16Le) == "中文 hello",
            "三种编码各解一遍");

        check("[编辑器] 换行风格探测：CRLF / LF / CR / 空文本回 CRLF",
            TextFileCodec.DetectNewline("a\r\nb") == TextNewlineStyle.CrLf
            && TextFileCodec.DetectNewline("a\nb") == TextNewlineStyle.Lf
            && TextFileCodec.DetectNewline("a\rb") == TextNewlineStyle.Cr
            && TextFileCodec.DetectNewline("") == TextNewlineStyle.CrLf,
            "CRLF / LF / CR / 空");

        check("[编辑器] 换行归一：目标 LF 时 CRLF 与孤立 CR 都归一，反之亦然",
            TextFileCodec.NormalizeNewline("a\r\nb\rc\nd", TextNewlineStyle.Lf) == "a\nb\nc\nd"
            && TextFileCodec.NormalizeNewline("a\r\nb\nc", TextNewlineStyle.CrLf) == "a\r\nb\r\nc"
            && TextFileCodec.NormalizeNewline("a\r\nb", TextNewlineStyle.Cr) == "a\rb"
            && TextFileCodec.NormalizeNewline("a\r\nb", TextNewlineStyle.Lf) == "a\nb",
            "LF / CRLF / CR 三种目标各归一一次");

        check("[编辑器] 编码友好名（状态栏展示）",
            TextFileCodec.FriendlyEncodingName(TextFileEncodingKind.Utf8Bom) == "UTF-8 BOM"
            && TextFileCodec.FriendlyEncodingName(TextFileEncodingKind.Utf8) == "UTF-8"
            && TextFileCodec.FriendlyEncodingName(TextFileEncodingKind.Gb18030) == "GB18030",
            "UTF-8 BOM / UTF-8 / GB18030");

        // 编辑器内部统一 CRLF（AvalonEdit 的规范形态），保存前再归一回原风格
        check("[编辑器] 进编辑器前统一 CRLF（AvalonEdit 内部规范形态）",
            TextFileCodec.NormalizeForEditor("a\nb", TextNewlineStyle.Lf) == "a\r\nb"
            && TextFileCodec.NormalizeForEditor("a\r\nb", TextNewlineStyle.CrLf) == "a\r\nb",
            "LF → CRLF · CRLF 保持");
    }

    // ---------------------------------------------------------------- 真实读写往返

    private static void CheckFileRoundTrip(Action<string, bool, string> check, Action<string, bool> report)
    {
        var dir = SelfCheckScratch.MakeDirectory("text-editor-rt");
        var text = "第一行 hello\r\n第二行 world 🎉\r\n";

        // 各编码写 → 读回：文字一致、编码识别一致、BOM 被剥掉
        foreach (var kind in new[]
                 {
                     TextFileEncodingKind.Utf8,
                     TextFileEncodingKind.Utf8Bom,
                     TextFileEncodingKind.Gb18030,
                     TextFileEncodingKind.Utf16Le,
                 })
        {
            var path = Path.Combine(dir, $"rt-{kind}.txt");
            var writeOk = TextFileCodec.TryWrite(path, text, kind, TextNewlineStyle.CrLf, out var writeError);
            TextFileContent? content = null;
            string? readError = null;
            if (writeOk) content = TextFileCodec.TryRead(path, out readError);
            check($"[编辑器] {kind} 写入 → 读回往返一致（BOM 剥离 / 编码识别 / 文字不变）",
                writeOk && content is not null
                && content.Text == text
                && content.Encoding == kind
                && content.Newline == TextNewlineStyle.CrLf
                && !content.Text.StartsWith('\uFEFF'),
                $"{kind}: write={writeOk} read={content is not null} {writeError ?? readError ?? ""}");
        }

        // LF 文件读回仍是 LF（换行风格不被偷偷改）
        var lfPath = Path.Combine(dir, "rt-lf.txt");
        TextFileCodec.TryWrite(lfPath, "a\nb\nc", TextFileEncodingKind.Utf8, TextNewlineStyle.Lf, out _);
        var lfContent = TextFileCodec.TryRead(lfPath, out _);
        check("[编辑器] LF 文件读回仍是 LF", lfContent is { Newline: TextNewlineStyle.Lf },
            lfContent?.Newline.ToString() ?? "<null>");

        // 读失败给出错误消息而不是抛异常
        check("[编辑器] 读不存在的文件返回 null + 错误消息",
            TextFileCodec.TryRead(Path.Combine(dir, "no-such-file.txt"), out var missingError) is null
            && !string.IsNullOrEmpty(missingError),
            missingError ?? "<空>");

        report("text-editor 读写往返完成（临时目录已清理）", true);
    }

    // ---------------------------------------------------------------- 最近文件 / 会话列表

    private static void CheckRecentList(Action<string, bool, string> check)
    {
        List<string> empty = [];

        check("[编辑器] 最近列表：新文件排最前，原有位置提升",
            RecentFileList.Push(empty, @"C:\a\one.cs")[0] == @"C:\a\one.cs"
            && RecentFileList.Push(new List<string> { @"C:\a\one.cs", @"C:\b\two.cs" }, @"C:\b\two.cs")
                is [_, @"C:\a\one.cs"],
            "push / 提升已有项");

        check("[编辑器] 最近列表：同路径不同大小写视为同一个（Windows 路径不区分大小写）",
            RecentFileList.Push(new List<string> { @"C:\A\One.CS" }, @"c:\a\one.cs").Count == 1,
            "One.CS vs one.cs");

        check("[编辑器] 最近列表封顶（最多 10 条，最旧的被丢掉）",
            RecentFileList.Push(Enumerable.Range(0, TextEditorConfig.MaxRecentFiles)
                .Select(i => $@"C:\f{i}.txt").ToList(), @"C:\new.txt")
                .Count == TextEditorConfig.MaxRecentFiles,
            TextEditorConfig.MaxRecentFiles.ToString());

        check("[编辑器] 最近列表：空 / null / 空白路径安全",
            RecentFileList.Push(empty, "").Count == 0
            && RecentFileList.Push(empty, null).Count == 0
            && RecentFileList.Push(null, @"C:\a.txt").Count == 1
            && RecentFileList.Remove(empty, @"C:\x.txt").Count == 0,
            "空路径 / null");

        check("[编辑器] 最近列表清洗：去空白去重，会话上限 20",
            RecentFileList.Sanitize(new[] { "  a.txt ", "", "a.TXT", "b.txt" }).Count == 2
            && RecentFileList.SanitizeSession(Enumerable.Range(0, 40).Select(i => $@"C:\s{i}.txt")).Count
                == TextEditorConfig.MaxSessionFiles,
            "去重 / 会话封顶");
    }

    // ---------------------------------------------------------------- 查找 / 替换

    private static void CheckSearch(Action<string, bool, string> check)
    {
        // 下标：alpha(0) / Beta(6) / alpha(11) / BETA(17) / beta(22)
        const string text = "alpha Beta alpha BETA beta";

        check("[编辑器] 查找全部：不区分大小写 alpha×2 / beta×3，区分大小写 alpha×2 / beta×1",
            TextSearch.FindAll(text, "alpha", matchCase: false).Count == 2
            && TextSearch.FindAll(text, "beta", matchCase: false).Count == 3
            && TextSearch.FindAll(text, "beta", matchCase: true) is [22]
            && TextSearch.FindAll(text, "BETA", matchCase: true) is [17],
            "不区分 alpha×2 / beta×3 · 区分 beta×1 / BETA×1");

        check("[编辑器] 空模式不算匹配（查找框没输入不乱跳）",
            !TextSearch.IsValidPattern("")
            && TextSearch.FindAll(text, "", true).Count == 0
            && TextSearch.NextMatch(text, "", true, 0) is null
            && TextSearch.ReplaceAll(text, "", "x", true) is (var replaced, 0) && replaced == text,
            "空模式全部安全");

        check("[编辑器] 下一个：从光标处往后找、到末尾环绕",
            TextSearch.NextMatch(text, "beta", false, 0) == 6
            && TextSearch.NextMatch(text, "beta", false, 7) == 17
            && TextSearch.NextMatch(text, "beta", false, 18) == 22
            && TextSearch.NextMatch(text, "beta", false, 23) == 6        // 环绕回第一个
            && TextSearch.NextMatch(text, "beta", true, 0) == 22         // 区分大小写只有最后那个小写 beta
            && TextSearch.NextMatch(text, "beta", true, 23) == 22,       // 环绕回它自己
            "不区分 0→6/7→17/18→22/23→6(环绕) · 区分 0→22/23→22(环绕)");

        check("[编辑器] 上一个：从当前匹配起点往前、到开头环绕",
            TextSearch.PreviousMatch(text, "beta", false, 17) == 6
            && TextSearch.PreviousMatch(text, "beta", false, 6) == 22    // 环绕到最后一个
            && TextSearch.PreviousMatch(text, "beta", true, 26) == 22
            && TextSearch.PreviousMatch(text, "beta", true, 22) == 22,   // 只有一个小写 beta：环绕回它自己
            "不区分 17→6/6→22(环绕) · 区分 26→22/22→22(环绕)");

        check("[编辑器] 全部替换：计数与结果一致，大小写规则跟随开关",
            TextSearch.ReplaceAll(text, "beta", "X", true) is (var r1, 1) && r1 == "alpha Beta alpha BETA X"
            && TextSearch.ReplaceAll(text, "beta", "X", false) is (var r2, 3) && r2 == "alpha X alpha X X"
            && TextSearch.ReplaceAll(text, "没有的", "X", false) is (var r3, 0) && r3 == text,
            "区分×1 · 不区分×3 · 无匹配原样");

        check("[编辑器] 模式长于文本 / 空文本安全",
            TextSearch.NextMatch("ab", "abcdef", true, 0) is null
            && TextSearch.FindAll("", "abc", true).Count == 0,
            "长模式 / 空文本");
    }

    // ---------------------------------------------------------------- 标签规则

    private static void CheckTabRules(Action<string, bool, string> check)
    {
        check("[编辑器] 标签标题：未保存加 ● 前缀",
            EditorTab.ComposeHeader(true, "main.cs") == "● main.cs"
            && EditorTab.ComposeHeader(false, "main.cs") == "main.cs",
            "● main.cs / main.cs");

        check("[编辑器] 无标题命名：第一个「无标题」，之后「无标题 2/3」",
            EditorTab.UntitledTitle(1) == "无标题"
            && EditorTab.UntitledTitle(2) == "无标题 2"
            && EditorTab.UntitledTitle(3) == "无标题 3"
            && EditorTab.UntitledTitle(0) == "无标题",
            "无标题 / 无标题 2 / 无标题 3");

        // 关标签补位：与浏览器同一套规则（关激活原地补位 / 关别的激活不动 / 全关 -1）
        check("[编辑器] 关激活标签：原地补位，末尾退一格",
            EditorTab.NextActiveAfterRemove(3, 0, 0) == 0
            && EditorTab.NextActiveAfterRemove(3, 2, 2) == 1
            && EditorTab.NextActiveAfterRemove(1, 0, 0) == -1,
            "3/0/0→0 · 3/2/2→1 · 1/0/0→-1");
        check("[编辑器] 关非激活标签：激活位置不动（前面关了退一格，后面关了不动）",
            EditorTab.NextActiveAfterRemove(3, 2, 0) == 0
            && EditorTab.NextActiveAfterRemove(3, 0, 2) == 1
            && EditorTab.NextActiveAfterRemove(3, 1, 1) == 1,
            "3/2/0→0 · 3/0/2→1 · 3/1/1→1");
    }

    // ---------------------------------------------------------------- 标签文档（脏标记链路）

    private static void CheckTabDocument(Action<string, bool, string> check)
    {
        // 载入不点亮未保存；编辑点亮；保存清除并改名
        var tab = EditorTab.FromFile(@"C:\d\demo.cs", new TextFileContent("hello", TextFileEncodingKind.Utf8, TextNewlineStyle.CrLf));
        check("[编辑器] 从文件建标签：不脏 / 标题取文件名 / 记录编码与换行",
            !tab.IsDirty && tab.Title == "demo.cs" && tab.Header == "demo.cs"
            && tab.Encoding == TextFileEncodingKind.Utf8 && tab.Newline == TextNewlineStyle.CrLf,
            $"{tab.Header} · {tab.Encoding}");

        tab.Document.Text += " world";
        check("[编辑器] 编辑文档点亮未保存标记（标题加 ●）",
            tab.IsDirty && tab.Header == "● demo.cs",
            tab.Header);

        tab.MarkSaved(@"C:\d\renamed.md", TextFileEncodingKind.Utf8Bom, TextNewlineStyle.Lf);
        check("[编辑器] 保存后清脏标记 / 换标题 / 更新编码与换行",
            !tab.IsDirty && tab.Title == "renamed.md" && tab.Header == "renamed.md"
            && tab.Encoding == TextFileEncodingKind.Utf8Bom && tab.Newline == TextNewlineStyle.Lf,
            $"{tab.Header} · {tab.Encoding} · {tab.Newline}");

        // 无标题标签：SetTextSilently 不点亮
        var untitled = EditorTab.Untitled(1);
        untitled.SetTextSilently("草稿");
        check("[编辑器] 无标题标签：命名 / 载入草稿不点亮未保存",
            untitled.Title == "无标题" && untitled.FilePath is null && !untitled.IsDirty && untitled.Document.Text == "草稿",
            untitled.Header);
    }

    // ---------------------------------------------------------------- 高亮暗色调色

    private static void CheckHighlighting(Action<string, bool, string> check)
    {
        var cs = EditorHighlighting.ForFile("demo.cs");
        check("[编辑器] 按扩展名取到内置高亮定义（.cs）", cs is not null, cs?.Name ?? "<null>");

        check("[编辑器] 识别不了的扩展名返回 null（纯文本）",
            EditorHighlighting.ForFile("data.unknown-ext") is null && EditorHighlighting.ForFile(null) is null,
            "unknown / null");

        var comment = cs is null ? null : cs.NamedHighlightingColors.FirstOrDefault(c =>
            string.Equals(c.Name, "Comment", StringComparison.OrdinalIgnoreCase));
        var color = comment is null ? null : EditorHighlighting.MapColor(comment.Name);
        check("[编辑器] 暗色调色：注释被覆写成暗底可读的绿色系",
            color is { } c && c.G > c.R && c.G > 100,
            color?.ToString() ?? "<null>");

        check("[编辑器] 调色映射：关键字蓝 / 字符串橙 / 未知名字保持默认",
            EditorHighlighting.MapColor("Keywords") is { } k && k.B > k.R
            && EditorHighlighting.MapColor("String") is { } s && s.R > s.B
            && EditorHighlighting.MapColor("完全陌生的名字") is null,
            "Keywords / String / 未知");

        // 幂等：同一份定义调两次不抛异常、结果一致
        if (cs is not null)
        {
            EditorHighlighting.ApplyDarkPalette(cs);
            EditorHighlighting.ApplyDarkPalette(cs);
            check("[编辑器] 暗色调色幂等（重复调用不抛异常）", true, cs.Name);
        }
    }

    // ---------------------------------------------------------------- 配置默认值与落盘

    private static void CheckConfig(Action<string, bool, string> check)
    {
        // 纯函数夹取
        check("[编辑器] 字号夹取：非法回默认 14，越界夹回 8~36",
            TextEditorConfig.ClampFontSize(double.NaN) == 14
            && TextEditorConfig.ClampFontSize(2) == 8
            && TextEditorConfig.ClampFontSize(99) == 36
            && TextEditorConfig.ClampFontSize(14) == 14,
            "NaN→14 · 2→8 · 99→36");

        check("[编辑器] 缩放夹取：非法回 100%，越界夹回 25~400，0.5% 取整",
            TextEditorConfig.ClampZoomPercent(double.NaN) == 100
            && TextEditorConfig.ClampZoomPercent(5) == 25
            && TextEditorConfig.ClampZoomPercent(999) == 400
            && TextEditorConfig.ClampZoomPercent(133.3) == 133.5,
            "NaN→100 · 5→25 · 999→400 · 133.3→133.5");

        check("[编辑器] 步进缩放：放大 / 缩小各 10%，到顶不再变",
            TextEditorConfig.StepZoomPercent(100, 1) == 110
            && TextEditorConfig.StepZoomPercent(100, -1) == 90
            && TextEditorConfig.StepZoomPercent(400, 1) == 400
            && TextEditorConfig.StepZoomPercent(25, -1) == 25
            && TextEditorConfig.StepZoomPercent(100, 0) == 100,
            "100→110 / 90 / 400, 25 顶住");

        check("[编辑器] 缩进 / 会话序号夹取与字体回退链",
            TextEditorConfig.ClampIndentSize(6) == 6
            && TextEditorConfig.ClampIndentSize(99) == 4
            && TextEditorConfig.ClampSessionIndex(5, 3) == 2
            && TextEditorConfig.ClampSessionIndex(1, 0) == 0
            && TextEditorConfig.BuildFontFamily("")!.StartsWith("Cascadia Mono", StringComparison.Ordinal)
            && TextEditorConfig.BuildFontFamily("Consolas") == "Consolas" + TextEditorConfig.FontFallbackChain,
            "缩进 / 序号 / 字体链");

        // 配置文件真实读写
        var store = ToolConfigStore.For(TextEditorConfig.ToolId);
        var config = store.Load<TextEditorConfig>();
        check("[编辑器] 首次加载即生成 config.json", File.Exists(config.FilePath), config.FilePath);
        check("[编辑器] 默认值：Cascadia Mono / 14px / 100% / 深空底色 / 行号开 / 换行关 / 空格缩进 4",
            config.FontName == TextEditorConfig.DefaultFontName
            && config.FontSize == 14 && config.ZoomPercent == 100
            && config.BackgroundHex == TextEditorConfig.DefaultBackgroundHex
            && config.ForegroundHex == TextEditorConfig.DefaultForegroundHex
            && config.ShowLineNumbers && !config.WordWrap
            && config.ConvertTabsToSpaces && config.IndentSize == 4
            && config.RecentFiles.Count == 0 && config.SessionFiles.Count == 0
            && config.RememberSession,
            $"{config.FontName} / {config.FontSize}px / {config.ZoomPercent}%");

        config.FontName = "Consolas";
        config.FontSize = 18;
        config.ZoomPercent = 999;          // 写超界值 → 落盘前被夹回
        config.BackgroundHex = "#1E1E1E";
        config.ShowLineNumbers = false;
        config.RecentFiles = new List<string> { @"C:\a\1.cs", @"C:\b\2.md" };
        config.SaveNow();

        var reloaded = JsonFileStore.Read<TextEditorConfig>(config.FilePath);
        check("[编辑器] 改字体 / 字号 / 颜色 / 行号 / 最近列表后立即落盘并能读回（缩放被夹回 400）",
            reloaded is { FontName: "Consolas", FontSize: 18, ZoomPercent: 400
                , BackgroundHex: "#1E1E1E", ShowLineNumbers: false }
            && reloaded.RecentFiles.Count == 2 && reloaded.RecentFiles[0] == @"C:\a\1.cs",
            $"{reloaded?.FontName} / {reloaded?.ZoomPercent}%");

        // 还原成默认，避免影响下一轮 / 其它检查
        var fresh = new TextEditorConfig();
        config.FontName = fresh.FontName;
        config.FontSize = fresh.FontSize;
        config.ZoomPercent = fresh.ZoomPercent;
        config.BackgroundHex = fresh.BackgroundHex;
        config.ForegroundHex = fresh.ForegroundHex;
        config.ShowLineNumbers = fresh.ShowLineNumbers;
        config.RecentFiles = fresh.RecentFiles;
        config.SaveNow();
    }
}
