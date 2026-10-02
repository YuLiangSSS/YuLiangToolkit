using System.IO;
using System.Linq;
using YuLiangToolkit.Tools.ChatRoom;
using YuLiangToolkit.Tools.ChatRoom.Net;
using YuLiangToolkit.Tools.ChatRoom.Protocol;
using YuLiangToolkit.Tools.ChatRoom.Sessions;
using YuLiangToolkit.Tools.ChatRoom.Traffic;
using YuLiangToolkit.Tools.ChatRoom.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「联机聊天室」自检：协议帧 / CRC / 单次流量上限 / 会话文件夹命名 / 聊天记录 /
/// <b>真的在 127.0.0.1 上开一局主机 + 客户端聊一轮</b>（双向文字与文件）。
///
/// <para>网络那一组走回环连接，不碰外网；会话文件夹写到 <c>YLT_CHAT_HOME</c> 指定的临时目录
/// （<see cref="Run"/> 里自己设，跑完就撤），所以不会污染真实数据。</para>
/// </summary>
public static class ChatRoomChecks
{
    /// <summary>
    /// 跑完所有聊天室断言。
    /// <paramref name="check"/> 带 detail；<paramref name="plain"/> 是只报对错的简写。
    /// </summary>
    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        // 默认自己在（可写的）临时目录里造一个会话根；已经用 YLT_CHAT_HOME 指定过就沿用
        // （跑完不想删、要翻记录时好用）
        var scratch = Environment.GetEnvironmentVariable(ChatSessionStore.RootOverrideVariable);
        if (string.IsNullOrWhiteSpace(scratch))
        {
            scratch = SelfCheckScratch.MakeDirectory("ylt-chat");
            Environment.SetEnvironmentVariable(ChatSessionStore.RootOverrideVariable, scratch);
        }

