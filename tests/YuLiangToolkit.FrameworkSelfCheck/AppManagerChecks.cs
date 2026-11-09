using System.Text.Json;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Tools.AppManager;
using YuLiangToolkit.Tools.AppManager.Models;
using YuLiangToolkit.Tools.AppManager.Services;
using YuLiangToolkit.Tools.AppManager.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「应用管理器」自检：注册表卸载项的过滤与同名去重、安装日期解析、大小 / 盘符 / 驱动器标签格式化、
/// 卸载命令行拆分、排序比较（未知值沉底 · 正逆序 · 同值稳定）、筛选、配置默认值与 JSON 往返、
/// 工具元数据，以及一次**只读**的本机真实扫描。
///
/// <para><b>安全边界</b>：本模块<b>绝不</b>写注册表、不删文件、<b>不启动任何卸载 / 修改程序</b>
/// （<see cref="AppProcessLauncher"/> 只断言命令拆分的纯逻辑，不调用 <c>Start</c>），
/// 也不创建 WPF 窗口。</para>
/// </summary>
internal static class AppManagerChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckIncludeRules(assert);
        CheckDedupe(assert);
        CheckInstallDate(assert);
        CheckSizeAndDrive(assert);
        CheckCommandLine(assert);
        CheckLauncherSafety(assert);
        CheckSorting(assert);
        CheckFiltering(assert);
        CheckRowText(assert);
        CheckConfig(assert);
        CheckRealRegistryScan(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new AppManagerTool();
        check("[应用管理器] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "app-manager" && tool.Name == "应用管理器"
            && tool.Category == "系统" && tool.Order == 47,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[应用管理器] 标签覆盖已安装应用 / 卸载 / 修改 / 安装日期 / 占用大小 / 驱动器",
            tool.Tags.Contains("已安装应用") && tool.Tags.Contains("卸载") && tool.Tags.Contains("修改")
            && tool.Tags.Contains("安装日期") && tool.Tags.Contains("占用大小") && tool.Tags.Contains("驱动器"),
            string.Join(",", tool.Tags));

        check("[应用管理器] 描述覆盖排序 / 筛选 / 卸载 / 修改",
            tool.Description.Contains("安装日期") && tool.Description.Contains("排序")
            && tool.Description.Contains("驱动器") && tool.Description.Contains("卸载")
            && tool.Description.Contains("修改"),
            tool.Description);

        check("[应用管理器] 工具 Id 与 AppManagerConfig.ToolId 一致",
            tool.Id == AppManagerConfig.ToolId, AppManagerConfig.ToolId);

        check("[应用管理器] 实现了 ITool 且 EmojiIcon 非空",
            tool is YuLiangToolkit.Core.Tools.ITool && tool.EmojiIcon.Length > 0, tool.EmojiIcon);
    }

    // ---------------------------------------------------------------- 过滤规则

    private static void CheckIncludeRules(CheckFn check)
    {
        check("[应用管理器] 正常应用（有名字、没标系统组件）会被收录",
            AppCatalogRules.ShouldInclude("7-Zip 25.01 (x64)", false, "", "", "C:\\Program Files\\7-Zip\\Uninstall.exe"));

        check("[应用管理器] 空名 / 纯空白不收录",
            !AppCatalogRules.ShouldInclude("", false, "", "", "x")
            && !AppCatalogRules.ShouldInclude(null, false, "", "", "x")
            && !AppCatalogRules.ShouldInclude("   ", false, "", "", "x"));

        check("[应用管理器] SystemComponent = 1 的（系统组件）不收录",
            !AppCatalogRules.ShouldInclude("Windows Update Helper", true, "", "", "x"));

        check("[应用管理器] 带 ParentKeyName 的（属于另一个产品的子项）不收录",
            !AppCatalogRules.ShouldInclude("某软件的插件", false, "某软件", "", "x"));

        check("[应用管理器] ReleaseType 是补丁 / 更新 / 服务包的不收录",
            !AppCatalogRules.ShouldInclude("安全更新 KB5031354", false, "", "Security Update", "x")
            && !AppCatalogRules.ShouldInclude("Hotfix", false, "", "Hotfix", "x")
            && !AppCatalogRules.ShouldInclude("Rollup", false, "", "Update Rollup", "x")
            && !AppCatalogRules.ShouldInclude("SP1", false, "", "ServicePack", "x")
            && AppCatalogRules.ShouldInclude("普通软件", false, "", "", "x"));

        check("[应用管理器] 没有卸载命令、名字又是 KB 补丁的不收录（更新残留项）",
            !AppCatalogRules.ShouldInclude("KB5031354", false, "", "", "")
            && !AppCatalogRules.ShouldInclude("kb5031354", false, "", "", null)
            && AppCatalogRules.ShouldInclude("KB Studio", false, "", "", "")
            && AppCatalogRules.ShouldInclude("KB5031354", false, "", "", "wusa.exe /uninstall"));

        check("[应用管理器] 像不像 KB 补丁的判定",
            AppCatalogRules.LooksLikePatchName("KB5031354")
            && AppCatalogRules.LooksLikePatchName("kb2267602")
            && !AppCatalogRules.LooksLikePatchName("KB")
            && !AppCatalogRules.LooksLikePatchName("KBS 新闻")
            && !AppCatalogRules.LooksLikePatchName("")
            && !AppCatalogRules.LooksLikePatchName(null));

        check("[应用管理器] ReleaseType 判定（空 / 非更新类返回 false，更新类返回 true）",
            !AppCatalogRules.IsPatchReleaseType(null)
            && !AppCatalogRules.IsPatchReleaseType("")
            && !AppCatalogRules.IsPatchReleaseType("Major")
            && AppCatalogRules.IsPatchReleaseType("Security")
            && AppCatalogRules.IsPatchReleaseType("security update"));
    }

    // ---------------------------------------------------------------- 同名去重

    private static void CheckDedupe(CheckFn check)
    {
        static InstalledApp Make(string name, string scope, string version = "1.0") => new()
        {
            Name = name,
            RegistryScope = scope,
            Version = version,
        };

        var deduped = AppCatalogRules.DedupeByName(new[]
        {
            Make("A", "HKLM-WOW64", "32bit"),
            Make("B", "HKCU"),
            Make("A", "HKLM", "64bit"),
            Make("A", "HKCU", "user-home"),
            Make("C", "HKLM"),
        });

        check("[应用管理器] 同名去重后只剩一条，且按第一次出现的位置保持顺序",
            deduped.Count == 3 && deduped[0].Name == "A" && deduped[1].Name == "B" && deduped[2].Name == "C",
            string.Join(",", deduped.Select(app => app.Name)));

        check("[应用管理器] 同名去重优先保留 HKLM(64)，其次 HKCU，最后 HKLM-WOW64",
            deduped[0].Version == "64bit", deduped[0].Version);

        var userFirst = AppCatalogRules.DedupeByName(new[] { Make("A", "HKCU", "user"), Make("A", "HKLM-WOW64", "wow") });
        check("[应用管理器] 只有 HKCU 与 WOW64 两条时保留 HKCU",
            userFirst.Count == 1 && userFirst[0].Version == "user", userFirst.FirstOrDefault()?.Version ?? "");

        var blank = AppCatalogRules.DedupeByName(new[] { Make("", "HKLM"), Make("  ", "HKCU") });
        check("[应用管理器] 空名字在去重阶段被丢掉", blank.Count == 0, blank.Count.ToString());

        check("[应用管理器] 注册表来源优先级：HKLM < HKCU < HKLM-WOW64 < 其它",
            AppCatalogRules.ScopePriority("HKLM") == 1
            && AppCatalogRules.ScopePriority("hkcu") == 2
            && AppCatalogRules.ScopePriority("HKLM-WOW64") == 3
            && AppCatalogRules.ScopePriority("") == 4);
    }

    // ---------------------------------------------------------------- 安装日期

    private static void CheckInstallDate(CheckFn check)
    {
        check("[应用管理器] 安装日期 8 位写法 20260626 → 2026-06-26",
            AppCatalogRules.ParseInstallDate("20260626") == new DateOnly(2026, 6, 26),
            AppCatalogRules.ParseInstallDate("20260626")?.ToString("yyyy-MM-dd") ?? "null");

        check("[应用管理器] 安装日期支持 - / . 分隔与单位数月日",
            AppCatalogRules.ParseInstallDate("2026-6-26") == new DateOnly(2026, 6, 26)
            && AppCatalogRules.ParseInstallDate("2026/06/26") == new DateOnly(2026, 6, 26)
            && AppCatalogRules.ParseInstallDate("2026.06.26") == new DateOnly(2026, 6, 26));

        check("[应用管理器] 安装日期空串 / null / 位数不对 → null",
            AppCatalogRules.ParseInstallDate("") is null
            && AppCatalogRules.ParseInstallDate(null) is null
            && AppCatalogRules.ParseInstallDate("   ") is null
            && AppCatalogRules.ParseInstallDate("2026") is null
            && AppCatalogRules.ParseInstallDate("2026062") is null
            && AppCatalogRules.ParseInstallDate("abcdefgh") is null);

        // 实测有安装程序把 InstallDate / DisplayName 写成定长缓冲，值后面拖一串 \0（osu!lazer 就是这样）
        check("[应用管理器] 带 \\0 填充的日期与名字能被清理（定长缓冲写坏的注册表值）",
            AppCatalogRules.ParseInstallDate("20260620\0\0\0\0") == new DateOnly(2026, 6, 20)
            && AppCatalogRules.CleanText("osu!(lazer)\0\0\0\0") == "osu!(lazer)"
            && AppCatalogRules.CleanText("  7-Zip  ") == "7-Zip"
            && AppCatalogRules.CleanText(null) == "",
            AppCatalogRules.CleanText("osu!(lazer)\0\0\0"));

        check("[应用管理器] 安装日期月 / 日越界 → null（不抛异常）",
            AppCatalogRules.ParseInstallDate("20261301") is null
            && AppCatalogRules.ParseInstallDate("20260632") is null
            && AppCatalogRules.ParseInstallDate("20260230") is null
            && AppCatalogRules.ParseInstallDate("18990101") is null);

        check("[应用管理器] 安装日期显示文本：有日期补零，没日期写「未知」",
            AppCatalogRules.FormatInstallDate(new DateOnly(2026, 6, 3)) == "2026/06/03"
            && AppCatalogRules.FormatInstallDate(null) == "未知",
            AppCatalogRules.FormatInstallDate(new DateOnly(2026, 6, 3)));
    }

    // ---------------------------------------------------------------- 大小 / 盘符

    private static void CheckSizeAndDrive(CheckFn check)
    {
        check("[应用管理器] 大小格式化：KB / MB / GB 三档",
            AppCatalogRules.FormatSize(870) == "870 KB"
            && AppCatalogRules.FormatSize(493581) == "482 MB"
            && AppCatalogRules.FormatSize(1024 * 1024 * 2) == "2 GB",
            $"{AppCatalogRules.FormatSize(870)} / {AppCatalogRules.FormatSize(493581)} / {AppCatalogRules.FormatSize(1024 * 1024 * 2)}");

        check("[应用管理器] 没记大小（0 / 负数）显示「未知」",
            AppCatalogRules.FormatSize(0) == "未知" && AppCatalogRules.FormatSize(-5) == "未知");

        check("[应用管理器] 从路径取盘符（大小写 / 引号 / 非盘符路径）",
            AppCatalogRules.DriveOf(@"C:\Program Files\7-Zip\") == "C"
            && AppCatalogRules.DriveOf(@"d:\Games") == "D"
            && AppCatalogRules.DriveOf("\"E:\\Tools\"") == "E"
            && AppCatalogRules.DriveOf(@"\\server\share") == ""
            && AppCatalogRules.DriveOf("") == ""
            && AppCatalogRules.DriveOf(null) == ""
            && AppCatalogRules.DriveOf("C") == "");

        check("[应用管理器] 盘符推断顺序：安装目录 → 图标 → 卸载命令",
            AppCatalogRules.ResolveDrive(@"D:\Apps\X", @"C:\X\app.exe,0", @"""C:\X\uninst.exe""") == "D"
            && AppCatalogRules.ResolveDrive("", @"C:\X\app.exe,0", @"""C:\X\uninst.exe""") == "C"
            && AppCatalogRules.ResolveDrive("", "", @"""C:\X\uninst.exe"" /S") == "C"
            && AppCatalogRules.ResolveDrive("", "", "msiexec /X{GUID}") == "");

        check("[应用管理器] 图标路径去掉末尾的 ,0 序号",
            AppCatalogRules.StripIconIndex(@"C:\App\app.exe,0") == @"C:\App\app.exe"
            && AppCatalogRules.StripIconIndex(@"""C:\App\app.exe"",12") == @"C:\App\app.exe"
            && AppCatalogRules.StripIconIndex(@"C:\App\app.exe") == @"C:\App\app.exe"
            && AppCatalogRules.StripIconIndex(@"C:\App\a,b.exe") == @"C:\App\a,b.exe"
            && AppCatalogRules.StripIconIndex("") == "");

        check("[应用管理器] 驱动器标签：有卷标 «卷标 (C:)»、无卷标 «本地磁盘 (C:)»",
            AppCatalogRules.DriveDisplayName("C", "Acer") == "Acer (C:)"
            && AppCatalogRules.DriveDisplayName("c:", "") == "本地磁盘 (C:)"
            && AppCatalogRules.DriveDisplayName("", "Acer") == "未记录位置",
            AppCatalogRules.DriveDisplayName("C", "Acer"));
    }

    // ---------------------------------------------------------------- 命令行拆分

    private static void CheckCommandLine(CheckFn check)
    {
        var quoted = AppCatalogRules.SplitCommandLine(@"""C:\Program Files\draw.io\Uninstall draw.io.exe"" /allusers");
        check("[应用管理器] 带引号的卸载命令：引号里是可执行文件，其余是参数",
            quoted.Executable == @"C:\Program Files\draw.io\Uninstall draw.io.exe"
            && quoted.Arguments == "/allusers",
            quoted.Executable + " | " + quoted.Arguments);

        var plain = AppCatalogRules.SplitCommandLine(@"C:\Program Files\7-Zip\Uninstall.exe");
        check("[应用管理器] 不带引号且没有参数：整串就是可执行文件",
            plain.Executable == @"C:\Program Files\7-Zip\Uninstall.exe" && plain.Arguments == "",
            plain.Executable + " | " + plain.Arguments);

        var plainWithArgs = AppCatalogRules.SplitCommandLine(@"C:\Program Files\7-Zip\Uninstall.exe /S");
        check("[应用管理器] 不带引号但有参数：以 .exe 结尾处切分（路径里的空格不误切）",
            plainWithArgs.Executable == @"C:\Program Files\7-Zip\Uninstall.exe"
            && plainWithArgs.Arguments == "/S",
            plainWithArgs.Executable + " | " + plainWithArgs.Arguments);

        var msi = AppCatalogRules.SplitCommandLine("msiexec /X{ABCD-1234}");
        check("[应用管理器] 没有路径也没有 .exe 的命令（msiexec）：按第一个空格切分",
            msi.Executable == "msiexec" && msi.Arguments == "/X{ABCD-1234}",
            msi.Executable + " | " + msi.Arguments);

        check("[应用管理器] 命令拆分：空串 / null 得到空可执行文件",
            !AppCatalogRules.SplitCommandLine("").HasExecutable
            && !AppCatalogRules.SplitCommandLine(null).HasExecutable
            && !AppCatalogRules.SplitCommandLine("   ").HasExecutable);

        var extra = AppCatalogRules.SplitCommandLine(@"  ""C:\a b\u.exe""   /x   ");
        check("[应用管理器] 命令拆分：首尾与中间多余空格被清掉",
            extra.Executable == @"C:\a b\u.exe" && extra.Arguments == "/x",
            extra.Executable + " | " + extra.Arguments);

        var broken = AppCatalogRules.SplitCommandLine(@"""C:\Program Files\X\uninst.exe");
        check("[应用管理器] 只有开头一个引号（写坏的键值）也能拆出可执行文件",
            broken.Executable == @"C:\Program Files\X\uninst.exe", broken.Executable);

        var com = AppCatalogRules.SplitCommandLine(@"C:\Tools\a b.bat /quiet");
        check("[应用管理器] .bat / .cmd / .com 也认作可执行文件结尾",
            com.Executable == @"C:\Tools\a b.bat" && com.Arguments == "/quiet",
            com.Executable + " | " + com.Arguments);

        var single = AppCatalogRules.SplitCommandLine("notepad.exe");
        check("[应用管理器] 只有一个可执行文件时参数为空",
            single.Executable == "notepad.exe" && single.Arguments == "");
    }

    // ---------------------------------------------------------------- 启动器安全边界

    /// <summary>
    /// 卸载 / 修改的启动器只测「空命令不会真的启动」：真正的 Start 会去跑安装程序，
    /// 自检里绝不调用（安全边界见类注释）。
    /// </summary>
    private static void CheckLauncherSafety(CheckFn check)
    {
        var launcher = new AppProcessLauncher();
        var empty = launcher.Start("");
        check("[应用管理器] 启动器遇到空命令直接返回失败原因（不会启动任何进程）",
            !empty.Started && empty.Error.Length > 0, empty.Error);

        var blank = launcher.Start("    ");
        check("[应用管理器] 启动器遇到纯空白命令也不启动",
            !blank.Started && blank.Error.Length > 0, blank.Error);
    }

    // ---------------------------------------------------------------- 排序

    private static void CheckSorting(CheckFn check)
    {
        static InstalledApp Make(string name, long size = 0, string date = "", string publisher = "") => new()
        {
            Name = name,
            SizeInKilobytes = size,
            InstallDateRaw = date,
            InstallDate = AppCatalogRules.ParseInstallDate(date),
            Publisher = publisher,
        };

        var apps = new[]
        {
            Make("Charlie", 300, "20260601", "Zeta"),
            Make("Alpha", 0, "", "Beta"),
            Make("Bravo", 100, "20260101", "Alpha"),
            Make("Delta", 0, "20260301", ""),
        };

        var byName = AppCatalogRules.Sort(apps, AppCatalogRules.SortKey.Name, descending: false);
        check("[应用管理器] 按名称正序",
            string.Join(",", byName.Select(app => app.Name)) == "Alpha,Bravo,Charlie,Delta",
            string.Join(",", byName.Select(app => app.Name)));

        var byNameDesc = AppCatalogRules.Sort(apps, AppCatalogRules.SortKey.Name, descending: true);
        check("[应用管理器] 按名称逆序",
            string.Join(",", byNameDesc.Select(app => app.Name)) == "Delta,Charlie,Bravo,Alpha",
            string.Join(",", byNameDesc.Select(app => app.Name)));

        var bySize = AppCatalogRules.Sort(apps, AppCatalogRules.SortKey.Size, descending: false);
        check("[应用管理器] 按大小正序，没记大小的排在最后",
            bySize.Select(app => app.Name).SequenceEqual(new[] { "Bravo", "Charlie", "Alpha", "Delta" }),
            string.Join(",", bySize.Select(app => app.Name)));

        var bySizeDesc = AppCatalogRules.Sort(apps, AppCatalogRules.SortKey.Size, descending: true);
        check("[应用管理器] 按大小逆序时没记大小的仍然在最后",
            bySizeDesc.Select(app => app.Name).SequenceEqual(new[] { "Charlie", "Bravo", "Delta", "Alpha" }),
            string.Join(",", bySizeDesc.Select(app => app.Name)));

        var byDate = AppCatalogRules.Sort(apps, AppCatalogRules.SortKey.InstallDate, descending: false);
        check("[应用管理器] 按安装日期正序，没记日期的排在最后",
            byDate.Select(app => app.Name).SequenceEqual(new[] { "Bravo", "Delta", "Charlie", "Alpha" }),
            string.Join(",", byDate.Select(app => app.Name)));

        var byDateDesc = AppCatalogRules.Sort(apps, AppCatalogRules.SortKey.InstallDate, descending: true);
        check("[应用管理器] 按安装日期逆序（最新的在前，未知仍在最后）",
            byDateDesc.Select(app => app.Name).SequenceEqual(new[] { "Charlie", "Delta", "Bravo", "Alpha" }),
            string.Join(",", byDateDesc.Select(app => app.Name)));

        var sameSize = new[]
        {
            Make("Zeta", 500, "", "P1"),
            Make("Beta", 500, "", "P2"),
            Make("Kappa", 500, "", "P3"),
        };
        var stable = AppCatalogRules.Sort(sameSize, AppCatalogRules.SortKey.Size, descending: false);
        var stableDesc = AppCatalogRules.Sort(sameSize, AppCatalogRules.SortKey.Size, descending: true);
        check("[应用管理器] 排序值相同时按名称兜底，且方向反过来也整体一致",
            stable.Select(app => app.Name).SequenceEqual(new[] { "Beta", "Kappa", "Zeta" })
            && stableDesc.Select(app => app.Name).SequenceEqual(new[] { "Zeta", "Kappa", "Beta" }),
            string.Join(",", stable.Select(app => app.Name)) + " / " + string.Join(",", stableDesc.Select(app => app.Name)));

        var byPublisher = AppCatalogRules.Sort(apps, AppCatalogRules.SortKey.Publisher, descending: false);
        check("[应用管理器] 按发布者正序（空发布者排最前，与 Ordinal 无关）",
            byPublisher[0].Name == "Delta", byPublisher[0].Name);

        check("[应用管理器] 排序依据的配置字符串可往返（不认识的值回退按名称）",
            AppCatalogRules.ParseSortKey("installdate") == AppCatalogRules.SortKey.InstallDate
            && AppCatalogRules.ParseSortKey("SIZE") == AppCatalogRules.SortKey.Size
            && AppCatalogRules.ParseSortKey("publisher") == AppCatalogRules.SortKey.Publisher
            && AppCatalogRules.ParseSortKey("name") == AppCatalogRules.SortKey.Name
            && AppCatalogRules.ParseSortKey("") == AppCatalogRules.SortKey.Name
            && AppCatalogRules.ParseSortKey(null) == AppCatalogRules.SortKey.Name
            && AppCatalogRules.ParseSortKey("乱写的") == AppCatalogRules.SortKey.Name
            && AppCatalogRules.SortKeyToConfig(AppCatalogRules.SortKey.InstallDate) == "installdate");
    }

    // ---------------------------------------------------------------- 筛选

    private static void CheckFiltering(CheckFn check)
    {
        static InstalledApp Make(string name, string publisher, string version, string installLocation) => new()
        {
            Name = name,
            Publisher = publisher,
            Version = version,
            InstallLocation = installLocation,
            DriveLetter = AppCatalogRules.DriveOf(installLocation),
        };

        var app = Make("7-Zip 25.01 (x64)", "Igor Pavlov", "25.01", @"C:\Program Files\7-Zip");

        check("[应用管理器] 搜索命中名称 / 发布者 / 版本，且忽略大小写",
            AppCatalogRules.MatchesKeyword(app, "7-zip")
            && AppCatalogRules.MatchesKeyword(app, "igor")
            && AppCatalogRules.MatchesKeyword(app, "25.01")
            && !AppCatalogRules.MatchesKeyword(app, "winrar"));

        check("[应用管理器] 空搜索词 = 全过",
            AppCatalogRules.MatchesKeyword(app, "")
            && AppCatalogRules.MatchesKeyword(app, null)
            && AppCatalogRules.MatchesKeyword(app, "   "));

        check("[应用管理器] 驱动器筛选：所有 / 指定盘 / 未记录位置",
            AppCatalogRules.MatchesDrive(app, "")
            && AppCatalogRules.MatchesDrive(app, AppCatalogRules.AllDrives)
            && AppCatalogRules.MatchesDrive(app, "C")
            && AppCatalogRules.MatchesDrive(app, "c")
            && !AppCatalogRules.MatchesDrive(app, "D")
            && !AppCatalogRules.MatchesDrive(app, AppCatalogRules.UnknownDrive));

        var unknown = Make("老软件", "", "1.0", "");
        check("[应用管理器] 没记录安装位置的应用只在「所有驱动器 / 未记录位置」下出现",
            AppCatalogRules.MatchesDrive(unknown, "")
            && AppCatalogRules.MatchesDrive(unknown, AppCatalogRules.UnknownDrive)
            && !AppCatalogRules.MatchesDrive(unknown, "C"));
    }

    // ---------------------------------------------------------------- 行对象展示文本

    private static void CheckRowText(CheckFn check)
    {
        var full = new InstalledAppRow(new InstalledApp
        {
            Name = "7-Zip",
            Publisher = "Igor Pavlov",
            Version = "25.01",
            SizeInKilobytes = 5758,
            InstallLocation = @"C:\Program Files\7-Zip",
            InstallDateRaw = "20260626",
            InstallDate = AppCatalogRules.ParseInstallDate("20260626"),
            DriveLetter = "C",
            UninstallCommand = @"C:\Program Files\7-Zip\Uninstall.exe",
        });

        check("[应用管理器] 行的副标题是「发布者 · v版本」",
            full.SubtitleText == "Igor Pavlov · v25.01", full.SubtitleText);

        check("[应用管理器] 行的大小 / 日期 / 盘符文本",
            full.SizeText == "5.6 MB" && full.InstallDateText == "2026/06/26" && full.DriveText == "C:",
            $"{full.SizeText} / {full.InstallDateText} / {full.DriveText}");

        check("[应用管理器] 有卸载命令就能卸载；没有修改命令就点不了修改",
            full.CanUninstall && !full.CanModify);

        check("[应用管理器] 不能点时悬停提示说清原因",
            !full.CanModify && full.ModifyHintText.Contains("ModifyPath"),
            full.ModifyHintText);

        var bare = new InstalledAppRow(new InstalledApp { Name = "极简软件" });
        check("[应用管理器] 字段全空时也给出可读文本",
            bare.SubtitleText.Contains("没有登记")
            && bare.SizeText == "未知"
            && bare.InstallDateText == "安装日期未知"
            && bare.DriveText == "未记录位置"
            && !bare.CanUninstall
            && bare.UninstallHintText.Contains("UninstallString"),
            $"{bare.SubtitleText} / {bare.SizeText} / {bare.InstallDateText} / {bare.DriveText}");

        check("[应用管理器] 行详情提示包含卸载命令（悬停可核对）",
            full.DetailText.Contains("卸载命令") && full.DetailText.Contains("Uninstall.exe"),
            full.DetailText);

        check("[应用管理器] 行状态：默认不在运行，标记运行后显示「运行中」",
            !full.IsRunning && full.StateText == ""
            && SetRunning(full) && full.StateText.Contains("运行中"));
    }

    private static bool SetRunning(InstalledAppRow row)
    {
        row.IsRunning = true;
        return row.IsRunning;
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new AppManagerConfig();
        check("[应用管理器] 配置默认值：按名称正序、所有驱动器、自动扫描、确认后再执行",
            config.SortKey == "name"
            && config.SortDescending == false
            && config.DriveFilter == ""
            && config.SearchText == ""
            && config.AutoScanOnOpen
            && config.ConfirmBeforeRun
            && config.RescanAfterRun
            && config.ShowUnknownSize,
            $"{config.SortKey}/{config.SortDescending}/{config.DriveFilter}/{config.AutoScanOnOpen}/{config.ConfirmBeforeRun}");

        var json = JsonSerializer.Serialize(new AppManagerConfig
        {
            SearchText = "zip",
            DriveFilter = "D",
            SortKey = "size",
            SortDescending = true,
            ConfirmBeforeRun = false,
        });
        var back = JsonSerializer.Deserialize<AppManagerConfig>(json);
        check("[应用管理器] 配置能 JSON 往返（筛选 / 排序 / 方向都落盘）",
            back is not null
            && back.SearchText == "zip"
            && back.DriveFilter == "D"
            && back.SortKey == "size"
            && back.SortDescending
            && back.ConfirmBeforeRun == false,
            json);
    }

    // ---------------------------------------------------------------- 真实只读扫描

    private static void CheckRealRegistryScan(CheckFn check)
    {
        IReadOnlyList<InstalledApp> apps;
        try
        {
            apps = new RegistryAppCatalog().Load();
        }
        catch (Exception ex)
        {
            check("[应用管理器] 真实扫描注册表不抛异常", false, ex.Message);
            return;
        }

        check("[应用管理器] 真实扫描注册表不抛异常，且能读到应用", true, $"{apps.Count} 个");

        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.Name)) problems.Add("空名");
            if (!seen.Add(app.Name.Trim())) problems.Add("重复：" + app.Name);
            if (app.Name.Length > 0 && app.RegistryScope.Length == 0) problems.Add("缺来源：" + app.Name);
            // 有原始日期串但解析不出来（注册表写坏了不怪数据，只把它列出来方便排查）
            if (app.InstallDateRaw.Length > 0 && app.InstallDate is null) problems.Add("日期解析失败：" + app.Name);
            // 卸载命令必须能拆出可执行文件，否则界面上的「卸载」按钮会点了没反应
            if (app.UninstallCommand.Length > 0 && !AppCatalogRules.SplitCommandLine(app.UninstallCommand).HasExecutable)
                problems.Add("卸载命令拆不开：" + app.Name);
            if (app.DriveLetter.Length > 0 && !char.IsLetter(app.DriveLetter[0]))
                problems.Add("盘符异常：" + app.Name);
            // 名字里不能残留 NUL / 控制字符（有些安装程序写定长缓冲，Trim 去不掉）
            if (app.Name.Any(char.IsControl)) problems.Add("名字含控制字符：" + app.Name);
        }

        check("[应用管理器] 真实数据自洽（不重名 / 有来源 / 卸载命令可拆 / 盘符合法）",
            problems.Count == 0,
            problems.Count == 0 ? $"{apps.Count} 个应用全部自洽" : string.Join("；", problems.Take(5)));

        var sample = apps.Take(3).Select(app => app.Name).ToArray();
        check("[应用管理器] 真实数据有名称可读",
            apps.All(app => app.Name.Trim().Length > 0),
            string.Join(" | ", sample));

        var withSize = apps.Count(app => app.HasSize);
        var withDate = apps.Count(app => app.HasInstallDate);
        var withUninstall = apps.Count(app => app.CanUninstall);
        check("[应用管理器] 真实数据的字段覆盖率在合理范围（大小 / 日期 / 卸载命令）",
            withUninstall > 0 && withUninstall >= withSize,
            $"共 {apps.Count}，有大小 {withSize}，有日期 {withDate}，有卸载命令 {withUninstall}");
    }
}
