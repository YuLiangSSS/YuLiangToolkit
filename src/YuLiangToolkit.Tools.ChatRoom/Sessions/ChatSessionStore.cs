using System.Text;
using System.Text.Json;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ChatRoom.Protocol;

namespace YuLiangToolkit.Tools.ChatRoom.Sessions;

/// <summary>
/// 一次会话的数据文件夹（<c>Data\chat-room\年份.月份.日期-时间.分钟.秒\</c>）的文件门面。
///
/// <para><b>为什么独立成一层</b>：聊天室要落三种东西 —— 聊天记录（一行一条 JSON，只追加）、
/// 媒体文件（按大类分子目录）、会话抬头（<c>session.json</c>）。这三件事都只是文件 IO，
/// 和网络、界面毫无关系，拆出来就能被自检直接断言（真的建目录、真的读回来）。</para>
///
/// <para><b>根目录</b>默认是 <c>Data\chat-room\</c>；环境变量 <c>YLT_CHAT_HOME</c> 可以临时盖掉它
/// （自检 / 演示时把记录写到临时目录，不污染真实数据）。</para>
///
/// <para>所有 IO 都不抛异常：失败返回 null / 空列表，界面自己提示。聊天记录丢失不该让聊天崩掉。</para>
/// </summary>
public sealed class ChatSessionStore
{
    /// <summary>会话抬头文件名（会话文件夹根下）。</summary>
    public const string InfoFileName = ChatSessionNaming.InfoFileName;

    /// <summary>聊天记录文件名（会话文件夹根下，一行一条 JSON）。</summary>
    public const string LogFileName = ChatSessionNaming.LogFileName;

    /// <summary>会话根目录覆盖用的环境变量。</summary>
    public const string RootOverrideVariable = "YLT_CHAT_HOME";

