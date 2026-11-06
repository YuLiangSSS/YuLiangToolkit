using System.IO;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Tools.Archive;
using YuLiangToolkit.Tools.ExcelViewer;
using YuLiangToolkit.Tools.MediaPlayer;
using YuLiangToolkit.Tools.OpenWith;
using YuLiangToolkit.Tools.PdfReader;
using YuLiangToolkit.Tools.PictureViewer;
using YuLiangToolkit.Tools.PptViewer;
using YuLiangToolkit.Tools.TextEditor;
using YuLiangToolkit.Tools.WordViewer;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「打开方式」的纯逻辑自检（被测代码见 <see cref="OpenWithCatalog"/> / <see cref="OpenWithArguments"/> /
/// <see cref="OpenWithRegistration"/>）。
///
/// <para><b>硬性约束</b>：这里<b>绝不调用</b> <see cref="OpenWithRegistration.Register"/> /
/// <see cref="OpenWithRegistration.Unregister"/> / <see cref="OpenWithRegistration.EnsureUpToDate"/> ——
/// 它们会往当前用户 HKCU 写注册表、复制入口 exe，跑一次自检就在用户机器上留垃圾。
/// 只允许测纯逻辑 + 只读的 <see cref="OpenWithRegistration.IsRegistered"/> /
/// <see cref="OpenWithRegistration.Describe"/>（只 OpenSubKey / 读文件，不写）。</para>
///
/// <para>文件系统部分只在 <see cref="SelfCheckScratch"/> 临时目录里造文件，不碰仓库 Data/Config。</para>
/// </summary>
internal static class OpenWithChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> checkPlain)
    {
        void Check(string name, bool ok, string detail = "") => check("[打开方式] " + name, ok, detail);

        // ================================================================
        // 1. 常量与三个入口
        // ================================================================
        Check("常量：应用名 = 凉雨工具集 / 状态键在 Software\\YuLiangToolkit\\OpenWith",
            OpenWithRegistration.AppDisplayName == "凉雨工具集"
            && OpenWithRegistration.RegistryApplicationsPath == @"Software\Classes\Applications"
            && OpenWithRegistration.StateKeyPath == @"Software\YuLiangToolkit\OpenWith",
            OpenWithRegistration.StateKeyPath);

        var openers = OpenWithRegistration.Openers;
        Check("入口：正好 3 个（自动 / 文本 / 压缩包），显示名以应用名开头且互不相同",
            openers.Count == 3
            && openers.Select(o => o.Mode).SequenceEqual(new[]
                { OpenWithMode.Auto, OpenWithMode.Text, OpenWithMode.Archive })
            && openers.All(o => o.DisplayName.StartsWith(OpenWithRegistration.AppDisplayName, StringComparison.Ordinal))
            && openers.Select(o => o.DisplayName).Distinct().Count() == 3);

        Check("入口：只有文本 / 压缩包需要入口副本（后缀以 .exe 结尾），自动模式用主程序本身",
            openers[0].FileNameSuffix.Length == 0
            && openers[1].FileNameSuffix.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && openers[2].FileNameSuffix.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && openers[1].FileNameSuffix != openers[2].FileNameSuffix);

        // ================================================================
        // 2. 路径 / 文件名 / 命令行（含空格）
        // ================================================================
        var mainExe = @"C:\Apps\凉雨 工具集\YuLiangTookit.exe";
        var textOpener = Path.Combine(Path.GetDirectoryName(mainExe)!, "YuLiangTookit.open-text.exe");
        var archiveOpener = Path.Combine(Path.GetDirectoryName(mainExe)!, "YuLiangTookit.open-archive.exe");

        Check("路径：主程序路径原样返回；入口副本路径能还原成主程序（大小写 / 后缀都容忍）",
            OpenWithRegistration.ResolveCanonicalExecutablePath(mainExe) == mainExe
            && OpenWithRegistration.ResolveCanonicalExecutablePath(textOpener) == mainExe
            && OpenWithRegistration.ResolveCanonicalExecutablePath(archiveOpener) == mainExe
            && OpenWithRegistration.ResolveCanonicalExecutablePath(textOpener.ToUpperInvariant()) != ""
            && OpenWithRegistration.ResolveCanonicalExecutablePath(@"C:\Apps\Other.exe") == @"C:\Apps\Other.exe",
            OpenWithRegistration.ResolveCanonicalExecutablePath(textOpener));

        Check("路径：按 exe 文件名能反推模式（主程序 → null，两个副本 → 文本 / 压缩包）",
            OpenWithRegistration.InferModeFromExecutable(mainExe) is null
            && OpenWithRegistration.InferModeFromExecutable(textOpener) == OpenWithMode.Text
            && OpenWithRegistration.InferModeFromExecutable(archiveOpener) == OpenWithMode.Archive);

        Check("文件名：自动 = 主 exe 名；文本 / 压缩包各自带后缀且三个互不相同",
            OpenWithRegistration.GetOpenerFileName(mainExe, OpenWithMode.Auto) == "YuLiangTookit.exe"
            && OpenWithRegistration.GetOpenerFileName(mainExe, OpenWithMode.Text).EndsWith(".open-text.exe")
            && OpenWithRegistration.GetOpenerFileName(mainExe, OpenWithMode.Archive).EndsWith(".open-archive.exe")
            && new[]
            {
                OpenWithRegistration.GetOpenerFileName(mainExe, OpenWithMode.Auto),
                OpenWithRegistration.GetOpenerFileName(mainExe, OpenWithMode.Text),
                OpenWithRegistration.GetOpenerFileName(mainExe, OpenWithMode.Archive),
            }.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3);

        Check("路径：自动模式的入口就是主 exe；文本 / 压缩包副本和主 exe 同目录",
            OpenWithRegistration.GetOpenerPath(mainExe, OpenWithMode.Auto) == mainExe
            && Path.GetDirectoryName(OpenWithRegistration.GetOpenerPath(mainExe, OpenWithMode.Text))
               == Path.GetDirectoryName(mainExe)
            && Path.GetDirectoryName(OpenWithRegistration.GetOpenerPath(mainExe, OpenWithMode.Archive))
               == Path.GetDirectoryName(mainExe));

        var autoCommand = OpenWithRegistration.BuildOpenCommand(mainExe, OpenWithMode.Auto);
        var textCommand = OpenWithRegistration.BuildOpenCommand(textOpener, OpenWithMode.Text);
        var archiveCommand = OpenWithRegistration.BuildOpenCommand(archiveOpener, OpenWithMode.Archive);

        Check("命令行：自动模式 = \"主程序\" \"%1\"（与历史 PDF / 图片关联兼容，不带 --open-as）",
            autoCommand == "\"" + mainExe + "\" \"%1\"",
            autoCommand);

        Check("命令行：文本 / 压缩包模式带 --open-as=text / archive，且 %1 带引号（路径有空格也不会坏）",
            textCommand == "\"" + textOpener + "\" --open-as=text \"%1\""
            && archiveCommand == "\"" + archiveOpener + "\" --open-as=archive \"%1\"",
            textCommand);

        Check("命令行：首尾空白 / 多余引号会被清掉",
            OpenWithRegistration.BuildOpenCommand("  \"" + mainExe + "\"  ", OpenWithMode.Auto)
            == "\"" + mainExe + "\" \"%1\"");

        // ================================================================
        // 3. 命令行参数解析（纯逻辑）
        // ================================================================
        var folder = SelfCheckScratch.MakeDirectory("open-with");
        var fileA = Path.Combine(folder, "a.txt");
        var fileB = Path.Combine(folder, "b.zip");
        var missing = Path.Combine(folder, "missing.txt");
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");

        Check("参数：BuildModeArgument 拼出 --open-as=text / archive / auto",
            OpenWithArguments.BuildModeArgument(OpenWithMode.Text) == "--open-as=text"
            && OpenWithArguments.BuildModeArgument(OpenWithMode.Archive) == "--open-as=archive"
            && OpenWithArguments.BuildModeArgument(OpenWithMode.Auto) == "--open-as=auto");

        Check("参数：ParseMode 忽略大小写 / 空白，也认中文；不认识的返回 null",
            OpenWithArguments.ParseMode(" Text ") == OpenWithMode.Text
            && OpenWithArguments.ParseMode("txt") == OpenWithMode.Text
            && OpenWithArguments.ParseMode("压缩包") == OpenWithMode.Archive
            && OpenWithArguments.ParseMode("AUTO") == OpenWithMode.Auto
            && OpenWithArguments.ParseMode("unknown") is null
            && OpenWithArguments.ParseMode(null) is null);

        bool ParserCase(string[] args, OpenWithMode expectedMode, string expectedPath)
            => OpenWithArguments.TryParse(args, out var mode, out var path)
               && mode == expectedMode
               && string.Equals(path, expectedPath, StringComparison.Ordinal);

        Check("参数：--open-as=text / --open-as archive 两种写法都能解析出模式 + 文件",
            ParserCase(new[] { "--open-as=text", fileA }, OpenWithMode.Text, fileA)
            && ParserCase(new[] { "--open-as", "archive", fileB }, OpenWithMode.Archive, fileB));

        Check("参数：普通启动（只有真实存在的文件）= 自动模式；文件不存在 / 只有模式没有文件 = 不是打开请求",
            ParserCase(new[] { fileA }, OpenWithMode.Auto, fileA)
            && !OpenWithArguments.TryParse(new[] { missing }, out _, out _)
            && !OpenWithArguments.TryParse(new[] { "--open-as=text" }, out _, out _)
            && !OpenWithArguments.TryParse(Array.Empty<string>(), out _, out _)
            && !OpenWithArguments.TryParse(null, out _, out _));

        Check("参数：未知开关被忽略、未知模式退化成自动模式；Excel 参数只取第一个文件",
            ParserCase(new[] { "--noop", "--open-as=weird", fileA, fileB }, OpenWithMode.Auto, fileA));

        var normalized = OpenWithArguments.NormalizeFileArguments(new[] { fileA, "--open-as=text", missing });
        Check("参数：单实例转发把存在文件转绝对路径，开关 / 不存在的参数原样保留",
            normalized.Length == 3
            && normalized[0] == Path.GetFullPath(fileA)
            && normalized[1] == "--open-as=text"
            && normalized[2] == missing);

        // ================================================================
        // 4. 自动分流表（扩展名 → 工具）
        // ================================================================
        Check("分流表：8 个目标、Id 与各工具工程的 ToolId 一致",
            OpenWithCatalog.Targets.Select(t => t.ToolId).SequenceEqual(new[]
            {
                PdfReaderConfig.ToolId,
                PictureViewerConfig.ToolId,
                WordViewerConfig.ToolId,
                ExcelViewerConfig.ToolId,
                PptViewerConfig.ToolId,
                new ArchiveTool().Id,
                MediaPlayerConfig.ToolId,
                TextEditorConfig.ToolId,
            })
            && OpenWithCatalog.TextToolId == TextEditorConfig.ToolId
            && OpenWithCatalog.ArchiveToolId == new ArchiveTool().Id
            && OpenWithCatalog.FallbackToolId == TextEditorConfig.ToolId,
            string.Join(",", OpenWithCatalog.Targets.Select(t => t.ToolId)));

        Check("分流表：目标显示信息完整（名称 / 图标 / 说明非空，每个工具至少认领 1 个扩展名）",
            OpenWithCatalog.Targets.All(t =>
                t.ToolName.Length > 0 && t.EmojiIcon.Length > 0 && t.Hint.Length > 0
                && t.Extensions is { Count: > 0 }));

        Check("扩展名表：全部小写、带前导点、各自内部无重复；AllExtensions 去重后数量对得上",
            OpenWithCatalog.TextArchiveExtensions.Concat(OpenWithCatalog.MediaExtensions)
                .Concat(OpenWithCatalog.TextExtensions)
                .All(e => e.StartsWith('.') && e == e.ToLowerInvariant())
            && OpenWithCatalog.TextArchiveExtensions.Distinct().Count() == OpenWithCatalog.TextArchiveExtensions.Count
            && OpenWithCatalog.MediaExtensions.Distinct().Count() == OpenWithCatalog.MediaExtensions.Count
            && OpenWithCatalog.TextExtensions.Distinct().Count() == OpenWithCatalog.TextExtensions.Count
            && OpenWithCatalog.AllExtensions.Count
               == OpenWithCatalog.Targets.SelectMany(t => t.Extensions)
                   .Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Check("扩展名表：图片 / PDF / PPT 与对应阅读器的关联表一致",
            OpenWithCatalog.Targets.Single(t => t.ToolId == PictureViewerConfig.ToolId).Extensions
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(PictureFileAssociation.SupportedExtensions
                    .OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            && OpenWithCatalog.Targets.Single(t => t.ToolId == PdfReaderConfig.ToolId).Extensions
                .SequenceEqual(PdfFileAssociation.SupportedExtensions)
            && OpenWithCatalog.Targets.Single(t => t.ToolId == PptViewerConfig.ToolId).Extensions
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(PptFileAssociation.SupportedExtensions
                    .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)));

        Check("扩展名表：播放器表覆盖 MediaTypes 的全部音频；除 .ts 冲突外覆盖全部视频",
            MediaTypes.AudioExtensions.All(e => OpenWithCatalog.MediaExtensions.Contains(e, StringComparer.OrdinalIgnoreCase))
            && MediaTypes.VideoExtensions.Where(e => e != ".ts")
                .All(e => OpenWithCatalog.MediaExtensions.Contains(e, StringComparer.OrdinalIgnoreCase))
            && !OpenWithCatalog.MediaExtensions.Contains(".ts")
            && OpenWithCatalog.TextExtensions.Contains(".ts"),
            "音频 " + MediaTypes.AudioExtensions.Count + " / 视频 " + MediaTypes.VideoExtensions.Count);

        Check("扩展名表：压缩包工具认领常见格式（zip / rar / 7z / tar / tar.gz / gz）",
            new[] { ".zip", ".rar", ".7z", ".tar", ".tar.gz", ".tgz", ".gz" }
                .All(e => OpenWithCatalog.TextArchiveExtensions.Contains(e, StringComparer.OrdinalIgnoreCase)));

        string? Route(string? path) => OpenWithCatalog.ResolveToolId(path);
        Check("分流：PDF / 图片 / Word / Excel / PPT / 压缩包 / 音视频 / 文本都进对应工具",
            Route(@"C:\a\b.pdf") == PdfReaderConfig.ToolId
            && Route(@"C:\a\B.PDF") == PdfReaderConfig.ToolId
            && Route(@"C:\a\b.png") == PictureViewerConfig.ToolId
            && Route(@"C:\a\b.docx") == WordViewerConfig.ToolId
            && Route(@"C:\a\b.xlsx") == ExcelViewerConfig.ToolId
            && Route(@"C:\a\b.pptx") == PptViewerConfig.ToolId
            && Route(@"C:\a\b.PPSX") == PptViewerConfig.ToolId
            && Route(@"C:\a\b.zip") == new ArchiveTool().Id
            && Route(@"C:\a\b.tar.gz") == new ArchiveTool().Id
            && Route(@"C:\a\b.mp3") == MediaPlayerConfig.ToolId
            && Route(@"C:\a\b.mp4") == MediaPlayerConfig.ToolId
            && Route(@"C:\a\b.txt") == TextEditorConfig.ToolId
            && Route(@"C:\a\b.cs") == TextEditorConfig.ToolId
            && Route(@"C:\a\b.ts") == TextEditorConfig.ToolId);

        Check("分流：认不出的扩展名 / 没有扩展名 / 空路径返回 null（由主程序用文本编辑器兜底）",
            Route(@"C:\a\b.xyz") is null
            && Route(@"C:\a\README") is null
            && Route("") is null
            && Route(null) is null);

        Check("分流：多段扩展名取最长的（a.tar.gz 命中 .tar.gz，不会被 .gz 抢走）；大小写不敏感",
            OpenWithCatalog.ResolveExtension(@"C:\a\b.tar.gz") == ".tar.gz"
            && OpenWithCatalog.ResolveExtension(@"C:\a\b.TAR.GZ") == ".tar.gz"
            && OpenWithCatalog.ResolveExtension(@"C:\a\b.GZ") == ".gz"
            && OpenWithCatalog.ResolveExtension(@"C:\a\b.unknownext") is null);

        Check("分流：DescribeRoute 给出「扩展名 → 工具名」；不认识的说文本编辑器兜底",
            OpenWithCatalog.DescribeRoute(@"C:\a\b.pdf").Contains("PDF 阅读器", StringComparison.Ordinal)
            && OpenWithCatalog.DescribeRoute(@"C:\a\b.xyz").Contains("文本编辑器", StringComparison.Ordinal)
            && OpenWithCatalog.DescribeRoute("").Contains("还没有选择文件", StringComparison.Ordinal));

        Check("扩展名规范化：补点 / 转小写 / 去空白；空输入返回 null",
            OpenWithCatalog.NormalizeExtension(" ZIP ") == ".zip"
            && OpenWithCatalog.NormalizeExtension("Tar.Gz") == ".tar.gz"
            && OpenWithCatalog.NormalizeExtension(" .md ") == ".md"
            && OpenWithCatalog.NormalizeExtension("  ") is null
            && OpenWithCatalog.NormalizeExtension(null) is null);

        // ================================================================
        // 5. 登记状态只读函数（绝不写注册表）
        // ================================================================
        var missingExe = Path.Combine(folder, "NoSuchApp.exe");
        var missingState = OpenWithRegistration.Describe(missingExe);
        Check("状态：主程序不存在 → 未登记 + 明确提示，不会抛异常",
            !missingState.Registered
            && !missingState.OpenersUpToDate
            && missingState.Message.Contains("找不到主程序", StringComparison.Ordinal),
            missingState.Message);

        Check("状态：不存在的 exe 不算已登记（IsRegistered 只读注册表）",
            !OpenWithRegistration.IsRegistered(missingExe));

        var fakeExe = Path.Combine(folder, "FakeApp.exe");
        File.WriteAllBytes(fakeExe, new byte[] { 0x4D, 0x5A });
        var fakeState = OpenWithRegistration.Describe(fakeExe);
        Check("状态：Describe 与 IsRegistered 对得上，三个入口路径齐全且在主程序同目录",
            fakeState.Registered == OpenWithRegistration.IsRegistered(fakeExe)
            && fakeState.OpenerPaths.Count == 3
            && fakeState.OpenerPaths[0] == fakeExe
            && fakeState.OpenerPaths.Skip(1).All(p => Path.GetDirectoryName(p) == folder)
            && fakeState.Message.Length > 0);

        var selfState = OpenWithRegistration.Describe(Environment.ProcessPath ?? "");
        Check("状态：用自检进程自己的 exe 查状态也不抛异常（未登记 / 已登记都允许，只要求返回说明）",
            selfState.Message.Length > 0 && selfState.OpenerPaths.Count == 3);

        // ================================================================
        // 6. 工具 / 配置元数据（不创建视图，不碰 WPF）
        // ================================================================
        var tool = new OpenWithTool();
        Check("工具元数据：Id / 名称 / 分类 / 排序 / 标签正确",
            tool.Id == OpenWithConfig.ToolId
            && OpenWithConfig.ToolId == "open-with"
            && tool.Name == "打开方式"
            && tool.Category == "文件"
            && tool.Order == 43
            && tool.Tags.Count > 0
            && tool.EmojiIcon.Length > 0);

        var config = new OpenWithConfig();
        Check("配置：默认模拟路径为空（重建配置后回到出厂状态）",
            config.LastSimulatedPath == "");

        // ================================================================
        // 7. 主页设置开关 / 构建门禁 / 清理（只读，绝不写注册表）
        // ================================================================
        var shellConfig = new YuLiangToolkit.App.Models.ShellConfig();
        Check("主页设置：打开方式关联默认关闭（ShellConfig 字段默认 false）",
            !shellConfig.OpenWithAssociationEnabled);

#if DEBUG
        Check("构建门禁：Debug 构建不允许登记「打开方式」", !OpenWithRegistration.IsBuildSupported);

        // 门禁在碰注册表之前就返回，这条调用不会写任何东西
        var gateResult = OpenWithRegistration.Register(fakeExe);
        Check("构建门禁：Debug 下 Register 被拒绝并提示用 Release / 发布版",
            !gateResult.Success
            && gateResult.Message.Contains("Debug", StringComparison.Ordinal)
            && !OpenWithRegistration.IsRegistered(fakeExe));

        Check("清理：空路径 / null 不会误删任何东西（ClearAll 直接返回失败）",
            !OpenWithRegistration.ClearAll("").Success
            && !OpenWithRegistration.ClearAll(null!).Success);
#else
        Check("构建门禁：Release 构建允许登记「打开方式」", OpenWithRegistration.IsBuildSupported);
        Check("构建门禁：Release 下不可用文案为空", OpenWithRegistration.UnsupportedBuildMessage == "");
#endif

        Check("清理：HasRegistrationArtifacts 对空路径 / null 返回 false，不抛异常",
            !OpenWithRegistration.HasRegistrationArtifacts("")
            && !OpenWithRegistration.HasRegistrationArtifacts(null));

        Check("清理：已知 ProgId 与 PDF / 图片 / PPT 阅读器一致（只清本软件的打开方式引用，不误删别的程序）",
            OpenWithRegistration.KnownAssociationProgIds
                .OrderBy(p => p, StringComparer.Ordinal)
                .SequenceEqual(new[]
                    {
                        PictureFileAssociation.ProgId,
                        PdfFileAssociation.ProgId,
                        PptFileAssociation.ProgId,
                    }
                    .OrderBy(p => p, StringComparer.Ordinal)),
            string.Join(",", OpenWithRegistration.KnownAssociationProgIds));

        checkPlain("[打开方式] 只读自检完成（未写注册表 / 未复制入口 exe）", true);
    }
}
