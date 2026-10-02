using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ChatRoom.Media;
using YuLiangToolkit.Tools.ChatRoom.Net;
using YuLiangToolkit.Tools.ChatRoom.Protocol;
using YuLiangToolkit.Tools.ChatRoom.Sessions;
using YuLiangToolkit.Tools.ChatRoom.Traffic;
using ChatSessionStore = YuLiangToolkit.Tools.ChatRoom.Sessions.ChatSessionStore;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>
/// ViewModel 的「动作」部分：开房间 / 连主机 / 发消息 / 发文件 / 关连接。
/// 单独一个 partial 是因为这一块改得最频繁，和界面数据的定义分开放，找起来快。
/// </summary>
public sealed partial class ChatRoomViewModel
{
    // ---------------------------------------------------------------- 开 / 关

    /// <summary>开房间（主机）：新建会话文件夹 + 开始监听。</summary>
    public async Task StartHostAsync()
    {
        if (IsOnline || IsBusy2) return;
        IsBusy2 = true;
        try
        {
            Cfg.LastRole = ChatRole.Host;
            Notice("正在开房间…");
            Log($"开始监听 {ListenAddress}:{ListenPort}");

            _meter = new TrafficMeter(Cfg.TrafficLimitBytes, Cfg.TrafficScope);
            _warned = false;
            _session = new ChatRoomSession(ChatRoomConfig.ToolId, Cfg.EffectiveNickname, Cfg.Emoji, _meter, _store);
            WireSession(_session);

            var error = await _session.StartHostAsync(ListenAddress, ListenPort);
            if (error.Length > 0)
            {
                Notice($"开房间失败：{error}", ChatNoticeLevel.Error);
                Log($"✗ {error}");
                await TeardownSessionAsync();
                return;
            }

            ApplyTrafficSettings();
            OnPropertyChanged(nameof(IsHost));
            // ⚠ 顺序要紧：先把 IsOnline 置起来再算摘要 —— UpdateConnectionSummary 靠 IsOnline 判断
            // “到底是没连上、还是连着但还没人进来”，反了就永远显示「未监听」（截图里踩过）。
            IsOnline = true;
            UpdateConnectionSummary();
            Cfg.RememberSession(_session.SessionName);
            RefreshHistory();
            Log($"✓ 房间已开：监听 {_session.ListenText}");
            Log($"  本次记录文件夹：{_session.SessionDirectory}");
            if (LanAddress.Length > 0)
                Log($"  告诉对方填：{LanAddress}:{_session.Host?.EndPoint?.Port ?? ListenPort}");
            else
                Log("  没检测到局域网地址（可能没连网卡）；外网请用内网穿透工具给的地址");
            AddSystemMessage($"房间已开（监听 {_session.ListenText}）。把地址告诉对方：{AdvertiseText()}");
            AddSystemMessage("对方连上后会出现在「房间成员」里。");
        }
        finally
        {
            IsBusy2 = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>连主机（客户端）：新建会话文件夹 + 连接 + 握手。</summary>
    public async Task ConnectAsync()
    {
        if (IsOnline || IsBusy2) return;

        if (string.IsNullOrWhiteSpace(HostAddress))
        {
            Notice("请先填主机地址（IPv4 / IPv6 / 域名，域名可以是内网穿透工具给的）", ChatNoticeLevel.Warn);
            return;
        }

        IsBusy2 = true;
        try
        {
            Cfg.LastRole = ChatRole.Client;
            Notice($"正在连接 {HostAddress}:{HostPort} …");
            Log($"开始连接 {HostAddress}:{HostPort}");

            _meter = new TrafficMeter(Cfg.TrafficLimitBytes, Cfg.TrafficScope);
            _warned = false;
            _session = new ChatRoomSession(ChatRoomConfig.ToolId, Cfg.EffectiveNickname, Cfg.Emoji, _meter, _store);
            WireSession(_session);

            var error = await _session.ConnectAsync(HostAddress, HostPort);
            if (error.Length > 0)
            {
                Notice($"连接失败：{error}", ChatNoticeLevel.Error);
                Log($"✗ {error}");
                await TeardownSessionAsync();
                return;
            }

            ApplyTrafficSettings();
            OnPropertyChanged(nameof(IsHost));
            // 同上：先置 IsOnline 再算摘要，否则「未连接」一直挂着
            IsOnline = true;
            UpdateConnectionSummary();
            Cfg.RememberSession(_session.SessionName);
            RefreshHistory();
            Log($"✓ 已连上 {_session.HostNameText}（{_session.PeerText}，握手 {_session.LatencyMs} ms）");
            Log($"  本次记录文件夹：{_session.SessionDirectory}");
            AddSystemMessage($"已加入 {_session.HostNameText} 的房间（{_session.PeerText}）。");
        }
        finally
        {
            IsBusy2 = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>断开 / 关房间。</summary>
    public async Task StopAsync()
    {
        if (_session is null) return;
        IsBusy2 = true;
        try
        {
            var session = _session;
            Notice("正在断开…");
            await session.StopAsync(IsHost ? "主机停止了房间" : "本端断开");
            AddSystemMessage($"已断开（本次会话：{session.SessionName}）");
            Log($"✓ 已断开。记录见 {session.SessionDirectory}");
            await TeardownSessionAsync();
        }
        finally
        {
            IsBusy2 = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task TeardownSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            try { await session.DisposeAsync(); } catch { }
        }
        IsOnline = false;
        HasUnread = false;
        MemberItems.Clear();
        UpdateMembersPlaceholder();
        UpdateConnectionSummary();
        RefreshStatus();
    }

    /// <summary>把网络层的事件接到界面上（所有界面更新都经 Ui(...) 切回 UI 线程）。</summary>
    private void WireSession(ChatRoomSession session)
    {
        session.Noticed += notice => Ui(() =>
        {
            Notice(notice.Text, notice.Level);
            Log($"[{notice.Level}] {notice.Text}");
        });

        session.MessagePosted += posted => Ui(() => PostMessage(posted));

        session.TransferChanged += transfer => Ui(() => OnTransferChanged(transfer));

        session.MembersChanged += _ => Ui(() =>
        {
            RebuildMembers();
            UpdateConnectionSummary();
        });

        session.Disconnected += error => Ui(() =>
        {
            if (error is { Length: > 0 })
            {
                Notice($"连接断了：{error}", ChatNoticeLevel.Error);
                Log($"✗ 连接断开：{error}");
                AddSystemMessage($"连接断开：{error}");
            }
            else
            {
                AddSystemMessage("连接已关闭。");
            }
            IsOnline = false;
            UpdateConnectionSummary();
        });
    }

    // ---------------------------------------------------------------- 发东西

    /// <summary>发一条文字。</summary>
    public async Task SendTextAsync()
    {
        if (_session is null || !IsOnline) return;
        var text = MessageText.Trim();
        if (text.Length == 0) return;
        MessageText = "";
        try { await _session.SendTextAsync(text); }
        catch (Exception ex) { Notice($"发送失败：{ChatLink.Describe(ex)}", ChatNoticeLevel.Error); }
    }

    /// <summary>选文件（图片 / 音频 / 视频 / 任意文件）并发出去。</summary>
    public async Task PickAndSendFilesAsync()
    {
        if (_session is null || !IsOnline) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择要发送的文件（图片 / 音频 / 视频 / 任意文件，可以多选）",
            Filter = "全部文件 (*.*)|*.*"
                     + "|图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
                     + "|音频 (*.mp3;*.wav;*.flac;*.m4a;*.ogg)|*.mp3;*.wav;*.flac;*.m4a;*.ogg"
                     + "|视频 (*.mp4;*.mkv;*.avi;*.mov;*.webm)|*.mp4;*.mkv;*.avi;*.mov;*.webm",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        await SendFilesAsync(dialog.FileNames);
    }

    /// <summary>只选图片（会自动按设置缩小，省流量）。</summary>
    public async Task PickAndSendImagesAsync()
    {
        if (_session is null || !IsOnline) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择要发送的图片（会自动按「发图压缩」设置缩小）",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|全部文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        await SendFilesAsync(dialog.FileNames);
    }

    /// <summary>把一批文件发出去（图片按设置先压缩；超过确认阈值先问一句）。</summary>
    public async Task SendFilesAsync(IEnumerable<string> paths)
    {
        var session = _session;
        if (session is null || !IsOnline) return;

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var prepared = path;
            var cleanup = "";

            try
            {
                var kind = ChatSessionNaming.KindOfFile(path);
                var size = new FileInfo(path).Length;

                // 大文件先确认（避免手一滑把几个 G 传出去，流量额度瞬间见底）
                if (Cfg.ConfirmFileBytes > 0 && size > Cfg.ConfirmFileBytes)
                {
                    if (!Confirm($"「{Path.GetFileName(path)}」有 {FileSize.Format(size)}，" +
                                 $"本次剩余额度 {FileSize.Format(_meter?.RemainingBytes ?? long.MaxValue)}。\n确定要发送吗？",
                            "确认发送大文件"))
                    {
                        AddSystemMessage($"已取消发送：{Path.GetFileName(path)}（{FileSize.Format(size)}）");
                        continue;
                    }
                }

                // 图片：按设置生成一份压缩副本再发（原图留在本机，记录里也能点开）
                if (kind == ChatContentKind.Image && Cfg.EffectiveImageMaxEdge > 0)
                {
                    IsBusy2 = true;
                    Notice("正在压缩图片…");
                    var temp = Path.Combine(Path.GetTempPath(), $"ylt-chat-{Guid.NewGuid():N}{Path.GetExtension(path)}");
                    var made = await Task.Run(() => ImageUtil.CreateThumbnail(path, temp,
                        Cfg.EffectiveImageMaxEdge, Cfg.ImageJpegQuality));
                    if (made is not null && File.Exists(made) && new FileInfo(made).Length > 0
                        && new FileInfo(made).Length < size)
                    {
                        prepared = made;
                        cleanup = made;
                        Log($"图片已压缩：{Path.GetFileName(path)} {FileSize.Format(size)} → {FileSize.Format(new FileInfo(made).Length)}（{Cfg.ImageQualityText}）");
                    }
                    else
                    {
                        Log($"图片未压缩（原图 {FileSize.Format(size)}，压不小），按原样发送：{Path.GetFileName(path)}");
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    }
                    IsBusy2 = false;
                    Notice("");
                }

                Notice($"正在发送 {Path.GetFileName(prepared)} …");
                await session.SendFileAsync(prepared, TransferNote);
                TransferNote = "";
            }
            catch (Exception ex)
            {
                Notice($"发送失败：{ChatLink.Describe(ex)}", ChatNoticeLevel.Error);
                Log($"✗ 发送 {Path.GetFileName(path)} 失败：{ChatLink.Describe(ex)}");
            }
            finally
            {
                IsBusy2 = false;
                if (cleanup.Length > 0)
                {
                    try { if (File.Exists(cleanup)) File.Delete(cleanup); } catch { }
                }
            }
        }
        RefreshStatus();
    }

    /// <summary>测一次延迟（客户端）。</summary>
    public async Task PingAsync()
    {
        if (_session is null || !IsOnline) return;
        await _session.PingAsync();
        await Task.Delay(120);
        Notice(_session.LatencyMs > 0 ? $"延迟约 {_session.LatencyMs} ms" : "已发送心跳，稍等一下看延迟");
        RefreshStatus();
    }

    /// <summary>把一个客户端移出房间（主机）。</summary>
    public async Task KickAsync(ChatMemberItem? item)
    {
        if (_session is null || item is null || item.IsSelf) return;
        if (!Confirm($"把「{item.Member.Name}」移出房间？", "移出成员")) return;
        await _session.KickAsync(item.Member.MemberId);
        Log($"已把 {item.Member.Name} 移出房间");
    }

    /// <summary>清空房间（主机把所有人请出去）。</summary>
    public async Task KickAllAsync()
    {
        if (_session is null || !IsOnline) return;
        if (!Confirm("把所有客户端都移出房间？", "清空房间")) return;
        await _session.KickAllAsync();
        Log("已清空房间");
    }

    // ---------------------------------------------------------------- 界面数据维护

    /// <summary>把网络层的一条消息显示到界面上。</summary>
    private void PostMessage(ChatRoomSession.ChatMessageNotification posted)
    {
        var item = ChatMessageItem.Create(
            posted.MessageId, posted.SenderId, posted.SenderName, posted.SenderEmoji, posted.IsHost, posted.IsMine,
            posted.SentLocal, posted.Text, posted.ContentKind, posted.File, false);
        item.LocalPath = posted.LocalPath;
        item.PreviewPath = posted.PreviewPath;
        item.IsSystem = posted.IsSystem;

        AppendMessage(item);
        if (!posted.IsMine && !posted.IsSystem) HasUnread = true;
    }

    private void AddSystemMessage(string text)
        => AppendMessage(ChatMessageItem.FromSystem(text, DateTime.Now));

    private void AppendMessage(ChatMessageItem item)
    {
        // 同一条消息重复到达（例如预览图补发）就更新那一条，不要又加一行
        var existing = Messages.FirstOrDefault(m => m.MessageId == item.MessageId && m.MessageId.Length > 0);
        if (existing is not null)
        {
            if (item.LocalPath is { Length: > 0 }) existing.LocalPath = item.LocalPath;
            if (item.PreviewPath is { Length: > 0 }) existing.PreviewPath = item.PreviewPath;
            return;
        }

        Messages.Add(item);
        while (Messages.Count > Math.Max(50, Cfg.MaxMessageItems)) Messages.RemoveAt(0);
        MessagesChanged?.Invoke();
    }

    /// <summary>消息列表变了（界面滚到底用）。</summary>
    public event Action? MessagesChanged;

    /// <summary>文件传输状态变化：更新 / 新建那条消息里的进度。</summary>
    private void OnTransferChanged(ChatRoomSession.TransferNotification transfer)
    {
        // 进度对象已经在消息里引用了；这里只管“第一次见到它时建一条消息”
        if (transfer.NewMessage is { } payload)
        {
            var item = ChatMessageItem.Create(
                payload.MessageId, payload.SenderId, payload.SenderName, payload.SenderEmoji, payload.IsHost,
                payload.IsMine, payload.SentLocal, payload.Text, transfer.Kind, transfer.Progress, false);
            item.LocalPath = transfer.LocalPath;
            item.PreviewPath = transfer.PreviewPath;
            AppendMessage(item);
            if (!payload.IsMine) HasUnread = true;
        }
        else
        {
            // 只更新进度：同一条消息里已经引用了同一个 FileProgress 对象，界面自己会刷新
            var existing = Messages.FirstOrDefault(m => m.File is not null && m.File.TransferId == transfer.TransferId);
            if (existing is not null)
            {
                if (transfer.LocalPath is { Length: > 0 }) existing.LocalPath = transfer.LocalPath;
                if (transfer.PreviewPath is { Length: > 0 }) existing.PreviewPath = transfer.PreviewPath;
            }
        }

        if (!Transfers.Any(t => ReferenceEquals(t, transfer.Progress)))
            Transfers.Insert(0, transfer.Progress);

        while (Transfers.Count > 200) Transfers.RemoveAt(Transfers.Count - 1);

        if (transfer.Error is { Length: > 0 })
            Log($"✗ [{transfer.Progress.DirectionText}] {transfer.FileName}：{transfer.Error}");
        else if (transfer.Progress.State == TransferState.Done)
            Log($"✓ [{transfer.Progress.DirectionText}] {transfer.FileName}（{FileSize.Format(transfer.SizeBytes)}）");

        UpdateTransferSummary();
        RefreshStatus();
    }

    /// <summary>传输列表摘要（收 / 发各一行）。</summary>
    private void UpdateTransferSummary()
    {
        var sent = Transfers.Where(t => t.Outgoing).ToList();
        var received = Transfers.Where(t => !t.Outgoing).ToList();
        UploadSummary = Summarize(sent, "还没有发送过文件");
        DownloadSummary = Summarize(received, "还没有收到过文件");

        static string Summarize(List<FileProgress> list, string empty)
        {
            if (list.Count == 0) return empty;
            var done = list.Count(t => t.State == TransferState.Done);
            var bytes = list.Where(t => t.State == TransferState.Done).Sum(t => t.TotalBytes);
            return $"共 {list.Count} 个（完成 {done} 个，{FileSize.Format(bytes)}）";
        }
    }

    /// <summary>刷新成员列表（保持“离开的人灰掉但不消失”）。</summary>
    private void RebuildMembers()
    {
        var session = _session;
        if (session is null)
        {
            MemberItems.Clear();
            UpdateMembersPlaceholder();
            return;
        }

        var wanted = session.Members;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in wanted)
        {
            seen.Add(member.MemberId);
            var existing = MemberItems.FirstOrDefault(m => m.Member.MemberId == member.MemberId);
            if (existing is null)
            {
                MemberItems.Add(new ChatMemberItem(member.Clone(), member.MemberId == session.LocalMemberId)
                {
                    CanKick = session.IsHost && member.MemberId != session.LocalMemberId,
                });
            }
            else
            {
                existing.IsOnline = true;
                existing.Member.Name = member.Name;
                existing.Member.Emoji = member.Emoji;
                existing.Member.Endpoint = member.Endpoint;
                existing.Refresh();
            }
        }

        foreach (var item in MemberItems)
            if (!seen.Contains(item.Member.MemberId)) item.IsOnline = false;
    }

    private void UpdateMembersPlaceholder()
    {
        RaiseOnPropertyChanged(nameof(MemberListSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>成员列表下方的一行统计。</summary>
    public string MemberListSummary
    {
        get
        {
            if (_session is null) return "未连接：没有成员";
            if (_session.IsHost)
                return $"主机 1 人 + 客户端 {_session.ClientCount} 人（上限 {(MaxClients > 0 ? MaxClients.ToString() : "不限")}）";
            return $"房间共 {Math.Max(1, _session.Members.Count)} 人";
        }
    }

    private void UpdateConnectionSummary()
    {
        var session = _session;
        if (session is null || !IsOnline)
        {
            PeerSummary = "未连接";
            ListenSummary = IsHost && Cfg.LastRole == ChatRole.Host ? "未监听" : "";
            RaiseOnPropertyChanged(nameof(MemberListSummary));
            return;
        }

        PeerSummary = session.IsHost
            ? $"我是主机　监听 {session.ListenText}　已连 {session.ClientCount} 人"
            : $"已连主机 {session.HostNameText}　{session.PeerText}　延迟 {session.LatencyMs} ms";

        ListenSummary = session.IsHost
            ? $"告诉对方：{AdvertiseText()}"
            : "（客户端不需要监听）";
        RaiseOnPropertyChanged(nameof(MemberListSummary));
    }

    /// <summary>给对方填的地址（主机端显示）。</summary>
    public string AdvertiseText()
    {
        var port = _session?.Host?.EndPoint?.Port ?? ListenPort;
        var address = ListenAddress is "0.0.0.0" or "::" or "[::]"
            ? (LanAddress.Length > 0 ? LanAddress : "（用 ipconfig 查本机 IPv4）")
            : ListenAddress.Trim('[', ']');
        return $"{address}:{port}";
    }

    private void RefreshStatus()
    {
        var snapshot = _meter?.Snapshot() ?? default;
        Traffic = snapshot;

        if (IsOnline && _session is not null)
        {
            if (_session.IsHost)
            {
                PeerSummary = $"我是主机　监听 {_session.ListenText}　已连 {_session.ClientCount} 人";
                if (_session.ClientCount != MemberItems.Count(m => m.IsOnline && !m.IsSelf))
                    RebuildMembers();
            }
            else
            {
                PeerSummary = $"已连主机 {_session.HostNameText}　{_session.PeerText}　延迟 {_session.LatencyMs} ms";
            }
        }

        // 剩余额度提醒（每次触到阈值只提醒一次）
        if (_meter is not null && Cfg.TrafficWarnBytes > 0 && _meter.RemainingBytes != long.MaxValue
            && _meter.RemainingBytes <= Cfg.TrafficWarnBytes && !_warned)
        {
            _warned = true;
            Notice($"注意：本次流量只剩 {FileSize.Format(_meter.RemainingBytes)} 了", ChatNoticeLevel.Warn);
            AddSystemMessage($"⚠ 本次流量只剩 {FileSize.Format(_meter.RemainingBytes)}");
        }

        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(StatusBadgeText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyTrafficSettings()
    {
        _meter?.SetLimit(Cfg.TrafficLimitBytes, Cfg.TrafficScope);
        _warned = false;
        Traffic = _meter?.Snapshot() ?? default;
    }

    private void OnConfigChanged()
    {
        OnPropertyChanged(nameof(SelfDisplay));
        OnPropertyChanged(nameof(ImageQualityHint));
        ApplyTrafficSettings();
    }

    private void Notice(string text, ChatNoticeLevel level = ChatNoticeLevel.Info)
    {
        NoticeText = text;
        ActivityText = $"{DateTime.Now:HH:mm:ss}　{text}";
    }

    private void RaiseOnPropertyChanged(string name) => OnPropertyChanged(name);
}
