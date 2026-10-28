using System.Text.Json;

namespace YuLiangToolkit.Tools.BoardGames.Shared;

// 说明：本文件的 DTO 全部放帧头（RoomFrame.Extra）里，字段名按 camelCase 序列化
// （由 RoomProtocol.JsonOptions 决定）。所有字段都当作「可缺失」处理：
// 传输层的第一原则是容错 —— 对方少个字段不能让本端崩。

/// <summary>客户端连上后发的「报家门」。</summary>
public sealed class HelloInfo
{
    /// <summary>固定是 <see cref="BoardProtocol.Magic"/>。</summary>
    public string Magic { get; set; } = "";

    /// <summary>要玩的游戏：<c>gomoku</c> / <c>xiangqi</c>（跟主机对不上就用不了）。</summary>
    public string GameId { get; set; } = "";

    /// <summary>玩家名（主机显示「谁谁谁」）。</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>协议版本（对不上就拒连并提示）。</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>
    /// 上次连接时主机分配的成员 id（<b>首次进房为空字符串</b>）。
    /// 重连时带上它，主机就能复用原成员（保留进房顺序 / 席位 / 上场次数）。
    /// </summary>
    public string MemberId { get; set; } = "";
}

/// <summary>主机回给客户端的握手回执。</summary>
public sealed class WelcomeInfo
{
    public string Magic { get; set; } = "";

    public string GameId { get; set; } = "";

    /// <summary>主机（房主）的玩家名。</summary>
    public string HostName { get; set; } = "";

    public int ProtocolVersion { get; set; }

    /// <summary>主机分配给本客户端的成员 id（之后发 hello 重连要带回来）。</summary>
    public string YourMemberId { get; set; } = "";

    /// <summary>包装后的完整房间状态（<see cref="BoardStateEnvelope"/>；可能为空 = 还没有状态）。</summary>
    public JsonElement? State { get; set; }
}

/// <summary>主机广播的最新完整状态（房间 + 棋局）。</summary>
public sealed class StateInfo
{
    /// <summary>包装后的完整状态（<c>{seq, room, game}</c>）。</summary>
    public JsonElement? State { get; set; }

    /// <summary>可选附带的提示（例如「开始新的一局」）。</summary>
    public string? Notice { get; set; }
}

/// <summary>客户端上报的游戏动作（走棋是 <c>move</c>，由各游戏自己定义 Type 与 Payload）。</summary>
public sealed class ActionInfo
{
    public string Type { get; set; } = "";

    public JsonElement? Payload { get; set; }
}

/// <summary>聊天消息。</summary>
public sealed class ChatInfo
{
    public string Name { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>true = 系统提示（进/出房间、对局提示等），界面用灰字显示。</summary>
    public bool IsSystem { get; set; }
}

/// <summary>系统提示（<see cref="Level"/>：info / warn / error）。</summary>
public sealed class NoticeInfo
{
    public string Text { get; set; } = "";

    public string Level { get; set; } = "info";
}

/// <summary>请求（悔棋 / 求和 / 再来一局 / 认输）。</summary>
public sealed class RequestInfo
{
    public string Type { get; set; } = "";

    /// <summary>可选附加数据（目前协议不用，留好扩展位）。</summary>
    public JsonElement? Payload { get; set; }
}

/// <summary>对请求的应答。</summary>
public sealed class ResponseInfo
{
    public string Type { get; set; } = "";

    /// <summary>true = 同意；false = 拒绝。</summary>
    public bool Accept { get; set; }

    /// <summary>可选附加数据（预留给以后扩展）。</summary>
    public JsonElement? Payload { get; set; }
}

/// <summary>礼貌离开（带原因，对方能立刻知道不是掉线）。</summary>
public sealed class ByeInfo
{
    public string? Reason { get; set; }
}

/// <summary>
/// 连接结束的原因分类（<see cref="BoardRoomSession.LastDisconnectKind"/>）——
/// 基类据此决定客户端要不要「自动重连」：只有网络问题才重试，对方主动关房 / 离开绝不重试。
/// </summary>
public enum BoardDisconnectKind
{
    /// <summary>还没断开 / 原因未知。</summary>
    None = 0,

    /// <summary>本端主动离开（用户点离开、切同机双人、关工具）——<b>绝不自动重连</b>。</summary>
    LocalLeave = 1,

