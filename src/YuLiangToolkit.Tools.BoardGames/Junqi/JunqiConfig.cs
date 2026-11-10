using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Junqi;

/// <summary>军棋玩法：二人军棋 / 四国军棋。</summary>
public enum JunqiVariant
{
    /// <summary>二人军棋（陆战棋）。</summary>
    Duel = 0,

    /// <summary>四国军棋（4 人两队）。</summary>
    FourKingdoms = 1,
}

/// <summary>「军棋」配置 → <c>Config\junqi\config.json</c>。</summary>
public sealed class JunqiConfig : ToolConfig, IBoardGameRoomConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "junqi";

    private string _playerName = "";

    /// <summary>玩家名。</summary>
    public string PlayerName
    {
        get => _playerName;
        set => Set(ref _playerName, (value ?? "").Trim());
    }

    private string _lastJoinAddress = "";

    /// <summary>上次加入过的主机地址。</summary>
    public string LastJoinAddress
    {
        get => _lastJoinAddress;
        set => Set(ref _lastJoinAddress, (value ?? "").Trim());
    }

    private int _listenPort;

    /// <summary>开房端口（0 = 自动挑）。</summary>
    public int ListenPort
    {
        get => _listenPort;
        set => Set(ref _listenPort, value is >= 0 and <= 65535 ? value : 0);
    }

    private string _autoRotateMode = "manual";

    /// <summary>换人方式（跟棋牌其它游戏一致）。</summary>
    public string AutoRotateMode
    {
        get => _autoRotateMode;
        set => Set(ref _autoRotateMode, string.IsNullOrWhiteSpace(value) ? "manual" : value.Trim());
    }

    private string _autoPickMode = "rotation";

    /// <summary>补位方式。</summary>
    public string AutoPickMode
    {
        get => _autoPickMode;
        set => Set(ref _autoPickMode, string.IsNullOrWhiteSpace(value) ? "rotation" : value.Trim());
    }

    private string _variant = "duel";

    /// <summary>玩法：<c>duel</c>（二人军棋）/ <c>fourKingdoms</c>（四国军棋）。</summary>
    public string Variant
    {
        get => _variant;
        set => Set(ref _variant, string.IsNullOrWhiteSpace(value) ? "duel" : value.Trim());
    }

    /// <summary>玩法枚举。</summary>
    [JsonIgnore]
    public JunqiVariant VariantValue
        => string.Equals(Variant, "fourKingdoms", StringComparison.OrdinalIgnoreCase)
            ? JunqiVariant.FourKingdoms
            : JunqiVariant.Duel;

    /// <summary>玩家名的实际显示值。</summary>
    [JsonIgnore]
    public string EffectivePlayerName
        => PlayerName.Length > 0 ? PlayerName
            : Environment.UserName is { Length: > 0 } name ? name
            : "玩家";

    /// <summary>玩法 → 存储键。</summary>
    public static string VariantToKey(JunqiVariant variant)
        => variant == JunqiVariant.FourKingdoms ? "fourKingdoms" : "duel";

    /// <summary>玩法中文名。</summary>
    public static string VariantDisplayName(JunqiVariant variant)
        => variant == JunqiVariant.FourKingdoms ? "四国军棋" : "二人军棋";

    /// <summary>玩法说明。</summary>
    public static string VariantDescription(JunqiVariant variant) => variant == JunqiVariant.FourKingdoms
        ? "4 人两队（对家同盟）共用一个十字棋盘，暗棋自动裁判，先夺对方两面军旗的一队获胜"
        : "两人各 25 枚棋子，暗棋自动裁判，夺旗 / 对方无子可走即胜";
}
