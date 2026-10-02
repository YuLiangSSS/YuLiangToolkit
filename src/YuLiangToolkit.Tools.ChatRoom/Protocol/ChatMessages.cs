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
