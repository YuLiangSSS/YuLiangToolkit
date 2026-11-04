using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.ChatRoom.Protocol;

/// <summary>
/// 协议里的成员 / 一次性数据记录（都当成 DTO 用，属性名 camelCase 上线）。
/// 放在一个文件里是因为它们都很短，而且改协议时应当一起看。
/// </summary>
public sealed class ChatMember
{
    public string MemberId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>头像（一个 emoji，省得做图片头像上传）。</summary>
    public string Emoji { get; set; } = "🙂";

    /// <summary><c>Host</c> 或 <c>Client</c>。</summary>
    public string Role { get; set; } = "Client";

    /// <summary>对方地址（主机上显示 <c>192.168.1.7:52341</c> 这种，方便排错）。</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>连上 / 进来的时间（本地时间）。</summary>
    public DateTime JoinedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public bool IsHost => string.Equals(Role, "Host", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore] public string Display => $"{Emoji} {Name}";

    public ChatMember Clone() => new()
    {
        MemberId = MemberId,
        Name = Name,
        Emoji = Emoji,
        Role = Role,
        Endpoint = Endpoint,
        JoinedAt = JoinedAt,
    };
}

/// <summary>握手包（<c>hello</c>）的载荷。</summary>
public sealed class HelloInfo
{
    public string Magic { get; set; } = ChatProtocol.Magic;
    public int ProtocolVersion { get; set; } = ChatProtocol.Version;
    public string Role { get; set; } = "Client";
    public string MemberId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Emoji { get; set; } = "🙂";
    public string AppVersion { get; set; } = "";
    /// <summary>主机告诉客户端自己实际监听的端口（端口填 0 自动挑时，客户端要知道真实端口）。</summary>
    public int ListenPort { get; set; }

    /// <summary>
    /// 这条连接是不是「通话媒体连接」（true = 用 <c>callHello</c> 握手，只跑音频 / 视频包）。
    /// <para>普通聊天连接为 false；老版本主机把 <c>callHello</c> 当未知包跳过 → 握手超时 →
    /// 本端提示「对方版本不支持通话」，不会把媒体连接误当成一个聊天成员。</para>
    /// </summary>
    public bool IsMedia { get; set; }

    /// <summary>通话媒体连接用的令牌（聊天 welcome 时主机发给本端的，防止别人往通话里灌包）。</summary>
    public string CallToken { get; set; } = "";
}

/// <summary>主机回给客户端的 <c>welcome</c> 载荷。</summary>
public sealed class WelcomeInfo
{
    /// <summary>主机给你定的成员 Id（客户端之后报这个 Id，同名也不会混）。</summary>
    public string MemberId { get; set; } = "";
    /// <summary>本次会话的数据文件夹名（<c>2025.1.3-14.5.9</c> 这种）。</summary>
    public string SessionName { get; set; } = "";
    public string HostName { get; set; } = "";
    public List<ChatMember> Members { get; set; } = new();

    /// <summary>本端开通话媒体连接时要带上的令牌（只在回给这个客户端的 welcome 里有值）。</summary>
    public string CallToken { get; set; } = "";

    /// <summary>主机接受了这条通话媒体连接（<c>callHello</c> 的回执）。</summary>
    public bool MediaAccepted { get; set; }

    /// <summary>拒绝通话媒体连接的原因（<see cref="MediaAccepted"/> = false 时有值）。</summary>
    public string MediaRejectReason { get; set; } = "";
}

/// <summary>通话里的一个人（<c>callState</c> 里的一项；线上是 camelCase）。</summary>
public sealed class CallParticipantWire
{
    public string MemberId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Emoji { get; set; } = "🙂";
    public bool IsHost { get; set; }

    /// <summary>麦克风开着没有（关 = 静音，别人看不到也听不到你的声音）。</summary>
    public bool MicOn { get; set; } = true;

    /// <summary>摄像头开着没有（关 = 别人看到的是头像）。</summary>
    public bool CameraOn { get; set; } = true;

    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;

    public CallParticipantWire Clone() => (CallParticipantWire)MemberwiseClone();
}

/// <summary>
/// 通话状态（<c>callState</c>，主机唯一权威，广播给所有聊天连接 + 主机本端界面）。
/// <para>客户端不自己维护参与者名单：收到什么就显示什么，避免两端状态不一致。</para>
/// </summary>
public sealed class CallStateInfo
{
    /// <summary>现在有没有通话（没人为 false；<see cref="Participants"/> 为空）。</summary>
    public bool Active { get; set; }

