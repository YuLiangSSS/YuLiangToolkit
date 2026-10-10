using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>
/// 「连接信息」面板里的一行：<b>一个可以直接复制给对方的完整地址</b>（已经拼好端口）。
///
/// <para>主机开完房间后，界面把本机的 IPv4 / IPv6 逐个列成这样一行 ——
/// 用户不用自己去 ipconfig 里找，也不用琢磨「到底该发哪个」。
/// 内网穿透工具要的「本地端口」也在这里显示（<see cref="Text"/> 里的端口就是它）。</para>
/// </summary>
public sealed class ShareEndpointRow : ObservableObject
{
    public ShareEndpointRow(string text, string family, string interfaceName, bool isRecommended, string note = "")
    {
        Text = text;
        Family = family;
        InterfaceName = interfaceName;
        IsRecommended = isRecommended;
        Note = note;
    }

    /// <summary>可以直接发给对方的地址，例如 <c>192.168.1.7:5200</c> / <c>[2409:…::1]:5200</c>。</summary>
    public string Text { get; }

    /// <summary><c>IPv4</c> / <c>IPv6</c> / <c>内网穿透</c>。</summary>
    public string Family { get; }

    /// <summary>网卡名（对方连不上时好排查是哪张卡）。</summary>
    public string InterfaceName { get; }

    /// <summary>是不是最可能连得通的那一个（界面上标「推荐」）。</summary>
    public bool IsRecommended { get; }

    /// <summary>附注（例如「这张是虚拟网卡，对方多半连不上」）。</summary>
    public string Note { get; }

    /// <summary>界面上跟在地址后面的小字。</summary>
    public string DetailText => IsRecommended
        ? $"{InterfaceName}　推荐"
        : Note.Length > 0 ? $"{InterfaceName}　{Note}" : InterfaceName;

    public override string ToString() => Text;
}
