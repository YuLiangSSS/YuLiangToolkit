using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.OsuMania.ViewModels;
using YuLiangToolkit.Tools.OsuMania.Views;

namespace YuLiangToolkit.Tools.OsuMania;

/// <summary>
/// 「osu! 等级计算器」：拖入 .osu 批量计算 mania 星数（Star Rating）与等级（Level）。
///
/// <para>星数由 <c>Core/Difficulty/StarRatingCalculator</c> 计算（早期版本 Star Rating Rebirth / srcalc 思路的
/// C# 重写，其中数学工具来自 ppy osu!lazer，MIT；**不是 osu! 官方 mania 难度算法**，与游戏内 SR
/// 可能有 1 星以上差异），等级由 <c>Core/LevelCalculator</c> 按这套 SR 拟合的旧公式换算；
/// 两者都只读谱面、**不修改任何文件**。</para>
/// </summary>
public class OsuLevelCalculatorTool : ITool
{
    public string Id => OsuLevelCalculatorConfig.ToolId;

    public string Name => "osu! 等级计算器";

    public string Description => "拖入 .osu 批量计算 mania 星数（Star Rating）与等级（Level），可看 OD / 键数 / 谱师等谱面信息";

    public string EmojiIcon => "⭐";

    public string Category => "音游工具";

    /// <summary>收进「音游工具」类别页。</summary>
    public IReadOnlyList<string> CategoryPath => new[] { "音游工具" };

    public IReadOnlyList<string> Tags => new[]
    {
        "osu", "osu!", "mania", "音游", "谱面", "beatmap", "星数", "star", "rating", "等级", "level", "难度",
    };

    public int Order => 53;

    public FrameworkElement CreateView() => new OsuLevelCalculatorView { DataContext = new OsuLevelCalculatorViewModel() };
}
