namespace YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

/// <summary>
/// 自动上场方式（一局结束后怎么换人）：
/// <list type="bullet">
///   <item><see cref="Manual"/>：手动安排（默认）—— 房主自己挑下一局上场的两个人；</item>
///   <item><see cref="LoserOut"/>：输的下场 —— 赢家留场，输家下场换一个人；</item>
///   <item><see cref="BothOut"/>：双方都下场 —— 两个人都换掉（平局也换）。</item>
/// </list>
/// </summary>
public enum BoardAutoRotateMode
{
    /// <summary>手动安排（默认）：一局结束后不动席位，等房主自己选人。</summary>
    Manual = 0,

    /// <summary>输的下场：赢家留场，输家下场，从替补里补一个人。</summary>
    LoserOut = 1,

    /// <summary>双方都下场：两个人都下去，从替补里补两个人。</summary>
    BothOut = 2,
}

/// <summary>补位方式（从「没在场上的人」里怎么挑人）。</summary>
public enum BoardAutoPickMode
{
    /// <summary>轮流上场（默认）：没上过场的优先，同样场次按进房先后顺序。</summary>
    Rotation = 0,

    /// <summary>随机上场：没上过场的人里随机挑。</summary>
    Random = 1,
}

/// <summary>
/// 自动轮换的候选成员（纯数据，跟界面/网络无关）：
/// 只带排人需要的字段 —— 在线状态、上场次数、进房顺序。
/// </summary>
public sealed record BoardRotationCandidate(
    string MemberId,
    string Name,
    bool IsOnline,
    int GamesPlayed,
    int JoinOrder);

/// <summary>
/// 自动轮换算出来的下一局席位：
/// <see cref="SeatOneMemberId"/> / <see cref="SeatTwoMemberId"/> 是下一局两个席位上的成员 id；
/// <see cref="Changed"/> 表示席位跟本局相比有没有变化（false = 保持原上场）；
/// <see cref="Reason"/> 是一句中文说明（会进聊天提示，例如「输的下场：小明替换小红」）。
/// </summary>
public sealed record BoardRotationResult(
    string SeatOneMemberId,
    string SeatTwoMemberId,
    bool Changed,
    string Reason);

/// <summary>
/// 自动轮换的「排人规则」（<b>纯逻辑</b>，不碰界面 / 配置 / 网络，自检可直接断言）。
///
/// <para><b>候选池</b> = 房间里<b>在线</b>、且<b>不在当前两个席位上</b>的成员 —— 场上正在打的两个人
/// 不参与补位（输家是「下场」，更不能再被自己顶回来）。</para>
///
/// <para><b>排序</b>：先按 <see cref="BoardRotationCandidate.GamesPlayed"/> 升序（没上过场 / 上场少的优先）；
/// 场次相同的再按补位方式决定 —— 「轮流上场」按进房顺序（<see cref="BoardRotationCandidate.JoinOrder"/>）升序，
/// 「随机上场」在同样场次的人里随机打乱（允许传固定种子的 <see cref="Random"/>，结果可复现）。</para>
///
/// <para><b>人数不够就回退</b>：输的下场至少要有 1 个替补、双方都下场至少要有 2 个替补，
/// 否则保持原上场并给出原因（不会把人强行排重）。</para>
/// </summary>
public static class BoardRotationPlanner
{
    /// <summary>手动安排时返回的固定说明。</summary>
    public const string ManualReason = "手动安排";

    /// <summary>没人可换时的固定说明（自检 / 提示文案都认它）。</summary>
    public const string NoReplacementReason = "没有可替换的选手，维持原上场";

