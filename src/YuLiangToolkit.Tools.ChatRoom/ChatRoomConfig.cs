using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ChatRoom.Traffic;

namespace YuLiangToolkit.Tools.ChatRoom;

/// <summary>本端这次扮演什么角色。</summary>
public enum ChatRole
{
    /// <summary>开一个端口等人连（要能被对方访问到：局域网直接填本机 IP，外网要端口映射 / 内网穿透）。</summary>
    Host = 0,

    /// <summary>连到别人的主机上。</summary>
    Client = 1,
}

/// <summary>发图片前要不要先缩一下（省流量的关键开关）。</summary>
public enum ImageQualityPreset
{
    /// <summary>原图直发（最清楚，也最费流量）。</summary>
    Original = 0,

    /// <summary>只压过大的图（推荐）：长边超过阈值才缩。</summary>
    Balance = 1,

    /// <summary>全部压到 1280（图片消息会明显变小）。</summary>
    Small = 2,

    /// <summary>全部压到 800（最省流量，看清内容够用）。</summary>
    Tiny = 3,
}

/// <summary>
/// 「联机聊天室」的选项 → <c>Config\chat-room\config.json</c>。
/// 属性一改就自动落盘，界面直接双向绑定，没有“保存”按钮。
/// </summary>
public sealed class ChatRoomConfig : ToolConfig
{
    /// <summary>功能 Id（同时是 <c>Data\</c> / <c>Config\</c> 下的小文件夹名）——定了就别改。</summary>
    public const string ToolId = "chat-room";

    // ---------------------------------------------------------------- 身份

    /// <summary>昵称（对方看到的名字）。</summary>
    public string Nickname { get; set; } = "";

    /// <summary>头像（就是一个 emoji，省得做头像上传）。</summary>
    public string Emoji { get; set; } = "🐟";

    // ---------------------------------------------------------------- 主机

    /// <summary>监听地址：<c>0.0.0.0</c>（IPv4 全开，推荐）/ <c>::</c>（含 IPv6）/ 具体网卡地址。</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>监听端口（填 0 = 让系统随便挑一个空闲端口）。</summary>
    public int ListenPort { get; set; } = 5200;

    /// <summary>
    /// 傻瓜模式（默认开）：不让用户填监听地址与端口，工具自己挑
    /// （优先双栈 <c>::</c> + 上次那个端口，占了就换一个空闲的），
    /// 开完房间直接把「本机 IPv4 / IPv6 + 端口」摊在界面上。
    ///
    /// <para>注意：<b>不是把 IP 和端口藏起来</b>——内网穿透工具要照着填本机端口，
    /// 所以只是不让用户「选」，该显示的一个都不少。</para>
    /// </summary>
    public bool SimpleMode { get; set; } = true;

    // ---------------------------------------------------------------- 客户端

    /// <summary>要连的主机地址：IPv4 / IPv6 / 域名都可以；也可以带端口（<c>1.2.3.4:5200</c>）。</summary>
    public string HostAddress { get; set; } = "";

    /// <summary>要连的端口。</summary>
    public int HostPort { get; set; } = 5200;

    // ---------------------------------------------------------------- 流量

    /// <summary>
    /// 单次连接的流量上限（MB；0 = 不限）。一次“连接”= 一次会话 = 一个会话文件夹，
    /// 断开重连就是新的一次。
    /// </summary>
    public double TrafficLimitMb { get; set; } = 100;

    /// <summary>上限针对哪个方向（收发合计 / 仅上传 / 仅下载 / 不限）。</summary>
    public TrafficQuotaScope TrafficScope { get; set; } = TrafficQuotaScope.Both;

    /// <summary>剩多少额度时开始提醒（MB）。</summary>
    public double TrafficWarnMb { get; set; } = 10;

    /// <summary>快到上限 / 被拦下时顺便发一条系统消息告诉对方。</summary>
    public bool NotifyPeerOnLimit { get; set; } = true;

    // ---------------------------------------------------------------- 发送前的本地处理

    /// <summary>发图片前先缩一下（省流量）。</summary>
    public ImageQualityPreset ImageQuality { get; set; } = ImageQualityPreset.Balance;

    /// <summary>「只压过大的图」的阈值：长边超过它才缩。</summary>
    public int ImageMaxEdge { get; set; } = 1920;

    /// <summary>JPEG 质量（30~100）。</summary>
    public int ImageJpegQuality { get; set; } = 82;

    /// <summary>文件大于这个大小（MB）就先问一句再发。</summary>
    public double ConfirmFileMb { get; set; } = 20;

