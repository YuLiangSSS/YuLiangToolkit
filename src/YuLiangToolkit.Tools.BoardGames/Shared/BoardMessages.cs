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

    /// <summary>玩家名（主机显示「对手：谁谁谁」）。</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>协议版本（对不上就拒连并提示）。</summary>
    public int ProtocolVersion { get; set; }
}

/// <summary>主机回给客户端的握手回执。</summary>
public sealed class WelcomeInfo
{
    public string Magic { get; set; } = "";

    public string GameId { get; set; } = "";

    /// <summary>主机（房主）的玩家名。</summary>
    public string HostName { get; set; } = "";

    /// <summary>客户端执哪边：<c>black</c>/<c>white</c> 或 <c>red</c>/<c>black</c>。</summary>
    public string YourSide { get; set; } = "";

    public int ProtocolVersion { get; set; }

    /// <summary>包装后的局面（见 <see cref="BoardSnapshotEnvelope"/>；可能为空 = 还没有局面）。</summary>
    public JsonElement? Snapshot { get; set; }
}

/// <summary>主机广播的最新局面。</summary>
public sealed class SnapshotInfo
{
    /// <summary>包装后的局面（<c>{seq, hostSide, clientSide, game}</c>）。</summary>
    public JsonElement? Snapshot { get; set; }

    /// <summary>附带的提示（例如「对局开始」「对方悔棋了」，客户端显示在状态栏 / 聊天记录）。</summary>
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

    /// <summary>可选附加数据（例如悔棋想撤几步）；目前协议不用，留好扩展位。</summary>
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
/// 主机把游戏局面包一层再发（见 WIP 文档 3.3；<see cref="BoardProtocol.WrapSnapshot"/> 负责序列化）。
/// <para><see cref="Seq"/> 只增不减，客户端可据此忽略乱序的旧包；<see cref="HostSide"/>/<see cref="ClientSide"/>
/// 每次都可能变（「再来一局」换边时）。</para>
/// </summary>
public sealed class BoardSnapshotEnvelope
{
    public int Seq { get; set; }

    /// <summary>主机执哪边。</summary>
    public string HostSide { get; set; } = "";

    /// <summary>客户端执哪边（本端收到后据此知道「我执哪边」）。</summary>
    public string ClientSide { get; set; } = "";

    /// <summary>游戏自己的局面（各游戏 DOM 自己定义，房间不解释）。</summary>
    public JsonElement? Game { get; set; }
}

/// <summary>
/// 主机开房 / 客户端握手成功时，游戏交给房间的「欢迎信息」：
/// 客户端执哪边 + 当前局面（<b>已包装</b>，见 <see cref="BoardSnapshotEnvelope"/>）。
/// </summary>
public sealed record BoardWelcome(string ClientSide, JsonElement Snapshot);

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
