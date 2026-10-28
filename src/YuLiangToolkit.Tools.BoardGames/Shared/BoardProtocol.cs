using System.Text.Json;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.BoardGames.Shared;

/// <summary>
/// 「棋牌房间」的线上协议常量（纯逻辑，自检可直接断言）。
///
/// <para><b>协议 v2</b>：一个房间可以有很多人（房主 + 最多 15 个客户端，共 16 人），
/// 只有坐在两个「上场位」上的人在比赛，其余人观战；房主广播<b>完整房间状态</b>
/// （成员表 + 两个席位 + 自动轮换设置 + 棋局快照），客户端只认状态包。</para>
///
/// <para>传输格式本身由 <see cref="RoomProtocol"/> 负责（4 字节长度前缀 + JSON 头 + 裸载荷），
/// 这里只约定包类型、状态包装与握手字段。本协议<b>不含任何棋局规则</b>。</para>
/// </summary>
public static class BoardProtocol
{
    /// <summary>握手用的协议标识：对不上就直接拒连（防止别的程序连进来把连接搞乱）。</summary>
    public const string Magic = "YLTBOARD1";

    /// <summary>协议版本（v2 = 多人观战 + 房主安排上场 + 自动轮换；两面必须同版本）。</summary>
    public const int Version = 2;

    /// <summary>五子棋的 <c>GameId</c>（与工具 Id 一致）。</summary>
    public const string GameGomoku = "gomoku";

    /// <summary>中国象棋的 <c>GameId</c>（与工具 Id 一致）。</summary>
    public const string GameXiangqi = "xiangqi";

    /// <summary>包类型（线上是字符串，加减类型不会让老版本错位）。</summary>
    public static class Kinds
    {
        /// <summary>客户端 → 主机：报家门（magic / 版本 / 游戏 / 玩家名 / 上次成员 id）。</summary>
        public const string Hello = "hello";

        /// <summary>主机 → 客户端：握手回执（你的成员 id + 完整状态包装）。</summary>
        public const string Welcome = "welcome";

        /// <summary>主机 → 所有客户端：最新完整状态（房间 + 棋局）。</summary>
        public const string State = "state";

        /// <summary>客户端 → 主机：走棋等游戏动作。</summary>
        public const string Action = "action";

        /// <summary>双向：聊天文字（客户端发的由主机转发给其他客户端）。</summary>
        public const string Chat = "chat";

        /// <summary>双向：系统提示（info / warn / error）。</summary>
        public const string Notice = "notice";

        /// <summary>双向：请求（undo / draw / rematch / resign）。</summary>
        public const string Request = "request";

        /// <summary>双向：对请求的应答（accept / reject）。</summary>
        public const string Response = "response";

        /// <summary>双向：礼貌离开（带上原因，对方能立刻知道不是掉线）。</summary>
        public const string Bye = "bye";
    }

    /// <summary>
    /// 主机把「房间状态 + 棋局快照」包一层再发：
    /// <c>{ "seq": 12, "room": { members…, seatOneMemberId… }, "game": { …游戏自己的快照… } }</c>。
    /// <para><see cref="BoardStateEnvelope.Seq"/> 只增不减，客户端可据此忽略乱序旧包。</para>
    /// </summary>
    public static JsonElement WrapState(int seq, BoardRoomState room, JsonElement game)
        => JsonSerializer.SerializeToElement(new BoardStateEnvelope
        {
            Seq = seq,
            Room = room,
            Game = game.ValueKind is JsonValueKind.Undefined ? null : game,
        }, RoomProtocol.JsonOptions);

    /// <summary>把收到的包装状态解回来；格式不对返回 <c>null</c>（容错第一，别让一个坏包把界面搞崩）。</summary>
    public static BoardStateEnvelope? ReadState(JsonElement wrapped)
    {
        if (wrapped.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        try
        {
            return wrapped.Deserialize<BoardStateEnvelope>(RoomProtocol.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前协议的一句话摘要（排错 / 界面提示用）。</summary>
    public static string Describe()
        => $"YuLiangToolkit 棋牌房间协议 v{Version}（magic={Magic}）"
           + "；帧格式：4 字节长度 + JSON 头 + 裸载荷；"
           + $"包类型：{string.Join(" / ", new[]
           {
               Kinds.Hello, Kinds.Welcome, Kinds.State, Kinds.Action, Kinds.Chat,
               Kinds.Notice, Kinds.Request, Kinds.Response, Kinds.Bye,
           })}"
           + "；状态 = 房间成员/席位/自动轮换 + 棋局快照";
}