    // ---------------------------------------------------------------- 界面 / 记录

    /// <summary>聊天列表最多显示多少条（太多会卡；记录文件本身不受影响）。</summary>
    public int MaxMessageItems { get; set; } = 800;

    /// <summary>打开工具时自动按上次的角色准备（主机就自动开始监听）。</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>收到消息时把窗口标题标个星号（不抢焦点、不弹窗）。</summary>
    public bool MarkUnreadTitle { get; set; } = true;

    // ---------------------------------------------------------------- 上次状态

    /// <summary>上次扮演的角色（下次打开默认选它）。</summary>
    public ChatRole LastRole { get; set; } = ChatRole.Host;

    /// <summary>上次开始连接的会话文件夹名（界面上显示“最后一次记录在哪”）。</summary>
    public string LastSessionName { get; set; } = "";

    /// <summary>
    /// 最近几次会话（最新在前，最多 20 条）。与外壳的 <c>LastUsed</c> 一个套路：
    /// 列表原地增删改照样会落盘（自动保存靠内容比对，不依赖变更通知）。
    /// </summary>
    public List<string> RecentSessions { get; set; } = new();

    /// <summary>把一次会话记进「最近会话」（去重 + 只留 20 条）。</summary>
    public void RememberSession(string sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName)) return;
        RecentSessions.RemoveAll(s => string.Equals(s, sessionName, StringComparison.OrdinalIgnoreCase));
        RecentSessions.Insert(0, sessionName);
        while (RecentSessions.Count > 20) RecentSessions.RemoveAt(RecentSessions.Count - 1);
        LastSessionName = sessionName;
    }

    // ---------------------------------------------------------------- 计算属性

    /// <summary>单次流量上限的字节数（0 = 不限）。</summary>
    [JsonIgnore]
    public long TrafficLimitBytes => TrafficLimitMb <= 0 ? 0 : (long)(TrafficLimitMb * 1024 * 1024);

    /// <summary>剩余额度提醒阈值（字节）。</summary>
    [JsonIgnore]
    public long TrafficWarnBytes => TrafficWarnMb <= 0 ? 0 : (long)(TrafficWarnMb * 1024 * 1024);

    /// <summary>确认阈值（字节）。</summary>
    [JsonIgnore]
    public long ConfirmFileBytes => ConfirmFileMb <= 0 ? 0 : (long)(ConfirmFileMb * 1024 * 1024);

    /// <summary>有效监听端口（0 = 让系统挑）。</summary>
    [JsonIgnore]
    public int EffectiveListenPort => ListenPort is >= 0 and <= 65535 ? ListenPort : 5200;

    /// <summary>有效连接端口。</summary>
    [JsonIgnore]
    public int EffectiveHostPort => HostPort is >= 1 and <= 65535 ? HostPort : 5200;

    /// <summary>昵称（空的话给个默认名，免得对方看到一个空白名字）。</summary>
    [JsonIgnore]
    public string EffectiveNickname => string.IsNullOrWhiteSpace(Nickname) ? $"用户{Environment.MachineName}" : Nickname.Trim();

    /// <summary>界面上的阈值文案（发图片前给用户看的预估）。</summary>
    [JsonIgnore]
    public string ImageQualityText => ImageQuality switch
    {
        ImageQualityPreset.Original => "原图直发（最清楚，最费流量）",
        ImageQualityPreset.Small => "压到长边 1280（明显省流量）",
        ImageQualityPreset.Tiny => "压到长边 800（最省流量）",
        _ => $"长边超过 {ImageMaxEdge} 像素才缩（推荐）",
    };

    /// <summary>按预设算出发图时要用的长边上限（0 = 不处理）。</summary>
    [JsonIgnore]
    public int EffectiveImageMaxEdge => ImageQuality switch
    {
        ImageQualityPreset.Original => 0,
        ImageQualityPreset.Small => 1280,
        ImageQualityPreset.Tiny => 800,
        _ => Math.Clamp(ImageMaxEdge, 320, 8000),
    };

    /// <summary>流量上限的一行说明。</summary>
    [JsonIgnore]
    public string TrafficLimitText => TrafficLimitBytes <= 0
        ? "不限流量"
        : $"{FileSize.Format(TrafficLimitBytes)} / 次（{TrafficScope switch
        {
            TrafficQuotaScope.Upload => "仅上传",
            TrafficQuotaScope.Download => "仅下载",
            TrafficQuotaScope.Off => "不限",
            _ => "收发合计",
        }}）";
}
