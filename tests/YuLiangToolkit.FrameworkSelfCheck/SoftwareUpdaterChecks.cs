using System.Text;
using YuLiangToolkit.Tools.SoftwareUpdater;
using YuLiangToolkit.Tools.SoftwareUpdater.ViewModels;
using YuLiangToolkit.Tools.SoftwareUpdater.Winget;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「软件升级器」自检：winget 中文 / 英文表格解析（含 CJK 列宽、ANSI、回车覆盖、统计行）、
/// 包 Id 安全白名单与命令拼装、批量勾选规则（多选 / 全选 / 全不选 / 反选 / 表头三态）、
/// 工具元数据与配置默认值。
/// <b>全程不启动 winget.exe、不写真实注册表、不联网</b>；真正的扫描 / 升级由界面人工冒烟验收。
/// </summary>
internal static class SoftwareUpdaterChecks
{
    /// <summary>
    /// 自检框架传进来的是 <c>Action&lt;string,bool,string&gt;</c>（detail 必填），
    /// 这里包一层带默认值的委托，模块内部就能写 <c>check("名字", ok)</c> 而不必到处补 <c>""</c>。
    /// </summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);
        CheckMetadata(fn);
        CheckPackageRules(fn);
        CheckChineseTable(fn);
        CheckEnglishTable(fn);
        CheckNoiseFiltering(fn);
        CheckCommandBuilding(fn);
        CheckPackageIdWhitelist(fn);
        CheckSelectionRules(fn);
        CheckHeaderState(fn);
        CheckItemAndConfig(fn);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(CheckFn check)
    {
        var tool = new SoftwareUpdaterTool();
        check("[软件升级器] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "software-updater" && tool.Name == "软件升级器"
            && tool.Category == "系统" && tool.Order == 505,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[软件升级器] 标签与描述覆盖已安装应用 / 可用更新 / 批量选择 / winget",
            tool.Tags.Contains("winget") && tool.Tags.Contains("升级") && tool.Tags.Contains("批量")
            && tool.Tags.Contains("已安装应用")
            && tool.Description.Contains("winget") && tool.Description.Contains("全选")
            && tool.Description.Contains("反选") && tool.Description.Contains("多选"),
            tool.Description);

        // 工具 Id 必须等于 Data / Config / Cache 的目录名
        check("[软件升级器] 工具 Id 与 SoftwareUpdaterConfig.ToolId 一致",
            tool.Id == SoftwareUpdaterConfig.ToolId, SoftwareUpdaterConfig.ToolId);

        check("[软件升级器] 实现了 ITool 且 EmojiIcon 非空",
            tool is YuLiangToolkit.Core.Tools.ITool && tool.EmojiIcon.Length > 0,
            tool.EmojiIcon);
    }

    // ---------------------------------------------------------------- 数据模型

    private static void CheckPackageRules(CheckFn check)
    {
        check("[软件升级器] 有可用版本才算有更新；空 / 占位符 / Unknown 都不算",
            new WingetPackage("A", "A.B", "1.0", "2.0", "winget").HasUpdate
            && !new WingetPackage("A", "A.B", "1.0", "", "winget").HasUpdate
            && !new WingetPackage("A", "A.B", "1.0", "-", "winget").HasUpdate
            && !new WingetPackage("A", "A.B", "1.0", "--", "winget").HasUpdate
            && !new WingetPackage("A", "A.B", "1.0", "N/A", "winget").HasUpdate
            && !new WingetPackage("A", "A.B", "1.0", "Unknown", "winget").HasUpdate
            && !new WingetPackage("A", "A.B", "1.0", "unknown", "winget").HasUpdate);

        check("[软件升级器] 被 winget 截断的 Id（… / ...）能被识别",
            new WingetPackage("A", "Very.Long.Package…", "1", "2", "winget").IsIdTruncated
            && new WingetPackage("A", "Very.Long.Package...", "1", "2", "winget").IsIdTruncated
            && !new WingetPackage("A", "7zip.7zip", "1", "2", "winget").IsIdTruncated);

        check("[软件升级器] CanUpgrade = 有更新 + Id 未截断 + Id 安全",
            WingetCommand.CanUpgrade(new WingetPackage("7-Zip", "7zip.7zip", "25.01", "26.05", "winget"))
            && !WingetCommand.CanUpgrade(new WingetPackage("7-Zip", "7zip.7zip", "25.01", "", "winget"))
            && !WingetCommand.CanUpgrade(new WingetPackage("X", "ARP\\Machine\\X64\\A", "1", "2", ""))
            && !WingetCommand.CanUpgrade(new WingetPackage("X", "Some.Package…", "1", "2", "winget")));
    }