    /// <summary>这轮通话什么时候开始的（UTC；没开始为 null）。</summary>
    public DateTime? StartedAtUtc { get; set; }

    public List<CallParticipantWire> Participants { get; set; } = new();
}

/// <summary>
/// 通话控制（<c>callControl</c>，在媒体连接上，客户端 → 主机）：麦克风 / 摄像头开关。
/// 主机收到后更新自己的参与者快照，再广播 <c>callState</c> 让所有人看到。
/// </summary>
public sealed class CallControlInfo
{
    /// <summary>null = 这一项不变。</summary>
    public bool? MicOn { get; set; }

    public bool? CameraOn { get; set; }
}

/// <summary>音频 / 视频包的附带信息（<c>callAudio</c> / <c>callVideo</c> 的 extra）。</summary>
public sealed class CallMediaInfo
{
    /// <summary>发送时刻（发送端本机毫秒，统计延迟用；不参与解码）。</summary>
    public long TimestampMs { get; set; }
}

/// <summary>
/// 语音消息（<c>voice</c>）的元数据：整段 MP3 放在裸载荷里，这里只放「谁、什么时候、多少秒」。
/// <para>接收端靠 <see cref="DurationSeconds"/> 在气泡上显示秒数、写进聊天记录，历史回看也用它。</para>
/// </summary>
public sealed class VoiceInfo
{
    public string MessageId { get; set; } = "";
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime SentLocal { get; set; } = DateTime.Now;
    public ChatMember? Member { get; set; }

    /// <summary>录音时长（秒，至少 1；界面上显示成 <c>5″</c>）。</summary>
    public int DurationSeconds { get; set; }

    /// <summary>建议的文件名（接收端落盘用，重名会自动加序号）。</summary>
    public string FileName { get; set; } = "";
}

/// <summary>成员名单（<c>members</c>）载荷。</summary>
public sealed class MemberListInfo
{
    public List<ChatMember> Members { get; set; } = new();
}

/// <summary>文字 / 系统消息（<c>text</c>、<c>system</c>）载荷。</summary>
public sealed class TextInfo
{
    public string MessageId { get; set; } = "";
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime SentLocal { get; set; } = DateTime.Now;
    public ChatMember? Member { get; set; }
}

/// <summary>系统消息载荷（<c>system</c>）。</summary>
public sealed class SystemInfo
{
    public string MessageId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime SentLocal { get; set; } = DateTime.Now;
    public ChatMember? Member { get; set; }
}

/// <summary>被移出房间（<c>kick</c>）载荷。</summary>
public sealed class KickInfo
{
    public string MemberId { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>文件开始发送（<c>transferStart</c>）载荷。</summary>
public sealed class TransferInfo
{
    public string TransferId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>image / audio / video / archive / doc / file（决定落在哪个子目录、界面上怎么预览）。</summary>
    public string ContentKind { get; set; } = "file";
    /// <summary>中文类型说明（"图片" / "视频"…），界面上直接显示。</summary>
    public string KindLabel { get; set; } = "文件";
    public string Sha256 { get; set; } = "";
    public uint Crc32 { get; set; }
    /// <summary>同一条消息的 Id：图片缩略图与原图共享它，界面才能把它们并成一条。</summary>
    public string MessageId { get; set; } = "";
    /// <summary>发送方的留言（可空）。</summary>
    public string? Note { get; set; }
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime SentLocal { get; set; } = DateTime.Now;
    /// <summary>true = 这是自动压缩出来的预览图（不是用户发的原件）。</summary>
    public bool IsPreview { get; set; }
}

/// <summary>文件接收结果（<c>transferDone</c>）载荷。</summary>
public sealed class TransferResultInfo
{
    public string TransferId { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>落到对方会话文件夹里的相对路径（出错时为空）。</summary>
    public string? SavedAs { get; set; }
    /// <summary>对方那边算出来的 CRC32（用来判断“传过去的字节是不是原样”）。</summary>
    public uint Crc32 { get; set; }
}

/// <summary>取消 / 失败（<c>transferCancel</c>）载荷。</summary>
public sealed class CancelInfo
{
    public string TransferId { get; set; } = "";
    public string? Error { get; set; }
}

/// <summary>再见（<c>bye</c>）载荷。</summary>
public sealed class ByeInfo
{
    public string Reason { get; set; } = "";
}

/// <summary>把对象转成 <see cref="JsonElement"/>（塞进 <see cref="ChatFrameHeader.Extra"/> 用）。</summary>
public static class ChatJson
{
    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, ChatProtocol.JsonOptions);
}
