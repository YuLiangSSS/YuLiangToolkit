using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.BoardGames.Doudizhu;

namespace YuLiangToolkit.Tools.BoardGames.Doudizhu;

/// <summary>
/// 「斗地主」配置 → <c>Config\doudizhu\config.json</c>。
/// 房间相关字段与棋牌其它游戏一致（玩家名 / 端口 / 上次地址 / 换人方式）。
/// </summary>
public sealed class DoudizhuConfig : ToolConfig, YuLiangToolkit.Tools.BoardGames.Shared.ViewModels.IBoardGameRoomConfig
{
    /// <summary>工具 Id（Data / Config 目录名）。</summary>
    [JsonIgnore]
    public static string ToolId => "doudizhu";

    private string _playerName = "";

    /// <summary>玩家名（联机时对方看到的名字）。</summary>
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

    /// <summary>开房端口（0 = 系统自动挑）。</summary>
    public int ListenPort
    {
        get => _listenPort;
        set => Set(ref _listenPort, value is >= 0 and <= 65535 ? value : 0);
    }

    private string _autoRotateMode = "manual";

    /// <summary>换人方式：<c>manual</c> / <c>loserOut</c> / <c>bothOut</c>（跟棋牌其它游戏一致）。</summary>
    public string AutoRotateMode
    {
        get => _autoRotateMode;
        set => Set(ref _autoRotateMode, string.IsNullOrWhiteSpace(value) ? "manual" : value.Trim());
    }

    private string _autoPickMode = "rotation";

    /// <summary>补位方式：<c>rotation</c> / <c>random</c>。</summary>
    public string AutoPickMode
    {
        get => _autoPickMode;
        set => Set(ref _autoPickMode, string.IsNullOrWhiteSpace(value) ? "rotation" : value.Trim());
    }

    private bool _aiEnabled = true;

    /// <summary>单机时另外两家是否由电脑接管（默认开）。</summary>
    public bool AiEnabled
    {
        get => _aiEnabled;
        set => Set(ref _aiEnabled, value);
    }

    private string _aiDifficulty = "normal";

    /// <summary>电脑难度：beginner / normal / hard / master。</summary>
    public string AiDifficulty
    {
        get => _aiDifficulty;
        set => Set(ref _aiDifficulty, string.IsNullOrWhiteSpace(value) ? "normal" : value.Trim());
    }

    private string _mode = "classic";

    /// <summary>发牌模式：<c>classic</c>（经典）/ <c>doubleDeck</c>（双副牌 · 炸弹多）/ <c>openBottom</c>（底牌明牌）。</summary>
    public string Mode
    {
        get => _mode;
        set => Set(ref _mode, string.IsNullOrWhiteSpace(value) ? "classic" : value.Trim());
    }

    /// <summary>玩家名的实际显示值。</summary>
    [JsonIgnore]
    public string EffectivePlayerName
        => PlayerName.Length > 0 ? PlayerName
            : Environment.UserName is { Length: > 0 } name ? name
            : "玩家";

    /// <summary>把配置里的模式字符串转成枚举。</summary>
    [JsonIgnore]
    public DoudizhuMode ModeValue => Mode switch
    {
        "doubleDeck" => DoudizhuMode.DoubleDeck,
        "openBottom" => DoudizhuMode.OpenBottom,
        _ => DoudizhuMode.Classic,
    };

    /// <summary>模式字符串 ↔ 枚举（协议 / 配置共用）。</summary>
    public static string ModeToKey(DoudizhuMode mode) => mode switch
    {
        DoudizhuMode.DoubleDeck => "doubleDeck",
        DoudizhuMode.OpenBottom => "openBottom",
        _ => "classic",
    };

    /// <summary>模式的中文名。</summary>
    public static string ModeDisplayName(DoudizhuMode mode) => mode switch
    {
        DoudizhuMode.DoubleDeck => "双副牌（炸弹多）",
        DoudizhuMode.OpenBottom => "底牌明牌",
        _ => "经典",
    };

    /// <summary>模式的一句话说明。</summary>
    public static string ModeDescription(DoudizhuMode mode) => mode switch
    {
        DoudizhuMode.DoubleDeck => "两副牌 108 张，4 个王（2 个王炸）、每种点数 8 张（炸弹更多），三人各 36 张",
        DoudizhuMode.OpenBottom => "经典发牌，但 3 张底牌一开始就摊开，抢地主更刺激",
        _ => "一副牌 54 张，三人各 17 张 + 3 张暗底牌",
    };
}
