using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.BoardGames.Ludo;

/// <summary>「飞行棋」配置 → <c>Config\ludo\config.json</c>。</summary>
public sealed class LudoConfig : ToolConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "ludo";

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

    private LudoRulesState _rules = new();

    /// <summary>规则开关。</summary>
    public LudoRulesState Rules
    {
        get => _rules;
        set => Set(ref _rules, value ?? new LudoRulesState());
    }

    /// <summary>把规则写回配置。</summary>
    public void ApplyRules(LudoRules rules) => Rules = LudoRulesState.From(rules);

    /// <summary>玩家名的实际显示值。</summary>
    [JsonIgnore]
    public string EffectivePlayerName
        => PlayerName.Length > 0 ? PlayerName
            : Environment.UserName is { Length: > 0 } name ? name
            : "玩家";
}