    /// <summary>
    /// 按自动轮换设置算下一局的两个席位。
    /// </summary>
    /// <param name="mode">上场方式（手动 / 输的下场 / 双方都下场）。</param>
    /// <param name="pickMode">补位方式（轮流 / 随机）。</param>
    /// <param name="currentSeatOneMemberId">本局先手位成员 id（空字符串 = 空位）。</param>
    /// <param name="currentSeatTwoMemberId">本局后手位成员 id（空字符串 = 空位）。</param>
    /// <param name="winnerMemberId">本局赢家成员 id；平局 / 不知道时传 null 或空字符串。</param>
    /// <param name="loserMemberId">本局输家成员 id；平局 / 不知道时传 null 或空字符串。</param>
    /// <param name="members">房间里所有成员（含当前场上的两人，内部自己筛）。</param>
    /// <param name="random">随机补位用的随机源；传 null 用 <see cref="Random.Shared"/>。自检传固定种子可复现。</param>
    public static BoardRotationResult Plan(
        BoardAutoRotateMode mode,
        BoardAutoPickMode pickMode,
        string currentSeatOneMemberId,
        string currentSeatTwoMemberId,
        string? winnerMemberId,
        string? loserMemberId,
        IReadOnlyList<BoardRotationCandidate> members,
        Random? random = null)
    {
        var seatOne = Normalize(currentSeatOneMemberId);
        var seatTwo = Normalize(currentSeatTwoMemberId);

        // ① 手动安排：席位原样不动，等房主自己挑人
        if (mode == BoardAutoRotateMode.Manual)
            return new BoardRotationResult(seatOne, seatTwo, false, ManualReason);

        // 候选池：在线的、不在场上的成员（按场次 / 补位方式排好）
        var pool = BuildPool(members, seatOne, seatTwo, pickMode, random);

        // ② 输的下场：赢家留场，输家的位置换一个人
        if (mode == BoardAutoRotateMode.LoserOut)
        {
            var winner = Normalize(winnerMemberId);
            var loser = Normalize(loserMemberId);

            if (winner.Length == 0 || loser.Length == 0)
                return new BoardRotationResult(seatOne, seatTwo, false, "平局：不轮换，维持原上场");

            var winnerIsSeatOne = string.Equals(winner, seatOne, StringComparison.Ordinal);
            var winnerIsSeatTwo = string.Equals(winner, seatTwo, StringComparison.Ordinal);
            var loserIsSeatOne = string.Equals(loser, seatOne, StringComparison.Ordinal);
            var loserIsSeatTwo = string.Equals(loser, seatTwo, StringComparison.Ordinal);

            // 赢家 / 输家跟场上两个席位对不上（数据异常 / 中途作废）：不动，别瞎换
            if (!(winnerIsSeatOne && loserIsSeatTwo) && !(winnerIsSeatTwo && loserIsSeatOne))
                return new BoardRotationResult(seatOne, seatTwo, false, "本局结果对不上场上选手，维持原上场");

            if (pool.Count < 1)
                return new BoardRotationResult(seatOne, seatTwo, false, NoReplacementReason);

            var newcomer = pool[0];
            // 赢家留在自己原来的席位（不换边），输家的位置由新人顶上
            var newOne = winnerIsSeatOne ? seatOne : newcomer.MemberId;
            var newTwo = winnerIsSeatOne ? newcomer.MemberId : seatTwo;
            var loserName = loserIsSeatOne ? NameOf(members, seatOne) : NameOf(members, seatTwo);
            var reason = $"输的下场：{DisplayName(newcomer)}替换{loserName}";
            return new BoardRotationResult(newOne, newTwo, !SameSeats(newOne, newTwo, seatOne, seatTwo), reason);
        }

        // ③ 双方都下场：两个位置都换人（平局也换）
        if (pool.Count < 2)
            return new BoardRotationResult(seatOne, seatTwo, false, NoReplacementReason);

        var first = pool[0];
        var second = pool[1];
        var changed = !SameSeats(first.MemberId, second.MemberId, seatOne, seatTwo);
        var text = changed
            ? $"双方下场：{DisplayName(first)}、{DisplayName(second)}上场"
            : NoReplacementReason;
        return new BoardRotationResult(first.MemberId, second.MemberId, changed, text);
    }

    /// <summary>两个席位有没有变化（空字符串也照常比较）。</summary>
    private static bool SameSeats(string one, string two, string oldOne, string oldTwo)
        => string.Equals(one, oldOne, StringComparison.Ordinal)
           && string.Equals(two, oldTwo, StringComparison.Ordinal);

