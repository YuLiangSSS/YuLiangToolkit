using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Live2D.Play;

/// <summary>大白饭团的四种形状（参考图：三角形 / 球形 / 太鼓形 / 俵形）。</summary>
public enum RiceBallKind
{
    /// <summary>三角形（最标准的饭团）。</summary>
    Triangle = 0,

    /// <summary>球形（圆滚滚，江户时期的样式）。</summary>
    Sphere = 1,

    /// <summary>太鼓形（像车轮 / 圆盘，边缘略扁）。</summary>
    Taiko = 2,

    /// <summary>俵形（像米袋的圆柱状，适合装进便当盒）。</summary>
    Bale = 3,
}

/// <summary>
/// 一种大白饭团：名字 / 说明 / 价格（金币）/ 吃掉能回多少饱食度 / 涨多少好感度。
/// <para>参考图的四种形状：三角形、球形、太鼓（圆）形、俵形；越「实在」的形状回得越多、也越贵。</para>
/// </summary>
public sealed record RiceBallFood(
    RiceBallKind Kind,
    string Name,
    string Description,
    int Price,
    double FullnessGain,
    double AffectionGain)
{
    /// <summary>界面上的小图形（用字符画，省得为四种形状各做一份图标资源）。</summary>
    public string Glyph => Kind switch
    {
        RiceBallKind.Triangle => "△",
        RiceBallKind.Sphere => "○",
        RiceBallKind.Taiko => "▭",
        RiceBallKind.Bale => "⬭",
        _ => "🍙",
    };
}

/// <summary>饭团目录（四种，数值为初始平衡；改这里要同步自检与文档）。</summary>
public static class RiceBallCatalog
{
    /// <summary>四种大白饭团（顺序 = <see cref="RiceBallKind"/>）。</summary>
    public static IReadOnlyList<RiceBallFood> Foods { get; } = new[]
    {
        new RiceBallFood(RiceBallKind.Triangle, "三角形饭团",
            "最标准的饭团，捏得紧实；吃下去刚好垫垫肚子。", Price: 10, FullnessGain: 15, AffectionGain: 1),
        new RiceBallFood(RiceBallKind.Sphere, "球形饭团",
            "圆滚滚的白饭球，入口松软；便宜管饱。", Price: 8, FullnessGain: 12, AffectionGain: 1),
        new RiceBallFood(RiceBallKind.Taiko, "太鼓形饭团",
            "像车轮一样的厚饭团，咬下去很有满足感。", Price: 12, FullnessGain: 18, AffectionGain: 2),
        new RiceBallFood(RiceBallKind.Bale, "俵形饭团",
            "像小小米袋的圆柱饭团，分量最足，一个顶饱。", Price: 15, FullnessGain: 22, AffectionGain: 3),
    };

    /// <summary>按种类取饭团（未知按三角形）。</summary>
    public static RiceBallFood Get(RiceBallKind kind)
        => Foods[(int)kind is >= 0 and < 4 ? (int)kind : 0];

    /// <summary>种类数量（= 4）。</summary>
    public static int Count => Foods.Count;
}

/// <summary>每日任务种类（目前 3 个：摸摸头 / 聊一次 / 喂一次）。</summary>
public enum DailyTaskKind
{
    /// <summary>摸摸大肥鱼的头（桌宠互动 pat_head）。</summary>
    PetHead = 0,

    /// <summary>和桌宠聊一次（AI 聊天 / 轻对话都算）。</summary>
    Chat = 1,

    /// <summary>喂一次饭团（吃掉才算）。</summary>
    Feed = 2,
}

/// <summary>一个每日任务：标题 / 说明 / 目标次数 / 奖励金币。</summary>
public sealed record DailyTaskDefinition(
    DailyTaskKind Kind,
    string Title,
    string Description,
    int TargetCount,
    int RewardCoins);

/// <summary>每日任务目录（3 个；每天 0 点本地时间重置进度）。</summary>
public static class DailyTaskCatalog
{
    /// <summary>三个每日任务（顺序 = <see cref="DailyTaskKind"/>）。</summary>
    public static IReadOnlyList<DailyTaskDefinition> Tasks { get; } = new[]
    {
        new DailyTaskDefinition(DailyTaskKind.PetHead, "摸摸头", "摸摸大肥鱼的头 3 次", 3, 6),
        new DailyTaskDefinition(DailyTaskKind.Chat, "聊聊天", "和大肥鱼聊 1 次天", 1, 4),
        new DailyTaskDefinition(DailyTaskKind.Feed, "投喂饭团", "让大肥鱼吃掉 1 个饭团", 1, 8),
    };

    /// <summary>按种类取任务定义（未知按第一个）。</summary>
    public static DailyTaskDefinition Get(DailyTaskKind kind) => Tasks[(int)kind];

