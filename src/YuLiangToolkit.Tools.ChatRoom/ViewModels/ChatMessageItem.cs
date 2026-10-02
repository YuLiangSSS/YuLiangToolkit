using System.IO;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ChatRoom.Protocol;
using YuLiangToolkit.Tools.ChatRoom.Sessions;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>文件传输的状态（界面上的进度条 / 结果文案）。</summary>
public enum TransferState
{
    /// <summary>排队中（同一时刻只传一个，避免多条数据连接抢带宽）。</summary>
    Queued = 0,
    Sending = 1,
    Receiving = 2,
    Done = 3,
    Failed = 4,
    Cancelled = 5,
    /// <summary>被单次流量上限挡住（还没开始传就拒了）。</summary>
    Blocked = 6,
}

/// <summary>
/// 文件传输的进度（发送端与接收端共用同一个对象，界面绑它）。
/// <b>关键点</b>：进度是“本次连接”的实时数据，不是历史 —— 从磁盘读回历史记录时这里全是 0/已完成。
/// </summary>
public sealed class FileProgress : ObservableObject
{
    private long _transferred;
    private double _speed;
    private TransferState _state = TransferState.Queued;
    private string _message = "";

    public FileProgress(string transferId, string fileName, long totalBytes, string kindLabel, bool outgoing)
    {
        TransferId = transferId;
        FileName = fileName;
        TotalBytes = totalBytes;
        KindLabel = kindLabel;
        Outgoing = outgoing;
    }

    public string TransferId { get; }

    public string FileName { get; }

    public long TotalBytes { get; }

    public string KindLabel { get; }

    /// <summary>true = 本机发出去的，false = 收到的。</summary>
    public bool Outgoing { get; }

    public TransferState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    /// <summary>已传输字节。</summary>
    public long TransferredBytes
    {
        get => _transferred;
        set
        {
            if (!Set(ref _transferred, value)) return;
            OnPropertyChanged(nameof(Ratio));
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    /// <summary>当前速度（字节/秒）。</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (!Set(ref _speed, value)) return;
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    /// <summary>附加说明（失败原因 / "已存到 images\xxx.png"）。</summary>
    public string Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    public double Ratio => TotalBytes <= 0 ? 0 : Math.Clamp((double)TransferredBytes / TotalBytes, 0, 1);

    public bool IsActive => State is TransferState.Sending or TransferState.Receiving or TransferState.Queued;

    public bool IsDone => State == TransferState.Done;

    public bool IsFailed => State is TransferState.Failed or TransferState.Cancelled or TransferState.Blocked;

    public string StateText => State switch
    {
        TransferState.Queued => "排队中",
        TransferState.Sending => "发送中",
        TransferState.Receiving => "接收中",
        TransferState.Done => Outgoing ? "已发出" : "已保存",
        TransferState.Failed => "失败",
        TransferState.Cancelled => "已取消",
        TransferState.Blocked => "被流量上限拦下",
        _ => "",
    };

    public string ProgressText
    {
        get
        {
            var text = $"{FileSize.Format(TransferredBytes)} / {FileSize.Format(TotalBytes)}";
            if (State is TransferState.Sending or TransferState.Receiving && Speed > 1)
                text += $"　{FileSize.Format((long)Speed)}/s";
            if (Message.Length > 0) text += $"　{Message}";
            return text;
        }
    }

    public string DirectionText => Outgoing ? "发送" : "接收";

    /// <summary>列表里的一行。</summary>
    public string Describe() => $"[{KindLabel}] {FileName}　{FileSize.Format(TotalBytes)}　{StateText}　{ProgressText}";
}

/// <summary>
/// 聊天界面里的一条消息（文字 / 系统提示 / 文件）。
///
/// <para>对齐方式：自己发的靠右（<see cref="IsMine"/>），别人的靠左；系统提示居中。
/// 图片 / 音频 / 视频各自有一个 <c>Has*</c> 开关，XAML 里各挂一个模板，不用的那块不占地方。</para>
/// </summary>
public sealed class ChatMessageItem : ObservableObject
{
    private string _text = "";
    private string? _localPath;
    private string? _previewPath;
    private bool _isUnread;

    private ChatMessageItem() { }

    /// <summary>消息 Id（同一条消息的文字 / 预览图共享它）。</summary>
    public string MessageId { get; init; } = "";

    /// <summary>发送方成员 Id（自己发的就是本端 Id）。</summary>
    public string SenderId { get; init; } = "";

    public string SenderName { get; init; } = "";

    public string SenderEmoji { get; init; } = "🙂";

    public bool IsHost { get; init; }

    /// <summary>自己发的（界面上靠右、蓝色气泡）。</summary>
    public bool IsMine { get; init; }

    /// <summary>系统提示（居中灰字，不带头像）。</summary>
    public bool IsSystem { get; set; }

    public bool IsFile => File is not null;

    public DateTime SentAt { get; init; } = DateTime.Now;

    public string TimeText => SentAt.ToString("HH:mm:ss");

    public string SenderLine => IsSystem ? "" : $"{SenderEmoji} {SenderName}{(IsHost ? "（主机）" : "")}";

    /// <summary>文字内容（文件消息里是留言，可以是空）。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            OnPropertyChanged(nameof(HasText));
        }
    }

    public bool HasText => Text.Length > 0;

    /// <summary>文件信息（<see cref="IsFile"/> 为 false 时是 null）。</summary>
    public FileProgress? File { get; init; }

    public ChatContentKind ContentKind { get; init; } = ChatContentKind.File;