    // ---------------------------------------------------------------- 表格解析（中文表头 + CJK 列宽）

    /// <summary>
    /// 按 winget 的真实做法造表格：每列用「显示列宽」左对齐（CJK 算 2 列）。
    /// 这样中文应用名不会把后面的 ID / 版本挤到错误的列里。
    /// </summary>
    private static int DisplayWidth(string text)
        => text.Sum(ch => ch > 0x2E80 ? 2 : 1);

    private static string Pad(string text, int width)
    {
        var padding = width - DisplayWidth(text);
        return text + new string(' ', Math.Max(1, padding));
    }

    private sealed record TableRow(string Name, string Id, string Version, string Available, string Source);

    private static string BuildChineseTable(IEnumerable<TableRow> rows)
    {
        const int nameWidth = 34, idWidth = 46, versionWidth = 18, availableWidth = 18;
        var sb = new StringBuilder();
        sb.AppendLine(Pad("名称", nameWidth) + Pad("ID", idWidth) + Pad("版本", versionWidth)
                      + Pad("可用", availableWidth) + "源");
        sb.AppendLine(new string('-', nameWidth + idWidth + versionWidth + availableWidth + 8));
        foreach (var row in rows)
        {
            sb.AppendLine(Pad(row.Name, nameWidth) + Pad(row.Id, idWidth)
                          + Pad(row.Version, versionWidth) + Pad(row.Available, availableWidth) + row.Source);
        }
        sb.AppendLine("41 升级可用。");
        return sb.ToString();
    }

    private static void CheckChineseTable(CheckFn check)
    {
        var text = BuildChineseTable(new[]
        {
            new TableRow("7-Zip 25.01 (x64)", "7zip.7zip", "25.01", "26.05", "winget"),
            new TableRow("网易云音乐", "NetEase.CloudMusic", "3.1.37", "3.1.42", "winget"),
            new TableRow("腾讯会议", "Tencent.TencentMeeting", "3.45.2", "", ""),
            new TableRow("Visual Studio 组件", "ARP\\Machine\\X64\\{ABCD-1234}", "40.9.55383", "", ""),
        });

        var packages = WingetOutputParser.Parse(text);
        check("[软件升级器] 中文表头表格解析出行数（统计行 / 分隔线被过滤）",
            packages.Count == 4, $"{packages.Count} 行");

        if (packages.Count == 4)
        {
            check("[软件升级器] 中文表格：英文名行各列都对得上",
                packages[0].Name == "7-Zip 25.01 (x64)" && packages[0].Id == "7zip.7zip"
                && packages[0].Version == "25.01" && packages[0].AvailableVersion == "26.05"
                && packages[0].Source == "winget" && packages[0].HasUpdate,
                packages[0].ToString());

            check("[软件升级器] 中文表格：CJK 应用名占 2 列也不串列",
                packages[1].Name == "网易云音乐" && packages[1].Id == "NetEase.CloudMusic"
                && packages[1].Version == "3.1.37" && packages[1].AvailableVersion == "3.1.42"
                && packages[1].Source == "winget",
                $"{packages[1].Name} / {packages[1].Id} / {packages[1].AvailableVersion}");

            check("[软件升级器] 中文表格：可用列为空时是空串、不是把来源吃进来",
                packages[2].Name == "腾讯会议" && packages[2].Id == "Tencent.TencentMeeting"
                && packages[2].AvailableVersion == "" && packages[2].Source == ""
                && !packages[2].HasUpdate,
                $"{packages[2].Name} / '{packages[2].AvailableVersion}' / '{packages[2].Source}'");

            check("[软件升级器] 中文表格：带空格与反斜杠的注册表 Id 也完整读出来",
                packages[3].Id == "ARP\\Machine\\X64\\{ABCD-1234}"
                && packages[3].Version == "40.9.55383",
                packages[3].Id);
        }

        check("[软件升级器] 同一个包出现两次时保留「有可用版本」那条",
            WingetOutputParser.Parse(BuildChineseTable(new[]
            {
                new TableRow("7-Zip", "7zip.7zip", "25.01", "", "winget"),
                new TableRow("7-Zip", "7zip.7zip", "25.01", "26.05", "winget"),
            })).Single().AvailableVersion == "26.05");

        check("[软件升级器] 空输出 / 没有表头一律返回空列表，不抛异常",
            WingetOutputParser.Parse(null).Count == 0
            && WingetOutputParser.Parse("").Count == 0
            && WingetOutputParser.Parse("winget 源尚未就绪").Count == 0);

        check("[软件升级器] 尾部统计文字能被单独取出",
            WingetOutputParser.FindSummary(text) == "41 升级可用。",
            WingetOutputParser.FindSummary(text));
    }

