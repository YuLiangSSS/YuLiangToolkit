using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.ChatRoom;
using YuLiangToolkit.Tools.ChatRoom.Call;
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
            CheckSimpleMode(check, plain);
            CheckDualStack(check, plain, scratch);
            CheckLoopback(check, plain, scratch);
            CheckCallCodecs(check, plain);
            CheckCallProtocol(check, plain);
            CheckCallLoopback(check, plain);
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

    // ---------------------------------------------------------------- 傻瓜模式（自动挑监听 + 地址显示）

    /// <summary>
    /// 傻瓜模式的两件事：**工具自己挑监听地址与端口**（纯逻辑，探测函数换成假的来断言），
    /// 以及**开完房间要能报出本机 IPv4 / IPv6**（地址枚举是真枚举网卡）。
    /// </summary>
    private static void CheckSimpleMode(Action<string, bool, string> check, Action<string, bool> plain)
    {
        // ---- 端口探测（真绑一次） ----
        var free = FindFreePort();
        check("端口探测：空闲端口返回 true", ChatProtocol.IsPortAvailable("0.0.0.0", free), free.ToString());

        var holder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        holder.Start();
        var busy = ((System.Net.IPEndPoint)holder.LocalEndpoint).Port;
        check("端口探测：被占用的端口返回 false（探测时故意不设 SO_REUSEADDR，否则 Windows 会“抢”端口）",
            !ChatProtocol.IsPortAvailable("0.0.0.0", busy), busy.ToString());
        holder.Stop();

        plain("端口探测：非法端口一律 false",
            !ChatProtocol.IsPortAvailable("0.0.0.0", 0) && !ChatProtocol.IsPortAvailable("0.0.0.0", 70000));

        // ---- 傻瓜模式挑方案：探测函数换成假的，纯逻辑逐条断言 ----
        var alwaysFree = new Func<string, int, bool>((_, _) => true);
        var neverFree = new Func<string, int, bool>((_, _) => false);

        var withV6 = ChatProtocol.ChooseSimpleListenPlan(5200, true, alwaysFree);
        check("傻瓜模式：支持 IPv6 时监听 ::（双栈）并沿用上次的端口",
            withV6 is { Address: "::", Port: 5200, DualStack: true }, withV6.Reason);

        var busyPlan = ChatProtocol.ChooseSimpleListenPlan(5200, true, neverFree);
        check("傻瓜模式：端口被占就改成让系统挑一个（0），地址仍然是双栈",
            busyPlan is { Address: "::", Port: 0, DualStack: true } && busyPlan.Reason.Contains("占用"), busyPlan.Reason);

        var noV6Plan = ChatProtocol.ChooseSimpleListenPlan(5200, false, alwaysFree);
        check("傻瓜模式：本机不支持 IPv6 时退回 0.0.0.0，并且不再是双栈",
            noV6Plan is { Address: "0.0.0.0", Port: 5200, DualStack: false }, noV6Plan.Reason);

        var weirdPlan = ChatProtocol.ChooseSimpleListenPlan(80, true, alwaysFree);
        check("傻瓜模式：端口不在 1024~65535 就不硬用（改成让系统挑）", weirdPlan.Port == 0, weirdPlan.Reason);
        plain("傻瓜模式：上次没设过端口（0）也是让系统挑",
            ChatProtocol.ChooseSimpleListenPlan(0, true, alwaysFree).Port == 0);

        // ---- 地址枚举：开完房间要报给用户的东西 ----
        var addresses = ChatProtocol.ListShareAddresses();
        plain("地址列表：只可能是 IPv4 / IPv6 两族", addresses.All(a => a.Family is "IPv4" or "IPv6"));
        plain("地址列表：不含回环 / 自动私有地址 / IPv6 链路本地（报了对方也连不上）",
            addresses.All(IsAddressWorthSharing));

        var list = addresses.ToList();
        var firstV6 = list.FindIndex(a => a.Family == "IPv6");
        var lastV4 = list.FindLastIndex(a => a.Family == "IPv4");
        plain("地址列表：IPv4 全部排在 IPv6 前面", firstV6 < 0 || lastV4 < 0 || lastV4 < firstV6);
        plain("地址列表：每族最多只有一个「推荐」",
            list.Count(a => a is { Family: "IPv4", IsRecommended: true }) <= 1
            && list.Count(a => a is { Family: "IPv6", IsRecommended: true }) <= 1);
        check("「有没有可用 IPv6」和地址列表一致（界面那句「当前网络没有 IPv6」靠它）",
            ChatProtocol.HasUsableIPv6 == list.Any(a => a.Family == "IPv6"),
            $"IPv6 {list.Count(a => a.Family == "IPv6")} 个 / IPv4 {list.Count(a => a.Family == "IPv4")} 个");

        // ---- 拼端口（界面上一行一个「地址:端口」） ----
        check("拼端口：IPv4 / 带方括号的 IPv6 / 裸 IPv6 都能拼",
            ChatProtocol.WithPort("192.168.1.7", 5200) == "192.168.1.7:5200"
            && ChatProtocol.WithPort("[2409::1]", 5200) == "[2409::1]:5200"
            && ChatProtocol.WithPort("2409::1", 5200) == "[2409::1]:5200"
            && ChatProtocol.WithPort("", 5200) == "",
            ChatProtocol.WithPort("2409::1", 5200));

        plain("「当前网络没有 IPv6」那句话是给人看的（不是空串）", ChatProtocol.NoIpv6Text.Contains("没有 IPv6"));
    }

    /// <summary>这个地址值不值得报给对方（独立实现一遍，别拿被测代码自己验自己）。</summary>
    private static bool IsAddressWorthSharing(ChatProtocol.ShareAddress address)
    {
        var text = address.Text.Trim('[', ']');
        if (!System.Net.IPAddress.TryParse(text, out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return !text.StartsWith("169.254.");
        return !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast && !ip.IsIPv6SiteLocal;
    }

    // ---------------------------------------------------------------- 双栈监听（同一个端口收两族）

    /// <summary>
    /// 回归断言：绑 <c>::</c> 时**必须显式开双栈**。
    ///
    /// <para>Windows 上 IPv6 通配地址默认 <c>IPV6_V6ONLY=1</c>（只收 IPv6），.NET 的
    /// <see cref="System.Net.Sockets.TcpListener"/> 不会替我们打开 —— 不设 <c>DualMode</c>，
    /// 绑了 <c>::</c> 之后 IPv4 客户端**一个都连不进来**，而界面上还写着「IPv4 与 IPv6 都能连」。
    /// 傻瓜模式默认就走双栈，所以这条必须有断言兜着（改回不设 DualMode 会立刻红）。</para>
    /// </summary>
    private static void CheckDualStack(Action<string, bool, string> check, Action<string, bool> plain, string scratch)
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            check("跳过「双栈监听」整组自检：本机不支持 IPv6", true, "Socket.OSSupportsIPv6 = false");
            return;
        }

        Exception? failure = null;
        var notes = new List<string>();

        var worker = Task.Run(async () =>
        {
            try { await RunDualStackAsync(check, notes).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
        });

        if (!worker.Wait(TimeSpan.FromSeconds(60)))
            failure = new TimeoutException("双栈自检 60 秒没跑完（多半卡在连接上了）");

        check("双栈自检整体跑完没有抛异常", failure is null,
            failure is null ? string.Join("；", notes) : $"{failure.GetType().Name}: {failure.Message}");
    }

    private static async Task RunDualStackAsync(Action<string, bool, string> check, List<string> notes)
    {
        var port = FindFreePort();
        var store = new ChatSessionStore(ChatRoomConfig.ToolId);

        var host = new ChatRoomSession(ChatRoomConfig.ToolId, "双栈主机", "🐟",
            new TrafficMeter(0, TrafficQuotaScope.Off), store);
        var overV4 = new ChatRoomSession(ChatRoomConfig.ToolId, "IPv4 客户端", "🐻",
            new TrafficMeter(0, TrafficQuotaScope.Off), store);
        var overV6 = new ChatRoomSession(ChatRoomConfig.ToolId, "IPv6 客户端", "🐼",
            new TrafficMeter(0, TrafficQuotaScope.Off), store);

        try
        {
            var startError = await host.StartHostAsync("::", port).ConfigureAwait(false);
            check("主机能在 :: 上开双栈监听（傻瓜模式的默认姿势）",
                startError.Length == 0 && host.IsDualStack, startError.Length > 0 ? startError : host.ListenText);
            notes.Add($"监听 {host.ListenText}，双栈={host.IsDualStack}");

            var v4Error = await overV4.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
            check("同一个端口：IPv4 客户端（127.0.0.1）连得进来", v4Error.Length == 0 && overV4.IsOnline, v4Error);

            var v6Error = await overV6.ConnectAsync("::1", port).ConfigureAwait(false);
            check("同一个端口：IPv6 客户端（::1）也连得进来", v6Error.Length == 0 && overV6.IsOnline, v6Error);

            await WaitAsync(() => host.ClientCount == 2, 5000).ConfigureAwait(false);
            check("两族客户端都出现在主机的成员名单里", host.ClientCount == 2, host.ClientCount.ToString());
        }
        finally
        {
            try { await overV6.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await overV4.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await host.DisposeAsync().ConfigureAwait(false); } catch { }
        }
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

    // ---------------------------------------------------------------- 群通话：编解码（纯逻辑，无设备 / 无网络）

    /// <summary>
    /// 群语音 / 群视频通信用到的编解码轮子：μ-law / 重采样 / 混音 / JPEG / 降采样。
    /// 全部是纯逻辑，不开端口、不碰设备，几十毫秒跑完。
    /// </summary>
    private static void CheckCallCodecs(Action<string, bool, string> check, Action<string, bool> plain)
    {
        // ---- μ-law：正弦波往返误差（G.711 8 位对数量化的实际误差约 1~2% RMS，5% 是宽松上限） ----
        const int sampleRate = 16000;
        var sine = new float[400];
        for (var i = 0; i < sine.Length; i++)
            sine[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000f * i / sampleRate);

        var encoded = new byte[sine.Length];
        MuLawCodec.Encode(sine, encoded);
        var decoded = new float[sine.Length];
        MuLawCodec.Decode(encoded, decoded);

        double signalPower = 0, errorPower = 0;
        for (var i = 0; i < sine.Length; i++)
        {
            signalPower += sine[i] * sine[i];
            var diff = sine[i] - decoded[i];
            errorPower += diff * diff;
        }
        var rmsPercent = Math.Sqrt(errorPower / Math.Max(1e-12, signalPower)) * 100;
        check("μ-law 正弦波（16 kHz / 0.5 振幅）Encode→Decode 往返 RMS 误差 < 5%",
            rmsPercent < 5, $"RMS 误差 {rmsPercent:0.00}%");

        // 边界与批量长度语义：-1 / 0 / 1 不越界；越界输入被夹住；批量接口只写 min(源, 目的) 个
        var edgeOk = true;
        foreach (var value in new[] { -1f, 0f, 1f, -8f, 8f })
        {
            var back = MuLawCodec.Decode(MuLawCodec.Encode(value));
            edgeOk &= back is >= -1f and <= 1f;
        }
        edgeOk &= Math.Abs(MuLawCodec.Decode(MuLawCodec.Encode(0f))) < 0.05f;

        var bulk = new float[100];
        for (var i = 0; i < bulk.Length; i++) bulk[i] = MathF.Sin(i * 0.1f) * 0.5f;

        var longDestination = Enumerable.Repeat((byte)0xAB, 150).ToArray();
        MuLawCodec.Encode(bulk, longDestination);
        var tailUntouched = longDestination.Skip(bulk.Length).All(b => b == 0xAB);

        var encodedBulk = new byte[bulk.Length];
        MuLawCodec.Encode(bulk, encodedBulk);
        var shortDestination = Enumerable.Repeat(float.NaN, 70).ToArray();
        MuLawCodec.Decode(encodedBulk, shortDestination);
        var wroteOnlyDestinationLength = shortDestination.All(v => !float.IsNaN(v) && v is >= -1f and <= 1f);

        check("μ-law 边界 -1/0/1 与越界输入都不越界；批量 Encode/Decode 只写 min(源, 目的) 个",
            edgeOk && tailUntouched && wroteOnlyDestinationLength,
            $"边界={edgeOk} 尾部未动={tailUntouched} 只写目的长度={wroteOnlyDestinationLength}");

        // ---- PcmResampler：输出数量按比例，并且跨块连续（小数相位不能丢） ----
        var resampler48k = new PcmResampler(48000, 16000);
        var input48k = new float[4800];
        for (var i = 0; i < input48k.Length; i++)
            input48k[i] = MathF.Sin(2 * MathF.PI * 1000f * i / 48000f);
        var output48k = new float[4800 / 3 + 8];
        var written48k = resampler48k.Process(input48k, output48k);
        check("PcmResampler 48k→16k：4800 个采样输出约 1600 个（±2）",
            Math.Abs(written48k - 1600) <= 2, $"输出 {written48k} 个");

        var resampler441k = new PcmResampler(44100, 16000);
        var blockA = new float[4410];
        var blockB = new float[4409];
        for (var i = 0; i < blockA.Length; i++)
            blockA[i] = MathF.Sin(2 * MathF.PI * 300f * i / 44100f);
        for (var i = 0; i < blockB.Length; i++)
            blockB[i] = MathF.Sin(2 * MathF.PI * 300f * (blockA.Length + i) / 44100f);
        var buffer441k = new float[4410 * 16000 / 44100 + 8];
        var firstCount = resampler441k.Process(blockA, buffer441k);
        var secondCount = resampler441k.Process(blockB, buffer441k);
        var totalCount = firstCount + secondCount;
        var expectedCount = (int)Math.Round((blockA.Length + blockB.Length) * 16000.0 / 44100.0);
        resampler441k.Reset();
        var afterResetFirst = resampler441k.Process(blockA, buffer441k);
        var afterResetSecond = resampler441k.Process(blockB, buffer441k);
        check("PcmResampler 44.1k→16k：跨块总量符合比例（±2），Reset 后行为可复现",
            Math.Abs(totalCount - expectedCount) <= 2
            && afterResetFirst == firstCount && afterResetSecond == secondCount,
            $"{firstCount}+{secondCount}={totalCount}（期望约 {expectedCount}）；Reset 后 {afterResetFirst}+{afterResetSecond}");

        // ---- AudioMixer：限幅 / 补静音 / RemoveMember / 缓冲上限 ----
        var mixer = new AudioMixer(48000);
        var sineA = new float[640];
        var sineB = new float[640];
        for (var i = 0; i < sineA.Length; i++)
        {
            var value = 0.9f * MathF.Sin(2 * MathF.PI * 440f * i / 16000f);
            sineA[i] = value;
            sineB[i] = value;
        }
        mixer.AddFrame("甲", sineA, sineA.Length, 16000);
        mixer.AddFrame("乙", sineB, sineB.Length, 16000);
        var mixed = new float[480 * 2];
        var mixedFrames = mixer.Mix(480, mixed);
        var peak = 0f;
        var leftEqualsRight = true;
        for (var i = 0; i < mixedFrames; i++)
        {
            peak = Math.Max(peak, Math.Abs(mixed[i * 2]));
            if (Math.Abs(mixed[i * 2] - mixed[i * 2 + 1]) > 1e-5f) leftEqualsRight = false;
        }

        var emptyMixer = new AudioMixer(48000);
        var silence = new float[480 * 2];
        var silentFrames = emptyMixer.Mix(480, silence);
        var paddedSilence = silentFrames == 480 && silence.All(v => v == 0f);

        var sparseMixer = new AudioMixer(48000);
        sparseMixer.AddFrame("只有一个采样的人", new[] { 0.5f }, 1, 16000);
        var sparse = new float[480 * 2];
        var sparseFrames = sparseMixer.Mix(480, sparse);
        var tailIsSilent = sparseFrames == 480 && sparse.Skip(60).All(v => v == 0f);

        check("AudioMixer：两路同相正弦相加限幅在 [-1,1]、左右声道一致；缓冲不足补静音且返回请求帧数",
            mixedFrames == 480 && peak is > 0.01f and <= 1.0001f && leftEqualsRight && paddedSilence && tailIsSilent,
            $"峰值 {peak:0.000}，空混音 {silentFrames} 帧，稀疏 {sparseFrames} 帧");

        var removals = new AudioMixer(48000);
        removals.AddFrame("留下的", sineA, sineA.Length, 16000);
        removals.AddFrame("要走的", sineB, sineB.Length, 16000);
        removals.RemoveMember("要走的");
        var afterRemove = new float[480 * 2];
        removals.Mix(480, afterRemove);

        // 只加「要走的」再移除：之后混出来必须全是静音，证明它真的不参与混音了
        var onlyRemoved = new AudioMixer(48000);
        onlyRemoved.AddFrame("要走的", sineB, sineB.Length, 16000);
        onlyRemoved.RemoveMember("要走的");
        var afterRemovedOnly = new float[480 * 2];
        onlyRemoved.Mix(480, afterRemovedOnly);
        var removedIsSilent = afterRemovedOnly.All(v => v == 0f);

        check("AudioMixer：MixSampleRate 跟构造参数一致、RemoveMember 后该成员不再参与混音",
            removals.MixSampleRate == 48000
            && removals.Members.Contains("留下的") && !removals.Members.Contains("要走的")
            && removals.BufferedFrames("要走的") == 0 && removedIsSilent
            && AudioMixer.TargetLatencyMilliseconds > 0
            && AudioMixer.TargetLatencyMilliseconds <= AudioMixer.MaxLatencyMilliseconds,
            $"MixSampleRate={removals.MixSampleRate}，成员 {string.Join("/", removals.Members)}，移除后静音={removedIsSilent}");

        var backlog = new AudioMixer(48000);
        var chunk = new float[640];
        for (var i = 0; i < chunk.Length; i++)
            chunk[i] = 0.2f * MathF.Sin(2 * MathF.PI * 200f * i / 16000f);
        for (var i = 0; i < 125; i++) backlog.AddFrame("话多的人", chunk, chunk.Length, 16000);   // 共 5 秒
        var buffered = backlog.BufferedFrames("话多的人");
        var maxLatencyFrames = AudioMixer.MaxLatencyMilliseconds * backlog.MixSampleRate / 1000;
        check("AudioMixer：连续灌 5 秒数据后缓冲长度受 MaxLatency 约束（不会无限积压）",
            buffered > 0 && buffered <= maxLatencyFrames + 2400,
            $"缓冲 {buffered} 帧（上限约 {maxLatencyFrames} 帧 = {AudioMixer.MaxLatencyMilliseconds} ms @ {backlog.MixSampleRate} Hz）");

        // ---- JPEG：BGRA → 编码 → 解码 / 直接转 BitmapSource ----
        const int jpegWidth = 64;
        const int jpegHeight = 48;
        var bgra = FakeFramePainter.GradientBgra(jpegWidth, jpegHeight, 0);
        var jpeg = JpegFrameCodec.EncodeBgra(bgra, jpegWidth, jpegHeight, 60);
        var bitmap = JpegFrameCodec.DecodeToBitmap(jpeg);
        check("JpegFrameCodec：64×48 BGRA → EncodeBgra → DecodeToBitmap 尺寸一致且已冻结",
            jpeg.Length > 0 && bitmap.PixelWidth == jpegWidth && bitmap.PixelHeight == jpegHeight && bitmap.IsFrozen,
            $"{bitmap.PixelWidth}×{bitmap.PixelHeight}，{jpeg.Length} 字节，frozen={bitmap.IsFrozen}");

        var fromBgra = JpegFrameCodec.FromBgra(bgra, jpegWidth, jpegHeight);
        // 统一转成 Bgra32 再比像素，免得依赖实现挑的像素格式
        var normalized = new FormatConvertedBitmap(fromBgra, PixelFormats.Bgra32, null, 0);
        normalized.Freeze();
        var pixels = new byte[jpegWidth * jpegHeight * 4];
        normalized.CopyPixels(pixels, jpegWidth * 4, 0);
        var lastPixel = (jpegWidth * jpegHeight - 1) * 4;
        var cornersMatch = ChannelNear(pixels, 0, bgra, 0) && ChannelNear(pixels, 2, bgra, 2)
            && ChannelNear(pixels, lastPixel, bgra, lastPixel) && ChannelNear(pixels, lastPixel + 2, bgra, lastPixel + 2);
        check("JpegFrameCodec：FromBgra 尺寸一致、首尾像素颜色对得上（±20/通道）",
            fromBgra.PixelWidth == jpegWidth && fromBgra.PixelHeight == jpegHeight && fromBgra.IsFrozen && cornersMatch,
            $"首像素 BGR=({pixels[0]},{pixels[1]},{pixels[2]}) 源=({bgra[0]},{bgra[1]},{bgra[2]})");

        // ---- BgraDownscaler：等比缩到目标宽；比源宽大时不放大 ----
        const int sourceWidth = 1280;
        const int sourceHeight = 720;
        var bigFrame = FakeFramePainter.GradientBgra(sourceWidth, sourceHeight, 0);
        var scaled = BgraDownscaler.Downscale(bigFrame, sourceWidth, sourceHeight, 320,
            out var scaledWidth, out var scaledHeight);
        var smallFrame = FakeFramePainter.GradientBgra(160, 120, 0);
        var unchanged = BgraDownscaler.Downscale(smallFrame, 160, 120, 320,
            out var unchangedWidth, out var unchangedHeight);
        check("BgraDownscaler：1280×720 → 目标宽 320 得到 320×180 且字节数对；目标比源宽时返回原尺寸（不放大）",
            scaledWidth == 320 && scaledHeight == 180 && scaled.Length == 320 * 180 * 4
            && unchangedWidth == 160 && unchangedHeight == 120 && unchanged.Length == 160 * 120 * 4,
            $"缩到 {scaledWidth}×{scaledHeight}（{scaled.Length} 字节）｜不放大 {unchangedWidth}×{unchangedHeight}");

        // ---- CallProtocolRules：冻结的协议常量与画质档位 ----
        var saver = CallProtocolRules.VideoProfile(CallQualityPreset.Saver);
        var standard = CallProtocolRules.VideoProfile(CallQualityPreset.Standard);
        var high = CallProtocolRules.VideoProfile(CallQualityPreset.High);
        var profilesMatch = saver.Width == 240 && saver.Height == 180 && saver.Fps == 6
            && standard.Width == 320 && standard.Height == 240 && standard.Fps == 10
            && high.Width == 480 && high.Height == 360 && high.Fps == 12;
        check("CallProtocolRules：音频 640 字节 / 40 ms / 16 kHz、视频三档宽高帧率、最多 8 人、速率文本",
            CallProtocolRules.AudioBytesPerFrame == 640
            && CallProtocolRules.AudioSamplesPerFrame == 640
            && CallProtocolRules.AudioFrameMilliseconds == 40
            && CallProtocolRules.AudioSampleRate == 16000
            && CallProtocolRules.MillisecondsOf(640) == 40
            && profilesMatch
            && CallProtocolRules.MaxParticipants == 8
            && CallStats.FormatRate(0) == "0 kbps"
            && CallStats.FormatRate(500) == "500 kbps"
            && CallStats.FormatRate(1500) == "1.5 Mbps",
            $"Saver={saver.Width}×{saver.Height}@{saver.Fps}｜Standard={standard.Width}×{standard.Height}@{standard.Fps}"
            + $"｜High={high.Width}×{high.Height}@{high.Fps}");
    }

    /// <summary>两个颜色通道差不超过容差（JPEG 有损，±20/通道 足够宽松）。</summary>
    private static bool ChannelNear(byte[] a, int indexA, byte[] b, int indexB)
        => Math.Abs(a[indexA] - b[indexB]) <= 20;

    // ---------------------------------------------------------------- 群通话：协议新类型（纯逻辑）

    /// <summary>
    /// 通话的协议增量：<c>callHello</c>（媒体连接握手）/ <c>callState</c>（主机权威状态）/
    /// <c>callControl</c>（麦克风 / 摄像头开关）/ <c>callAudio</c> / <c>callVideo</c>。
    /// 断言它们都在 <see cref="ChatProtocol.Kinds"/> 里、而且经 <see cref="ChatProtocol.BuildFrame"/>
    /// 拼帧再解析能原样读回（线上是 camelCase）。
    /// </summary>
    private static void CheckCallProtocol(Action<string, bool, string> check, Action<string, bool> plain)
    {
        var callKinds = new[]
        {
            ChatProtocol.Kinds.CallHello, ChatProtocol.Kinds.CallState, ChatProtocol.Kinds.CallControl,
            ChatProtocol.Kinds.CallAudio, ChatProtocol.Kinds.CallVideo,
        };
        var describe = ChatProtocol.Describe();
        check("通话新增 5 个包类型且互不相同，协议摘要里能看到 callHello / callState / callAudio / callVideo",
            callKinds.Distinct().Count() == callKinds.Length
            && callKinds.All(k => !string.IsNullOrWhiteSpace(k))
            && describe.Contains("callHello", StringComparison.Ordinal)
            && describe.Contains("callState", StringComparison.Ordinal)
            && describe.Contains("callAudio", StringComparison.Ordinal)
            && describe.Contains("callVideo", StringComparison.Ordinal),
            string.Join(" / ", callKinds));

        var hello = ParseCallExtra<HelloInfo>(ChatProtocol.BuildFrame(
            ChatFrameHeader.New(ChatProtocol.Kinds.CallHello, "client-1",
                new HelloInfo { IsMedia = true, CallToken = "call-token-9" }),
            ReadOnlySpan<byte>.Empty));
        var welcome = ParseCallExtra<WelcomeInfo>(ChatProtocol.BuildFrame(
            ChatFrameHeader.New(ChatProtocol.Kinds.Welcome, "host-1",
                new WelcomeInfo { MediaAccepted = true, CallToken = "call-token-9" }),
            ReadOnlySpan<byte>.Empty));
        check("callHello / welcome 往返：IsMedia / CallToken / MediaAccepted 都能原样读回",
            hello is { IsMedia: true, CallToken: "call-token-9" }
            && welcome is { MediaAccepted: true, CallToken: "call-token-9" },
            $"hello isMedia={hello?.IsMedia} token={hello?.CallToken}｜welcome accepted={welcome?.MediaAccepted}");

        var state = new CallStateInfo
        {
            Active = true,
            StartedAtUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc),
            Participants =
            {
                new CallParticipantWire
                {
                    MemberId = "m-host", Name = "主机", Emoji = "🐟", IsHost = true, MicOn = true, CameraOn = false,
                },
                new CallParticipantWire
                {
                    MemberId = "m-client", Name = "客户端", Emoji = "🐻", MicOn = false, CameraOn = true,
                },
            },
        };
        var stateBack = ParseCallExtra<CallStateInfo>(ChatProtocol.BuildFrame(
            ChatFrameHeader.New(ChatProtocol.Kinds.CallState, "host-1", state), ReadOnlySpan<byte>.Empty));
        var controlBack = ParseCallExtra<CallControlInfo>(ChatProtocol.BuildFrame(
            ChatFrameHeader.New(ChatProtocol.Kinds.CallControl, "client-1",
                new CallControlInfo { MicOn = false, CameraOn = true }),
            ReadOnlySpan<byte>.Empty));
        check("callState / callControl 往返：参与者名单与麦克风 / 摄像头开关都不丢",
            stateBack is { Active: true, Participants.Count: 2 }
            && stateBack.Participants[0] is { MemberId: "m-host", Name: "主机", IsHost: true, CameraOn: false }
            && stateBack.Participants[1] is { MemberId: "m-client", MicOn: false, CameraOn: true }
            && controlBack is { MicOn: false, CameraOn: true },
            $"参与者 {stateBack?.Participants.Count} 人，control mic={controlBack?.MicOn} camera={controlBack?.CameraOn}");
    }

    /// <summary>把 <see cref="ChatProtocol.BuildFrame"/> 拼出来的帧解析回头，并反序列化它的 Extra。</summary>
    private static T? ParseCallExtra<T>(byte[] frame) where T : class
    {
        var headerLength = BitConverter.ToInt32(frame, 0);
        var header = JsonSerializer.Deserialize<ChatFrameHeader>(
            frame.AsSpan(ChatProtocol.LengthPrefixBytes, headerLength), ChatProtocol.JsonOptions);
        return header?.Extra is { } extra
            ? JsonSerializer.Deserialize<T>(extra.GetRawText(), ChatProtocol.JsonOptions)
            : null;
    }

    // ---------------------------------------------------------------- 群通话：真的开一场（回环 + 假设备）

    /// <summary>
    /// 在 127.0.0.1 上真开一局聊天室，两端都用 <see cref="FakeCallMediaFactory"/>（假摄像头 / 假麦克风 /
    /// 假扬声器）加入群通话，验证：加入 → 双向音视频 → 统计 → 麦克风 / 摄像头开关同步 → 挂断。
    /// <para>⚠ 全程不碰真实设备：自检机器可能没有摄像头，也不该亮摄像头灯 / 录环境声。</para>
    /// </summary>
    private static void CheckCallLoopback(Action<string, bool, string> check, Action<string, bool> plain)
    {
        Exception? failure = null;
        var report = new List<string>();

        var worker = Task.Run(async () =>
        {
            try { await RunCallLoopbackAsync(check, report).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
        });

        if (!worker.Wait(TimeSpan.FromSeconds(120)))
            failure = new TimeoutException("通话回环自检 120 秒没跑完（多半卡在媒体连接握手或假设备启停上）");

        check("通话回环整体跑完没有抛异常", failure is null,
            failure is null ? string.Join("；", report) : $"{failure.GetType().Name}: {failure.Message}");
    }

    private static async Task RunCallLoopbackAsync(Action<string, bool, string> check, List<string> report)
    {
        var port = FindFreePort();
        var store = new ChatSessionStore(ChatRoomConfig.ToolId);

        var host = new ChatRoomSession(ChatRoomConfig.ToolId, "通话主机", "🐟",
            new TrafficMeter(0, TrafficQuotaScope.Off), store);
        var client = new ChatRoomSession(ChatRoomConfig.ToolId, "通话客户端", "🐻",
            new TrafficMeter(0, TrafficQuotaScope.Off), store);

        // 两端各用一份假工厂：引擎拿到的摄像头 / 麦克风 / 扬声器全是假的
        var hostMedia = new FakeCallMediaFactory();
        var clientMedia = new FakeCallMediaFactory();

        var clientFramesAtHost = new ConcurrentBag<CallRawFrame>();
        var hostFramesAtClient = new ConcurrentBag<CallRawFrame>();
        var hostStats = new CallStats();
        var clientStats = new CallStats();
        var clientTexts = new ConcurrentBag<string>();

        host.CallRemoteVideoFrame += frame => clientFramesAtHost.Add(frame);
        client.CallRemoteVideoFrame += frame => hostFramesAtClient.Add(frame);
        host.CallStatsChanged += stats => Volatile.Write(ref hostStats, stats);
        client.CallStatsChanged += stats => Volatile.Write(ref clientStats, stats);
        client.MessagePosted += message => { if (!message.IsSystem && message.Text.Length > 0) clientTexts.Add(message.Text); };

        try
        {
            var startError = await host.StartHostAsync("127.0.0.1", port).ConfigureAwait(false);
            var connectError = await client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
            if (startError.Length != 0 || connectError.Length != 0)
            {
                check("通话回环：主机开房间、客户端连上", false, $"{startError} {connectError}");
                return;
            }
            await WaitAsync(() => host.ClientCount == 1, 8000).ConfigureAwait(false);

            // ① 客户端先发起通话：会另开一条媒体连接连主机同一个端口（callHello + 令牌）
            var clientJoin = await client.JoinCallAsync(
                new CallSettings { EnableVideo = true, EnableAudio = true }, clientMedia).ConfigureAwait(false);
            await WaitAsync(() => client.IsInCall
                && host.CurrentCallState.Participants.Any(p => p.MemberId == client.LocalMemberId), 8000).ConfigureAwait(false);
            var hostSawClientJoin = host.CurrentCallState.Participants.Any(p => p.MemberId == client.LocalMemberId);
            check("客户端发起群通话：媒体连接握手成功，主机在 callState 里看到它",
                clientJoin.Ok && client.IsInCall && hostSawClientJoin,
                clientJoin.Ok ? $"主机通话人数 {host.CurrentCallState.Participants.Count}" : clientJoin.Error);

            // ② 主机也加入（主机端同样传假工厂，否则引擎会去开真摄像头）
            var hostJoin = await host.JoinCallAsync(
                new CallSettings { EnableVideo = true, EnableAudio = true }, hostMedia).ConfigureAwait(false);
            await WaitAsync(() => host.IsInCall && host.ActiveCallEngine is not null && client.ActiveCallEngine is not null,
                8000).ConfigureAwait(false);
            check("主机也能加入自己房间的通话，两端通话引擎都起来了",
                hostJoin.Ok && host.IsInCall && host.ActiveCallEngine is not null && client.ActiveCallEngine is not null,
                hostJoin.Error);
            report.Add($"双方入会：主机通话 {host.CurrentCallState.Participants.Count} 人");

            // ③ 视频双向：远端帧的 MemberId 是发送方
            await WaitAsync(() => CountFramesFrom(clientFramesAtHost, client.LocalMemberId) > 0
                && CountFramesFrom(hostFramesAtClient, host.LocalMemberId) > 0, 12000).ConfigureAwait(false);
            var hostSawClientVideo = CountFramesFrom(clientFramesAtHost, client.LocalMemberId);
            var clientSawHostVideo = CountFramesFrom(hostFramesAtClient, host.LocalMemberId);
            check("视频双向通了：主机收到客户端的 JPEG 帧、客户端收到主机的帧（MemberId 对得上）",
                hostSawClientVideo > 0 && clientSawHostVideo > 0,
                $"主机收到客户端 {hostSawClientVideo} 帧｜客户端收到主机 {clientSawHostVideo} 帧");

            // ④ 音频双向 + 假扬声器在拉混音数据（播放链路真的在跑）
            await WaitAsync(() => Volatile.Read(ref hostStats).ReceivedAudioPackets > 0
                && Volatile.Read(ref clientStats).ReceivedAudioPackets > 0, 12000).ConfigureAwait(false);
            await WaitAsync(() => (hostMedia.LastSpeaker?.PulledFrames ?? 0) > 0
                && (clientMedia.LastSpeaker?.PulledFrames ?? 0) > 0, 8000).ConfigureAwait(false);
            var hostAudioPackets = Volatile.Read(ref hostStats).ReceivedAudioPackets;
            var clientAudioPackets = Volatile.Read(ref clientStats).ReceivedAudioPackets;
            var hostPulledFrames = hostMedia.LastSpeaker?.PulledFrames ?? 0;
            var clientPulledFrames = clientMedia.LastSpeaker?.PulledFrames ?? 0;
            check("音频双向通了：两端都收到对方的 μ-law 包，假扬声器在拉混音数据",
                hostAudioPackets > 0 && clientAudioPackets > 0 && hostPulledFrames > 0 && clientPulledFrames > 0,
                $"收到音频 主机 {hostAudioPackets} 包 / 客户端 {clientAudioPackets} 包｜"
                + $"扬声器拉 主机 {hostPulledFrames} 帧 / 客户端 {clientPulledFrames} 帧");

            // ⑤ 统计：两端码率都 > 0；丢帧计数不抛异常即可
            await Task.Delay(1500).ConfigureAwait(false);
            var hostSnapshot = Volatile.Read(ref hostStats);
            var clientSnapshot = Volatile.Read(ref clientStats);
            check("两端通话统计：发送 / 接收码率都 > 0，丢帧计数可读",
                hostSnapshot.SendKbps > 0 && hostSnapshot.ReceiveKbps > 0
                && clientSnapshot.SendKbps > 0 && clientSnapshot.ReceiveKbps > 0
                && hostSnapshot.DroppedVideoFrames >= 0 && clientSnapshot.DroppedVideoFrames >= 0,
                $"主机 ↑{hostSnapshot.SendKbps:0} ↓{hostSnapshot.ReceiveKbps:0} kbps｜"
                + $"客户端 ↑{clientSnapshot.SendKbps:0} ↓{clientSnapshot.ReceiveKbps:0} kbps｜"
                + $"丢帧 {clientSnapshot.DroppedVideoFrames}");

            // ⑥ 通话媒体不算进「单次文件流量上限」：聊天连接自己的流量计几乎不涨
            var meterBefore = client.Meter.SentBytes;
            var mediaBefore = clientSnapshot.SentBytes;
            await Task.Delay(2000).ConfigureAwait(false);
            var meterGrowth = client.Meter.SentBytes - meterBefore;
            var mediaGrowth = Volatile.Read(ref clientStats).SentBytes - mediaBefore;
            check("通话媒体不计入文件流量上限（聊天连接自己的 Meter 增长远小于媒体发送量）",
                mediaGrowth > 0 && meterGrowth * 8 < mediaGrowth,
                $"2 秒内媒体 ↑{mediaGrowth} 字节，聊天连接的 Meter 只涨 {meterGrowth} 字节");

            // ⑦ 麦克风 / 摄像头开关：客户端关掉后，主机的 callState 里对应状态变 false
            var engine = client.ActiveCallEngine!;
            try { engine.SetMicOn(false); } catch (Exception ex) { report.Add($"SetMicOn 抛异常：{ex.Message}"); }
            await WaitAsync(() => host.CurrentCallState.Participants.Any(
                p => p.MemberId == client.LocalMemberId && !p.MicOn), 8000).ConfigureAwait(false);
            var micOffSeen = host.CurrentCallState.Participants.Any(
                p => p.MemberId == client.LocalMemberId && !p.MicOn);
            try { engine.SetCameraOn(false); } catch (Exception ex) { report.Add($"SetCameraOn 抛异常：{ex.Message}"); }
            await WaitAsync(() => host.CurrentCallState.Participants.Any(
                p => p.MemberId == client.LocalMemberId && !p.CameraOn), 8000).ConfigureAwait(false);
            var cameraOffSeen = host.CurrentCallState.Participants.Any(
                p => p.MemberId == client.LocalMemberId && !p.CameraOn);
            check("客户端关麦克风 / 关摄像头后，主机 callState 里该成员的 MicOn / CameraOn 变成 false",
                micOffSeen && cameraOffSeen, $"MicOn=false 看到={micOffSeen}｜CameraOn=false 看到={cameraOffSeen}");

            // ⑧ 通话期间聊天仍然可用（媒体走的是另一条连接）
            await host.SendTextAsync("通话中发文字").ConfigureAwait(false);
            await WaitAsync(() => clientTexts.Contains("通话中发文字"), 8000).ConfigureAwait(false);
            check("通话期间聊天仍然可用（主机的文字客户端收到了）",
                clientTexts.Contains("通话中发文字"), string.Join(" | ", clientTexts));

            // ⑨ 挂断：客户端退出通话，主机名单里也随 callState 移除
            await client.LeaveCallAsync().ConfigureAwait(false);
            await WaitAsync(() => !client.IsInCall
                && host.CurrentCallState.Participants.All(p => p.MemberId != client.LocalMemberId), 8000).ConfigureAwait(false);
            check("客户端挂断后自己不在通话里，主机 callState 的通话名单里也移除了它",
                !client.IsInCall && host.CurrentCallState.Participants.All(p => p.MemberId != client.LocalMemberId),
                $"IsInCall={client.IsInCall}｜主机通话人数 {host.CurrentCallState.Participants.Count}");
        }
        finally
        {
            try { await host.LeaveCallAsync().ConfigureAwait(false); } catch { }
            // 兜底收掉假设备的后台线程（引擎正常会释放它们；重复释放是幂等的）
            try { await clientMedia.DisposeCreatedAsync().ConfigureAwait(false); } catch { }
            try { await hostMedia.DisposeCreatedAsync().ConfigureAwait(false); } catch { }
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await host.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>某个人的远端视频帧收到多少帧。</summary>
    private static int CountFramesFrom(ConcurrentBag<CallRawFrame> frames, string memberId)
        => frames.Count(frame => string.Equals(frame.MemberId, memberId, StringComparison.Ordinal));

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