    /// <summary>对方发来 bye（房主关闭房间 / 房主离开 / 被拒绝进房）——<b>不自动重连</b>。</summary>
    PeerBye = 2,

    /// <summary>TCP 正常关闭但没收到 bye（对方进程崩溃、系统直接断链）——可以重试几轮。</summary>
    PeerClosed = 3,

    /// <summary>连接被重置 / 超时 / 读写出错（网络掐断）——<b>自动重连</b>。</summary>
    NetworkError = 4,
}

/// <summary>
/// 房间里的一个成员（房主 + 每个接入的客户端）。
/// <para><see cref="GamesPlayed"/> / 席位等真正的房间信息由基类维护在 <see cref="BoardRoomState"/> 里；
/// 会话只负责成员身份、在线状态与进房顺序。</para>
/// </summary>
public sealed class BoardMemberInfo
{
    /// <summary>稳定 id：房主固定 <c>host</c>，客户端 <c>m1</c>/<c>m2</c>…（重连沿用）。</summary>
    public string MemberId { get; set; } = "";

    /// <summary>玩家名。</summary>
    public string Name { get; set; } = "";

    /// <summary>是不是房主（会话表里恒为 false，房主由基类合并）。</summary>
    public bool IsHost { get; set; }

    /// <summary>当前在不在线（掉线但保留在成员表里时为 false）。</summary>
    public bool IsOnline { get; set; }

    /// <summary>完整打完的局数（会话恒填 0，由基类维护真正的值后放进房间状态）。</summary>
    public int GamesPlayed { get; set; }

    /// <summary>进房顺序（越小越早；自动轮换「轮流上场」按它排）。</summary>
    public int JoinOrder { get; set; }

    /// <summary>复制一份（事件里传给基类，避免基类拿到会被后续改动的同一个对象）。</summary>
    public BoardMemberInfo Clone() => new()
    {
        MemberId = MemberId,
        Name = Name,
        IsHost = IsHost,
        IsOnline = IsOnline,
        GamesPlayed = GamesPlayed,
        JoinOrder = JoinOrder,
    };
}

/// <summary>
/// 房间状态（房主权威；客户端只读）。基类把它和棋局一起包进 <see cref="BoardStateEnvelope"/> 广播。
/// </summary>
public sealed class BoardRoomState
{
    /// <summary>全部成员（含房主；掉线但保留的成员也在里面，<see cref="BoardMemberInfo.IsOnline"/>=false）。</summary>
    public List<BoardMemberInfo> Members { get; set; } = new();

    /// <summary>先手位（五子棋=黑棋、象棋=红方）上的成员 id；空字符串 = 空位。</summary>
    public string SeatOneMemberId { get; set; } = "";

    /// <summary>后手位上的成员 id；空字符串 = 空位。</summary>
    public string SeatTwoMemberId { get; set; } = "";

    /// <summary>自动上场方式：<c>manual</c>（手动安排）/ <c>loserOut</c>（输的下场）/ <c>bothOut</c>（双方都下场）。</summary>
    public string AutoRotateMode { get; set; } = "manual";

    /// <summary>补位方式：<c>rotation</c>（轮流上场）/ <c>random</c>（随机上场）。</summary>
    public string AutoPickMode { get; set; } = "rotation";

    /// <summary>房间人数上限（含房主）。</summary>
    public int MaxMembers { get; set; } = 16;
}

/// <summary>
/// 包装状态：seq 只增；room + game 一起发（棋牌状态很小）。
/// <para>客户端收到后按 seq 忽略乱序旧包，把 <see cref="Room"/> 交给房间模型、<see cref="Game"/> 交给游戏 VM。</para>
/// </summary>
public sealed class BoardStateEnvelope
{
    public int Seq { get; set; }

    public BoardRoomState? Room { get; set; }

    public JsonElement? Game { get; set; }
}

/// <summary>联机请求的类型字符串（别在代码里手写字符串，免得两边拼错）。</summary>
public static class BoardRequestTypes
{
    /// <summary>悔棋：请求方希望撤回到自己该走。</summary>
    public const string Undo = "undo";

    /// <summary>求和。</summary>
    public const string Draw = "draw";

    /// <summary>再来一局。</summary>
    public const string Rematch = "rematch";

    /// <summary>认输。</summary>
    public const string Resign = "resign";
}
