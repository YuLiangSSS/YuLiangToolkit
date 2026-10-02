using System.Text.Json;

namespace YuLiangToolkit.Tools.ChatRoom.Protocol;

/// <summary>
/// 一个包的「头」：4 字节长度前缀之后是它的 JSON，再往后是裸载荷。
/// 字符串字段一律可空（传输层的容错第一：对方少个字段不能让本端崩）。
/// </summary>
public sealed class ChatFrameHeader
{
    /// <summary>包类型，见 <see cref="ChatProtocol.Kinds"/>。</summary>
    public string Kind { get; set; } = "";

    /// <summary>发送方成员 Id（握手前为空）。</summary>
    public string? From { get; set; }

    /// <summary>本条包自己的唯一 Id（应答时用 <see cref="ReplyTo"/> 指回来）。</summary>
    public string? Frame { get; set; }

    /// <summary>这是对哪个包的应答。</summary>
    public string? ReplyTo { get; set; }

    /// <summary>载荷字节数（数据连接上就是文件剩余字节数，进度条靠它）。</summary>
    public long Payload { get; set; }

    /// <summary>随类型而变的附加 JSON（见协议说明文件）。</summary>
    public JsonElement? Extra { get; set; }

    /// <summary>把 <see cref="Extra"/> 反序列化成强类型对象；缺失 / 解析失败返回 <c>default</c>。</summary>
    public T? ExtraAs<T>() where T : class
    {
        if (Extra is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        try { return element.Deserialize<T>(ChatProtocol.JsonOptions); }
        catch { return null; }
    }

    /// <summary>把任意对象塞进 <see cref="Extra"/>。</summary>
    public ChatFrameHeader WithExtra(object? extra)
    {
        if (extra is not null)
            Extra = JsonSerializer.SerializeToElement(extra, ChatProtocol.JsonOptions);
        return this;
    }

    /// <summary>构造一个带头部字段的包。</summary>
    public static ChatFrameHeader New(string kind, string? from, object? extra = null, string? replyTo = null)
    {
        var header = new ChatFrameHeader
        {
            Kind = kind,
            From = from,
            Frame = NewFrameId(),
            ReplyTo = replyTo,
        };
        return header.WithExtra(extra);
    }

    /// <summary>新包 Id（时间戳 + 随机，日志里按它找一条消息很方便）。</summary>
    public static string NewFrameId() => Guid.NewGuid().ToString("N")[..12];
}
