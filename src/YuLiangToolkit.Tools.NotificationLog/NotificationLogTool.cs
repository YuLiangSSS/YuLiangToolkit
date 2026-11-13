using System.Windows;
using YuLiangToolkit.Core.Notifications;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.NotificationLog.ViewModels;
using YuLiangToolkit.Tools.NotificationLog.Views;

namespace YuLiangToolkit.Tools.NotificationLog;

/// <summary>
/// 「通知管理」工具入口：看所有工具发过的通知、筛选、逐条删除、清空。
///
/// <para><b>扩展性</b>：它只认 Core 的 <see cref="NotificationHistory"/>。
/// 以后新加的工具只要照常调 <see cref="NotificationCenter.Show"/>，通知就自动进历史、
/// 自动出现在来源筛选里 —— 本工具一行代码都不用改。</para>
/// </summary>
public sealed class NotificationLogTool : ITool
{
    public string Id => NotificationLogConfig.ToolId;

    public string Name => "通知管理";

    public string Description =>
        "记录所有工具发过的通知（快捷操作、秒表、未来更多工具），可按来源与类型筛选、"
        + "搜关键词、逐条删除或一键清空；可设最多保留多少条、是否重启后还在。";

    public string EmojiIcon => "🔔";

    public string Category => "系统";

    public IReadOnlyList<string> CategoryPath => new[] { "系统" };

    public IReadOnlyList<string> Tags => new[]
    {
        "通知", "通知历史", "通知中心", "提醒", "notification", "日志",
    };

    /// <summary>排在系统分类里秒表 (170) 之后。</summary>
    public int Order => 172;

    public FrameworkElement CreateView()
    {
        // 用外壳注入的那一份历史（App 启动时设给 NotificationCenter.History）。
        // 极端情况下（自检 / 没有外壳）没有注入，就临时建一份空的，界面照样能开。
        var history = NotificationCenter.History ?? new NotificationHistory();
        var viewModel = new NotificationLogViewModel(history);
        return new NotificationLogView { DataContext = viewModel };
    }
}
