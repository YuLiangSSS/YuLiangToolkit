using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Live2D;
using YuLiangToolkit.Tools.Live2D.Ai;
using YuLiangToolkit.Tools.Live2D.Model;
using YuLiangToolkit.Tools.Live2D.Play;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「Live2D 桌宠 · 养成（饥饿 / 金币 / 每日任务 / 投喂）」自检：全部纯逻辑、无窗口、不联网、
/// 不碰真实 <c>Data</c> / <c>Config</c>（落盘走自检临时 <c>YLT_HOME</c> 下的 <see cref="ToolStorage"/>）。
///
/// <para><b>覆盖范围</b>（对应任务 7 大类）：</para>
/// <list type="number">
///   <item>投喂台词（<see cref="FeedingLines"/>）：饱食度档位阈值 / 中文名 / 吃完后台词与称呼替换 /
///         饿的提醒 / 表情关键词；</item>
///   <item>饭团物理（<see cref="RiceBallPhysics"/>）：重力 / 地面与墙壁反弹 / 拖动与已吃掉不推进 /
///         贴地 1200 帧归零 / 松手速度（含超速夹取与采样数）/ 嘴部线段求交 / 米粒生成与推进；</item>
///   <item>养成服务（<see cref="PetCareService"/>）：初始值 / 整小时掉值补结算 / 封底 / 手动·锁定·总开关 /
///         手动 SetFullness / 吃饭加饱食度与封顶 / 锁定与关闭时的好感度增量；</item>
///   <item>金币与饭团商店：买 / 出库 / 金币不足 / 锁定 / 总开关 / 库存封顶 / 手动改值夹取；</item>
///   <item>每日任务：3 个任务目录 / 摸摸头 3 次 +6 只发一次 / 聊天 +4 / 投喂 +8 与提示 / 金币锁定与自然模式关 /
///         任务总开关 / 跨天 Tick 清零；</item>
///   <item>养成持久化：落盘 <c>care-state.json</c> / 新服务读回 / 坏 JSON 回退 / 手改坏数值归一化；</item>
///   <item>吃饭状态机（<see cref="FeedingController"/>）：凑近张嘴 / 回落 / 咀嚼波动 / 吞咽 / Reset。</item>
/// </list>
///
/// <para>另外还断言投喂加好感度（<see cref="AffectionEngine.ApplyDirectGain"/> 与好感度锁定）、
/// <see cref="AiChatService.AddAffection"/> / <c>AppendNotice</c> / <c>AppendLocalPetMessage</c>、
/// 以及 <see cref="Live2DConfig"/> 的 <c>Pet*</c> 配置默认值与 JSON 往返。</para>
/// </summary>
internal static class Live2DCareChecks
{
    /// <summary>自检用临时数据目录名（挂在自检临时 YLT_HOME 下，不碰真实 Data）。</summary>
    private const string TempToolId = "live2d";

    /// <summary>养成状态落盘文件名（相对 <c>Data\live2d\</c>）。</summary>
    private const string CareStateFileName = "care-state.json";

    /// <summary>聊天状态落盘文件（相对 <c>Data\live2d\</c>；投喂好感度 / 本地台词也走它）。</summary>
    private const string ChatStateFileName = "ai/chat-state.json";

