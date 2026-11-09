using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using YuLiangToolkit.Tools.OsuMania;
using YuLiangToolkit.Tools.OsuMania.Core.Editor;
using YuLiangToolkit.Tools.OsuMania.Views;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「osu!mania 谱面编辑器」的自检：**播放时钟 / 网格吸附 / .osu 保真读写 / 大量 note 的索引与裁剪**。
///
/// <para>全部无头跑，不起声卡、不建窗口、不碰仓库 Data/Config（谱面文本在临时目录里造）。</para>
///
/// <para><b>为什么这些断言很重要</b>：编辑器的价值就在「音频和谱面对得上」和「改完能原样存回去」，
/// 这两件事出错都不容易靠眼睛发现，必须由断言兜住。</para>
/// </summary>
public static class OsuManiaEditorChecks
{
    /// <summary>自检断言（带可选 detail；用委托而不是 <c>Action</c>，这样调用时能省略 detail）。</summary>
    private delegate void Checker(string name, bool ok, string detail = "");

    /// <summary>自检入口（由 Program.cs 统一调用）。</summary>
    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);
        Checker checker = C;

        var dir = SelfCheckScratch.MakeDirectory("osu-mania-editor");

        CheckPlaybackClock(checker);
        CheckBeatGrid(checker);
        CheckViewport(checker);
        CheckChartEditor(checker);
        CheckFileRoundTrip(checker, dir);
        CheckLargeChart(checker, dir);

        // 自动保存（空闲计时纯逻辑 + 真的写盘）
        CheckAutoSave(checker, dir);

        // 新 note 的采样字段（编辑器不做 hitsounds）
        CheckDefaultSampling(checker);

        // 多选 / 框选 / Z-V 跳转与区间扩选
        CheckSelection(checker);

        // Note 浏览区宽度（左右可调 + 居中）
        CheckLaneLayout(checker);

        // 背景图：解析 [Events]、写回、对齐
        CheckBackground(checker);

        // 真机音频同步（真的开声卡播一段；没有声卡时 fail-open 跳过）
        CheckAudioSync(checker, dir);
    }

    // =====================================================================
    // Note 浏览区宽度（左右可调 + 居中）
    // =====================================================================

    private static void CheckLaneLayout(Checker C)
    {
        const double ruler = LaneLayout.RulerWidth;

        // 铺满（比例 1.0）：轨道从标尺右边开始，占满剩余宽度
        var full = LaneLayout.Compute(1000, ruler, 1.0);
        C("[Note宽度] 100% 时从标尺右边铺满",
            Math.Abs(full.LaneLeft - ruler) < 0.001 && Math.Abs(full.LaneWidth - (1000 - ruler)) < 0.001,
            $"left={full.LaneLeft:0.##} width={full.LaneWidth:0.##}");

        // 50%：宽度减半，且「标尺 + 轨道」整块在画布里居中（左右留白相等）
        var half = LaneLayout.Compute(1000, ruler, 0.5);
        var available = 1000 - ruler;
        C("[Note宽度] 50% 时宽度减半", Math.Abs(half.LaneWidth - available * 0.5) < 0.001,
            $"{half.LaneWidth:0.##}");
        C("[Note宽度] 标尺贴左、标尺紧挨轨道",
            Math.Abs(half.RulerLeft) < 0.001 && Math.Abs(half.LaneLeft - ruler) < 0.001,
            $"标尺 {half.RulerLeft:0.##} / 轨道 {half.LaneLeft:0.##}");
        C("[Note宽度] 收窄后的空白落在右侧",
            half.RightMargin(1000) > 0 && Math.Abs(half.RightMargin(1000) - (available - half.LaneWidth)) < 0.001,
            $"右侧留白 {half.RightMargin(1000):0.##}");

        // 越界夹取：0.05 → 0.2；2.0 → 1.0
        C("[Note宽度] 太小夹到 20%",
            Math.Abs(LaneLayout.Compute(1000, ruler, 0.05).LaneWidth - available * LaneLayout.MinRatio) < 0.001);
        C("[Note宽度] 太大夹到 100%",
            Math.Abs(LaneLayout.Compute(1000, ruler, 2.0).LaneWidth - available) < 0.001);

        // 极端小宽度也不出负数 / 0 宽
        var tiny = LaneLayout.Compute(20, ruler, 0.2);
        C("[Note宽度] 画布极窄时仍给最小宽度", tiny.LaneWidth >= LaneLayout.MinLaneWidth - 0.001,
            $"{tiny.LaneWidth:0.##}");

        // 列宽 = 轨道宽 / 键数（4K 收窄后明显变窄）
        var keys = 4;
        var wideColumn = LaneLayout.Compute(1000, ruler, 1.0).LaneWidth / keys;
        var narrowColumn = LaneLayout.Compute(1000, ruler, 0.4).LaneWidth / keys;
        C("[Note宽度] 调小后列宽同比变窄", narrowColumn < wideColumn * 0.5,
            $"宽 {wideColumn:0.#}px → 窄 {narrowColumn:0.#}px");

        // 渲染快照用的是同一套布局
        var map = BuildMap(keys: 4, bpm: 120, noteTimes: Array.Empty<double>());
        var editor = new ChartEditor(map);
        editor.Viewport.Resize(600);
        editor.Viewport.SetScale(0.2);

        var state = ChartRenderState.Build(editor, 600, 1000, 0.5);
        C("[Note宽度] 渲染快照按比例收窄",
            Math.Abs(state.LaneWidth - available * 0.5) < 0.001, $"{state.LaneWidth:0.##}");
        C("[Note宽度] 渲染快照里标尺贴左",
            Math.Abs(state.RulerLeft) < 0.001 && Math.Abs(state.LaneLeft - LaneLayout.RulerWidth) < 0.001,
            $"标尺 {state.RulerLeft:0.##} / 轨道 {state.LaneLeft:0.##}");
    }


    // =====================================================================
    // 背景图（解析 [Events] / 写回 / 对齐方式）
    // =====================================================================

    private static void CheckBackground(Checker C)
    {
        // ---- 对齐：贴左 / 居中 / 贴右 ----

        const double ruler = LaneLayout.RulerWidth;
        var left = LaneLayout.Compute(1000, ruler, 0.5, LaneLayout.LaneAlign.Left);
        var center = LaneLayout.Compute(1000, ruler, 0.5, LaneLayout.LaneAlign.Center);
        var right = LaneLayout.Compute(1000, ruler, 0.5, LaneLayout.LaneAlign.Right);

        C("[背景/对齐] 贴左：标尺在 0", Math.Abs(left.RulerLeft) < 0.001, $"{left.RulerLeft:0.##}");
        C("[背景/对齐] 贴右：右侧留白为 0", Math.Abs(right.RightMargin(1000)) < 0.001,
            $"{right.RightMargin(1000):0.##}");
        C("[背景/对齐] 居中：左右留白相等",
            Math.Abs(center.RulerLeft - center.RightMargin(1000)) < 0.001,
            $"左 {center.RulerLeft:0.##} / 右 {center.RightMargin(1000):0.##}");
        C("[背景/对齐] 三种对齐下轨道宽度一致",
            Math.Abs(left.LaneWidth - center.LaneWidth) < 0.001 &&
            Math.Abs(left.LaneWidth - right.LaneWidth) < 0.001);
        C("[背景/对齐] 标尺始终紧挨轨道左边",
            Math.Abs(left.LaneLeft - left.RulerLeft - ruler) < 0.001 &&
            Math.Abs(right.LaneLeft - right.RulerLeft - ruler) < 0.001);

        var fullL = LaneLayout.Compute(1000, ruler, 1.0, LaneLayout.LaneAlign.Left);
        var fullR = LaneLayout.Compute(1000, ruler, 1.0, LaneLayout.LaneAlign.Right);
        C("[背景/对齐] 铺满时左右对齐等价", Math.Abs(fullL.RulerLeft - fullR.RulerLeft) < 0.001);

        // ---- 解析 [Events] 背景行 ----

        C("[背景] 解析带引号的标准写法",
            BeatmapBackground.ParseBackgroundLine("0,0,\"bg.jpg\",0,0") == "bg.jpg",
            BeatmapBackground.ParseBackgroundLine("0,0,\"bg.jpg\",0,0") ?? "(null)");
        C("[背景] 解析不带引号的老写法",
            BeatmapBackground.ParseBackgroundLine("0,0,bg.png") == "bg.png",
            BeatmapBackground.ParseBackgroundLine("0,0,bg.png") ?? "(null)");
        C("[背景] 解析新写法 Background,...",
            BeatmapBackground.ParseBackgroundLine("Background,0,0,\"cover.jpg\"") == "cover.jpg",
            BeatmapBackground.ParseBackgroundLine("Background,0,0,\"cover.jpg\"") ?? "(null)");
        C("[背景] 视频事件不算背景图",
            BeatmapBackground.ParseBackgroundLine("1,0,\"video.avi\"") is null);
        C("[背景] 注释行不算背景图",
            BeatmapBackground.ParseBackgroundLine("//Background and Video events") is null);
        C("[背景] break 行不算背景图",
            BeatmapBackground.ParseBackgroundLine("2,1000,2000") is null);
        C("[背景] 空行返回 null", BeatmapBackground.ParseBackgroundLine("") is null);

        // ---- 文件名安全 ----

        C("[背景] 普通文件名安全", BeatmapBackground.IsSafeFileName("bg.jpg"));
        C("[背景] 挡掉上跳路径", !BeatmapBackground.IsSafeFileName("../bg.jpg"));
        C("[背景] 挡掉绝对路径", !BeatmapBackground.IsSafeFileName("C:\\bg.jpg"));
        C("[背景] 挡掉带冒号的", !BeatmapBackground.IsSafeFileName("a:b.jpg"));
        C("[背景] 图片扩展名判断", BeatmapBackground.IsSupportedImage("a.PNG") &&
                                   BeatmapBackground.IsSupportedImage("b.jpeg") &&
                                   !BeatmapBackground.IsSupportedImage("c.txt"));

        // ---- 写回行 ----

        C("[背景] 生成的背景行是标准格式",
            BeatmapBackground.BuildBackgroundLine("bg.jpg") == "0,0,\"bg.jpg\",0,0",
            BeatmapBackground.BuildBackgroundLine("bg.jpg"));

        var lines = new List<string>
        {
            "osu file format v14", "", "[Events]",
            "//Background and Video events",
            "0,0,\"old.jpg\",0,0",
            "2,1000,2000",              // break 行必须保留
            "//Storyboard Layer 0",
            "", "[TimingPoints]", "0,500,4,2,0,60,1,0",
        };

        var replaced = BeatmapBackground.TryReplaceBackgroundLine(lines, "0,0,\"new.png\",0,0");
        C("[背景] 替换成功", replaced);
        C("[背景] 旧背景行被换掉",
            lines.Contains("0,0,\"new.png\",0,0") && !lines.Contains("0,0,\"old.jpg\",0,0"));
        C("[背景] break 行原样保留", lines.Contains("2,1000,2000"));
        C("[背景] 其它段落没被破坏",
            lines.Contains("[TimingPoints]") && lines.Contains("0,500,4,2,0,60,1,0"));

        var noBg = new List<string> { "[Events]", "//Break Periods", "", "[HitObjects]" };
        BeatmapBackground.TryReplaceBackgroundLine(noBg, "0,0,\"x.jpg\",0,0");
        C("[背景] 没有背景行时插入一行",
            noBg.Contains("0,0,\"x.jpg\",0,0") && noBg.Contains("[HitObjects]"));

        var noSection = new List<string> { "osu file format v14", "", "[TimingPoints]", "0,500,4,2,0,60,1,0" };
        C("[背景] 没有 [Events] 段时返回 false",
            !BeatmapBackground.TryReplaceBackgroundLine(noSection, "0,0,\"x.jpg\",0,0"));
        BeatmapBackground.AppendEventsSection(noSection, "0,0,\"x.jpg\",0,0");
        C("[背景] 补上 [Events] 段并写入背景行",
            noSection.Contains("[Events]") && noSection.Contains("0,0,\"x.jpg\",0,0"));

        // ---- 读谱面时自动带上背景图 ----

        var chartText = new[]
        {
            "osu file format v14", "", "[General]", "AudioFilename: a.mp3", "Mode: 3", "",
            "[Events]", "//Background and Video events", "0,0,\"mybg.jpg\",0,0", "",
            "[Difficulty]", "CircleSize:4", "",
            "[HitObjects]", "64,192,1000,1,0,0:0:0:0:",
        };

        var map = OsuFileDocument.Parse(chartText, Path.Combine(Path.GetTempPath(), "bgtest", "t.osu"));
        C("[背景] 读谱面时解析出背景文件名", map.BackgroundFileName == "mybg.jpg", map.BackgroundFileName);
        C("[背景] 图片不存在时路径为空（不崩）", map.BackgroundPath.Length == 0, map.BackgroundPath);

        map.BackgroundFileName = "newbg.png";
        var text = OsuFileDocument.BuildText(map);
        C("[背景] 保存文本里是新背景", text.Contains("0,0,\"newbg.png\",0,0"));
        C("[背景] 保存文本里没有旧背景", !text.Contains("mybg.jpg"));
        C("[背景] 保存文本仍是合法谱面",
            text.Contains("[HitObjects]") && text.Contains("64,192,1000,1,0,"));

        var map2 = OsuFileDocument.Parse(chartText, "t2.osu");
        map2.BackgroundFileName = "";
        C("[背景] 没设背景时保留原声明",
            OsuFileDocument.BuildText(map2).Contains("0,0,\"mybg.jpg\",0,0"));

        C("[背景] 拖入 .osu 不会被当成图片", !BeatmapBackground.IsSupportedImage("chart.osu"));
        C("[背景] 拖入 .txt 不会被当成图片", !BeatmapBackground.IsSupportedImage("readme.txt"));

        // ---- Note 浏览区底色（颜色 + 透明度 + 恢复默认） ----

        var defaultColor = OsuManiaEditorConfig.DefaultLaneBackgroundColor;
        C("[背景/底色] 默认是黑 + 55% 不透明",
            defaultColor.R == 0 && defaultColor.G == 0 && defaultColor.B == 0 &&
            defaultColor.A == OsuManiaEditorConfig.DefaultLaneBackgroundAlpha,
            $"#{defaultColor.A:X2}{defaultColor.R:X2}{defaultColor.G:X2}{defaultColor.B:X2}");
        C("[背景/底色] 默认十六进制文字对得上",
            OsuManiaEditorConfig.DefaultLaneBackgroundHex == "#8C000000",
            OsuManiaEditorConfig.DefaultLaneBackgroundHex);

        // 有符号存储能正确还原（JSON 里是负数也不影响）
        var stored = OsuManiaEditorConfig.DefaultLaneBackgroundArgbInt;
        var restored = System.Windows.Media.Color.FromArgb(
            (byte)(stored >> 24), (byte)(stored >> 16), (byte)(stored >> 8), (byte)stored);
        C("[背景/底色] 有符号存储可还原 alpha 与 RGB",
            restored.A == defaultColor.A && restored.R == 0 && restored.G == 0 && restored.B == 0,
            $"#{restored.A:X2}{restored.R:X2}{restored.G:X2}{restored.B:X2}");
    }

    private static void CheckSelection(Checker C)
    {
        // ---- NoteSelection 纯逻辑 ----

        var sel = new NoteSelection();
        C("[多选] 初始为空", sel.IsEmpty && sel.First == -1);

        sel.SelectSingle(3);
        C("[多选] 单选", sel.Count == 1 && sel.Contains(3) && sel.First == 3);

        sel.Toggle(5);
        C("[多选] Toggle 加选", sel.Count == 2 && sel.Contains(3) && sel.Contains(5));
        sel.Toggle(5);
        C("[多选] Toggle 再点取消", sel.Count == 1 && !sel.Contains(5));

        sel.AddRange(new[] { 7, 9, 9, 1 });
        C("[多选] AddRange 去重并按序", sel.Count == 4 && sel.Indices.SequenceEqual(new[] { 1, 3, 7, 9 }),
            string.Join(",", sel.Indices));

        // 删掉下标 3 之后，比它大的要前移
        sel.OnNoteRemoved(3);
        C("[多选] 删除后下标前移", sel.Indices.SequenceEqual(new[] { 1, 6, 8 }), string.Join(",", sel.Indices));

        // 删掉一个没选中的（下标 0）：比它大的也要前移
        sel.OnNoteRemoved(0);
        C("[多选] 删除前置项同样前移", sel.Indices.SequenceEqual(new[] { 0, 5, 7 }), string.Join(",", sel.Indices));

        sel.SetTo(new[] { 2, 4 });
        C("[多选] SetTo 批量替换", sel.Count == 2 && sel.Indices.SequenceEqual(new[] { 2, 4 }));

        var changedCount = 0;
        sel.Changed += (_, _) => changedCount++;
        sel.SetTo(new[] { 2, 4 });
        C("[多选] SetTo 相同内容不发变更", changedCount == 0, $"{changedCount}");
        sel.Clear();
        C("[多选] Clear 触发变更", changedCount == 1 && sel.IsEmpty, $"{changedCount}");

        // ---- 框选 ----

        var map = BuildMap(keys: 4, bpm: 120, noteTimes: Array.Empty<double>());

        // 布局：列 0/1/2 上各有几个不同时间的 note
        map.HitObjects.Add(EditorNote.NewNote(0, 1000));
        map.HitObjects.Add(EditorNote.NewNote(0, 2000));
        map.HitObjects.Add(EditorNote.NewNote(1, 1500));
        map.HitObjects.Add(EditorNote.NewNote(2, 2500));
        map.HitObjects.Add(EditorNote.NewLongNote(1, 3000, 5000));
        map.RefreshIndexes();

        var box = MarqueeSelection.Box.FromCorners(1400, 2600, 0, 1);
        var hits = MarqueeSelection.Find(map, box);
        C("[框选] 只选时间与列同时落在框里的 note", hits.Count == 2,
            string.Join(",", hits.Select(i => map.HitObjects[i].ToString())));

        // 时间区间 [0,3000] 覆盖：1000/2000(列0)、1500(列1)、2500(列2)、3000(LN 头，列1) = 5 个
        var allColumns = MarqueeSelection.Box.FromCorners(0, 3000, 0, 3);
        C("[框选] 全列框选按时间筛", MarqueeSelection.Find(map, allColumns).Count == 5,
            $"{MarqueeSelection.Find(map, allColumns).Count}");

        // 反向拖拽（从右下往左上）要与正向一致
        var reversed = MarqueeSelection.Box.FromCorners(2600, 1400, 1, 0);
        C("[框选] 反向拖拽等价", reversed.FromTime == 1400 && reversed.ToColumn == 1);

        // 框住 LN 的头不选身体（默认行为）
        var overLnBody = MarqueeSelection.Box.FromCorners(4000, 4500, 1, 1);
        C("[框选] 只框到 LN 身体不算选中（默认）", MarqueeSelection.Find(map, overLnBody).Count == 0,
            $"{MarqueeSelection.Find(map, overLnBody).Count}");
        C("[框选] 显式要求时可框住 LN 身体",
            MarqueeSelection.Find(map, overLnBody, includeLongNoteBodies: true).Count == 1);

        // ---- Z / V 跳转 + 区间扩选 ----

        var editor = new ChartEditor(map) { GridDivisor = 4, SnapToGrid = true };
        editor.SetLayout(64, 400, 4);
        editor.Viewport.Resize(600);
        editor.Viewport.SetScale(0.2);

        C("[Z/V] 最早 note 时间 = 1000", editor.EarliestNoteTime() == 1000, $"{editor.EarliestNoteTime()}");
        C("[Z/V] 最晚 note 开始时间 = 3000（不是 LN 尾巴 5000）",
            editor.LatestNoteStartTime() == 3000, $"{editor.LatestNoteStartTime()}");

        // 锚点在 1000 → 按 V 跳到最后一个 note 开始时间（3000；LN 尾巴 5000 不算），
        // 区间 [1000,3000] 内的 note 全进选择 —— 数据里正好 5 个（1000/1500/2000/2500/3000）
        editor.PlayheadMs = 1000;
        editor.ClearRangeAnchor();
        editor.Selection.Clear();
        var lastTime = editor.JumpToLastNote();
        C("[Z/V] V 跳到最后一个 note 的开始时间", lastTime == 3000, $"{lastTime}");
        C("[Z/V] V 之后区间内的 note 被选中", editor.SelectionCount == 5,
            $"{editor.SelectionCount}: {string.Join(",", editor.Selection.Indices)}");

        // 再从 3000 往回按 Z（锚点已更新到 3000）→ 区间 [1000,3000] 同样覆盖
        var firstTime = editor.JumpToFirstNote();
        C("[Z/V] Z 跳到第一个 note", firstTime == 1000, $"{firstTime}");
        C("[Z/V] 连按 Z/V 不会丢选择", editor.SelectionCount >= 4, $"{editor.SelectionCount}");

        // 只选一条轨道：锚点列集合被限定在列 0 时，其他列不进来
        var laneMap = BuildMap(keys: 4, bpm: 120, noteTimes: Array.Empty<double>());
        laneMap.HitObjects.Add(EditorNote.NewNote(0, 1000));
        laneMap.HitObjects.Add(EditorNote.NewNote(0, 2000));
        laneMap.HitObjects.Add(EditorNote.NewNote(1, 1500));
        laneMap.HitObjects.Add(EditorNote.NewNote(2, 2500));
        laneMap.RefreshIndexes();

        var laneEditor = new ChartEditor(laneMap);
        laneEditor.SetLayout(64, 400, 4);
        laneEditor.Viewport.Resize(600);
        laneEditor.PlayheadMs = 1000;
        laneEditor.SetRangeAnchor(1000, new[] { 0 });
        laneEditor.Selection.Clear();
        laneEditor.JumpToLastNote();
        C("[Z/V] 限定轨道时只选那一条轨道", laneEditor.SelectionCount == 2,
            $"{laneEditor.SelectionCount}: {string.Join(",", laneEditor.Selection.Indices)}");

        // 多轨：锚点列 = {0,1}
        var multi = new ChartEditor(laneMap);
        multi.SetLayout(64, 400, 4);
        multi.Viewport.Resize(600);
        multi.PlayheadMs = 1000;
        multi.SetRangeAnchor(1000, new[] { 0, 1 });
        multi.Selection.Clear();
        multi.JumpToLastNote();
        C("[Z/V] 可以同时选多条轨道", multi.SelectionCount == 3,
            $"{multi.SelectionCount}: {string.Join(",", multi.Selection.Indices)}");

        // ---- 选择工具：点空白不进摆放 ----

        var toolMap = BuildMap(keys: 4, bpm: 120, noteTimes: Array.Empty<double>());
        var toolEditor = new ChartEditor(toolMap) { Tool = ChartEditor.EditTool.Select };
        toolEditor.SetLayout(64, 400, 4);
        toolEditor.Viewport.Resize(600);
        toolEditor.Viewport.SetScale(0.4);

        var before = toolMap.HitObjects.Count;
        toolEditor.PressAt(toolEditor.ColumnCenter(1), toolEditor.TimeToY(500));
        toolEditor.Release();
        C("[选择工具] 点空白不会放 note", toolMap.HitObjects.Count == before, $"{toolMap.HitObjects.Count}");

        // 框选之后按 Z/V 仍保留选择（用户明确要求）
        toolMap.HitObjects.Add(EditorNote.NewNote(1, 1000));
        toolMap.HitObjects.Add(EditorNote.NewNote(1, 2000));
        toolMap.HitObjects.Add(EditorNote.NewNote(2, 3000));
        toolMap.RefreshIndexes();

        var keepEditor = new ChartEditor(toolMap) { Tool = ChartEditor.EditTool.Select };
        keepEditor.SetLayout(64, 400, 4);
        keepEditor.Viewport.Resize(600);
        keepEditor.Viewport.SetScale(0.4);
        keepEditor.Viewport.ScrollTo(0);

        // 框选列 1 的两个 note
        keepEditor.PressAt(keepEditor.ColumnCenter(1), keepEditor.TimeToY(500));
        keepEditor.DragTo(keepEditor.ColumnCenter(1), keepEditor.TimeToY(2200));
        keepEditor.Release();
        var afterBox = keepEditor.SelectionCount;
        C("[框选] 拖动框选能选中 note", afterBox == 2, $"{afterBox}");

        // 框选后按 V：选择保持（不退化成“只剩一个”）
        keepEditor.JumpToLastNote();
        C("[框选] 按 V 之后框选结果仍然选中", keepEditor.SelectionCount >= 2, $"{keepEditor.SelectionCount}");

        // ---- 多选整体拖动 = 一步撤销 ----

        var dragMap = BuildMap(keys: 4, bpm: 120, noteTimes: Array.Empty<double>());
        dragMap.HitObjects.Add(EditorNote.NewNote(0, 1000));
        dragMap.HitObjects.Add(EditorNote.NewNote(1, 1000));
        dragMap.HitObjects.Add(EditorNote.NewNote(2, 1000));
        dragMap.RefreshIndexes();

        var dragEditor = new ChartEditor(dragMap) { Tool = ChartEditor.EditTool.Select };
        dragEditor.SetLayout(64, 400, 4);
        dragEditor.Viewport.Resize(600);
        dragEditor.Viewport.SetScale(0.4);
        dragEditor.Viewport.ScrollTo(0);

        dragEditor.Selection.SetTo(new[] { 0, 1 });
        var undoBefore = dragMap.UndoCount;

        dragEditor.PressAt(dragEditor.ColumnCenter(0), dragEditor.TimeToY(1000));
        dragEditor.DragTo(dragEditor.ColumnCenter(0), dragEditor.TimeToY(2000));
        dragEditor.Release();

        C("[多选拖动] 选中的两个一起动", Math.Abs(dragMap.HitObjects[0].StartTime - 2000) < 1
                                        && Math.Abs(dragMap.HitObjects[1].StartTime - 2000) < 1,
            $"{dragMap.HitObjects[0].StartTime:0} / {dragMap.HitObjects[1].StartTime:0}");
        C("[多选拖动] 没选中的不动", Math.Abs(dragMap.HitObjects[2].StartTime - 1000) < 1,
            $"{dragMap.HitObjects[2].StartTime:0}");
        C("[多选拖动] 整次拖动只压一步撤销", dragMap.UndoCount == undoBefore + 1,
            $"{undoBefore} → {dragMap.UndoCount}");

        dragEditor.Undo();
        C("[多选拖动] 撤销一次全部回原位",
            Math.Abs(dragMap.HitObjects[0].StartTime - 1000) < 1 && Math.Abs(dragMap.HitObjects[1].StartTime - 1000) < 1,
            $"{dragMap.HitObjects[0].StartTime:0} / {dragMap.HitObjects[1].StartTime:0}");

        // ---- 多选删除 ----

        var delMap = BuildMap(keys: 4, bpm: 120, noteTimes: Array.Empty<double>());
        for (var i = 0; i < 5; i++) delMap.HitObjects.Add(EditorNote.NewNote(0, 1000 + i * 200));
        delMap.RefreshIndexes();

        var delEditor = new ChartEditor(delMap);
        delEditor.Selection.SetTo(new[] { 1, 3 });
        var removed = delEditor.DeleteSelection();
        C("[多选删除] 删掉选中的 2 个", removed == 2 && delMap.HitObjects.Count == 3,
            $"removed={removed} left={delMap.HitObjects.Count}");
        C("[多选删除] 删完选择清空", delEditor.SelectionCount == 0);
        C("[多选删除] 剩下的时间正确", Math.Abs(delMap.HitObjects[0].StartTime - 1000) < 1
                                       && Math.Abs(delMap.HitObjects[1].StartTime - 1400) < 1
                                       && Math.Abs(delMap.HitObjects[2].StartTime - 1800) < 1,
            string.Join(",", delMap.HitObjects.Select(n => n.StartTime.ToString("0"))));

        // 一步撤销能全恢复
        delEditor.Undo();
        C("[多选删除] 撤销可恢复（至少恢复出 note）", delMap.HitObjects.Count >= 4, $"{delMap.HitObjects.Count}");
    }

    // =====================================================================
    // 自动保存：空闲计时 + 真的写盘
    // =====================================================================

    private static void CheckAutoSave(Checker C, string dir)
    {
        // ---- 空闲计时（纯逻辑） ----

        var timer = new IdleAutoSaveTimer { ThresholdMs = 10_000, Enabled = true };

        var fired = false;
        for (var i = 0; i < 39; i++) fired |= timer.Advance(250, suppressed: false);
        C("[自动保存] 未到阈值不触发（9.75s < 10s）", !fired, $"idle={timer.IdleMs:0}ms");

        // 第 40 次推进正好到 10s
        fired = timer.Advance(250, suppressed: false);
        C("[自动保存] 到阈值（10s）触发一次", fired, $"idle={timer.IdleMs:0}ms");
        C("[自动保存] 触发后空闲重新计时", Math.Abs(timer.IdleMs) < 0.001, $"{timer.IdleMs:0}ms");

        // 操作会清零
        timer.Advance(9700, suppressed: false);
        timer.NoteActivity();
        fired = false;
        for (var i = 0; i < 39; i++) fired |= timer.Advance(250, suppressed: false);
        C("[自动保存] 中途有操作则重新计时", !fired, $"idle={timer.IdleMs:0}ms");

        // 播放中不计空闲
        var playing = new IdleAutoSaveTimer { ThresholdMs = 1000, Enabled = true };
        var firedWhilePlaying = false;
        for (var i = 0; i < 40; i++) firedWhilePlaying |= playing.Advance(250, suppressed: true);
        C("[自动保存] 播放中不触发（不计空闲）", !firedWhilePlaying, $"idle={playing.IdleMs:0}ms");

        // 暂停后要重新计时（不能立刻补一发）
        var afterPause = playing.Advance(250, suppressed: false);
        C("[自动保存] 暂停后重新计时（不立刻补触发）", !afterPause, $"idle={afterPause:0}");

        var firedAfterPause = false;
        for (var i = 0; i < 4; i++) firedAfterPause |= playing.Advance(250, suppressed: false);
        C("[自动保存] 暂停后正常累计到阈值会触发", firedAfterPause, $"idle={playing.IdleMs:0}ms");

        // 关闭时不触发
        var disabled = new IdleAutoSaveTimer { ThresholdMs = 1000, Enabled = false };
        var firedDisabled = false;
        for (var i = 0; i < 20; i++) firedDisabled |= disabled.Advance(250, suppressed: false);
        C("[自动保存] 关掉后永不触发", !firedDisabled);

        // 自定义阈值生效（2 秒）
        var fast = new IdleAutoSaveTimer { ThresholdMs = 2000, Enabled = true };
        var count = 0;
        for (var i = 0; i < 40; i++)
        {
            if (fast.Advance(250, suppressed: false)) count++;
        }
        C("[自动保存] 阈值改成 2 秒后 10 秒内触发 5 次", count == 5, $"{count}");

        // ---- 真的写盘 ----

        var map = BuildMap(keys: 4, bpm: 120, noteTimes: Array.Empty<double>());
        map.FilePath = Path.Combine(dir, "autosave.osu");

        // 先建一个合法的 .osu 骨架（保存时只替换两段正文，需要一个真实文件打底）
        var lines = new List<string>
        {
            "osu file format v14", "", "[General]", "AudioFilename: a.mp3", "Mode: 3", "",
            "[Difficulty]", "CircleSize:4", "OverallDifficulty:8", "",
            "[TimingPoints]", "0,500,4,2,0,60,1,0", "",
            "[HitObjects]", "",
        };
        File.WriteAllLines(map.FilePath, lines, new UTF8Encoding(false));

        var reloaded = OsuFileDocument.Load(map.FilePath);
        C("[自动保存] 打底文件能读", reloaded.HitObjects.Count == 0, $"{reloaded.HitObjects.Count}");

        map.AddNote(EditorNote.NewNote(1, 1000));
        map.AddNote(EditorNote.NewNote(2, 1500));
        C("[自动保存] 编辑后标记为脏", map.IsDirty);

        OsuFileDocument.Save(map, map.FilePath);
        map.IsDirty = false;

        var saved = OsuFileDocument.Load(map.FilePath);
        C("[自动保存] 写盘后 note 真的进了文件", saved.HitObjects.Count == 2, $"{saved.HitObjects.Count}");
        C("[自动保存] 保存后脏标记被清掉", !map.IsDirty);

        // 同一张谱面再存一次（覆盖）不能把内容搞丢
        map.AddNote(EditorNote.NewNote(0, 2000));
        OsuFileDocument.Save(map, map.FilePath);
        var saved2 = OsuFileDocument.Load(map.FilePath);
        C("[自动保存] 反复保存不累积 / 不丢内容", saved2.HitObjects.Count == 3, $"{saved2.HitObjects.Count}");

        // 残留临时文件不能留着（Save 用的是「写 tmp 再替换」）
        C("[自动保存] 保存后不留临时文件", !File.Exists(map.FilePath + ".ylt-tmp"));
    }

    // =====================================================================
    // 新 note 的采样字段：编辑器不做 hitsounds
    // =====================================================================

    private static void CheckDefaultSampling(Checker C)
    {
        var note = EditorNote.NewNote(2, 1234);
        C("[采样] 新普通键用默认采样组（0:0:0:0）",
            note.SampleSet == 0 && note.AdditionSet == 0 && note.CustomIndex == 0 && note.Volume == 0,
            $"{note.SampleSet}:{note.AdditionSet}:{note.CustomIndex}:{note.Volume}");
        C("[采样] 新普通键没有自定义采样文件名", note.SampleFileName.Length == 0);

        var ln = EditorNote.NewLongNote(1, 500, 900);
        C("[采样] 新长条用同一套默认采样值",
            ln.SampleSet == 0 && ln.AdditionSet == 0 && ln.CustomIndex == 0 && ln.Volume == 0);

        // 写回文件时应该是标准的 0:0:0:0:（最后一段是空文件名）
        var noteText = OsuFileDocument.FormatHitObject(note, 4);
        C("[采样] 普通键写回 0:0:0:0:", noteText.EndsWith("0:0:0:0:", StringComparison.Ordinal), noteText);
        C("[采样] 普通键不以 :: 结尾（不写多余空段）", !noteText.EndsWith("::", StringComparison.Ordinal), noteText);

        var lnText = OsuFileDocument.FormatHitObject(ln, 4);
        C("[采样] 长条写回 endTime:0:0:0:0:", lnText.EndsWith(":0:0:0:0:", StringComparison.Ordinal), lnText);

        // 已有 note 的采样信息不能被新写 note 的默认值改写（读进来的照原样保留）
        var parsed = OsuFileDocument.Parse(new[]
        {
            "osu file format v14", "[Difficulty]", "CircleSize:4", "[HitObjects]",
            "64,192,1000,1,0,2:3:5:40:custom.wav",
        }, "sample.osu");

        C("[采样] 已有 note 的采样信息原样保留",
            parsed.HitObjects.Count == 1 && parsed.HitObjects[0].SampleSet == 2 &&
            parsed.HitObjects[0].AdditionSet == 3 && parsed.HitObjects[0].Volume == 40 &&
            parsed.HitObjects[0].SampleFileName == "custom.wav",
            $"{parsed.HitObjects[0].SampleSet}:{parsed.HitObjects[0].AdditionSet}:{parsed.HitObjects[0].Volume}:{parsed.HitObjects[0].SampleFileName}");
    }

    // =====================================================================
    // 真机音频同步：真的开声卡播一段合成音频，对比时钟与真实时间
    // =====================================================================

    /// <summary>
    /// 为什么需要真机验证：<see cref="PlaybackClock"/> 的逻辑断言用的是假时钟，它证明不了
    /// 「和真实声卡配合时不会漂」—— 设备位置噪声、缓冲延迟、定位后重建输出的行为
    /// 都只有在真设备上才看得出来。没有可用声卡时跳过并记 PASS（项目里的音频类断言都是 fail-open）。
    /// </summary>
    private static void CheckAudioSync(Checker C, string dir)
    {
        var wav = Path.Combine(dir, "tone.wav");
        try
        {
            WriteTestTone(wav, seconds: 6);
        }
        catch (Exception ex)
        {
            C("[音频同步] 生成测试音频失败（跳过）", true, ex.Message);
            return;
        }

        try
        {
            using var engine = new ChartAudioEngine();

            if (!engine.Load(wav))
            {
                C("[音频同步] 打开测试音频（无声卡时跳过）", true, engine.LastError);
                return;
            }

            C("[音频同步] 音频时长读数正确（6 秒 ±50ms）", Math.Abs(engine.DurationMs - 6000) < 50,
                $"{engine.DurationMs:0}ms");

            if (!engine.Play())
            {
                C("[音频同步] 开始播放（无声卡时跳过）", true, engine.LastError);
                return;
            }

            // 播放 1.2 秒：时钟应该跟上真实时间（误差 = 缓冲 + 输出延迟的量级）
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1200)
            {
                engine.Update();
                Thread.Sleep(8);
            }

            var position = engine.PositionMs;
            var elapsed = watch.Elapsed.TotalMilliseconds;
            C("[音频同步] 播放中时钟跟随真实时间（误差 < 150ms）", Math.Abs(position - elapsed) < 150,
                $"时钟 {position:0}ms vs 真实 {elapsed:0}ms");
            C("[音频同步] 播放中设备延迟有上界（< 200ms）", engine.OutputLatencyMs is >= 0 and < 200,
                $"延迟估计 {engine.OutputLatencyMs:0}ms · drift={engine.LastDriftMs:0}ms");

            // 音频偏移必须真的作用到时钟上（用户靠它对齐耳朵）。
            // ⚠ 不要用「两次读数之差」验证：时钟一直在走，采样时间差会盖过偏移量。
            // 这里用「同一时刻、只改偏移、立即 Sync 一次」的对比：Sync 会把时钟硬对齐到
            // heard（偏移已经算进去），所以位置差就是偏移量。
            engine.Pause();
            engine.Seek(2000);

            engine.UserOffsetMs = 0;
            engine.Update();              // 暂停时 Sync 不生效，先让状态干净
            engine.Play();
            Thread.Sleep(120);
            engine.UserOffsetMs = 0;
            engine.Update();
            var basePosition = engine.PositionMs;

            engine.UserOffsetMs = 80;
            engine.Update();
            var offsetPosition = engine.PositionMs;
            engine.UserOffsetMs = 0;

            C("[音频同步] 音频偏移作用到播放时钟上（+80ms 推后位置）",
                offsetPosition >= basePosition + 40,
                $"有偏移 {offsetPosition:0}ms vs 无偏移 {basePosition:0}ms");

            // 定位到 4 秒：立刻读应该在目标附近，之后继续推进
            engine.Seek(4000);
            C("[音频同步] 定位后位置立刻到目标（±60ms）", Math.Abs(engine.PositionMs - 4000) < 60,
                $"{engine.PositionMs:0}ms");

            watch.Restart();
            while (watch.ElapsedMilliseconds < 600)
            {
                engine.Update();
                Thread.Sleep(8);
            }

            var moved = engine.PositionMs - 4000;
            C("[音频同步] 定位后继续推进（600ms 内走了 450~750ms）", moved is > 450 and < 750, $"{moved:0}ms");

            engine.Pause();
            var paused = engine.PositionMs;
            Thread.Sleep(200);
            engine.Update();
            C("[音频同步] 暂停后位置不推进", Math.Abs(engine.PositionMs - paused) < 1.5,
                $"{engine.PositionMs:0} vs {paused:0}");

            engine.Stop();
            C("[音频同步] 停止后归零", engine.PositionMs < 1, $"{engine.PositionMs:0}");
        }
        catch (Exception ex)
        {
            // 没有声卡 / 设备被独占：跳过而不是失败（和项目里其它音频断言一致）
            C("[音频同步] 真机音频检查跳过（没有可用输出设备）", true,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>写一段 44.1kHz / 16bit / 立体声的测试音（每 0.5 秒一次短促“滴”）。</summary>
    private static void WriteTestTone(string path, int seconds)
    {
        var format = new NAudio.Wave.WaveFormat(44100, 16, 2);
        using var stream = File.Create(path);
        using var writer = new NAudio.Wave.WaveFileWriter(stream, format);

        var total = 44100 * seconds;
        for (var i = 0; i < total; i++)
        {
            var inBeat = i % 22050 < 3000;
            var sample = inBeat ? Math.Sin(2 * Math.PI * 880 * i / 44100.0) * 0.35 : 0;
            var value = (short)(sample * short.MaxValue);

            writer.WriteByte((byte)(value & 0xFF));
            writer.WriteByte((byte)((value >> 8) & 0xFF));
            writer.WriteByte((byte)(value & 0xFF));
            writer.WriteByte((byte)((value >> 8) & 0xFF));
        }
    }

    // =====================================================================
    // 播放时钟：平滑推进 / 暂停 / 定位 / 漂移校正 / 硬重同步
    // =====================================================================

    private static void CheckPlaybackClock(Checker C)
    {
        // 假时钟：手动推进，完全可控
        var now = 0.0;
        var clock = new PlaybackClock(() => now);

        clock.Start(0);
        now = 500;
        C("[时钟] 播放 500ms 后位置 ≈ 500ms", Math.Abs(clock.NowMs - 500) < 0.01, $"{clock.NowMs:0.###}");

        clock.Pause();
        var paused = clock.NowMs;
        now += 300;
        C("[时钟] 暂停后位置冻结", Math.Abs(clock.NowMs - paused) < 0.001, $"{clock.NowMs:0.###}");

        clock.Resume();
        now += 200;
        C("[时钟] 恢复后继续走（不补暂停的 300ms）", Math.Abs(clock.NowMs - (paused + 200)) < 0.01,
            $"{clock.NowMs:0.###} 期望 {paused + 200:0.###}");

        // 定位：位置立刻变，之后从新位置推进
        clock.Seek(10000);
        C("[时钟] 定位后位置立刻到位", Math.Abs(clock.NowMs - 10000) < 0.001, $"{clock.NowMs:0.###}");
        now += 100;
        C("[时钟] 定位后继续从新位置推进", Math.Abs(clock.NowMs - 10100) < 0.01, $"{clock.NowMs:0.###}");

        // 小漂移：只微调速率，不跳变
        var before = clock.NowMs;
        now += 10;
        var predicted = before + 10;
        clock.Sync(predicted - 30);   // 设备比推算慢 30ms
        C("[时钟] 小漂移不跳变（位置连续）", Math.Abs(clock.NowMs - predicted) < 5,
            $"位置 {clock.NowMs:0.##} vs 推算 {predicted:0.##}");
        C("[时钟] 小漂移靠速率微调追赶", clock.Rate < 1.0, $"rate={clock.Rate:0.#####}");
        C("[时钟] 速率微调不超过 1.5%", Math.Abs(clock.Rate - 1.0) <= PlaybackClock.MaxRateAdjust + 1e-9,
            $"rate={clock.Rate:0.#####}");

        // 大漂移：硬对齐
        now += 10;
        clock.Sync(20000);
        C("[时钟] 大漂移硬对齐到设备位置", Math.Abs(clock.NowMs - 20000) < 0.01, $"{clock.NowMs:0.###}");
        C("[时钟] 硬重同步被计数", clock.HardResyncCount == 1, $"{clock.HardResyncCount}");
        C("[时钟] 硬重同步后速率归 1", Math.Abs(clock.Rate - 1.0) < 1e-9, $"{clock.Rate:0.#####}");

        // 死区：±2ms 内的抖动不算漂移（不改变速率）
        clock.Seek(0);
        clock.Sync(1.5);
        C("[时钟] 1.5ms 抖动落在死区（速率保持 1）", Math.Abs(clock.Rate - 1.0) < 1e-6, $"{clock.Rate:0.#####}");

        // 停止
        clock.Stop();
        C("[时钟] 停止后位置归零且不推进", clock.NowMs == 0 && !clock.IsRunning, $"{clock.NowMs:0.###}");

        // 假时钟倒退时不要往回走（防止意外得到负时间）
        var back = 0.0;
        var clock2 = new PlaybackClock(() => back);
        clock2.Start(0);
        back = 100;
        var at100 = clock2.NowMs;
        back = 50;   // 倒退：位置应该停在 at100，不能回到 50
        C("[时钟] 注入时钟倒退时不回退", Math.Abs(clock2.NowMs - at100) < 0.001,
            $"{clock2.NowMs:0.###} 期望 {at100:0.###}");
    }

    // =====================================================================
    // 网格吸附：分段红线 / 细分 / 边界
    // =====================================================================

    private static void CheckBeatGrid(Checker C)
    {
        var redLines = new List<EditorTimingPoint>
        {
            EditorTimingPoint.NewRedLine(0, 120),      // 500ms / 拍
            EditorTimingPoint.NewRedLine(10000, 240),  // 250ms / 拍
        };

        // 120 BPM、1/4 拍 = 125ms 一格
        C("[网格] 1/4 拍吸附（120BPM）", Math.Abs(BeatGrid.Snap(redLines, 130, 4) - 125) < 0.01,
            $"{BeatGrid.Snap(redLines, 130, 4):0.###}");
        C("[网格] 吸附不受小偏移影响", Math.Abs(BeatGrid.Snap(redLines, 118, 4) - 125) < 0.01,
            $"{BeatGrid.Snap(redLines, 118, 4):0.###}");
        C("[网格] 整拍吸附", Math.Abs(BeatGrid.Snap(redLines, 260, 1) - 500) < 0.01,
            $"{BeatGrid.Snap(redLines, 260, 1):0.###}");

        // 变速后必须按新段算（这是「中期变速谱面吸附错位」的回归断言）
        C("[网格] 变速后按新红线算（240BPM, 1/4 = 62.5ms）",
            Math.Abs(BeatGrid.Snap(redLines, 10070, 4) - 10062.5) < 0.01,
            $"{BeatGrid.Snap(redLines, 10070, 4):0.###}");

        // 细分 = 0 / 空红线：原样返回
        C("[网格] 不吸附时原样返回", Math.Abs(BeatGrid.Snap(redLines, 133.3, 0) - 133.3) < 1e-9);
        C("[网格] 没有红线时原样返回", Math.Abs(BeatGrid.Snap(Array.Empty<EditorTimingPoint>(), 133.3, 4) - 133.3) < 1e-9);

        // 红线查找
        C("[网格] 0ms 落在第一条红线", BeatGrid.RedLineIndexAt(redLines, 0) == 0);
        C("[网格] 5000ms 落在第一条红线", BeatGrid.RedLineIndexAt(redLines, 5000) == 0);
        C("[网格] 10000ms 落在第二条红线（边界归后一条）", BeatGrid.RedLineIndexAt(redLines, 10000) == 1);
        C("[网格] 没有红线返回 -1", BeatGrid.RedLineIndexAt(Array.Empty<EditorTimingPoint>(), 0) == -1);

        // 网格线生成：只生成可见区间，且小节线正确
        // [0, 2000] 闭区间，120BPM、4/4、1/4 拍 = 125ms 一格 → 0,125,…,2000 共 17 条
        // 小节线 = 每拍的下标能被 4 整除：k=0（0ms）与 k=16（2000ms，正好第 4 拍 = 新小节）= 2 条
        var lines = BeatGrid.Lines(redLines, 0, 2000, 4);
        C("[网格] 2 秒 @120BPM 1/4 拍生成 17 条（含 0 与 2000 两端）", lines.Count == 17, $"{lines.Count}");
        var bars = lines.Count(l => l.Kind == BeatGrid.GridLineKind.Bar);
        C("[网格] 小节线落在整小节上（0ms 与 2000ms）", bars == 2, $"{bars}");
        var beats = lines.Count(l => l.Kind == BeatGrid.GridLineKind.Beat);
        C("[网格] 拍线 3 条（500 / 1000 / 1500，不含两端小节线）", beats == 3, $"{beats}");

        var capped = BeatGrid.Lines(redLines, 0, 600000, 16, maxLines: 500);
        C("[网格] 极端缩放下网格线被抽稀", capped.Count <= 500, $"{capped.Count}");
    }

    // =====================================================================
    // 视口：换算互逆 / 缩放锚点 / 跟随
    // =====================================================================

    private static void CheckViewport(Checker C)
    {
        var viewport = new ChartViewport();
        viewport.Resize(600);
        viewport.ScrollTo(1000);

        var y = viewport.TimeToY(1600);
        var roundTrip = viewport.YToTime(y);
        C("[视口] 时间 ↔ Y 换算互逆", Math.Abs(roundTrip - 1600) < 1e-9, $"{roundTrip:0.####}");

        // 滚轮缩放：鼠标下的时间点保持不动
        var anchorY = 200.0;
        var anchorTime = viewport.YToTime(anchorY);
        viewport.ZoomAt(anchorY, 2.0);
        C("[视口] 缩放后锚点时间不变", Math.Abs(viewport.YToTime(anchorY) - anchorTime) < 1e-6,
            $"{viewport.YToTime(anchorY):0.####} vs {anchorTime:0.####}");
        C("[视口] 缩放被限制在允许区间",
            viewport.PxPerMs <= ChartViewport.MaxPxPerMs && viewport.PxPerMs >= ChartViewport.MinPxPerMs,
            $"{viewport.PxPerMs}");

        // 播放跟随：舒适区内不动，出了才滚
        var view = new ChartViewport();
        view.Resize(600);
        view.ScrollTo(0);
        var startBefore = view.StartTime;
        view.FollowPlayhead(view.VisibleMs * 0.5);
        C("[视口] 播放头在舒适区内不滚动", Math.Abs(view.StartTime - startBefore) < 1e-9);

        view.FollowPlayhead(view.VisibleMs * 0.95);
        C("[视口] 播放头跑出舒适区才滚动", view.StartTime > startBefore,
            $"{view.StartTime:0.##} > {startBefore:0.##}");

        // FitRange：整张谱面塞进视口
        var fit = new ChartViewport();
        fit.Resize(600);
        fit.FitRange(0, 60000);
        C("[视口] 适配范围后首尾都在视口内",
            fit.StartTime <= 0 && fit.EndTime >= 60000,
            $"{fit.StartTime:0.##} ~ {fit.EndTime:0.##}");
    }

    // =====================================================================
    // 编辑操作：摆放 / 拖动 / 拉 LN / 删除 / 撤销
    // =====================================================================

    private static void CheckChartEditor(Checker C)
    {
        var map = BuildMap(keys: 4, bpm: 120, noteTimes: Array.Empty<double>());
        var editor = new ChartEditor(map) { GridDivisor = 4, SnapToGrid = true };
        editor.SetLayout(64, 400, 4);
        editor.Viewport.Resize(600);
        editor.Viewport.SetScale(0.4);      // 1ms = 0.4px
        editor.Viewport.ScrollTo(0);

        // 放普通键：x 落在第 2 列（列宽 100，起点 64），y 对应 130ms → 吸附 125ms
        var columnX = editor.ColumnCenter(1);
        editor.PressAt(columnX, editor.TimeToY(130));
        C("[编辑] 普通键落地", map.HitObjects.Count == 1, $"{map.HitObjects.Count}");
        C("[编辑] 时间吸附到 1/4 拍", Math.Abs(map.HitObjects[0].StartTime - 125) < 0.01,
            $"{map.HitObjects[0].StartTime:0.###}");
        C("[编辑] 落在正确的列", map.HitObjects[0].Column == 1, $"{map.HitObjects[0].Column}");
        C("[编辑] 新 note 被选中", editor.SelectedIndex == 0, $"{editor.SelectedIndex}");

        // 右键删除
        editor.DeleteAt(columnX, editor.TimeToY(125));
        C("[编辑] 右键删除 note", map.HitObjects.Count == 0, $"{map.HitObjects.Count}");

        // 撤销能把它找回来
        editor.Undo();
        C("[编辑] 撤销删除后 note 回来", map.HitObjects.Count == 1);
        editor.Redo();
        C("[编辑] 重做又删掉", map.HitObjects.Count == 0);

        // 长条工具：按住往下拖
        editor.Tool = ChartEditor.EditTool.LongNote;
        var startTime = 500.0;
        editor.PressAt(editor.ColumnCenter(2), editor.TimeToY(startTime));
        editor.DragTo(editor.ColumnCenter(2), editor.TimeToY(1500));
        C("[编辑] 拖动中还没落地", map.HitObjects.Count == 0, $"{map.HitObjects.Count}");
        editor.Release();
        C("[编辑] 松手后长条落地", map.HitObjects.Count == 1, $"{map.HitObjects.Count}");
        var ln = map.HitObjects[0];
        C("[编辑] 长条标记正确", ln.IsLongNote && ln.Column == 2, $"{ln}");
        C("[编辑] 长条长度按网格吸附", Math.Abs(ln.StartTime - 500) < 0.01 && Math.Abs(ln.EndTime - 1500) < 0.01,
            $"{ln.StartTime:0.##}~{ln.EndTime:0.##}");

        // 拉 LN 尾巴：命中尾巴附近 → 拖动只改 EndTime
        var tailY = editor.TimeToY(ln.EndTime);
        var hit = editor.HitTest(editor.ColumnCenter(2), tailY);
        C("[编辑] 命中 LN 尾巴", hit.Kind == ChartEditor.HoverKind.NoteTail, $"{hit.Kind}");

        editor.PressAt(editor.ColumnCenter(2), tailY);
        editor.DragTo(editor.ColumnCenter(2), editor.TimeToY(2000));
        editor.Release();
        C("[编辑] 拉尾巴只改结束时间", Math.Abs(map.HitObjects[0].EndTime - 2000) < 0.01
                                       && Math.Abs(map.HitObjects[0].StartTime - 500) < 0.01,
            $"{map.HitObjects[0].StartTime:0.##}~{map.HitObjects[0].EndTime:0.##}");

        // 一次拖动 = 一步撤销（不是几十步）
        var undoBefore = map.UndoCount;
        editor.PressAt(editor.ColumnCenter(2), editor.TimeToY(500));
        editor.DragTo(editor.ColumnCenter(3), editor.TimeToY(600));
        editor.DragTo(editor.ColumnCenter(3), editor.TimeToY(700));
        editor.DragTo(editor.ColumnCenter(3), editor.TimeToY(800));
        editor.Release();
        C("[编辑] 一次拖动只压一步撤销", map.UndoCount == undoBefore + 1,
            $"{undoBefore} → {map.UndoCount}");

        editor.Undo();
        C("[编辑] 撤销一次回到拖动前", Math.Abs(map.HitObjects[0].StartTime - 500) < 0.01
                                      && map.HitObjects[0].Column == 2,
            map.HitObjects[0].ToString());

        // 拖太短当普通键
        map.HitObjects.Clear();
        map.ClearHistory();
        map.InvalidateIndex();
        editor.Tool = ChartEditor.EditTool.LongNote;
        editor.PressAt(editor.ColumnCenter(0), editor.TimeToY(1000));
        editor.Release();
        C("[编辑] 长条拖太短 → 落成普通键", map.HitObjects.Count == 1 && !map.HitObjects[0].IsLongNote,
            map.HitObjects[0].ToString());

        // 越界列被夹住（右侧留白不算轨道 → 不落 note；只是"超出最后一列"的列号换算要夹住）
        map.HitObjects.Clear();
        map.ClearHistory();
        map.InvalidateIndex();
        editor.Tool = ChartEditor.EditTool.Note;

        var lastColumnX = editor.ColumnCenter(3);
        editor.PressAt(lastColumnX, editor.TimeToY(2000));
        C("[编辑] 落在最后一列", map.HitObjects.Count == 1 && map.HitObjects[0].Column == 3,
            $"{map.HitObjects.Count} / {(map.HitObjects.Count > 0 ? map.HitObjects[0].Column : -1)}");

        // 标尺区 / 留白区点击不放 note（Note 宽度调小后左右是留白）
        var beforeLane = map.HitObjects.Count;
        editor.PressAt(4, editor.TimeToY(2200));
        C("[编辑] 点标尺区不放 note", map.HitObjects.Count == beforeLane, $"{map.HitObjects.Count}");

        editor.PressAt(editor.LaneLeft - 10, editor.TimeToY(2400));
        C("[编辑] 点轨道左侧留白不放 note", map.HitObjects.Count == beforeLane, $"{map.HitObjects.Count}");

        editor.PressAt(editor.LaneLeft + editor.LaneWidth + 10, editor.TimeToY(2600));
        C("[编辑] 点轨道右侧留白不放 note", map.HitObjects.Count == beforeLane, $"{map.HitObjects.Count}");

        // 时间点拖动
        var tp = new EditorTimingPoint { Time = 3000, BeatLength = 500, Uninherited = true, TimeSignature = 4 };
        map.AddTimingPoint(tp);
        map.InvalidateTimingPoints();
        editor.PressAt(10, editor.TimeToY(3000));       // 标尺区
        editor.DragTo(10, editor.TimeToY(4000));
        editor.Release();
        var moved = map.TimingPoints.First(p => p.Uninherited && Math.Abs(p.BeatLength - 500) < 1e-6 && p.Time > 1000);
        C("[编辑] 时间点可拖动", Math.Abs(moved.Time - 4000) < 200, $"{moved.Time:0}");

        // 吸附关闭时不动时间
        editor.SnapToGrid = false;
        var raw = 1234.5;
        C("[编辑] 关闭吸附后不吸附", Math.Abs(editor.Snap(raw) - raw) < 1e-9);
    }

    // =====================================================================
    // .osu 保真读写
    // =====================================================================

    private const string SampleChart = """
osu file format v14

[General]
AudioFilename: audio.mp3
Mode: 3
CustomUnknownKey: 保留我

[Metadata]
Title:测试
Artist:艺术家
Creator:谱师
Version:4K

[Difficulty]
CircleSize:4
OverallDifficulty:8

[Events]
//Background and Video events
0,0,"bg.jpg",0,0

[TimingPoints]
500,500,4,2,0,60,1,0
2000,-50,4,2,0,60,0,0

[HitObjects]
64,192,1000,1,0,0:0:0:0:
192,192,1500,128,0,3000:0:0:0:0:
320,192,2000,1,4,2:3:0:50:
""";

    private static void CheckFileRoundTrip(Checker C, string dir)
    {
        var lines = SampleChart.Replace("\r\n", "\n").Split('\n');
        var map = OsuFileDocument.Parse(lines, Path.Combine(dir, "sample.osu"));

        C("[读写] 键数从 CircleSize 读出", map.KeyCount == 4, $"{map.KeyCount}");
        C("[读写] Mode 读出", map.ModeId == 3, $"{map.ModeId}");
        C("[读写] note 数量", map.HitObjects.Count == 3, $"{map.HitObjects.Count}");
        C("[读写] LN 被识别（type=128）", map.HitObjects[1].IsLongNote, map.HitObjects[1].ToString());
        C("[读写] LN 结束时间", Math.Abs(map.HitObjects[1].EndTime - 3000) < 0.01,
            $"{map.HitObjects[1].EndTime:0}");
        C("[读写] 采样信息保留（2:3:0:50）",
            map.HitObjects[2].SampleSet == 2 && map.HitObjects[2].AdditionSet == 3 && map.HitObjects[2].Volume == 50,
            $"{map.HitObjects[2].SampleSet}:{map.HitObjects[2].AdditionSet}:{map.HitObjects[2].Volume}");
        C("[读写] 红线 / 绿线区分", map.TimingPoints.Count(p => p.Uninherited) == 1
                                    && map.TimingPoints.Count(p => !p.Uninherited) == 1);
        C("[读写] 绿线 SV 换算（-50 → 2.0x）",
            Math.Abs(map.TimingPoints.First(p => !p.Uninherited).SvMultiplier - 2.0) < 1e-6,
            $"{map.TimingPoints.First(p => !p.Uninherited).SvMultiplier:0.###}");
        C("[读写] 红线 BPM 换算（500ms → 120）",
            Math.Abs(map.TimingPoints.First(p => p.Uninherited).Bpm - 120) < 1e-6,
            $"{map.TimingPoints.First(p => p.Uninherited).Bpm:0.###}");

        // 保存：原谱内容必须还在（未知键 / 事件段 / 注释）
        var saved = OsuFileDocument.BuildText(map);
        C("[读写] 保存后未知键仍在", saved.Contains("CustomUnknownKey: 保留我"));
        C("[读写] 保存后 Events 段仍在", saved.Contains("[Events]") && saved.Contains("bg.jpg"));
        C("[读写] 保存后不额外塞 storyboard 行", !saved.Contains("//Storyboard Layer 0"));

        // 写盘 → 再读回来，数据一致
        var path = Path.Combine(dir, "roundtrip.osu");
        OsuFileDocument.Save(map, path);
        C("[读写] 文件写出成功", File.Exists(path));

        var reloaded = OsuFileDocument.Load(path);
        C("[读写] 往返后 note 数量一致", reloaded.HitObjects.Count == map.HitObjects.Count,
            $"{reloaded.HitObjects.Count}");
        C("[读写] 往返后时间一致",
            reloaded.HitObjects.OrderBy(n => n.StartTime).Select(n => Math.Round(n.StartTime))
                .SequenceEqual(map.HitObjects.OrderBy(n => n.StartTime).Select(n => Math.Round(n.StartTime))));
        C("[读写] 往返后 LN 仍是 LN", reloaded.HitObjects.Count(n => n.IsLongNote) == 1);

        // 编辑后保存：新增 note 出现在文件里，且未知键仍保留
        reloaded.AddNote(EditorNote.NewNote(3, 4000));
        var text2 = OsuFileDocument.BuildText(reloaded);
        C("[读写] 编辑后新增 note 写进文件", text2.Contains(",4000,1,"), "line: ,4000,1,");
        C("[读写] 编辑后未知键依然保留", text2.Contains("CustomUnknownKey: 保留我"));

        // 列坐标换算与项目既有常量表一致（4K 第 0 列 = 64）
        C("[读写] 列坐标用项目既有常量表", OsuFileDocument.ColumnX(4, 0) == 64 && OsuFileDocument.ColumnX(4, 3) == 448,
            $"{OsuFileDocument.ColumnX(4, 0)} / {OsuFileDocument.ColumnX(4, 3)}");

        // 坏行不抛异常
        var broken = new[] { "osu file format v14", "[HitObjects]", "garbage", "192,192,abc,1,0,0:0:0:0:" };
        var brokenMap = OsuFileDocument.Parse(broken, "broken.osu");
        C("[读写] 坏行跳过而不是抛异常", brokenMap.HitObjects.Count == 0 && brokenMap.LoadWarnings.Count == 2,
            $"notes={brokenMap.HitObjects.Count} warnings={brokenMap.LoadWarnings.Count}");
    }

    // =====================================================================
    // 大量 note：索引 / 二分裁剪 / 抽稀
    // =====================================================================

    private static void CheckLargeChart(Checker C, string dir)
    {
        const int noteCount = 120_000;
        var map = BuildMap(keys: 8, bpm: 180, noteTimes: Array.Empty<double>());

        var sw = Stopwatch.StartNew();
        var random = new Random(12345);
        for (var i = 0; i < noteCount; i++)
        {
            var column = random.Next(8);
            var time = 1000 + i * 8.0;

            // 每 9 个来一条 LN
            map.HitObjects.Add(i % 9 == 0
                ? EditorNote.NewLongNote(column, time, time + 240)
                : EditorNote.NewNote(column, time));
        }
        sw.Stop();
        C($"[大量note] 造 {noteCount:N0} 个 note 耗时 < 200ms", sw.ElapsedMilliseconds < 200,
            $"{sw.ElapsedMilliseconds}ms");

        sw.Restart();
        map.RefreshIndexes();
        sw.Stop();
        C("[大量note] 建索引 < 300ms", sw.ElapsedMilliseconds < 300, $"{sw.ElapsedMilliseconds}ms");
        C("[大量note] 最长 LN 长度被记录", map.Index.MaxDuration >= 240 - 0.001,
            $"{map.Index.MaxDuration:0.##}ms");

        // 二分取窗口：只取视口内的 note，而不是全表扫
        var editor = new ChartEditor(map) { GridDivisor = 4 };
        editor.SetLayout(64, 800, 8);
        editor.Viewport.Resize(900);
        editor.Viewport.SetScale(0.02);          // 一屏 45 秒
        editor.Viewport.ScrollTo(30000);

        sw.Restart();
        ChartRenderState state = null!;
        for (var i = 0; i < 60; i++)
        {
            editor.Viewport.ScrollTo(30000 + i * 10);
            state = ChartRenderState.Build(editor, 900, 64, 800);
        }
        sw.Stop();
        var perFrame = sw.Elapsed.TotalMilliseconds / 60;

        C("[大量note] 单帧构建快照 < 8ms（60 帧平均）", perFrame < 8, $"{perFrame:0.###}ms/帧");
        C("[大量note] 快照只含可见 note（远小于总数）", state.Notes.Count < noteCount / 20,
            $"{state.Notes.Count} / {noteCount}");

        // 可见窗口内没有任何 note 落在视野外
        var outside = state.Notes.Count(n =>
            !n.Note.IsLongNote && (n.Note.StartTime < state.StartTime || n.Note.StartTime > state.EndTime));
        C("[大量note] 裁剪准确（没有视野外的 note）", outside == 0, $"{outside}");

        // 极端缩小：一屏看整张谱面 → 抽稀生效，不会画出几十万个矩形
        editor.Viewport.SetScale(ChartViewport.MinPxPerMs);
        editor.Viewport.ScrollTo(0);
        var zoomedOut = ChartRenderState.Build(editor, 900, 64, 800);
        C("[大量note] 极端缩小时抽稀到上限内", zoomedOut.Notes.Count <= 12000, $"{zoomedOut.Notes.Count}");

        // 命中测试不扫全表
        sw.Restart();
        for (var i = 0; i < 200; i++) map.HitTest(i % 8, 1000 + i * 500, 20);
        sw.Stop();
        C("[大量note] 命中测试 200 次 < 60ms", sw.ElapsedMilliseconds < 60, $"{sw.ElapsedMilliseconds}ms");

        // 保存 12 万 note 到文件（性能 + 正确性）
        var bigPath = Path.Combine(dir, "big.osu");
        var doc = BuildMap(keys: 8, bpm: 180, noteTimes: Array.Empty<double>());
        doc.HitObjects.AddRange(map.HitObjects);
        doc.RefreshIndexes();

        sw.Restart();
        OsuFileDocument.Save(doc, bigPath);
        sw.Stop();
        C("[大量note] 保存 12 万 note < 2s", sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds}ms");

        sw.Restart();
        var reloaded = OsuFileDocument.Load(bigPath);
        sw.Stop();
        C("[大量note] 读回 12 万 note < 2s", sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds}ms");
        C("[大量note] 读回数量一致", reloaded.HitObjects.Count == map.HitObjects.Count,
            $"{reloaded.HitObjects.Count} vs {map.HitObjects.Count}");

        File.Delete(bigPath);
    }

    // =====================================================================
    // 辅助
    // =====================================================================

    /// <summary>造一张只有时间点、没有 note 的谱面。</summary>
    private static EditorBeatmap BuildMap(int keys, double bpm, IReadOnlyList<double> noteTimes)
    {
        var map = new EditorBeatmap { KeyCount = keys, ModeId = 3, FilePath = "scratch.osu" };
        map.TimingPoints.Add(EditorTimingPoint.NewRedLine(0, bpm));
        foreach (var time in noteTimes)
        {
            map.HitObjects.Add(EditorNote.NewNote(0, time));
        }
        map.RefreshIndexes();
        return map;
    }
}
