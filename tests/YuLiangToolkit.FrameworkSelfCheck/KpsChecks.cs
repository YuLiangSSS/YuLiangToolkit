using System.Text.Json;
using System.Windows.Media;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Kps;
using YuLiangToolkit.Tools.Kps.Models;
using YuLiangToolkit.Tools.Kps.Presets;
using YuLiangToolkit.Tools.Kps.Statistics;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「KPS 按键统计」纯逻辑自检：统计引擎口径 / Capture 快照 / 线程安全 / 内置预设 /
/// 配置 JSON 序列化与持久化 / 颜色与计数器小工具 / 按键格子 ApplyStyle。
///
/// <para>全部不碰真实键盘、不弹窗：统计引擎用<b>注入的假时钟</b>，断言窗口边界、
/// 50ms 采样、100ms 折线图都能精确到 1ms。</para>
/// </summary>
public static class KpsChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        // ===================== 1. 常量与初始状态 =====================
        C("统计口径常量：窗口 1000ms / 折线采样 100ms / 默认 300 点",
            KpsStatistics.KpsWindowMs == 1000
            && KpsStatistics.GraphSampleMs == 100
            && KpsStatistics.DefaultHistoryCapacity == 300);

        long now = 10_000;
        var stats = new KpsStatistics(clock: () => now);
        C("初始状态全 0：当前 / 峰值 / 平均 / 总按下 / 总抬起 / 最近按下 / 统计时长",
            stats.CurrentKps == 0 && stats.PeakKps == 0 && stats.AverageKps == 0
            && stats.TotalPresses == 0 && stats.TotalReleases == 0
            && stats.LastPressAtMs == 0 && stats.ElapsedSeconds == 0);
        C("初始折线图容量 = 默认 300 点（图表 30 秒 × 10 点/秒）",
            stats.HistoryCapacity == KpsStatistics.DefaultHistoryCapacity);

        // ===================== 2. 当前 KPS：1 秒滑动窗口 =====================
        now = 10_000;
        var window = new KpsStatistics(clock: () => now);
        window.RegisterPress(0x44); // t = 10000
        now = 10_999;
        C("窗口滑动：999ms 前按下的键仍在窗口内", window.CurrentKps == 1);
        now = 11_000;
        C("窗口滑动：正好 1000ms 前的按下不算（窗口边界是开区间）", window.CurrentKps == 0);

        // 再按 3 次（11000 / 11010 / 11020），然后看窗口怎么依次滑出
        window.RegisterPress(0x44);
        now = 11_010;
        window.RegisterPress(0x44);
        now = 11_020;
        window.RegisterPress(0x44);
        now = 11_100;
        C("窗口滑动：1 秒内的按下按次数累计", window.CurrentKps == 3, $"current={window.CurrentKps}");
        now = 12_000;
        C("窗口滑动：最早一次到 1000ms 被移出窗口", window.CurrentKps == 2);
        now = 12_019;
        C("窗口滑动：窗口内还剩最后 1 次", window.CurrentKps == 1);
        now = 12_020;
        C("窗口滑动：最后一键到 1000ms 后窗口清空", window.CurrentKps == 0);

        // ===================== 3. 峰值：只在 Tick 采样时更新 =====================
        now = 0;
        var peak = new KpsStatistics(clock: () => now);
        for (var i = 0; i < 4; i++)
        {
            now = i * 10;
            peak.RegisterPress(0x44);
        }
        C("峰值：按键事件本身不更新峰值（只在 Tick 采样时更新）",
            peak.PeakKps == 0 && peak.CurrentKps == 4);
        now = 40;
        peak.Tick();
        C("峰值：Tick 采样到当前 KPS", peak.PeakKps == 4);
        now = 50;
        peak.RegisterPress(0x44);
        C("峰值：新按键要等下一次 Tick 采样才可能刷新峰值",
            peak.PeakKps == 4 && peak.CurrentKps == 5);
        now = 60;
        peak.Tick();
        C("峰值：Tick 后取历史最大（5）", peak.PeakKps == 5);
        now = 5000;
        peak.Tick();
        C("峰值：窗口滑空后峰值仍保持历史最大", peak.PeakKps == 5 && peak.CurrentKps == 0);

        // ===================== 4. 平均 KPS：只算 KPS>0 的采样点 =====================
        now = 0;
        var average = new KpsStatistics(clock: () => now);
        for (var i = 0; i < 5; i++)
        {
            now = i * 10;
            average.RegisterPress(0x44);
        }
        now = 100;
        average.Tick();   // 采样 5
        now = 2000;
        average.Tick();   // 采样 0（不计入平均）
        C("平均：KPS=0 的空闲采样点完全不参与计算（不被空闲时间稀释）", average.AverageKps == 5,
            $"average={average.AverageKps}");

        for (var i = 0; i < 6; i++)
        {
            now = 2000 + i * 10;
            average.RegisterPress(0x46);
        }
        now = 2100;
        average.Tick();   // 采样 6
        C("平均：只对 KPS>0 的采样点取算术平均（(5+6)/2 = 5.5 → 6）",
            average.AverageKps == 6, $"average={average.AverageKps}");

        now = 0;
        var averageHalf = new KpsStatistics(clock: () => now);
        for (var i = 0; i < 2; i++)
        {
            now = i * 10;
            averageHalf.RegisterPress(0x44);
        }
        now = 100;
        averageHalf.Tick();   // 采样 2
        now = 2000;
        averageHalf.Tick();   // 采样 0（不计入）
        for (var i = 0; i < 3; i++)
        {
            now = 2000 + i * 10;
            averageHalf.RegisterPress(0x46);
        }
        now = 2100;
        averageHalf.Tick();   // 采样 3
        C("平均：x.5 按 AwayFromZero 进位（(2+3)/2 = 2.5 → 3，不是银行家舍入的 2）",
            averageHalf.AverageKps == 3, $"average={averageHalf.AverageKps}");

        // ===================== 5. 折线图：每 100ms 一点 + 容量裁剪 =====================
        now = 1000;
        var graph = new KpsStatistics(clock: () => now);
        graph.RegisterPress(0x44);   // t = 1000
        now = 1050;
        graph.Tick();
        C("折线图：不满 100ms 不追加采样点", graph.Capture().GraphValues.Count == 0);
        now = 1100;
        graph.Tick();
        C("折线图：正好 100ms 追加一个采样点", graph.Capture().GraphValues.Count == 1);
        now = 1150;
        graph.Tick();
        C("折线图：50ms 的 Tick 不会重复追加", graph.Capture().GraphValues.Count == 1);
        now = 1200;
        graph.Tick();
        var graphSnapshot = graph.Capture();
        C("折线图：点值 = 采样时的当前 KPS",
            graphSnapshot.GraphValues.Count == 2
            && graphSnapshot.GraphValues[0] == 1 && graphSnapshot.GraphValues[1] == 1,
            $"values=[{string.Join(",", graphSnapshot.GraphValues)}]");

        now = 0;
        var trimmed = new KpsStatistics(historyCapacity: 10, clock: () => now);
        for (var i = 1; i <= 14; i++)
        {
            now = i * 100;
            trimmed.Tick();   // 14 个点，容量只有 10 → 裁掉最旧的 4 个
        }
        C("折线图：容量 = HistoryCapacity，超出后裁掉最旧的点",
            trimmed.Capture().GraphValues.Count == 10,
            $"count={trimmed.Capture().GraphValues.Count}");
        for (var i = 0; i < 3; i++) trimmed.RegisterPress(0x44);
        now = 1500;
        trimmed.Tick();
        var trimmedSnapshot = trimmed.Capture();
        C("折线图：裁剪保留最近的采样（最后一点 3、最旧一点 0）",
            trimmedSnapshot.GraphValues.Count == 10
            && trimmedSnapshot.GraphValues[^1] == 3
            && trimmedSnapshot.GraphValues[0] == 0,
            $"values=[{string.Join(",", trimmedSnapshot.GraphValues)}]");

        now = 0;
        var resized = new KpsStatistics(clock: () => now);
        for (var i = 1; i <= 12; i++)
        {
            now = i * 100;
            resized.Tick();
        }
        C("折线图：默认容量 300 时 12 个点不裁剪", resized.Capture().GraphValues.Count == 12);
        resized.HistoryCapacity = 10;
        C("折线图：运行时调小 HistoryCapacity 会立刻丢掉最旧的点",
            resized.HistoryCapacity == 10 && resized.Capture().GraphValues.Count == 10);

        // ===================== 6. Capture：快照副本 / 动态纵轴 =====================
        now = 500;
        var capture = new KpsStatistics(historyCapacity: 64, clock: () => now);
        var emptySnapshot = capture.Capture();
        C("Capture：空数据时纵轴最小为 1（不会除以 0）",
            emptySnapshot.GraphValues.Count == 0 && emptySnapshot.GraphMax == 1);
        C("KpsSnapshot.Empty：空快照纵轴也是 1",
            KpsSnapshot.Empty.GraphMax == 1 && KpsSnapshot.Empty.GraphValues.Count == 0
            && KpsSnapshot.Empty.TotalPresses == 0);

        now = 600;
        for (var i = 0; i < 5; i++)
        {
            now = 600 + i * 10;
            capture.RegisterPress(0x44);
        }
        C("Capture：纵轴跟随当前值（5）", capture.Capture().GraphMax == 5);
        for (var i = 0; i < 5; i++) capture.RegisterPress(0x44);   // 同一时刻再按 5 次
        C("Capture：纵轴动态跟随当前值（10），没有写死的上限", capture.Capture().GraphMax == 10);
        now = 700;
        capture.Tick();    // 采样 10，刷新峰值
        now = 5000;
        capture.Tick();    // 窗口滑空，但峰值还在
        var peakSnapshot = capture.Capture();
        C("Capture：纵轴也考虑历史峰值（窗口滑空后仍 >= 峰值）",
            peakSnapshot.CurrentKps == 0 && peakSnapshot.PeakKps == 10
            && peakSnapshot.GraphMax >= peakSnapshot.PeakKps,
            $"current={peakSnapshot.CurrentKps} peak={peakSnapshot.PeakKps} max={peakSnapshot.GraphMax}");
        C("Capture：纵轴是向上取整后的整数",
            peakSnapshot.GraphMax == Math.Floor(peakSnapshot.GraphMax));
        C("Capture：快照字段与各属性当前值一致",
            peakSnapshot.TotalPresses == capture.TotalPresses
            && peakSnapshot.PeakKps == capture.PeakKps
            && peakSnapshot.AverageKps == capture.AverageKps);

        now = 3000;
        var copySource = new KpsStatistics(clock: () => now);
        copySource.RegisterPress(0x44);   // t = 3000
        now = 3100;
        copySource.Tick();                // 折线图 1 个点（值 1）
        var firstSnapshot = copySource.Capture();
        var secondSnapshot = copySource.Capture();
        C("Capture：每次都是新数组（多次取快照互不共享）",
            !ReferenceEquals(firstSnapshot.GraphValues, secondSnapshot.GraphValues)
            && firstSnapshot.GraphValues.Count == 1);
        if (firstSnapshot.GraphValues is double[] mutableCopy) mutableCopy[0] = 999;
        var afterMutate = copySource.Capture();
        C("Capture：改快照里的数组不影响引擎",
            afterMutate.GraphValues.Count == 1 && afterMutate.GraphValues[0] == 1,
            $"values=[{string.Join(",", afterMutate.GraphValues)}]");

        // ===================== 7. Reset / ElapsedSeconds =====================
        now = 500;
        var elapsed = new KpsStatistics(clock: () => now);
        C("ElapsedSeconds：刚创建时为 0（假时钟从大数字开始也不出现负时长）", elapsed.ElapsedSeconds == 0);
        now = 2000;
        C("ElapsedSeconds：按注入时钟计算（1.5 秒）",
            Math.Abs(elapsed.ElapsedSeconds - 1.5) < 1e-9, $"elapsed={elapsed.ElapsedSeconds}");
        now = 100;   // 时钟回拨（正常时钟不会，但显示不能变成负数）
        C("ElapsedSeconds：时钟回拨也不会返回负数", elapsed.ElapsedSeconds == 0);

        now = 0;
        var reset = new KpsStatistics(clock: () => now);
        now = 100;
        reset.RegisterPress(0x44);
        reset.RegisterPress(0x44);
        now = 150;
        reset.RegisterRelease(0x44);
        now = 200;
        reset.Tick();
        var beforeReset = reset.Capture();
        C("Reset 前：有计数 / 峰值 / 折线点",
            beforeReset.TotalPresses == 2 && beforeReset.TotalReleases == 1
            && beforeReset.PeakKps == 2 && beforeReset.GraphValues.Count == 1,
            $"presses={beforeReset.TotalPresses} peak={beforeReset.PeakKps} points={beforeReset.GraphValues.Count}");
        now = 500;
        reset.Reset();
        var afterReset = reset.Capture();
        C("Reset：计数 / 峰值 / 平均 / 折线 / 时长全部归零",
            afterReset.TotalPresses == 0 && afterReset.TotalReleases == 0
            && afterReset.PeakKps == 0 && afterReset.AverageKps == 0
            && afterReset.CurrentKps == 0 && afterReset.GraphValues.Count == 0
            && afterReset.ElapsedSeconds == 0 && afterReset.GraphMax == 1);
        C("Reset：每键计数 / 最近按下时间也清零",
            reset.GetKeyPressCount(0x44) == 0 && reset.GetKeyCounts().Count == 0
            && reset.LastPressAtMs == 0);
        now = 600;
        reset.RegisterPress(0x44);
        C("Reset：之后重新从 0 开始统计",
            reset.TotalPresses == 1 && reset.CurrentKps == 1 && reset.GetKeyPressCount(0x44) == 1);

        // ===================== 8. 每键计数 / 抬起 / 最近按下时间 =====================
        now = 0;
        var keys = new KpsStatistics(clock: () => now);
        keys.RegisterPress(0x44);
        now = 5;
        keys.RegisterPress(0x44);
        now = 10;
        keys.RegisterPress(0x46);
        C("每键计数：按虚拟键码分别累计",
            keys.GetKeyPressCount(0x44) == 2 && keys.GetKeyPressCount(0x46) == 1
            && keys.GetKeyPressCount(0x4B) == 0);
        C("GetKeyCounts：返回所有键的快照",
            keys.GetKeyCounts().Count == 2
            && keys.GetKeyCounts()[0x44] == 2 && keys.GetKeyCounts()[0x46] == 1);
        var countsCopy = keys.GetKeyCounts();
        if (countsCopy is Dictionary<int, long> mutableCounts) mutableCounts[0x44] = 999;
        C("GetKeyCounts：返回的是副本（改它不影响引擎）", keys.GetKeyPressCount(0x44) == 2);
        C("总按下次数 = 各键计数之和", keys.TotalPresses == 3);
        C("LastPressAtMs：最近一次按下的时间", keys.LastPressAtMs == 10);
        keys.RegisterRelease(0x44);
        keys.RegisterRelease(0x46);
        keys.RegisterRelease(0x44);
        C("TotalReleases：只记录抬起次数", keys.TotalReleases == 3 && keys.TotalPresses == 3);
        C("抬起不影响当前 KPS", keys.CurrentKps == 3);

        // ===================== 9. 线程安全冒烟（并发写 + 并发读） =====================
        var concurrent = new KpsStatistics(clock: () => 12_345);
        const int writerCount = 4;
        const int pressesPerWriter = 500;
        var readerOk = true;
        using (var stopReader = new CancellationTokenSource())
        {
            var reader = Task.Run(() =>
            {
                try
                {
                    while (!stopReader.IsCancellationRequested)
                    {
                        _ = concurrent.Capture();
                        _ = concurrent.CurrentKps;
                        Thread.Sleep(1);
                    }
                }
                catch
                {
                    readerOk = false;
                }
            });
            var writers = Enumerable.Range(0, writerCount)
                .Select(writer => Task.Run(() =>
                {
                    for (var i = 0; i < pressesPerWriter; i++) concurrent.RegisterPress(0x41 + writer);
                }))
                .ToArray();
            Task.WaitAll(writers);
            stopReader.Cancel();
            var readerFinished = reader.Wait(5000);
            C("线程安全：多线程并发 RegisterPress 后总按下次数准确",
                concurrent.TotalPresses == writerCount * pressesPerWriter,
                $"presses={concurrent.TotalPresses}");
            C("线程安全：并发写入时每键计数准确",
                concurrent.GetKeyPressCount(0x41) == pressesPerWriter
                && concurrent.GetKeyPressCount(0x44) == pressesPerWriter,
                $"0x41={concurrent.GetKeyPressCount(0x41)} 0x44={concurrent.GetKeyPressCount(0x44)}");
            C("线程安全：并发读取 Capture / CurrentKps 不抛异常", readerFinished && readerOk);
        }

        // ===================== 10. 内置预设 =====================
        var builtIn = KpsPresets.BuiltIn;
        C("预设：内置预设列表非空", builtIn.Count >= 5, $"共 {builtIn.Count} 套");
        C("预设：Id 唯一（大小写不敏感）",
            builtIn.Select(p => p.Id.ToUpperInvariant()).Distinct().Count() == builtIn.Count);
        C("预设：Id / Name 都不为空", builtIn.All(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name)));
        C("预设：全部标记为内置（界面据此禁用删除 / 改名）", builtIn.All(p => p.BuiltIn));
        C("预设：默认 Id = key-4 且能在内置预设里找到",
            KpsPresets.DefaultId == "key-4" && KpsPresets.Find(KpsPresets.DefaultId) is not null);
        C("预设：Default 就是 DefaultId 指向的那套", ReferenceEquals(KpsPresets.Default, KpsPresets.Find(KpsPresets.DefaultId)));

        foreach (var requiredId in new[] { "key-4", "key-5", "key-6", "key-8", "arrows" })
            C($"预设：内置包含 {requiredId}", KpsPresets.Find(requiredId) is not null);

        C("预设：每套至少一行、每行至少一个键",
            builtIn.All(p => p.Rows.Count > 0 && p.Rows.All(row => row.Keys.Count > 0)));
        C("预设：重点键的 VK 都在 1..0xFF、Label 非空、默认启用、宽度 >= 1",
            builtIn.All(p => p.Rows.SelectMany(row => row.Keys).All(key =>
                key.VirtualKey is >= 1 and <= 0xFF
                && !string.IsNullOrWhiteSpace(key.Label)
                && key.Enabled
                && key.WidthUnits >= 1)));

        var key4 = KpsPresets.Find("key-4");
        var key5 = KpsPresets.Find("key-5");
        var key6 = KpsPresets.Find("key-6");
        var key8 = KpsPresets.Find("key-8");
        var arrows = KpsPresets.Find("arrows");
        C("预设：4K 正好 4 键（D F J K）",
            key4 is not null && key4.Rows.Sum(row => row.Keys.Count) == 4
            && key4.Rows.SelectMany(row => row.Keys).Select(key => key.VirtualKey)
                .SequenceEqual(new[] { 0x44, 0x46, 0x4A, 0x4B }));
        C("预设：5K 正好 5 键，中键是宽 2 格的空格",
            key5 is not null && key5.Rows.Sum(row => row.Keys.Count) == 5
            && key5.Rows.SelectMany(row => row.Keys).Any(key => key.VirtualKey == 0x20 && key.WidthUnits == 2));
        C("预设：6K 正好 6 键（S D F J K L）",
            key6 is not null && key6.Rows.Sum(row => row.Keys.Count) == 6
            && key6.Rows.SelectMany(row => row.Keys).Select(key => key.VirtualKey)
                .SequenceEqual(new[] { 0x53, 0x44, 0x46, 0x4A, 0x4B, 0x4C }));
        C("预设：8K 正好 8 键（含分号 0xBA）",
            key8 is not null && key8.Rows.Sum(row => row.Keys.Count) == 8
            && key8.Rows.SelectMany(row => row.Keys).Any(key => key.VirtualKey == 0xBA));
        C("预设：方向键正好 4 个，上键单独一行（↑ / ← ↓ →）",
            arrows is not null && arrows.Rows.Count == 2
            && arrows.Rows.Sum(row => row.Keys.Count) == 4
            && arrows.Rows[0].Keys.Count == 1 && arrows.Rows[0].Keys[0].VirtualKey == 0x26
            && arrows.Rows[1].Keys.Select(key => key.VirtualKey).SequenceEqual(new[] { 0x25, 0x28, 0x27 }));

        C("预设：Find 大小写不敏感", KpsPresets.Find("KEY-4") is not null && KpsPresets.Find("Key-8") is not null);
        C("预设：Find 空 / 未知 Id 返回 null",
            KpsPresets.Find(null) is null && KpsPresets.Find("") is null
            && KpsPresets.Find("   ") is null && KpsPresets.Find("不存在的预设") is null);

        var clonedRows = KpsPresets.CloneRows(KpsPresets.Default.Rows);
        C("预设：CloneRows 行数 / 键数一致",
            clonedRows.Count == KpsPresets.Default.Rows.Count
            && clonedRows.Sum(row => row.Keys.Count) == KpsPresets.Default.Rows.Sum(row => row.Keys.Count));
        C("预设：CloneRows 是深拷贝（行对象 / 键对象都是新的）",
            !ReferenceEquals(clonedRows[0], KpsPresets.Default.Rows[0])
            && !ReferenceEquals(clonedRows[0].Keys[0], KpsPresets.Default.Rows[0].Keys[0]));
        clonedRows[0].Keys[0].VirtualKey = 0x99;
        clonedRows[0].Keys[0].Label = "改过的";
        C("预设：改深拷贝出来的副本不影响原预设",
            KpsPresets.Default.Rows[0].Keys[0].VirtualKey == 0x44
            && KpsPresets.Default.Rows[0].Keys[0].Label == "D");

        var helperKey = KpsPresets.Key(0x20, "空格", 2);
        C("预设：Key 便捷方法会填齐 VK / 文字 / 宽度",
            helperKey.VirtualKey == 0x20 && helperKey.Label == "空格"
            && helperKey.WidthUnits == 2 && helperKey.Enabled);

        // ===================== 11. 配置 JSON 序列化 + 持久化 =====================
        var defaults = new KpsConfig();
        C("配置默认值：当前预设 key-4、布局来自默认预设、热键同 DmNote",
            defaults.ActivePresetId == KpsPresets.DefaultId
            && defaults.KeyRows.Count == KpsPresets.Default.Rows.Count
            && defaults.KeyRows.Sum(row => row.Keys.Count) == 4
            && defaults.HotkeyToggleOverlay == "Ctrl+Shift+O"
            && defaults.HotkeyToggleLock == "Ctrl+Shift+L"
            && defaults.HotkeyResetStats == "Ctrl+Shift+R"
            && defaults.HotkeyToggleListening == "Ctrl+Shift+P"
            && defaults.GraphSeconds == 30
            && defaults.CustomPresets.Count == 0 && defaults.SavedKeyCounters.Count == 0);

        var config = new KpsConfig
        {
            ActivePresetId = "my-preset",
            GraphSeconds = 45,
            PersistKeyCounters = true,
            HotkeyToggleOverlay = "Alt+Shift+K",
            KeyRows = KpsPresets.CloneRows((key5 ?? KpsPresets.Default).Rows),
        };
        config.SavedKeyCounters["68"] = 1234;   // "68" = D
        config.SavedKeyCounters["70"] = 56;     // "70" = F
        config.CustomPresets.Add(new KpsPreset
        {
            Id = "my-preset",
            Name = "我的 5K",
            BuiltIn = false,
            Rows = new List<KpsKeyRow>
            {
                new()
                {
                    Keys =
                    {
                        KpsPresets.Key(0x44, "D"),
                        KpsPresets.Key(0x46, "F"),
                        KpsPresets.Key(0x20, "空格", 2),
                    },
                },
            },
        });

        var json = JsonFileStore.Serialize(config);
        var jsonSummary = json.Replace("\r", " ").Replace("\n", " ").Trim();
        if (jsonSummary.Length > 160) jsonSummary = jsonSummary[..160];
        var reloaded = JsonSerializer.Deserialize<KpsConfig>(json, JsonFileStore.Options);
        C("配置序列化：JSON → KpsConfig 能读回", reloaded is not null, jsonSummary);
        if (reloaded is not null)
        {
            C("配置序列化：KeyRows 往返（5K 布局，含宽 2 的空格）",
                reloaded.KeyRows.Count == 1 && reloaded.KeyRows[0].Keys.Count == 5
                && reloaded.KeyRows[0].Keys.Any(key => key.VirtualKey == 0x20 && key.WidthUnits == 2));
            C("配置序列化：CustomPresets 往返（Id / Name / BuiltIn / Rows）",
                reloaded.CustomPresets.Count == 1
                && reloaded.CustomPresets[0].Id == "my-preset"
                && reloaded.CustomPresets[0].Name == "我的 5K"
                && !reloaded.CustomPresets[0].BuiltIn
                && reloaded.CustomPresets[0].Rows.Count == 1
                && reloaded.CustomPresets[0].Rows[0].Keys.Count == 3);
            C("配置序列化：SavedKeyCounters 往返（键 = 虚拟键码字符串）",
                reloaded.SavedKeyCounters.Count == 2
                && reloaded.SavedKeyCounters.TryGetValue("68", out var dCount) && dCount == 1234
                && reloaded.SavedKeyCounters.TryGetValue("70", out var fCount) && fCount == 56,
                string.Join(",", reloaded.SavedKeyCounters.Select(pair => $"{pair.Key}={pair.Value}")));
            C("配置序列化：热键 / 图表时长 / 计数器开关往返",
                reloaded.HotkeyToggleOverlay == "Alt+Shift+K"
                && reloaded.HotkeyToggleLock == "Ctrl+Shift+L"
                && reloaded.GraphSeconds == 45
                && reloaded.PersistKeyCounters
                && reloaded.ActivePresetId == "my-preset");
            C("配置计算属性：按 ActivePresetId 解析出自定义预设名", reloaded.ActivePresetName == "我的 5K");
            C("配置序列化：带 setter 的计算属性有 [JsonIgnore]，不写进 JSON",
                !json.Contains("activePresetName", StringComparison.Ordinal)
                && !json.Contains("hasCustomPresets", StringComparison.Ordinal));
        }

        var defaultRoundTrip = JsonSerializer.Deserialize<KpsConfig>(
            JsonFileStore.Serialize(new KpsConfig()), JsonFileStore.Options);
        C("配置序列化：默认热键值原样往返",
            defaultRoundTrip is not null
            && defaultRoundTrip.HotkeyToggleOverlay == "Ctrl+Shift+O"
            && defaultRoundTrip.HotkeyToggleLock == "Ctrl+Shift+L"
            && defaultRoundTrip.HotkeyResetStats == "Ctrl+Shift+R"
            && defaultRoundTrip.HotkeyToggleListening == "Ctrl+Shift+P");

        // 配置持久化：在自检临时数据根（YLT_HOME）里走一遍 ToolConfigStore 的 Load → SaveNow → 读回
        var store = ToolConfigStore.For("kps-selftest-tmp");
        var persisted = store.Load<KpsConfig>();
        C("配置持久化：落在 Config\\kps-selftest-tmp\\config.json",
            Path.GetFileName(Path.GetDirectoryName(persisted.FilePath)!) == "kps-selftest-tmp"
            && Path.GetFileName(persisted.FilePath) == "config.json",
            persisted.FilePath);
        persisted.GraphSeconds = 50;
        persisted.HotkeyResetStats = "Ctrl+Alt+R";
        persisted.SavedKeyCounters["65"] = 999;   // "65" = A
        persisted.SaveNow();
        var reread = JsonFileStore.Read<KpsConfig>(persisted.FilePath);
        C("配置持久化：SaveNow 后能从磁盘读回（图表时长 / 热键 / 计数器）",
            reread is not null
            && reread.GraphSeconds == 50
            && reread.HotkeyResetStats == "Ctrl+Alt+R"
            && reread.SavedKeyCounters.TryGetValue("65", out var savedCount) && savedCount == 999,
            persisted.FilePath);
        store.StopWatching();

        // ===================== 12. 颜色工具 =====================
        var parsedColor = KpsColor.Parse("#FF112233", Colors.Black);
        C("颜色：解析 #AARRGGBB",
            parsedColor == Color.FromArgb(0xFF, 0x11, 0x22, 0x33), KpsColor.ToHex(parsedColor));
        C("颜色：省略 # / 大小写都能解析",
            KpsColor.Parse("ff112233", Colors.Black) == parsedColor
            && KpsColor.Parse("#ff112233", Colors.Black) == parsedColor);
        C("颜色：非法 / 空值回退默认色",
            KpsColor.Parse("不是颜色", Colors.Red) == Colors.Red
            && KpsColor.Parse(null, Colors.Red) == Colors.Red
            && KpsColor.Parse("   ", Colors.Red) == Colors.Red);
        C("颜色：IsValid 校验合法 / 非法 / 空值",
            KpsColor.IsValid("#80FF0000") && KpsColor.IsValid("4C8DFF")
            && !KpsColor.IsValid("坏值") && !KpsColor.IsValid("") && !KpsColor.IsValid(null));
        C("颜色：ToHex 输出 #AARRGGBB",
            KpsColor.ToHex(Color.FromArgb(0x80, 0xFF, 0x00, 0x10)) == "#80FF0010");
        C("颜色：ToRgbHex 只取 RGB",
            KpsColor.ToRgbHex(Color.FromArgb(0x80, 0x4C, 0x8D, 0xFF)) == "#4C8DFF");
        C("颜色：WithAlpha 乘不透明度并夹紧到 0..1",
            KpsColor.WithAlpha(Color.FromArgb(0xC8, 0x10, 0x20, 0x30), 0.5) == Color.FromArgb(100, 0x10, 0x20, 0x30)
            && KpsColor.WithAlpha(Color.FromArgb(0xC8, 0, 0, 0), 2.0) == Color.FromArgb(200, 0, 0, 0)
            && KpsColor.WithAlpha(Color.FromArgb(0xC8, 0, 0, 0), -1) == Color.FromArgb(0, 0, 0, 0));
        var parsedBrush = KpsColor.ParseBrush("#FF123456", Colors.Black);
        C("颜色：ParseBrush 返回冻结画刷（跨线程安全）",
            parsedBrush.IsFrozen && parsedBrush.Color == Color.FromArgb(0xFF, 0x12, 0x34, 0x56));

        // ===================== 13. 计数器格式 =====================
        C("计数器格式：0 / 999 原样显示",
            KpsCountFormat.Format(0) == "0" && KpsCountFormat.Format(999) == "999");
        C("计数器格式：1234 → 1,234（千位分组）",
            KpsCountFormat.Format(1234) == "1,234", KpsCountFormat.Format(1234));
        C("计数器格式：9999 仍用千位分组",
            KpsCountFormat.Format(9999) == "9,999", KpsCountFormat.Format(9999));
        C("计数器格式：10000 → 1万",
            KpsCountFormat.Format(10000) == "1万", KpsCountFormat.Format(10000));
        C("计数器格式：12345 → 1.2万",
            KpsCountFormat.Format(12345) == "1.2万", KpsCountFormat.Format(12345));
        C("计数器格式：123456789 → 1.2亿",
            KpsCountFormat.Format(123_456_789) == "1.2亿", KpsCountFormat.Format(123456789));
        C("计数器格式：负数按 0 显示", KpsCountFormat.Format(-5) == "0");

        // ===================== 14. 按键格子 ApplyStyle =====================
        var cellKey = new KpsKey { VirtualKey = 0x44, Label = "D" };
        var cellRow = new KpsKeyRow { Keys = { cellKey } };
        var cell = new KpsKeyCell(cellKey, cellRow, rowIndex: 0, columnIndex: 0);
        var cellConfig = new KpsConfig();
        cell.ApplyStyle(cellConfig);
        C("按键格子：Label 用配置里的文字", cell.Label == "D");
        C("按键格子：未按下 / 按下 / 文字 / 计数颜色按配置解析",
            cell.IdleBrush is SolidColorBrush idle
            && idle.Color == KpsColor.Parse(cellConfig.KeyIdleColor, Colors.Transparent)
            && cell.PressedBrush is SolidColorBrush pressed
            && pressed.Color == KpsColor.Parse(cellConfig.KeyPressedColor, Colors.Transparent)
            && cell.ForegroundBrush is SolidColorBrush foreground
            && foreground.Color == KpsColor.Parse(cellConfig.KeyTextColor, Colors.Transparent));

        cellKey.Label = "";
        cellKey.VirtualKey = 0x20;
        cell.ApplyStyle(cellConfig);
        C("按键格子：Label 为空时退回键名（空格）", cell.Label == "空格");

        cellKey.ColorHex = "#FFFF0000";
        cell.ApplyStyle(cellConfig);
        C("按键格子：单键颜色覆盖生效",
            cell.PressedBrush is SolidColorBrush overridden && overridden.Color == Color.FromRgb(0xFF, 0, 0));
        cellKey.ColorHex = "";

        cell.Count = 1234;
        cellConfig.ShowKeyCounters = true;
        cell.ApplyStyle(cellConfig);
        C("按键格子：CountText 用计数器格式（1234 → 1,234）",
            cell.CountText == "1,234" && cell.HasCount);
        C("按键格子：计数 > 0 且总开关打开时显示计数器", cell.ShowCounter);
        cellConfig.ShowKeyCounters = false;
        cell.ApplyStyle(cellConfig);
        C("按键格子：总开关关闭时不显示计数器", !cell.ShowCounter);
        cell.Count = 0;
        C("按键格子：0 次时计数文字为空", cell.CountText == "" && !cell.HasCount);

        cellKey.WidthUnits = 99;
        var wideUnits = cell.WidthUnits;
        cellKey.WidthUnits = 0;
        C("按键格子：宽度倍数夹紧到 1..8", wideUnits == 8 && cell.WidthUnits == 1);

        var snapshotKey = cell.Snapshot();
        snapshotKey.Label = "改过的";
        C("按键格子：Snapshot 是模型副本（改它不影响原模型）",
            !ReferenceEquals(cell.Model, snapshotKey)
            && cell.Model.Label == "" && snapshotKey.Label == "改过的");
    }
}