    /// <summary>任务总奖励（全部完成 = 18 金币）。</summary>
    public static int TotalReward => 6 + 4 + 8;

    /// <summary>任务数量（= 3）。</summary>
    public static int Count => Tasks.Count;
}

/// <summary>饱食度档位（决定吃完之后 / 平时说的话和表情）。</summary>
public enum FullnessTier
{
    /// <summary>很饿（&lt; 20）：难受 / 恳求。</summary>
    Starving = 0,

    /// <summary>没吃饱（20 ~ 44）：还想要。</summary>
    Hungry = 1,

    /// <summary>正常（45 ~ 79）。</summary>
    Normal = 2,

    /// <summary>很饱（&ge; 80）：满足。</summary>
    Full = 3,
}

/// <summary>
/// 养成（饥饿 / 金币 / 每日任务）的设置快照。三项数值（好感度另见好感度设置）都能：
/// **自然模式**（系统自动涨 / 掉）、**手动模式**（只有用户自己拖）、**锁定**（冻结，任何自动变化都不改，
/// 但用户手动拖动仍然可以改 —— 方便截图 / 调试 / 保持某个状态）。
/// </summary>
public sealed class PetCareSettings
{
    // ---------- 总开关 ----------
    /// <summary>养成系统总开关（关掉 = 不饿、不显示投喂入口 / 金币 / 任务）。</summary>
    public bool Enabled { get; init; } = true;

    // ---------- 饱食度 ----------
    /// <summary>饱食度自然模式：按 <see cref="FullnessDecayPerHour"/> 随时间下降。</summary>
    public bool FullnessNatural { get; init; } = true;

    /// <summary>锁定饱食度：时间 / 吃饭都不改它，只有手动拖动能改。</summary>
    public bool FullnessLocked { get; init; }

    /// <summary>每小时掉多少饱食度（0 ~ 20，默认 4 = 大约 25 小时从满到饿）。</summary>
    public double FullnessDecayPerHour { get; init; } = 4;

    /// <summary>新存档的初始饱食度（0 ~ 100，默认 70；只在第一次创建存档时用）。</summary>
    public double FullnessInitial { get; init; } = 70;

    // ---------- 金币 ----------
    /// <summary>金币自然模式：每日任务完成自动发币（关掉 = 只统计进度、不发币）。</summary>
    public bool CoinsNatural { get; init; } = true;

    /// <summary>锁定金币：任务奖励 / 消费都不改它，只有手动拖动能改。</summary>
    public bool CoinsLocked { get; init; }

    /// <summary>新存档初始金币（0 ~ 100000，默认 30；只在第一次创建存档时用）。</summary>
    public int CoinsInitial { get; init; } = 30;

    // ---------- 每日任务 / 饿的时候主动说话 ----------
    /// <summary>每日任务是否自动统计 / 发奖励。</summary>
    public bool DailyTasksEnabled { get; init; } = true;

    /// <summary>很饿的时候是否主动说话（"好饿啊～…"）。</summary>
    public bool HungryReminderEnabled { get; init; } = true;

    /// <summary>饱食度低于多少算「很饿」并触发主动说话（0 ~ 100，默认 25）。</summary>
    public double HungryThreshold { get; init; } = 25;

    /// <summary>饿的时候每隔多少分钟主动说一句（1 ~ 240，默认 8）。</summary>
    public double HungryReminderMinutes { get; init; } = 8;
}

/// <summary>养成状态（落盘到 <c>Data\live2d\care-state.json</c>）。</summary>
public sealed class PetCareState
{
    /// <summary>当前饱食度 0 ~ 100。</summary>
    public double Fullness { get; set; } = 70;

    /// <summary>上次更新饱食度的时间（Unix 秒；用于按小时掉值）。</summary>
    public long LastUpdateUnix { get; set; }

    /// <summary>金币数量（&ge; 0）。</summary>
    public int Coins { get; set; } = 30;

    /// <summary>饭团库存，按 <see cref="RiceBallKind"/> 下标（长度 4）。</summary>
    public int[] Inventory { get; set; } = new int[4];

    /// <summary>任务进度所属的本地日期（yyyy-MM-dd）。</summary>
    public string TaskDate { get; set; } = "";

    /// <summary>三个任务今天的进度（长度 = <see cref="DailyTaskCatalog.Count"/>）。</summary>
    public int[] TaskProgress { get; set; } = new int[3];

    /// <summary>三个任务今天是否已经发过奖励（长度 = 3）。</summary>
    public bool[] TaskRewarded { get; set; } = new bool[3];

    /// <summary>复制一份（防外部改到服务内部状态）。</summary>
    public PetCareState Clone() => new()
    {
        Fullness = Fullness,
        LastUpdateUnix = LastUpdateUnix,
        Coins = Coins,
        Inventory = (int[])Inventory.Clone(),
        TaskDate = TaskDate,
        TaskProgress = (int[])TaskProgress.Clone(),
        TaskRewarded = (bool[])TaskRewarded.Clone(),
    };
}