    public string KindLabel => ChatSessionNaming.LabelOf(ContentKind);

    /// <summary>本机磁盘上的完整路径（收到的存在会话文件夹里，发出的指向原文件）。</summary>
    public string? LocalPath
    {
        get => _localPath;
        set
        {
            if (!Set(ref _localPath, value)) return;
            OnPropertyChanged(nameof(CanOpenFile));
        }
    }

    /// <summary>自动压缩的预览图路径（大图不直接加载原图，省内存）。</summary>
    public string? PreviewPath
    {
        get => _previewPath;
        set
        {
            if (!Set(ref _previewPath, value)) return;
            OnPropertyChanged(nameof(ThumbnailPath));
        }
    }

    /// <summary>界面优先用预览图。</summary>
    public string? ThumbnailPath => PreviewPath ?? LocalPath;

    /// <summary>相对会话文件夹的路径（写进聊天记录，方便回头找）。</summary>
    public string RelativePath { get; init; } = "";

    public string? PreviewRelativePath { get; init; }

    /// <summary>文件下载 / 保存完了没有（决定「打开 / 打开所在文件夹」能不能点）。</summary>
    public bool CanOpenFile => LocalPath is { Length: > 0 } && System.IO.File.Exists(LocalPath);

    public bool HasImage => ContentKind == ChatContentKind.Image && ThumbnailPath is { Length: > 0 };

    public bool HasAudio => ContentKind == ChatContentKind.Audio && CanOpenFile;

    public bool HasVideo => ContentKind == ChatContentKind.Video && CanOpenFile;

    /// <summary>本条消息是不是“图片预览”（true 时界面上不再单列文件行）。</summary>
    public bool IsPreviewFile { get; init; }

    /// <summary>收到的、还没看过的消息（用来标新消息）。</summary>
    public bool IsUnread
    {
        get => _isUnread;
        set => Set(ref _isUnread, value);
    }

    /// <summary>文件那一行的说明文字。</summary>
    public string FileSummary
    {
        get
        {
            if (File is null) return "";
            var size = FileSize.Format(File.TotalBytes);
            var path = RelativePath.Length > 0 ? $"　→　{RelativePath}" : "";
            return $"{KindLabel}　{size}{path}";
        }
    }

    /// <summary>从系统提示构造。</summary>
    public static ChatMessageItem FromSystem(string text, DateTime when, string sender = "")
        => new()
        {
            MessageId = Guid.NewGuid().ToString("N")[..12],
            IsSystem = true,
            SenderName = sender.Length > 0 ? sender : "系统",
            SentAt = when,
            Text = text,
        };

    /// <summary>构造一条消息（文字 / 文件共用）。</summary>
    public static ChatMessageItem Create(
        string messageId, string senderId, string senderName, string senderEmoji, bool isHost, bool isMine,
        DateTime sentAt, string text, ChatContentKind kind, FileProgress? file, bool isPreviewFile = false)
        => new()
        {
            MessageId = messageId,
            SenderId = senderId,
            SenderName = senderName,
            SenderEmoji = senderEmoji,
            IsHost = isHost,
            IsMine = isMine,
            SentAt = sentAt,
            Text = text,
            ContentKind = kind,
            File = file,
            IsPreviewFile = isPreviewFile,
        };

    /// <summary>把一条消息还原成聊天记录里的一行。</summary>
    public ChatSessionStore.ChatLogEntry ToLogEntry(string? crc32 = null) => new()
    {
        MessageId = MessageId,
        Kind = IsSystem ? "system" : IsFile ? "file" : "text",
        Sender = SenderName,
        SentLocal = SentAt,
        Text = Text.Length > 0 ? Text : null,
        FileName = File?.FileName,
        FileSize = File?.TotalBytes ?? 0,
        ContentKind = IsFile ? ContentKind.ToString().ToLowerInvariant() : null,
        RelativePath = RelativePath.Length > 0 ? RelativePath : null,
        PreviewPath = PreviewRelativePath,
        Direction = IsMine ? "out" : "in",
        Crc32 = crc32,
    };

    /// <summary>把聊天记录里的一行还原成界面消息（历史回看用，进度相关的字段都是已完成状态）。</summary>
    public static ChatMessageItem FromLogEntry(ChatSessionStore.ChatLogEntry entry, string selfName)
    {
        var isMine = string.Equals(entry.Direction, "out", StringComparison.OrdinalIgnoreCase);
        var kind = Enum.TryParse<ChatContentKind>(entry.ContentKind, ignoreCase: true, out var parsed)
            ? parsed
            : ChatSessionNaming.KindOfFile(entry.FileName);

        var item = new ChatMessageItem
        {
            MessageId = entry.MessageId,
            SenderId = isMine ? "self" : "peer",
            SenderName = entry.Sender,
            SenderEmoji = isMine ? "🙂" : "👤",
            IsMine = isMine,
            IsSystem = string.Equals(entry.Kind, "system", StringComparison.OrdinalIgnoreCase),
            SentAt = entry.SentLocal,
            Text = entry.Text ?? "",
            ContentKind = kind,
            RelativePath = entry.RelativePath ?? "",
            PreviewRelativePath = entry.PreviewPath,
            File = entry.FileName is { Length: > 0 }
                ? new FileProgress(entry.MessageId, entry.FileName, entry.FileSize, ChatSessionNaming.LabelOf(kind), isMine)
                { State = TransferState.Done }
                : null,
        };
        _ = selfName;
        return item;
    }
}
