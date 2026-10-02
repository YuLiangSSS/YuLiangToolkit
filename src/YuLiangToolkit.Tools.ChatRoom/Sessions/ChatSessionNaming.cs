using System.Text;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.ChatRoom.Sessions;

/// <summary>文件大类（决定落在会话文件夹的哪个子目录、界面上怎么预览）。</summary>
public enum ChatContentKind
{
    File = 0,
    Image = 1,
    Audio = 2,
    Video = 3,
    Archive = 4,
    Doc = 5,
}

/// <summary>
/// 会话文件夹的命名与结构约定（<b>纯字符串逻辑</b>，自检直接断言）。
///
/// <para>文件夹名格式：<c>年份.月份.日期-时间.分钟.秒</c>，例如 <c>2025.1.3-14.5.9</c>。
/// 故意**不补前导零**（月/日/时/分/秒都按自然数字写）——这是需求里指定的格式，
/// 所以同一天里 <c>…-9.5.1</c> 会排在 <c>…-14.5.9</c> 后面，纯粹按名字排序不等于按时间排序；
/// 要按时间排序请用文件夹的创建时间（界面上「历史会话」就是按时间排的）。</para>
/// </summary>
public static class ChatSessionNaming
{
    /// <summary>会话文件夹根目录下放全部内容的子目录。</summary>
    public static readonly string[] CategoryFolders = { "images", "audio", "video", "files" };

    /// <summary>
    /// 所有会话文件夹都放在这个子目录下（<c>Data\chat-room\Sessions\2025.1.3-14.5.9\</c>）。
    ///
    /// <para>为什么不直接摊在 <c>Data\chat-room\</c> 下：那个目录里还有协议说明、旧版本可能留下的东西，
    /// 「历史记录」列表会把它们当成一次聊天列出来（踩过：列表里出现一个叫 <c>Protocol</c> 的“会话”）。</para>
    /// </summary>
    public const string SessionsFolderName = "Sessions";

    /// <summary>会话索引 / 说明文件（都在会话文件夹根下）。</summary>
    public const string LogFileName = "chat-log.jsonl";
    public const string InfoFileName = "session.json";
    public const string ReadmeFileName = "怎么用这个文件夹.md";

    /// <summary>把时间格式化成会话文件夹名：<c>年份.月份.日期-时间.分钟.秒</c>。</summary>
    public static string FolderName(DateTime time)
        => $"{time.Year}.{time.Month}.{time.Day}-{time.Hour}.{time.Minute}.{time.Second}";

    /// <summary>反过来解析（认得出来就返回时间，认不出返回 null）。</summary>
    public static DateTime? ParseFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = name.Trim();

        // 同一秒开两次时名字是 "<时间>-2" / "-3"…：先把序号剪掉再解析
        var parts = text.Split('-');
        if (parts.Length is < 2 or > 3) return null;
        if (parts.Length == 3 && !int.TryParse(parts[2], out _)) return null;

        var date = parts[0].Split('.');
        var time = parts[1].Split('.');
        if (date.Length != 3 || time.Length != 3) return null;
        if (!int.TryParse(date[0], out var year) || !int.TryParse(date[1], out var month) || !int.TryParse(date[2], out var day))
            return null;
        if (!int.TryParse(time[0], out var hour) || !int.TryParse(time[1], out var minute) || !int.TryParse(time[2], out var second))
            return null;

        try { return new DateTime(year, month, day, hour, minute, second); }
        catch { return null; }
    }

    /// <summary>文件夹名是否合法（正则级别的检查，界面上挑历史会话时用）。</summary>
    public static bool IsValidFolderName(string? name) => ParseFolderName(name) is not null;

    /// <summary>文件大类 → 子目录名。</summary>
    public static string FolderOf(ChatContentKind kind) => kind switch
    {
        ChatContentKind.Image => "images",
        ChatContentKind.Audio => "audio",
        ChatContentKind.Video => "video",
        _ => "files",
    };

    /// <summary>文件大类 → 界面上显示的中文（"图片" / "视频"…）。</summary>
    public static string LabelOf(ChatContentKind kind) => kind switch
    {
        ChatContentKind.Image => "图片",
        ChatContentKind.Audio => "音频",
        ChatContentKind.Video => "视频",
        ChatContentKind.Archive => "压缩包",
        ChatContentKind.Doc => "文档",
        _ => "文件",
    };

    /// <summary>扩展名 → 大类（决定了它进哪个子目录、界面上给不给预览）。</summary>
    public static ChatContentKind KindOfExtension(string? extension)
    {
        var ext = (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "bmp" or "gif" or "webp" or "ico" or "tif" or "tiff" => ChatContentKind.Image,
            "mp3" or "wav" or "flac" or "aac" or "m4a" or "ogg" or "opus" or "wma" or "ape" => ChatContentKind.Audio,
            "mp4" or "mkv" or "avi" or "mov" or "wmv" or "flv" or "webm" or "m4v" or "ts" or "mpg" or "mpeg" or "rmvb" => ChatContentKind.Video,
            "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" => ChatContentKind.Archive,
            "txt" or "md" or "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx"
                or "json" or "xml" or "csv" or "ini" or "log" or "html" or "htm" or "cs" or "js" or "py" => ChatContentKind.Doc,
            _ => ChatContentKind.File,
        };
    }

    /// <summary>按文件名猜大类。</summary>
    public static ChatContentKind KindOfFile(string? fileName)
        => KindOfExtension(Path.GetExtension(fileName ?? ""));

    /// <summary>把任意名字洗成 Windows 能用的文件名（去掉非法字符与结尾的点/空格）。</summary>
    public static string SafeFileName(string? name, string fallback = "未命名")
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0) return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var safe = sb.ToString().Trim().TrimEnd('.', ' ');
        if (safe.Length == 0) return fallback;

        // Windows 的保留设备名，做文件名会直接失败
        var stem = Path.GetFileNameWithoutExtension(safe);
        string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;

        return safe.Length > 120 ? safe[..120] + Path.GetExtension(safe) : safe;
    }

    /// <summary>会话文件夹里那份说明文件的内容（让人在资源管理器里也看得懂）。</summary>
    public static string ReadmeText(string sessionName, string role, string serverText) => $"""
        # 这次聊天记录（{sessionName}）

        这是「联机聊天室」每次开始连接时自动新建的文件夹，名字就是开始连接的时刻：
        `年份.月份.日期-时间.分钟.秒`（例：2025.1.3-14.5.9，月/日/时/分/秒不补零）。

        本次身份：{role}
        对端地址：{(serverText.Length > 0 ? serverText : "（主机监听中）")}

        ## 里面有什么

        | 文件 / 文件夹 | 内容 |
        | --- | --- |
        | `chat-log.jsonl` | 全部消息，一行一条 JSON（文字、文件、系统提示都在里面，按时间追加） |
        | `session.json` | 本次会话抬头：开始/结束时间、身份、对端、消息条数与流量统计 |
        | `images/` | 收到和发出的图片（原图 + 自动压缩的预览图） |
        | `audio/` | 音频文件 |
        | `video/` | 视频文件 |
        | `files/` | 其它任何文件（压缩包、文档…） |

        重名文件会自动加序号（`照片 (1).png`），不会覆盖。
        流量用完时聊天室会拒绝继续发送并在这里记一条系统消息，但已有的记录都在。
        """;
}