    /// <summary>聊天记录每行的 JSON 选项：**故意关掉注释和尾随逗号**（宽容模式会把 <c>{ 这不是合法 JSON</c>
    /// 这种残缺行解析成一个空对象，白白多出一条“空消息”）。</summary>
    private static readonly JsonSerializerOptions LineOptions = new(ChatProtocol.JsonOptions)
    {
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public ChatSessionStore(string toolId)
    {
        ToolId = toolId;
    }

    /// <summary>功能 Id（决定默认会话根目录）。</summary>
    public string ToolId { get; }

    /// <summary>会话根目录（可能被 <c>YLT_CHAT_HOME</c> 覆盖）。</summary>
    public string RootDirectory
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(RootOverrideVariable);
            return string.IsNullOrWhiteSpace(custom)
                ? AppPaths.ToolDataDir(ToolId)
                : custom.Trim().Trim('"');
        }
    }

    /// <summary>所有会话文件夹所在目录（<c>&lt;根&gt;\Sessions</c>）。</summary>
    public string SessionsRoot => Path.Combine(RootDirectory, ChatSessionNaming.SessionsFolderName);

    /// <summary>某个会话的文件夹完整路径（只拼路径，不创建）。</summary>
    public string SessionDirectory(string sessionName)
        => Path.Combine(SessionsRoot, ChatSessionNaming.SafeFileName(sessionName, "会话"));

    /// <summary>某个会话里的聊天记录文件路径。</summary>
    public string LogPath(string sessionName) => Path.Combine(SessionDirectory(sessionName), LogFileName);

    /// <summary>某个会话里的抬头文件路径。</summary>
    public string InfoPath(string sessionName) => Path.Combine(SessionDirectory(sessionName), InfoFileName);

    /// <summary>
    /// 新建一次会话的文件夹（<b>每次开始连接时调用</b>）：建好根目录、各内容子目录、
    /// 一份说明文件与抬头文件。返回最终用到的会话名（同秒重复会自动补 <c>-2</c>、<c>-3</c>）。
    /// </summary>
    public string CreateSession(ChatSessionInfo info, DateTime? startTime = null)
    {
        var started = startTime ?? info.StartedAt;
        var baseName = ChatSessionNaming.FolderName(started);

        var name = baseName;
        for (var i = 2; i < 1000; i++)
        {
            if (!Directory.Exists(SessionDirectory(name))) break;
            name = $"{baseName}-{i}";
        }

        info.SessionName = name;
        info.StartedAt = started;

        try
        {
            var dir = SessionDirectory(name);
            Directory.CreateDirectory(dir);
            foreach (var sub in ChatSessionNaming.CategoryFolders)
                Directory.CreateDirectory(Path.Combine(dir, sub));

            File.WriteAllText(Path.Combine(dir, ChatSessionNaming.ReadmeFileName),
                ChatSessionNaming.ReadmeText(name, info.RoleText, info.PeerText), new UTF8Encoding(false));

            SaveInfo(info);
            AppendSystemLine(name, $"会话开始（{info.RoleText}）。记录文件夹：{dir}");
        }
        catch { /* 只读环境：聊天照常，只是没有记录 */ }

        return name;
    }

    /// <summary>写会话抬头（覆盖写）。</summary>
    public bool SaveInfo(ChatSessionInfo info)
    {
        try
        {
            Directory.CreateDirectory(SessionDirectory(info.SessionName));
            File.WriteAllText(InfoPath(info.SessionName),
                JsonSerializer.Serialize(info, new JsonSerializerOptions(ChatProtocol.JsonOptions) { WriteIndented = true }),
                new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>读会话抬头；读不到返回 null。</summary>
    public ChatSessionInfo? LoadInfo(string sessionName)
    {
        try
        {
            var path = InfoPath(sessionName);
            return File.Exists(path) ? JsonSerializer.Deserialize<ChatSessionInfo>(File.ReadAllText(path), ChatProtocol.JsonOptions) : null;
        }
        catch { return null; }
    }

    /// <summary>枚举历史会话（按开始时间倒序；没抬头文件的也能认出来）。</summary>
    public IReadOnlyList<ChatSessionInfo> ListSessions()
    {
        var result = new List<ChatSessionInfo>();
        try
        {
            var root = SessionsRoot;
            if (!Directory.Exists(root)) return result;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                var info = LoadInfo(name) ?? new ChatSessionInfo
                {
                    SessionName = name,
                    StartedAt = ChatSessionNaming.ParseFolderName(name) ?? Directory.GetCreationTime(dir),
                };
                info.SessionName = name;
                info.DirectoryPath = dir;
                result.Add(info);
            }
        }
        catch { /* 目录读不了就当没有 */ }

        return result.OrderByDescending(s => s.StartedAt).ToList();
    }

    /// <summary>
    /// 把一个（可能是别的盘 / 别的目录的）文件写进会话文件夹的对应子目录。
    /// 重名自动加序号，<b>绝不覆盖</b>。返回相对会话文件夹的路径（例如 <c>images\照片.png</c>）。
    /// </summary>
    public string? ImportFile(string sessionName, string sourcePath, ChatContentKind kind, string? displayName = null)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;
            var dir = Path.Combine(SessionDirectory(sessionName), ChatSessionNaming.FolderOf(kind));
            Directory.CreateDirectory(dir);

            var name = ChatSessionNaming.SafeFileName(displayName ?? Path.GetFileName(sourcePath));
            var target = MakeUnique(Path.Combine(dir, name));
            File.Copy(sourcePath, target, overwrite: false);
            return Path.GetRelativePath(SessionDirectory(sessionName), target);
        }
        catch { return null; }
    }

    /// <summary>写一段字节进会话文件夹（接收端流式落盘用；调用方自己处理重名）。</summary>
    public bool WriteBytes(string sessionName, string relativePath, byte[] content)
    {
        try
        {
            var full = Path.Combine(SessionDirectory(sessionName), relativePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(full, content);
            return true;
        }
        catch { return false; }
    }

    /// <summary>写文本进会话文件夹。</summary>
    public bool WriteText(string sessionName, string relativePath, string content)
    {
        try
        {
            var full = Path.Combine(SessionDirectory(sessionName), relativePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>会话文件夹里某个相对路径的完整路径（不检查存在）。</summary>
    public string FullPath(string sessionName, string relativePath)
        => Path.Combine(SessionDirectory(sessionName), relativePath);

    /// <summary>
    /// 为一个新文件挑一个不重名的会话内相对路径（<c>images\照片 (1).png</c>）。
    /// 接收端先要路径（要写进聊天记录）再写文件，所以这一步必须能单独问。
    /// </summary>
    public string MakeRelativeFilePath(string sessionName, ChatContentKind kind, string fileName)
    {
        var dir = Path.Combine(SessionDirectory(sessionName), ChatSessionNaming.FolderOf(kind));
        try { Directory.CreateDirectory(dir); } catch { /* 写的时候还会再试一次 */ }
        var safe = ChatSessionNaming.SafeFileName(fileName);
        var target = MakeUnique(Path.Combine(dir, safe));
        return Path.GetRelativePath(SessionDirectory(sessionName), target);
    }

    /// <summary>追加一条聊天记录（一行 JSON；文件不存在会自动建）。</summary>
    public bool AppendLog(string sessionName, ChatLogEntry entry)
    {
        try
        {
            var path = LogPath(sessionName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = JsonSerializer.Serialize(entry, LineOptions);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(line);
            return true;
        }
        catch { return false; }
    }

    /// <summary>往聊天记录里追加一条“本机自己产生”的系统消息（会话开始/结束、落盘失败等）。</summary>
    public bool AppendSystemLine(string sessionName, string text) => AppendLog(sessionName, new ChatLogEntry
    {
        MessageId = Guid.NewGuid().ToString("N")[..12],
        Kind = "system",
        SentLocal = DateTime.Now,
        Text = text,
        Sender = "本机",
    });

    /// <summary>读回整个会话的聊天记录（坏行跳过，不因为一行坏了整份读不出来）。</summary>
    public IReadOnlyList<ChatLogEntry> ReadLog(string sessionName)
    {
        var list = new List<ChatLogEntry>();
        try
        {
            var path = LogPath(sessionName);
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    // ⚠ 这里用严格模式（LineOptions）。用宽容模式的话 "{ 这不是坏 JSON" 会被解析成
                    // 一个字段全空的 ChatLogEntry，界面上就多一条空白消息（自检里抓到过）。
                    // 另外：Kind 为空的行直接跳过 —— 程序被杀时留下的半行 JSON 可能刚好能解析出前半截字段。
                    var entry = JsonSerializer.Deserialize<ChatLogEntry>(line, LineOptions);
                    if (entry is null || entry.MessageId.Length == 0 || entry.Kind.Length == 0) continue;
                    list.Add(entry);
                }
                catch { /* 坏行跳过 */ }
            }
        }
        catch { /* 读不了就当空记录 */ }
        return list;
    }

    /// <summary>打开会话根目录。</summary>
    public bool OpenInExplorer(string? sessionName = null)
    {
        try
        {
            var target = string.IsNullOrWhiteSpace(sessionName) ? RootDirectory : SessionDirectory(sessionName!);
            if (!Directory.Exists(target)) Directory.CreateDirectory(target);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>在资源管理器里定位到某个文件。</summary>
    public bool RevealInExplorer(string sessionName, string relativePath)
    {
        try
        {
            var full = FullPath(sessionName, relativePath);
            if (!File.Exists(full)) return OpenInExplorer(sessionName);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{full}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>把一个新文件写进某个会话（流式落盘用；返回相对路径或 null）。</summary>
    public async Task<string?> WriteFileAsync(string sessionName, ChatContentKind kind, string fileName,
        Func<Stream, CancellationToken, Task> writeBody, CancellationToken ct)
    {
        try
        {
            var relative = MakeRelativeFilePath(sessionName, kind, fileName);
            var full = FullPath(sessionName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await writeBody(stream, ct).ConfigureAwait(false);
            }
            return relative;
        }
        catch { return null; }
    }

    /// <summary>重名就加 <c>(1)</c>、<c>(2)</c>…（和 <c>ToolStorage.Import</c> 一个套路，但不依赖它）。</summary>
    public static string MakeUnique(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return fullPath;
            var dir = Path.GetDirectoryName(fullPath) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(fullPath);
            var ext = Path.GetExtension(fullPath);
            for (var i = 1; i < 10000; i++)
            {
                var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
        }
        catch { return fullPath; }
    }

    /// <summary>会话抬头（<c>session.json</c>）。</summary>
    public sealed class ChatSessionInfo
    {
        /// <summary>会话文件夹名（= 开始连接的时刻）。</summary>
        public string SessionName { get; set; } = "";

        public DateTime StartedAt { get; set; } = DateTime.Now;

        public DateTime? EndedAt { get; set; }

        /// <summary><c>Host</c> / <c>Client</c>（本端身份）。</summary>
        public string Role { get; set; } = "Host";

        /// <summary>对方的地址；主机端为空（是别人连进来）。</summary>
        public string Peer { get; set; } = "";

        public string HostName { get; set; } = "";

        public string MemberId { get; set; } = "";

        public string AppVersion { get; set; } = "";

        /// <summary>消息条数（文字 + 文件 + 系统提示）。</summary>
        public int MessageCount { get; set; }

        /// <summary>收到的文件个数。</summary>
        public int ReceivedFileCount { get; set; }

        /// <summary>发出的文件个数。</summary>
        public int SentFileCount { get; set; }

        /// <summary>本端发出的总字节（含包头）。</summary>
        public long SentBytes { get; set; }

        /// <summary>本端收到的总字节（含包头）。</summary>
        public long ReceivedBytes { get; set; }

        /// <summary>单次流量上限（0 = 不限）——记录下来，回看时知道当时是怎么设的。</summary>
        public long TrafficLimitBytes { get; set; }

        /// <summary>会话文件夹完整路径（读取时补上，不写进文件）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DirectoryPath { get; set; } = "";

        /// <summary>身份中文（"主机" / "客户端"）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string RoleText => string.Equals(Role, "Client", StringComparison.OrdinalIgnoreCase) ? "客户端" : "主机";

        /// <summary>对端显示文本。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string PeerText => Peer;

        /// <summary>列表里的一行摘要。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string SummaryText
            => $"{StartedAt:yyyy-MM-dd HH:mm:ss}　{RoleText}（{StatusText}）　{MessageCount} 条消息　收 {FileSize.Format(ReceivedBytes)} / 发 {FileSize.Format(SentBytes)}";

        /// <summary>是否已结束。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string StatusText => EndedAt is null ? "进行中/异常退出" : "已结束";
    }

    /// <summary>聊天记录里的一行（<c>chat-log.jsonl</c>）。</summary>
    public sealed class ChatLogEntry
    {
        public string MessageId { get; set; } = "";

        /// <summary><c>text</c> / <c>system</c> / <c>file</c>。</summary>
        public string Kind { get; set; } = "text";

        /// <summary>发送方显示名（本机自己产生的写"本机"）。</summary>
        public string Sender { get; set; } = "";

        public DateTime SentLocal { get; set; } = DateTime.Now;

        /// <summary>文字内容（文件消息里是留言）。</summary>
        public string? Text { get; set; }

        /// <summary>文件名（本地存的那份的名字）。</summary>
        public string? FileName { get; set; }

        public long FileSize { get; set; }

        /// <summary><c>image</c> / <c>audio</c> / <c>video</c> / <c>file</c> …</summary>
        public string? ContentKind { get; set; }

        /// <summary>相对会话文件夹的路径（图片会自动压缩时，这是原图）。</summary>
        public string? RelativePath { get; set; }

        /// <summary>自动压缩出来的预览图相对路径。</summary>
        public string? PreviewPath { get; set; }

        /// <summary>发出的方向：<c>out</c>（本机发出）/ <c>in</c>（收到）。</summary>
        public string Direction { get; set; } = "in";

        /// <summary>文件的 CRC32（十六进制，对账用）。</summary>
        public string? Crc32 { get; set; }
    }
}
