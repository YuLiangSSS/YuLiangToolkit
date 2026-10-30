using System.Windows;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.ChatRoom.ViewModels;
using YuLiangToolkit.Tools.ChatRoom.Views;

namespace YuLiangToolkit.Tools.ChatRoom;

/// <summary>
/// 「联机聊天室」功能入口：主机开监听、客户端连 IP:端口 的局域网聊天，
/// 支持文字 / 图片 / 音频 / 视频 / 任意文件，带单次流量上限。
///
/// <para>协议与实现细节见 <c>docs/dev/pitfalls.md</c>；
/// 线上协议本身有一份人能读的说明（<c>Data\chat-room\Protocol\chat-protocol.json</c>）。</para>
/// </summary>
public class ChatRoomTool : ITool, IToolCacheSource
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

    /// <summary>
    /// 告诉主页「设置 → 清理缓存」本功能有哪些可以清的东西。
    ///
    /// <para>本功能的「最近会话列表」（最近连过 / 开过的主机名）存在
    /// <c>Config\chat-room\config.json</c> 里；这里只清这份「最近」列表，
    /// 聊天记录文件（<c>Data\chat-room</c> 下的历史消息）不动，昵称 / 端口等设置也不动。
    /// 列表为空时返回空数组。</para>
    ///
    /// <para><b>以后新工具怎么接</b>：让工具类多实现 <see cref="IToolCacheSource"/>，
    /// 在这里返回「Id + 名称 + 说明 + 当前数量 + 清理动作」（<see cref="ToolCacheItem"/>）即可，
    /// 外壳会自动收集所有实现了该接口的工具，不实现就等于没有缓存项。</para>
    /// </summary>
    public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
    {
        // 直接读盘上的配置（不触发工具加载），拿「最近会话列表」条数。
        var count = ToolConfigPeek.Read<ChatRoomConfig>(ChatRoomConfig.ToolId)?.RecentSessions?.Count ?? 0;
        if (count == 0) return Array.Empty<ToolCacheItem>();

        return new[]
        {
            new ToolCacheItem
            {
                Id = "recent-sessions",
                Name = "最近会话列表",
                Description = "存在 Config\\chat-room\\config.json 里，只清「最近」列表；聊天记录文件与其它设置不动",
                Detail = $"{count} 个会话",
                // 清空后配置照常自动落盘；聊天记录文件不受影响。
                Clear = () => ToolConfigPeek.Mutate<ChatRoomConfig>(
                    ChatRoomConfig.ToolId, config => { config.RecentSessions?.Clear(); }),
            },
        };
    }
}
