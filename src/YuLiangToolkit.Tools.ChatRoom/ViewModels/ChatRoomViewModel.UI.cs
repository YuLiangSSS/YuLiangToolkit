using System.Diagnostics;
using System.Windows;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ChatRoom.Net;
using ChatSessionStore = YuLiangToolkit.Tools.ChatRoom.Sessions.ChatSessionStore;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>
/// ViewModel 的「小动作」部分：打开文件 / 打开文件夹 / 复制地址 / 历史会话 / 表情……
/// 这些命令都只有几行，但数量多，单独放一个 partial 让主文件保持可读。
/// </summary>
public sealed partial class ChatRoomViewModel
{
    /// <summary>打开消息里的文件（收到的在会话文件夹里，发出的指向原位置）。</summary>
    private void OpenFile(ChatMessageItem? item)
    {
        if (item?.LocalPath is not { Length: > 0 } path)
        {
            Notice("这条消息没有可打开的本机文件（可能是历史记录里的旧路径）", ChatNoticeLevel.Warn);
            return;
        }
        if (!File.Exists(path))
        {
            Notice($"文件已经不在了：{path}", ChatNoticeLevel.Warn);
            return;
        }
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { Notice($"打不开：{ex.Message}", ChatNoticeLevel.Error); }
    }

    /// <summary>在资源管理器里定位到那个文件（界面代码后置也会调，所以是 public）。</summary>
    public void RevealFile(ChatMessageItem? item)
    {
        if (item is null) return;
        if (item.RelativePath.Length > 0 && _session is not null)
        {
            if (_store.RevealInExplorer(_session.SessionName, item.RelativePath)) return;
        }
        if (item.LocalPath is { Length: > 0 } path && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }
            catch { }
        }
        Notice("找不到这个文件的位置", ChatNoticeLevel.Warn);
    }

    /// <summary>打开传输列表里那一条对应的文件（收到的文件 / 自己发出的原件）。</summary>
    private void OpenTransfer(FileProgress? progress)
    {
        if (progress is null) return;
        var item = Messages.FirstOrDefault(m => m.File is not null && m.File.TransferId == progress.TransferId);
        if (item is null)
        {
            Notice("这次传输已经不在当前列表里了（可能已经清空聊天）", ChatNoticeLevel.Warn);
            return;
        }
        OpenFile(item);
    }

    /// <summary>把「对方该填的地址」复制到剪贴板。</summary>
    private void CopyAddress()
    {
        var text = IsHost && IsOnline ? AdvertiseText() : $"{HostAddress}:{HostPort}";
        try
        {
            Clipboard.SetText(text);
            Notice($"已复制：{text}");
        }
        catch (Exception ex)
        {
            Notice($"复制失败（剪贴板被占用？）：{ex.Message}", ChatNoticeLevel.Warn);
        }
    }

    /// <summary>清空聊天列表（不动磁盘上的记录文件）。</summary>
    private void ClearChat()
    {
        if (Messages.Count == 0) return;
        if (!Confirm($"清空界面上的 {Messages.Count} 条消息？\n（磁盘上的聊天记录不会被删，仍在会话文件夹里）", "清空聊天")) return;
        Messages.Clear();
        HasUnread = false;
        MessagesChanged?.Invoke();
    }

    /// <summary>打开本次会话的记录文件夹。</summary>
    private void OpenSessionFolder()
    {
        if (_session is null || _session.SessionName.Length == 0)
        {
            _store.OpenInExplorer();
            Notice($"还没有开始连接，先打开了根目录：{_store.RootDirectory}");
            return;
        }
        _store.OpenInExplorer(_session.SessionName);
        Notice($"已打开 {_session.SessionDirectory}");
    }

    /// <summary>打开协议说明文件。</summary>
    private void OpenProtocolDoc()
    {
        try
        {
            if (File.Exists(ProtocolDocPath))
                Process.Start(new ProcessStartInfo { FileName = ProtocolDocPath, UseShellExecute = true });
            else
                MessageBox.Show(ProtocolDescriptor(), "聊天室协议", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show(ProtocolDescriptor(), "聊天室协议", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>协议摘要（说明文件打不开时的兜底显示）。</summary>
    public string ProtocolDescriptor() => Protocol.ChatProtocol.Describe();

    /// <summary>刷新历史会话列表（按时间倒序）。</summary>
    private void RefreshHistory()
    {
        History.Clear();
        foreach (var info in _store.ListSessions()) History.Add(info);
        HistorySummary = History.Count == 0
            ? "还没有历史记录（每次开始连接都会在 Data\\chat-room 下新建一个以时间命名的文件夹）"
            : $"共 {History.Count} 次记录，最近一次：{History[0].StartedAt:yyyy-MM-dd HH:mm:ss}";
    }

    /// <summary>打开某次历史会话的文件夹。</summary>
    private void OpenHistoryFolder(ChatSessionStore.ChatSessionInfo? info)
    {
        if (info is null) { _store.OpenInExplorer(); return; }
        _store.OpenInExplorer(info.SessionName);
    }

    /// <summary>把某次历史会话读回界面（看以前聊了什么）。</summary>
    private void OpenHistory(ChatSessionStore.ChatSessionInfo? info)
    {
        if (info is null) return;
        var entries = _store.ReadLog(info.SessionName);
        if (entries.Count == 0)
        {
            Notice($"{info.SessionName} 里没有聊天记录", ChatNoticeLevel.Warn);
            return;
        }

        if (!Confirm($"把 {info.SessionName} 的 {entries.Count} 条记录读到界面上？\n" +
                     "（会先清空当前列表；当前正在聊的内容不受影响，记录文件也不会动）", "查看历史记录"))
            return;

        Messages.Clear();
        foreach (var entry in entries)
        {
            var item = ChatMessageItem.FromLogEntry(entry, Cfg.EffectiveNickname);
            // 历史记录里的文件路径按那次会话的文件夹拼回来
            if (entry.RelativePath is { Length: > 0 } relative)
                item.LocalPath = _store.FullPath(info.SessionName, relative);
            if (entry.PreviewPath is { Length: > 0 } preview)
                item.PreviewPath = _store.FullPath(info.SessionName, preview);
            Messages.Add(item);
        }
        MessagesChanged?.Invoke();
        Notice($"已载入历史记录：{info.SessionName}（{entries.Count} 条）");
        AddSystemMessage($"—— 以上是 {info.SessionName} 的记录（{info.StartedAt:yyyy-MM-dd HH:mm:ss}，{info.RoleText}）——");
    }

    /// <summary>把一个 emoji 插到输入框末尾。</summary>
    private void InsertEmoji(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        MessageText += emoji;
    }

    /// <summary>用系统默认程序打开一个网址（协议说明里的链接等）。</summary>
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Notice($"打不开 {url}：{ex.Message}", ChatNoticeLevel.Warn); }
    }

    /// <summary>界面代码后置需要提示一句时用（例如播放失败）。</summary>
    public void NoticeExternal(string text) => Notice(text, ChatNoticeLevel.Warn);

    /// <summary>工具被切走时：断开连接并落盘（在后台线程里断，别卡住界面）。</summary>
    protected override void OnClosed()
    {
        try { _statusTimer.Stop(); } catch { }
        var session = _session;
        _session = null;
        if (session is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await session.StopAsync("工具关闭"); } catch { }
                try { await session.DisposeAsync(); } catch { }
            });
        }
        IsOnline = false;
        base.OnClosed();
    }
}