    // ---------------------------------------------------------------- 表格解析（英文表头）

    private static void CheckEnglishTable(CheckFn check)
    {
        // 英文 winget 会把「可用版本」写成两个词 "Available Version"：第二个 Version 必须被并进 Available 列，
        // 否则可用版本会落到被忽略的那一格。
        var sb = new StringBuilder();
        sb.AppendLine(Pad("Name", 26) + Pad("Id", 32) + Pad("Version", 16)
                      + Pad("Available", 14) + Pad("Version", 10) + "Source");
        sb.AppendLine(new string('-', 98));
        sb.AppendLine(Pad("VideoLAN VLC", 26) + Pad("VideoLAN.VLC", 32) + Pad("3.0.20", 16)
                      + Pad("3.0.21", 14) + Pad("", 10) + "winget");
        sb.AppendLine("2 upgrades available.");

        var packages = WingetOutputParser.Parse(sb.ToString());
        check("[软件升级器] 英文表头（Available Version 两个词）也能解析",
            packages.Count == 1
            && packages[0].Name == "VideoLAN VLC"
            && packages[0].Id == "VideoLAN.VLC"
            && packages[0].Version == "3.0.20"
            && packages[0].AvailableVersion == "3.0.21"
            && packages[0].Source == "winget",
            packages.Count == 1 ? packages[0].ToString() : $"{packages.Count} 行");

        check("[软件升级器] 英文统计行不会混进列表",
            packages.All(p => !p.Id.Contains("upgrades available", StringComparison.OrdinalIgnoreCase)));
    }

    // ---------------------------------------------------------------- 噪声清洗

    private static void CheckNoiseFiltering(CheckFn check)
    {
        check("[软件升级器] CleanLine 取回车覆盖后的最后一帧并去掉 ANSI 颜色",
            WingetOutputParser.CleanLine("\u001B[2K正在下载 10%\r正在下载 90%\r7-Zip 25.01")
                == "7-Zip 25.01",
            WingetOutputParser.CleanLine("\u001B[2K正在下载 10%\r正在下载 90%\r7-Zip 25.01"));

        check("[软件升级器] 分隔线（半角 / 制表线）与统计行都能识别",
            WingetOutputParser.IsSeparatorLine("--------------------")
            && WingetOutputParser.IsSeparatorLine("────────────")
            && WingetOutputParser.IsSummaryLine("41 升级可用。")
            && WingetOutputParser.IsSummaryLine("41 upgrades available.")
            && !WingetOutputParser.IsSeparatorLine("7-Zip 25.01"));

        check("[软件升级器] 表头识别同时支持中英文、且不把数据行当表头",
            WingetOutputParser.IsTableHeader("名称    ID    版本    可用    源")
            && WingetOutputParser.IsTableHeader("Name   Id   Version   Available   Source")
            && !WingetOutputParser.IsTableHeader("7-Zip   7zip.7zip   25.01")
            && !WingetOutputParser.IsTableHeader("--------------------------------"));

        // 在表格前后各塞一行 ANSI 进度帧 / 统计行，解析结果不受影响
        var noisy = "\u001B[32m正在刷新源…\u001B[0m\r"
                    + BuildChineseTable(new[] { new TableRow("7-Zip", "7zip.7zip", "1", "2", "winget") });
        check("[软件升级器] 输出前的进度帧 / 颜色码不影响表格解析",
            WingetOutputParser.Parse(noisy).Count == 1
            && WingetOutputParser.Parse(noisy)[0].Id == "7zip.7zip");
    }

