using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.ChatRoom.Protocol;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>成员列表里的一行。</summary>
public sealed class ChatMemberItem : ObservableObject
{
    private bool _isOnline = true;

    public ChatMemberItem(ChatMember member, bool isSelf)
    {
        Member = member;
        IsSelf = isSelf;
    }

    public ChatMember Member { get; }

    /// <summary>是不是本机自己。</summary>
    public bool IsSelf { get; }

    /// <summary>本端是主机、且这一行是客户端 → 界面显示「移出」按钮。</summary>
    public bool CanKick { get; init; }

    /// <summary>已连上 / 已离开（离开的那行灰掉，不清掉，免得用户以为看花了眼）。</summary>
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (!Set(ref _isOnline, value)) return;
            OnPropertyChanged(nameof(Opacity));
        }
    }

    public double Opacity => IsOnline ? 1.0 : 0.45;

    public string Display => $"{Member.Emoji} {Member.Name}{(IsSelf ? "（我）" : "")}";

    public string RoleText => Member.IsHost ? "主机" : "客户端";

    public string EndpointText => Member.Endpoint.Length > 0 ? Member.Endpoint : "（本机）";

    public string JoinedText => Member.JoinedAt.ToString("HH:mm:ss");

    public string Describe() => $"{RoleText}　{Display}　{EndpointText}";

    /// <summary>成员信息在外部被改了之后（改名 / 换头像 / 换地址），通知界面刷新全部展示字段。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(EndpointText));
        OnPropertyChanged(nameof(JoinedText));
    }

    public override string ToString() => Describe();
}
