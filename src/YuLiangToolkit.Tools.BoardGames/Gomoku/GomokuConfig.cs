using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Gomoku;

/// <summary>
/// 「联机五子棋」的选项 → <c>Config\gomoku\config.json</c>。
/// 属性一改就自动落盘（基类后台比对保存），界面直接双向绑定，没有「保存」按钮。
///
/// <para><b>开工前能改的</b>：棋盘路数、禁手、房主执子 —— 这些决定「这一局怎么下」，
/// 已经开始的对局里改会提示「下一局生效」（由 ViewModel 负责）。</para>
/// </summary>
public sealed class GomokuConfig : ToolConfig, IBoardGameRoomConfig
{
    /// <summary>功能 Id（同时是 <c>Data\gomoku</c> / <c>Config\gomoku</c> 的文件夹名）——定了就别改。</summary>
    public const string ToolId = "gomoku";

    // ---------------------------------------------------------------- 房间（IBoardGameRoomConfig）

    /// <summary>玩家名（对方在房间列表 / 聊天里看到的名字；留空时用「玩家」）。</summary>
    public string PlayerName { get; set; } = "玩家";

    /// <summary>上次成功加入的房间地址（客户端「加入地址」输入框的默认值）。</summary>
    public string LastJoinAddress { get; set; } = "";

    /// <summary>开房间用的监听端口（<b>0 = 让系统自动挑一个空闲端口</b>）。</summary>
    public int ListenPort { get; set; } = 5310;

    // ---------------------------------------------------------------- 对局规则

    /// <summary>房主执哪边：<c>"black"</c>（黑棋，先手）/ <c>"white"</c>（白棋，后手）。</summary>
    public string HostSide { get; set; } = "black";

    /// <summary>
    /// 禁手开关（默认关）：开启后黑棋落在三三 / 四四 / 长连（≥6）上立即判负；
    /// 关闭则长连也算赢。黑棋正好五连永远算赢。
    /// </summary>
    public bool ForbiddenEnabled { get; set; }

    /// <summary>棋盘上要不要用红色小叉标出「黑棋禁手点」（黑方回合时才计算）。</summary>
    public bool ShowForbiddenPoints { get; set; } = true;

    /// <summary>棋盘路数：15（默认）或 19。</summary>
    public int BoardSize { get; set; } = GomokuBoard.DefaultSize;

    /// <summary>右侧「棋谱」卡片要不要显示（关掉只影响显示，棋谱本身照常记）。</summary>
    public bool ShowMoveList { get; set; } = true;

    // ---------------------------------------------------------------- 计算属性

    /// <summary>规整后的棋盘路数：只认 15 / 19，其它值一律按 15。</summary>
    [JsonIgnore]
    public int EffectiveBoardSize => BoardSize == 19 ? 19 : GomokuBoard.DefaultSize;

    /// <summary>规整后的房主执子：只认 black / white，其它值一律按 black（先手）。</summary>
    [JsonIgnore]
    public string EffectiveHostSide =>
        string.Equals(HostSide, "white", StringComparison.OrdinalIgnoreCase) ? "white" : "black";

    /// <summary>房主是不是执白（用于界面显示 / 文案）。</summary>
    [JsonIgnore]
    public bool HostPlaysWhite => EffectiveHostSide == "white";

    /// <summary>昵称（空的话给个默认名，免得对方看到一个空白名字）。</summary>
    [JsonIgnore]
    public string EffectivePlayerName =>
        string.IsNullOrWhiteSpace(PlayerName) ? "玩家" : PlayerName.Trim();

    /// <summary>当前规则的一行说明（设置卡片上显示，帮助用户理解「禁手开 / 关」的区别）。</summary>
    [JsonIgnore]
    public string ForbiddenSummary => ForbiddenEnabled
        ? "禁手：开（黑棋三三 / 四四 / 长连判负，黑棋正好五连算赢）"
        : "禁手：关（双方连成五子或更长的长连都算赢）";
}
