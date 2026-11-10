namespace YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

/// <summary>
/// 多席位（2~4 人）的换人规则 —— <b>纯逻辑</b>，不碰界面 / 网络，自检直接断言。
///
/// <para>跟 <see cref="BoardRotationPlanner"/> 的区别：那个是写死「两个席位」的（五子棋 / 象棋），
/// 这里支持斗地主 3 席、四国军棋 4 席。<b>换人模式沿用同一套枚举</b>
/// （<see cref="BoardAutoRotateMode.Manual"/> / <see cref="BoardAutoRotateMode.LoserOut"/> /
/// <see cref="BoardAutoRotateMode.BothOut"/>），房主在下拉框里选，跟棋牌其它游戏一致。</para>
///
/// <para><b>排队池</b> = 房间里在线、且不在当前场上席位上的成员；按「上场次数升序 →
/// 进房顺序 / 随机」排（跟 <see cref="BoardRotationPlanner"/> 一样，没上过场的优先）。</para>
/// </summary>
public static class BoardMultiSeatRotation
{
    /// <summary>一个席位当前是谁（<paramref name="MemberId"/> 空 = 空位）。</summary>
    /// <param name="MemberId">成员 id。</param>
    /// <param name="IsWinner">这个席位是不是本局赢家（不知道 / 平局传 false）。</param>
    public readonly record struct SeatOutcome(string MemberId, bool IsWinner);

    /// <summary>换人结果。</summary>
    /// <param name="Seats">下一局的席位成员 id（长度与传进来的席位一致）。</param>
    /// <param name="Changed">跟本局相比有没有换人。</param>
    /// <param name="Reason">一句话说明（进聊天 / 提示）。</param>
    public sealed record Result(IReadOnlyList<string> Seats, bool Changed, string Reason);

    /// <summary>
    /// 算下一局的席位。
    /// </summary>
    /// <param name="mode">换人模式（手动 / 输的下场 / 全都下场）。</param>
    /// <param name="pickMode">补位方式（轮流 / 随机）。</param>
    /// <param name="seats">本局席位（按座位顺序，含空位）。</param>
    /// <param name="members">房间里所有成员（含场上的，内部自己筛）。</param>
    /// <param name="random">随机补位用；null = <see cref="Random.Shared"/>。</param>
    public static Result Plan(
        BoardAutoRotateMode mode,
        BoardAutoPickMode pickMode,
        IReadOnlyList<SeatOutcome> seats,
        IReadOnlyList<BoardRotationCandidate> members,
        Random? random = null)
    {
        var current = seats.Select(s => Normalize(s.MemberId)).ToList();
        if (mode == BoardAutoRotateMode.Manual)
            return new Result(current, false, BoardRotationPlanner.ManualReason);

        var pool = BuildPool(members, current, pickMode, random);

        if (mode == BoardAutoRotateMode.LoserOut)
        {
            // 赢家留场；其余席位（包括空位）由排队池补
            var next = new List<string>(current.Count);
            var replaced = new List<string>();
            foreach (var (seat, index) in seats.Select((s, i) => (s, i)))
            {
                var id = Normalize(seat.MemberId);
                if (id.Length > 0 && seat.IsWinner)
                {
                    next.Add(id);
                    continue;
                }

                var newcomer = pool.FirstOrDefault(p => p.IsOnline && !next.Contains(p.MemberId));
                if (newcomer is null)
                {
                    next.Add(id);                       // 没人可补：保持原样
                    continue;
                }
                pool.Remove(newcomer);
                next.Add(newcomer.MemberId);
                replaced.Add(newcomer.Name);
            }

            if (replaced.Count == 0)
                return new Result(current, false, BoardRotationPlanner.NoReplacementReason);

            var changed = !next.SequenceEqual(current);
            return new Result(next, changed,
                changed ? $"输的下场：{string.Join("、", replaced)}上场" : BoardRotationPlanner.NoReplacementReason);
        }

        // 双方都下场：所有席位都换人（平局也换）
        if (pool.Count < current.Count)
            return new Result(current, false, BoardRotationPlanner.NoReplacementReason);

        var newcomers = pool.Take(current.Count).ToList();
        var nextSeats = newcomers.Select(p => p.MemberId).ToList();
        var isChanged = !nextSeats.SequenceEqual(current);
        return new Result(nextSeats, isChanged,
            isChanged ? $"全场换人：{string.Join("、", newcomers.Select(p => p.Name))}上场"
                      : BoardRotationPlanner.NoReplacementReason);
    }

    /// <summary>候选池：在线 + 不在当前席位上；场次升序 → 补位方式。</summary>
    private static List<BoardRotationCandidate> BuildPool(
        IReadOnlyList<BoardRotationCandidate> members,
        IReadOnlyList<string> currentSeats,
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
            if (currentSeats.Contains(id, StringComparer.Ordinal)) continue;
            if (!member.IsOnline) continue;
            if (!seen.Add(id)) continue;
            pool.Add(member);
        }

        if (pickMode == BoardAutoPickMode.Random)
        {
            var rng = random ?? Random.Shared;
            var ordered = new List<BoardRotationCandidate>(pool.Count);
            foreach (var group in pool.GroupBy(m => m.GamesPlayed).OrderBy(g => g.Key))
            {
                var shuffled = group.ToList();
                for (var i = shuffled.Count - 1; i > 0; i--)
                {
                    var j = rng.Next(i + 1);
                    (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
                }
                ordered.AddRange(shuffled);
            }
            return ordered;
        }

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

    private static string Normalize(string? memberId) => (memberId ?? "").Trim();
}