    // ---------------------------------------------------------------- 命令拼装

    private static void CheckCommandBuilding(CheckFn check)
    {
        var all = WingetCommand.BuildScanArguments(WingetCommand.AllApps, false);
        check("[软件升级器] 全部应用扫描 = winget list --disable-interactivity --nowarn",
            all.SequenceEqual(new[] { "list", "--disable-interactivity", "--nowarn" }),
            string.Join(' ', all));

        var upgradable = WingetCommand.BuildScanArguments(WingetCommand.UpgradableOnly, false);
        check("[软件升级器] 仅可更新扫描 = winget upgrade --disable-interactivity --nowarn",
            upgradable.SequenceEqual(new[] { "upgrade", "--disable-interactivity", "--nowarn" }),
            string.Join(' ', upgradable));

        check("[软件升级器] --include-unknown 只给 upgrade，不给 list",
            WingetCommand.BuildScanArguments(WingetCommand.UpgradableOnly, true).Contains("--include-unknown")
            && !WingetCommand.BuildScanArguments(WingetCommand.AllApps, true).Contains("--include-unknown"));

        check("[软件升级器] 非法扫描模式回退到「全部应用」",
            WingetCommand.NormalizeScanMode("乱写的值") == WingetCommand.AllApps
            && WingetCommand.NormalizeScanMode(null) == WingetCommand.AllApps
            && WingetCommand.NormalizeScanMode(WingetCommand.UpgradableOnly) == WingetCommand.UpgradableOnly);

        var upgrade = WingetCommand.BuildUpgradeArguments("7zip.7zip", false, false);
        check("[软件升级器] 升级命令逐个参数拼装、带 --exact 与两项协议确认、不带 --all",
            upgrade.SequenceEqual(new[]
            {
                "upgrade", "--id", "7zip.7zip", "--exact", "--disable-interactivity",
                "--accept-package-agreements", "--accept-source-agreements",
            }),
            string.Join(' ', upgrade));

        var silent = WingetCommand.BuildUpgradeArguments("NetEase.CloudMusic", true, true);
        check("[软件升级器] 静默 / 包含未知版本两个开关都体现在升级命令里",
            silent.Contains("--silent") && silent.Contains("--include-unknown")
            && silent.Contains("--disable-interactivity"),
            string.Join(' ', silent));

        var rejected = false;
        try { WingetCommand.BuildUpgradeArguments("ARP\\Machine\\X64\\A", false, false); }
        catch (ArgumentException) { rejected = true; }
        check("[软件升级器] 不安全 Id 直接拒绝拼装升级命令（抛 ArgumentException）", rejected);
    }

    private static void CheckPackageIdWhitelist(CheckFn check)
    {
        check("[软件升级器] 合法的 winget 包 Id 放行",
            WingetCommand.IsSafePackageId("7zip.7zip")
            && WingetCommand.IsSafePackageId("Tencent.TencentMeeting")
            && WingetCommand.IsSafePackageId("a+b_c.d-e")
            && WingetCommand.IsSafePackageId("Microsoft.VisualStudio.2022.Community"));

        check("[软件升级器] 注册表 / MSIX 来源、伪选项、截断 Id 与超长 Id 全部拦下",
            !WingetCommand.IsSafePackageId("ARP\\Machine\\X64\\A")
            && !WingetCommand.IsSafePackageId("MSIX\\Microsoft.X_1.0_x64__8wekyb3d8bbwe")
            && !WingetCommand.IsSafePackageId("--silent")
            && !WingetCommand.IsSafePackageId("-id")
            && !WingetCommand.IsSafePackageId("Very.Long.Package…")
            && !WingetCommand.IsSafePackageId("")
            && !WingetCommand.IsSafePackageId(null)
            && !WingetCommand.IsSafePackageId(new string('a', 201)));

        check("[软件升级器] 含空格 / 引号 / 中文的 Id 也拦下（防参数注入）",
            !WingetCommand.IsSafePackageId("Some App 282800")
            && !WingetCommand.IsSafePackageId("a\"b")
            && !WingetCommand.IsSafePackageId("网易云音乐"));
    }

