using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.Games.Tetris.ViewModels;

/// <summary>
/// 俄罗斯方块的<b>换人设置</b>（partial 扩展）：对局结束后怎么换人，跟象棋 / 五子棋那套完全一致 ——
/// <b>手动安排</b> / <b>输的下场</b> / <b>全场换人</b>，外加补位方式（轮流 / 随机）。
///
/// <para>俄罗斯方块联机是 1 对 1：一局结束后赢家留下、输家下场（或两人都下），
/// 从观战 / 排队的玩家里补上来。房主在房间卡里设置。</para>
/// </summary>
public sealed partial class TetrisViewModel
{
    /// <summary>换人方式选项（跟棋牌共用同一套枚举，避免两套说法）。</summary>
    public IReadOnlyList<OptionItem<string>> AutoRotateOptions { get; } = OptionItem.From(
        ("manual", "手动安排（房主指定）"),
        ("loserOut", "输的下场，换观战席的下一位"),
        ("bothOut", "全场换人（不论输赢）"));

    /// <summary>补位方式选项。</summary>
    public IReadOnlyList<OptionItem<string>> AutoPickOptions { get; } = OptionItem.From(
        ("rotation", "轮流上场（没上过场的优先）"),
        ("random", "随机上场"));

    /// <summary>当前换人方式键（存在配置里）。</summary>
    public string AutoRotateModeKey
    {
        get => Cfg.AutoRotateMode;
        set
        {
            var normalized = MatchModes.ToKey(MatchModes.ParseRotate(value));
            if (string.Equals(Cfg.AutoRotateMode, normalized, StringComparison.Ordinal)) return;
            Cfg.AutoRotateMode = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedAutoRotate));
            OnPropertyChanged(nameof(AutoRotateHintText));
            BroadcastSwapSettings();
        }
    }

    /// <summary>当前补位方式键。</summary>
    public string AutoPickModeKey
    {
        get => Cfg.AutoPickMode;
        set
        {
            var normalized = MatchModes.ToKey(MatchModes.ParsePick(value));
            if (string.Equals(Cfg.AutoPickMode, normalized, StringComparison.Ordinal)) return;
            Cfg.AutoPickMode = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedAutoPick));
            BroadcastSwapSettings();
        }
    }

    /// <summary>换人方式下拉选中项。</summary>
    public OptionItem<string> SelectedAutoRotate
    {
        get => OptionItem<string>.Find(AutoRotateOptions, AutoRotateModeKey) ?? AutoRotateOptions[0];
        set { if (value is not null) AutoRotateModeKey = value.Value; }
    }

    /// <summary>补位方式下拉选中项。</summary>
    public OptionItem<string> SelectedAutoPick
    {
        get => OptionItem<string>.Find(AutoPickOptions, AutoPickModeKey) ?? AutoPickOptions[0];
        set { if (value is not null) AutoPickModeKey = value.Value; }
    }

    /// <summary>换人说明。</summary>
    public string AutoRotateHintText => MatchModes.ParseRotate(AutoRotateModeKey) switch
    {
        MatchRotateMode.LoserOut => "一局结束后：赢的留下，输的下场，从观战席补一位",
        MatchRotateMode.BothOut => "一局结束后：两个人都下场，从观战席补两位",
        _ => "一局结束后：由房主自己指定谁上场",
    };

    /// <summary>只有房主能改换人设置。</summary>
    public bool CanEditSwapSettings => _session?.IsHost == true;

    /// <summary>房主改完设置后同步给客户端（复用系统提示通道，客户端会显示在状态栏）。</summary>
    private void BroadcastSwapSettings()
    {
        if (_session?.IsHost != true) return;
        var text = $"换人方式：{MatchModes.Describe(MatchModes.ParseRotate(AutoRotateModeKey))}"
                   + $"（{MatchModes.Describe(MatchModes.ParsePick(AutoPickModeKey))}）";
        _ = _session.SendNoticeAsync(text);
        Log(text);
    }
}
