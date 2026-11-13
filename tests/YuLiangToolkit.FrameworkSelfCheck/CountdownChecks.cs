using YuLiangToolkit.Core.Media;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Countdown;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「倒计时」自检：状态机（开始 / 暂停 / 继续 / 重置 / 加时 / 到点）、
/// 时长解析（秒 / 分:秒 / 时:分:秒 / 1h30m）、显示格式化（向上取整、跨小时）、
/// 快捷键定义、配置默认值与往返。
///
/// <para>纯逻辑：时间由注入的时间戳控制，不用真的等；不创建窗口、不播声音。</para>
/// </summary>
internal static class CountdownChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckStateMachine(assert);
        CheckAddTime(assert);
        CheckParse(assert);
        CheckFormat(assert);
        CheckShortcuts(assert);
        CheckConfig(assert);
    }

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new CountdownTool();
        check("[倒计时] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "countdown" && tool.Name == "倒计时" && tool.EmojiIcon.Length > 0
            && tool.Category == "系统" && tool.Order == 171 && tool is ITool,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[倒计时] 实现了 IShortcutProvider（能出现在主页设置 → 快捷键）",
            tool is YuLiangToolkit.Core.Input.IShortcutProvider,
            tool.GetType().Name);

        check("[倒计时] 暴露 4 条快捷键（开始 / 暂停 / 重置 / +1 分钟）",
            tool.GetShortcuts().Count == 4,
            string.Join(" / ", tool.GetShortcuts().Select(s => s.Name)));

        check("[倒计时] 默认「开始」快捷键是 Ctrl+Alt+D，其余未设置",
            tool.Config.StartHotkey == "Ctrl+Alt+D"
            && tool.Config.PauseHotkey == "" && tool.Config.ResetHotkey == "",
            $"{tool.Config.StartHotkey} / [{tool.Config.PauseHotkey}]");
    }

    private static void CheckStateMachine(CheckFn check)
    {
        var engine = new CountdownEngine(TimeSpan.FromSeconds(10));

        check("[倒计时] 初始状态：停着、剩余 = 设定时长",
            engine.IsIdle && engine.RemainingAt(TimeSpan.Zero) == TimeSpan.FromSeconds(10),
            engine.StateText);

        var started = engine.Start(TimeSpan.Zero);
        check("[倒计时] 开始后按时间递减（3 秒后剩 7 秒）",
            started && engine.IsRunning
            && engine.RemainingAt(TimeSpan.FromSeconds(3)) == TimeSpan.FromSeconds(7),
            engine.RemainingAt(TimeSpan.FromSeconds(3)).ToString());

        check("[倒计时] 已经在走时再按开始不重置",
            !engine.Start(TimeSpan.FromSeconds(4))
            && engine.RemainingAt(TimeSpan.FromSeconds(4)) == TimeSpan.FromSeconds(6),
            engine.RemainingAt(TimeSpan.FromSeconds(4)).ToString());

        var paused = engine.Pause(TimeSpan.FromSeconds(5));
        check("[倒计时] 暂停后剩余冻结（过了 60 秒还是 5 秒）",
            paused && engine.IsPaused
            && engine.RemainingAt(TimeSpan.FromSeconds(65)) == TimeSpan.FromSeconds(5),
            engine.RemainingAt(TimeSpan.FromSeconds(65)).ToString());

        var resumed = engine.Start(TimeSpan.FromSeconds(100));
        check("[倒计时] 暂停后继续：从暂停处接着走（不是从头）",
            resumed && engine.RemainingAt(TimeSpan.FromSeconds(102)) == TimeSpan.FromSeconds(3),
            engine.RemainingAt(TimeSpan.FromSeconds(102)).ToString());

        // 继续后剩 3 秒（102 秒时），再过 3 秒就该到点
        var finishedNow = engine.Pulse(TimeSpan.FromSeconds(105));
        check("[倒计时] 走到 0 时状态变「时间到」、只报一次 JustFinished",
            finishedNow && engine.IsFinished && engine.JustFinished
            && engine.RemainingAt(TimeSpan.FromSeconds(200)) == TimeSpan.Zero,
            $"{engine.StateText} / JustFinished={engine.JustFinished}");

        check("[倒计时] 走完后再 Pulse 不会重复报「刚走完」",
            !engine.Pulse(TimeSpan.FromSeconds(205)) && !engine.JustFinished,
            "false");

        check("[倒计时] 走完再按开始 = 按设定时长重新开始",
            engine.Start(TimeSpan.FromSeconds(300))
            && engine.RemainingAt(TimeSpan.FromSeconds(300)) == TimeSpan.FromSeconds(10),
            engine.RemainingAt(TimeSpan.FromSeconds(300)).ToString());

        engine.Reset();
        check("[倒计时] 重置后回到停着、剩余 = 设定时长",
            engine.IsIdle && engine.RemainingAt(TimeSpan.FromSeconds(999)) == TimeSpan.FromSeconds(10),
            engine.RemainingAt(TimeSpan.FromSeconds(999)).ToString());

        // 进度
        var progress = new CountdownEngine(TimeSpan.FromSeconds(100));
        progress.Start(TimeSpan.Zero);
        check("[倒计时] 进度按比例算（走一半 = 0.5、没走 = 0）",
            Math.Abs(progress.ProgressAt(TimeSpan.FromSeconds(50)) - 0.5) < 0.001
            && Math.Abs(progress.ProgressAt(TimeSpan.Zero)) < 0.001,
            progress.ProgressAt(TimeSpan.FromSeconds(50)).ToString("F3"));

        // 设定时长范围
        var clamp = new CountdownEngine(TimeSpan.FromSeconds(999999));
        check("[倒计时] 设定时长夹在 1 秒 ~ 99 小时",
            clamp.Configured == TimeSpan.FromSeconds(CountdownEngine.MaxSeconds),
            clamp.Configured.ToString());

        var names = new CountdownEngine(TimeSpan.FromSeconds(5));
        var idleName = names.StateText;
        names.Start(TimeSpan.Zero);
        var runningName = names.StateText;

        // 暂停态：时间冻结，Pulse 不该把它推进到「时间到」
        names.Pause(TimeSpan.FromSeconds(1));
        var pausedName = names.StateText;
        var pausedStaysPaused = !names.Pulse(TimeSpan.FromSeconds(999));

        // 继续走完
        names.Start(TimeSpan.FromSeconds(1000));
        names.Pulse(TimeSpan.FromSeconds(1004));
        var finishedName = names.StateText;

        check("[倒计时] 四种状态的中文名（已就绪 / 倒计时中 / 已暂停 / 时间到）",
            idleName == "已就绪" && runningName == "倒计时中"
            && pausedName == "已暂停" && finishedName == "时间到",
            $"{idleName} / {runningName} / {pausedName} / {finishedName}");

        check("[倒计时] 暂停时时间冻结，Pulse 不会把它推进到「时间到」",
            pausedStaysPaused && pausedName == "已暂停",
            $"paused={pausedName} / 没到点={pausedStaysPaused}");
    }

    private static void CheckAddTime(CheckFn check)
    {
        var engine = new CountdownEngine(TimeSpan.FromSeconds(60));

        engine.AddTime(TimeSpan.Zero, TimeSpan.FromMinutes(1));
        check("[倒计时] 停着时加时 = 改设定时长",
            engine.Configured == TimeSpan.FromSeconds(120)
            && engine.RemainingAt(TimeSpan.Zero) == TimeSpan.FromSeconds(120),
            engine.Configured.ToString());

        engine.Start(TimeSpan.Zero);
        engine.AddTime(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        check("[倒计时] 倒计时中加 1 分钟：剩余时间立刻变长",
            engine.RemainingAt(TimeSpan.FromSeconds(10)) == TimeSpan.FromSeconds(170),
            engine.RemainingAt(TimeSpan.FromSeconds(10)).ToString());

        engine.Pause(TimeSpan.FromSeconds(20));
        engine.AddTime(TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(-1));
        // 顺序：设定 60 + 加 1 分 = 120；走 10 秒剩 110；加 1 分变 170；再走 10 秒剩 160；减 1 分 = 100
        check("[倒计时] 暂停时减 1 分钟也能用（160 − 60 = 100 秒）",
            engine.RemainingAt(TimeSpan.FromSeconds(20)) == TimeSpan.FromSeconds(100),
            engine.RemainingAt(TimeSpan.FromSeconds(20)).ToString());

        // 减到 0 就变「时间到」
        var zero = new CountdownEngine(TimeSpan.FromSeconds(30));
        zero.Start(TimeSpan.Zero);
        zero.Pause(TimeSpan.FromSeconds(5));
        zero.AddTime(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(-10));

        check("[倒计时] 减时减到 0 以下就变「时间到」",
            zero.IsFinished && zero.RemainingAt(TimeSpan.FromSeconds(5)) == TimeSpan.Zero,
            zero.StateText);
    }

    private static void CheckParse(CheckFn check)
    {
        check("[倒计时] 时长解析：纯数字 = 秒（90 → 90 秒）",
            CountdownEngine.Parse("90") == TimeSpan.FromSeconds(90),
            CountdownEngine.Parse("90")?.ToString() ?? "null");

        check("[倒计时] 时长解析：分:秒（5:30 → 330 秒）",
            CountdownEngine.Parse("5:30") == TimeSpan.FromSeconds(330),
            CountdownEngine.Parse("5:30")?.ToString() ?? "null");

        check("[倒计时] 时长解析：时:分:秒（1:02:03）",
            CountdownEngine.Parse("1:02:03") == new TimeSpan(1, 2, 3),
            CountdownEngine.Parse("1:02:03")?.ToString() ?? "null");

        check("[倒计时] 时长解析：带单位（1h30m / 90s / 5m / 1时30分）",
            CountdownEngine.Parse("1h30m") == TimeSpan.FromMinutes(90)
            && CountdownEngine.Parse("90s") == TimeSpan.FromSeconds(90)
            && CountdownEngine.Parse("5m") == TimeSpan.FromMinutes(5)
            && CountdownEngine.Parse("1时30分") == TimeSpan.FromMinutes(90),
            $"{CountdownEngine.Parse("1h30m")} / {CountdownEngine.Parse("90s")}");

        check("[倒计时] 时长解析：看不懂的写法返回 null（界面提示，不会静默成 0）",
            CountdownEngine.Parse("abc") is null
            && CountdownEngine.Parse("") is null
            && CountdownEngine.Parse(null) is null
            && CountdownEngine.Parse("1h30") is null
            && CountdownEngine.Parse("1:2:3:4") is null,
            "abc / 空 / null / 1h30 / 1:2:3:4");

        check("[倒计时] 时长解析：全角冒号也认（5：30）",
            CountdownEngine.Parse("5：30") == TimeSpan.FromSeconds(330),
            CountdownEngine.Parse("5：30")?.ToString() ?? "null");

        check("[倒计时] 时长解析：超出范围会被夹住（0 → 1 秒）",
            CountdownEngine.Parse("0") == TimeSpan.FromSeconds(CountdownEngine.MinSeconds),
            CountdownEngine.Parse("0")?.ToString() ?? "null");
    }

    private static void CheckFormat(CheckFn check)
    {
        check("[倒计时] 显示：不足 1 小时写 mm:ss",
            CountdownEngine.Format(TimeSpan.FromSeconds(65)) == "01:05"
            && CountdownEngine.Format(TimeSpan.FromSeconds(5)) == "00:05",
            CountdownEngine.Format(TimeSpan.FromSeconds(65)));

        check("[倒计时] 显示：超过 1 小时自动带小时位",
            CountdownEngine.Format(new TimeSpan(1, 2, 3)) == "1:02:03",
            CountdownEngine.Format(new TimeSpan(1, 2, 3)));

        check("[倒计时] 显示：秒数**向上取整**（29.4 秒该显示 00:30，不能提前显示 0）",
            CountdownEngine.Format(TimeSpan.FromSeconds(29.4)) == "00:30",
            CountdownEngine.Format(TimeSpan.FromSeconds(29.4)));

        check("[倒计时] 显示：负时长按 0 处理",
            CountdownEngine.Format(TimeSpan.FromSeconds(-5)) == "00:00",
            CountdownEngine.Format(TimeSpan.FromSeconds(-5)));

        check("[倒计时] 人话描述：1 小时 30 分 / 45 秒",
            CountdownEngine.Describe(TimeSpan.FromMinutes(90)) == "1 小时 30 分"
            && CountdownEngine.Describe(TimeSpan.FromSeconds(45)) == "45 秒"
            && CountdownEngine.Describe(TimeSpan.Zero) == "0 秒",
            CountdownEngine.Describe(TimeSpan.FromMinutes(90)));
    }

    private static void CheckShortcuts(CheckFn check)
    {
        var tool = new CountdownTool();
        var shortcuts = tool.GetShortcuts();

        check("[倒计时] 四条快捷键默认：开始有快捷键、其余留空",
            shortcuts.First(s => s.Id == CountdownTool.ShortcutIdStart).Primary.IsEmpty == false
            && shortcuts.First(s => s.Id == CountdownTool.ShortcutIdPause).Primary.IsEmpty
            && shortcuts.First(s => s.Id == CountdownTool.ShortcutIdReset).Primary.IsEmpty
            && shortcuts.First(s => s.Id == CountdownTool.ShortcutIdAddMinute).Primary.IsEmpty,
            string.Join(" / ", shortcuts.Select(s => $"{s.Name}={s.DisplayText}")));

        check("[倒计时] 四条都允许单条禁用（CanSplit 为假：倒计时不需要分步）",
            shortcuts.All(s => s.Enabled) && shortcuts.All(s => !s.CanSplit),
            "4 条启用 / 都不支持分步");

        // 改键写回配置
        YuLiangToolkit.Core.Input.KeyCombo.TryParse("Ctrl+Shift+9", out var combo);
        shortcuts.First(s => s.Id == CountdownTool.ShortcutIdPause).SetCombo(combo, null);

        check("[倒计时] 改键会写回配置",
            tool.Config.PauseHotkey == "Ctrl+Shift+9",
            tool.Config.PauseHotkey);

        // 单条禁用
        tool.GetShortcuts().First(s => s.Id == CountdownTool.ShortcutIdReset).SetEnabled(false);
        check("[倒计时] 单条禁用写回配置",
            !tool.Config.ResetEnabled, tool.Config.ResetEnabled.ToString());

        // 工具级总开关
        ((YuLiangToolkit.Core.Input.IShortcutProvider)tool).ShortcutsEnabled = false;
        check("[倒计时] 工具级总开关写回配置",
            !tool.Config.ShortcutsEnabled, tool.Config.ShortcutsEnabled.ToString());
    }

    private static void CheckConfig(CheckFn check)
    {
        var config = new CountdownConfig();
        check("[倒计时] 配置默认值：5 分钟 / 四个预设 / 显示进度环 / 通知开",
            config.DurationSeconds == 300
            && config.Presets.Count == 4
            && config.ShowProgressRing && config.Notification.Enabled,
            $"{config.DurationSeconds}s / 预设 {config.Presets.Count} 个");

        check("[倒计时] 预设解析：非法项被忽略",
            ParsePresets("60,abc,300,999999") is { Count: 2 },
            string.Join(",", ParsePresets("60,abc,300,999999")));

        config.DurationSeconds = 999999;
        var clamped = config.DurationSeconds;
        config.DurationSeconds = 0;
        check("[倒计时] 时长夹在 1 秒 ~ 99 小时",
            clamped == CountdownEngine.MaxSeconds && config.DurationSeconds == CountdownEngine.MinSeconds,
            $"{clamped} / {config.DurationSeconds}");

        // 配置往返
        var store = YuLiangToolkit.Core.Configuration.ToolConfigStore
            .For(CountdownConfig.ToolId, "selfcheck.json");
        try
        {
            var saved = store.Load<CountdownConfig>();
            saved.DurationSeconds = 1500;
            saved.StartHotkey = "Ctrl+Alt+5";
            saved.Muted = true;

            var reloaded = YuLiangToolkit.Core.Configuration.ToolConfigStore
                .For(CountdownConfig.ToolId, "selfcheck.json").Load<CountdownConfig>();

            check("[倒计时] 配置往返（时长 / 快捷键 / 静音）",
                reloaded.DurationSeconds == 1500
                && reloaded.StartHotkey == "Ctrl+Alt+5"
                && reloaded.Muted,
                $"{reloaded.DurationSeconds} / {reloaded.StartHotkey}");
        }
        finally
        {
            store.DeleteFile();
        }

        check("[声音] 倒计时也能用系统音清单（与秒表共用）",
            SoundPlayer.BuiltInChoices.Count >= 3
            && SoundPlayer.IsPlayable("system:SystemExclamation"),
            $"{SoundPlayer.BuiltInChoices.Count} 项");
    }

    private static IReadOnlyList<int> ParsePresets(string text)
    {
        var config = new CountdownConfig { CustomPresets = text };
        return config.Presets;
    }
}
