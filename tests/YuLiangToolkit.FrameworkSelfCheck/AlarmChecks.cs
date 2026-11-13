using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Alarm;
using YuLiangToolkit.Tools.Alarm.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「闹钟」自检：重复天解析 / 存储、下一次响铃时刻（跨天 / 跨周 / 只响一次 / 今天已过）、
/// 贪睡推迟、到点判定、条目规范化、配置默认值与往返。
///
/// <para>纯逻辑：「现在」全部显式传，所以结果确定；不创建窗口、不播声音。</para>
/// </summary>
internal static class AlarmChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckDays(assert);
        CheckNextOccurrence(assert);
        CheckSnooze(assert);
        CheckEntry(assert);
        CheckConfig(assert);
    }

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new AlarmTool();
        check("[闹钟] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "alarm" && tool.Name == "闹钟" && tool.EmojiIcon.Length > 0
            && tool.Category == "系统" && tool.Order == 173 && tool is ITool,
            $"{tool.Id} / {tool.Name} / {tool.Order}");

        check("[闹钟] 标签含闹钟 / 提醒 / 贪睡",
            tool.Tags.Contains("闹钟") && tool.Tags.Contains("提醒") && tool.Tags.Contains("贪睡"),
            string.Join(" / ", tool.Tags));

        check("[闹钟] 描述写明按星期重复与贪睡",
            tool.Description.Contains("星期") && tool.Description.Contains("贪睡"),
            tool.Description);
    }

    private static void CheckDays(CheckFn check)
    {
        var mondayToFriday = AlarmSchedule.ParseDays("1,2,3,4,5");

        check("[闹钟] 重复天解析：1~5 = 周一到周五",
            mondayToFriday.Count == 5
            && mondayToFriday.Contains(DayOfWeek.Monday)
            && mondayToFriday.Contains(DayOfWeek.Friday)
            && !mondayToFriday.Contains(DayOfWeek.Saturday),
            string.Join(",", mondayToFriday));

        check("[闹钟] 重复天解析：7 = 周日",
            AlarmSchedule.ParseDays("7").SequenceEqual(new[] { DayOfWeek.Sunday }),
            "7 → 周日");

        check("[闹钟] 重复天解析：空 = 不重复、非法项忽略",
            AlarmSchedule.ParseDays("").Count == 0
            && AlarmSchedule.ParseDays(null).Count == 0
            && AlarmSchedule.ParseDays("0,8,abc,3").SequenceEqual(new[] { DayOfWeek.Wednesday }),
            "0/8/abc 被忽略");

        check("[闹钟] 重复天存储：去重并按周一在前排序",
            AlarmSchedule.FormatDays(new[] { DayOfWeek.Sunday, DayOfWeek.Wednesday, DayOfWeek.Monday })
                == "1,3,7",
            AlarmSchedule.FormatDays(new[] { DayOfWeek.Sunday, DayOfWeek.Wednesday, DayOfWeek.Monday }));

        check("[闹钟] 星期中文名：周一 … 周日",
            AlarmSchedule.WeekdayName(DayOfWeek.Monday) == "周一"
            && AlarmSchedule.WeekdayName(DayOfWeek.Sunday) == "周日",
            $"{AlarmSchedule.WeekdayName(DayOfWeek.Monday)} … {AlarmSchedule.WeekdayName(DayOfWeek.Sunday)}");
    }

    private static void CheckNextOccurrence(CheckFn check)
    {
        // 2026-11-13 是周五
        var fridayMorning = new DateTime(2026, 11, 13, 8, 0, 0);

        var laterToday = AlarmSchedule.NextOccurrence(TimeSpan.FromHours(9), Array.Empty<DayOfWeek>(), fridayMorning);
        check("[闹钟] 只响一次：今天还没到点 → 就是今天",
            laterToday == new DateTime(2026, 11, 13, 9, 0, 0),
            laterToday?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        var alreadyPassed = AlarmSchedule.NextOccurrence(TimeSpan.FromHours(7), Array.Empty<DayOfWeek>(), fridayMorning);
        check("[闹钟] 只响一次：今天已过点 → 明天",
            alreadyPassed == new DateTime(2026, 11, 14, 7, 0, 0),
            alreadyPassed?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        // 工作日闹钟：周五 8 点看 7 点的，下一次是下周一（跳过周末）
        var weekdayOnly = AlarmSchedule.NextOccurrence(
            TimeSpan.FromHours(7),
            AlarmSchedule.ParseDays("1,2,3,4,5"),
            fridayMorning);

        check("[闹钟] 工作日闹钟：周五已过点 → 下周一（跳过周末）",
            weekdayOnly == new DateTime(2026, 11, 16, 7, 0, 0)
            && weekdayOnly?.DayOfWeek == DayOfWeek.Monday,
            weekdayOnly?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        // 每天：周五已过点 → 周六
        var daily = AlarmSchedule.NextOccurrence(
            TimeSpan.FromHours(7),
            AlarmSchedule.ParseDays("1,2,3,4,5,6,7"),
            fridayMorning);

        check("[闹钟] 每天重复：周五已过点 → 明天（周六）",
            daily == new DateTime(2026, 11, 14, 7, 0, 0),
            daily?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        // 只设周三：周五 → 下周三
        var wednesdayOnly = AlarmSchedule.NextOccurrence(
            TimeSpan.FromHours(7), AlarmSchedule.ParseDays("3"), fridayMorning);

        check("[闹钟] 只设周三：周五看到的是下周三",
            wednesdayOnly == new DateTime(2026, 11, 18, 7, 0, 0),
            wednesdayOnly?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        // 时刻必须是严格大于现在
        var now = new DateTime(2026, 11, 13, 7, 0, 0);
        var exact = AlarmSchedule.NextOccurrence(TimeSpan.FromHours(7), Array.Empty<DayOfWeek>(), now);
        check("[闹钟] 正好等于现在的时刻不算（必须严格大于，避免立刻触发）",
            exact == new DateTime(2026, 11, 14, 7, 0, 0),
            exact?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        check("[闹钟] 非法时刻返回 null",
            AlarmSchedule.NextOccurrence(TimeSpan.FromHours(25), Array.Empty<DayOfWeek>(), now) is null,
            "null");

        // 说明文字
        check("[闹钟] 下次时间的说明：今天 / 明天 / 周三",
            AlarmSchedule.DescribeNext(new DateTime(2026, 11, 13, 9, 0, 0), fridayMorning) == "今天 09:00"
            && AlarmSchedule.DescribeNext(new DateTime(2026, 11, 14, 7, 0, 0), fridayMorning) == "明天 07:00"
            && AlarmSchedule.DescribeNext(new DateTime(2026, 11, 18, 7, 0, 0), fridayMorning) == "周三 07:00",
            AlarmSchedule.DescribeNext(new DateTime(2026, 11, 18, 7, 0, 0), fridayMorning));

        check("[闹钟] 剩余时间的说明：小时+分 / 不足 1 分",
            AlarmSchedule.DescribeUntil(fridayMorning.AddMinutes(83), fridayMorning) == "1 小时 23 分"
            && AlarmSchedule.DescribeUntil(fridayMorning.AddSeconds(30), fridayMorning) == "不到 1 分",
            AlarmSchedule.DescribeUntil(fridayMorning.AddMinutes(83), fridayMorning));
    }

    private static void CheckSnooze(CheckFn check)
    {
        var now = new DateTime(2026, 11, 13, 7, 0, 0);

        check("[闹钟] 贪睡：默认推迟 5 分钟",
            AlarmSchedule.TrimSnooze(now) == now.AddMinutes(5),
            AlarmSchedule.TrimSnooze(now).ToString("HH:mm"));

        check("[闹钟] 贪睡：可自定义分钟数，并夹在 1~60",
            AlarmSchedule.TrimSnooze(now, 10) == now.AddMinutes(10)
            && AlarmSchedule.TrimSnooze(now, 0) == now.AddMinutes(1)
            && AlarmSchedule.TrimSnooze(now, 999) == now.AddMinutes(60),
            $"{AlarmSchedule.TrimSnooze(now, 10):HH:mm} / {AlarmSchedule.TrimSnooze(now, 0):HH:mm}");
    }

    private static void CheckEntry(CheckFn check)
    {
        var entry = new AlarmEntry { Time = "7:5", Label = "起床", Days = "1,2,3,4,5" };

        check("[闹钟] 时间写法会规范化（7:5 → 07:05）",
            entry.Time == "07:05" && entry.DisplayTime == "07:05",
            entry.Time);

        check("[闹钟] 时刻解析成 TimeSpan（07:05 → 7 小时 5 分）",
            entry.TimeOfDay == new TimeSpan(7, 5, 0),
            entry.TimeOfDay.ToString());

        check("[闹钟] 重复说明：工作日 / 每天 / 只响一次",
            new AlarmEntry { Days = "1,2,3,4,5" }.RepeatText == "周一、周二、周三、周四、周五"
            && new AlarmEntry { Days = "1,2,3,4,5,6,7" }.RepeatText == "每天"
            && new AlarmEntry { Days = "" }.RepeatText == "只响一次",
            new AlarmEntry { Days = "1,2,3,4,5" }.RepeatText);

        check("[闹钟] 星期勾选框与配置字符串双向同步",
            MondayToggleWorks(out var detail), detail);

        check("[闹钟] 标题：有备注用备注、没有就用时间",
            new AlarmEntry { Time = "07:00", Label = "起床" }.DisplayTitle == "起床"
            && new AlarmEntry { Time = "07:00", Label = "" }.DisplayTitle == "07:00",
            "起床 / 07:00");

        check("[闹钟] 开关状态文字",
            new AlarmEntry { Enabled = true }.StateText == "已开启"
            && new AlarmEntry { Enabled = false }.StateText == "已关闭",
            "已开启 / 已关闭");

        // 回归：程序刚启动时，今天的设定时刻早就过了 —— 不能立刻响
        var lateStart = new AlarmViewModel();
        var ringingOnStartup = lateStart.IsRinging;
        lateStart.StopAll();

        check("[闹钟] 启动时不会把今天已经过去的点补响（曾一打开就响）",
            !ringingOnStartup, ringingOnStartup ? "启动就在响" : "启动不响");

        var workday = AlarmEntry.CreateWorkdayDefault();
        check("[闹钟] 默认闹钟：工作日 07:00「起床」",
            workday.Time == "07:00" && workday.Label == "起床" && workday.Days == "1,2,3,4,5",
            $"{workday.Time} / {workday.Label} / {workday.Days}");
    }

    private static bool MondayToggleWorks(out string detail)
    {
        var entry = new AlarmEntry { Days = "3" };

        var before = entry.Monday;
        entry.Monday = true;          // 勾上周一
        var afterAdd = entry.Days;

        entry.Monday = false;         // 再取消
        var afterRemove = entry.Days;

        detail = $"{before} → {afterAdd} → {afterRemove}";
        return !before && afterAdd.Contains('1') && !afterRemove.Contains('1') && afterRemove.Contains('3');
    }

    private static void CheckConfig(CheckFn check)
    {
        var config = new AlarmConfig();
        check("[闹钟] 配置默认值：贪睡 5 分 / 不静音 / 通知开",
            config.SnoozeMinutes == AlarmSchedule.DefaultSnoozeMinutes
            && !config.Muted && config.Notification.Enabled,
            $"{config.SnoozeMinutes} 分");

        config.SnoozeMinutes = 999;
        var high = config.SnoozeMinutes;
        config.SnoozeMinutes = 0;
        check("[闹钟] 贪睡时长夹在 1~60 分",
            high == 60 && config.SnoozeMinutes == 1,
            $"{high} / {config.SnoozeMinutes}");

        check("[闹钟] EnabledCount 只数启用的",
            CountEnabled() == 2, CountEnabled().ToString());

        // 配置往返
        var store = YuLiangToolkit.Core.Configuration.ToolConfigStore
            .For(AlarmConfig.ToolId, "selfcheck.json");
        try
        {
            var saved = store.Load<AlarmConfig>();
            saved.Sound = "system:SystemHand";
            saved.RepeatSound = true;
            saved.Alarms.Clear();
            saved.Alarms.Add(new AlarmEntry { Time = "06:30", Label = "早起", Days = "1,3,5" });

            var reloaded = YuLiangToolkit.Core.Configuration.ToolConfigStore
                .For(AlarmConfig.ToolId, "selfcheck.json").Load<AlarmConfig>();

            check("[闹钟] 配置往返（声音 / 反复播放 / 闹钟条目含重复天）",
                reloaded.Sound == "system:SystemHand"
                && reloaded.RepeatSound
                && reloaded.Alarms.Count == 1
                && reloaded.Alarms[0].Time == "06:30"
                && reloaded.Alarms[0].Label == "早起"
                && reloaded.Alarms[0].Days == "1,3,5",
                $"{reloaded.Alarms.Count} 个 / {reloaded.Alarms[0].Time}");

            check("[闹钟] 重启后闹钟的重复天仍然正确解析",
                reloaded.Alarms[0].RepeatDays.Count == 3
                && reloaded.Alarms[0].RepeatDays.Contains(DayOfWeek.Monday),
                string.Join(",", reloaded.Alarms[0].RepeatDays));
        }
        finally
        {
            store.DeleteFile();
        }
    }

    private static int CountEnabled()
    {
        var config = new AlarmConfig();
        config.Alarms.Add(new AlarmEntry { Time = "07:00", Enabled = true });
        config.Alarms.Add(new AlarmEntry { Time = "08:00", Enabled = false });
        config.Alarms.Add(new AlarmEntry { Time = "09:00", Enabled = true });
        return config.EnabledCount;
    }
}
