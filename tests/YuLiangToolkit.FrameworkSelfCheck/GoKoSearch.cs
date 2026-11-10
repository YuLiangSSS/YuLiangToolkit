using YuLiangToolkit.Tools.BoardGames.Go;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 围棋「打劫」局面的<b>自动构造</b>（自检用）。
///
/// <para>手工摆劫形很容易算错气（踩过好几次），这里改成<b>搜索</b>：
/// 在 6×6 小棋盘上做随机/系统性落子搜索，找到一个「落子 → 提子 → 对方回提被禁」的状态序列，
/// 直接交给自检验证 <see cref="GoGame.Play"/> 的劫判定。</para>
/// </summary>
internal static class GoKoSearch
{
    /// <summary>
    /// 搜索一个能形成打劫禁着的序列。
    /// </summary>
    /// <returns>成功返回「黑方落子坐标（提子那手）」与棋盘尺寸；失败返回 null。</returns>
    public static (int Size, int ToX, int ToY, GoPoint CapturedPoint)? Find()
    {
        // 在 5×5 上做广度优先随机搜索：找一个黑落子提掉 1 子、且白立刻回提被判劫的局面。
        const int size = 5;
        var rng = new Random(20260808);

        for (var attempt = 0; attempt < 4000; attempt++)
        {
            var game = new GoGame(size);
            var startMoves = rng.Next(4, 14);

            // 随机铺一些子（跳过非法手）
            for (var i = 0; i < startMoves; i++)
            {
                var x = rng.Next(size);
                var y = rng.Next(size);
                if (game.Play(game.Turn, x, y) != GoPlayResult.Ok)
                {
                    // 换个点再试一次
                    for (var k = 0; k < 6; k++)
                    {
                        var nx = rng.Next(size);
                        var ny = rng.Next(size);
                        if (game.Play(game.Turn, nx, ny) == GoPlayResult.Ok) break;
                    }
                }
            }

            // 现在轮到某一方；枚举所有能提子的一手
            var turn = game.Turn;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (game.Get(x, y) != GoCell.Empty) continue;

                    var probe = GoGame.FromSnapshot(game.ToSnapshot()).Game;
                    if (probe is null) continue;

                    var result = probe.Play(turn, x, y);
                    if (result != GoPlayResult.Ok) continue;
                    if (probe.LastMove is not { Captured.Count: 1 } last) continue;

                    // 提完一子后，对面立刻下在被提的位置：应当被判劫
                    var capturedPoint = last.Captured[0];
                    var koTest = probe.Play(probe.Turn, capturedPoint.X, capturedPoint.Y);
                    if (koTest == GoPlayResult.Ko)
                    {
                        return (size, x, y, capturedPoint);
                    }
                }
            }
        }

        return null;
    }
}
