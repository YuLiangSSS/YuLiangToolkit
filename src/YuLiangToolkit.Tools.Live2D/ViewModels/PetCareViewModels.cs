using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Live2D.Play;

namespace YuLiangToolkit.Tools.Live2D.ViewModels;

/// <summary>
/// 「🍙 饥饿 · 金币 · 每日任务」设置卡里饭团商店的一行：
/// 形状字符 / 名字 / 说明 / 价格 / 回复数值 / 当前库存。
///
/// <para>行对象是固定的（<see cref="RiceBallCatalog.Foods"/> 4 个），养成状态变化时只刷
/// <see cref="InventoryText"/>，不重建集合，免得每 2 秒刷一次把 ScrollViewer 滚动位置抖掉。</para>
///
/// <para>「买一个」「扔出去」按钮在 XAML 里用 <c>Click</c> + <c>Tag="{Binding Kind}"</c>（Tag 是
/// <see cref="RiceBallKind"/> 枚举）交给页面后置代码转成 <c>Live2DViewModel</c> 的命令。</para>
/// </summary>
public sealed class RiceBallRowViewModel : ObservableObject
{
    private string _inventoryText;

    /// <summary>按目录里的饭团创建一个行。</summary>
    /// <param name="food">饭团定义（名字 / 价格 / 回复数值都是固定的）。</param>
    /// <param name="inventory">当前库存数量。</param>
    public RiceBallRowViewModel(RiceBallFood food, int inventory)
    {
        Kind = food.Kind;
        Name = food.Name;
        Description = food.Description;
        Glyph = food.Glyph;
        PriceText = $"{food.Price} 金币";
        ValueText = $"饱食 +{food.FullnessGain:0.#} · 好感 +{food.AffectionGain:0.#}";
        _inventoryText = FormatInventory(inventory);
    }

    /// <summary>饭团种类（按钮 <c>Tag</c> 绑它，后置代码据此转成命令参数）。</summary>
    public RiceBallKind Kind { get; }

    /// <summary>名字（如「三角形饭团」）。</summary>
    public string Name { get; }

    /// <summary>一句话说明。</summary>
    public string Description { get; }

    /// <summary>形状字符（△ / ○ / ▭ / ⬭，省得为四种形状各做一份图标资源）。</summary>
    public string Glyph { get; }

    /// <summary>价格文本（如「10 金币」）。</summary>
    public string PriceText { get; }

    /// <summary>吃掉后的回复文本（如「饱食 +15 · 好感 +1」）。</summary>
    public string ValueText { get; }

    /// <summary>库存文本（如「库存 2」）。</summary>
    public string InventoryText
    {
        get => _inventoryText;
        private set => Set(ref _inventoryText, value);
    }

    /// <summary>刷新库存文本（养成状态变化时调用）。</summary>
    public void UpdateInventory(int inventory) => InventoryText = FormatInventory(inventory);

    private static string FormatInventory(int inventory) => $"库存 {Math.Max(0, inventory)}";
}

/// <summary>
/// 「🍙 饥饿 · 金币 · 每日任务」设置卡里今日任务的一行：
/// 标题 / 说明 / 进度 / 奖励金币 / 是否完成。
///
/// <para>每日任务目录固定 3 个，进度按本地日期每天 0 点重置；行对象复用，只刷新进度与状态。</para>
/// </summary>
public sealed class DailyTaskRowViewModel : ObservableObject
{
    private string _progressText;
    private string _statusText;
    private bool _isDone;

    /// <summary>按目录里的任务创建一个行。</summary>
    /// <param name="task">任务定义（标题 / 说明 / 目标次数 / 奖励金币是固定的）。</param>
    /// <param name="progress">今天已经完成的次数。</param>
    /// <param name="rewarded">今天是否已经发过奖励。</param>
    public DailyTaskRowViewModel(DailyTaskDefinition task, int progress, bool rewarded)
    {
        Kind = task.Kind;
        Title = task.Title;
        Description = task.Description;
        TargetCount = task.TargetCount;
        RewardText = $"+{task.RewardCoins} 金币";
        _progressText = "";
        _statusText = "";
        Update(progress, rewarded);
    }

    /// <summary>任务种类（目前没绑界面，留给以后按种类跳转 / 调试）。</summary>
    public DailyTaskKind Kind { get; }

    /// <summary>标题（如「摸摸头」）。</summary>
    public string Title { get; }

    /// <summary>说明（如「摸摸大肥鱼的头 3 次」）。</summary>
    public string Description { get; }

    /// <summary>目标次数。</summary>
    public int TargetCount { get; }

    /// <summary>奖励文本（如「+6 金币」）。</summary>
    public string RewardText { get; }

    /// <summary>进度文本（如「1/3」）。</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>今天是否已经完成（进度达到目标；金币锁定 / 手动模式时可能已完成但没发奖励）。</summary>
    public bool IsDone
    {
        get => _isDone;
        private set => Set(ref _isDone, value);
    }

    /// <summary>状态文本（「✓ 已完成」/「已完成（奖励未发）」/「未完成」）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>刷新进度与完成状态（养成状态变化 / 跨天重置时调用）。</summary>
    public void Update(int progress, bool rewarded)
    {
        var done = progress >= TargetCount;
        ProgressText = $"{Math.Clamp(progress, 0, TargetCount)}/{TargetCount}";
        IsDone = done;
        StatusText = done
            ? (rewarded ? "✓ 已完成" : "已完成（奖励未发）")
            : "未完成";
    }
}