    // ---------------------------------------------------------------- 批量勾选规则

    private static void CheckSelectionRules(CheckFn check)
    {
        // 6 行：0/1/3/4 可升级，2 不可升级，5 被筛掉（不在 visible 里但已勾上）
        var canUpgrade = new[] { true, true, false, true, true, true };
        var visible = new[] { 0, 1, 2, 3, 4 };
        var current = new[] { false, false, false, false, false, true };

        var all = SelectionRules.Apply(current, visible, canUpgrade, SelectionAction.All);
        check("[软件升级器] 全选只勾「可见且可升级」的行，被筛掉的行保持原样",
            all.SequenceEqual(new[] { true, true, false, true, true, true })
            && SelectionRules.CountChecked(all) == 5,
            string.Join(",", all));

        // 全选是「加法」：不会新增不可升级行的勾选，也不会越权取消它已有的勾选（清掉要显式点全不选）。
        var fromEmpty = SelectionRules.Apply(
            new[] { false, false, false, false, false, false }, visible, canUpgrade, SelectionAction.All);
        var withNonUpgradableChecked = SelectionRules.Apply(
            new[] { false, false, true, false, false, false }, visible, canUpgrade, SelectionAction.All);
        check("[软件升级器] 全选不会新增不可升级行的勾选；已有勾选留给「全不选」清",
            !fromEmpty[2] && withNonUpgradableChecked[2]
            && !SelectionRules.Apply(withNonUpgradableChecked, visible, canUpgrade,
                SelectionAction.None)[2],
            string.Join(",", fromEmpty) + " / " + string.Join(",", withNonUpgradableChecked));

        var none = SelectionRules.Apply(all, visible, canUpgrade, SelectionAction.None);
        check("[软件升级器] 全不选清空可见项、不影响被筛掉的行",
            none.SequenceEqual(new[] { false, false, false, false, false, true }),
            string.Join(",", none));

        var invert = SelectionRules.Apply(all, visible, canUpgrade, SelectionAction.Invert);
        check("[软件升级器] 反选只对可见且可升级的行取反",
            invert.SequenceEqual(new[] { false, false, false, false, false, true }),
            string.Join(",", invert));

        var rowSelected = new[] { true, false, false, true, false, true };
        var noRows = SelectionRules.Apply(current, visible, canUpgrade, SelectionAction.CheckRows);
        check("[软件升级器] CheckRows 没给 rowSelected 时不改任何勾选",
            noRows.SequenceEqual(current), string.Join(",", noRows));

        var checkedRows = SelectionRules.Apply(current, visible, canUpgrade,
            SelectionAction.CheckRows, rowSelected);
        check("[软件升级器] 勾选多选中的行：只勾可见且可升级的，被筛掉的行不动",
            checkedRows.SequenceEqual(new[] { true, false, false, true, false, true }),
            string.Join(",", checkedRows));

        var uncheckedRows = SelectionRules.Apply(checkedRows, visible, canUpgrade,
            SelectionAction.UncheckRows, rowSelected);
        check("[软件升级器] 取消勾选多选中的行",
            uncheckedRows.SequenceEqual(new[] { false, false, false, false, false, true }),
            string.Join(",", uncheckedRows));

        check("[软件升级器] 空可见列表是安全的空操作",
            SelectionRules.Apply(current, Array.Empty<int>(), canUpgrade, SelectionAction.All)
                .SequenceEqual(current));

        check("[软件升级器] 越界下标不会抛异常",
            SelectionRules.Apply(current, new[] { -1, 99 }, canUpgrade, SelectionAction.All)
                .SequenceEqual(current));
    }