/// <summary>一次投喂的结果（会话据此涨好感度、说不同的话、挑表情）。</summary>
public sealed class FeedResult
{
    /// <summary>吃完之后的饱食度档位（决定说"饱饱的 / 我还想要 / 好饿"）。</summary>
    public FullnessTier Tier { get; init; }

    /// <summary>吃完之后的饱食度数值。</summary>
    public double FullnessAfter { get; init; }

    /// <summary>饱食度实际变化（锁定 / 总开关关闭时可能为 0）。</summary>
    public double FullnessChanged { get; init; }

    /// <summary>这次吃饭应该加的好感度（由会话交给好感度引擎，受好感度锁定影响）。</summary>
    public double AffectionGain { get; init; }

    /// <summary>这次吃饭顺带拿到的金币（任务奖励；没完成 = 0）。</summary>
    public int CoinsRewarded { get; init; }

    /// <summary>完成的任务提示（如「今日任务完成：投喂饭团，+8 金币」；没有 = null）。</summary>
    public string? TaskNotice { get; init; }
}

/// <summary>养成提示种类（界面用来决定颜色 / 是否弹提示）。</summary>
public enum CareNoticeKind
{
    /// <summary>普通信息（买饭团 / 丢饭团）。</summary>
    Info = 0,

    /// <summary>任务完成 / 发奖励。</summary>
    Reward = 1,

    /// <summary>操作失败（金币不够 / 没库存）。</summary>
    Warning = 2,
}

/// <summary>养成提示（任务完成 / 金币变化 / 操作失败）。</summary>
public sealed class CareNoticeEventArgs : EventArgs
{
    /// <summary>提示文本（中文）。</summary>
    public required string Text { get; init; }

    /// <summary>提示种类。</summary>
    public CareNoticeKind Kind { get; init; } = CareNoticeKind.Info;
}

/// <summary>构造 <see cref="PetCareService"/> 的回调（配置现读 + 日志 + 提示）。</summary>
public sealed class PetCareServiceOptions
{
    /// <summary>数据落盘目录（<c>Data\live2d\</c>，内部用 <c>care-state.json</c>）。</summary>
    public ToolStorage? Storage { get; init; }

    /// <summary>现读设置（改配置立刻生效）。</summary>
    public Func<PetCareSettings>? Settings { get; init; }

    /// <summary>日志（工具页日志）。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>界面提示（任务完成 / 金币不足）。</summary>
    public Action<CareNoticeEventArgs>? Notice { get; init; }
}

/// <summary>
/// 养成服务：饱食度自然下降 / 手动 / 锁定、金币、每日任务进度与发奖励、饭团库存与购买、吃饭结算、落盘。
///
/// <para><b>线程</b>：构造与公开方法都在 UI 线程调用（会话负责）；事件也在 UI 线程触发。</para>
///
/// <para><b>数值规则（自检按这个断言）</b>：</para>
/// <list type="bullet">
///   <item>饱食度 0~100，1 位小数；自然模式每小时掉 <see cref="PetCareSettings.FullnessDecayPerHour"/>，
///         按 <see cref="PetCareState.LastUpdateUnix"/> 算完整小时数（一次可补多段）；最低 0。</item>
///   <item>手动模式（FullnessNatural=false）时间不掉；锁定（FullnessLocked）时时间 / 吃饭都不改，
///         但 <see cref="SetFullness"/> 手动仍可改。</item>
///   <item>吃饭：饱食度 + 饭团 <see cref="RiceBallFood.FullnessGain"/>（夹到 100）；好感度变化由会话处理；
///         喂食任务进度 +1。</item>
///   <item>金币：任务完成在「金币自然模式 && 未锁定」时自动 +奖励；<see cref="TryBuy"/> 扣钱 + 库存 +1；
///         锁定 / 手动模式不影响用户主动买 / 卖（但锁定会阻止发奖励）。</item>
///   <item>每日任务：按本地日期 <c>yyyy-MM-dd</c> 重置进度；进度达到目标且未发奖 → 发奖（若金币自然模式）；
///         每个任务每天只发一次。</item>
/// </list>
/// </summary>
public sealed class PetCareService : INotifyPropertyChanged, IDisposable
{
    // ==================== 常量 ====================

    /// <summary>落盘文件名（相对 <see cref="ToolStorage"/> 的功能数据文件夹：Data\live2d\care-state.json）。</summary>
    private const string StateFileName = "care-state.json";

    /// <summary>饱食度下限 / 上限。</summary>
    private const double MinFullness = 0;

    /// <summary>饱食度上限。</summary>
    private const double MaxFullness = 100;