        try
        {
            CheckProtocol(check, plain);
            CheckCrc32(check, plain);
            CheckTrafficMeter(check, plain);
            CheckNaming(plain);
            CheckSessionFolder(check, plain, scratch);
            CheckLoopback(check, plain, scratch);
        }
        finally
        {
            // 只清掉自己造的那个（YLT_CHAT_HOME 是调用方设的就别动）
            if (Environment.GetEnvironmentVariable(ChatSessionStore.RootOverrideVariable) == scratch
                && scratch.Contains("ylt-chat-"))
                Environment.SetEnvironmentVariable(ChatSessionStore.RootOverrideVariable, null);
        }
    }

    // ---------------------------------------------------------------- 协议

    private static void CheckProtocol(Action<string, bool, string> check, Action<string, bool> plain)
    {
        // 端点解析：IPv4 / 域名 / IPv6 / 带端口 / 错误输入
        var v4 = ChatProtocol.ParseEndpoint("192.168.1.7");
        plain("解析 IPv4 地址", v4.Ok && v4.Host == "192.168.1.7" && v4.Port == 0);

        var v4port = ChatProtocol.ParseEndpoint("192.168.1.7:5200");
        plain("解析 IPv4:端口（内网穿透工具常给这种）",
            v4port.Ok && v4port.Host == "192.168.1.7" && v4port.Port == 5200);

        var v6 = ChatProtocol.ParseEndpoint("[2001:db8::1]:5200");
        plain("解析 [IPv6]:端口", v6.Ok && v6.Host == "2001:db8::1" && v6.Port == 5200);

        var v6bare = ChatProtocol.ParseEndpoint("fe80::1");
        plain("解析裸 IPv6（多个冒号不会被当成端口）", v6bare.Ok && v6bare.Host == "fe80::1" && v6bare.Port == 0);

        var domain = ChatProtocol.ParseEndpoint("abc.frp.example.com:12345");
        plain("解析域名:端口（内网穿透给的域名）",
            domain.Ok && domain.Host == "abc.frp.example.com" && domain.Port == 12345);

        plain("空地址被拒", !ChatProtocol.ParseEndpoint("   ").Ok);
        plain("端口不是数字被拒", !ChatProtocol.ParseEndpoint("1.2.3.4:abc").Ok);
        plain("端口越界被拒", !ChatProtocol.ParseEndpoint("1.2.3.4:70000").Ok);
        plain("IPv6 缺右括号被拒", !ChatProtocol.ParseEndpoint("[2001:db8::1").Ok);

        // 帧格式：4 字节小端长度（只数 JSON 头）+ JSON 头 + 裸载荷
        var header = ChatFrameHeader.New(ChatProtocol.Kinds.Text, "me", new TextInfo { MessageId = "m1" });
        var payload = Encoding.UTF8.GetBytes("你好，聊天室");
        var frame = ChatProtocol.BuildFrame(header, payload);
        var declared = BitConverter.ToInt32(frame, 0);
        var expectedHeaderLength = frame.Length - ChatProtocol.LengthPrefixBytes - payload.Length;
        check("长度前缀 = JSON 头的字节数（不含前缀、不含载荷）", declared == expectedHeaderLength,
            $"声明 {declared}，头实际 {expectedHeaderLength}");

        var headerLength = declared;
        var parsed = System.Text.Json.JsonSerializer.Deserialize<ChatFrameHeader>(
            frame.AsSpan(ChatProtocol.LengthPrefixBytes, headerLength), ChatProtocol.JsonOptions);
        check("帧头能原样解析回来", parsed is { Kind: "text", From: "me" } && parsed.ExtraAs<TextInfo>()?.MessageId == "m1",
            parsed?.Kind ?? "(null)");
        check("载荷长度自动写进头里（调用方不用自己填）", parsed?.Payload == payload.Length,
            $"{parsed?.Payload} / {payload.Length}");
        plain("载荷原样保留（中文不乱码）",
            Encoding.UTF8.GetString(frame.AsSpan(ChatProtocol.LengthPrefixBytes + headerLength)) == "你好，聊天室");

        plain("长度前缀合法性判定（0 / 负数 / 超上限都拒）",
            !ChatProtocol.IsValidFrameLength(0) && !ChatProtocol.IsValidFrameLength(-5)
            && ChatProtocol.IsValidFrameLength(1024) && !ChatProtocol.IsValidFrameLength(ChatProtocol.MaxFrameBytes + 1));

        plain("协议说明文件已随 dll 打包（人能读的那份）",
            typeof(ChatProtocol).Assembly.GetManifestResourceNames()
                .Contains("YuLiangToolkit.Tools.ChatRoom.Protocol.chat-protocol.json"));

        check("查看协议说明的摘要文本非空", ChatProtocol.Describe().Contains("YLTCHAT1"), "");
    }

    // ---------------------------------------------------------------- CRC32

    private static void CheckCrc32(Action<string, bool, string> check, Action<string, bool> plain)
    {
        var standard = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));
        check("CRC32 标准向量（123456789 → cbf43926）", standard == 0xCBF43926u, standard.ToString("x8"));

        plain("CRC32 空数据为 0", Crc32.Compute(Array.Empty<byte>()) == 0u);

        // 文件是边收边算的，分块结果必须和一次算完全一样
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 251)).ToArray();
        var whole = Crc32.Compute(data);
        var state = Crc32.Begin;
        for (var offset = 0; offset < data.Length; offset += 777)
        {
            var length = Math.Min(777, data.Length - offset);
            state = Crc32.Append(state, data.AsSpan(offset, length));
        }
        check("分块计算的 CRC 与一次算一致", Crc32.Finish(state) == whole,
            $"{Crc32.Finish(state):x8} / {whole:x8}");

        var changed = (byte[])data.Clone();
        changed[^1] ^= 0xFF;
        plain("改一个字节 CRC 就不同（校验真的有用）", Crc32.Compute(changed) != whole);
    }

    // ---------------------------------------------------------------- 单次流量上限

    private static void CheckTrafficMeter(Action<string, bool, string> check, Action<string, bool> plain)
    {
        var free = new TrafficMeter(0, TrafficQuotaScope.Off);
        free.AddSent(999_999_999);
        free.AddReceived(999_999_999);
        plain("不限制时永远放行", free.Check(1_000_000_000).Allowed && !free.IsExhausted);
        plain("不限制时剩余额度是 long.MaxValue", free.RemainingBytes == long.MaxValue);

        const long tenMb = 10 * 1024 * 1024;
        var both = new TrafficMeter(tenMb, TrafficQuotaScope.Both);
        plain("刚开局额度充足", both.Check(5 * 1024 * 1024).Allowed && both.RemainingBytes == tenMb);

        both.AddSent(4 * 1024 * 1024);
        both.AddReceived(2 * 1024 * 1024);
        plain("收发合计已用 6 MB / 限 10 MB",
            both.UsedBytes == 6 * 1024 * 1024 && both.RemainingBytes == 4 * 1024 * 1024);

        // 最关键的一条：文件比剩余额度大 → 传之前就拒（而不是传一半被掐）
        var tooBig = both.Check(5 * 1024 * 1024);
        plain("文件比剩余额度大 → 发送前就拒", !tooBig.Allowed && tooBig.Reason.Contains("超出单次流量上限"));
        plain("刚好等于剩余额度 → 放行", both.Check(4 * 1024 * 1024).Allowed);
        plain("超一点点也拒", !both.Check(4 * 1024 * 1024 + 1).Allowed);

        both.AddSent(4 * 1024 * 1024);
        plain("到顶后连文字都不让发（bytes=0）", !both.Check(0).Allowed && both.IsExhausted);
        plain("到顶提醒只提醒一次", both.ShouldNotifyExhausted() && !both.ShouldNotifyExhausted());

        var upload = new TrafficMeter(tenMb, TrafficQuotaScope.Upload);
        upload.AddReceived(9 * 1024 * 1024);
        plain("仅上传模式：下载不占额度", upload.Check(9 * 1024 * 1024).Allowed && upload.UsedBytes == 0);
        upload.AddSent(9 * 1024 * 1024);
        plain("仅上传模式：上传占额度", !upload.Check(2 * 1024 * 1024).Allowed);

        var download = new TrafficMeter(tenMb, TrafficQuotaScope.Download);
        download.AddSent(9 * 1024 * 1024);
        plain("仅下载模式：上传不占额度", download.Check(9 * 1024 * 1024).Allowed);
        download.AddReceived(9 * 1024 * 1024);
        Console.WriteLine($"PROBE download: limit={download.LimitBytes} scope={download.Scope} used={download.UsedBytes} remaining={download.RemainingBytes} exhausted={download.IsExhausted}");
        check("仅下载模式：收到的流量占额度（剩 1 MB，再要 2 MB 就被拒）",
            download.UsedBytes == 9 * 1024 * 1024 && download.RemainingBytes == 1024 * 1024
            && !download.Check(2 * 1024 * 1024).Allowed && download.Check(1024 * 1024).Allowed,
            $"used={download.UsedBytes} remaining={download.RemainingBytes}");

        both.SetLimit(0, TrafficQuotaScope.Off);
        plain("中途改成不限 → 立刻放行", both.Check(1_000_000).Allowed && !both.IsExhausted);
        both.SetLimit(tenMb, TrafficQuotaScope.Both);
        plain("再改回限额 → 立刻恢复判定", both.IsExhausted);

        // 速率用假时钟，读数才稳定
        var fake = 0L;
        var meter = new TrafficMeter(0, TrafficQuotaScope.Off, () => fake);
        meter.AddSent(1000);
        fake += 1000;
        meter.AddSent(1000);
        fake += 1000;
        Console.WriteLine($"PROBE rate: fake={fake}");
        var snapshot = meter.Snapshot();
        check("速率按滑动窗口算（2 次采样之间发了 2 KB → 几百 B/s 量级，不是 0 也不是天文数字）",
            snapshot.SentRate is > 100 and < 4000, $"{snapshot.SentRate:0} B/s（窗口 {fake} ms）");
        plain("快照里的收发字节与文案", snapshot.SentBytes == 2000 && snapshot.Summary.Contains("已发"));

        meter.Reset();
        plain("Reset 之后从头算（重新连接 = 新的一次流量）",
            meter.SentBytes == 0 && meter.ReceivedBytes == 0 && meter.Snapshot().UsedRatio == 0);
    }

    // ---------------------------------------------------------------- 会话命名 / 文件归类

    private static void CheckNaming(Action<string, bool> plain)
    {
        var time = new DateTime(2025, 1, 3, 14, 5, 9);
        plain("会话文件夹名 = 年份.月份.日期-时间.分钟.秒",
            ChatSessionNaming.FolderName(time) == "2025.1.3-14.5.9");
        plain("名字里不补前导零（按需求原样）",
            ChatSessionNaming.FolderName(new DateTime(2025, 12, 31, 23, 59, 59)) == "2025.12.31-23.59.59");

        plain("能从文件夹名反解出时间", ChatSessionNaming.ParseFolderName("2025.1.3-14.5.9") == time);
        plain("带 -2 后缀的名字也认得出来（同一秒开两次）",
            ChatSessionNaming.ParseFolderName("2025.1.3-14.5.9-2") == time);
        plain("乱名字解析为 null / 判定非法",
            ChatSessionNaming.ParseFolderName("Protocol") is null
            && !ChatSessionNaming.IsValidFolderName("随便起的名字")
            && ChatSessionNaming.ParseFolderName("2025.13.3-14.5.9") is null);

        plain("图片 → images 子目录（扩展名大小写不敏感）",
            ChatSessionNaming.KindOfExtension(".PNG") == ChatContentKind.Image
            && ChatSessionNaming.FolderOf(ChatContentKind.Image) == "images");
        plain("音频 → audio", ChatSessionNaming.KindOfFile("a.mp3") == ChatContentKind.Audio
            && ChatSessionNaming.FolderOf(ChatContentKind.Audio) == "audio");
        plain("视频 → video", ChatSessionNaming.KindOfFile("a.MP4") == ChatContentKind.Video
            && ChatSessionNaming.FolderOf(ChatContentKind.Video) == "video");
        plain("压缩包 / 文档 / 其它都进 files",
            ChatSessionNaming.KindOfFile("a.zip") == ChatContentKind.Archive
            && ChatSessionNaming.KindOfFile("a.docx") == ChatContentKind.Doc
            && ChatSessionNaming.KindOfFile("没有任何扩展名") == ChatContentKind.File
            && ChatSessionNaming.FolderOf(ChatContentKind.Archive) == "files");
        plain("中文标签", ChatSessionNaming.LabelOf(ChatContentKind.Video) == "视频"
            && ChatSessionNaming.LabelOf(ChatContentKind.File) == "文件");

        plain("文件名里的非法字符被替换",
            ChatSessionNaming.SafeFileName("a\\b/c:d*e?f\"g<h>i|j.png") == "a_b_c_d_e_f_g_h_i_j.png");
        plain("结尾的点与空格被去掉（Windows 会悄悄吃掉）",
            ChatSessionNaming.SafeFileName("名字. ") == "名字");
        plain("Windows 保留设备名会被加下划线", ChatSessionNaming.SafeFileName("CON.txt").StartsWith('_'));
        plain("空名字有兜底", ChatSessionNaming.SafeFileName("   ") == "未命名");
    }

    // ---------------------------------------------------------------- 会话文件夹 / 聊天记录

    private static void CheckSessionFolder(Action<string, bool, string> check, Action<string, bool> plain, string scratch)
    {
        var store = new ChatSessionStore(ChatRoomConfig.ToolId);
        var start = new DateTime(2025, 1, 3, 14, 5, 9);
        var info = new ChatSessionStore.ChatSessionInfo { Role = "Host", HostName = "自检" };
        var name = store.CreateSession(info, start);

        check("开始连接就新建一个以时间命名的会话文件夹",
            name == "2025.1.3-14.5.9" && Directory.Exists(store.SessionDirectory(name)),
            store.SessionDirectory(name));

        foreach (var sub in ChatSessionNaming.CategoryFolders)
            plain($"会话文件夹里有 {sub}/ 子目录", Directory.Exists(Path.Combine(store.SessionDirectory(name), sub)));

        check("会话都放在根目录下的 Sessions 子目录里（历史列表不会被别的东西污染）",
            Path.GetFileName(store.SessionsRoot) == "Sessions"
            && string.Equals(Path.GetDirectoryName(store.SessionsRoot), store.RootDirectory, StringComparison.OrdinalIgnoreCase),
            store.SessionsRoot);
        var second = store.CreateSession(new ChatSessionStore.ChatSessionInfo { Role = "Client" }, start);
        check("同一秒再开一次自动补 -2（不覆盖上一个）", second == "2025.1.3-14.5.9-2", second);

        plain("会话文件夹里有说明文件",
            File.Exists(Path.Combine(store.SessionDirectory(name), ChatSessionNaming.ReadmeFileName)));
        var loaded = store.LoadInfo(name);
        check("抬头 session.json 能读回来（身份 / 起始时间）",
            loaded is { Role: "Host", SessionName: "2025.1.3-14.5.9" } && loaded.StartedAt == start, "");

        // 聊天记录：追加 + 读回 + 坏行跳过
        store.AppendLog(name, new ChatSessionStore.ChatLogEntry
        {
            MessageId = "m1", Kind = "text", Sender = "小张", Text = "第一条", Direction = "in", SentLocal = start,
        });
        store.AppendSystemLine(name, "系统提示一条");
        // 故意插一行写了一半的 JSON（程序被杀时很常见）：读记录要跳过它，不能整份读不出来
        File.AppendAllText(store.LogPath(name), "{\"messageId\":\"bad\",\"kind\":\"text\"" + Environment.NewLine);
        store.AppendLog(name, new ChatSessionStore.ChatLogEntry
        {
            MessageId = "m2", Kind = "file", Sender = "我", FileName = "照片.png", FileSize = 12345,
            ContentKind = "image", RelativePath = Path.Combine("images", "照片.png"), Direction = "out", SentLocal = start,
        });

        var entries = store.ReadLog(name);
        // 文件里一共 5 行（会话开始 + 文字 + 系统 + 那份写了一半的坏行 + 文件），坏行必须被跳过
        check("聊天记录一行一条 JSON，能原样读回（写了一半的坏行自动跳过）", entries.Count == 4, $"读到 {entries.Count} 条");
        plain("文字 / 系统 / 文件三种记录都在",
            entries.Any(e => e.Kind == "text" && e.Text == "第一条")
            && entries.Any(e => e.Kind == "system" && e.Text!.Contains("系统提示"))
            && entries.Any(e => e.Kind == "file" && e.FileName == "照片.png" && e.ContentKind == "image"));
        plain("记录是追加的（不是覆盖）", File.ReadAllLines(store.LogPath(name)).Length >= 4);

        // 文件导入：重名自动加序号，不覆盖
        var source = Path.Combine(scratch, "源文件.txt");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(source, "内容");
        var first = store.ImportFile(name, source, ChatContentKind.Doc, "报告.txt");
        var again = store.ImportFile(name, source, ChatContentKind.Doc, "报告.txt");
        check("导入文件进 files/，重名自动加序号（不覆盖）",
            first == Path.Combine("files", "报告.txt") && again == Path.Combine("files", "报告 (1).txt"),
            $"{first} / {again}");
        plain("导入的文件真的落盘了", File.Exists(store.FullPath(name, first!)));

        var sessions = store.ListSessions();
        check("历史会话列表能列出两个（含同秒补序号的那个）", sessions.Count == 2, $"列出 {sessions.Count} 个");
        plain("列表里每个名字都能反解出时间",
            sessions.All(s => ChatSessionNaming.ParseFolderName(s.SessionName) is not null));

        info.EndedAt = start.AddMinutes(12);
        info.MessageCount = 3;
        info.SentBytes = 4096;
        info.ReceivedBytes = 8192;
        store.SaveInfo(info);
        var reloaded = store.LoadInfo(name);
        check("会话统计写回 session.json",
            reloaded is { EndedAt: not null, MessageCount: 3, SentBytes: 4096, ReceivedBytes: 8192 }, "");
        plain("列表摘要带上身份与消息数",
            reloaded!.SummaryText.Contains("主机") && reloaded.SummaryText.Contains("3 条消息"));
    }

    // ---------------------------------------------------------------- 真的连一轮（回环）

    private static void CheckLoopback(Action<string, bool, string> check, Action<string, bool> plain, string scratch)
    {
        Exception? failure = null;
        var report = new List<string>();

        var worker = Task.Run(async () =>
        {
            try { await RunLoopbackAsync(check, report, scratch).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
        });

        if (!worker.Wait(TimeSpan.FromSeconds(120)))
            failure = new TimeoutException("回环会话 120 秒没跑完（多半卡在连接或传输上了）");

        check("回环会话整体跑完没有抛异常", failure is null,
            failure is null ? string.Join("；", report) : $"{failure.GetType().Name}: {failure.Message}");
    }

    private static async Task RunLoopbackAsync(Action<string, bool, string> check, List<string> report, string scratch)
    {
        var port = FindFreePort();
        var store = new ChatSessionStore(ChatRoomConfig.ToolId);

        var host = new ChatRoomSession(ChatRoomConfig.ToolId, "自检主机", "🐟",
            new TrafficMeter(0, TrafficQuotaScope.Off), store);
        var client = new ChatRoomSession(ChatRoomConfig.ToolId, "自检客户端", "🐻",
            new TrafficMeter(0, TrafficQuotaScope.Off), store);

        var hostTexts = new List<string>();
        var clientTexts = new List<string>();
        var hostFiles = new List<string>();
        var clientFiles = new List<string>();
        var hostMemberCount = 0;

        host.MessagePosted += m => { if (!m.IsSystem && m.Text.Length > 0) hostTexts.Add(m.Text); };
        client.MessagePosted += m => { if (!m.IsSystem && m.Text.Length > 0) clientTexts.Add(m.Text); };
        host.TransferChanged += t => { if (t.NewMessage is not null) hostFiles.Add(t.FileName); };
        client.TransferChanged += t => { if (t.NewMessage is not null) clientFiles.Add(t.FileName); };
        host.MembersChanged += m => hostMemberCount = Math.Max(hostMemberCount, m.Count);

        // 排错开关：设了 YLT_CHAT_TRACE 就把两边的状态提示与断线原因写到文件
        var tracePath = Environment.GetEnvironmentVariable("YLT_CHAT_TRACE");
        if (!string.IsNullOrEmpty(tracePath))
        {
            void Trace(string side, string text)
            {
                try { File.AppendAllText(tracePath, $"{DateTime.Now:HH:mm:ss.fff} [{side}] {text}{Environment.NewLine}"); }
                catch { /* 排错用，失败就算了 */ }
            }
            host.Noticed += n => Trace("host", $"{n.Level}: {n.Text}");
            client.Noticed += n => Trace("client", $"{n.Level}: {n.Text}");
            host.Disconnected += e => Trace("host", $"断线：{e ?? "(正常)"}");
            client.Disconnected += e => Trace("client", $"断线：{e ?? "(正常)"}");
            host.MessagePosted += m => Trace("host", $"消息：{(m.IsSystem ? "系统 " : "")}{(m.IsMine ? "我" : "对方")} {m.Text}");
            client.MessagePosted += m => Trace("client", $"消息：{(m.IsSystem ? "系统 " : "")}{(m.IsMine ? "我" : "对方")} {m.Text}");
            client.TextArrived += (_, t) => Trace("client", $"TextArrived: {t}");
            host.TextArrived += (_, t) => Trace("host", $"TextArrived: {t}");
        }

        try
        {
            // ① 主机开房间（只绑回环，不占局域网的端口）
            var startError = await host.StartHostAsync("127.0.0.1", port).ConfigureAwait(false);
            check("主机能在 127.0.0.1 上开监听", startError.Length == 0 && host.IsOnline, startError);
            check("主机监听到的端口就是指定的那个（端口填 0 时也能问到真实端口）",
                host.Host?.EndPoint?.Port == port, host.ListenText);
            report.Add($"主机监听 {host.ListenText}");

            // ② 客户端连上去 + 握手
            var connectError = await client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
            check("客户端能连上并完成握手", connectError.Length == 0 && client.IsOnline, connectError);

            check("两边各自新建了一个会话文件夹（客户端写自己的名字，不跟主机撞）",
                host.SessionName.Length > 0 && client.SessionName.Length > 0
                && host.SessionName != client.SessionName, $"{host.SessionName} / {client.SessionName}");

            check("客户端采用主机分配的成员 Id（不是自己编的那个）",
                client.LocalMemberId.Length > 0 && !client.LocalMemberId.StartsWith("me-", StringComparison.Ordinal),
                client.LocalMemberId);
            report.Add($"客户端会话 {client.SessionName}");

            await WaitAsync(() => host.ClientCount == 1 && hostMemberCount >= 2, 8000).ConfigureAwait(false);
            check("主机看到 1 个客户端、成员名单 2 个人（含主机自己）",
                host.ClientCount == 1 && hostMemberCount >= 2, $"clients={host.ClientCount} members={hostMemberCount}");

            // ③ 双向文字
            await host.SendTextAsync("主机说话：收到请回答").ConfigureAwait(false);
            await WaitAsync(() => clientTexts.Contains("主机说话：收到请回答"), 8000).ConfigureAwait(false);
            check("主机的文字客户端收到了", clientTexts.Contains("主机说话：收到请回答"), string.Join(" | ", clientTexts));

            await client.SendTextAsync("客户端说话：收到").ConfigureAwait(false);
            await WaitAsync(() => hostTexts.Contains("客户端说话：收到"), 8000).ConfigureAwait(false);
            check("客户端的文字主机收到了", hostTexts.Contains("客户端说话：收到"), string.Join(" | ", hostTexts));
            check("自己发的消息也进自己的界面（本端留一份）", clientTexts.Contains("客户端说话：收到"), "");

            // ④ 双向发文件（图片 / 压缩包 / 音频，各走各的子目录）
            Directory.CreateDirectory(scratch);
            var imagePath = Path.Combine(scratch, "照片.png");
            var archivePath = Path.Combine(scratch, "素材.zip");
            var audioPath = Path.Combine(scratch, "语音.mp3");
            WritePseudoFile(imagePath, 300 * 1024);
            WritePseudoFile(archivePath, 900 * 1024);
            WritePseudoFile(audioPath, 150 * 1024);

            await host.SendFileAsync(imagePath, "这是主机发的图").ConfigureAwait(false);
            await WaitAsync(() => clientFiles.Contains("照片.png"), 25000).ConfigureAwait(false);
            check("主机发的图片客户端收到了", clientFiles.Contains("照片.png"), string.Join(" | ", clientFiles));

            await client.SendFileAsync(archivePath, "客户端发的压缩包").ConfigureAwait(false);
            await WaitAsync(() => hostFiles.Contains("素材.zip"), 25000).ConfigureAwait(false);
            check("客户端发的压缩包主机收到了", hostFiles.Contains("素材.zip"), string.Join(" | ", hostFiles));

            await client.SendFileAsync(audioPath).ConfigureAwait(false);
            await WaitAsync(() => hostFiles.Contains("语音.mp3"), 25000).ConfigureAwait(false);
            check("音频也照样能发", hostFiles.Contains("语音.mp3"), string.Join(" | ", hostFiles));

            // ⑤ 收到的东西落盘要对得上（字节级一致）
            var receivedImage = Path.Combine(store.SessionDirectory(client.SessionName), "images", "照片.png");
            check("收到的图片落在客户端的 images/ 子目录（文件名不变）", File.Exists(receivedImage), receivedImage);
            check("收到的图片大小与源文件一致",
                File.Exists(receivedImage) && new FileInfo(receivedImage).Length == new FileInfo(imagePath).Length,
                File.Exists(receivedImage) ? new FileInfo(receivedImage).Length.ToString() : "(没有这个文件)");
            check("收到的图片内容与源文件逐字节一致（协议里的 CRC 也是这么算的）",
                File.Exists(receivedImage) && File.ReadAllBytes(receivedImage).SequenceEqual(File.ReadAllBytes(imagePath)), "");

            var receivedArchive = Path.Combine(store.SessionDirectory(host.SessionName), "files", "素材.zip");
            check("压缩包按大类落在 files/ 子目录", File.Exists(receivedArchive), receivedArchive);
            check("压缩包内容一致", File.Exists(receivedArchive)
                && File.ReadAllBytes(receivedArchive).SequenceEqual(File.ReadAllBytes(archivePath)), "");

            check("音频落在 audio/ 子目录",
                File.Exists(Path.Combine(store.SessionDirectory(host.SessionName), "audio", "语音.mp3")), "");

            check("主机发出的文件在本端会话文件夹里也留了一份（会话文件夹 = 完整账本）",
                File.Exists(Path.Combine(store.SessionDirectory(host.SessionName), "images", "照片.png")), "");

            // ⑥ 流量统计：两边都在累加，而且是镜像的
            var hostSnapshot = host.Meter.Snapshot();
            var clientSnapshot = client.Meter.Snapshot();
            check("主机统计到了收发流量", hostSnapshot.SentBytes > 0 && hostSnapshot.ReceivedBytes > 0,
                $"↑{hostSnapshot.SentBytes} ↓{hostSnapshot.ReceivedBytes}");
            check("客户端统计到了收发流量", clientSnapshot.SentBytes > 0 && clientSnapshot.ReceivedBytes > 0,
                $"↑{clientSnapshot.SentBytes} ↓{clientSnapshot.ReceivedBytes}");
            check("一方的发送量 ≈ 另一方的接收量（流量计的是同一条线上的字节）",
                Math.Abs(hostSnapshot.SentBytes - clientSnapshot.ReceivedBytes) < 4096
                && Math.Abs(clientSnapshot.SentBytes - hostSnapshot.ReceivedBytes) < 4096,
                $"host↑{hostSnapshot.SentBytes} client↓{clientSnapshot.ReceivedBytes}｜"
                + $"client↑{clientSnapshot.SentBytes} host↓{hostSnapshot.ReceivedBytes}");

            // ⑦ 聊天记录里查得到（关掉之后也不丢）
            var hostLog = store.ReadLog(host.SessionName);
            var clientLog = store.ReadLog(client.SessionName);
            check("主机的聊天记录里有客户端说的那句话",
                hostLog.Any(e => e.Text == "客户端说话：收到"), $"主机记录 {hostLog.Count} 条");
            check("客户端的聊天记录里有主机发的文字与收到的文件",
                clientLog.Any(e => e.Text == "主机说话：收到请回答")
                && clientLog.Any(e => e.FileName == "照片.png" && e.Direction == "in"),
                $"客户端记录 {clientLog.Count} 条");
            check("记录里的文件条目带着相对路径（回看时能点开）",
                clientLog.Any(e => e.RelativePath is { Length: > 0 } && e.RelativePath.Contains("images")),
                clientLog.FirstOrDefault(e => e.FileName is not null)?.RelativePath ?? "(没有文件条目)");

            // ⑧ 单次流量上限：额度小到装不下一个文件 → 发送前就拦下，连接不断
            var limitHost = new ChatRoomSession(ChatRoomConfig.ToolId, "限量主机", "🐟",
                new TrafficMeter(120 * 1024, TrafficQuotaScope.Both), store);
            var limitClient = new ChatRoomSession(ChatRoomConfig.ToolId, "限量客户端", "🐻",
                new TrafficMeter(120 * 1024, TrafficQuotaScope.Both), store);
            var blocked = new List<string>();
            limitHost.TransferChanged += t =>
            {
                if (t.Progress.State == TransferState.Blocked) blocked.Add(t.FileName);
            };
            try
            {
                var limitPort = FindFreePort();
                await limitHost.StartHostAsync("127.0.0.1", limitPort).ConfigureAwait(false);
                var limitError = await limitClient.ConnectAsync("127.0.0.1", limitPort).ConfigureAwait(false);
                check("限额会话也能正常握手（限额只管发东西，不管连接）", limitError.Length == 0, limitError);

                await limitHost.SendFileAsync(imagePath).ConfigureAwait(false);   // 300 KB > 120 KB 额度
                await WaitAsync(() => blocked.Contains("照片.png"), 8000).ConfigureAwait(false);
                check("超过单次流量上限的文件在发送前就被拦下", blocked.Contains("照片.png"), string.Join(" | ", blocked));
                check("拦下之后连接还在（不是断线）", limitHost.IsOnline && limitClient.IsOnline, "");
                check("被拦下的文件真的没跑出去（发送量很小）",
                    limitHost.Meter.SentBytes < 120 * 1024, limitHost.Meter.SentBytes.ToString());

                limitHost.Meter.SetLimit(0, TrafficQuotaScope.Off);
                limitClient.Meter.SetLimit(0, TrafficQuotaScope.Off);
                var sent = new List<string>();
                limitClient.TransferChanged += t => { if (t.NewMessage is not null) sent.Add(t.FileName); };
                await limitHost.SendFileAsync(imagePath).ConfigureAwait(false);
                await WaitAsync(() => sent.Contains("照片.png"), 25000).ConfigureAwait(false);
                check("把上限调大之后马上就能发了（不用重连）", sent.Contains("照片.png"), string.Join(" | ", sent));
            }
            finally
            {
                await limitClient.DisposeAsync().ConfigureAwait(false);
                await limitHost.DisposeAsync().ConfigureAwait(false);
            }

            // ⑨ 断开：主机这边人数归零，两边都补写会话统计
            await client.StopAsync("自检结束").ConfigureAwait(false);
            await WaitAsync(() => host.ClientCount == 0, 8000).ConfigureAwait(false);
            check("客户端断开后主机的客户端数回到 0", host.ClientCount == 0, host.ClientCount.ToString());

            await host.StopAsync("自检结束").ConfigureAwait(false);
            check("主机关掉监听后不再是联机状态", !host.IsOnline, "");

            var hostInfo = store.LoadInfo(host.SessionName);
            var clientInfo = store.LoadInfo(client.SessionName);
            check("会话结束后抬头补上了结束时间与统计",
                hostInfo is { EndedAt: not null, MessageCount: > 0, ReceivedFileCount: > 0 }
                && clientInfo is { EndedAt: not null, MessageCount: > 0 },
                $"host msgs={hostInfo?.MessageCount} recv={hostInfo?.ReceivedFileCount}｜client msgs={clientInfo?.MessageCount}");
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- 小工具

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(40).ConfigureAwait(false);
        }
    }

    private static void WritePseudoFile(string path, int bytes)
    {
        var data = new byte[bytes];
        new Random(bytes).NextBytes(data);
        File.WriteAllBytes(path, data);
    }
}