    /// <summary>固定假时钟（+08:00，和项目其它自检一致）：掉值 / 跨天 / 任务都用它注入，不依赖真实时间。</summary>
    private static readonly DateTimeOffset FakeNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(8));

    public static void Run(Action<string, bool, string> check, Action<string, bool> checkSimple)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        Safe(C, "投喂台词", () => CheckFeedingLines(C));
        Safe(C, "饭团目录", () => CheckRiceBallCatalog(C));
        Safe(C, "饭团物理", () => CheckRiceBallPhysics(C));
        Safe(C, "饱食度", () => CheckCareFullness(C));
        Safe(C, "金币与商店", () => CheckCareShop(C));
        Safe(C, "每日任务", () => CheckDailyTasks(C));
        Safe(C, "养成持久化", () => CheckCarePersistence(C));
        Safe(C, "吃饭状态机", () => CheckFeedingController(C));
        Safe(C, "好感度锁定", () => CheckAffectionLock(C));
        Safe(C, "聊天服务投喂接口", () => CheckChatServiceCareApi(C));
        Safe(C, "养成配置", () => CheckCareConfig(C));
    }

    // ====================================================================
    // 断言分组（组内异常只记一条 FAIL，不影响其它组）
    // ====================================================================

    /// <summary>运行一组断言；组内抛异常时只记一条 FAIL（和 AI 聊天自检同一写法）。</summary>
    private static void Safe(CheckFn C, string label, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            C($"养成：{label}断言运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- ① 投喂台词

    private static void CheckFeedingLines(CheckFn C)
    {
        C("养成：饱食度档位阈值（19.9 很饿 / 20 没吃饱 / 44.9 / 45 正常 / 79.9 / 80 很饱）",
            FeedingLines.ResolveTier(19.9) == FullnessTier.Starving
            && FeedingLines.ResolveTier(20) == FullnessTier.Hungry
            && FeedingLines.ResolveTier(44.9) == FullnessTier.Hungry
            && FeedingLines.ResolveTier(45) == FullnessTier.Normal
            && FeedingLines.ResolveTier(79.9) == FullnessTier.Normal
            && FeedingLines.ResolveTier(80) == FullnessTier.Full,
            "19.9/Starving、20/Hungry、45/Normal、80/Full");

        C("养成：档位中文名（很饿 / 没吃饱 / 正常 / 很饱）",
            FeedingLines.TierText(FullnessTier.Starving) == "很饿"
            && FeedingLines.TierText(FullnessTier.Hungry) == "没吃饱"
            && FeedingLines.TierText(FullnessTier.Normal) == "正常"
            && FeedingLines.TierText(FullnessTier.Full) == "很饱");

        // 台词池是随机的：跑多次确保每个档位都能抽到带 / 不带 {0} 的句子
        var afterEatOk = true;
        var afterEatFallbackSeen = new bool[4];
        var tiers = new[] { FullnessTier.Starving, FullnessTier.Hungry, FullnessTier.Normal, FullnessTier.Full };
        for (var i = 0; i < 80; i++)
        {
            foreach (var tier in tiers)
            {
                var line = FeedingLines.PickAfterEat(tier, "阿凉", new Random(1000 + i));
                if (string.IsNullOrWhiteSpace(line) || line.Contains("{0}", StringComparison.Ordinal))
                    afterEatOk = false;

                var fallback = FeedingLines.PickAfterEat(tier, "   ", new Random(2000 + i));
                if (string.IsNullOrWhiteSpace(fallback) || fallback.Contains("{0}", StringComparison.Ordinal))
                    afterEatOk = false;
                if (fallback.Contains("主人", StringComparison.Ordinal)) afterEatFallbackSeen[(int)tier] = true;
            }
        }

        C("养成：吃完后各档台词非空、{0} 占位全部被称呼替换（无残留）", afterEatOk);
        C("养成：称呼为空 / 空白时回退「主人」（4 个档位都抽到过）", afterEatFallbackSeen.All(seen => seen));

        var reminderOk = true;
        var reminderFallbackSeen = false;
        for (var i = 0; i < 80; i++)
        {
            var line = FeedingLines.PickHungryReminder("阿凉", new Random(3000 + i));
            if (string.IsNullOrWhiteSpace(line) || line.Contains("{0}", StringComparison.Ordinal)) reminderOk = false;
            var fallback = FeedingLines.PickHungryReminder(null, new Random(4000 + i));
            if (string.IsNullOrWhiteSpace(fallback) || fallback.Contains("{0}", StringComparison.Ordinal)) reminderOk = false;
            if (fallback.Contains("主人", StringComparison.Ordinal)) reminderFallbackSeen = true;
        }

        C("养成：饿的提醒非空且称呼替换正确", reminderOk);
        C("养成：饿的提醒称呼为空时回退「主人」", reminderFallbackSeen);

        C("养成：投喂台词 → 表情关键词（很饱 ＞＜ / 很饿 流冷汗 / 没吃饱吃完 流泪 / 正常吃完 ＞＜ / 平时空）",
            FeedingLines.GetExpressionKeyword(FullnessTier.Full, afterEat: true) == "＞＜"
            && FeedingLines.GetExpressionKeyword(FullnessTier.Full, afterEat: false) == "＞＜"
            && FeedingLines.GetExpressionKeyword(FullnessTier.Starving, afterEat: true) == "流冷汗"
            && FeedingLines.GetExpressionKeyword(FullnessTier.Starving, afterEat: false) == "流冷汗"
            && FeedingLines.GetExpressionKeyword(FullnessTier.Hungry, afterEat: true) == "流泪"
            && FeedingLines.GetExpressionKeyword(FullnessTier.Hungry, afterEat: false) == ""
            && FeedingLines.GetExpressionKeyword(FullnessTier.Normal, afterEat: true) == "＞＜"
            && FeedingLines.GetExpressionKeyword(FullnessTier.Normal, afterEat: false) == "");
    }

    // ---------------------------------------------------------------- ② 饭团目录 / 外观

    private static void CheckRiceBallCatalog(CheckFn C)
    {
        var foods = RiceBallCatalog.Foods;
        C("养成：4 种大白饭团数值（三角 10/饱食15/好感1、球形 8/12/1、太鼓 12/18/2、俵形 15/22/3）",
            RiceBallCatalog.Count == 4
            && foods.Count == 4
            && foods[0] is { Kind: RiceBallKind.Triangle, Name: "三角形饭团", Price: 10, FullnessGain: 15d, AffectionGain: 1d }
            && foods[1] is { Kind: RiceBallKind.Sphere, Name: "球形饭团", Price: 8, FullnessGain: 12d, AffectionGain: 1d }
            && foods[2] is { Kind: RiceBallKind.Taiko, Name: "太鼓形饭团", Price: 12, FullnessGain: 18d, AffectionGain: 2d }
            && foods[3] is { Kind: RiceBallKind.Bale, Name: "俵形饭团", Price: 15, FullnessGain: 22d, AffectionGain: 3d }
            && foods.All(food => food.Description.Length > 10),
            string.Join("、", foods.Select(food => $"{food.Name}:{food.Price}/{food.FullnessGain}/{food.AffectionGain}")));

        C("养成：饭团形状字符（△ / ○ / ▭ / ⬭，未知类型回落 🍙）",
            RiceBallCatalog.Get(RiceBallKind.Triangle).Glyph == "△"
            && RiceBallCatalog.Get(RiceBallKind.Sphere).Glyph == "○"
            && RiceBallCatalog.Get(RiceBallKind.Taiko).Glyph == "▭"
            && RiceBallCatalog.Get(RiceBallKind.Bale).Glyph == "⬭"
            && RiceBallCatalog.Get((RiceBallKind)99).Kind == RiceBallKind.Triangle);

        C("养成：饭团视觉半径（三角 27 / 球形 24 / 太鼓 27 / 俵形 30）",
            Near(RiceBallPhysics.GetRadius(RiceBallKind.Triangle), 27)
            && Near(RiceBallPhysics.GetRadius(RiceBallKind.Sphere), 24)
            && Near(RiceBallPhysics.GetRadius(RiceBallKind.Taiko), 27)
            && Near(RiceBallPhysics.GetRadius(RiceBallKind.Bale), 30)
            && Near(RiceBallPhysics.GetRadius((RiceBallKind)99), 26));
    }

    // ---------------------------------------------------------------- ③ 饭团物理

    private static void CheckRiceBallPhysics(CheckFn C)
    {
        C("养成物理：常量（重力 2200 / 反弹 0.45 / 地面摩擦 0.86 / 空气阻力 0.9997 / 最大速度 2600）",
            Near(RiceBallPhysics.Gravity, 2200)
            && Near(RiceBallPhysics.Restitution, 0.45)
            && Near(RiceBallPhysics.FloorFriction, 0.86)
            && Near(RiceBallPhysics.AirDrag, 0.9997)
            && Near(RiceBallPhysics.MaxThrowSpeed, 2600));

        var freeFall = new RiceBallBody { Kind = RiceBallKind.Sphere, X = 200, Y = 100 };
        RiceBallPhysics.Step(freeFall, 1.0 / 60.0, 800, 600);
        C("养成物理：一帧自由落体 vy ≈ 重力 / 60（2200/60 ≈ 36.7）",
            Near(freeFall.VelocityY, RiceBallPhysics.Gravity / 60.0, 0.5),
            freeFall.VelocityY.ToString("0.###"));

        // 地面：贴地 + 竖直反向 × 0.45 + 水平摩擦
        var floor = new RiceBallBody
        {
            Kind = RiceBallKind.Sphere, X = 100, Y = 600 - 24 - 1, VelocityX = 300, VelocityY = 500,
        };
        RiceBallPhysics.Step(floor, 1.0 / 60.0, 800, 600);
        var impact = 500 + RiceBallPhysics.Gravity / 60.0;
        C("养成物理：落到地面贴地 y = 高度 - 半径，竖直速度反向 × 0.45",
            Near(floor.Y, 600 - 24) && floor.VelocityY < 0
            && Near(-floor.VelocityY, impact * RiceBallPhysics.Restitution, 60),
            $"y={floor.Y:0.##} vy={floor.VelocityY:0.##}");
        C("养成物理：落地后水平速度按地面摩擦变慢（约保留 0.997）",
            floor.VelocityX < 300 && Near(floor.VelocityX, 299.2, 1.0),
            floor.VelocityX.ToString("0.##"));

        // 左右墙：位置夹回 + 水平速度反向
        var leftWall = new RiceBallBody { Kind = RiceBallKind.Sphere, X = 24 - 5, Y = 100, VelocityX = -300 };
        RiceBallPhysics.Step(leftWall, 1.0 / 60.0, 800, 600);
        C("养成物理：撞左墙夹回半径处、水平速度反向 × 0.45",
            Near(leftWall.X, 24) && leftWall.VelocityX > 0
            && Near(leftWall.VelocityX, 300 * RiceBallPhysics.Restitution, 1.5),
            $"x={leftWall.X:0.##} vx={leftWall.VelocityX:0.##}");

        var rightWall = new RiceBallBody { Kind = RiceBallKind.Bale, X = 800 - 30 + 5, Y = 100, VelocityX = 300 };
        RiceBallPhysics.Step(rightWall, 1.0 / 60.0, 800, 600);
        C("养成物理：撞右墙夹回（宽 - 半径）处、水平速度反向 × 0.45",
            Near(rightWall.X, 800 - 30) && rightWall.VelocityX < 0
            && Near(rightWall.VelocityX, -300 * RiceBallPhysics.Restitution, 1.5),
            $"x={rightWall.X:0.##} vx={rightWall.VelocityX:0.##}");

        var ceiling = new RiceBallBody { Kind = RiceBallKind.Sphere, X = 100, Y = 24 - 1, VelocityY = -100 };
        RiceBallPhysics.Step(ceiling, 1.0 / 60.0, 800, 600);
        C("养成物理：撞天花板轻弹一下（夹回半径处、竖直速度反向 × 0.3）",
            Near(ceiling.Y, 24) && ceiling.VelocityY > 0 && ceiling.VelocityY < 40,
            $"y={ceiling.Y:0.##} vy={ceiling.VelocityY:0.##}");

        // 拖动中 / 已吃掉 / 非法 dt：物理不推进
        var dragging = new RiceBallBody
        {
            Kind = RiceBallKind.Triangle, X = 100, Y = 100, VelocityX = 50, VelocityY = -20, IsDragging = true,
        };
        var consumed = new RiceBallBody
        {
            Kind = RiceBallKind.Triangle, X = 200, Y = 200, VelocityX = 50, VelocityY = -20, Consumed = true,
        };
        RiceBallPhysics.Step(dragging, 1.0 / 60.0, 800, 600);
        RiceBallPhysics.Step(consumed, 1.0 / 60.0, 800, 600);
        var frozen = new RiceBallBody { Kind = RiceBallKind.Sphere, X = 100, Y = 100, VelocityY = 10 };
        RiceBallPhysics.Step(frozen, 0, 800, 600);
        RiceBallPhysics.Step(frozen, double.NaN, 800, 600);
        C("养成物理：拖动中 / 已吃掉 / dt ≤ 0 或 NaN 时不推进",
            Near(dragging.X, 100) && Near(dragging.Y, 100) && Near(dragging.VelocityX, 50) && Near(dragging.VelocityY, -20)
            && Near(consumed.X, 200) && Near(consumed.Y, 200) && Near(consumed.VelocityY, -20)
            && Near(frozen.X, 100) && Near(frozen.Y, 100) && Near(frozen.VelocityY, 10));

        // 贴地后速度归零（避免贴地无限小抖动）
        var resting = new RiceBallBody
        {
            Kind = RiceBallKind.Sphere, X = 200, Y = 600 - 24, VelocityX = 4, VelocityY = 10,
        };
        for (var i = 0; i < 1200; i++) RiceBallPhysics.Step(resting, 1.0 / 60.0, 800, 600);
        C("养成物理：贴地 1200 帧后速度归零、位置不再抖",
            Near(resting.VelocityY, 0) && Near(resting.VelocityX, 0) && Near(resting.Y, 600 - 24),
            $"y={resting.Y:0.##} vx={resting.VelocityX:0.###} vy={resting.VelocityY:0.###}");

        // 松手速度：两点差分 → (1000, 500)
        var trail = new[]
        {
            new RiceBallDragSample(0, 0, 0),
            new RiceBallDragSample(100, 50, 0.1),
        };
        var (throwX, throwY) = RiceBallPhysics.ComputeThrowVelocity(trail, 0.1);
        C("养成物理：拖拽轨迹两点 → 松手速度 (1000, 500)",
            Near(throwX, 1000, 1) && Near(throwY, 500, 1),
            $"({throwX:0.#}, {throwY:0.#})");

        var fastTrail = new[]
        {
            new RiceBallDragSample(0, 0, 0),
            new RiceBallDragSample(1000, 1000, 0.1),
        };
        var (fastX, fastY) = RiceBallPhysics.ComputeThrowVelocity(fastTrail, 0.1);
        C("养成物理：超速按矢量长度夹到 2600（方向不变）",
            Near(Math.Sqrt(fastX * fastX + fastY * fastY), RiceBallPhysics.MaxThrowSpeed, 1)
            && Near(fastX, fastY, 1e-6),
            $"({fastX:0.#}, {fastY:0.#})");

        var (noneX, noneY) = RiceBallPhysics.ComputeThrowVelocity(Array.Empty<RiceBallDragSample>(), 0.1);
        var (oneX, oneY) = RiceBallPhysics.ComputeThrowVelocity(
            new[] { new RiceBallDragSample(10, 10, 0.05) }, 0.1);
        C("养成物理：空轨迹 / 只有 1 个采样点 → (0, 0)",
            Near(noneX, 0) && Near(noneY, 0) && Near(oneX, 0) && Near(oneY, 0));

        var three = new[]
        {
            new RiceBallDragSample(0, 0, 0),
            new RiceBallDragSample(100, 100, 0.1),
            new RiceBallDragSample(200, 300, 0.2),
        };
        var (twoX, twoY) = RiceBallPhysics.ComputeThrowVelocity(three, 0.2, sampleCount: 2);
        C("养成物理：sampleCount=2 只用最后两点（(100,100) → (200,300) / 0.1s = (1000, 2000)）",
            Near(twoX, 1000, 1) && Near(twoY, 2000, 1),
            $"({twoX:0.#}, {twoY:0.#})");

        var (allX, allY) = RiceBallPhysics.ComputeThrowVelocity(three, 0.2, sampleCount: 5);
        C("养成物理：sampleCount 超过轨迹长度时用全部点（首尾差分 = (1000, 1500)）",
            Near(allX, 1000, 1) && Near(allY, 1500, 1),
            $"({allX:0.#}, {allY:0.#})");

        var noTime = new[]
        {
            new RiceBallDragSample(0, 0, 0),
            new RiceBallDragSample(100, 50, 0),
        };
        var (nowX, nowY) = RiceBallPhysics.ComputeThrowVelocity(noTime, 0.1);
        C("养成物理：采样时间缺失（都为 0）时用松手时刻兜底",
            Near(nowX, 1000, 1) && Near(nowY, 500, 1),
            $"({nowX:0.#}, {nowY:0.#})");

        // 嘴部命中：线段与圆求交（含重力下坠项的二次近似）
        var hit = RiceBallPhysics.TryHitMouth(
            0, 0, 1000, 0, 0.1, 500, 50, 0, 20, out var hitT);
        C("养成物理：一帧穿过嘴 → 命中且 t ≈ 0.494",
            hit && Near(hitT, 0.494, 0.01),
            $"t={hitT:0.###}");

        var inside = RiceBallPhysics.TryHitMouth(
            50, 0, 0, 0, 1e-6, 500, 50, 0, 20, out var insideT);
        C("养成物理：起点已经在嘴里 → 命中且 t = 0",
            inside && Near(insideT, 0));

        C("养成物理：朝反方向 / 离嘴超过半径 → 不命中",
            !RiceBallPhysics.TryHitMouth(0, 0, -1000, 0, 0.1, 500, 50, 0, 20, out _)
            && !RiceBallPhysics.TryHitMouth(0, 0, 10, 0, 0.01, 500, 500, 0, 20, out _));

        C("养成物理：非法半径 / 非法时间 → 不命中且不抛异常",
            !RiceBallPhysics.TryHitMouth(0, 0, 1000, 0, 0.1, 500, 50, 0, 0, out _)
            && !RiceBallPhysics.TryHitMouth(0, 0, 1000, 0, 0.1, 500, 50, 0, double.NaN, out _)
            && !RiceBallPhysics.TryHitMouth(0, 0, 1000, 0, 0, 500, 50, 0, 20, out _));

        // 米饭散落粒子
        var crumbs = new List<RiceCrumb>();
        RiceBallPhysics.SpawnCrumbs(crumbs, 100, 200, 16, new Random(20260115));
        C("养成物理：吃掉撒 16 颗米粒（全部朝上 vy<0、速度 120~420、寿命 0.5~0.9、半径 1.2~2.6）",
            crumbs.Count == 16
            && crumbs.All(crumb => crumb.VelocityY < 0)
            && crumbs.All(crumb =>
            {
                var speed = Math.Sqrt(crumb.VelocityX * crumb.VelocityX + crumb.VelocityY * crumb.VelocityY);
                return speed >= 120 - 1e-6 && speed <= 420 + 1e-6;
            })
            && crumbs.All(crumb => crumb.Life >= 0.5 - 1e-9 && crumb.Life <= 0.9 + 1e-9 && Near(crumb.MaxLife, crumb.Life))
            && crumbs.All(crumb => crumb.Size >= 1.2 - 1e-9 && crumb.Size <= 2.6 + 1e-9)
            && crumbs.All(crumb => Near(crumb.X, 100) && Near(crumb.Y, 200)),
            $"共 {crumbs.Count} 颗");

        var falling = new RiceCrumb { X = 0, Y = 0, VelocityX = 10, VelocityY = 0, Life = 0.5, MaxLife = 0.5, Size = 2 };
        var fallingList = new List<RiceCrumb> { falling };
        RiceBallPhysics.StepCrumbs(fallingList, 0.1);
        C("养成物理：米粒重力只用 0.6 倍、寿命递减、透明度按寿命算",
            Near(falling.VelocityY, RiceBallPhysics.Gravity * 0.6 * 0.1, 0.5)
            && Near(falling.Life, 0.4)
            && Near(falling.X, 1.0)
            && Near(falling.Opacity, 0.8));

        var dying = new List<RiceCrumb>
        {
            new() { Life = 0.05, MaxLife = 0.5 },
            new() { Life = 1.0, MaxLife = 1.0 },
        };
        RiceBallPhysics.StepCrumbs(dying, 0.1);
        C("养成物理：寿命到 0 的米粒被移除（空列表不抛异常）",
            dying.Count == 1 && Near(dying[0].Life, 0.9)
            && SafeNoThrow(() => RiceBallPhysics.StepCrumbs(new List<RiceCrumb>(), 0.1))
            && SafeNoThrow(() => RiceBallPhysics.StepCrumbs(dying, 0)));
    }

    // ---------------------------------------------------------------- ④ 饱食度

    private static void CheckCareFullness(CheckFn C)
    {
        var decaySettings = new PetCareSettings { DailyTasksEnabled = false };
        using (var care = CreateCare(CreateStorage("live2d-care-check-decay"), () => decaySettings))
        {
            C("养成：新存档初始饱食度 70 / 金币 30",
                Near(care.Fullness, 70) && care.Coins == 30,
                $"{care.Fullness:0.#} / {care.Coins}");

            C("养成：StatusText 含饱食 / 金币 / 饭团 / 今日任务",
                care.StatusText.Contains("饱食", StringComparison.Ordinal)
                && care.StatusText.Contains("🪙", StringComparison.Ordinal)
                && care.StatusText.Contains("饭团", StringComparison.Ordinal)
                && care.StatusText.Contains("今日任务", StringComparison.Ordinal),
                care.StatusText);

            C("养成：档位 / 档位中文（70 = 正常）",
                care.Tier == FullnessTier.Normal && care.TierText == "正常");

            care.Tick(FakeNow);
            care.Tick(FakeNow.AddMinutes(59));
            C("养成：不满 1 小时不扣（+59 分钟仍 70）",
                Near(care.Fullness, 70), care.Fullness.ToString("0.#"));

            care.Tick(FakeNow.AddMinutes(120));
            C("养成：满 2 小时按整小时扣（70 → 62，4/小时）",
                Near(care.Fullness, 62), care.Fullness.ToString("0.#"));

            care.Tick(FakeNow.AddMinutes(120).AddHours(3));
            C("养成：一次补结算多段完整小时（再 3 小时 → 50）",
                Near(care.Fullness, 50), care.Fullness.ToString("0.#"));

            care.Tick(FakeNow.AddHours(100));
            C("养成：饱食度最低封底 0（不会掉成负数）",
                Near(care.Fullness, 0), care.Fullness.ToString("0.#"));
        }

        // 手动模式 / 锁定 / 总开关关闭：时间都不掉值
        var manualSettings = new PetCareSettings { FullnessNatural = false, DailyTasksEnabled = false };
        using (var care = CreateCare(CreateStorage("live2d-care-check-manual"), () => manualSettings))
        {
            care.Tick(FakeNow);
            care.Tick(FakeNow.AddHours(48));
            C("养成：自然模式关 → 时间不掉值（48 小时后仍 70）",
                Near(care.Fullness, 70), care.Fullness.ToString("0.#"));

            manualSettings = new PetCareSettings { FullnessLocked = true, DailyTasksEnabled = false };
            care.ApplySettings();
            care.Tick(FakeNow.AddHours(96));
            C("养成：锁定饱食度 → 时间不掉值（现读设置，再 48 小时仍 70）",
                Near(care.Fullness, 70), care.Fullness.ToString("0.#"));
        }

        var disabledSettings = new PetCareSettings { Enabled = false, DailyTasksEnabled = false };
        using (var care = CreateCare(CreateStorage("live2d-care-check-disabled"), () => disabledSettings))
        {
            care.Tick(FakeNow);
            care.Tick(FakeNow.AddHours(48));
            C("养成：总开关关闭 → 时间不掉值（48 小时后仍 70）",
                Near(care.Fullness, 70), care.Fullness.ToString("0.#"));
        }

        // 手动 → 自然：冻结期间的时间不补扣
        var resumeSettings = new PetCareSettings { FullnessNatural = false, DailyTasksEnabled = false };
        using (var care = CreateCare(CreateStorage("live2d-care-check-resume"), () => resumeSettings))
        {
            care.Tick(FakeNow);
            care.Tick(FakeNow.AddHours(48));
            resumeSettings = new PetCareSettings { FullnessNatural = true, DailyTasksEnabled = false };
            care.ApplySettings();
            care.Tick(FakeNow.AddHours(48).AddMinutes(10));
            C("养成：从手动切回自然模式后短时间不补扣（+10 分钟仍 70）",
                Near(care.Fullness, 70), care.Fullness.ToString("0.#"));
        }

        // 吃饭：加饱食度 / 封顶 / 锁定不改但好感度照给 / 总开关关闭好感度 0
        var eatSettings = new PetCareSettings { DailyTasksEnabled = false };
        using (var care = CreateCare(CreateStorage("live2d-care-check-eat"), () => eatSettings))
        {
            var triangle = care.ApplyEat(RiceBallKind.Triangle, FakeNow);
            C("养成：吃饭按饭团回复饱食度（70 + 15 → 85，档位变很饱）",
                Near(triangle.FullnessChanged, 15) && Near(triangle.FullnessAfter, 85)
                && triangle.Tier == FullnessTier.Full && Near(triangle.AffectionGain, 1));

            care.SetFullness(95);
            var bale = care.ApplyEat(RiceBallKind.Bale, FakeNow);
            C("养成：饱食度封顶 100（95 + 22 → 100，实际变化 5）",
                Near(bale.FullnessAfter, 100) && Near(bale.FullnessChanged, 5),
                $"after={bale.FullnessAfter:0.#} changed={bale.FullnessChanged:0.#}");

            eatSettings = new PetCareSettings { FullnessLocked = true, DailyTasksEnabled = false };
            care.ApplySettings();
            care.SetFullness(70);
            var lockedEat = care.ApplyEat(RiceBallKind.Triangle, FakeNow);
            C("养成：锁定饱食度时吃饭不改饱食度，但好感度增量仍按饭团给（+1）",
                Near(lockedEat.FullnessChanged, 0) && Near(lockedEat.FullnessAfter, 70)
                && Near(lockedEat.AffectionGain, 1),
                $"fullness={lockedEat.FullnessAfter:0.#} affection={lockedEat.AffectionGain:0.#}");

            care.SetFullness(42);
            C("养成：锁定后手动 SetFullness 仍然可以改（42）",
                Near(care.Fullness, 42), care.Fullness.ToString("0.#"));
        }

        var shutSettings = new PetCareSettings { Enabled = false, DailyTasksEnabled = false };
        using (var care = CreateCare(CreateStorage("live2d-care-check-eat-off"), () => shutSettings))
        {
            var eaten = care.ApplyEat(RiceBallKind.Bale, FakeNow);
            C("养成：总开关关闭时吃饭不改饱食度、好感度增量 = 0",
                Near(eaten.FullnessChanged, 0) && Near(eaten.AffectionGain, 0) && Near(care.Fullness, 70));
        }
    }

    // ---------------------------------------------------------------- ⑤ 金币与饭团商店

    private static void CheckCareShop(CheckFn C)
    {
        var settings = new PetCareSettings { DailyTasksEnabled = false };
        using (var care = CreateCare(CreateStorage("live2d-care-check-shop"), () => settings))
        {
            var bought = care.TryBuy(RiceBallKind.Triangle, out var buyError);
            C("养成：买三角形饭团扣 10 金币、库存 +1",
                bought && buyError.Length == 0 && care.Coins == 20 && care.GetInventory(RiceBallKind.Triangle) == 1,
                $"金币={care.Coins} 库存={care.GetInventory(RiceBallKind.Triangle)}");

            care.SetCoins(0);
            var poor = care.TryBuy(RiceBallKind.Triangle, out var poorError);
            C("养成：金币不足买不了，中文原因含「金币不足」且不扣库存",
                !poor && poorError.Contains("金币不足", StringComparison.Ordinal)
                && care.GetInventory(RiceBallKind.Triangle) == 1,
                poorError);

            care.SetCoins(100);
            C("养成：库存 0 时出库失败，中文原因含「没有」",
                !care.TryConsumeInventory(RiceBallKind.Sphere, out var emptyError)
                && emptyError.Contains("没有", StringComparison.Ordinal),
                emptyError);

            C("养成：出库成功库存 -1",
                care.TryConsumeInventory(RiceBallKind.Triangle, out var consumeError)
                && consumeError.Length == 0 && care.GetInventory(RiceBallKind.Triangle) == 0);

            settings = new PetCareSettings { CoinsLocked = true, DailyTasksEnabled = false };
            var lockedBuy = care.TryBuy(RiceBallKind.Triangle, out var lockedError);
            C("养成：锁定金币后买不了，中文原因含「锁定」",
                !lockedBuy && lockedError.Contains("锁定", StringComparison.Ordinal), lockedError);

            settings = new PetCareSettings { Enabled = false, DailyTasksEnabled = false };
            var offBuy = care.TryBuy(RiceBallKind.Triangle, out var offError);
            C("养成：总开关关闭后买不了，中文原因含「关闭」",
                !offBuy && offError.Contains("关闭", StringComparison.Ordinal), offError);

            settings = new PetCareSettings { DailyTasksEnabled = false };
            care.SetInventory(RiceBallKind.Triangle, 99);
            var full = care.TryBuy(RiceBallKind.Triangle, out var fullError);
            C("养成：单种饭团库存封顶 99（买第 100 个被拦下、不白扣钱）",
                !full && fullError.Contains("库存已满", StringComparison.Ordinal) && care.Coins == 100,
                fullError);

            care.SetCoins(-5);
            care.SetInventory(RiceBallKind.Sphere, 200);
            C("养成：手动改金币 / 库存会夹取（-5 → 0、200 → 99）",
                care.Coins == 0 && care.GetInventory(RiceBallKind.Sphere) == 99,
                $"金币={care.Coins} 库存={care.GetInventory(RiceBallKind.Sphere)}");

            C("养成：库存总数 / 状态文本同步（三角 99 + 球形 99 = 198）",
                care.TotalInventory == 198 && care.GetInventory(RiceBallKind.Triangle) == 99
                && care.StatusText.Contains("饭团", StringComparison.Ordinal));
        }
    }

    // ---------------------------------------------------------------- ⑥ 每日任务

    private static void CheckDailyTasks(CheckFn C)
    {
        C("养成任务：3 个任务目录（摸摸头 3 次 +6 / 聊 1 次 +4 / 投喂 1 次 +8，全完成 18 金币）",
            DailyTaskCatalog.Count == 3
            && DailyTaskCatalog.Get(DailyTaskKind.PetHead) is
                { Title: "摸摸头", TargetCount: 3, RewardCoins: 6 }
            && DailyTaskCatalog.Get(DailyTaskKind.Chat) is
                { Title: "聊聊天", TargetCount: 1, RewardCoins: 4 }
            && DailyTaskCatalog.Get(DailyTaskKind.Feed) is
                { Title: "投喂饭团", TargetCount: 1, RewardCoins: 8 }
            && DailyTaskCatalog.TotalReward == 18,
            string.Join("、", DailyTaskCatalog.Tasks.Select(task => $"{task.Title}+{task.RewardCoins}")));

        var settings = new PetCareSettings();
        using (var care = CreateCare(CreateStorage("live2d-care-check-tasks"), () => settings))
        {
            for (var i = 0; i < 3; i++) care.ReportDailyEvent(DailyTaskKind.PetHead, FakeNow);
            C("养成任务：摸摸头 3 次 → 完成并只发一次奖励（金币 30 → 36）",
                care.GetTaskProgress(DailyTaskKind.PetHead) == 3
                && care.IsTaskRewarded(DailyTaskKind.PetHead)
                && care.Coins == 36,
                $"进度={care.GetTaskProgress(DailyTaskKind.PetHead)} 金币={care.Coins}");

            care.ReportDailyEvent(DailyTaskKind.PetHead, FakeNow);
            C("养成任务：已领奖后再摸头不重复发钱、进度停在 3",
                care.Coins == 36 && care.GetTaskProgress(DailyTaskKind.PetHead) == 3);

            care.ReportDailyEvent(DailyTaskKind.Chat, FakeNow);
            C("养成任务：聊一次天 +4 金币（36 → 40）",
                care.Coins == 40 && care.IsTaskRewarded(DailyTaskKind.Chat), $"金币={care.Coins}");

            var fed = care.ApplyEat(RiceBallKind.Sphere, FakeNow);
            C("养成任务：投喂一次 +8 金币、CoinsRewarded = 8、提示含「投喂饭团」",
                care.Coins == 48 && fed.CoinsRewarded == 8
                && fed.TaskNotice is not null
                && fed.TaskNotice.Contains("投喂饭团", StringComparison.Ordinal),
                $"金币={care.Coins} 奖励={fed.CoinsRewarded} 提示={fed.TaskNotice}");

            C("养成任务：CompletedTaskCount = 3 / 总奖励 18 金币",
                care.CompletedTaskCount == 3
                && care.StatusText.Contains("今日任务 3/3", StringComparison.Ordinal));

            care.Tick(FakeNow.AddDays(1));
            C("养成任务：跨天 Tick 把进度 / 领奖全部清零",
                care.CompletedTaskCount == 0
                && care.GetTaskProgress(DailyTaskKind.PetHead) == 0
                && care.GetTaskProgress(DailyTaskKind.Chat) == 0
                && !care.IsTaskRewarded(DailyTaskKind.Feed));
        }

        // 金币锁定时：任务照样标完成，但不发币、提示说明未发奖励
        var lockedSettings = new PetCareSettings { CoinsLocked = true };
        using (var care = CreateCare(CreateStorage("live2d-care-check-tasks-locked"), () => lockedSettings))
        {
            for (var i = 0; i < 3; i++) care.ReportDailyEvent(DailyTaskKind.PetHead, FakeNow);
            C("养成任务：金币锁定时任务仍标完成但不发币（30 不变）",
                care.Coins == 30 && care.IsTaskRewarded(DailyTaskKind.PetHead));

            var fed = care.ApplyEat(RiceBallKind.Triangle, FakeNow);
            C("养成任务：金币锁定时投喂任务只标完成（CoinsRewarded = 0、提示含「未发奖励」）",
                fed.CoinsRewarded == 0 && care.IsTaskRewarded(DailyTaskKind.Feed)
                && fed.TaskNotice is not null
                && fed.TaskNotice.Contains("未发奖励", StringComparison.Ordinal),
                fed.TaskNotice ?? "");
        }

        // 金币自然模式关：同上（只统计进度）
        var noCoinSettings = new PetCareSettings { CoinsNatural = false };
        using (var care = CreateCare(CreateStorage("live2d-care-check-tasks-nocoin"), () => noCoinSettings))
        {
            care.ReportDailyEvent(DailyTaskKind.Chat, FakeNow);
            C("养成任务：金币自然模式关 → 完成进度但不发币",
                care.Coins == 30 && care.IsTaskRewarded(DailyTaskKind.Chat));
        }

        // 任务总开关关：完全不统计
        var offSettings = new PetCareSettings { DailyTasksEnabled = false };
        using (var care = CreateCare(CreateStorage("live2d-care-check-tasks-off"), () => offSettings))
        {
            care.ReportDailyEvent(DailyTaskKind.PetHead, FakeNow);
            C("养成任务：每日任务总开关关 → 不计进度、不发币",
                care.GetTaskProgress(DailyTaskKind.PetHead) == 0
                && care.Coins == 30 && !care.IsTaskRewarded(DailyTaskKind.PetHead));
        }
    }

    // ---------------------------------------------------------------- ⑦ 持久化

    private static void CheckCarePersistence(CheckFn C)
    {
        var storage = CreateStorage("live2d-care-check-persist");
        var settings = new PetCareSettings();
        var now = DateTimeOffset.Now;

        using (var care = CreateCare(storage, () => settings))
        {
            care.Tick(now);
            care.ReportDailyEvent(DailyTaskKind.Chat, now);
            care.SetFullness(33.3);
            care.SetCoins(77);
            care.SetInventory(RiceBallKind.Taiko, 5);
            care.SaveState();

            C("养成持久化：状态落盘到 Data\\live2d\\care-state.json",
                storage.FileExists(CareStateFileName),
                storage.PathOf(CareStateFileName));
        }

        using (var reopened = CreateCare(storage, () => settings))
        {
            C("养成持久化：新服务读回饱食度 / 金币 / 库存 / 今日任务",
                Near(reopened.Fullness, 33.3)
                && reopened.Coins == 77
                && reopened.GetInventory(RiceBallKind.Taiko) == 5
                && reopened.GetTaskProgress(DailyTaskKind.Chat) == 1
                && reopened.IsTaskRewarded(DailyTaskKind.Chat),
                $"饱食={reopened.Fullness:0.#} 金币={reopened.Coins} 库存={reopened.GetInventory(RiceBallKind.Taiko)}");
        }

        // 坏 JSON：绝不打断桌宠启动，按新存档开始
        var badStorage = CreateStorage("live2d-care-check-persist-bad");
        badStorage.WriteText(CareStateFileName, "{ 这不是合法 JSON ");
        using (var broken = CreateCare(badStorage, () => new PetCareSettings()))
        {
            C("养成持久化：坏 JSON 回退新存档（不抛异常，70 / 30）",
                Near(broken.Fullness, 70) && broken.Coins == 30,
                $"{broken.Fullness:0.#} / {broken.Coins}");
        }

        // 手改坏数值：数组长度 / 越界值都要归一化
        var weirdStorage = CreateStorage("live2d-care-check-persist-weird");
        weirdStorage.WriteText(CareStateFileName,
            "{\"fullness\":999,\"coins\":-5,\"inventory\":[7],\"taskDate\":\"\",\"taskProgress\":[99],\"taskRewarded\":[true]}");
        using (var normalized = CreateCare(weirdStorage, () => new PetCareSettings()))
        {
            C("养成持久化：手改坏数值被归一化（饱食夹到 100 / 金币 -5 → 0 / 库存数组补到 4 位）",
                Near(normalized.Fullness, 100)
                && normalized.Coins == 0
                && normalized.GetInventory(RiceBallKind.Triangle) == 7
                && normalized.GetInventory(RiceBallKind.Sphere) == 0,
                $"饱食={normalized.Fullness:0.#} 金币={normalized.Coins}");
        }
    }

    // ---------------------------------------------------------------- ⑧ 吃饭状态机

    private static void CheckFeedingController(CheckFn C)
    {
        var controller = new FeedingController();
        C("养成状态机：默认常量（咀嚼 1.4s / 吞咽 0.65s / 咀嚼频率 1.7Hz）与初始 Idle",
            Near(FeedingController.DefaultChewSeconds, 1.4)
            && Near(FeedingController.DefaultSwallowSeconds, 0.65)
            && Near(FeedingController.ChewFrequencyHz, 1.7)
            && controller.Phase == FeedingPhase.Idle
            && !controller.IsEating
            && !controller.HasOutput);

        // 饭团靠近：凑过去 + 张嘴
        controller.SetFocus(0.8f, 0.3f, 0.9f, 0.7f);
        for (var i = 0; i < 30; i++) controller.Update(1.0 / 60.0);
        C("养成状态机：SetFocus 后凑近（FocusX 逼近目标、Strength > 0.5、MouthOpen > 0.3、HasOutput）",
            controller.FocusX > 0.6f && controller.FocusX < 0.9f
            && controller.FocusY > 0.2f
            && controller.Strength > 0.5f
            && controller.MouthOpen > 0.3f
            && controller.HasOutput
            && controller.IsFocusActive,
            $"fx={controller.FocusX:0.###} mouth={controller.MouthOpen:0.###}");

        controller.ClearFocus();
        for (var i = 0; i < 180; i++) controller.Update(1.0 / 60.0);
        C("养成状态机：ClearFocus 后平滑回落（FocusX ≈ 0、HasOutput = false）",
            Math.Abs(controller.FocusX) < 0.01f
            && Math.Abs(controller.MouthOpen) < 0.01f
            && !controller.HasOutput
            && !controller.IsFocusActive);

        // 吃到饭团：咀嚼 → 伪吞咽 → 回平常
        controller.StartEating(1.4, 0.65);
        C("养成状态机：StartEating 立即进入咀嚼（IsEating / Phase=Chewing / HasOutput）",
            controller.IsEating && controller.Phase == FeedingPhase.Chewing
            && controller.PhaseElapsed == 0 && controller.HasOutput);

        var mouthSamples = new List<double>();
        var nodSamples = new List<double>();
        var chewFrames = 0;
        while (controller.Phase == FeedingPhase.Chewing && chewFrames < 200)
        {
            controller.Update(1.0 / 60.0);
            chewFrames++;
            if (chewFrames > 5 && controller.Phase == FeedingPhase.Chewing)
            {
                mouthSamples.Add(controller.MouthOpen);
                nodSamples.Add(controller.HeadNodDegrees);
            }
        }

        C("养成状态机：咀嚼约 1.4 秒后进入吞咽（84 ~ 86 帧）",
            controller.Phase == FeedingPhase.Swallowing
            && controller.PhaseElapsed == 0
            && chewFrames >= 84 && chewFrames <= 86,
            $"咀嚼帧数={chewFrames}");

        C("养成状态机：咀嚼时嘴一开一合（0.2 ~ 0.8 之间波动，幅度 > 0.1）",
            mouthSamples.Count > 20
            && mouthSamples.All(mouth => mouth >= 0.2 && mouth <= 0.8)
            && mouthSamples.Max() - mouthSamples.Min() > 0.1,
            $"样本 {mouthSamples.Count}：min={mouthSamples.Min():0.###} max={mouthSamples.Max():0.###}");

        C("养成状态机：咀嚼时轻微点头（HeadNodDegrees 正负都有、幅度 > 1°）",
            nodSamples.Count > 20
            && nodSamples.Max() > 1.0 && nodSamples.Min() < -1.0,
            $"min={nodSamples.Min():0.###} max={nodSamples.Max():0.###}");

        var swallowFrames = 0;
        while (controller.Phase == FeedingPhase.Swallowing && swallowFrames < 120)
        {
            controller.Update(1.0 / 60.0);
            swallowFrames++;
        }

        C("养成状态机：吞咽约 0.65 秒后回到 Idle（IsEating = false，38 ~ 41 帧）",
            controller.Phase == FeedingPhase.Idle && !controller.IsEating
            && swallowFrames >= 38 && swallowFrames <= 41,
            $"吞咽帧数={swallowFrames}");

        for (var i = 0; i < 120; i++) controller.Update(1.0 / 60.0);
        C("养成状态机：吃完回落后 HasOutput = false、嘴型归零",
            !controller.HasOutput && Math.Abs(controller.MouthOpen) < 0.01f);

        controller.SetFocus(0.5f, 0.5f, 0.8f, 0.8f);
        controller.Update(1.0 / 60.0);
        controller.StartEating();
        controller.Reset();
        C("养成状态机：Reset 立即回 Idle 并清空凑近 / 嘴 / 头输出",
            controller.Phase == FeedingPhase.Idle
            && !controller.IsEating
            && !controller.IsFocusActive
            && !controller.HasOutput
            && controller.MouthOpen == 0
            && controller.FocusX == 0
            && controller.HeadNodDegrees == 0);

        var clamped = new FeedingController();
        clamped.StartEating(0.01, 99);
        C("养成状态机：咀嚼 / 吞咽时长夹取（0.01 → 0.3s、99 → 3s）、PhaseText 中文",
            Near(clamped.ChewSeconds, 0.3)
            && Near(clamped.SwallowSeconds, 3)
            && FeedingController.PhaseText(FeedingPhase.Chewing) == "嚼嚼嚼…"
            && FeedingController.PhaseText(FeedingPhase.Swallowing) == "咕嘟…咽下去了"
            && FeedingController.PhaseText(FeedingPhase.Idle) == "平常");
    }

    // ---------------------------------------------------------------- ⑨ 好感度锁定 / 直接加减

    private static void CheckAffectionLock(CheckFn C)
    {
        var lockedNow = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(8));

        var locked = new AffectionEngine(new AffectionState
        {
            Value = 50,
            LastChatUnix = lockedNow.ToUnixTimeSeconds(),
        });
        locked.Settings = new AffectionSettings { Locked = true };
        C("养成好感度：锁定后聊天 / 时间掉值都不改好感度（ApplyChat / ApplyDecay 返回 0）",
            locked.ApplyChat(3, lockedNow) == 0
            && locked.ApplyDecay(lockedNow.AddDays(30)) == 0
            && Near(locked.Value, 50));

        var direct = new AffectionEngine(new AffectionState { Value = 50 });
        direct.Settings = new AffectionSettings();
        var gained = direct.ApplyDirectGain(3);
        C("养成好感度：ApplyDirectGain(+3) 50 → 53 且返回 3",
            Near(gained, 3) && Near(direct.Value, 53), $"{direct.Value:0.#}");

        var lost = direct.ApplyDirectGain(-3);
        C("养成好感度：ApplyDirectGain(-3) 53 → 50 且返回 -3",
            Near(lost, -3) && Near(direct.Value, 50), $"{direct.Value:0.#}");

        var high = new AffectionEngine(new AffectionState { Value = 99 });
        high.Settings = new AffectionSettings();
        var toMax = high.ApplyDirectGain(3);
        C("养成好感度：直接加好感度夹在 100（99 + 3 → 只加 1）",
            Near(toMax, 1) && Near(high.Value, 100), $"{high.Value:0.#}");

        var low = new AffectionEngine(new AffectionState { Value = 1 });
        low.Settings = new AffectionSettings();
        var toZero = low.ApplyDirectGain(-3);
        C("养成好感度：直接减好感度夹在 0（1 - 3 → 只减 1）",
            Near(toZero, -1) && Near(low.Value, 0), $"{low.Value:0.#}");

        var lockedDirect = new AffectionEngine(new AffectionState { Value = 50 });
        lockedDirect.Settings = new AffectionSettings { Locked = true };
        C("养成好感度：锁定后 ApplyDirectGain 返回 0、数值不变（投喂不加好感度）",
            lockedDirect.ApplyDirectGain(3) == 0 && Near(lockedDirect.Value, 50));

        C("养成好感度：delta = 0 / NaN 返回 0、数值不变",
            direct.ApplyDirectGain(0) == 0 && direct.ApplyDirectGain(double.NaN) == 0
            && Near(direct.Value, 50));

        var manual = new AffectionEngine(new AffectionState { Value = 50 });
        manual.Settings = new AffectionSettings { Locked = true };
        manual.SetValue(7.5);
        C("养成好感度：SetValue 手动设定不受锁定限制（7.5）",
            Near(manual.Value, 7.5), $"{manual.Value:0.#}");
    }

    // ---------------------------------------------------------------- ⑩ 聊天服务的投喂接口

    private static void CheckChatServiceCareApi(CheckFn C)
    {
        var storage = CreateChatStorage("live2d-care-check-chat");
        using (var service = CreateChatService(storage))
        {
            var before = service.Affection.Value;
            service.AddAffection(2);
            C("养成聊天：投喂加好感度 AddAffection(+2) 生效（50 → 52）",
                Near(before, 50) && Near(service.Affection.Value, 52) && Near(service.AffectionValue, 52),
                $"{service.Affection.Value:0.#}");

            service.AppendNotice("今日任务完成：投喂饭团，+8 金币");
            service.AppendLocalPetMessage("好饱呀～");
            C("养成聊天：AppendNotice 加 notice 消息、AppendLocalPetMessage 加 assistant 消息（共 2 条）",
                service.Messages.Count == 2
                && service.Messages[0].IsNotice
                && service.Messages[0].Text.Contains("投喂饭团", StringComparison.Ordinal)
                && service.Messages[1].IsBot
                && service.Messages[1].Text == "好饱呀～",
                $"消息 {service.Messages.Count} 条");

            service.AppendNotice("   ");
            service.AppendLocalPetMessage("");
            C("养成聊天：空白提示 / 空本地台词不产生空消息", service.Messages.Count == 2);

            service.SaveState();
        }

        using (var reopened = CreateChatService(storage))
        {
            C("养成聊天：投喂加的好感度与本地台词都会落盘，重启读回（52 / 2 条）",
                Near(reopened.Affection.Value, 52)
                && reopened.Messages.Count == 2
                && reopened.Messages[1].IsBot,
                $"好感度={reopened.Affection.Value:0.#} 消息={reopened.Messages.Count}");
        }

        var lockedStorage = CreateChatStorage("live2d-care-check-chat-locked");
        using var lockedService = CreateChatService(lockedStorage, () => new AffectionSettings { Locked = true });
        lockedService.AddAffection(2);
        C("养成聊天：好感度锁定后投喂不加好感度（仍 50）",
            Near(lockedService.Affection.Value, 50), $"{lockedService.Affection.Value:0.#}");
    }

    // ---------------------------------------------------------------- ⑪ 养成配置

    private static void CheckCareConfig(CheckFn C)
    {
        var defaults = new Live2DConfig();
        C("养成配置默认值：总开关开 / 自然模式开 / 未锁定 / 每小时 4 点 / 金币自然 / 任务与饥饿提醒开 / 阈值 25 / 8 分钟",
            defaults.PetCareEnabled
            && defaults.PetFullnessNatural
            && !defaults.PetFullnessLocked
            && Near(defaults.PetFullnessDecayPerHour, 4)
            && defaults.PetCoinsNatural
            && !defaults.PetCoinsLocked
            && defaults.PetDailyTasksEnabled
            && defaults.PetHungryReminderEnabled
            && Near(defaults.PetHungryThreshold, 25)
            && Near(defaults.PetHungryReminderMinutes, 8),
            $"decay={defaults.PetFullnessDecayPerHour} 阈值={defaults.PetHungryThreshold} 间隔={defaults.PetHungryReminderMinutes}");

        C("养成配置默认值：好感度锁定（AiAffectionLocked）默认关",
            !defaults.AiAffectionLocked);

        var cfg = new Live2DConfig
        {
            PetCareEnabled = false,
            PetFullnessNatural = false,
            PetFullnessLocked = true,
            PetFullnessDecayPerHour = 7.5,
            PetCoinsNatural = false,
            PetCoinsLocked = true,
            PetDailyTasksEnabled = false,
            PetHungryReminderEnabled = false,
            PetHungryThreshold = 33,
            PetHungryReminderMinutes = 45,
            AiAffectionLocked = true,
        };
        var json = JsonFileStore.Serialize(cfg);
        var restored = JsonSerializer.Deserialize<Live2DConfig>(json, JsonFileStore.Options);
        C("养成配置 JSON 往返：全部 Pet* / AiAffectionLocked 保持",
            restored is
            {
                PetCareEnabled: false,
                PetFullnessNatural: false,
                PetFullnessLocked: true,
                PetCoinsNatural: false,
                PetCoinsLocked: true,
                PetDailyTasksEnabled: false,
                PetHungryReminderEnabled: false,
                AiAffectionLocked: true,
            }
            && Near(restored.PetFullnessDecayPerHour, 7.5)
            && Near(restored.PetHungryThreshold, 33)
            && Near(restored.PetHungryReminderMinutes, 45),
            restored is null ? "反序列化为 null" : $"JSON {json.Length} 字");

        C("养成配置按 camelCase 落盘（petCareEnabled / petFullnessDecayPerHour / petHungryThreshold / aiAffectionLocked …）",
            json.Contains("\"petCareEnabled\"", StringComparison.Ordinal)
            && json.Contains("\"petFullnessNatural\"", StringComparison.Ordinal)
            && json.Contains("\"petFullnessLocked\"", StringComparison.Ordinal)
            && json.Contains("\"petFullnessDecayPerHour\"", StringComparison.Ordinal)
            && json.Contains("\"petCoinsNatural\"", StringComparison.Ordinal)
            && json.Contains("\"petCoinsLocked\"", StringComparison.Ordinal)
            && json.Contains("\"petDailyTasksEnabled\"", StringComparison.Ordinal)
            && json.Contains("\"petHungryReminderEnabled\"", StringComparison.Ordinal)
            && json.Contains("\"petHungryThreshold\"", StringComparison.Ordinal)
            && json.Contains("\"petHungryReminderMinutes\"", StringComparison.Ordinal)
            && json.Contains("\"aiAffectionLocked\"", StringComparison.Ordinal));
    }

    // ====================================================================
    // 构造 / 小工具
    // ====================================================================

    /// <summary>自检断言委托（带默认 <paramref name="detail"/>；单独声明避免 Action 默认参数不生效）。</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    /// <summary>临时数据文件夹（每次清掉旧的 care-state.json，保证从小存档开始；不碰真实 Data）。</summary>
    private static ToolStorage CreateStorage(string folder)
    {
        var storage = ToolStorage.For(TempToolId, folder);
        storage.EnsureCreated();
        storage.Delete(CareStateFileName);
        return storage;
    }

    /// <summary>临时聊天数据文件夹（清掉旧 chat-state.json；投喂好感度 / 本地台词落盘用）。</summary>
    private static ToolStorage CreateChatStorage(string folder)
    {
        var storage = ToolStorage.For(TempToolId, folder);
        storage.EnsureCreated();
        storage.Delete(ChatStateFileName);
        return storage;
    }

    /// <summary>按「现读设置」回调创建养成服务（和会话里 <c>BuildCareSettings</c> 的用法一致）。</summary>
    private static PetCareService CreateCare(ToolStorage storage, Func<PetCareSettings> settings)
        => new(new PetCareServiceOptions
        {
            Storage = storage,
            Settings = settings,
        });

    /// <summary>创建聊天服务（不联网：只有投喂加好感度 / 本地台词 / 落盘会被调用）。</summary>
    private static AiChatService CreateChatService(ToolStorage storage, Func<AffectionSettings>? affection = null)
        => new(new AiChatServiceOptions
        {
            Storage = storage,
            HttpHandler = new NoNetworkHandler(),
            RequestOptions = () => new AiRequestOptions { ApiKey = "" },
            Persona = () => new AiPersonaSettings(),
            Affection = affection ?? (() => new AffectionSettings()),
            OfflineFallback = text => "离线兜底：" + text,
        });

    /// <summary>跑一段「不应该抛异常」的动作，返回是否正常返回。</summary>
    private static bool SafeNoThrow(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>浮点近似相等（默认容差 1e-9）。</summary>
    private static bool Near(double actual, double expected, double tolerance = 1e-9)
        => Math.Abs(actual - expected) <= tolerance;

    /// <summary>假 HTTP handler：自检绝不应发网络请求，被调用直接抛（能立刻暴露意外的联网路径）。</summary>
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("养成自检不应发网络请求");
    }
}
