using YuLiangToolkit.Tools.BoardGames.Go;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 围棋规则的<b>局面构造验证</b>（自检用，纯逻辑）。
///
/// <para>围棋的提子 / 打劫测试很难靠人工推演摆对，这里统一用「摆局辅助」：
/// 直接往内部棋盘上放子（不经过对局校验），只用来<b>构造</b>特定局面；
/// 之后的提子 / 自杀 / 打劫判定仍然走真实的 <see cref="GoGame.Play"/>。</para>
/// </summary>
internal static class GoBoardSetup
{
    /// <summary>
    /// 摆一个「白棋单子只剩一口气」的局面（黑先手可提）。
    /// <para>方位：白 (1,0) 的邻居是 (0,0) (2,0) (1,1)。把 (2,0) (1,1) 放黑子，
    /// 则白 (1,0) 只剩 (0,0) 一口气 —— 黑下 (0,0) 即提。</para>
    /// </summary>
    /// <param name="game">棋局。</param>
    public static void SetupWhiteSingleStoneInAtari(GoGame game)
    {
        // 通过公开 API 摆：黑先落 (2,0)，白落 (1,0)，黑落 (1,1) —— 交替合法
        game.Reset();
        game.Play(GoCell.Black, 2, 0);      // 黑 (2,0)
        game.Play(GoCell.White, 1, 0);      // 白 (1,0)
        game.Play(GoCell.Black, 1, 1);      // 黑 (1,1)：白 (1,0) 只剩 (0,0)
        game.Play(GoCell.White, 8, 8);      // 白随便走一手，回合回到黑
    }

    /// <summary>
    /// 摆一个标准打劫局面：黑提掉白子后形成劫。
    /// <para>棋形（9 路棋盘左上角）：</para>
    /// <code>
    ///   (0,0) 空   (1,0) 白   (2,0) 黑
    ///   (0,1) 黑   (1,1) 黑
    /// </code>
    /// <para>白 (1,0) 的气只有 (0,0)；黑落 (0,0) 提掉它之后，
    /// 黑 (0,0) 这块<b>自己也只有一口气</b>（(1,0)，因为 (0,1) 被黑自己占着），于是形成劫。</para>
    /// </summary>
    public static void SetupKo(GoGame game)
    {
        game.Reset();
        game.Play(GoCell.Black, 0, 1);      // 黑 (0,1)：堵住 (0,0) 这一侧
        game.Play(GoCell.White, 1, 0);      // 白 (1,0)
        game.Play(GoCell.Black, 1, 1);      // 黑 (1,1)
        game.Play(GoCell.White, 8, 8);      // 白随便走
        game.Play(GoCell.Black, 2, 0);      // 黑 (2,0)：白 (1,0) 只剩 (0,0)
        game.Play(GoCell.White, 8, 7);      // 回合回到黑
    }
}
