using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.WorldClock;
using YuLiangToolkit.Tools.WorldClock.Models;
using YuLiangToolkit.Tools.WorldClock.Services;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「全球时钟」自检：工具元数据 / 时区解析（真查系统时区库 + 假的夏令时口径）/
/// 时间与日期格式化 / 跨日与时差文案 / 时段分档 / 城市库搜索 /
/// 拖动重排下标与锁定规则 / 配置默认值与 JSON 往返。
///
/// <para><b>全部是纯逻辑</b>：不创建窗口、不点界面、不依赖本机此刻是几点
/// （所有换算都传固定的 UTC 时刻），也不碰仓库的 Data/Config
/// （配置往返在 <c>YLT_HOME</c> 指向的临时数据根里走 ToolConfigStore）。</para>
/// </summary>
internal static class WorldClockChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckZoneResolution(assert);
        CheckTimeFormat(assert);
        CheckDateFormat(assert);
        CheckOffsetText(assert);
        CheckDayPhase(assert);
        CheckCatalog(assert);
        CheckListOperations(assert);
        CheckOffsetParsing(assert);
        CheckItemFill(assert);
        CheckConfig(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new WorldClockTool();
        check("[全球时钟] 工具 Id / 名称 / 分类 / 排序符合约定，且实现了 ITool",
            tool.Id == "world-clock" && tool.Name == "全球时钟"
            && tool.Category == "桌面" && tool.Order == 165
            && tool is ITool,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[全球时钟] 归在「桌面」类别下（CategoryPath 与 Category 一致）",
            tool.CategoryPath.Count == 1 && tool.CategoryPath[0] == "桌面",
            string.Join(" / ", tool.CategoryPath));

        check("[全球时钟] 主页图标非空、标签含时区 / 时差 / 世界时间",
            tool.EmojiIcon.Length > 0
            && tool.Tags.Contains("时钟") && tool.Tags.Contains("时区") && tool.Tags.Contains("时差"),
            string.Join(" / ", tool.Tags) + " · " + tool.EmojiIcon);

        check("[全球时钟] 描述能当主页搜索文案（写明表盘 / 列表 / 锁定 / 撤销 / 时差）",
            tool.Description.Contains("表盘") && tool.Description.Contains("列表")
            && tool.Description.Contains("锁定") && tool.Description.Contains("撤销")
            && tool.Description.Contains("时差"),
            tool.Description);
    }

    // ---------------------------------------------------------------- 时区解析

    private static void CheckZoneResolution(CheckFn check)
    {
        // 固定时刻：2024-01-15 12:00 UTC（北半球冬令时）
        var winter = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var summer = new DateTime(2024, 7, 15, 12, 0, 0, DateTimeKind.Utc);

        var beijing = ClockMath.Resolve("China Standard Time", null, winter);
        check("[全球时钟] 北京冬夏都固定 UTC+08:00、不参与夏令时",
            beijing.Offset == TimeSpan.FromHours(8) && !beijing.IsDaylightSaving && !beijing.IsMissing,
            $"{beijing.Offset} / dst={beijing.IsDaylightSaving}");

        var londonWinter = ClockMath.Resolve("GMT Standard Time", null, winter);
        var londonSummer = ClockMath.Resolve("GMT Standard Time", null, summer);
        check("[全球时钟] 伦敦冬天 UTC+00:00、夏天 UTC+01:00（夏令时由系统时区库算）",
            londonWinter.Offset == TimeSpan.Zero && !londonWinter.IsDaylightSaving
            && londonSummer.Offset == TimeSpan.FromHours(1) && londonSummer.IsDaylightSaving,
            $"冬 {londonWinter.Offset}(dst={londonWinter.IsDaylightSaving}) / 夏 {londonSummer.Offset}(dst={londonSummer.IsDaylightSaving})");

        var newYork = ClockMath.Resolve("Eastern Standard Time", null, summer);
        check("[全球时钟] 纽约夏天是 UTC-04:00（冬令时 -05:00 差一小时）",
            newYork.Offset == TimeSpan.FromHours(-4) && newYork.IsDaylightSaving,
            $"{newYork.Offset} / dst={newYork.IsDaylightSaving}");

        var kathmandu = ClockMath.Resolve("Nepal Standard Time", null, winter);
        check("[全球时钟] 加德满都这种 45 分钟偏移也算得对（UTC+05:45）",
            kathmandu.Offset == new TimeSpan(5, 45, 0),
            kathmandu.Offset.ToString());

        // 阿德莱德：南半球夏令时 10 月 ~ 4 月，所以 1 月是 +10:30、7 月才是基准的 +09:30
        var adelaideWinter = ClockMath.Resolve("Cen. Australia Standard Time", null, summer);
        var adelaideSummer = ClockMath.Resolve("Cen. Australia Standard Time", null, winter);
        check("[全球时钟] 阿德莱德的半小时基准偏移（7 月 UTC+09:30、1 月夏令时 UTC+10:30）",
            adelaideWinter.Offset == new TimeSpan(9, 30, 0)
            && adelaideSummer.Offset == new TimeSpan(10, 30, 0),
            $"{adelaideWinter.Offset} / {adelaideSummer.Offset}");

        var custom = ClockMath.Resolve("China Standard Time", 330, winter);
        check("[全球时钟] 自定义偏移优先于时区 Id（+5:30）",
            custom.Offset == new TimeSpan(5, 30, 0) && !custom.IsDaylightSaving && !custom.IsMissing,
            custom.Offset.ToString());

        var clamped = ClockMath.Resolve("China Standard Time", 99 * 60, winter);
        check("[全球时钟] 自定义偏移越界时夹到 +14:00，不会崩",
            clamped.Offset == TimeSpan.FromHours(14),
            clamped.Offset.ToString());

        var missing = ClockMath.Resolve("这不是一个时区", null, winter);
        check("[全球时钟] 时区 Id 查不到 → 退化 UTC 并标记 IsMissing（界面会提示，不崩）",
            missing.IsMissing && missing.Offset == TimeSpan.Zero,
            $"missing={missing.IsMissing} / {missing.Offset}");

        var empty = ClockMath.Resolve("", null, winter);
        check("[全球时钟] 空时区 Id 也按「查不到」处理", empty.IsMissing, empty.Offset.ToString());

        check("[全球时钟] 本机时区查得出来、Id 非空",
            ClockMath.TryFindZone(TimeZoneInfo.Local.Id) is not null,
            TimeZoneInfo.Local.Id);

        // Unspecified 的 DateTime 按 UTC 处理（自检 / 手写代码时不用每次写 Kind）
        var unspecified = ClockMath.Resolve("China Standard Time", null, new DateTime(2024, 1, 15, 12, 0, 0));
        check("[全球时钟] DateTimeKind.Unspecified 按 UTC 解释（与显式 Utc 结果一致）",
            unspecified.Offset == beijing.Offset,
            unspecified.Offset.ToString());

        var localTime = ClockMath.Resolve("China Standard Time", null,
            new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Local));
        check("[全球时钟] DateTimeKind.Local 会先换成 UTC（不重复扣一次时区）",
            localTime.Offset == beijing.Offset,
            localTime.Offset.ToString());
    }

    // ---------------------------------------------------------------- 时间格式

    private static void CheckTimeFormat(CheckFn check)
    {
        var afternoon = new DateTime(2024, 3, 5, 14, 5, 9);

        check("[全球时钟] 24 小时制：带秒 / 不带秒",
            ClockMath.FormatTime(afternoon, hour24: true, showSeconds: true) == "14:05:09"
            && ClockMath.FormatTime(afternoon, hour24: true, showSeconds: false) == "14:05",
            ClockMath.FormatTime(afternoon, true, true) + " / " + ClockMath.FormatTime(afternoon, true, false));

        check("[全球时钟] 12 小时制：下午 / 下午带秒",
            ClockMath.FormatTime(afternoon, hour24: false, showSeconds: false) == "下午 2:05"
            && ClockMath.FormatTime(afternoon, hour24: false, showSeconds: true) == "下午 2:05:09",
            ClockMath.FormatTime(afternoon, false, true));

        var midnight = new DateTime(2024, 3, 5, 0, 7, 0);
        var noon = new DateTime(2024, 3, 5, 12, 0, 0);
        var morning = new DateTime(2024, 3, 5, 3, 0, 0);
        var evening = new DateTime(2024, 3, 5, 21, 30, 0);

        check("[全球时钟] 12 小时制的四个边界：00:07 凌晨 12:07 / 12:00 中午 12:00 / 03:00 凌晨 3:00 / 21:30 晚上 9:30",
            ClockMath.FormatTime(midnight, false, false) == "凌晨 12:07"
            && ClockMath.FormatTime(noon, false, false) == "中午 12:00"
            && ClockMath.FormatTime(morning, false, false) == "凌晨 3:00"
            && ClockMath.FormatTime(evening, false, false) == "晚上 9:30",
            $"{ClockMath.FormatTime(midnight, false, false)} / {ClockMath.FormatTime(noon, false, false)} / "
            + $"{ClockMath.FormatTime(morning, false, false)} / {ClockMath.FormatTime(evening, false, false)}");

        check("[全球时钟] 上午的 12 小时制写法（9:05 上午 9:05）",
            ClockMath.FormatTime(new DateTime(2024, 3, 5, 9, 5, 0), false, false) == "上午 9:05",
            ClockMath.FormatTime(new DateTime(2024, 3, 5, 9, 5, 0), false, false));
    }

    // ---------------------------------------------------------------- 日期

    private static void CheckDateFormat(CheckFn check)
    {
        var date = new DateTime(2024, 3, 5, 14, 5, 0);   // 2024-03-05 是周二
        check("[全球时钟] 日期写法「3月5日 周二」",
            ClockMath.FormatDate(date) == "3月5日 周二",
            ClockMath.FormatDate(date));

        check("[全球时钟] 同一年不写年份、跨年补上年份",
            ClockMath.FormatDate(date, 2024) == "3月5日 周二"
            && ClockMath.FormatDate(new DateTime(2025, 1, 1, 0, 30, 0), 2024) == "2025年1月1日 周三",
            ClockMath.FormatDate(date, 2024) + " / "
            + ClockMath.FormatDate(new DateTime(2025, 1, 1, 0, 30, 0), 2024));

        check("[全球时钟] 七天的星期名都能写出来、互不重复",
            new[]
            {
                ClockMath.WeekdayText(DayOfWeek.Monday), ClockMath.WeekdayText(DayOfWeek.Tuesday),
                ClockMath.WeekdayText(DayOfWeek.Wednesday), ClockMath.WeekdayText(DayOfWeek.Thursday),
                ClockMath.WeekdayText(DayOfWeek.Friday), ClockMath.WeekdayText(DayOfWeek.Saturday),
                ClockMath.WeekdayText(DayOfWeek.Sunday),
            }.Distinct().Count() == 7
            && ClockMath.WeekdayText(DayOfWeek.Sunday) == "周日",
            ClockMath.WeekdayText(DayOfWeek.Sunday));

        check("[全球时钟] 跨日角标：0 空串 / +1 明天 / -1 昨天 / 其它带天数",
            ClockMath.DescribeDayOffset(0) == ""
            && ClockMath.DescribeDayOffset(1) == "明天"
            && ClockMath.DescribeDayOffset(-1) == "昨天"
            && ClockMath.DescribeDayOffset(2) == "2 天后"
            && ClockMath.DescribeDayOffset(-3) == "3 天前",
            $"[{ClockMath.DescribeDayOffset(0)}] / {ClockMath.DescribeDayOffset(1)} / {ClockMath.DescribeDayOffset(-1)}");
    }

    // ---------------------------------------------------------------- 偏移 / 时差文案

    private static void CheckOffsetText(CheckFn check)
    {
        check("[全球时钟] 偏移写法：UTC / +08:00 / -05:30 / +05:45",
            ClockMath.FormatOffset(TimeSpan.Zero) == "UTC"
            && ClockMath.FormatOffset(TimeSpan.FromHours(8)) == "UTC+08:00"
            && ClockMath.FormatOffset(new TimeSpan(-5, -30, 0)) == "UTC-05:30"
            && ClockMath.FormatOffset(new TimeSpan(5, 45, 0)) == "UTC+05:45",
            $"{ClockMath.FormatOffset(TimeSpan.Zero)} / {ClockMath.FormatOffset(TimeSpan.FromHours(8))} / "
            + $"{ClockMath.FormatOffset(new TimeSpan(-5, -30, 0))}");

        check("[全球时钟] 时差文案：相同 / 快 8 小时 / 慢 5 小时 30 分 / 慢 45 分钟",
            ClockMath.FormatDiff(TimeSpan.Zero) == "与本地相同"
            && ClockMath.FormatDiff(TimeSpan.FromHours(8)) == "快 8 小时"
            && ClockMath.FormatDiff(new TimeSpan(-5, -30, 0)) == "慢 5 小时 30 分"
            && ClockMath.FormatDiff(TimeSpan.FromMinutes(-45)) == "慢 45 分钟"
            && ClockMath.FormatDiff(new TimeSpan(5, 45, 0)) == "快 5 小时 45 分",
            $"{ClockMath.FormatDiff(TimeSpan.FromHours(8))} / {ClockMath.FormatDiff(new TimeSpan(-5, -30, 0))}");

        check("[全球时钟] 紧凑偏移写法：+8 / +5:30 / -3:30 / +0 / -12",
            ClockMath.FormatOffsetMinutes(480) == "+8"
            && ClockMath.FormatOffsetMinutes(330) == "+5:30"
            && ClockMath.FormatOffsetMinutes(-210) == "-3:30"
            && ClockMath.FormatOffsetMinutes(0) == "+0"
            && ClockMath.FormatOffsetMinutes(-720) == "-12",
            $"{ClockMath.FormatOffsetMinutes(480)} / {ClockMath.FormatOffsetMinutes(330)} / {ClockMath.FormatOffsetMinutes(-210)}");
    }

    // ---------------------------------------------------------------- 时段

    private static void CheckDayPhase(CheckFn check)
    {
        check("[全球时钟] 时段分档边界：4 点夜里 / 5 点清晨 / 7 点清晨 / 8 点白天 / 16 点白天 / 17 点傍晚 / 19 点傍晚 / 20 点夜里",
            ClockMath.ClassifyDayPhase(4) == DayPhase.Night
            && ClockMath.ClassifyDayPhase(5) == DayPhase.Dawn
            && ClockMath.ClassifyDayPhase(7) == DayPhase.Dawn
            && ClockMath.ClassifyDayPhase(8) == DayPhase.Day
            && ClockMath.ClassifyDayPhase(16) == DayPhase.Day
            && ClockMath.ClassifyDayPhase(17) == DayPhase.Evening
            && ClockMath.ClassifyDayPhase(19) == DayPhase.Evening
            && ClockMath.ClassifyDayPhase(20) == DayPhase.Night
            && ClockMath.ClassifyDayPhase(0) == DayPhase.Night
            && ClockMath.ClassifyDayPhase(23) == DayPhase.Night,
            $"{ClockMath.ClassifyDayPhase(4)} / {ClockMath.ClassifyDayPhase(5)} / {ClockMath.ClassifyDayPhase(8)} / "
            + $"{ClockMath.ClassifyDayPhase(17)} / {ClockMath.ClassifyDayPhase(20)}");

        check("[全球时钟] 四种时段都有文字与 Emoji，且互不相同",
            new[] { DayPhase.Night, DayPhase.Dawn, DayPhase.Day, DayPhase.Evening }
                .Select(ClockMath.PhaseText).Distinct().Count() == 4
            && new[] { DayPhase.Night, DayPhase.Dawn, DayPhase.Day, DayPhase.Evening }
                .Select(ClockMath.PhaseEmoji).Distinct().Count() == 4,
            $"{ClockMath.PhaseText(DayPhase.Day)} {ClockMath.PhaseEmoji(DayPhase.Day)} / "
            + $"{ClockMath.PhaseText(DayPhase.Night)} {ClockMath.PhaseEmoji(DayPhase.Night)}");

        check("[全球时钟] 时段文字与分档一致（白天 / 夜晚这些是用户看得懂的中文）",
            ClockMath.PhaseText(ClockMath.ClassifyDayPhase(10)) == "白天"
            && ClockMath.PhaseText(ClockMath.ClassifyDayPhase(23)) == "夜晚",
            ClockMath.PhaseText(ClockMath.ClassifyDayPhase(10)));
    }

    // ---------------------------------------------------------------- 城市库

    private static void CheckCatalog(CheckFn check)
    {
        check("[全球时钟] 城市库有足够多的城市（>= 90），且没有同名重复",
            ClockCatalog.All.Count >= 90
            && ClockCatalog.All.Select(c => c.Name).Distinct().Count() == ClockCatalog.All.Count,
            $"{ClockCatalog.All.Count} 个");

        check("[全球时钟] 城市库每条都有中文名 / 英文名 / 时区 Id / 大洲",
            ClockCatalog.All.All(c =>
                !string.IsNullOrWhiteSpace(c.Name)
                && !string.IsNullOrWhiteSpace(c.EnglishName)
                && !string.IsNullOrWhiteSpace(c.TimeZoneId)
                && !string.IsNullOrWhiteSpace(c.Region)),
            ClockCatalog.All.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.EnglishName))?.Name ?? "全部齐全");

        // 城市库里的时区 Id 必须全都在本机存在，否则用户一加就变成「UTC 兜底」
        var missing = ClockCatalog.All
            .Where(c => ClockMath.TryFindZone(c.TimeZoneId) is null)
            .Select(c => $"{c.Name}={c.TimeZoneId}")
            .ToList();
        check("[全球时钟] 城市库里的时区 Id 在本机都存在（缺哪个一看就知道）",
            missing.Count == 0, missing.Count == 0 ? $"{ClockCatalog.All.Count} 条全部可解析" : string.Join(", ", missing));

        check("[全球时钟] 每个城市的大洲都在分组清单里",
            ClockCatalog.All.All(c => ClockCatalog.Regions.Contains(c.Region)),
            string.Join(" / ", ClockCatalog.Regions));

        check("[全球时钟] 中文搜索命中最常用的几座城市",
            ClockCatalog.Search("北京").Any(c => c.Name == "北京")
            && ClockCatalog.Search("伦敦").Any(c => c.Name == "伦敦")
            && ClockCatalog.Search("纽约").Any(c => c.Name == "纽约"),
            $"{ClockCatalog.Search("北京").Count} / {ClockCatalog.Search("伦敦").Count} / {ClockCatalog.Search("纽约").Count}");

        check("[全球时钟] 英文名搜索（大小写不敏感）",
            ClockCatalog.Search("london").Any(c => c.Name == "伦敦")
            && ClockCatalog.Search("TOKYO").Any(c => c.Name == "东京"),
            $"{ClockCatalog.Search("london").Count} / {ClockCatalog.Search("TOKYO").Count}");

        check("[全球时钟] 国家名也能搜到（英国 → 伦敦、日本 → 东京）",
            ClockCatalog.Search("英国").Any(c => c.Name == "伦敦")
            && ClockCatalog.Search("日本").Any(c => c.Name == "东京"),
            $"{ClockCatalog.Search("英国").Count} / {ClockCatalog.Search("日本").Count}");

        check("[全球时钟] 同一时区的多座城市都在（北京 / 上海 / 香港 都是 China Standard Time）",
            ClockCatalog.All.Count(c => c.TimeZoneId == "China Standard Time") >= 3,
            string.Join(" / ", ClockCatalog.All.Where(c => c.TimeZoneId == "China Standard Time").Select(c => c.Name)));

        check("[全球时钟] 空关键词返回全部、找不到的关键词返回空",
            ClockCatalog.Search("").Count == ClockCatalog.All.Count
            && ClockCatalog.Search("   ").Count == ClockCatalog.All.Count
            && ClockCatalog.Search("zzzz-没有这个地方").Count == 0,
            $"{ClockCatalog.Search("").Count} / {ClockCatalog.Search("zzzz-没有这个地方").Count}");

        check("[全球时钟] Find 精确取城市、大小写 / 空白不敏感、找不到返回 null",
            ClockCatalog.Find("北京")?.TimeZoneId == "China Standard Time"
            && ClockCatalog.Find(" 北京 ")?.Name == "北京"
            && ClockCatalog.Find("没有这个城市") is null
            && ClockCatalog.Find(null) is null,
            ClockCatalog.Find("北京")?.TimeZoneId ?? "null");

        check("[全球时钟] 首次运行的默认城市都能在城市库里找到",
            ClockCatalog.DefaultCityNames.Count >= 3
            && ClockCatalog.DefaultCityNames.All(n => ClockCatalog.Find(n) is not null),
            string.Join(" / ", ClockCatalog.DefaultCityNames));

        check("[全球时钟] 按大洲取城市（亚洲 > 0、不存在的大洲返回空）",
            ClockCatalog.InRegion("亚洲").Count > 0
            && ClockCatalog.InRegion("南极洲").Count == 0,
            $"{ClockCatalog.InRegion("亚洲").Count}");
    }

    // ---------------------------------------------------------------- 列表操作

    private static void CheckListOperations(CheckFn check)
    {
        var list = new List<string> { "A", "B", "C", "D" };

        var moved = ClockMath.Move(list, 0, 2);
        check("[全球时钟] Move：把第 1 项移到第 3 位",
            moved && string.Join("", list) == "BCAD", string.Join("", list));

        list = new List<string> { "A", "B", "C", "D" };
        moved = ClockMath.Move(list, 3, 0);
        check("[全球时钟] Move：末项移到最前", moved && string.Join("", list) == "DABC", string.Join("", list));

        list = new List<string> { "A", "B", "C", "D" };
        check("[全球时钟] Move：原地不动 / 下标越界都返回 false 且不改列表",
            !ClockMath.Move(list, 1, 1) && !ClockMath.Move(list, 9, 0) && !ClockMath.Move(list, -1, 0)
            && string.Join("", list) == "ABCD",
            string.Join("", list));

        check("[全球时钟] Move：目标下标越界时夹到末尾（不抛异常）",
            ClockMath.Move(list, 0, 99) && string.Join("", list) == "BCDA", string.Join("", list));

        // 拖放下标换算：往下拖要减 1（拖放最容易错的地方）
        check("[全球时钟] ResolveDropIndex：往下拖到第 3 项之前 = 下标 2（不是 3）",
            ClockMath.ResolveDropIndex(0, 3, 4) == 2, ClockMath.ResolveDropIndex(0, 3, 4).ToString());

        check("[全球时钟] ResolveDropIndex：往上拖到第 1 项之前 = 下标 0（不减 1）",
            ClockMath.ResolveDropIndex(3, 0, 4) == 0, ClockMath.ResolveDropIndex(3, 0, 4).ToString());

        check("[全球时钟] ResolveDropIndex：拖到自己前面 = 原地不动",
            ClockMath.ResolveDropIndex(1, 1, 4) == 1 && ClockMath.ResolveDropIndex(1, 2, 4) == 1,
            $"{ClockMath.ResolveDropIndex(1, 1, 4)} / {ClockMath.ResolveDropIndex(1, 2, 4)}");

        check("[全球时钟] ResolveDropIndex：拖到末尾 / 空列表",
            ClockMath.ResolveDropIndex(0, 4, 4) == 3 && ClockMath.ResolveDropIndex(0, 0, 0) == 0,
            $"{ClockMath.ResolveDropIndex(0, 4, 4)} / {ClockMath.ResolveDropIndex(0, 0, 0)}");

        check("[全球时钟] ResolveDropIndex：insertBefore 越界会被夹住",
            ClockMath.ResolveDropIndex(0, 99, 4) == 3 && ClockMath.ResolveDropIndex(3, -5, 4) == 0,
            $"{ClockMath.ResolveDropIndex(0, 99, 4)} / {ClockMath.ResolveDropIndex(3, -5, 4)}");

        // 刷新节奏：离下一整秒的毫秒数
        check("[全球时钟] 刷新对齐：整秒时等 1000ms、差 250ms 时等 750ms、999ms 时等 1ms",
            ClockMath.DelayToNextSecond(new DateTime(2024, 1, 1, 0, 0, 0, 0)) == 1000
            && ClockMath.DelayToNextSecond(new DateTime(2024, 1, 1, 0, 0, 0, 750)) == 250
            && ClockMath.DelayToNextSecond(new DateTime(2024, 1, 1, 0, 0, 0, 999)) == 1,
            $"{ClockMath.DelayToNextSecond(new DateTime(2024, 1, 1, 0, 0, 0, 750))}");
    }

    // ---------------------------------------------------------------- 偏移输入

    private static void CheckOffsetParsing(CheckFn check)
    {
        check("[全球时钟] 偏移输入：+8 / -5:30 / 5.5 / UTC+8 都能解析",
            ClockMath.ParseOffsetMinutes("+8") == 480
            && ClockMath.ParseOffsetMinutes("-5:30") == -330
            && ClockMath.ParseOffsetMinutes("5.5") == 330
            && ClockMath.ParseOffsetMinutes("UTC+8") == 480,
            $"{ClockMath.ParseOffsetMinutes("+8")} / {ClockMath.ParseOffsetMinutes("-5:30")} / "
            + $"{ClockMath.ParseOffsetMinutes("5.5")} / {ClockMath.ParseOffsetMinutes("UTC+8")}");

        check("[全球时钟] 偏移输入：GMT 前缀 / 只有前缀 / 负小数 / 首尾空白",
            ClockMath.ParseOffsetMinutes("GMT-3") == -180
            && ClockMath.ParseOffsetMinutes("UTC") == 0
            && ClockMath.ParseOffsetMinutes(" -2.5 ") == -150,
            $"{ClockMath.ParseOffsetMinutes("GMT-3")} / {ClockMath.ParseOffsetMinutes("UTC")} / "
            + $"{ClockMath.ParseOffsetMinutes(" -2.5 ")}");

        check("[全球时钟] 偏移输入：看不懂的写法返回 null（界面会提示，不会静默成 0）",
            ClockMath.ParseOffsetMinutes("abc") is null
            && ClockMath.ParseOffsetMinutes("") is null
            && ClockMath.ParseOffsetMinutes(null) is null
            && ClockMath.ParseOffsetMinutes("+8:99") is null,
            "abc / 空 / null / +8:99 都是 null");

        check("[全球时钟] 偏移输入：超出范围夹到 -12:00 ~ +14:00",
            ClockMath.ParseOffsetMinutes("+20") == 14 * 60
            && ClockMath.ParseOffsetMinutes("-20") == -12 * 60,
            $"{ClockMath.ParseOffsetMinutes("+20")} / {ClockMath.ParseOffsetMinutes("-20")}");

        check("[全球时钟] 偏移输入与紧凑输出互相对得上（Parse(Format(x)) == x）",
            new[] { 0, 480, 330, -330, 345, -720, 840 }
                .All(m => ClockMath.ParseOffsetMinutes(ClockMath.FormatOffsetMinutes(m)) == m),
            string.Join(" / ", new[] { 480, 330, -330, 345 }.Select(ClockMath.FormatOffsetMinutes)));
    }

    // ---------------------------------------------------------------- 一只钟的显示字段

    private static void CheckItemFill(CheckFn check)
    {
        var winter = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var localOffset = TimeSpan.FromHours(8);            // 假装本机是 UTC+08:00
        var localNow = ClockMath.ToWallClock(winter, localOffset);   // 本地 20:00
        var beijing = new WorldClockItem { CityName = "北京", TimeZoneId = "China Standard Time" };

        beijing.Refresh(winter, localNow, localOffset, hour24: true, showSeconds: true,
            showDate: true, showOffset: true, colorByDayNight: true);

        check("[全球时钟] 北京 12:00 UTC = 20:00，日期文字与「与本地相同」",
            beijing.TimeText == "20:00:00" && beijing.DateText.StartsWith("1月15日")
            && beijing.OffsetText == "与本地相同" && beijing.DayOffsetText == ""
            && beijing.ZoneOffsetText == "UTC+08:00",
            $"{beijing.TimeText} / {beijing.DateText} / {beijing.OffsetText} / {beijing.ZoneOffsetText}");

        check("[全球时钟] 北京 20:00 判成夜晚、Emoji 是月亮、底色该压暗",
            beijing.Phase == DayPhase.Night && beijing.PhaseEmoji == "🌙"
            && beijing.IsNight && beijing.DimAtNight,
            $"{beijing.Phase} / {beijing.PhaseEmoji} / DimAtNight={beijing.DimAtNight}");

        var london = new WorldClockItem { CityName = "伦敦", TimeZoneId = "GMT Standard Time" };
        london.Refresh(winter, localNow, localOffset, true, true, true, true, true);

        check("[全球时钟] 伦敦冬天 = 12:00，比本地慢 8 小时，白天",
            london.TimeText == "12:00:00"
            && london.OffsetText == "慢 8 小时"
            && london.Phase == DayPhase.Day
            && !london.DimAtNight,
            $"{london.TimeText} / {london.OffsetText} / {london.Phase}");

        var tokyo = new WorldClockItem { CityName = "东京", TimeZoneId = "Tokyo Standard Time" };
        tokyo.Refresh(winter, localNow, localOffset, true, true, true, true, true);
        check("[全球时钟] 东京 = 21:00，比本地快 1 小时",
            tokyo.TimeText == "21:00:00" && tokyo.OffsetText == "快 1 小时",
            $"{tokyo.TimeText} / {tokyo.OffsetText}");

        // 跨日：UTC 2024-01-15 20:00 → 本地（+8）是 1 月 16 日凌晨 4 点，而纽约是 1 月 15 日 15:00
        var lateUtc = new DateTime(2024, 1, 15, 20, 0, 0, DateTimeKind.Utc);
        var lateLocalNow = ClockMath.ToWallClock(lateUtc, localOffset);
        var newYork = new WorldClockItem { CityName = "纽约", TimeZoneId = "Eastern Standard Time" };
        newYork.Refresh(lateUtc, lateLocalNow, localOffset, true, true, true, true, true);
        check("[全球时钟] 跨日角标：本地已到 1/16，纽约还是 1/15 → 「昨天」",
            newYork.DayOffsetText == "昨天" && newYork.DateText.Contains("1月15日"),
            $"{newYork.DateText} / [{newYork.DayOffsetText}] / {newYork.TimeText}");

        // 跨日「明天」：UTC 13:00 → 本地（+8）1/15 21:00，悉尼（+11 夏令时）1/16 00:00
        var eastUtc = new DateTime(2024, 1, 15, 13, 0, 0, DateTimeKind.Utc);
        var eastLocalNow = ClockMath.ToWallClock(eastUtc, localOffset);
        var sydney = new WorldClockItem { CityName = "悉尼", TimeZoneId = "AUS Eastern Standard Time" };
        sydney.Refresh(eastUtc, eastLocalNow, localOffset, true, true, true, true, true);
        check("[全球时钟] 跨日角标：悉尼（UTC+11 夏令时）已经过了零点 → 「明天」",
            sydney.DayOffsetText == "明天" && sydney.DateText.Contains("1月16日"),
            $"{sydney.DateText} / [{sydney.DayOffsetText}] / {sydney.TimeText}");

        // 关掉的显示项要真的变空（界面不用再各自判断）
        var hidden = new WorldClockItem { CityName = "北京", TimeZoneId = "China Standard Time" };
        hidden.Refresh(winter, localNow, localOffset, hour24: false, showSeconds: false,
            showDate: false, showOffset: false, colorByDayNight: false);
        check("[全球时钟] 关掉日期 / 时差 / 昼夜底色后对应字段为空",
            hidden.DateText == "" && hidden.OffsetText == "" && !hidden.DimAtNight
            && hidden.TimeText == "晚上 8:00",
            $"date=[{hidden.DateText}] offset=[{hidden.OffsetText}] time={hidden.TimeText}");

        check("[全球时钟] 时间照样算得对（关显示项不影响 Hour/Minute/Second）",
            hidden.Hour == 20 && hidden.Minute == 0 && hidden.Second == 0,
            $"{hidden.Hour}:{hidden.Minute}:{hidden.Second}");

        // 自定义偏移的钟
        var custom = WorldClockItem.FromOffset("", 330);
        custom.Refresh(winter, localNow, localOffset, true, true, true, true, true);
        check("[全球时钟] 自定义 +5:30 的钟：名字自动写成 UTC+05:30、时间 17:30、比本地慢 2 小时 30 分",
            custom.CityName == "UTC+05:30" && custom.TimeText == "17:30:00"
            && custom.OffsetText == "慢 2 小时 30 分" && custom.EnglishName == "",
            $"{custom.CityName} / {custom.TimeText} / {custom.OffsetText}");

        check("[全球时钟] 从城市库建钟会带上英文名与时区 Id",
            WorldClockItem.FromCity(new ClockCityOption("巴黎", "Paris", "Romance Standard Time", "欧洲")) is
            { CityName: "巴黎", EnglishName: "Paris", TimeZoneId: "Romance Standard Time" } paris
            && paris.CustomOffsetMinutes is null,
            "巴黎 / Paris / Romance Standard Time");

        // 时区坏了：不抛异常、标 IsMissing、仍然显示 UTC 时间
        var broken = new WorldClockItem { CityName = "坏时区", TimeZoneId = "不存在的时区" };
        broken.Refresh(winter, localNow, localOffset, true, true, true, true, true);
        check("[全球时钟] 时区坏掉的钟：标 IsMissing、按 UTC 显示、不抛异常",
            broken.IsZoneMissing && broken.TimeText == "12:00:00",
            $"{broken.IsZoneMissing} / {broken.TimeText}");

        // 锁定的界面语义
        var lockable = new WorldClockItem { CityName = "伦敦", TimeZoneId = "GMT Standard Time" };
        var beforeDelete = lockable.CanDelete && lockable.CanDrag
                           && lockable.LockGlyph == WorldClockItem.LockGlyphUnlocked;
        lockable.IsLocked = true;
        var afterLock = !lockable.CanDelete && !lockable.CanDrag
                        && lockable.LockGlyph == WorldClockItem.LockGlyphLocked
                        && lockable.DeleteHint.Contains("解锁") && lockable.LockHint.Contains("解锁");
        lockable.IsLocked = false;
        var afterUnlock = lockable.CanDelete && lockable.CanDrag
                          && lockable.LockGlyph == WorldClockItem.LockGlyphUnlocked;
        check("[全球时钟] 锁定语义：锁上不能删 / 不能拖、图标换成闭合锁、提示改口径；解锁恢复",
            beforeDelete && afterLock && afterUnlock
            && WorldClockItem.LockGlyphLocked != WorldClockItem.LockGlyphUnlocked,
            $"锁定前 {beforeDelete} / 锁定后 {afterLock} / 解锁后 {afterUnlock}");

        check("[全球时钟] 夏令时角标只在夏令时为真（伦敦夏天 / 冬天）",
            BuildLondon(summer: true).DstBadgeText == "夏令时"
            && BuildLondon(summer: false).DstBadgeText == "",
            $"{BuildLondon(summer: true).DstBadgeText} / [{BuildLondon(summer: false).DstBadgeText}]");

        check("[全球时钟] 悬停提示里有城市、时区、当前偏移",
            beijing.DetailTip.Contains("北京") && beijing.DetailTip.Contains("China Standard Time")
            && beijing.DetailTip.Contains("UTC+08:00"),
            beijing.DetailTip.Replace(Environment.NewLine, " | "));
    }

    private static WorldClockItem BuildLondon(bool summer)
    {
        var utc = summer
            ? new DateTime(2024, 7, 15, 12, 0, 0, DateTimeKind.Utc)
            : new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var localOffset = TimeSpan.FromHours(8);
        var item = new WorldClockItem { CityName = "伦敦", TimeZoneId = "GMT Standard Time" };
        item.Refresh(utc, ClockMath.ToWallClock(utc, localOffset), localOffset, true, true, true, true, true);
        return item;
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var defaults = new WorldClockConfig();
        check("[全球时钟] 配置默认值：表盘视图 / 24 小时制 / 显示秒 / 日期 / 时差 / 昼夜底色 / 刻度 / 表盘 160",
            defaults.ViewMode == WorldClockConfig.ViewAnalog
            && defaults.IsAnalogView && defaults.SafeViewMode == WorldClockConfig.ViewAnalog
            && defaults.Hour24 && defaults.ShowSeconds && defaults.ShowDate
            && defaults.ShowOffset && defaults.ColorByDayNight && defaults.ShowDialTicks
            && defaults.DialSize == 160,
            $"{defaults.ViewMode} / DialSize={defaults.DialSize}");

        check("[全球时钟] 配置默认没有城市（默认城市由 ViewModel 首次运行填，尊重用户删除）",
            defaults.Clocks.Count == 0,
            defaults.Clocks.Count.ToString());

        defaults.ViewMode = "乱写的视图";
        check("[全球时钟] 视图写错时回退表盘（不会白屏）",
            defaults.SafeViewMode == WorldClockConfig.ViewAnalog && !defaults.IsAnalogView,
            defaults.SafeViewMode);

        defaults.ViewMode = WorldClockConfig.ViewList;
        check("[全球时钟] 切列表视图后 IsAnalogView 为假",
            defaults.SafeViewMode == WorldClockConfig.ViewList && !defaults.IsAnalogView,
            defaults.SafeViewMode);

        defaults.DialSize = 9999;
        var clampedHigh = defaults.DialSize;
        defaults.DialSize = 1;
        var clampedLow = defaults.DialSize;
        check("[全球时钟] 表盘大小夹在 130 ~ 320",
            clampedHigh == 320 && clampedLow == 130,
            $"{clampedHigh} / {clampedLow}");

        // 配置往返（工具 Id 走临时数据根，不碰仓库 Config）
        var store = ToolConfigStore.For(WorldClockConfig.ToolId, "selfcheck.json");
        try
        {
            var config = store.Load<WorldClockConfig>();
            config.ViewMode = WorldClockConfig.ViewList;
            config.DialSize = 240;
            config.ShowSeconds = false;
            config.Clocks.Clear();
            config.Clocks.Add(new WorldClockItem
            {
                CityName = "伦敦",
                EnglishName = "London",
                TimeZoneId = "GMT Standard Time",
                IsLocked = true,
            });
            config.Clocks.Add(WorldClockItem.FromOffset("老家", 330));

            var reloaded = ToolConfigStore.For(WorldClockConfig.ToolId, "selfcheck.json").Load<WorldClockConfig>();
            check("[全球时钟] 配置往返：视图 / 表盘大小 / 秒开关 / 城市列表都存得住",
                reloaded.ViewMode == WorldClockConfig.ViewList
                && reloaded.DialSize == 240
                && !reloaded.ShowSeconds
                && reloaded.Clocks.Count == 2,
                $"{reloaded.ViewMode} / {reloaded.DialSize} / {reloaded.Clocks.Count} 个城市");

            check("[全球时钟] 配置往返：城市名 / 时区 / 锁定 / 自定义偏移都在",
                reloaded.Clocks[0].CityName == "伦敦"
                && reloaded.Clocks[0].TimeZoneId == "GMT Standard Time"
                && reloaded.Clocks[0].IsLocked
                && reloaded.Clocks[1].CityName == "老家"
                && reloaded.Clocks[1].CustomOffsetMinutes == 330,
                $"{reloaded.Clocks[0].CityName}({reloaded.Clocks[0].IsLocked}) / "
                + $"{reloaded.Clocks[1].CityName}({reloaded.Clocks[1].CustomOffsetMinutes})");

            var json = config.ToJson();
            check("[全球时钟] 瞬态显示字段不写进配置（时间 / 时差 / 昼夜 / Emoji 都不落盘）",
                !json.Contains("timeText") && !json.Contains("offsetText") && !json.Contains("phaseEmoji")
                && !json.Contains("dimAtNight") && !json.Contains("zoneOffsetText"),
                json.Length > 400 ? "配置文件里只有持久化字段" : json);
        }
        finally
        {
            store.DeleteFile();
        }
    }
}