    /// <summary>金币上限（防手滑 / 溢出）。</summary>
    private const int MaxCoins = 100000;

    /// <summary>单种饭团库存上限。</summary>
    private const int MaxInventoryPerKind = 99;

    /// <summary>一小时的秒数（饱食度按整小时结算）。</summary>
    private const long SecondsPerHour = 3600;

    /// <summary>设置回调为 null 时用的默认设置（默认值就是产品默认值）。</summary>
    private static readonly PetCareSettings DefaultSettings = new();

    // ==================== 字段 ====================

    private readonly PetCareServiceOptions _options;
    private readonly PetCareState _state;

    /// <summary>上一次 ApplySettings 时的设置快照（只用于比较「哪些开关变了」，取设置一律走现读回调）。</summary>
    private PetCareSettings _appliedSettings;

    /// <summary>最近一次 Tick / ReportDailyEvent / ApplyEat 传入的时间（Unix 秒）；用于在没有 now 参数时跟注入的假时钟保持一致。</summary>
    private long _clockUnix;

    private bool _disposed;

    /// <summary>创建服务（<paramref name="options"/> 的回调都允许为 null）。</summary>
    public PetCareService(PetCareServiceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state = LoadState();
        _appliedSettings = Settings;
    }

    // ==================== 公开属性 ====================

    /// <summary>持久化状态（服务层负责落盘）。</summary>
    public PetCareState State => _state;

    /// <summary>当前设置（现读配置回调，可能每帧变）。</summary>
    public PetCareSettings Settings => _options.Settings?.Invoke() ?? DefaultSettings;

    /// <summary>当前饱食度 0 ~ 100。</summary>
    public double Fullness => _state.Fullness;

    /// <summary>当前金币。</summary>
    public int Coins => _state.Coins;

    /// <summary>当前饱食度档位。</summary>
    public FullnessTier Tier => FeedingLines.ResolveTier(_state.Fullness);

    /// <summary>饱食度档位的中文（很饿 / 没吃饱 / 正常 / 很饱）。</summary>
    public string TierText => FeedingLines.TierText(Tier);

    /// <summary>一句话状态（"饱食 72% · 金币 35 · 今日任务 2/3"）。</summary>
    public string StatusText =>
        $"饱食 {_state.Fullness.ToString("0", CultureInfo.InvariantCulture)}%（{TierText}）· " +
        $"🪙 {_state.Coins} · 饭团 {TotalInventory} · 今日任务 {CompletedTaskCount}/{DailyTaskCatalog.Count}";

    /// <summary>某个饭团的库存数量。</summary>
    public int GetInventory(RiceBallKind kind) => _state.Inventory[KindIndex(kind)];

    /// <summary>所有饭团库存总数。</summary>
    public int TotalInventory
    {
        get
        {
            var total = 0;
            foreach (var count in _state.Inventory) total += count;
            return total;
        }
    }

    /// <summary>今天某个任务是否已完成并领奖。</summary>
    public bool IsTaskRewarded(DailyTaskKind kind) => _state.TaskRewarded[TaskIndex(kind)];

    /// <summary>今天某个任务的进度（0 ~ 目标）。</summary>
    public int GetTaskProgress(DailyTaskKind kind) => _state.TaskProgress[TaskIndex(kind)];

    /// <summary>今天已完成的任务数。</summary>
    public int CompletedTaskCount
    {
        get
        {
            var count = 0;
            foreach (var rewarded in _state.TaskRewarded)
            {
                if (rewarded) count++;
            }
            return count;
        }
    }

    /// <summary>状态变化（饱食度 / 金币 / 库存 / 任务进度；在 UI 线程触发）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>提示（任务完成 / 金币不足等；在 UI 线程触发）。</summary>
    public event EventHandler<CareNoticeEventArgs>? NoticeRaised;

    /// <summary>属性变化（WPF 绑定用）。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    // ==================== 公开方法 ====================

    /// <summary>
    /// 定时结算（会话每 ~2 秒调一次）：按本地时间掉饱食度、跨天重置每日任务、刷新状态。
    /// 总开关关闭 / 没时间流逝时只做跨天检查。
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        if (_disposed) return;

        var nowUnix = now.ToUnixTimeSeconds();
        _clockUnix = nowUnix;

        // ① 跨天：本地日期变了 → 今日任务进度 / 领奖标记全部清零
        var tasksReset = ResetTasksIfNewDay(now);

