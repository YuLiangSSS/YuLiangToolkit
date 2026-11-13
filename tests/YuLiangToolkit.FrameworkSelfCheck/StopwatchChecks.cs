using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Media;
using YuLiangToolkit.Core.Notifications;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Stopwatch;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「秒表」自检：计时状态机（开始 / 暂停 / 继续 / 停止归零 / 计次切分 / 幂等）、
/// 时长格式化（不足一分钟 / 跨分钟 / 跨小时 / 不进位）、快捷键定义（单步 / 分步 / 启停位）、
/// 声音选项解析、配置默认值与 JSON 往返。
///
/// <para><b>纯逻辑</b>：状态机的时间由调用方喂（不读真实时钟），所以可以任意跳时间断言、
/// 不用真的等 3 秒；不创建窗口、不播声音、不注册热键、不碰仓库 Data/Config。</para>
/// </summary>
internal static class StopwatchChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckStateMachine(assert);
        CheckFormat(assert);
        CheckShortcutDefinitions(assert);
        CheckSoundChoices(assert);
        CheckConfig(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new StopwatchTool();
        check("[秒表] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定，且实现 ITool",
            tool.Id == StopwatchConfig.ToolId && tool.Id == "stopwatch"
            && tool.Name == "秒表" && tool.EmojiIcon.Length > 0
            && tool.Category == "系统" && tool.Order == 170
            && tool is ITool,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[秒表] 归在「系统」分类下（CategoryPath 与 Category 一致）",
            tool.CategoryPath.Count == 1 && tool.CategoryPath[0] == "系统",
            string.Join(" / ", tool.CategoryPath));

        check("[秒表] 标签包含秒表 / 快捷键 / 计次（主页搜索能命中）",
            tool.Tags.Contains("秒表") && tool.Tags.Contains("快捷键") && tool.Tags.Contains("计次"),
            string.Join(" / ", tool.Tags));

        check("[秒表] 描述写明全局快捷键与后台通知",
            tool.Description.Contains("快捷键") && tool.Description.Contains("通知"),
            tool.Description);

        check("[秒表] 实现了 IShortcutProvider（能出现在主页设置 → 快捷键）",
            tool is YuLiangToolkit.Core.Input.IShortcutProvider,
            tool.GetType().Name);

        check("[秒表] 生成了三条快捷键定义（开始 / 暂停 / 停止）",
            tool.GetShortcuts().Count == 3
            && tool.GetShortcuts().Select(s => s.Id).OrderBy(x => x).SequenceEqual(new[] { "pause", "start", "stop" }),
            string.Join(" / ", tool.GetShortcuts().Select(s => s.Name)));
    }

    // ---------------------------------------------------------------- 状态机

    private static void CheckStateMachine(CheckFn check)
    {
        var session = new StopwatchSession();
        check("[秒表] 初始状态：停着、0 时长、无计次",
            session.IsIdle && session.Elapsed == TimeSpan.Zero && session.Laps.Count == 0,
            session.StateText);

        // 开始 → 走 3 秒
        var started = session.Start(TimeSpan.Zero);
        var afterThreeSeconds = session.ElapsedAt(TimeSpan.FromSeconds(3));
        check("[秒表] 开始后时长按时间走（3 秒后就该是 3 秒）",
            started && session.IsRunning && afterThreeSeconds == TimeSpan.FromSeconds(3),
            afterThreeSeconds.ToString());

        // 重复开始幂等
        var secondStart = session.Start(TimeSpan.FromSeconds(4));
        check("[秒表] 已经在走时再按开始不做任何事（幂等，不重置）",
            !secondStart && session.ElapsedAt(TimeSpan.FromSeconds(4)) == TimeSpan.FromSeconds(4),
            session.ElapsedAt(TimeSpan.FromSeconds(4)).ToString());

        // 暂停 → 时间冻结
        var paused = session.Pause(TimeSpan.FromSeconds(5));
        var frozenAtFive = session.ElapsedAt(TimeSpan.FromSeconds(60));
        check("[秒表] 暂停后时间冻结（过了 55 秒还是 5 秒）",
            paused && session.IsPaused && frozenAtFive == TimeSpan.FromSeconds(5),
            frozenAtFive.ToString());

        // 暂停时再暂停 = 什么都不做
        check("[秒表] 暂停状态再按暂停不做任何事",
            !session.Pause(TimeSpan.FromSeconds(70)), "false");

        // 继续 → 接着走
        var resumed = session.Start(TimeSpan.FromSeconds(100));
        var afterResume = session.ElapsedAt(TimeSpan.FromSeconds(102));
        check("[秒表] 继续后从暂停处接着走（不是从 0 重来）",
            resumed && afterResume == TimeSpan.FromSeconds(7),
            afterResume.ToString());

        // 计次
        var lap1 = session.Lap(TimeSpan.FromSeconds(103));   // 总 8 秒
        var lap2 = session.Lap(TimeSpan.FromSeconds(105));   // 总 10 秒
        check("[秒表] 计次：第一次间隔 = 总时长，第二次间隔 = 两次之差",
            lap1 is { Index: 1 } && lap1.Value.Total == TimeSpan.FromSeconds(8)
            && lap1.Value.Split == TimeSpan.FromSeconds(8)
            && lap2 is { Index: 2 } && lap2.Value.Split == TimeSpan.FromSeconds(2)
            && session.Laps.Count == 2,
            $"#{lap1?.Index} {lap1?.Total}/{lap1?.Split} · #{lap2?.Index} {lap2?.Total}/{lap2?.Split}");

        // 停止归零
        var stopped = session.Stop();
        check("[秒表] 停止后归零并清空计次",
            stopped && session.IsIdle && session.Elapsed == TimeSpan.Zero && session.Laps.Count == 0,
            session.StateText);

        check("[秒表] 已经是停止状态再按停止返回 false（不误报变化）",
            !session.Stop(), "false");

        check("[秒表] 停着时计次返回 null（不会凭空记一笔）",
            session.Lap(TimeSpan.FromSeconds(200)) is null, "null");

        // 暂停时也能计次
        var pausedSession = new StopwatchSession();
        pausedSession.Start(TimeSpan.Zero);
        pausedSession.Pause(TimeSpan.FromSeconds(2));
        check("[秒表] 暂停状态下也能计次",
            pausedSession.Lap(TimeSpan.FromSeconds(30)) is { Index: 1 },
            pausedSession.Laps.Count.ToString());

        // 清空计次不影响计时
        pausedSession.ClearLaps();
        check("[秒表] 清空计次不影响已走的时间",
            pausedSession.Laps.Count == 0 && pausedSession.ElapsedAt(TimeSpan.FromSeconds(30)) == TimeSpan.FromSeconds(2),
            pausedSession.ElapsedAt(TimeSpan.FromSeconds(30)).ToString());

        // Pulse 把时间折算进累计值
        var pulseSession = new StopwatchSession();
        pulseSession.Start(TimeSpan.Zero);
        pulseSession.Pulse(TimeSpan.FromMilliseconds(1500));
        check("[秒表] Pulse 把当前时间折算进累计值（暂停后 Elapsed 也准）",
            pulseSession.Elapsed == TimeSpan.FromMilliseconds(1500),
            pulseSession.Elapsed.ToString());

        // 状态文字
        var textSession = new StopwatchSession();
        var idleText = textSession.StateText;
        textSession.Start(TimeSpan.Zero);
        var runningText = textSession.StateText;
        textSession.Pause(TimeSpan.FromSeconds(1));
        var pausedText = textSession.StateText;
        check("[秒表] 三种状态各有中文说明（已停止 / 计时中 / 已暂停）",
            idleText == "已停止" && runningText == "计时中" && pausedText == "已暂停",
            $"{idleText} / {runningText} / {pausedText}");
    }

    // ---------------------------------------------------------------- 格式化

    private static void CheckFormat(CheckFn check)
    {
        check("[秒表] 不足一分钟：00:00.00 起步、00:03.50 这种写法",
            StopwatchSession.Format(TimeSpan.Zero) == "00:00.00"
            && StopwatchSession.Format(TimeSpan.FromMilliseconds(3500)) == "00:03.50",
            StopwatchSession.Format(TimeSpan.FromMilliseconds(3500)));

        check("[秒表] 跨分钟：01:05.25（分补零、秒补零）",
            StopwatchSession.Format(new TimeSpan(0, 0, 1, 5, 250)) == "01:05.25",
            StopwatchSession.Format(new TimeSpan(0, 0, 1, 5, 250)));

        check("[秒表] 跨小时：1:00:00.00（小时不补零、分秒补零）",
            StopwatchSession.Format(TimeSpan.FromHours(1)) == "1:00:00.00"
            && StopwatchSession.Format(new TimeSpan(0, 2, 3, 4) + TimeSpan.FromMilliseconds(500)) == "2:03:04.50",
            StopwatchSession.Format(new TimeSpan(0, 2, 3, 4) + TimeSpan.FromMilliseconds(500)));

        check("[秒表] 毫秒向下取整（999.9ms 不能显示成 01.00，应该还是 00.99）",
            StopwatchSession.Format(TimeSpan.FromMilliseconds(999.9)) == "00:00.99",
            StopwatchSession.Format(TimeSpan.FromMilliseconds(999.9)));

        check("[秒表] 关掉百分秒后只到秒（00:03）",
            StopwatchSession.Format(TimeSpan.FromMilliseconds(3500), showMilliseconds: false) == "00:03",
            StopwatchSession.Format(TimeSpan.FromMilliseconds(3500), false));

        check("[秒表] 负时长按 0 处理（不会显示成 -00:01）",
            StopwatchSession.Format(TimeSpan.FromSeconds(-5)) == "00:00.00",
            StopwatchSession.Format(TimeSpan.FromSeconds(-5)));

        check("[秒表] 精确格式到毫秒（复制 / 导出用）",
            StopwatchSession.FormatPrecise(TimeSpan.FromMilliseconds(1234)) == "00:01.234"
            && StopwatchSession.FormatPrecise(new TimeSpan(0, 1, 2, 3) + TimeSpan.FromMilliseconds(4)) == "1:02:03.004",
            StopwatchSession.FormatPrecise(new TimeSpan(0, 1, 2, 3) + TimeSpan.FromMilliseconds(4)));    }

    // ---------------------------------------------------------------- 快捷键定义

    private static void CheckShortcutDefinitions(CheckFn check)
    {
        var tool = new StopwatchTool();
        var shortcuts = tool.GetShortcuts();

        check("[秒表] 默认三条都是「同时」形态（没有第二步）",
            shortcuts.All(s => s.Kind == YuLiangToolkit.Core.Input.ShortcutKind.Single),
            string.Join(" / ", shortcuts.Select(s => s.Kind)));

        check("[秒表] 默认组合互不冲突（Ctrl+Alt+F9 / F10 / F11）",
            shortcuts.Select(s => s.Primary.ToString()).Distinct().Count() == 3,
            string.Join(" / ", shortcuts.Select(s => s.Primary.ToString())));

        check("[秒表] 默认三条都启用、都允许拆分",
            shortcuts.All(s => s.Enabled && s.CanSplit),
            $"启用 {shortcuts.Count(s => s.Enabled)} / 可拆 {shortcuts.Count(s => s.CanSplit)}");

        // 改键 → 写回配置
        var start = shortcuts.First(s => s.Id == StopwatchTool.ShortcutIdStart);
        YuLiangToolkit.Core.Input.KeyCombo.TryParse("Ctrl+Shift+F9", out var newCombo);
        start.SetCombo(newCombo, null);

        check("[秒表] 在汇总页改键会写回工具配置",
            tool.Config.Start == "Ctrl+Shift+F9"
            && tool.GetShortcuts().First(s => s.Id == StopwatchTool.ShortcutIdStart).Primary.ToString() == "Ctrl+Shift+F9",
            tool.Config.Start);

        // 拆分成两步
        var startAfter = tool.GetShortcuts().First(s => s.Id == StopwatchTool.ShortcutIdStart);
        YuLiangToolkit.Core.Input.KeyCombo.TryParse("Ctrl+K", out var second);
        startAfter.SetCombo(startAfter.Primary, second);

        check("[秒表] 设置第二步后变成分步形态并写进第二个配置字段",
            tool.Config.StartSecond == "Ctrl+K"
            && tool.GetShortcuts().First(s => s.Id == StopwatchTool.ShortcutIdStart).Kind
                == YuLiangToolkit.Core.Input.ShortcutKind.Sequence,
            tool.Config.StartSecond);

        // 单条禁用
        var pause = tool.GetShortcuts().First(s => s.Id == StopwatchTool.ShortcutIdPause);
        pause.SetEnabled(false);

        check("[秒表] 单条禁用写回配置（开始那条不受影响）",
            !tool.Config.PauseEnabled && tool.Config.StartEnabled,
            $"暂停 {tool.Config.PauseEnabled} / 开始 {tool.Config.StartEnabled}");

        // 工具级总开关
        var provider = (YuLiangToolkit.Core.Input.IShortcutProvider)tool;
        provider.ShortcutsEnabled = false;

        check("[秒表] 工具级总开关写回配置",
            !tool.Config.ShortcutsEnabled, tool.Config.ShortcutsEnabled.ToString());

        // 序列超时
        YuLiangToolkit.Core.Input.IShortcutActionHost host = tool;
        host.SequenceTimeoutMs = 3500;
        check("[秒表] 分步等待时长可调且写回配置",
            tool.Config.SequenceTimeoutMs == 3500, tool.Config.SequenceTimeoutMs.ToString());

        host.SequenceTimeoutMs = 99;
        check("[秒表] 分步等待时长被夹到合法范围（500~5000）",
            tool.Config.SequenceTimeoutMs == 500, tool.Config.SequenceTimeoutMs.ToString());

        // AnySequence 计算属性
        check("[秒表] 有任一条是分步时 AnySequence 为真",
            tool.Config.AnySequence, tool.Config.AnySequence.ToString());
    }

    // ---------------------------------------------------------------- 声音

    private static void CheckSoundChoices(CheckFn check)
    {
        check("[声音] 内置清单第一项是「不播放」（id = none）",
            SoundPlayer.BuiltInChoices.Count > 0
            && SoundPlayer.BuiltInChoices[0].Id == SoundPlayer.NoneId
            && SoundPlayer.BuiltInChoices[0].Name == "不播放",
            SoundPlayer.BuiltInChoices[0].Id);

        check("[声音] 内置系统音都有中文名、id 以 system: 开头",
            SoundPlayer.SystemSounds.Count >= 3
            && SoundPlayer.SystemSounds.All(s => s.Id.StartsWith("system:") && s.Name.Length > 0),
            string.Join(" / ", SoundPlayer.SystemSounds.Select(s => s.Name)));

        check("[声音] 空 / none 都不播（安全返回 false）",
            !SoundPlayer.TryPlay("") && !SoundPlayer.TryPlay(null) && !SoundPlayer.TryPlay(SoundPlayer.NoneId),
            "空 / null / none");

        check("[声音] 文件来源的 id 能取出路径、能包装成 id",
            SoundPlayer.FromFile(@"C:\a\b.wav") == @"file:C:\a\b.wav"
            && SoundPlayer.TryGetFilePath(@"file:C:\a\b.wav") == @"C:\a\b.wav"
            && SoundPlayer.TryGetFilePath(SoundPlayer.NoneId) is null,
            SoundPlayer.FromFile(@"C:\a\b.wav"));

        check("[声音] 描述文字：不播放 / 系统音名 / 自定义文件名",
            SoundPlayer.Describe(SoundPlayer.NoneId) == "不播放"
            && SoundPlayer.Describe("system:SystemAsterisk").Contains("提示")
            && SoundPlayer.Describe(@"file:C:\a\不存在的.wav").Contains("不存在"),
            SoundPlayer.Describe(@"file:C:\a\不存在的.wav"));

        check("[声音] 不存在的文件判为不可播放（界面能标出来）",
            !SoundPlayer.IsPlayable(@"file:C:\绝对不存在的目录\x.wav")
            && SoundPlayer.IsPlayable("system:SystemAsterisk"),
            "system 音可播 / 假文件不可播");

        check("[声音] 播放不存在的文件安全返回 false（不抛异常、不偷偷播系统默认音）",
            !SoundPlayer.TryPlay(@"file:C:\绝对不存在的目录\x.wav"),
            "false");
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new StopwatchConfig();
        check("[秒表] 配置默认值：三条快捷键 / 三个声音 / 显示开关 / 通知 / 分步等待",
            config.Start == "Ctrl+Alt+F9" && config.Pause == "F10" && config.Stop == "F11"
            && config.StartEnabled && config.PauseEnabled && config.StopEnabled && config.ShortcutsEnabled
            && !config.Muted && config.ShowMilliseconds && config.ShowLaps
            && config.SequenceTimeoutMs == 2000
            && config.Notification is { Enabled: true, Mode: NotificationMode.InApp },
            $"{config.Start} / {config.Pause} / {config.Stop}");

        check("[秒表] 三个声音默认都设了（不是静音）",
            config.StartSound.Length > 0 && config.PauseSound.Length > 0 && config.StopSound.Length > 0,
            $"{config.StartSound} / {config.PauseSound} / {config.StopSound}");

        config.SequenceTimeoutMs = 99999;
        var clampedHigh = config.SequenceTimeoutMs;
        config.SequenceTimeoutMs = 1;
        check("[秒表] 分步等待时长夹在 500~5000（配置层也兜一次底）",
            clampedHigh == 5000 && config.SequenceTimeoutMs == 500,
            $"{clampedHigh} / {config.SequenceTimeoutMs}");

        // 配置往返（走临时数据根，不碰仓库 Config）
        var store = ToolConfigStore.For(StopwatchConfig.ToolId, "selfcheck.json");
        try
        {
            var saved = store.Load<StopwatchConfig>();
            saved.Start = "Ctrl+Alt+S";
            saved.StartSecond = "Ctrl+Alt+E";
            saved.PauseEnabled = false;
            saved.Muted = true;
            saved.Notification.Mode = NotificationMode.Windows;
            saved.Notification.Corner = NotificationCorner.TopLeft;
            saved.Notification.DurationMs = 5000;

            var reloaded = ToolConfigStore.For(StopwatchConfig.ToolId, "selfcheck.json").Load<StopwatchConfig>();
            check("[秒表] 配置往返：快捷键（含第二步）/ 启用位 / 静音都存得住",
                reloaded.Start == "Ctrl+Alt+S" && reloaded.StartSecond == "Ctrl+Alt+E"
                && !reloaded.PauseEnabled && reloaded.Muted,
                $"{reloaded.Start} → {reloaded.StartSecond} / 暂停启用 {reloaded.PauseEnabled}");

            check("[秒表] 配置往返：通知设置（通道 / 方位 / 时长）是这个工具自己的一份",
                reloaded.Notification.Mode == NotificationMode.Windows
                && reloaded.Notification.Corner == NotificationCorner.TopLeft
                && reloaded.Notification.DurationMs == 5000,
                $"{reloaded.Notification.Mode} / {reloaded.Notification.Corner} / {reloaded.Notification.DurationMs}");

            var json = saved.ToJson();
            check("[秒表] 配置里没有把运行时状态写进去（没有 elapsedText / stateText 之类）",
                !json.Contains("elapsedText") && !json.Contains("stateText") && !json.Contains("hasLaps"),
                "只存持久化字段");
        }
        finally
        {
            store.DeleteFile();
        }
    }
}
