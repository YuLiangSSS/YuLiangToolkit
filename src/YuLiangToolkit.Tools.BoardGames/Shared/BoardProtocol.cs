using System.Text.Json;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.BoardGames.Shared;

/// <summary>
/// 「棋牌房间」的线上协议常量（纯逻辑，自检可直接断言）。
///
/// <para>传输格式本身由 <see cref="RoomProtocol"/> 负责（4 字节长度前缀 + JSON 头 + 裸载荷），
/// 这里只约定<b>包类型</b>与<b>握手字段</b>：客户端连上先发 <c>hello</c>，主机校验后回 <c>welcome</c>，
/// 之后主机广播 <c>snapshot</c>、客户端上报 <c>action</c>，聊天 / 提示 / 请求应答双向走。</para>
///
/// <para>本协议<b>不含任何棋局规则</b>：局面长什么样由各游戏自己的 Snapshot 决定，
/// 房间只把它包一层 <see cref="BoardSnapshotEnvelope"/> 透传。</para>
/// </summary>
public static class BoardProtocol
{
    /// <summary>握手用的协议标识：对不上就直接拒连（防止别的程序连进来把连接搞乱）。</summary>
    public const string Magic = "YLTBOARD1";

    /// <summary>协议版本。加减字段不算破坏兼容；改帧格式才动它。</summary>
    public const int Version = 1;

    /// <summary>五子棋的 <c>GameId</c>（与工具 Id 一致）。</summary>
    public const string GameGomoku = "gomoku";

    /// <summary>中国象棋的 <c>GameId</c>（与工具 Id 一致）。</summary>
    public const string GameXiangqi = "xiangqi";

    /// <summary>包类型（线上是字符串，加减类型不会让老版本错位）。</summary>
    public static class Kinds
    {
        /// <summary>客户端 → 主机：报家门（magic / 版本 / 游戏 / 玩家名）。</summary>
        public const string Hello = "hello";

        /// <summary>主机 → 客户端：握手回执（你执哪边 + 当前局面）。</summary>
        public const string Welcome = "welcome";

        /// <summary>主机 → 客户端：最新局面（客户端只认快照，不自己改棋局）。</summary>
        public const string Snapshot = "snapshot";

        /// <summary>客户端 → 主机：走棋等游戏动作。</summary>
        public const string Action = "action";

        /// <summary>双向：聊天文字。</summary>
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
    /// 主机把游戏局面包一层再发（会话不管内容，只透传）：
    /// <c>{ "seq": 12, "hostSide": "black", "clientSide": "white", "game": { …游戏自己的快照… } }</c>。
    /// </summary>
    public static JsonElement WrapSnapshot(int seq, string hostSide, string clientSide, JsonElement game)
        => JsonSerializer.SerializeToElement(new BoardSnapshotEnvelope
        {
            Seq = seq,
            HostSide = hostSide,
            ClientSide = clientSide,
            Game = game,
        }, RoomProtocol.JsonOptions);

    /// <summary>把收到的包装局面解回来；格式不对返回 <c>null</c>（容错第一，别让一个坏包把界面搞崩）。</summary>
    public static BoardSnapshotEnvelope? ReadSnapshot(JsonElement wrapped)
    {
        if (wrapped.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        try
        {
            return wrapped.Deserialize<BoardSnapshotEnvelope>(RoomProtocol.JsonOptions);
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
               Kinds.Hello, Kinds.Welcome, Kinds.Snapshot, Kinds.Action, Kinds.Chat,
               Kinds.Notice, Kinds.Request, Kinds.Response, Kinds.Bye,
           })}";
}