        // ② 饱食度自然掉值：只按「完整小时」结算（一次可补多段），不满 1 小时不扣
        var settings = Settings;
        var fullnessChanged = false;
        if (IsNaturalDecayActive(settings))
        {
            var last = _state.LastUpdateUnix;
            if (last <= 0)
            {
                // 老存档没有基准：以现在为起点，不补扣历史
                _state.LastUpdateUnix = nowUnix;
            }
            else if (nowUnix < last)
            {
                // 系统时间往回调过：以当前为基准，避免算出负数小时
                _state.LastUpdateUnix = nowUnix;
            }
            else
            {
                var hours = (nowUnix - last) / SecondsPerHour;
                if (hours > 0)
                {
                    var decayPerHour = Math.Max(0, settings.FullnessDecayPerHour);
                    var decay = hours * decayPerHour;
                    if (decay > 0)
                    {
                        var after = ClampFullness(_state.Fullness - decay);
                        if (after < _state.Fullness)
                        {
                            _state.Fullness = after;
                            fullnessChanged = true;
                        }
                    }

                    // 只消费整小时，剩下的零头留到下次（下次凑满 1 小时再扣）
                    _state.LastUpdateUnix = last + hours * SecondsPerHour;
                }
            }
        }
        else
        {
            // 手动模式 / 锁定 / 总开关关闭：冻结期间的时间不累计，
            // 否则重新打开自然模式时会按 LastUpdateUnix 一次性暴扣。
            if (_state.LastUpdateUnix != nowUnix) _state.LastUpdateUnix = nowUnix;
        }

