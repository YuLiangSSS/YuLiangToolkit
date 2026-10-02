using System.Text.Json;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ChatRoom.Media;
using YuLiangToolkit.Tools.ChatRoom.Protocol;
using YuLiangToolkit.Tools.ChatRoom.Sessions;
using YuLiangToolkit.Tools.ChatRoom.Traffic;
using YuLiangToolkit.Tools.ChatRoom.ViewModels;
using ChatLogEntry = YuLiangToolkit.Tools.ChatRoom.Sessions.ChatSessionStore.ChatLogEntry;

namespace YuLiangToolkit.Tools.ChatRoom.Net;

/// <summary>会话里的一条“轻量通知”（界面把它显示成状态栏一行字）。</summary>
/// <param name="Level">信息 / 警告 / 错误。</param>
/// <param name="Text">内容。</param>
public readonly record struct ChatNotice(ChatNoticeLevel Level, string Text);

public enum ChatNoticeLevel { Info, Warn, Error }

/// <summary>
/// 主机端与客户端的共同部分：一条（主机是多条）连接 + 会话文件夹 + 聊天记录 + 文件传输 + 流量计量。
///
/// <para>主机与客户端的差别其实很小 —— 都是「一束连接 + 一个会话文件夹 + 一堆要落盘的消息」。
/// 差别只有：主机 accept 并转发给其它人（<see cref="ChatHost"/>），客户端只连一条（<see cref="ChatClient"/>）。
/// 所以公共逻辑全在这里，子类只写各自那点不同。</para>
///
/// <para><b>落盘时机</b>：每条消息（自己发的 / 收到的）都会立刻追加一行到
/// <c>chat-log.jsonl</c>；文件是先落盘再写记录。这样即使程序被强杀，已经聊过的内容也在。</para>
/// </summary>
public abstract class ChatRoomConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _transferGate = new(1, 1);   // 一次只传一个文件（见 ChatLink 的说明）
    private readonly CancellationTokenSource _cts = new();
    private int _messageCounter;

    protected ChatRoomConnection(string toolId, string localName, string localEmoji, TrafficMeter meter, ChatSessionStore store)
    {
        ToolId = toolId;
        LocalName = localName;
        LocalEmoji = localEmoji;
        Meter = meter;
        Store = store;
        Info = new ChatSessionStore.ChatSessionInfo
        {
            AppVersion = AppVersionText,
            TrafficLimitBytes = meter.LimitBytes,
        };
    }

    /// <summary>会话数据的文件门面。</summary>
    public ChatSessionStore Store { get; }

    /// <summary>本次连接的流量计量与限额。</summary>
    public TrafficMeter Meter { get; }

    /// <summary>会话抬头（结束时补 EndedAt 再写一次）。</summary>
    public ChatSessionStore.ChatSessionInfo Info { get; }

    /// <summary>本次会话文件夹名（连接成功后才有值，例如 <c>2025.1.3-14.5.9</c>）。</summary>
    public string SessionName { get; protected set; } = "";

    /// <summary>会话文件夹完整路径（没建会话时是空串）。</summary>
    public string SessionDirectory => SessionName.Length > 0 ? Store.SessionDirectory(SessionName) : "";

    /// <summary>本端成员信息。</summary>
    public ChatMember Self { get; } = new();

    public string ToolId { get; }

    public string LocalName { get; }

    public string LocalEmoji { get; }

    /// <summary>当前成员名单（主机 = 自己 + 所有客户端；客户端 = 主机 + 自己 + 其它人）。</summary>
    public List<ChatMember> Members { get; } = new();

    /// <summary>本端成员 Id。</summary>
    public string LocalMemberId => Self.MemberId;

    protected CancellationToken Token => _cts.Token;

    private static string AppVersionText =>
        typeof(ChatRoomConnection).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    // ---------------------------------------------------------------- 事件（界面订阅）

    /// <summary>收到文字消息（已经自动落盘到聊天记录）。</summary>
    public event Action<TextInfo, string>? TextArrived;

    /// <summary>收到系统提示。</summary>
    public event Action<SystemInfo>? SystemArrived;

    /// <summary>成员名单变了。</summary>
    public event Action<IReadOnlyList<ChatMember>>? MembersChanged;

    /// <summary>要往界面上加一条消息（自己发的也走这里，界面逻辑就只有一条路）。</summary>
    public event Action<ChatMessageNotification>? MessagePosted;

    /// <summary>文件传输进度 / 状态变化。</summary>
    public event Action<TransferNotification>? TransferChanged;

    /// <summary>状态栏提示。</summary>
    public event Action<ChatNotice>? Noticed;

    /// <summary>连接结束了（<paramref name="error"/> 为空 = 正常退出）。</summary>
    public event Action<string?>? Disconnected;

    /// <summary>把一条消息交给界面（同时已经在 <see cref="Store"/> 里落盘了）。</summary>
    /// <param name="MessageId">消息 Id。</param>
    /// <param name="SenderName">发送方显示名。</param>
    /// <param name="SenderEmoji">发送方头像。</param>
    /// <param name="SenderId">发送方成员 Id。</param>
    /// <param name="IsHost">发送方是不是主机。</param>
    /// <param name="IsMine">是不是本机发的。</param>
    /// <param name="SentLocal">本地时间。</param>
    /// <param name="Text">文字内容。</param>
    /// <param name="ContentKind">文件大类（没有文件时忽略）。</param>
    /// <param name="File">文件进度对象（没有文件时为 null）。</param>
    /// <param name="RelativePath">相对会话文件夹的路径。</param>
    /// <param name="PreviewRelativePath">预览图相对路径。</param>
    /// <param name="LocalPath">本机完整路径。</param>
    /// <param name="PreviewPath">预览图完整路径。</param>
    /// <param name="IsSystem">是不是系统提示。</param>
    public readonly record struct ChatMessageNotification(
        string MessageId, string SenderName, string SenderEmoji, string SenderId, bool IsHost, bool IsMine,
        DateTime SentLocal, string Text, ChatContentKind ContentKind, FileProgress? File,
        string RelativePath, string? PreviewRelativePath, string? LocalPath, string? PreviewPath, bool IsSystem);

    /// <summary>文件传输的通知（界面据此更新进度条 / 加一条消息）。</summary>
    public readonly record struct TransferNotification(
        string TransferId, string FileName, ChatContentKind Kind, long SizeBytes, bool Outgoing,
        FileProgress Progress, string RelativePath, string? LocalPath, string? PreviewPath, string? PreviewRelativePath,
        ChatMessageItemPayload? NewMessage, string? Error);

    /// <summary>新建文件消息时需要的字段（发送 / 接收两个方向共用）。</summary>
    public readonly record struct ChatMessageItemPayload(
        string MessageId, string SenderName, string SenderEmoji, string SenderId, bool IsHost, bool IsMine,
        DateTime SentLocal, string Text);

    /// <summary>
    /// 把成员名单 / 状态提示转发给界面。
    /// （C# 的事件只能在<b>声明它的那个类</b>里触发，所以这里给子类留一对转发方法。）
    /// </summary>
    protected void RaiseNoticed(ChatNotice notice) => Noticed?.Invoke(notice);

    /// <summary>触发 <see cref="MembersChanged"/>（子类与主机 / 客户端转发用）。</summary>
    internal void RaiseMembersChanged() => MembersChanged?.Invoke(Members.ToList());

    /// <summary>发一条状态提示（界面 + 聊天记录都会留痕）。</summary>
    protected void Notice(ChatNoticeLevel level, string text)
    {
        Noticed?.Invoke(new ChatNotice(level, text));
        if (SessionName.Length > 0) Store.AppendSystemLine(SessionName, text);
    }

    /// <summary>把 <see cref="ChatMember"/> 转成界面用的发送者字段。</summary>
    protected static (string Name, string Emoji, string Id, bool IsHost) Describe(ChatMember? member, string fallbackName, string fallbackEmoji, string fallbackId, bool fallbackHost)
        => member is null
            ? (fallbackName, fallbackEmoji, fallbackId, fallbackHost)
            : (member.Name, member.Emoji, member.MemberId, member.IsHost);

    /// <summary>本端成员 Id / 名字写入 <see cref="Self"/>。</summary>
    protected void InitSelf(string memberId, string role)
    {
        Self.MemberId = memberId;
        Self.Name = LocalName;
        Self.Emoji = LocalEmoji;
        Self.Role = role;
        Self.JoinedAt = DateTime.Now;
        Info.MemberId = memberId;
        Info.Role = role;
        Info.HostName = LocalName;
    }

    /// <summary>客户端采用主机分配的成员 Id（同名 / 同机多开也不会混）。</summary>
    internal void AdoptHostMemberId(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId)) return;
        Self.MemberId = memberId;
        Info.MemberId = memberId;
    }

    /// <summary>
    /// 建立本次会话的数据文件夹（<b>每次开始连接都要新建一个</b>）。
    /// 会话名 = 开始连接的时刻，格式 <c>年份.月份.日期-时间.分钟.秒</c>。
    /// </summary>
    protected string BeginSession(string role, string peerText, DateTime? startedAt = null)
    {
        Info.Peer = peerText;
        Info.TrafficLimitBytes = Meter.LimitBytes;
        SessionName = Store.CreateSession(Info, startedAt);
        Notice(ChatNoticeLevel.Info, $"本次记录文件夹：{SessionDirectory}");
        return SessionName;
    }

    /// <summary>往聊天记录里追加一条（消息本身已经/即将交给界面）。</summary>
    protected void LogMessage(ChatSessionStore.ChatLogEntry entry)
    {
        if (SessionName.Length == 0) return;
        Store.AppendLog(SessionName, entry);
        Info.MessageCount++;
    }

    /// <summary>记一条“本机发出的文字”。</summary>
    protected ChatLogEntry BuildTextLog(string messageId, string text, DateTime when, bool mine, string sender)
        => new()
        {
            MessageId = messageId,
            Kind = string.IsNullOrEmpty(sender) ? "system" : "text",
            Sender = sender,
            SentLocal = when,
            Text = text,
            Direction = mine ? "out" : "in",
        };

    /// <summary>新的消息 Id。</summary>
    protected string NextMessageId() => $"{DateTime.Now:HHmmssfff}-{Interlocked.Increment(ref _messageCounter):D4}";

    // ---------------------------------------------------------------- 文件传输

    /// <summary>缩略图长边上限（0 = 不生成预览图，界面上直接加载原图）。</summary>
    public int ThumbnailMaxEdge { get; set; } = 1600;

    /// <summary>缩略图 JPEG 质量（30~100）。</summary>
    public int ThumbnailQuality { get; set; } = 82;

    /// <summary>
    /// 把一个本地文件发给对端：① 先落一份到本端会话文件夹（账本要完整）
    /// ② 交给界面显示进度 ③ 真的推过去 ④ 等回执更新状态。
    /// </summary>
    /// <param name="link">发给谁（主机 = 广播给所有人；客户端 = 那唯一一条连接）。</param>
    /// <param name="sourcePath">本机上的文件。</param>
    /// <param name="note">附带的留言（可空）。</param>
    /// <param name="forcedName">想换个文件名发（可空 = 用原文件名）。</param>
    public async Task SendFileToAsync(ChatLink link, string sourcePath, string note = "", string? forcedName = null)
    {
        var fileName = ChatSessionNaming.SafeFileName(forcedName ?? Path.GetFileName(sourcePath));
        var kind = ChatSessionNaming.KindOfFile(fileName);
        var messageId = NextMessageId();
        var when = DateTime.Now;

        long size;
        try { size = new FileInfo(sourcePath).Length; }
        catch (Exception ex)
        {
            Notice(ChatNoticeLevel.Error, $"读不到文件：{ex.Message}");
            return;
        }

        // ① 单次流量上限的预检：额度不够就根本不开传（传一半被掐更难受）
        var verdict = Meter.Check(size);
        if (!verdict.Allowed)
        {
            BlockTransfer(fileName, kind, size, verdict.Reason);
            return;
        }

        var progress = new FileProgress(Guid.NewGuid().ToString("N")[..12], fileName, size,
            ChatSessionNaming.LabelOf(kind), true);

        // ② 本端会话文件夹里也留一份（两个方向都存，回看时才是完整的）
        var relative = "";
        var localFull = sourcePath;
        try
        {
            relative = Store.ImportFile(SessionName, sourcePath, kind, fileName) ?? "";
            if (relative.Length > 0) localFull = Store.FullPath(SessionName, relative);
        }
        catch { /* 落盘失败不影响发送 */ }

        // 图片在本端也生成一张缩略图（纯本地，不花流量）
        var thumbRelative = CreateLocalThumbnail(relative, kind);

        // ③ 先让界面把这条消息显示出来（进度从「排队中」开始动）
        TransferChanged?.Invoke(new TransferNotification(
            progress.TransferId, fileName, kind, size, true, progress, relative, localFull,
            null, null,
            new ChatMessageItemPayload(messageId, LocalName, LocalEmoji, Self.MemberId, Self.IsHost, true, when, note),
            null));

        var log = new ChatLogEntry
        {
            MessageId = messageId,
            Kind = "file",
            Sender = LocalName,
            SentLocal = when,
            Text = note.Length > 0 ? note : null,
            FileName = fileName,
            FileSize = size,
            ContentKind = kind.ToString().ToLowerInvariant(),
            RelativePath = relative.Length > 0 ? relative : null,
            PreviewPath = thumbRelative,
            Direction = "out",
        };

        await _transferGate.WaitAsync(Token).ConfigureAwait(false);
        try
        {
            progress.State = TransferState.Sending;
            var info = new TransferInfo
            {
                TransferId = progress.TransferId,
                FileName = fileName,
                SizeBytes = size,
                ContentKind = kind.ToString().ToLowerInvariant(),
                KindLabel = ChatSessionNaming.LabelOf(kind),
                MessageId = messageId,
                Note = note.Length > 0 ? note : null,
                SentAtUtc = DateTime.UtcNow,
                SentLocal = when,
                Crc32 = 0,
            };

            var result = await link.SendFileAsync(info, sourcePath,
                sent => progress.TransferredBytes = sent, Token).ConfigureAwait(false);

            if (result is null)
            {
                progress.State = TransferState.Failed;
                progress.Message = "没等到对方的回执（连接可能断了）";
            }
            else if (result.Ok)
            {
                progress.State = TransferState.Done;
                progress.TransferredBytes = size;
                progress.Message = result.SavedAs is { Length: > 0 } saved ? $"对方存为 {saved}" : "对方已收到";
                Info.SentFileCount++;
            }
            else
            {
                progress.State = TransferState.Failed;
                progress.Message = result.Error ?? "对方接收失败";
            }
        }
        catch (OperationCanceledException)
        {
            progress.State = TransferState.Cancelled;
            progress.Message = "已取消";
        }
        catch (Exception ex)
        {
            progress.State = TransferState.Failed;
            progress.Message = ChatLink.Describe(ex);
            Notice(ChatNoticeLevel.Error, $"发送「{fileName}」失败：{progress.Message}");
        }
        finally { _transferGate.Release(); }

        LogMessage(log);

        TransferChanged?.Invoke(new TransferNotification(
            progress.TransferId, fileName, kind, size, true, progress, relative, localFull,
            null, null, null,
            progress.State is TransferState.Failed or TransferState.Cancelled ? progress.Message : null));
    }

    /// <summary>被流量上限拦下时的统一处理（提示 + 界面留一条“被拦下”的记录）。</summary>
    private void BlockTransfer(string fileName, ChatContentKind kind, long size, string reason)
    {
        Notice(ChatNoticeLevel.Warn, $"「{fileName}」没有发出：{reason}");
        TransferChanged?.Invoke(new TransferNotification(
            Guid.NewGuid().ToString("N")[..12], fileName, kind, size, true,
            new FileProgress("", fileName, size, ChatSessionNaming.LabelOf(kind), true)
            { State = TransferState.Blocked, Message = reason },
            "", null, null, null, null, reason));
        if (SessionName.Length > 0)
            Store.AppendSystemLine(SessionName,
                $"被流量上限拦下，未发送：{fileName}（{FileSize.Format(size)}）—— {reason}");
        CheckTrafficExhausted();
    }

    /// <summary>收到对方推过来的文件：先落盘（边收边算 CRC）→ 回执 → 交给界面。</summary>
    public async Task ReceiveFileAsync(ChatLink link, TransferInfo info, CancellationToken ct)
    {
        var kind = Enum.TryParse<ChatContentKind>(info.ContentKind, ignoreCase: true, out var parsed)
            ? parsed
            : ChatSessionNaming.KindOfFile(info.FileName);
        var safeName = ChatSessionNaming.SafeFileName(info.FileName);
        var progress = new FileProgress(info.TransferId, safeName, info.SizeBytes,
            ChatSessionNaming.LabelOf(kind), false) { State = TransferState.Receiving };

        // 接收也算流量（用户设的是「这次连接总共能跑多少」）
        var verdict = Meter.Check(info.SizeBytes);
        if (!verdict.Allowed)
        {
            // 拒收也必须把对方正在灌的字节读干净，否则这条连接后面的包全乱
            await link.SkipRawBytesAsync(info.SizeBytes, ct).ConfigureAwait(false);
            await link.SendTransferResultAsync(new TransferResultInfo
            {
                TransferId = info.TransferId,
                Ok = false,
                Error = $"对方停止接收：{verdict.Reason}",
            }, ct).ConfigureAwait(false);
            Notice(ChatNoticeLevel.Warn, $"拒收「{safeName}」：{verdict.Reason}");
            CheckTrafficExhausted();
            return;
        }

        var relative = "";
        var full = "";
        uint crc = 0;
        try
        {
            relative = Store.MakeRelativeFilePath(SessionName, kind, safeName);
            full = Store.FullPath(SessionName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            // 杈规敹杈圭畻 CRC锛歅umpRawBytesAsync 姣忔鐏岀洏鐨勫瓧鑺傞兘瑕佸杺缁?CRC锛屽惁鍒欏涓嶄笂
            var crcState = Crc32.Begin;
            await using (var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             256 * 1024, useAsync: true))
            {
                await link.PumpRawBytesAsync(info.SizeBytes, file,
                    received => progress.TransferredBytes = received, ct, chunk => crcState = Crc32.Append(crcState, chunk))
                    .ConfigureAwait(false);
            }
            crc = Crc32.Finish(crcState);

            await link.SendTransferResultAsync(new TransferResultInfo
            {
                TransferId = info.TransferId,
                Ok = true,
                SavedAs = relative,
                Crc32 = crc,
            }, ct).ConfigureAwait(false);

            progress.State = TransferState.Done;
            progress.TransferredBytes = info.SizeBytes;
            progress.Message = $"已存到 {relative}";
            Info.ReceivedFileCount++;

            var sender = link.Peer ?? new ChatMember { Name = "对方", Emoji = "👤" };
            TransferChanged?.Invoke(new TransferNotification(
                info.TransferId, safeName, kind, info.SizeBytes, false, progress, relative, full,
                null, null,
                new ChatMessageItemPayload(info.MessageId, sender.Name, sender.Emoji, sender.MemberId, sender.IsHost,
                    false, info.SentLocal, info.Note ?? ""),
                null));

            var thumbRelative = CreateLocalThumbnail(relative, kind);
            LogMessage(new ChatLogEntry
            {
                MessageId = info.MessageId,
                Kind = "file",
                Sender = sender.Name,
                SentLocal = info.SentLocal,
                Text = info.Note,
                FileName = safeName,
                FileSize = info.SizeBytes,
                ContentKind = kind.ToString().ToLowerInvariant(),
                RelativePath = relative,
                PreviewPath = thumbRelative,
                Direction = "in",
                Crc32 = crc.ToString("x8"),
            });
        }
        catch (OperationCanceledException)
        {
            progress.State = TransferState.Cancelled;
            progress.Message = "连接中断";
            CleanupPartial(full);
        }
        catch (Exception ex)
        {
            progress.State = TransferState.Failed;
            progress.Message = ChatLink.Describe(ex);
            Notice(ChatNoticeLevel.Error, $"接收「{safeName}」失败：{progress.Message}");
            CleanupPartial(full);
            try
            {
                await link.SendTransferResultAsync(new TransferResultInfo
                {
                    TransferId = info.TransferId,
                    Ok = false,
                    Error = progress.Message,
                }, ct).ConfigureAwait(false);
            }
            catch { /* 连接可能已经断了 */ }
        }
        finally { CheckTrafficExhausted(); }
    }

    /// <summary>收了一半失败时把半成品删掉（不留一个看着像完整的坏文件）。</summary>
    private static void CleanupPartial(string fullPath)
    {
        try { if (fullPath.Length > 0 && File.Exists(fullPath)) File.Delete(fullPath); } catch { }
    }

    /// <summary>给会话文件夹里的一张图生成缩略图（纯本地）；不是图片 / 关了预览就返回 null。</summary>
    private string? CreateLocalThumbnail(string relativePath, ChatContentKind kind)
    {
        if (ThumbnailMaxEdge <= 0 || kind != ChatContentKind.Image || relativePath.Length == 0) return null;
        try
        {
            var source = Store.FullPath(SessionName, relativePath);
            if (!File.Exists(source)) return null;
            var thumbRelative = Path.Combine(ChatSessionNaming.FolderOf(kind),
                Path.GetFileNameWithoutExtension(relativePath) + "_预览.jpg");
            var thumbFull = Store.FullPath(SessionName, thumbRelative);
            return ImageUtil.CreateThumbnail(source, thumbFull, ThumbnailMaxEdge, ThumbnailQuality) is null
                ? null
                : thumbRelative;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- 自己发出的文字 / 系统消息

    /// <summary>发一条文字（自己发的也要落盘 + 交给界面，<paramref name="deliver"/> 负责真的送出去）。</summary>
    internal async Task SendLocalTextAsync(string text, Func<TextInfo, string, Task> deliver)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        var bytes = Encoding.UTF8.GetByteCount(text);
        var verdict = Meter.Check(bytes);
        if (!verdict.Allowed)
        {
            Notice(ChatNoticeLevel.Warn, $"文字没有发出：{verdict.Reason}");
            CheckTrafficExhausted();
            return;
        }

        var info = new TextInfo
        {
            MessageId = NextMessageId(),
            SentAtUtc = DateTime.UtcNow,
            SentLocal = DateTime.Now,
            Member = Self.Clone(),
        };

        LogMessage(new ChatLogEntry
        {
            MessageId = info.MessageId,
            Kind = "text",
            Sender = LocalName,
            SentLocal = info.SentLocal,
            Text = text,
            Direction = "out",
        });

        MessagePosted?.Invoke(new ChatMessageNotification(
            info.MessageId, LocalName, LocalEmoji, Self.MemberId, Self.IsHost, true,
            info.SentLocal, text, ChatContentKind.File, null, "", null, null, null, false));

        await deliver(info, text).ConfigureAwait(false);
        CheckTrafficExhausted();
    }

    /// <summary>自己产生一条系统提示（本端界面 + 记录都留痕，不发给对方）。</summary>
    internal void PostLocalSystem(string text)
    {
        var when = DateTime.Now;
        if (SessionName.Length > 0) Store.AppendSystemLine(SessionName, text);
        MessagePosted?.Invoke(new ChatMessageNotification(
            Guid.NewGuid().ToString("N")[..12], "系统", "⚙", "", false, false,
            when, text, ChatContentKind.File, null, "", null, null, null, true));
    }

    /// <summary>收到对方的文字消息：落盘 + 交给界面（子类不用重复这段）。</summary>
    internal void HandleIncomingText(ChatLink link, TextInfo info, string text)
    {
        var member = info.Member ?? link.Peer;
        var (name, emoji, id, isHost) = Describe(member, "对方", "👤", link.PeerId, false);

        LogMessage(new ChatLogEntry
        {
            MessageId = info.MessageId,
            Kind = "text",
            Sender = name,
            SentLocal = info.SentLocal == default ? DateTime.Now : info.SentLocal,
            Text = text,
            Direction = "in",
        });

        MessagePosted?.Invoke(new ChatMessageNotification(
            info.MessageId, name, emoji, id, isHost, false,
            info.SentLocal == default ? DateTime.Now : info.SentLocal,
            text, ChatContentKind.File, null, "", null, null, null, false));

        TextArrived?.Invoke(info, text);
    }

    /// <summary>收到对方的系统提示：落盘 + 交给界面。</summary>
    internal void HandleIncomingSystem(ChatLink link, SystemInfo info)
    {
        var member = info.Member ?? link.Peer;
        var name = member?.Name ?? "系统";
        var when = info.SentLocal == default ? DateTime.Now : info.SentLocal;
        if (SessionName.Length > 0) Store.AppendSystemLine(SessionName, $"{name}：{info.Text}");
        MessagePosted?.Invoke(new ChatMessageNotification(
            info.MessageId, name, member?.Emoji ?? "⚙", member?.MemberId ?? link.PeerId, member?.IsHost ?? false,
            false, when, info.Text, ChatContentKind.File, null, "", null, null, null, true));
        SystemArrived?.Invoke(info);
    }


    /// <summary>结束会话：补上结束时间与统计，落盘抬头。</summary>
    protected void EndSession(string? error)
    {
        if (SessionName.Length == 0) return;
        var snapshot = Meter.Snapshot();
        Info.EndedAt = DateTime.Now;
        Info.SentBytes = snapshot.SentBytes;
        Info.ReceivedBytes = snapshot.ReceivedBytes;
        Info.TrafficLimitBytes = Meter.LimitBytes;
        Store.SaveInfo(Info);
        Store.AppendSystemLine(SessionName,
            error is { Length: > 0 }
                ? $"会话结束（{error}）。共 {Info.MessageCount} 条消息，收 {FileSize.Format(Info.ReceivedBytes)} / 发 {FileSize.Format(Info.SentBytes)}"
                : $"会话正常结束。共 {Info.MessageCount} 条消息，收 {FileSize.Format(Info.ReceivedBytes)} / 发 {FileSize.Format(Info.SentBytes)}");
        Disconnected?.Invoke(error);
    }

    /// <summary>流量到顶时提醒一次（双方各自提醒自己的用户）。</summary>
    protected void CheckTrafficExhausted()
    {
        if (!Meter.ShouldNotifyExhausted()) return;
        var snapshot = Meter.Snapshot();
        Notice(ChatNoticeLevel.Warn,
            $"本次流量已到上限（{FileSize.Format(snapshot.LimitBytes)}），已停止发送；可以在「流量」里调大上限或重连");
    }

    /// <summary>把成员信息序列化成 JSON（写进 session.json 用）。</summary>
    protected string MembersJson() => JsonSerializer.Serialize(Members, ChatProtocol.JsonOptions);

    public virtual async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        await Task.CompletedTask.ConfigureAwait(false);
        _cts.Dispose();
        _transferGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
