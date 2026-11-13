using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Notifications;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.NotificationLog;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「通知管理」自检：历史入库 / 删除单条 / 清空 / 上限裁剪（保最新）/ 来源归纳 /
/// 时间显示（今天 / 昨天 / 更早 / 跨年）/ 事件通知 / 持久化往返 / 关掉「记住历史」不落盘。
///
/// <para>顺带验证「通知中心把发出过的东西自动记进历史」这条链路 ——
/// 这是扩展性的关键：以后新工具发通知不需要改通知管理工具。</para>
/// </summary>
internal static class NotificationLogChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckHistoryCore(assert);
        CheckTimeFormat(assert);
        CheckSources(assert);
        CheckCenterIntegration(assert);
        CheckPersistence(assert);
        CheckConfig(assert);
    }

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new NotificationLogTool();
        check("[通知管理] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "notification-history" && tool.Name == "通知管理"
            && tool.EmojiIcon.Length > 0 && tool.Category == "系统" && tool.Order == 172 && tool is ITool,
            $"{tool.Id} / {tool.Name} / {tool.Order}");

        check("[通知管理] 标签含通知 / 通知历史 / 提醒",
            tool.Tags.Contains("通知") && tool.Tags.Contains("通知历史") && tool.Tags.Contains("提醒"),
            string.Join(" / ", tool.Tags));

        check("[通知管理] 描述写明「所有工具的通知都会自动进来」",
            tool.Description.Contains("所有工具") && tool.Description.Contains("筛选"),
            tool.Description);
    }

    /// <summary>造一条记录。</summary>
    private static NotificationRecord Make(string title, string sourceId = "stopwatch",
        NotificationKind kind = NotificationKind.Info, DateTime? utc = null)
        => new(Guid.NewGuid(), utc ?? DateTime.UtcNow, title, "正文", kind, sourceId, sourceId, NotificationMode.InApp);

    private static void CheckHistoryCore(CheckFn check)
    {
        var history = new NotificationHistory();

        check("[通知管理] 初始为空、默认开启、默认上限 500",
            history.Items.Count == 0 && history.Enabled && history.Capacity == 500,
            $"count={history.Items.Count} capacity={history.Capacity}");

        history.Add(Make("第一条"));
        history.Add(Make("第二条"));
        history.Add(Make("第三条"));

        check("[通知管理] 新记录排在最前（最新在前）",
            history.Items.Count == 3 && history.Items[0].Title == "第三条",
            string.Join(" / ", history.Items.Select(i => i.Title)));

        var removed = history.Remove(history.Items[1].Id);
        check("[通知管理] 能删掉指定的一条（其余顺序不变）",
            removed && history.Items.Count == 2
            && history.Items[0].Title == "第三条" && history.Items[1].Title == "第一条",
            string.Join(" / ", history.Items.Select(i => i.Title)));

        check("[通知管理] 删不存在的 Id 返回 false（不抛异常）",
            !history.Remove(Guid.NewGuid()), "false");

        check("[通知管理] 关掉 Enabled 后不再入库（已有记录保留）",
            ToggleAndAdd(history), $"count={history.Items.Count}");

        var cleared = history.Clear();
        check("[通知管理] 清空返回清掉几条、之后为空",
            cleared == 2 && history.Items.Count == 0, cleared.ToString());

        // 上限裁剪：丢最旧的
        var capped = new NotificationHistory { Capacity = 100 };
        for (var i = 0; i < 130; i++) capped.Add(Make($"第 {i} 条"));

        check("[通知管理] 超过上限时丢最旧的（保留最新 100 条）",
            capped.Items.Count == 100 && capped.Items[0].Title == "第 129 条",
            $"count={capped.Items.Count} 最新={capped.Items[0].Title}");

        check("[通知管理] 上限夹在 100~5000",
            ClampCapacity(1) == 100 && ClampCapacity(99999) == 5000 && ClampCapacity(800) == 800,
            $"{ClampCapacity(1)} / {ClampCapacity(99999)} / {ClampCapacity(800)}");

        // 事件：新增 / 删除 / 清空都该触发 Changed
        var changed = 0;
        var watched = new NotificationHistory();
        watched.Changed += (_, _) => changed++;
        watched.Add(Make("x"));
        var afterAdd = changed;
        watched.Remove(watched.Items[0].Id);
        var afterRemove = changed;
        watched.Add(Make("y"));
        var afterSecondAdd = changed;
        watched.Clear();
        var afterClear = changed;

        check("[通知管理] 新增 / 删除 / 清空都会通知界面刷新",
            afterAdd == 1 && afterRemove == 2 && afterSecondAdd == 3 && afterClear == 4,
            $"{afterAdd} / {afterRemove} / {afterSecondAdd} / {afterClear}");
    }

    private static bool ToggleAndAdd(NotificationHistory history)
    {
        history.Enabled = false;
        var before = history.Items.Count;
        history.Add(Make("不该进来的"));
        return history.Items.Count == before;
    }

    private static int ClampCapacity(int value)
    {
        var history = new NotificationHistory { Capacity = value };
        return history.Capacity;
    }

    private static void CheckTimeFormat(CheckFn check)
    {
        // ⚠ DescribeTime 用 TimestampLocal（本机时区）。为了让断言与本机时区无关，
        // 这里把「本机时间」当成输入，再让它自己换回 UTC 存进记录。
        var now = new DateTime(2026, 11, 13, 15, 30, 0);

        NotificationRecord Local(DateTime localTime)
            => new(Guid.NewGuid(), localTime.ToUniversalTime(), "t", "",
                NotificationKind.Info, "s", "s", NotificationMode.InApp);

        var today = Local(now.AddHours(-1));
        check("[通知管理] 今天的记录只显示时间（14:30）",
            today.DescribeTime(now) == "14:30", today.DescribeTime(now));

        var yesterday = Local(now.AddDays(-1));
        check("[通知管理] 昨天显示「昨天 HH:mm」",
            yesterday.DescribeTime(now).StartsWith("昨天"), yesterday.DescribeTime(now));

        var older = Local(new DateTime(2026, 11, 5, 9, 5, 0));
        check("[通知管理] 更早显示「M月d日 HH:mm」",
            older.DescribeTime(now) == "11月5日 09:05", older.DescribeTime(now));

        var lastYear = Local(new DateTime(2025, 3, 4, 8, 0, 0));
        check("[通知管理] 跨年的记录带上年份",
            lastYear.DescribeTime(now) == "2025年3月4日 08:00", lastYear.DescribeTime(now));

        var longBody = new NotificationRecord(Guid.NewGuid(), now.ToUniversalTime(),
            "t", new string('字', 300), NotificationKind.Info, "s", "s", NotificationMode.InApp);
        check("[通知管理] 长正文会被截断成摘要（不撑破列表）",
            longBody.DescribeBody().Length <= 161 && longBody.DescribeBody().EndsWith('…'),
            longBody.DescribeBody().Length.ToString());

        check("[通知管理] 正文里的换行被压成空格（一行显示）",
            new NotificationRecord(Guid.NewGuid(), now.ToUniversalTime(), "t", "第一行\n第二行",
                NotificationKind.Info, "s", "s", NotificationMode.InApp).DescribeBody() == "第一行 第二行",
            "第一行 第二行");
    }

    private static void CheckSources(CheckFn check)
    {
        var history = new NotificationHistory();
        history.Add(Make("a", "stopwatch"));
        history.Add(Make("b", "tool-a"));
        history.Add(Make("c", "stopwatch"));

        var sources = history.Sources();
        check("[通知管理] 来源从历史里归纳（去重、按出现顺序）",
            sources.Count == 2 && sources[0].Id == "stopwatch" && sources[1].Id == "tool-a",
            string.Join(" / ", sources.Select(s => s.Id)));

        // 来源显示名：注册过的用注册名，没注册过的用 Id
        NotificationCenter.RegisterSource("tool-b", "工具乙");
        var record = Make("x", "tool-b");

        check("[通知管理] 登记过的来源显示成人话，没登记的退回 Id",
            record.DisplaySource == "tool-b" && NotificationCenter.DescribeSource("tool-b") == "工具乙"
            && NotificationCenter.DescribeSource("从没登记过的") == "从没登记过的",
            NotificationCenter.DescribeSource("tool-b"));

        check("[通知管理] 来源为空时显示「其它」（不会出现空白标签）",
            NotificationCenter.DescribeSource("") == "其它", NotificationCenter.DescribeSource(""));
    }

    /// <summary>起一个记数的 sink，用来验证「发通知 → 自动进历史」。</summary>
    private sealed class CountingSink : INotificationSink
    {
        public bool IsAvailable => true;
        public int Sent { get; private set; }
        public bool Send(NotificationRequest request, NotificationSettings settings)
        {
            Sent++;
            return true;
        }
    }

    private static void CheckCenterIntegration(CheckFn check)
    {
        var history = new NotificationHistory();
        var sink = new CountingSink();
        var settings = new NotificationSettings { Mode = NotificationMode.InApp };

        NotificationCenter.SetSinks(sink, null);
        NotificationCenter.History = history;

        try
        {
            var sent = NotificationCenter.Show(settings,
                new NotificationRequest("秒表已开始", "正在计时", NotificationKind.Info, "stopwatch"));

            check("[通知管理] 任何工具发通知都会自动进历史（不用改本工具）",
                sent && history.Items.Count == 1
                && history.Items[0].Title == "秒表已开始"
                && history.Items[0].SourceId == "stopwatch",
                $"count={history.Items.Count}");

            check("[通知管理] 历史里记下了实际送达的通道",
                history.Items[0].DeliveredVia == NotificationMode.InApp
                && !history.Items[0].WasFallback,
                history.Items[0].DeliveredVia.ToString());

            // 关掉通知 → 根本不会发出 → 也不该进历史
            var before = history.Items.Count;
            NotificationCenter.Show(new NotificationSettings { Enabled = false },
                new NotificationRequest("不该出现", "", NotificationKind.Info, "x"));

            check("[通知管理] 工具关掉通知时不发也不记",
                history.Items.Count == before, history.Items.Count.ToString());

            // 历史关掉 → 仍然发通知，只是不记
            history.Enabled = false;
            before = history.Items.Count;
            var stillSent = NotificationCenter.Show(settings,
                new NotificationRequest("发了但不记", "", NotificationKind.Info, "stopwatch"));

            check("[通知管理] 历史关掉时通知照发、只是不记录",
                stillSent && history.Items.Count == before, $"sent={stillSent} count={history.Items.Count}");
            history.Enabled = true;
        }
        finally
        {
            NotificationCenter.History = null;
            NotificationCenter.SetSinks(null, null);
        }
    }

    private static void CheckPersistence(CheckFn check)
    {
        var store = ToolConfigStore.For(NotificationLogConfig.ToolId, "selfcheck.json");
        var file = NotificationLogStore.PathOf();
        try
        {
            var records = new List<NotificationRecord>
            {
                Make("第一条", "stopwatch", NotificationKind.Success),
                Make("第二条", "tool-a", NotificationKind.Error),
            };

            NotificationLogStore.Save(records, remember: true);
            var loaded = NotificationLogStore.Load();

            check("[通知管理] 历史持久化往返（标题 / 来源 / 类型都在）",
                loaded.Count == 2 && loaded[0].Title == "第一条"
                && loaded[0].SourceId == "stopwatch"
                && loaded[1].Kind == NotificationKind.Error,
                string.Join(" / ", loaded.Select(r => r.Title)));

            NotificationLogStore.Save(records, remember: false);
            check("[通知管理] 关掉「记住历史」后文件被删掉、读回空",
                !System.IO.File.Exists(file) && NotificationLogStore.Load().Count == 0,
                System.IO.File.Exists(file) ? "文件还在" : "已删除");

            check("[通知管理] 文件不存在时读历史安全返回空（不抛异常）",
                NotificationLogStore.Load().Count == 0, "0 条");
        }
        finally
        {
            store.DeleteFile();
            try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); } catch { /* 忽略 */ }
            try { if (System.IO.File.Exists(file + ".tmp")) System.IO.File.Delete(file + ".tmp"); } catch { /* 忽略 */ }
        }
    }

    private static void CheckConfig(CheckFn check)
    {
        var config = new NotificationLogConfig();
        check("[通知管理] 配置默认值：记住历史 / 上限 500 / 通知开",
            config.RememberHistory && config.Capacity == 500 && config.Notification.Enabled,
            $"{config.RememberHistory} / {config.Capacity}");

        config.Capacity = 99999;
        var clampedHigh = config.Capacity;
        config.Capacity = 1;
        check("[通知管理] 上限夹在 100~5000",
            clampedHigh == 5000 && config.Capacity == 100,
            $"{clampedHigh} / {config.Capacity}");

        var store = ToolConfigStore.For(NotificationLogConfig.ToolId, "selfcheck-config.json");
        try
        {
            var saved = store.Load<NotificationLogConfig>();
            saved.RememberHistory = false;
            saved.Capacity = 1200;

            var reloaded = ToolConfigStore.For(NotificationLogConfig.ToolId, "selfcheck-config.json")
                .Load<NotificationLogConfig>();

            check("[通知管理] 配置往返（记住历史 / 上限）",
                !reloaded.RememberHistory && reloaded.Capacity == 1200,
                $"{reloaded.RememberHistory} / {reloaded.Capacity}");
        }
        finally
        {
            store.DeleteFile();
        }
    }
}
