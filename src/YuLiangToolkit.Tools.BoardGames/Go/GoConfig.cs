using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Go;

/// <summary>「围棋」配置 → <c>Config\go\config.json</c>。</summary>
public sealed class GoConfig : ToolConfig, IBoardGameRoomConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "go";

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

    private int _boardSize = 19;

    /// <summary>棋盘路数（9 / 13 / 19）。</summary>
    public int BoardSize
    {
        get => _boardSize;
        set => Set(ref _boardSize, GoGame.AllowedSizes.Contains(value) ? value : 19);
    }

    private double _komi = 7.5;

    /// <summary>贴目（白方额外加）。</summary>
    public double Komi
    {
        get => _komi;
        set => Set(ref _komi, value is >= 0 and <= 50 ? value : 7.5);
    }

    private string _hostSide = "black";

    /// <summary>房主默认执哪一方。</summary>
    public string HostSide
    {
        get => _hostSide;
        set => Set(ref _hostSide, string.IsNullOrWhiteSpace(value) ? "black" : value.Trim());
    }

    private bool _aiEnabled = true;

    /// <summary>同机模式下开电脑对手。</summary>
    public bool AiEnabled
    {
        get => _aiEnabled;
        set => Set(ref _aiEnabled, value);
    }

    private string _aiDifficulty = "normal";

    /// <summary>电脑难度。</summary>
    public string AiDifficulty
    {
        get => _aiDifficulty;
        set => Set(ref _aiDifficulty, string.IsNullOrWhiteSpace(value) ? "normal" : value.Trim());
    }

    /// <summary>玩家名的实际显示值。</summary>
    [JsonIgnore]
    public string EffectivePlayerName
        => PlayerName.Length > 0 ? PlayerName
            : Environment.UserName is { Length: > 0 } name ? name
            : "玩家";
}
