using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

/// <summary>
/// 房间成员列表里的一行（房主 + 每个客户端 + 掉线保留的成员）。
///
/// <para>由基类维护：成员表变化时<b>原地更新</b>（不重建整个列表，界面上的选中/滚动位置不会跳），
/// 席位 / 身份这些派生文案在 <see cref="Apply"/> 里一次性算好，XAML 直接绑。</para>
/// </summary>
public sealed class BoardMemberItem : ObservableObject
{
    private string _name = "";
    private bool _isHost;
    private bool _isSelf;
    private bool _isOnline;
    private int _gamesPlayed;
    private int _joinOrder;
    private string _seat = "";

    /// <summary>成员 id（房主固定 <c>host</c>，客户端 <c>m1</c>/<c>m2</c>…）。</summary>
    public string MemberId { get; init; } = "";

    /// <summary>玩家名（会话重连改名字时会变）。</summary>
    public string Name
    {
        get => _name;
        private set
        {
            if (!Set(ref _name, value ?? "")) return;
            OnPropertyChanged(nameof(NameText));
        }
    }

    /// <summary>是不是房主。</summary>
    public bool IsHost
    {
        get => _isHost;
        private set
        {
            if (!Set(ref _isHost, value)) return;
            OnPropertyChanged(nameof(HostBadgeText));
            OnPropertyChanged(nameof(NameText));
        }
    }

    /// <summary>是不是本端（界面标「你」）。</summary>
    public bool IsSelf
    {
        get => _isSelf;
        private set
        {
            if (!Set(ref _isSelf, value)) return;
            OnPropertyChanged(nameof(IsSelfText));
            OnPropertyChanged(nameof(NameText));
        }
    }

    /// <summary>当前在不在线（掉线但保留在房间里的成员是 false）。</summary>
    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (!Set(ref _isOnline, value)) return;
            OnPropertyChanged(nameof(OnlineText));
        }
    }

    /// <summary>完整打完的局数（作废 / 中途退出不算）。</summary>
    public int GamesPlayed
    {
        get => _gamesPlayed;
        private set
        {
            if (!Set(ref _gamesPlayed, value)) return;
            OnPropertyChanged(nameof(GamesPlayedText));
        }
    }

    /// <summary>进房顺序（越小越早；自动轮换「轮流上场」按它排）。</summary>
    public int JoinOrder
    {
        get => _joinOrder;
        private set => Set(ref _joinOrder, value);
    }

    /// <summary>坐在哪个席位：<c>""</c>（观战 / 空位）/ <c>"first"</c>（先手位）/ <c>"second"</c>（后手位）。</summary>
    public string Seat
    {
        get => _seat;
        private set
        {
            if (!Set(ref _seat, value ?? "")) return;
            OnPropertyChanged(nameof(IsSeated));
            OnPropertyChanged(nameof(SeatText));
            OnPropertyChanged(nameof(RoleText));
        }
    }

    /// <summary>是不是坐在上场席位上（没坐 = 观战）。</summary>
    public bool IsSeated => _seat.Length > 0;

    // ---------------------------------------------------------------- 界面文案（XAML 直接绑）

    /// <summary>名字，本端后缀「（你）」。</summary>
    public string NameText => (Name.Length == 0 ? "玩家" : Name) + (IsSelf ? "（你）" : "");

    /// <summary>房主徽标文案（不是房主时为空）。</summary>
    public string HostBadgeText => IsHost ? "房主" : "";

    /// <summary>「你」徽标文案（不是本端时为空）。</summary>
    public string IsSelfText => IsSelf ? "你" : "";

    /// <summary>身份：房主 / 玩家。</summary>
    public string RoleText => IsHost ? "房主" : "玩家";

    /// <summary>位置：先手位 / 后手位 / 观战。</summary>
    public string SeatText => _seat switch
    {
        "first" => "先手位",
        "second" => "后手位",
        _ => "观战",
    };

    /// <summary>上场次数文案。</summary>
    public string GamesPlayedText => $"已打 {GamesPlayed} 局";

    /// <summary>在线状态文案。</summary>
    public string OnlineText => IsOnline ? "在线" : "离线";

    /// <summary>
    /// 用最新成员信息刷新这一行（原地更新，属性变了才发通知）。
    /// </summary>
    /// <param name="name">玩家名。</param>
    /// <param name="isHost">是不是房主。</param>
    /// <param name="isSelf">是不是本端。</param>
    /// <param name="isOnline">在不在线。</param>
    /// <param name="gamesPlayed">打完的局数。</param>
    /// <param name="joinOrder">进房顺序。</param>
    /// <param name="seat">席位（"" / "first" / "second"）。</param>
    public void Apply(string? name, bool isHost, bool isSelf, bool isOnline,
        int gamesPlayed, int joinOrder, string? seat)
    {
        Name = name ?? "";
        IsHost = isHost;
        IsSelf = isSelf;
        IsOnline = isOnline;
        GamesPlayed = gamesPlayed;
        JoinOrder = joinOrder;
        Seat = seat ?? "";
    }

    /// <summary>排错用：一行文本。</summary>
    public override string ToString()
        => $"{NameText}{(IsHost ? "（房主）" : "")} · {SeatText} · {GamesPlayedText} · {OnlineText}";
}

/// <summary>
/// 席位下拉框里的一个选项：某个成员 或 「空位」。
/// <see cref="MemberId"/> 为空字符串时表示空位（把这一席清空）。
/// </summary>
public sealed class BoardSeatOption
{
    /// <summary>创建一个席位选项。</summary>
    /// <param name="memberId">成员 id；空字符串 = 空位。</param>
    /// <param name="displayName">下拉框里显示的文字。</param>
    public BoardSeatOption(string memberId, string displayName)
    {
        MemberId = (memberId ?? "").Trim();
        DisplayName = (displayName ?? "").Trim();
    }

    /// <summary>成员 id（空字符串 = 空位）。</summary>
    public string MemberId { get; }

    /// <summary>下拉框显示文字。</summary>
    public string DisplayName { get; }

    /// <summary>是不是「空位」选项。</summary>
    public bool IsEmpty => MemberId.Length == 0;

    /// <summary>空位选项（文案可换，默认「空位」）。</summary>
    public static BoardSeatOption Empty(string displayName = "空位") => new("", displayName);

    public override string ToString() => DisplayName;
}
