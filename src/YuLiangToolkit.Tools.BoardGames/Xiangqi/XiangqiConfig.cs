using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>
/// 「联机象棋」设置（存 <c>Config\xiangqi\config.json</c>）。
/// 房间三件套（玩家名 / 上次加入地址 / 监听端口）由 <see cref="IBoardGameRoomConfig"/> 约定，
/// 交给 Shared 层的 <c>BoardGameRoomViewModelBase</c> 接管。
/// </summary>
public sealed class XiangqiConfig : ToolConfig, IBoardGameRoomConfig
{
    // ---------- 房间（IBoardGameRoomConfig 的三项） ----------

    /// <summary>玩家名（开房 / 加入时自报家门）。</summary>
    public string PlayerName { get; set; } = "玩家";

    /// <summary>上次加入的房间地址（下次打开自动填好）。</summary>
    public string LastJoinAddress { get; set; } = "";

    /// <summary>主机监听端口；0 = 让系统自动挑一个空闲端口。</summary>
    public int ListenPort { get; set; } = 5311;

    /// <summary>房主上次选的自动上场方式（<c>manual</c> / <c>loserOut</c> / <c>bothOut</c>）。</summary>
    public string AutoRotateMode { get; set; } = "manual";

    /// <summary>房主上次选的补位方式（<c>rotation</c> / <c>random</c>）。</summary>
    public string AutoPickMode { get; set; } = "rotation";

    // ---------- 对局 ----------

    /// <summary>房主开房时默认坐哪边（"red" = 先手位 / "black" = 后手位）；只影响下次开房。</summary>
    public string HostSide { get; set; } = "red";

    /// <summary>选中棋子时高亮可走点。</summary>
    public bool ShowLegalMoves { get; set; } = true;

    /// <summary>执黑时自动翻转棋盘（黑方在下）。</summary>
    public bool AutoFlip { get; set; } = true;

    /// <summary>显示最后一步的起点 / 终点标记。</summary>
    public bool ShowLastMove { get; set; } = true;

    /// <summary>显示棋盘边缘的路数 / 行号坐标。</summary>
    public bool ShowCoordinates { get; set; } = true;

    // ---------- 摆棋 / 让先偏好（记住上次的选择，方便再来一局） ----------

    /// <summary>上次选择的让子方："red" / "black"。</summary>
    public string HandicapGiver { get; set; } = "red";

    /// <summary>让先步数（0~5）：被让方（让子方的对面）开局可以连走几步。</summary>
    public int OpeningFreeMoves { get; set; }

    // ---------- 界面辅助 ----------

    /// <summary>HostSide 对应的阵营（认不出按红方）。</summary>
    [JsonIgnore]
    public XiangqiSide HostSideValue => XiangqiSides.Parse(HostSide) ?? XiangqiSide.Red;

    /// <summary>HandicapGiver 对应的阵营（认不出按红方）。</summary>
    [JsonIgnore]
    public XiangqiSide HandicapGiverValue => XiangqiSides.Parse(HandicapGiver) ?? XiangqiSide.Red;

    /// <summary>让先步数夹到 0~5（界面 / 逻辑统一用这个值）。</summary>
    [JsonIgnore]
    public int OpeningFreeMovesEffective => Math.Clamp(OpeningFreeMoves, 0, 5);

    /// <summary>房主默认坐席的中文说明（先手位红方 / 后手位黑方）。</summary>
    [JsonIgnore]
    public string HostSideText => HostSideValue == XiangqiSide.Red ? "先手位（红方）" : "后手位（黑方）";
}