    private static void CheckHeaderState(CheckFn check)
    {
        var canUpgrade = new[] { true, true, false, true };
        var visible = new[] { 0, 1, 2, 3 };

        check("[软件升级器] 表头三态：全不勾 / 全勾 / 部分勾",
            SelectionRules.HeaderState(new[] { false, false, true, false }, visible, canUpgrade) == false
            && SelectionRules.HeaderState(new[] { true, true, false, true }, visible, canUpgrade) == true
            && SelectionRules.HeaderState(new[] { true, false, false, false }, visible, canUpgrade) == null);

        check("[软件升级器] 不可升级的行不参与表头状态统计",
            SelectionRules.HeaderState(new[] { true, true, false, true }, visible, canUpgrade) == true
            && SelectionRules.HeaderState(new[] { false, false, true, false }, visible, canUpgrade) == false);

        check("[软件升级器] 没有可升级行时表头返回 false",
            SelectionRules.HeaderState(new[] { false, false, false, false },
                visible, new[] { false, false, false, false }) == false);
    }

    // ---------------------------------------------------------------- 列表项 / 配置默认值

    private static void CheckItemAndConfig(CheckFn check)
    {
        var item = new SoftwareUpdaterItem(
            new WingetPackage("7-Zip 25.01 (x64)", "7zip.7zip", "25.01", "26.05", "winget"));
        check("[软件升级器] 列表项：可升级 / 显示文本 / 搜索命中",
            item.CanUpgrade && item.HasUpdate && item.UpdateBadgeText == "有新版本"
            && item.VersionSummaryText.Contains("26.05")
            && item.Matches("7ZIP", true) && item.Matches("winget", true)
            && !item.Matches("photoshop", true),
            item.UpdateBadgeText);

        var plain = new SoftwareUpdaterItem(
            new WingetPackage("某个本机程序", "ARP\\Machine\\X64\\A", "1.0", "", ""));
        check("[软件升级器] 列表项：本机注册表来源且无更新时不可勾选、只看可更新时被筛掉",
            !plain.CanUpgrade && plain.UpdateBadgeText == "已是最新"
            && plain.SourceText == "本机 / 注册表"
            && !plain.Matches("", true) && plain.Matches("", false));

        check("[软件升级器] 列表项状态文本随升级结果变化（给界面 DataTrigger 用）",
            item.StateKey == "Pending" && item.StateText == "可升级"
            && RunState(item, UpgradeState.Done) == "✓ 已升级"
            && RunState(item, UpgradeState.Failed) == "✗ 失败"
            && RunState(item, UpgradeState.Skipped) == "已跳过");

        var cfg = new SoftwareUpdaterConfig();
        check("[软件升级器] 配置默认：全部应用扫描 / 只看可更新 / 打开自动扫描 / 升级前确认 / 10 分钟",
            cfg.ScanMode == WingetCommand.AllApps && cfg.OnlyUpgradable
            && cfg.AutoScanOnOpen && cfg.ConfirmBeforeUpgrade
            && !cfg.SilentInstall && !cfg.IncludeUnknown
            && cfg.UpgradeTimeoutMinutes == 10 && cfg.SafeTimeoutMinutes == 10,
            cfg.ToJson());

        cfg.UpgradeTimeoutMinutes = 999;
        check("[软件升级器] 超时上限被夹到 120 分钟",
            cfg.SafeTimeoutMinutes == 120, cfg.SafeTimeoutMinutes.ToString());
        cfg.UpgradeTimeoutMinutes = -5;
        check("[软件升级器] 超时下限被夹到 1 分钟",
            cfg.SafeTimeoutMinutes == 1, cfg.SafeTimeoutMinutes.ToString());
    }

    private static string RunState(SoftwareUpdaterItem item, UpgradeState state)
    {
        item.State = state;
        return item.StateText;
    }
}
