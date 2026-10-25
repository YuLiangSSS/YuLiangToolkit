using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

/// <summary>
/// 「房间信息」面板里的一行：<b>一个可以直接复制给对方的完整地址</b>（已经拼好端口）。
///
/// <para>主机开完房间后，把本机的 IPv4 / IPv6 地址逐个列成这样一行 ——
/// 用户不用自己去 ipconfig 里找，也不用琢磨「到底该发哪个」。</para>
/// </summary>
public sealed class RoomShareRow : ObservableObject
{
    /// <summary>可以直接发给对方的地址，例如 <c>192.168.1.7:5310</c> / <c>[2409:…::1]:5310</c>。</summary>
    public string AddressText { get; init; } = "";

    /// <summary>行首显示的完整文本（目前与 <see cref="AddressText"/> 相同，留给以后加附注）。</summary>
    public string DisplayText { get; init; } = "";

    /// <summary><c>IPv4</c> / <c>IPv6</c>。</summary>
    public string Family { get; init; } = "";

    /// <summary>网卡名（对方连不上时好排查是哪张卡）；推荐的会带「（推荐）」。目前不显示在界面上。</summary>
    public string InterfaceText { get; init; } = "";

    /// <summary>是不是最可能连得通的那一个。</summary>
    public bool IsRecommended { get; init; }

    /// <summary>是不是 IPv6（界面上标个小字）。</summary>
    public bool IsIpv6 => string.Equals(Family, "IPv6", StringComparison.OrdinalIgnoreCase);

    /// <summary>界面上的小字（推荐 / IPv6 / 网卡名）。</summary>
    public string DetailText
    {
        get
        {
            if (IsRecommended && IsIpv6) return "IPv6　推荐";
            if (IsRecommended) return "推荐";
            return IsIpv6 ? "IPv6" : "";
        }
    }

    public override string ToString() => AddressText;
}
