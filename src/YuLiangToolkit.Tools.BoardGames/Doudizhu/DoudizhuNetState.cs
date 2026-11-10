namespace YuLiangToolkit.Tools.BoardGames.Doudizhu;

/// <summary>
/// 「斗地主」联机 / 单机的状态包装（房主 → 客户端）。
/// <para>客户端的快照来自 <see cref="DoudizhuGame.SnapshotFor"/>，<b>只带自己那一份手牌</b>。</para>
/// </summary>
public sealed class DoudizhuNetState
{
    /// <summary>状态序号（只增，客户端据此丢弃乱序旧包）。</summary>
    public int Seq { get; set; }

    /// <summary>这一份是给谁的（成员 id）。</summary>
    public string ForMemberId { get; set; } = "";

    /// <summary>玩家在哪个座位（-1 = 不在场上）。</summary>
    public int Seat { get; set; } = -1;

    /// <summary>席位上的成员 id（按座位 0~2）。</summary>
    public string[] SeatMemberIds { get; set; } = Array.Empty<string>();

    /// <summary>席位上的名字（按座位 0~2）。</summary>
    public string[] SeatNames { get; set; } = Array.Empty<string>();

    /// <summary>本座位的游戏快照（别人的手牌不在这里）。</summary>
    public DoudizhuSnapshot? Snapshot { get; set; }

    /// <summary>换人方式（界面显示用）。</summary>
    public string AutoRotateMode { get; set; } = "manual";

    /// <summary>场下的队友（排队中的名字）。</summary>
    public string[] WaitingNames { get; set; } = Array.Empty<string>();
}
