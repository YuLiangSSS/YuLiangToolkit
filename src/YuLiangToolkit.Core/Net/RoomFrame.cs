using System.Text.Json;

namespace YuLiangToolkit.Core.Net;

/// <summary>
/// 通用 TCP 房间底座的「包头」：<b>4 字节小端长度前缀 + 本对象的 JSON + 裸载荷</b>。
///
/// <para>字段语义与聊天室的 <c>ChatFrameHeader</c> 一致（这里只是搬到 Core 里、与聊天无关）：
/// <see cref="Kind"/> 是包类型（字符串，加减类型不会让老版本错位），
/// <see cref="From"/> 是发送方名字/Id，<see cref="Frame"/>/<see cref="ReplyTo"/> 用来做请求应答配对，
/// <see cref="Extra"/> 放随类型而变的结构化数据，<see cref="Payload"/> 是跟在头后面的裸字节数。</para>
///
/// <para>字符串字段一律可空：传输层的第一原则是容错——对方少个字段不能让本端崩。</para>
/// </summary>
public sealed class RoomFrame
{
    /// <summary>包类型（由使用方约定，例如棋牌房间的 <c>hello</c> / <c>snapshot</c>）。</summary>
    public string Kind { get; set; } = "";

    /// <summary>发送方标识（一般填玩家名或连接 Id；握手前可以为空）。</summary>
    public string? From { get; set; }

    /// <summary>本包自己的唯一 Id（应答时用 <see cref="ReplyTo"/> 指回来）。</summary>
    public string? Frame { get; set; }

    /// <summary>这是对哪个包的应答。</summary>
    public string? ReplyTo { get; set; }

    /// <summary>
    /// 载荷字节数。<b>由 <see cref="RoomProtocol.BuildFrame"/> 统一写入</b>，
    /// 调用方不要自己维护（写错会让对方读错位，极难排查）。
    /// </summary>
    public long Payload { get; set; }

    /// <summary>随类型而变的附加 JSON。</summary>
    public JsonElement? Extra { get; set; }

    /// <summary>把 <see cref="Extra"/> 反序列化成强类型对象；缺失 / 类型不对 / 解析失败返回 <c>null</c>。</summary>
    public T? ExtraAs<T>() where T : class
    {
        if (Extra is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        try
        {
            return element.Deserialize<T>(RoomProtocol.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把任意对象塞进 <see cref="Extra"/>（传 null 等于不塞）。</summary>
    public RoomFrame WithExtra(object? extra)
    {
        if (extra is not null)
            Extra = JsonSerializer.SerializeToElement(extra, RoomProtocol.JsonOptions);
        return this;
    }

    /// <summary>构造一个带头部字段的包（自动生成 <see cref="Frame"/>）。</summary>
    public static RoomFrame New(string kind, string? from = null, object? extra = null, string? replyTo = null)
    {
        var frame = new RoomFrame
        {
            Kind = kind,
            From = from,
            Frame = NewFrameId(),
            ReplyTo = replyTo,
        };
        return frame.WithExtra(extra);
    }

    /// <summary>新包 Id（时间戳 + 随机，日志里按它找一条包很方便）。</summary>
    public static string NewFrameId() => Guid.NewGuid().ToString("N")[..12];
}
