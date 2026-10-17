using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.ChatRoom.ViewModels;
using YuLiangToolkit.Tools.ChatRoom.Views;

namespace YuLiangToolkit.Tools.ChatRoom;

/// <summary>
/// 「联机聊天室」功能入口：主机开监听、客户端连 IP:端口 的局域网聊天，
/// 支持文字 / 图片 / 音频 / 视频 / 任意文件，带单次流量上限。
///
/// <para>协议与实现细节见根目录 <c>AGENTS.md</c> 的 2.12 节；
/// 线上协议本身有一份人能读的说明（<c>Data\chat-room\Protocol\chat-protocol.json</c>）。</para>
/// </summary>
public class ChatRoomTool : ITool
{
    /// <summary>功能 Id：同时是 <c>Data\chat-room</c> 与 <c>Config\chat-room</c> 的文件夹名。</summary>
    public string Id => ChatRoomConfig.ToolId;

    public string Name => "联机聊天室";

    public string Description
        => "主机开一个 TCP 端口等人连，客户端填 IP/域名与端口就能聊：文字、图片、音频、视频、任意文件都能发；"
           + "支持 IPv6 与内网穿透，带单次流量上限与用量指示";

    public string EmojiIcon => "💬";

    public string Category => "网络";

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "聊天", "聊天室", "局域网", "联机", "TCP", "内网穿透", "IPv6", "传文件",
        "发图片", "语音", "视频", "流量限制", "主机", "客户端",
    };

    /// <summary>越小越靠前（视频转音频 10，Flash 下载器 20，批量重命名 40）。</summary>
    public int Order => 30;

    public FrameworkElement CreateView()
        => new ChatRoomView { DataContext = new ChatRoomViewModel() };
}
