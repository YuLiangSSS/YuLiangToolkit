using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Games.Snake;

/// <summary>「贪吃蛇」配置 → <c>Config\snake\config.json</c>。</summary>
public sealed class SnakeConfig : ToolConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "snake";

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

    /// <summary>开房端口（0 = 自动挑）。</summary>
    public int ListenPort
    {
        get => _listenPort;
        set => Set(ref _listenPort, value is >= 0 and <= 65535 ? value : 0);
    }

    // ---------------------------------------------------------------- 玩法设置（房主可改）

    /// <summary>玩法：<c>shared</c>（共享果）/ <c>battle</c>（大混战）。</summary>
    public string Mode { get; set; } = "battle";

    /// <summary>棋盘宽度。</summary>
    public int Width { get; set; } = 40;

    /// <summary>棋盘高度。</summary>
    public int Height { get; set; } = 30;

    /// <summary>共享果区域数。</summary>
    public int RegionCount { get; set; } = 2;

    /// <summary>人数上限。</summary>
    public int MaxPlayers { get; set; } = 4;

    /// <summary>果子上限。</summary>
    public int MaxFruits { get; set; } = 5;

    /// <summary>命数。</summary>
    public int Lives { get; set; } = 3;

    /// <summary>开局倒计时（秒）。</summary>
    public int CountdownSeconds { get; set; } = 3;

    /// <summary>限时（秒；0 = 不限）。</summary>
    public int TimeLimitSeconds { get; set; } = 120;

    /// <summary>边缘规则：<c>die</c> / <c>stop</c> / <c>wrap</c>。</summary>
    public string EdgeMode { get; set; } = "die";

    /// <summary>共享果玩法：没吃到时是否断 combo。</summary>
    public bool ComboBreaksOnMiss { get; set; } = true;

    /// <summary>重生是否保留原长度。</summary>
    public bool RespawnKeepsLength { get; set; } = true;

    /// <summary>出生点 3×3 爆炸。</summary>
    public bool SpawnBombEnabled { get; set; } = true;

    /// <summary>是否开启加速。</summary>
    public bool SpeedBoostEnabled { get; set; } = true;

    /// <summary>加速倍率。</summary>
    public double SpeedBoostMultiplier { get; set; } = 2.0;

    /// <summary>AI 数量。</summary>
    public int AiCount { get; set; } = 1;

    /// <summary>AI 等级（1~10）。</summary>
    public int AiLevel { get; set; } = 3;

    /// <summary>每个 AI 的算法（英文逗号分隔，如 <c>3,7,10</c>）。</summary>
    public string AiAlgorithms { get; set; } = "";

    /// <summary>取第 index 个 AI 的算法（没配就用统一等级）。</summary>
    public int AiAlgorithmOf(int index)
    {
        var parts = (AiAlgorithms ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        return index >= 0 && index < parts.Length && int.TryParse(parts[index].Trim(), out var level)
            ? Math.Clamp(level, 1, 10)
            : Math.Clamp(AiLevel, 1, 10);
    }

    /// <summary>写回每个 AI 的算法。</summary>
    public void SetAiAlgorithms(IEnumerable<int> levels)
        => AiAlgorithms = string.Join(",", levels.Select(l => Math.Clamp(l, 1, 10)));

    /// <summary>爬行速度（格/秒）。</summary>
    public double Speed { get; set; } = 8;

    /// <summary>玩家名的实际显示值。</summary>
    [JsonIgnore]
    public string EffectivePlayerName
        => PlayerName.Length > 0 ? PlayerName
            : Environment.UserName is { Length: > 0 } name ? name
            : "玩家";

    /// <summary>从配置读出对局设置。</summary>
    [JsonIgnore]
    public SnakeSettings Settings => new()
    {
        Width = Width,
        Height = Height,
        Mode = string.Equals(Mode, "shared", StringComparison.OrdinalIgnoreCase) ? SnakeMode.SharedFruit : SnakeMode.BattleRoyale,
        RegionCount = RegionCount,
        MaxPlayers = MaxPlayers,
        MaxFruits = MaxFruits,
        Lives = Lives,
        CountdownSeconds = CountdownSeconds,
        // 秒 → 步数（按基础速度换算）
        TimeLimitSteps = (int)Math.Round(TimeLimitSeconds * Math.Max(1, Speed)),
        EdgeMode = EdgeMode switch
        {
            "stop" => SnakeEdgeMode.Stop,
            "wrap" => SnakeEdgeMode.Wrap,
            _ => SnakeEdgeMode.Die,
        },
        ComboBreaksOnMiss = ComboBreaksOnMiss,
        RespawnKeepsLength = RespawnKeepsLength,
        SpawnBombEnabled = SpawnBombEnabled,
        SpeedBoostEnabled = SpeedBoostEnabled,
        SpeedBoostMultiplier = SpeedBoostMultiplier,
        AiCount = AiCount,
        AiLevel = AiLevel,
        Speed = Speed,
    };

    /// <summary>把界面上的设置写回配置。</summary>
    public void Apply(SnakeSettings settings)
    {
        Width = settings.Width;
        Height = settings.Height;
        Mode = settings.Mode == SnakeMode.SharedFruit ? "shared" : "battle";
        RegionCount = settings.RegionCount;
        MaxPlayers = settings.MaxPlayers;
        MaxFruits = settings.MaxFruits;
        Lives = settings.Lives;
        CountdownSeconds = settings.CountdownSeconds;
        TimeLimitSeconds = (int)Math.Round(settings.TimeLimitSteps / Math.Max(1, settings.Speed));
        EdgeMode = settings.EdgeMode switch
        {
            SnakeEdgeMode.Stop => "stop",
            SnakeEdgeMode.Wrap => "wrap",
            _ => "die",
        };
        ComboBreaksOnMiss = settings.ComboBreaksOnMiss;
        RespawnKeepsLength = settings.RespawnKeepsLength;
        SpawnBombEnabled = settings.SpawnBombEnabled;
        SpeedBoostEnabled = settings.SpeedBoostEnabled;
        SpeedBoostMultiplier = settings.SpeedBoostMultiplier;
        AiCount = settings.AiCount;
        AiLevel = settings.AiLevel;
        Speed = settings.Speed;
    }
}
