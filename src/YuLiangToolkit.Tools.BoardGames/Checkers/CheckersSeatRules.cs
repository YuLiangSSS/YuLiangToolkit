namespace YuLiangToolkit.Tools.BoardGames.Checkers;

/// <summary>
/// 「几人局该占哪几个角」的规则（需求里明确写了 2 / 3 / 4 / 6 人，**5 人不支持**）。
///
/// <para><b>规则</b>：</para>
/// <list type="bullet">
///   <item><b>2 人</b>：面对面 —— 上/下、左上/右下、左下/右上；</item>
///   <item><b>3 人</b>：上 + 左下 + 右下，或下 + 左上 + 右上；</item>
///   <item><b>4 人</b>：两两相邻、每组之间隔一个空位 —— 左上+左下 &amp; 右上+右下，或 下+左下 &amp; 上+右上；</item>
///   <item><b>6 人</b>：六个角全占。</item>
/// </list>
///
/// <para><b>「第一个人定规则」</b>：房主 / 第一个选位的人一旦选了某个角，
/// 就确定了这一局用哪一套组合，后面的人只能在这套组合里挑剩下的。</para>
/// </summary>
public static class CheckersSeatRules
{
    /// <summary>支持的人数（5 人不支持）。</summary>
    public static IReadOnlyList<int> SupportedCounts { get; } = new[] { 2, 3, 4, 6 };

    /// <summary>
    /// 所有合法的「座位组合」（每种人数下的几种标准排布）。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<CheckersPoint>> CombinationsOf(int playerCount) => playerCount switch
    {
        2 => new[]
        {
            new[] { CheckersPoint.North, CheckersPoint.South },
            new[] { CheckersPoint.NorthWest, CheckersPoint.SouthEast },
            new[] { CheckersPoint.SouthWest, CheckersPoint.NorthEast },
        },
        3 => new[]
        {
            new[] { CheckersPoint.North, CheckersPoint.SouthWest, CheckersPoint.SouthEast },
            new[] { CheckersPoint.South, CheckersPoint.NorthWest, CheckersPoint.NorthEast },
        },
        4 => new[]
        {
            // 左边一组（左上 + 左下）+ 右边一组（右上 + 右下）
            new[] { CheckersPoint.NorthWest, CheckersPoint.SouthWest, CheckersPoint.NorthEast, CheckersPoint.SouthEast },
            // 下面一组（下 + 左下）+ 上面一组（上 + 右上）
            new[] { CheckersPoint.South, CheckersPoint.SouthWest, CheckersPoint.North, CheckersPoint.NorthEast },
            // 下面一组（下 + 右下）+ 上面一组（上 + 左上）
            new[] { CheckersPoint.South, CheckersPoint.SouthEast, CheckersPoint.North, CheckersPoint.NorthWest },
        },
        6 => new[]
        {
            new[]
            {
                CheckersPoint.North, CheckersPoint.NorthEast, CheckersPoint.SouthEast,
                CheckersPoint.South, CheckersPoint.SouthWest, CheckersPoint.NorthWest,
            },
        },
        _ => Array.Empty<IReadOnlyList<CheckersPoint>>(),
    };

    /// <summary>这个人数支不支持。</summary>
    public static bool IsSupported(int playerCount) => SupportedCounts.Contains(playerCount);

    /// <summary>
    /// 第一个人选了 <paramref name="firstPick"/> 之后，这一局用的是哪套组合。
    /// </summary>
    /// <param name="playerCount">人数。</param>
    /// <param name="firstPick">第一个人选的角。</param>
    /// <param name="preferred">有多套组合都含这个角时，优先用第几套（界面上让房主选）。</param>
    /// <returns>选中的组合（该角不在任何组合里则返回 null）。</returns>
    public static IReadOnlyList<CheckersPoint>? Resolve(
        int playerCount, CheckersPoint firstPick, int preferred = 0)
    {
        var combos = CombinationsOf(playerCount)
            .Where(c => c.Contains(firstPick))
            .ToList();

        if (combos.Count == 0) return null;

        var index = Math.Clamp(preferred, 0, combos.Count - 1);
        return combos[index];
    }

    /// <summary>
    /// 已经选了 <paramref name="picked"/> 之后，剩下的可选角。
    /// </summary>
    public static IReadOnlyList<CheckersPoint> Remaining(int playerCount, IReadOnlyList<CheckersPoint> picked)
    {
        if (picked.Count == 0)
        {
            // 还没人选：所有角都能选
            return Enum.GetValues<CheckersPoint>();
        }

        var combo = Resolve(playerCount, picked[0]);
        if (combo is null) return Array.Empty<CheckersPoint>();

        // 后续的人只能在「这一套组合」里挑还没被选的
        return combo.Where(c => !picked.Contains(c)).ToList();
    }

    /// <summary>
    /// 校验一组选角是否合法（人数对、属于同一套组合）。
    /// </summary>
    public static bool IsValidSelection(int playerCount, IReadOnlyList<CheckersPoint> picked)
    {
        if (picked.Count != playerCount) return false;
        if (picked.Distinct().Count() != picked.Count) return false;

        return CombinationsOf(playerCount)
            .Any(combo => combo.All(picked.Contains) && picked.All(combo.Contains));
    }

    /// <summary>「只允许这几个角」的描述（界面提示用）。</summary>
    public static string DescribeAllowed(int playerCount, IReadOnlyList<CheckersPoint> picked)
    {
        if (picked.Count == 0)
            return $"{playerCount} 人局：第一个人可以随意选位置";

        var combo = Resolve(playerCount, picked[0]);
        if (combo is null) return "这个组合不合法";

        var remaining = combo.Where(c => !picked.Contains(c)).ToList();
        return remaining.Count == 0
            ? "座位已满"
            : $"这套组合剩下的位置：{string.Join("、", remaining.Select(CheckersBoard.DisplayName))}";
    }
}
