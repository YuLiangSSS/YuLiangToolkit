using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Games.Tetris.ViewModels;

/// <summary>
/// 俄罗斯方块的<b>「中间对战区 + 预览位置 + 观战视角」</b>（partial 扩展）。
///
/// <para><b>布局</b>（按需求）：中间那块分成左「我的棋盘」/ 右「对手（或我正在看的人）的棋盘」。
/// 预览（暂存 + 接下来）挪到<b>我的棋盘内</b>的某个角，四角随便选。</para>
/// </summary>
public sealed partial class TetrisViewModel
{
    // ---------------------------------------------------------------- 预览位置

    /// <summary>预览位置选项（左上 / 右上 / 左下 / 右下）。</summary>
    public IReadOnlyList<OptionItem<string>> PreviewCornerOptions { get; } = OptionItem.From(
        ("tl", "左上"), ("tr", "右上"), ("bl", "左下"), ("br", "右下"));

    /// <summary>当前预览位置（下拉绑定）。</summary>
    public OptionItem<string> SelectedPreviewCorner
    {
        get => OptionItem<string>.Find(PreviewCornerOptions, Cfg.PreviewCorner) ?? PreviewCornerOptions[1];
        set
        {
            if (value is null) return;
            Cfg.PreviewCorner = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewAtTop));
            OnPropertyChanged(nameof(PreviewAtLeft));
        }
    }

    /// <summary>预览在上方（左上 / 右上）。</summary>
    public bool PreviewAtTop => !string.Equals(Cfg.PreviewCorner, "bl", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(Cfg.PreviewCorner, "br", StringComparison.OrdinalIgnoreCase);

    /// <summary>预览在左边（左上 / 左下）。</summary>
    public bool PreviewAtLeft => !string.Equals(Cfg.PreviewCorner, "tr", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(Cfg.PreviewCorner, "br", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 中间对战区

    /// <summary>中间是否显示「对战区」（联机时才有对手可看）。</summary>
    public bool DuelPanelVisible => IsNetMode;

    /// <summary>左侧大棋盘的标题：普通玩家 = 我；观战者 = 我正在看的那个人。</summary>
    public string LeftBoardTitle => IsSpectating ? $"观战：{_viewingName}" : "我的棋盘";

    /// <summary>右侧棋盘标题。</summary>
    public string RightBoardTitle => IsSpectating ? "另一位玩家" : "对手";

    /// <summary>右侧棋盘状态（对手的，或观战时另一位的）。</summary>
    public TetrisBoardViewState DuelBoardState => GuestBoardState;

    /// <summary>右侧有没有人在下（没人在时显示占位）。</summary>
    public bool DuelBoardHasOpponent => _session?.IsHost == true ? _guestPresent : true;

    // ---------------------------------------------------------------- 观战视角

    /// <summary>观战者名单一行文字。</summary>
    public string SpectatorLine => SpectatorNames.Count == 0
        ? "暂无观战者"
        : $"观战（{SpectatorNames.Count}）：{string.Join("、", SpectatorNames)}";

    /// <summary>能不能切换观战视角（只有观战者能切）。</summary>
    public bool CanSwitchView => IsSpectating;

    /// <summary>正在看的玩家名。</summary>
    private string _viewingName = "房主";

    /// <summary>切换观战视角命令。</summary>
    public RelayCommand SwitchViewCommand { get; private set; } = null!;

    /// <summary>初始化（主构造函数调用）。</summary>
    private void InitializeDuelCommands()
    {
        SwitchViewCommand = new RelayCommand(_ => SwitchView(), _ => CanSwitchView);
    }

    /// <summary>
    /// 切换观战视角：在两位对局玩家之间来回切（把「正在看的那个人」放进大棋盘）。
    /// <para>实现方式：交换左右两块棋盘的来源。观战者自己没有棋盘，两块都是别人的。</para>
    /// </summary>
    private void SwitchView()
    {
        if (!IsSpectating) return;

        // 交换「大棋盘」与「右侧小棋盘」的显示内容
        var swap = _spectatorHostView;

        _spectatorHostView = !swap;
        GuestBoardState = swap ? HostSideBoard : GuestSideBoard;

        _viewingName = _spectatorHostView ? HostSideName : GuestSideName;
        if (_viewingName.Length == 0) _viewingName = _spectatorHostView ? "房主" : "对手";

        OnPropertyChanged(nameof(LeftBoardTitle));
        OnPropertyChanged(nameof(RightBoardTitle));
        OnPropertyChanged(nameof(DuelBoardState));
        Log($"切换到观战 {_viewingName}");
    }

    /// <summary>观战者当前是否在看「房主那一侧」。</summary>
    private bool _spectatorHostView = true;

    /// <summary>房主那一侧的棋盘 / 名字（观战用）。</summary>
    private TetrisBoardViewState HostSideBoard { get; set; } = TetrisBoardViewState.Empty;

    /// <summary>对手那一侧的棋盘。</summary>
    private TetrisBoardViewState GuestSideBoard { get; set; } = TetrisBoardViewState.Empty;

    private string HostSideName { get; set; } = "房主";
    private string GuestSideName { get; set; } = "对手";

    /// <summary>统一通知中间对战区相关属性。</summary>
    private void NotifyDuelState()
    {
        OnPropertyChanged(nameof(DuelPanelVisible));
        OnPropertyChanged(nameof(LeftBoardTitle));
        OnPropertyChanged(nameof(RightBoardTitle));
        OnPropertyChanged(nameof(DuelBoardState));
        OnPropertyChanged(nameof(DuelBoardHasOpponent));
        OnPropertyChanged(nameof(SpectatorLine));
        OnPropertyChanged(nameof(CanSwitchView));
        OnPropertyChanged(nameof(PreviewAtTop));
        OnPropertyChanged(nameof(PreviewAtLeft));
    }
}