        // ③ 有变化才落盘 + 发事件（每 2 秒一次的空结算不打扰界面）
        if (tasksReset || fullnessChanged)
        {
            SaveStateInternal();
            NotifyChanged(fullness: fullnessChanged, coins: false, inventory: false, tasks: tasksReset);
        }
    }

    /// <summary>配置改了：重新套用（锁定 / 模式 / 总开关变化立即生效）。</summary>
    public void ApplySettings()
    {
        var settings = Settings;
        var previous = _appliedSettings;
        _appliedSettings = settings;

        // 自然掉值不生效时把基准拨到当前（用最近一次注入的时间，测试里假时钟也能对上），
        // 这样解锁 / 重开自然模式不会把冻结期间的时间一次性补扣。
        if (!IsNaturalDecayActive(settings))
        {
            _state.LastUpdateUnix = _clockUnix > 0 ? _clockUnix : DateTimeOffset.Now.ToUnixTimeSeconds();
        }

        OnPropertyChanged(nameof(Settings));

        // 只在这几个开关真的变了时才刷新（改阈值 / 掉值速度这类无关设置不打扰界面）
        if (previous.Enabled != settings.Enabled ||
            previous.FullnessNatural != settings.FullnessNatural ||
            previous.FullnessLocked != settings.FullnessLocked ||
            previous.CoinsNatural != settings.CoinsNatural ||
            previous.CoinsLocked != settings.CoinsLocked ||
            previous.DailyTasksEnabled != settings.DailyTasksEnabled)
        {
            NotifyChanged(fullness: false, coins: false, inventory: false, tasks: false);
        }
    }

    /// <summary>手动设置饱食度（用户自己拖；锁定也允许）。</summary>
    public void SetFullness(double value)
    {
        var clamped = ClampFullness(value);
        if (Math.Abs(clamped - _state.Fullness) < 0.0001) return;
        _state.Fullness = clamped;
        SaveStateInternal();
        NotifyChanged(fullness: true, coins: false, inventory: false, tasks: false);
    }

    /// <summary>手动设置金币（用户自己拖；锁定也允许）。</summary>
    public void SetCoins(int value)
    {
        var clamped = ClampCoins(value);
        if (clamped == _state.Coins) return;
        _state.Coins = clamped;
        SaveStateInternal();
        NotifyChanged(fullness: false, coins: true, inventory: false, tasks: false);
    }

    /// <summary>手动设置某个饭团库存（调试 / 自检用）。</summary>
    public void SetInventory(RiceBallKind kind, int count)
    {
        var index = KindIndex(kind);
        var clamped = Math.Clamp(count, 0, MaxInventoryPerKind);
        if (clamped == _state.Inventory[index]) return;
        _state.Inventory[index] = clamped;
        SaveStateInternal();
        NotifyChanged(fullness: false, coins: false, inventory: true, tasks: false);
    }

    /// <summary>直接发金币（任务 / 调试；锁定或金币自然模式关闭时只记日志不改数值？——任务请走 ReportDailyEvent）。</summary>
    public void AddCoins(int amount, string reason)
    {
        var settings = Settings;
        if (settings.CoinsLocked)
        {
            // 锁定 = 任何自动 / 程序性的金币变化都不改它（用户手动拖动走 SetCoins）
            PushNotice("金币已锁定", CareNoticeKind.Warning);
            return;
        }

        if (amount == 0) return;

        var before = _state.Coins;
        var after = ClampCoins((long)before + amount);
        if (after != before)
        {
            _state.Coins = after;
            SaveStateInternal();
            NotifyChanged(fullness: false, coins: true, inventory: false, tasks: false);
        }

        var detail = string.IsNullOrWhiteSpace(reason) ? "" : $"（{reason.Trim()}）";
        var sign = amount > 0
            ? "+" + amount.ToString(CultureInfo.InvariantCulture)
            : amount.ToString(CultureInfo.InvariantCulture);
        PushNotice($"金币 {sign} → {after}{detail}", CareNoticeKind.Info);
    }

    /// <summary>
    /// 买一个饭团：金币够且总开关开 → 扣钱、库存 +1、返回 true；否则 <paramref name="error"/> 是中文原因。
    /// </summary>
    public bool TryBuy(RiceBallKind kind, out string error)
    {
        var settings = Settings;
        var food = RiceBallCatalog.Get(kind);
        var index = KindIndex(kind);

        if (!settings.Enabled)
        {
            error = "养成系统已关闭";
            return false;
        }

        if (settings.CoinsLocked)
        {
            error = "金币已锁定，不能买饭团";
            return false;
        }

        if (_state.Inventory[index] >= MaxInventoryPerKind)
        {
            // 库存封顶 99：再买就白扣钱了，直接拦下
            error = $"{food.Name}的库存已满（{MaxInventoryPerKind}）";
            return false;
        }

        if (_state.Coins < food.Price)
        {
            error = $"金币不足（需要 {food.Price} 金币，现在只有 {_state.Coins}）";
            return false;
        }

        _state.Coins -= food.Price;
        _state.Inventory[index] += 1;
        error = "";
        SaveStateInternal();
        NotifyChanged(fullness: false, coins: true, inventory: true, tasks: false);
        PushNotice($"买了一个{food.Name}（-{food.Price} 金币）", CareNoticeKind.Info);
        return true;
    }

    /// <summary>从库存里拿一个饭团出去（扔到屏幕上）：库存 -1；没有库存返回 false。</summary>
    public bool TryConsumeInventory(RiceBallKind kind, out string error)
    {
        var index = KindIndex(kind);
        if (_state.Inventory[index] <= 0)
        {
            error = $"没有{RiceBallCatalog.Get(kind).Name}了";
            return false;
        }

        _state.Inventory[index] -= 1;
        error = "";
        SaveStateInternal();
        NotifyChanged(fullness: false, coins: false, inventory: true, tasks: false);
        return true;
    }

    /// <summary>
    /// 饭团真的被吃掉了：饱食度 + 饭团回复、喂食任务进度 +1（可能发奖）、返回结果（档位 / 好感度增量 / 奖励）。
    /// </summary>
    public FeedResult ApplyEat(RiceBallKind kind, DateTimeOffset now)
    {
        var settings = Settings;
        var food = RiceBallCatalog.Get(kind);
        _clockUnix = now.ToUnixTimeSeconds();

        // ① 喂食任务：走每日任务逻辑（跨天 / 已领奖 / 金币模式都在里面判断），把奖励和提示带回来
        var taskReport = settings.DailyTasksEnabled
            ? RecordDailyEvent(DailyTaskKind.Feed, now)
            : default;

        // ② 饱食度：总开关关闭 / 锁定时不改；好感度增量不受锁定影响（好感度锁由会话处理）
        var beforeFullness = _state.Fullness;
        var fullnessChanged = 0.0;
        if (settings.Enabled && !settings.FullnessLocked)
        {
            _state.Fullness = ClampFullness(beforeFullness + food.FullnessGain);
            fullnessChanged = Math.Round(_state.Fullness - beforeFullness, 1, MidpointRounding.AwayFromZero);
            if (Math.Abs(fullnessChanged) < 0.0001) fullnessChanged = 0;
        }

        // ③ 吃饭算一次「刚更新过」：掉值基准拨到现在，避免吃完立刻又掉
        _state.LastUpdateUnix = _clockUnix;

        var result = new FeedResult
        {
            Tier = FeedingLines.ResolveTier(_state.Fullness),
            FullnessAfter = _state.Fullness,
            FullnessChanged = fullnessChanged,
            AffectionGain = settings.Enabled ? food.AffectionGain : 0,
            CoinsRewarded = taskReport.CoinsGained,
            TaskNotice = taskReport.Notice,
        };

        SaveStateInternal();
        NotifyChanged(
            fullness: fullnessChanged != 0,
            coins: taskReport.CoinsChanged,
            inventory: false,
            tasks: taskReport.TasksChanged);
        if (taskReport.Notice is not null) PushNotice(taskReport.Notice, CareNoticeKind.Reward);
        return result;
    }

    /// <summary>记录一次每日任务进度（摸摸头 / 聊天 / 喂食）；达到目标时按规则发奖励并提示。</summary>
    public void ReportDailyEvent(DailyTaskKind kind, DateTimeOffset now)
    {
        _clockUnix = now.ToUnixTimeSeconds();
        var report = RecordDailyEvent(kind, now);
        if (!report.TasksChanged && !report.CoinsChanged) return;

        SaveStateInternal();
        NotifyChanged(fullness: false, coins: report.CoinsChanged, inventory: false, tasks: report.TasksChanged);
        if (report.Notice is not null) PushNotice(report.Notice, CareNoticeKind.Reward);
    }

    /// <summary>把状态立刻落盘。</summary>
    public void SaveState() => SaveStateInternal();

    /// <summary>释放（落盘一次）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveStateInternal();
    }

    // ==================== 内部实现 ====================

    /// <summary>本地日期键（yyyy-MM-dd），跨天判断用。</summary>
    private static string TodayKey(DateTimeOffset now)
        => now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>自然掉值是否生效（总开关开 + 自然模式 + 未锁定）。</summary>
    private static bool IsNaturalDecayActive(PetCareSettings settings)
        => settings.Enabled && settings.FullnessNatural && !settings.FullnessLocked;

    /// <summary>饭团种类 → 库存数组下标（未知按 0，和 <see cref="RiceBallCatalog.Get"/> 一致）。</summary>
    private static int KindIndex(RiceBallKind kind)
    {
        var index = (int)kind;
        return index >= 0 && index < RiceBallCatalog.Count ? index : 0;
    }

    /// <summary>任务种类 → 进度数组下标（未知按 0）。</summary>
    private static int TaskIndex(DailyTaskKind kind)
    {
        var index = (int)kind;
        return index >= 0 && index < DailyTaskCatalog.Count ? index : 0;
    }

    /// <summary>饱食度夹取到 0~100 并保留 1 位小数（NaN 按 0 处理）。</summary>
    private static double ClampFullness(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = MinFullness;
        return Math.Round(Math.Clamp(value, MinFullness, MaxFullness), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>金币夹取到 0~100000（用 long 兜住大额加减，防 int 溢出）。</summary>
    private static int ClampCoins(long value) => (int)Math.Clamp(value, 0L, MaxCoins);

    /// <summary>
    /// 每日任务核心：跨天重置 → 已领奖就跳过 → 进度 +1（不超过目标）→ 达标标记领奖 + 按金币模式发奖。
    /// 返回本次是否有任务 / 金币变化，以及实际加了多少金币和完成提示（没有 = null）。
    /// </summary>
    private (bool TasksChanged, bool CoinsChanged, int CoinsGained, string? Notice) RecordDailyEvent(
        DailyTaskKind kind, DateTimeOffset now)
    {
        var settings = Settings;
        if (!settings.DailyTasksEnabled) return default;

        var tasksReset = ResetTasksIfNewDay(now);
        var index = TaskIndex(kind);
        var definition = DailyTaskCatalog.Tasks[index];
        var tasksChanged = tasksReset;
        var coinsGained = 0;
        string? notice = null;

        // 今天已经领过奖：只做跨天检查，不再累加进度
        if (!_state.TaskRewarded[index])
        {
            var progress = Math.Min(definition.TargetCount, _state.TaskProgress[index] + 1);
            if (progress != _state.TaskProgress[index])
            {
                _state.TaskProgress[index] = progress;
                tasksChanged = true;
            }

            if (progress >= definition.TargetCount)
            {
                // 达标：无论发不发金币都标记已领奖，今天不再重复提示
                _state.TaskRewarded[index] = true;
                tasksChanged = true;

                if (settings.CoinsNatural && !settings.CoinsLocked)
                {
                    var before = _state.Coins;
                    _state.Coins = ClampCoins((long)before + definition.RewardCoins);
                    coinsGained = _state.Coins - before;
                    notice = $"🎉 今日任务完成：{definition.Title}，+{definition.RewardCoins} 金币";
                }
                else
                {
                    notice = $"任务完成：{definition.Title}（金币自然模式关闭 / 已锁定，未发奖励）";
                }
            }
        }

        return (tasksChanged, coinsGained > 0, coinsGained, notice);
    }

    /// <summary>本地日期变了就把今日任务进度 / 领奖标记清零；返回是否发生了重置。</summary>
    private bool ResetTasksIfNewDay(DateTimeOffset now)
    {
        var today = TodayKey(now);
        if (string.Equals(_state.TaskDate, today, StringComparison.Ordinal)) return false;

        _state.TaskDate = today;
        _state.TaskProgress = new int[DailyTaskCatalog.Count];
        _state.TaskRewarded = new bool[DailyTaskCatalog.Count];
        return true;
    }

    /// <summary>发一个提示：先给事件订阅者，再转发给构造时的 Notice 回调（两者都可选）。</summary>
    private void PushNotice(string text, CareNoticeKind kind)
    {
        var args = new CareNoticeEventArgs { Text = text, Kind = kind };
        NoticeRaised?.Invoke(this, args);
        _options.Notice?.Invoke(args);
    }

    /// <summary>状态变化通知：按变化的面发 PropertyChanged，再发一次 StateChanged（StatusText 总会变）。</summary>
    private void NotifyChanged(bool fullness, bool coins, bool inventory, bool tasks)
    {
        if (fullness)
        {
            OnPropertyChanged(nameof(Fullness));
            OnPropertyChanged(nameof(Tier));
            OnPropertyChanged(nameof(TierText));
        }
        if (coins) OnPropertyChanged(nameof(Coins));
        if (inventory) OnPropertyChanged(nameof(TotalInventory));
        if (tasks) OnPropertyChanged(nameof(CompletedTaskCount));
        OnPropertyChanged(nameof(StatusText));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void Log(string message) => _options.Log?.Invoke(message);

    // ---------- 落盘 ----------

    /// <summary>构造时恢复存档；没有 / 读失败就按设置里的初始值新建一份（首次运行）。</summary>
    private PetCareState LoadState()
    {
        var now = DateTimeOffset.Now;
        var settings = Settings;
        PetCareState? state = null;

        var storage = _options.Storage;
        if (storage is not null)
        {
            try
            {
                var json = storage.ReadText(StateFileName);
                if (!string.IsNullOrWhiteSpace(json))
                    state = JsonSerializer.Deserialize<PetCareState>(json, JsonFileStore.Options);
            }
            catch (Exception ex)
            {
                // 读失败只记日志：按新存档开始，绝不打断桌宠启动
                Log("读取养成状态失败（按新存档开始）：" + ex.Message);
                state = null;
            }
        }

        if (state is null)
        {
            return new PetCareState
            {
                Fullness = ClampFullness(settings.FullnessInitial),
                LastUpdateUnix = now.ToUnixTimeSeconds(),
                Coins = ClampCoins(settings.CoinsInitial),
                Inventory = new int[RiceBallCatalog.Count],
                TaskDate = TodayKey(now),
                TaskProgress = new int[DailyTaskCatalog.Count],
                TaskRewarded = new bool[DailyTaskCatalog.Count],
            };
        }

        // 恢复 + 归一化（存档可能来自旧版本 / 被手改过：数组长度、越界值、坏日期都兜住）
        state.Fullness = ClampFullness(state.Fullness);
        state.Coins = ClampCoins(state.Coins);
        state.Inventory = NormalizeInventory(state.Inventory);
        state.TaskProgress = NormalizeTaskProgress(state.TaskProgress);
        state.TaskRewarded = NormalizeTaskRewarded(state.TaskRewarded);
        if (state.LastUpdateUnix <= 0) state.LastUpdateUnix = now.ToUnixTimeSeconds();

        // 跨天：构造时就把任务清干净，界面一打开就是「今日 0/3」
        if (!string.Equals(state.TaskDate, TodayKey(now), StringComparison.Ordinal))
        {
            state.TaskDate = TodayKey(now);
            state.TaskProgress = new int[DailyTaskCatalog.Count];
            state.TaskRewarded = new bool[DailyTaskCatalog.Count];
        }

        return state;
    }

    /// <summary>库存数组归一化成长度 4、每项 0~99。</summary>
    private static int[] NormalizeInventory(int[]? source)
    {
        var result = new int[RiceBallCatalog.Count];
        if (source is null) return result;
        for (var i = 0; i < result.Length && i < source.Length; i++)
            result[i] = Math.Clamp(source[i], 0, MaxInventoryPerKind);
        return result;
    }

    /// <summary>任务进度数组归一化成长度 3、每项 0~目标次数。</summary>
    private static int[] NormalizeTaskProgress(int[]? source)
    {
        var result = new int[DailyTaskCatalog.Count];
        if (source is null) return result;
        for (var i = 0; i < result.Length && i < source.Length; i++)
            result[i] = Math.Clamp(source[i], 0, DailyTaskCatalog.Tasks[i].TargetCount);
        return result;
    }

    /// <summary>领奖标记数组归一化成长度 3。</summary>
    private static bool[] NormalizeTaskRewarded(bool[]? source)
    {
        var result = new bool[DailyTaskCatalog.Count];
        if (source is null) return result;
        for (var i = 0; i < result.Length && i < source.Length; i++)
            result[i] = source[i];
        return result;
    }

    /// <summary>落盘（失败只记日志，不抛给调用方）。</summary>
    private void SaveStateInternal()
    {
        var storage = _options.Storage;
        if (storage is null) return;
        try
        {
            var json = JsonSerializer.Serialize(_state, JsonFileStore.Options);
            var path = storage.WriteText(StateFileName, json);
            if (path is null) Log("保存养成状态失败：写入被拒绝（磁盘只读 / 被占用？）");
        }
        catch (Exception ex)
        {
            Log("保存养成状态失败：" + ex.Message);
        }
    }
}