    /// <summary>
    /// 生成排好序的候选池：在线 + 不在两个席位上；
    /// 场次升序 → 同场次按补位方式（轮流 = 进房顺序；随机 = 洗牌）。
    /// </summary>
    private static List<BoardRotationCandidate> BuildPool(
        IReadOnlyList<BoardRotationCandidate>? members,
        string seatOne,
        string seatTwo,
        BoardAutoPickMode pickMode,
        Random? random)
    {
        var pool = new List<BoardRotationCandidate>();
        if (members is null) return pool;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member is null) continue;
            var id = Normalize(member.MemberId);
            if (id.Length == 0) continue;
            if (string.Equals(id, seatOne, StringComparison.Ordinal)) continue;   // 场上的人不补位
            if (string.Equals(id, seatTwo, StringComparison.Ordinal)) continue;
            if (!member.IsOnline) continue;                                       // 离线的人不能上场
            if (!seen.Add(id)) continue;                                          // 防御：重复 id 只算一次
            pool.Add(member);
        }

        if (pickMode == BoardAutoPickMode.Random)
        {
            // 先按场次分组（组间升序），组内洗牌 —— 保证「没上过场优先」不被随机性破坏；
            // 同一个 Random 种子 / 同一个初始顺序 → 结果可复现。
            var rng = random ?? Random.Shared;
            var ordered = new List<BoardRotationCandidate>(pool.Count);
            foreach (var group in pool.GroupBy(m => m.GamesPlayed).OrderBy(g => g.Key))
            {
                var shuffled = group.ToList();
                Shuffle(shuffled, rng);
                ordered.AddRange(shuffled);
            }
            return ordered;
        }

        // 轮流上场：场次升序 → 进房顺序升序 → 成员 id（最后这层只为让结果稳定）
        pool.Sort(static (a, b) =>
        {
            var byGames = a.GamesPlayed.CompareTo(b.GamesPlayed);
            if (byGames != 0) return byGames;
            var byJoin = a.JoinOrder.CompareTo(b.JoinOrder);
            if (byJoin != 0) return byJoin;
            return string.CompareOrdinal(a.MemberId, b.MemberId);
        });
        return pool;
    }

    /// <summary>Fisher-Yates 洗牌（用调用方给的随机源，便于固定种子复现）。</summary>
    private static void Shuffle<T>(List<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>成员 id 归一化：null / 空白 → 空字符串（= 空位）。</summary>
    private static string Normalize(string? memberId) => (memberId ?? "").Trim();

    /// <summary>从成员列表里找一个成员的显示名（找不到 / 空名给兜底文案）。</summary>
    private static string NameOf(IReadOnlyList<BoardRotationCandidate>? members, string memberId)
    {
        if (members is not null)
        {
            foreach (var member in members)
            {
                if (member is null) continue;
                if (string.Equals(Normalize(member.MemberId), memberId, StringComparison.Ordinal))
                    return DisplayName(member);
            }
        }
        return "原选手";
    }

    /// <summary>候选人的显示名（空名给「选手」）。</summary>
    private static string DisplayName(BoardRotationCandidate candidate)
    {
        var name = (candidate.Name ?? "").Trim();
        if (name.Length == 0) name = (candidate.MemberId ?? "").Trim();
        return name.Length == 0 ? "选手" : name;
    }
}

/// <summary>
/// 自动轮换设置的「值 ↔ 存储字符串 / 显示文案」转换。
/// <para>配置文件和协议里存英文键：<c>manual</c> / <c>loserOut</c> / <c>bothOut</c>、<c>rotation</c> / <c>random</c>；
/// 界面上显示中文。别在别处手写这些字符串，免得两边拼错。</para>
/// </summary>
public static class BoardAutoModes
{
    /// <summary>上场方式 → 存储键（manual / loserOut / bothOut）。</summary>
    public static string ToKey(BoardAutoRotateMode mode) => mode switch
    {
        BoardAutoRotateMode.LoserOut => "loserOut",
        BoardAutoRotateMode.BothOut => "bothOut",
        _ => "manual",
    };

    /// <summary>存储键 → 上场方式（认不出 / 空 → 手动安排）。</summary>
    public static BoardAutoRotateMode ParseRotate(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "loserout" => BoardAutoRotateMode.LoserOut,
        "bothout" => BoardAutoRotateMode.BothOut,
        _ => BoardAutoRotateMode.Manual,
    };

    /// <summary>补位方式 → 存储键（rotation / random）。</summary>
    public static string ToKey(BoardAutoPickMode mode)
        => mode == BoardAutoPickMode.Random ? "random" : "rotation";

    /// <summary>存储键 → 补位方式（认不出 / 空 → 轮流上场）。</summary>
    public static BoardAutoPickMode ParsePick(string? key)
        => (key ?? "").Trim().ToLowerInvariant() == "random" ? BoardAutoPickMode.Random : BoardAutoPickMode.Rotation;

    /// <summary>上场方式的中文名（下拉框 / 提示用）。</summary>
    public static string Describe(BoardAutoRotateMode mode) => mode switch
    {
        BoardAutoRotateMode.LoserOut => "输的下场",
        BoardAutoRotateMode.BothOut => "双方都下场",
        _ => "手动安排",
    };

    /// <summary>补位方式的中文名（下拉框 / 提示用）。</summary>
    public static string Describe(BoardAutoPickMode mode)
        => mode == BoardAutoPickMode.Random ? "随机上场" : "轮流上场";
}
