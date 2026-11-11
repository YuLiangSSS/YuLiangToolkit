using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Games.Tetris;

/// <summary>
/// 「俄罗斯方块」的强类型配置 → <c>Config\tetris\config.json</c>（改一下最多 400ms 自动落盘，没有保存按钮）。
///
/// <para>只存**该记住的东西**：最高分、几个显示开关、起始等级。棋盘本身不落盘 ——
/// 关掉就开新局（小游戏没必要做存档，做了反而要处理「读到半局」的兼容问题）。</para>
/// </summary>
public sealed class TetrisConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名，自检会核对它与 ITool.Id 一致）。</summary>
    [JsonIgnore]
    public static string ToolId => "tetris";

    /// <summary>起始等级下限。</summary>
    public const int MinStartLevel = 1;

    /// <summary>起始等级上限（和引擎的 <see cref="TetrisEngine.MaxLevel"/> 对齐）。</summary>
    public const int MaxStartLevel = TetrisEngine.MaxLevel;

    private int _highScore;

    /// <summary>历史最高分（只在刷新时写，见 ViewModel 的 <c>SubmitScore</c>）。</summary>
    public int HighScore
    {
        get => _highScore;
        set => Set(ref _highScore, Math.Max(0, value));
    }

    private int _startLevel = 1;

    /// <summary>起始等级（1 ~ 20）：觉得节奏太慢就直接从高等级开局。</summary>
    public int StartLevel
    {
        get => _startLevel;
        set => Set(ref _startLevel, Math.Clamp(value, MinStartLevel, MaxStartLevel));
    }

    private bool _showGhost = true;

    /// <summary>显示幽灵方块（落在最底时的位置预览）。</summary>
    public bool ShowGhost
    {
        get => _showGhost;
        set => Set(ref _showGhost, value);
    }

    private bool _showGrid = true;

    /// <summary>显示棋盘网格线。</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set => Set(ref _showGrid, value);
    }

    private bool _soloAiEnabled;

    /// <summary>单人模式是否开电脑对手（人机对战）。</summary>
    public bool SoloAiEnabled
    {
        get => _soloAiEnabled;
        set => Set(ref _soloAiEnabled, value);
    }

    private int _previewSlots = 3;

    /// <summary>预览块数（1~8）。</summary>
    public int PreviewSlots
    {
        get => _previewSlots;
        set => Set(ref _previewSlots, Math.Clamp(value, 1, 8));
    }

    private int _holdSlots = 1;

    /// <summary>暂存格数量（1~5；多个时按 C 轮流换出）。</summary>
    public int HoldSlots
    {
        get => _holdSlots;
        set => Set(ref _holdSlots, Math.Clamp(value, 1, 5));
    }

    private string _hudLayout = "";

    /// <summary>HUD 自由布局（各组件的位置，序列化字符串；空 = 默认布局）。</summary>
    public string HudLayout
    {
        get => _hudLayout;
        set => Set(ref _hudLayout, value ?? "");
    }

    private string _autoRotateMode = "manual";

    /// <summary>一局结束后怎么换人：manual / loserOut / bothOut（跟棋牌同一套）。</summary>
    public string AutoRotateMode
    {
        get => _autoRotateMode;
        set => Set(ref _autoRotateMode, string.IsNullOrWhiteSpace(value) ? "manual" : value.Trim());
    }

    private string _autoPickMode = "rotation";

    /// <summary>补位方式：rotation / random。</summary>
    public string AutoPickMode
    {
        get => _autoPickMode;
        set => Set(ref _autoPickMode, string.IsNullOrWhiteSpace(value) ? "rotation" : value.Trim());
    }

    private bool _showNext = true;

    /// <summary>显示「接下来 3 块」预览。</summary>
    public bool ShowNext
    {
        get => _showNext;
        set => Set(ref _showNext, value);
    }

    private bool _pauseOnLostFocus = true;

    /// <summary>切走程序窗口时自动暂停（默认开，免得回来时方已经堆到底了）。</summary>
    public bool PauseOnLostFocus
    {
        get => _pauseOnLostFocus;
        set => Set(ref _pauseOnLostFocus, value);
    }

    /// <summary>把一次对局的分数并进最高分；返回是否刷新了纪录。</summary>
    public bool SubmitScore(int score)
    {
        if (score <= HighScore) return false;
        HighScore = score;
        return true;
    }

    // ---------------------------------------------------------------- 联机（1 对 1）

    private string _playerName = "";

    /// <summary>玩家名（联机时对方看到的名字）；空 = 用系统用户名。</summary>
    public string PlayerName
    {
        get => _playerName;
        set => Set(ref _playerName, (value ?? "").Trim());
    }

    private string _lastJoinAddress = "";

    /// <summary>上次加入过的主机地址（下次打开自动带出来）。</summary>
    public string LastJoinAddress
    {
        get => _lastJoinAddress;
        set => Set(ref _lastJoinAddress, (value ?? "").Trim());
    }

    private int _listenPort;

    /// <summary>开房时监听的端口（0 = 让系统挑）。</summary>
    public int ListenPort
    {
        get => _listenPort;
        set => Set(ref _listenPort, value is >= 0 and <= 65535 ? value : 0);
    }

    private bool _aiEnabled;

    /// <summary>联机模式里用电脑对手代替真人（房主开着就等于是「我 vs 电脑」）。</summary>
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

    private string _lastNetMode = "garbage";

    /// <summary>上次联机选的模式（<c>garbage</c> / <c>score</c>）。</summary>
    public string LastNetMode
    {
        get => _lastNetMode;
        set => Set(ref _lastNetMode, string.Equals(value, "score", StringComparison.OrdinalIgnoreCase) ? "score" : "garbage");
    }

    /// <summary>玩家名的实际显示值（没填就用系统用户名 / 「玩家」）。</summary>
    [JsonIgnore]
    public string EffectivePlayerName
        => PlayerName.Length > 0 ? PlayerName
            : Environment.UserName is { Length: > 0 } name ? name
            : "玩家";
}
