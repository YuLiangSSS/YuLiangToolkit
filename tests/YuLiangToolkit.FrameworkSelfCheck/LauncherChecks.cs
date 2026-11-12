using System.IO;
using System.Text.Json;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Launcher;
using YuLiangToolkit.Tools.Launcher.Models;
using YuLiangToolkit.Tools.Launcher.Services;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「软件启动器」自检：工具元数据 / 导入扩展名校验 / 路径规范化与去重 / 默认显示名与名字清洗 /
/// 图标缓存路径与安全判断 / 失效检测（假 probe 注入）/ 清空无效项 / 配置默认值与 JSON 往返。
///
/// <para><b>全部是纯逻辑</b>：不启动任何程序、不调 Windows Shell、不依赖本机装了什么软件、不写仓库
/// Data/Config。文件存在与快捷方式解析都用本文件里的 <see cref="FakeLauncherProbe"/> 写死。</para>
/// </summary>
internal static class LauncherChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckSupportedPaths(assert);
        CheckPathHelpers(assert);
        CheckNormalizeAndSamePath(assert);
        CheckDefaultDisplayName(assert);
        CheckSanitizeDisplayName(assert);
        CheckIconPaths(assert);
        CheckHealth(assert);
        CheckFindInvalid(assert);
        CheckContainsPath(assert);
        CheckDescribeHealth(assert);
        CheckConfig(assert);
        CheckLinkedStartupRules(assert);
        CheckLinkedStartupSelection(assert);
        CheckLinkedStartupStartMany(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new LauncherTool();
        check("[软件启动器] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "launcher" && tool.Name == "软件启动器"
            && tool.Category == "系统" && tool.Order == 44,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[软件启动器] 标签含「启动器」「快捷方式」等主页筛选用词",
            tool.Tags.Contains("启动器") && tool.Tags.Contains("快捷方式")
            && tool.Tags.Contains("图标") && tool.Tags.Contains("exe"),
            string.Join(" / ", tool.Tags));

        check("[软件启动器] 描述写明 exe / 快捷方式 / 图标墙 / 清掉失效项，且实现了 ITool",
            tool.Description.Contains("exe") && tool.Description.Contains("快捷方式")
            && tool.Description.Contains("图标墙") && tool.Description.Contains("失效")
            && tool is YuLiangToolkit.Core.Tools.ITool && tool.EmojiIcon.Length > 0,
            tool.Description);
    }

    // ---------------------------------------------------------------- 导入扩展名

    private static void CheckSupportedPaths(CheckFn check)
    {
        check("[软件启动器] IsSupportedPath：.exe / .EXE / .lnk / .LNK 都支持（忽略大小写）",
            LauncherRules.IsSupportedPath(@"C:\Apps\Tool.exe")
            && LauncherRules.IsSupportedPath("C:\\Apps\\Tool.EXE")
            && LauncherRules.IsSupportedPath(@"C:\Users\Me\Desktop\微信.lnk")
            && LauncherRules.IsSupportedPath(@"C:\Users\Me\Desktop\微信.LNK"));

        check("[软件启动器] IsSupportedPath：.txt / .bat / 无扩展名 / null / 空白都不支持",
            !LauncherRules.IsSupportedPath(@"C:\Docs\readme.txt")
            && !LauncherRules.IsSupportedPath(@"C:\Docs\run.bat")
            && !LauncherRules.IsSupportedPath(@"C:\Apps\Makefile")
            && !LauncherRules.IsSupportedPath(null)
            && !LauncherRules.IsSupportedPath("")
            && !LauncherRules.IsSupportedPath("   "));

        check("[软件启动器] IsSupportedPath：带空格与首尾空白的路径仍按扩展名判断",
            LauncherRules.IsSupportedPath(@"C:\Program Files\My App\App 1.0.exe")
            && LauncherRules.IsSupportedPath(@"C:\Apps\Tool.exe ")
            && LauncherRules.IsSupportedPath("  C:\\Apps\\Tool.lnk  "));
    }

    // ---------------------------------------------------------------- 扩展名 / 文件名

    private static void CheckPathHelpers(CheckFn check)
    {
        check("[软件启动器] IsShortcutPath：只认 .lnk（忽略大小写），其它路径都不算",
            LauncherRules.IsShortcutPath(@"C:\Desktop\微信.lnk")
            && LauncherRules.IsShortcutPath(@"C:\Desktop\微信.LNK")
            && !LauncherRules.IsShortcutPath(@"C:\Apps\微信.exe")
            && !LauncherRules.IsShortcutPath(null)
            && !LauncherRules.IsShortcutPath(""));

        check("[软件启动器] ExtensionOf：含点且保留原大小写；无扩展名 / 空 / 尾部分隔符返回空串",
            LauncherRules.ExtensionOf(@"C:\Apps\Tool.Exe") == ".Exe"
            && LauncherRules.ExtensionOf(@"C:\Apps\Makefile") == ""
            && LauncherRules.ExtensionOf(@"C:\Apps\Tool.exe\") == ""
            && LauncherRules.ExtensionOf(null) == ""
            && LauncherRules.ExtensionOf("  ") == "");

        check("[软件启动器] FileNameOf：取不含扩展名的文件名；空 / 尾部分隔符返回空串",
            LauncherRules.FileNameOf(@"C:\Program Files\My App\WeChat.exe") == "WeChat"
            && LauncherRules.FileNameOf(@"C:\Users\Me\Desktop\微信.lnk") == "微信"
            && LauncherRules.FileNameOf(@"C:\Apps\") == ""
            && LauncherRules.FileNameOf(null) == ""
            && LauncherRules.FileNameOf("   ") == "");
    }

    // ---------------------------------------------------------------- 路径规范化 / 相同路径

    private static void CheckNormalizeAndSamePath(CheckFn check)
    {
        check("[软件启动器] NormalizePath：去掉首尾空白与成对引号",
            LauncherRules.NormalizePath("  \"C:\\Apps\\Tool.exe\"  ") == @"C:\Apps\Tool.exe"
            && LauncherRules.NormalizePath("  C:\\Apps\\Tool.exe  ") == @"C:\Apps\Tool.exe"
            && LauncherRules.NormalizePath("\"  C:\\Apps\\Tool.exe  \"") == @"C:\Apps\Tool.exe"
            && LauncherRules.NormalizePath(null) == ""
            && LauncherRules.NormalizePath("   ") == ""
            && LauncherRules.NormalizePath("\"\"") == "");

        var relative = LauncherRules.NormalizePath(@"relative\a.exe");
        check("[软件启动器] NormalizePath：相对路径转成绝对路径",
            relative.Length > 0 && Path.IsPathRooted(relative)
            && relative == Path.GetFullPath(@"relative\a.exe"),
            relative);

        check("[软件启动器] NormalizePath：非法字符路径不抛异常、退回清理后的原文本",
            LauncherRules.NormalizePath(@"C:\bad<name>.exe") == @"C:\bad<name>.exe",
            LauncherRules.NormalizePath(@"C:\bad<name>.exe"));

        var nulPath = "C:\\bad" + '\0' + "name.exe";
        var nulThrew = false;
        var nulNormalized = "";
        try { nulNormalized = LauncherRules.NormalizePath(nulPath); }
        catch { nulThrew = true; }
        check("[软件启动器] NormalizePath：含 NUL 的路径也不抛异常",
            !nulThrew && nulNormalized.Length > 0,
            nulThrew ? "抛出了异常" : $"长度 {nulNormalized.Length}");

        check("[软件启动器] SamePath：忽略大小写 / 引号 / 首尾空白",
            LauncherRules.SamePath(@"C:\Apps\MyTool.exe", @"c:\apps\MYTOOL.EXE")
            && LauncherRules.SamePath("\"C:\\Apps\\MyTool.exe\"", @"C:\Apps\MyTool.exe")
            && LauncherRules.SamePath(@"C:\Apps\MyTool.exe ", @"C:\Apps\MyTool.exe"));

        check("[软件启动器] SamePath：任一侧为空 / 空白 / 不同路径都不算相同",
            !LauncherRules.SamePath("", @"C:\Apps\MyTool.exe")
            && !LauncherRules.SamePath(@"C:\Apps\MyTool.exe", null)
            && !LauncherRules.SamePath("   ", "   ")
            && !LauncherRules.SamePath(@"C:\Apps\A.exe", @"C:\Apps\B.exe")
            && !LauncherRules.SamePath(null, null));
    }

    // ---------------------------------------------------------------- 默认显示名

    private static void CheckDefaultDisplayName(CheckFn check)
    {
        check("[软件启动器] DefaultDisplayName：.lnk 用快捷方式文件名，忽略版本说明",
            LauncherRules.DefaultDisplayName(@"C:\Users\Me\Desktop\微信.lnk", "WeChat 4.0 版本说明") == "微信");

        check("[软件启动器] DefaultDisplayName：.exe 优先用 FileDescription（去首尾空白）",
            LauncherRules.DefaultDisplayName(@"C:\Apps\WeChat.exe", "微信") == "微信"
            && LauncherRules.DefaultDisplayName(@"C:\Apps\WeChat.exe", "  微信  ") == "微信");

        check("[软件启动器] DefaultDisplayName：FileDescription 空白 / null 时退回文件名",
            LauncherRules.DefaultDisplayName(@"C:\Apps\WeChat.exe", "  \r\n\t ") == "WeChat"
            && LauncherRules.DefaultDisplayName(@"C:\Apps\WeChat.exe", null) == "WeChat"
            && LauncherRules.DefaultDisplayName(null) == "未命名");

        check("[软件启动器] DefaultDisplayName：超长名字截断到 MaxDisplayNameLength",
            LauncherRules.DefaultDisplayName(@"C:\Apps\" + new string('a', 80) + ".exe").Length
                == LauncherRules.MaxDisplayNameLength
            && LauncherRules.DefaultDisplayName(@"C:\Users\Me\Desktop\" + new string('短', 80) + ".lnk")
                == new string('短', LauncherRules.MaxDisplayNameLength),
            $"{LauncherRules.MaxDisplayNameLength} 字上限");
    }

    // ---------------------------------------------------------------- 名字清洗

    private static void CheckSanitizeDisplayName(CheckFn check)
    {
        check("[软件启动器] SanitizeDisplayName：去首尾空白与控制字符（换行 / Tab / 不可见字符）",
            LauncherRules.SanitizeDisplayName("  微信  ") == "微信"
            && LauncherRules.SanitizeDisplayName("微\n信\r\n") == "微信"
            && LauncherRules.SanitizeDisplayName(" \t\u0001名字\u0002 ") == "名字");

        check("[软件启动器] SanitizeDisplayName：超长截断并去掉截断后的尾随空白",
            LauncherRules.SanitizeDisplayName(new string('a', 100))
                == new string('a', LauncherRules.MaxDisplayNameLength)
            && LauncherRules.SanitizeDisplayName(new string('a', 59) + "   x")
                == new string('a', 59));

        check("[软件启动器] SanitizeDisplayName：空白输入用 fallback，fallback 也空则「未命名」",
            LauncherRules.SanitizeDisplayName("", "备用名") == "备用名"
            && LauncherRules.SanitizeDisplayName("   ", "  ") == "未命名"
            && LauncherRules.SanitizeDisplayName(null) == "未命名"
            && LauncherRules.SanitizeDisplayName(null, new string('b', 100))
                == new string('b', LauncherRules.MaxDisplayNameLength));
    }

    // ---------------------------------------------------------------- 图标缓存路径

    private static void CheckIconPaths(CheckFn check)
    {
        var iconPath = LauncherRules.IconRelativePath("abc-123_X");
        check("[软件启动器] IconRelativePath：结果在 icons 目录下、以 .png 结尾",
            iconPath == Path.Combine(LauncherRules.IconFolder, "abc-123_X.png")
            && iconPath.StartsWith(LauncherRules.IconFolder + Path.DirectorySeparatorChar)
            && iconPath.EndsWith(".png", StringComparison.Ordinal),
            iconPath);

        check("[软件启动器] IconRelativePath：非法字符替换为 -，空 Id 有兜底",
            LauncherRules.IconRelativePath("a/b c:d")
                == Path.Combine(LauncherRules.IconFolder, "a-b-c-d.png")
            && LauncherRules.IconRelativePath("启动项-1")
                == Path.Combine(LauncherRules.IconFolder, "启动项-1.png")
            && LauncherRules.IconRelativePath("???") == Path.Combine(LauncherRules.IconFolder, "item.png")
            && LauncherRules.IconRelativePath(null) == Path.Combine(LauncherRules.IconFolder, "item.png")
            && LauncherRules.IconRelativePath("   ") == Path.Combine(LauncherRules.IconFolder, "item.png"));

        check("[软件启动器] IsSafeRelativePath：icons\\a.png 与 icons/a.png 都安全",
            LauncherRules.IsSafeRelativePath(Path.Combine("icons", "a.png"))
            && LauncherRules.IsSafeRelativePath("icons/a.png")
            && LauncherRules.IsSafeRelativePath(@"  icons\a.png  ")
            && LauncherRules.IsSafeRelativePath("a.png"));

        check("[软件启动器] IsSafeRelativePath：绝对路径 / 含 .. 段 / 空 → 不安全",
            !LauncherRules.IsSafeRelativePath(Path.Combine(Path.GetTempPath(), "a.png"))
            && !LauncherRules.IsSafeRelativePath(@"\icons\a.png")
            && !LauncherRules.IsSafeRelativePath(@"icons\..\a.png")
            && !LauncherRules.IsSafeRelativePath("..")
            && !LauncherRules.IsSafeRelativePath("")
            && !LauncherRules.IsSafeRelativePath(null)
            && !LauncherRules.IsSafeRelativePath("   "));

        check("[软件启动器] IsSafeRelativePath：只有整段 .. 才算逃逸（a..b.png 仍是普通文件名）",
            LauncherRules.IsSafeRelativePath(@"icons\a..b.png")
            && LauncherRules.IsSafeRelativePath(@"icons\.\a.png"));
    }

    // ---------------------------------------------------------------- 健康状态

    private static void CheckHealth(CheckFn check)
    {
        var probe = new FakeLauncherProbe()
            .AddFile(@"C:\LTest\ok.exe")
            .AddFile(@"C:\LTest\target.exe")
            .AddFile(@"C:\LTest\readme.txt")
            .AddFile(@"C:\LTest\SomeFolder")
            .AddShortcut(@"C:\LTest\ok.lnk", @"C:\LTest\target.exe")
            .AddShortcut(@"C:\LTest\folder.lnk", @"C:\LTest\SomeFolder")
            .AddShortcut(@"C:\LTest\broken.lnk", "", resolved: false)
            .AddShortcut(@"C:\LTest\empty-target.lnk", "", resolved: true)
            .AddShortcut(@"C:\LTest\stale.lnk", @"C:\LTest\removed.exe");

        check("[软件启动器] CheckHealth：存在的 .exe → Ok",
            LauncherRules.CheckHealth(@"C:\LTest\ok.exe", probe) == LauncherHealth.Ok);

        check("[软件启动器] CheckHealth：不存在的 .exe / 空路径 / null → MissingFile",
            LauncherRules.CheckHealth(@"C:\LTest\gone.exe", probe) == LauncherHealth.MissingFile
            && LauncherRules.CheckHealth("", probe) == LauncherHealth.MissingFile
            && LauncherRules.CheckHealth("   ", probe) == LauncherHealth.MissingFile
            && LauncherRules.CheckHealth((string?)null, probe) == LauncherHealth.MissingFile);

        check("[软件启动器] CheckHealth：不支持的扩展名 → Unsupported（即使文件登记为存在）",
            LauncherRules.CheckHealth(@"C:\LTest\readme.txt", probe) == LauncherHealth.Unsupported
            && LauncherRules.CheckHealth(@"C:\LTest\run.bat", probe) == LauncherHealth.Unsupported);

        check("[软件启动器] CheckHealth：快捷方式文件本身不存在 → MissingFile",
            LauncherRules.CheckHealth(@"C:\LTest\not-there.lnk", probe) == LauncherHealth.MissingFile);

        check("[软件启动器] CheckHealth：快捷方式解析失败 / 目标为空 → BrokenShortcut",
            LauncherRules.CheckHealth(@"C:\LTest\broken.lnk", probe) == LauncherHealth.BrokenShortcut
            && LauncherRules.CheckHealth(@"C:\LTest\empty-target.lnk", probe)
                == LauncherHealth.BrokenShortcut);

        check("[软件启动器] CheckHealth：快捷方式目标不存在 → MissingTarget",
            LauncherRules.CheckHealth(@"C:\LTest\stale.lnk", probe) == LauncherHealth.MissingTarget);

        check("[软件启动器] CheckHealth：快捷方式目标存在（exe / 文件夹）→ Ok",
            LauncherRules.CheckHealth(@"C:\LTest\ok.lnk", probe) == LauncherHealth.Ok
            && LauncherRules.CheckHealth(@"C:\LTest\folder.lnk", probe) == LauncherHealth.Ok);

        check("[软件启动器] CheckHealth：带引号 / 大小写不同的路径也能命中假 probe",
            LauncherRules.CheckHealth("\"C:\\LTEST\\OK.EXE\"", probe) == LauncherHealth.Ok);

        check("[软件启动器] CheckHealth：item 重载走 item.Path，null item 不抛异常",
            LauncherRules.CheckHealth(new LauncherItemConfig { Path = @"C:\LTest\ok.exe" }, probe)
                == LauncherHealth.Ok
            && LauncherRules.CheckHealth((LauncherItemConfig)null!, probe) == LauncherHealth.MissingFile);
    }

    // ---------------------------------------------------------------- 失效项

    private static void CheckFindInvalid(CheckFn check)
    {
        var probe = new FakeLauncherProbe()
            .AddFile(@"C:\FTest\ok.exe")
            .AddFile(@"C:\FTest\target.exe")
            .AddFile(@"C:\FTest\readme.txt")
            .AddShortcut(@"C:\FTest\ok.lnk", @"C:\FTest\target.exe")
            .AddShortcut(@"C:\FTest\broken.lnk", "", resolved: false)
            .AddShortcut(@"C:\FTest\stale.lnk", @"C:\FTest\removed.exe");

        var okExe = new LauncherItemConfig { Path = @"C:\FTest\ok.exe" };
        var goneExe = new LauncherItemConfig { Path = @"C:\FTest\gone.exe" };
        var okLnk = new LauncherItemConfig { Path = @"C:\FTest\ok.lnk" };
        var brokenLnk = new LauncherItemConfig { Path = @"C:\FTest\broken.lnk" };
        var staleLnk = new LauncherItemConfig { Path = @"C:\FTest\stale.lnk" };
        var txt = new LauncherItemConfig { Path = @"C:\FTest\readme.txt" };

        var invalid = LauncherRules.FindInvalid(
            new[] { okExe, goneExe, okLnk, brokenLnk, staleLnk, txt }, probe);
        check("[软件启动器] FindInvalid：只挑失效项，数量正确",
            invalid.Count == 4, $"{invalid.Count} 项");

        check("[软件启动器] FindInvalid：保持原列表顺序（清空无效项时不会跳来跳去）",
            invalid.Count == 4 && ReferenceEquals(invalid[0], goneExe)
            && ReferenceEquals(invalid[1], brokenLnk)
            && ReferenceEquals(invalid[2], staleLnk)
            && ReferenceEquals(invalid[3], txt),
            string.Join(" | ", invalid.Select(item => item.Path)));

        check("[软件启动器] FindInvalid：全部正常时返回空",
            LauncherRules.FindInvalid(new[] { okExe, okLnk }, probe).Count == 0);

        check("[软件启动器] FindInvalid：null / 空集合 / 含 null 元素都不抛异常",
            LauncherRules.FindInvalid(null, probe).Count == 0
            && LauncherRules.FindInvalid(Array.Empty<LauncherItemConfig>(), probe).Count == 0
            && LauncherRules.FindInvalid(new LauncherItemConfig[] { null!, okExe }, probe).Count == 0);
    }

    // ---------------------------------------------------------------- 导入去重

    private static void CheckContainsPath(CheckFn check)
    {
        var items = new List<LauncherItemConfig>
        {
            new() { Path = @"C:\CTest\MixCase.exe" },
            new() { Path = @"C:\CTest\Other.lnk" },
        };

        check("[软件启动器] ContainsPath：大小写 / 引号 / 首尾空白不敏感",
            LauncherRules.ContainsPath(items, @"c:\ctest\mixcase.exe")
            && LauncherRules.ContainsPath(items, "\"C:\\CTest\\MixCase.exe\"")
            && LauncherRules.ContainsPath(items, "  C:\\CTest\\MixCase.exe  "));

        check("[软件启动器] ContainsPath：不同路径 / 空路径 / null / null 列表都不命中",
            !LauncherRules.ContainsPath(items, @"C:\CTest\Another.exe")
            && !LauncherRules.ContainsPath(items, "")
            && !LauncherRules.ContainsPath(items, null)
            && !LauncherRules.ContainsPath(null, @"C:\CTest\MixCase.exe"));

        check("[软件启动器] ContainsPath：相对路径与绝对路径视为同一个文件",
            LauncherRules.ContainsPath(
                new[]
                {
                    new LauncherItemConfig
                    {
                        Path = Path.Combine(Environment.CurrentDirectory, "RelativeTool.exe"),
                    },
                },
                "RelativeTool.exe"));

        check("[软件启动器] ContainsPath：列表里的 null 元素被跳过",
            !LauncherRules.ContainsPath(
                new LauncherItemConfig[] { null!, new() { Path = @"C:\CTest\MixCase.exe" } },
                @"C:\CTest\Nope.exe")
            && LauncherRules.ContainsPath(
                new LauncherItemConfig[] { null!, new() { Path = @"C:\CTest\MixCase.exe" } },
                @"C:\CTest\MixCase.exe"));
    }

    // ---------------------------------------------------------------- 状态说明

    private static void CheckDescribeHealth(CheckFn check)
    {
        var states = new[]
        {
            LauncherHealth.Ok,
            LauncherHealth.MissingFile,
            LauncherHealth.BrokenShortcut,
            LauncherHealth.MissingTarget,
            LauncherHealth.Unsupported,
        };
        var texts = states.Select(LauncherRules.DescribeHealth).ToArray();

        check("[软件启动器] DescribeHealth：五种状态都有非空中文说明",
            texts.All(text => !string.IsNullOrWhiteSpace(text))
            && texts[0] == "正常",
            string.Join(" | ", texts));

        check("[软件启动器] DescribeHealth：能区分文件没了 / 快捷方式坏了 / 目标没了 / 类型不支持",
            texts.Distinct().Count() == 5
            && texts[1].Contains("文件不存在")
            && texts[2].Contains("快捷方式") && texts[2].Contains("损坏")
            && texts[3].Contains("目标") && texts[3].Contains("卸载")
            && texts[4].Contains("不支持")
            && LauncherRules.DescribeHealth((LauncherHealth)999).Contains("不支持"));
    }

    // ---------------------------------------------------------------- 配置 / JSON

    private static void CheckConfig(CheckFn check)
    {
        var config = new LauncherConfig();
        check("[软件启动器] 配置默认值：启动项为空 / 自动刷新状态打开 / 联动启动关闭 / 没有选中项",
            config.Items.Count == 0 && config.AutoRefreshStatus && config.LastSelectedId == ""
            && config.ItemCount == 0 && !config.StartLinkedOnToolkitStartup && config.LinkedItemCount == 0,
            $"autoRefresh={config.AutoRefreshStatus} / 联动总开关={config.StartLinkedOnToolkitStartup}");

        var exe = new LauncherItemConfig
        {
            Name = "微信",
            Path = @"C:\Apps\WeChat.exe",
            CustomIconPath = LauncherRules.IconRelativePath("abc-123"),
            AddedAt = new DateTime(2025, 1, 2, 3, 4, 5),
        };
        check("[软件启动器] 启动项计算属性：Id 是 GUID / 是否快捷方式 / 文件名 / 有无自定义图标",
            Guid.TryParse(exe.Id, out _) && !exe.IsShortcut && exe.FileName == "WeChat"
            && exe.HasCustomIcon && LauncherRules.IsSafeRelativePath(exe.CustomIconPath),
            $"{exe.Id} / {exe.FileName} / {exe.CustomIconPath}");

        var lnk = new LauncherItemConfig
        {
            Name = "微信快捷方式",
            Path = @"C:\Users\Me\Desktop\微信.lnk",
            AddedAt = new DateTime(2025, 6, 7, 8, 9, 10),
        };
        check("[软件启动器] 启动项计算属性：.lnk 的 IsShortcut 为 true、默认没有自定义图标",
            lnk.IsShortcut && lnk.FileName == "微信" && !lnk.HasCustomIcon);

        check("[软件启动器] 启动项联动开关：默认关闭（不会在用户没要求时启动程序）",
            !exe.StartWithToolkit && !lnk.StartWithToolkit);

        config.Items.Add(exe);
        config.Items.Add(lnk);
        config.AutoRefreshStatus = false;
        config.StartLinkedOnToolkitStartup = true;
        exe.StartWithToolkit = true;
        config.LastSelectedId = exe.Id;

        var json = JsonFileStore.Serialize(config);
        check("[软件启动器] 配置 JSON：只落盘可写字段，[JsonIgnore] 计算属性不出现",
            json.Contains("\"items\"") && json.Contains("\"autoRefreshStatus\"")
            && json.Contains("\"lastSelectedId\"")
            && json.Contains("\"startLinkedOnToolkitStartup\"")
            && json.Contains("\"startWithToolkit\"")
            && !json.Contains("isShortcut", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("fileName", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("hasCustomIcon", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("itemCount", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("linkedItemCount", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("filePath", StringComparison.OrdinalIgnoreCase),
            json.Contains("\"items\"") ? "items 已落盘" : "items 缺失");

        var restored = JsonSerializer.Deserialize<LauncherConfig>(json, JsonFileStore.Options);
        var restoredExe = restored?.Items.FirstOrDefault(item => item.Id == exe.Id);
        var restoredLnk = restored?.Items.FirstOrDefault(item => item.Id == lnk.Id);

        check("[软件启动器] 配置 JSON 往返：Items / Name / Path / CustomIconPath / AddedAt 保留",
            restored is not null && restored.Items.Count == 2
            && restoredExe is not null && restoredExe.Name == "微信"
            && restoredExe.Path == @"C:\Apps\WeChat.exe"
            && restoredExe.CustomIconPath == exe.CustomIconPath
            && restoredExe.AddedAt == exe.AddedAt,
            restored is null ? "反序列化为 null" : $"{restored.Items.Count} 项");

        check("[软件启动器] 配置 JSON 往返：设置与选中 Id 保留，计算属性按新对象重算",
            restored is not null && !restored.AutoRefreshStatus
            && restored.LastSelectedId == exe.Id && restored.ItemCount == 2
            && restoredExe is not null && restoredExe.HasCustomIcon
            && restoredLnk is not null && restoredLnk.IsShortcut && restoredLnk.FileName == "微信"
            && restoredLnk.AddedAt == new DateTime(2025, 6, 7, 8, 9, 10),
            restored is null ? "反序列化为 null" : restored.LastSelectedId);

        check("[软件启动器] 配置 JSON 往返：总开关与逐项「联动启动」勾选都保留",
            restored is not null && restored.StartLinkedOnToolkitStartup
            && restored.LinkedItemCount == 1
            && restoredExe is not null && restoredExe.StartWithToolkit
            && restoredLnk is not null && !restoredLnk.StartWithToolkit,
            restored is null ? "反序列化为 null" : $"联动 {restored.LinkedItemCount} 项");

        check("[软件启动器] 配置 JSON 是 camelCase 且中文不转义（人眼可读、可手改）",
            json.Contains("\"autoRefreshStatus\"") && json.Contains("\"addedAt\"")
            && json.Contains("\"customIconPath\"") && json.Contains("微信"),
            "camelCase / 中文原样");
    }

    // ---------------------------------------------------------------- 联动启动

    private static void CheckLinkedStartupRules(CheckFn check)
    {
        check("[软件启动器] 联动启动：工具实现了「启动后任务」契约（外壳在主窗口显示后调一次）",
            new LauncherTool() is YuLiangToolkit.Core.Tools.IStartupTask,
            "LauncherTool : ITool, IStartupTask");

        check("[软件启动器] 联动启动：启动请求给 exe 带起始位置（它自己所在目录）",
            LauncherStartService.BuildRequest(@"C:\Apps\WeChat.exe") is { } exeRequest
            && exeRequest.FileName == @"C:\Apps\WeChat.exe"
            && exeRequest.WorkingDirectory == @"C:\Apps",
            LauncherStartService.BuildRequest(@"C:\Apps\WeChat.exe")?.WorkingDirectory ?? "(null)");

        check("[软件启动器] 联动启动：快捷方式不带起始位置（参数 / 起始位置交给 Windows Shell）",
            LauncherStartService.BuildRequest(@"C:\Users\Me\Desktop\微信.lnk") is { } lnkRequest
            && lnkRequest.WorkingDirectory == ""
            && lnkRequest.FileName.EndsWith("微信.lnk", StringComparison.Ordinal));

        check("[软件启动器] 联动启动：空 / 非法路径不给启动请求（不会误启动别的东西）",
            LauncherStartService.BuildRequest(null) is null
            && LauncherStartService.BuildRequest("") is null
            && LauncherStartService.BuildRequest("   ") is null);

        check("[软件启动器] 联动启动：状态文案区分「没勾」「总开关关着」「正常」",
            LauncherRules.DescribeLinkedStartup(false, 0).Contains("未勾选")
            && LauncherRules.DescribeLinkedStartup(true, 0).Contains("未勾选")
            && LauncherRules.DescribeLinkedStartup(false, 3).Contains("已关闭")
            && LauncherRules.DescribeLinkedStartup(false, 3).Contains("3")
            && LauncherRules.DescribeLinkedStartup(true, 3).Contains("3")
            && !LauncherRules.DescribeLinkedStartup(true, 3).Contains("已关闭"),
            LauncherRules.DescribeLinkedStartup(true, 3));
    }

    private static void CheckLinkedStartupSelection(CheckFn check)
    {
        var probe = new FakeLauncherProbe()
            .AddFile(@"C:\LStart\a.exe")
            .AddFile(@"C:\LStart\b.exe")
            .AddFile(@"C:\LStart\c.exe");

        var a = new LauncherItemConfig { Name = "A", Path = @"C:\LStart\a.exe", StartWithToolkit = true };
        var b = new LauncherItemConfig { Name = "B", Path = @"C:\LStart\b.exe" };
        var c = new LauncherItemConfig { Name = "C", Path = @"C:\LStart\c.exe", StartWithToolkit = true };
        var gone = new LauncherItemConfig { Name = "没了", Path = @"C:\LStart\gone.exe", StartWithToolkit = true };

        var config = new LauncherConfig();
        config.Items.Add(a);
        config.Items.Add(b);
        config.Items.Add(c);
        config.Items.Add(gone);
        check("[软件启动器] 联动启动：LinkedItemCount 只数勾了的项（不管失效与否）",
            config.LinkedItemCount == 3, config.LinkedItemCount.ToString());

        var selected = LauncherStartService.SelectStartupItems(config.Items, probe);
        check("[软件启动器] 联动启动：只挑勾了开关 + 当前可用的项，保持列表顺序",
            selected.Count == 2 && ReferenceEquals(selected[0], a) && ReferenceEquals(selected[1], c),
            string.Join(" / ", selected.Select(item => item.Name)));

        check("[软件启动器] 联动启动：失效项跳过（开机时不弹「找不到程序」）",
            !selected.Any(item => ReferenceEquals(item, gone)));

        check("[软件启动器] 联动启动：列表 null / 空 / 含 null 元素都不炸",
            LauncherStartService.SelectStartupItems(null, probe).Count == 0
            && LauncherStartService.SelectStartupItems(Array.Empty<LauncherItemConfig>(), probe).Count == 0
            && LauncherStartService.SelectStartupItems(
                new LauncherItemConfig[] { null!, new() { Path = @"C:\LStart\a.exe", StartWithToolkit = true } },
                probe).Count == 1);

        var duplicated = LauncherStartService.SelectStartupItems(
            new[]
            {
                new LauncherItemConfig { Path = @"C:\LStart\a.exe", StartWithToolkit = true },
                new LauncherItemConfig { Path = @"c:\lstart\A.EXE", StartWithToolkit = true },
            },
            probe);
        check("[软件启动器] 联动启动：同一路径只启动一次（大小写不敏感）",
            duplicated.Count == 1, duplicated.Count.ToString());
    }

    private static void CheckLinkedStartupStartMany(CheckFn check)
    {
        var starter = new FakeStarter(failOn: @"C:\LStart\broken.exe");
        var items = new[]
        {
            new LauncherItemConfig { Name = "A", Path = @"C:\LStart\a.exe" },
            new LauncherItemConfig { Name = "坏", Path = @"C:\LStart\broken.exe" },
            new LauncherItemConfig { Name = "B", Path = @"C:\LStart\b.exe" },
            new LauncherItemConfig { Name = "空", Path = "" },
        };

        var reported = new List<string>();
        var outcome = LauncherStartService.StartMany(items, starter,
            (item, error) => reported.Add($"{item.Name}:{(error is null ? "ok" : "fail")}"));

        check("[软件启动器] 联动启动：逐个启动，一项失败不影响后面的项",
            outcome.Started == 2 && starter.Requests.Count == 2
            && starter.Requests[0].FileName == @"C:\LStart\a.exe"
            && starter.Requests[1].FileName == @"C:\LStart\b.exe",
            $"启动 {outcome.Started} 项 / 请求 {starter.Requests.Count} 次");

        check("[软件启动器] 联动启动：失败项（启动异常 / 路径为空）记进结果里，名字可读",
            outcome.Failures.Count == 2 && !outcome.AllStarted
            && outcome.Failures[0].StartsWith("坏", StringComparison.Ordinal)
            && outcome.Failures[1].StartsWith("空", StringComparison.Ordinal),
            string.Join(" | ", outcome.Failures));

        check("[软件启动器] 联动启动：每项都给回调（成功 / 失败都报）",
            reported.Count == 4 && reported[0] == "A:ok" && reported[1] == "坏:fail"
            && reported[2] == "B:ok" && reported[3] == "空:fail",
            string.Join(" / ", reported));

        var allOk = LauncherStartService.StartMany(
            new[] { new LauncherItemConfig { Name = "A", Path = @"C:\LStart\a.exe" } },
            new FakeStarter(failOn: ""));
        check("[软件启动器] 联动启动：全部成功时 AllStarted 为 true",
            allOk.AllStarted && allOk.Started == 1);

        check("[软件启动器] 联动启动：空列表 / null 不启动任何东西",
            LauncherStartService.StartMany(null, new FakeStarter("")).Started == 0
            && LauncherStartService.StartMany(Array.Empty<LauncherItemConfig>(), new FakeStarter("")).Started == 0);
    }

    // ---------------------------------------------------------------- 假探测

    /// <summary>
    /// 自检用的假探测：文件在不在、快捷方式解析出什么，全部由字典写死。
    /// 键统一走 <see cref="LauncherRules.NormalizePath"/>（与生产代码同一条规范化路径），
    /// 并忽略大小写，模拟 Windows 文件系统；不碰真实磁盘。
    /// </summary>
    private sealed class FakeLauncherProbe : ILauncherProbe
    {
        private readonly Dictionary<string, bool> _exists = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ShortcutTarget> _shortcuts = new(StringComparer.OrdinalIgnoreCase);

        public FakeLauncherProbe AddFile(string path)
        {
            _exists[Key(path)] = true;
            return this;
        }

        public FakeLauncherProbe AddShortcut(string shortcutPath, string targetPath, bool resolved = true)
        {
            _exists[Key(shortcutPath)] = true;
            _shortcuts[Key(shortcutPath)] = new ShortcutTarget(
                targetPath, "", Path.GetDirectoryName(targetPath) ?? "", "", 0, resolved);
            return this;
        }

        public bool Exists(string path) => _exists.TryGetValue(Key(path), out var exists) && exists;

        public ShortcutTarget ResolveShortcut(string shortcutPath)
            => _shortcuts.TryGetValue(Key(shortcutPath), out var target) ? target : ShortcutTarget.Unresolved;

        private static string Key(string? path) => LauncherRules.NormalizePath(path);
    }

    /// <summary>
    /// 假的启动实现：只记录「被要求启动什么」，绝不真的开进程；
    /// <paramref name="failOn"/> 那个路径抛异常，用来验证「一项失败不影响后面」。
    /// </summary>
    private sealed class FakeStarter : ILauncherProcessStarter
    {
        private readonly string _failOn;

        public FakeStarter(string failOn) => _failOn = LauncherRules.NormalizePath(failOn);

        public List<LauncherStartRequest> Requests { get; } = new();

        public void Start(LauncherStartRequest request)
        {
            if (_failOn.Length > 0 && LauncherRules.SamePath(request.FileName, _failOn))
                throw new InvalidOperationException("找不到文件");

            Requests.Add(request);
        }
    }
}
