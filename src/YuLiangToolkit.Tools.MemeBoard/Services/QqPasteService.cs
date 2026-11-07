using System.Windows.Automation;

namespace YuLiangToolkit.Tools.MemeBoard.Services;

/// <summary>一次「粘贴到 QQ」操作的结果（给 ViewModel 写日志 / 状态栏用）。</summary>
/// <param name="Pasted">图片是否确认进入了 QQ 输入框。</param>
/// <param name="Sent">是否确认已发送（输入框回到空状态）。</param>
/// <param name="Message">给用户看的一句话说明。</param>
public sealed record QqPasteResult(bool Pasted, bool Sent, string Message)
{
    /// <summary>粘贴成功（不代表已发送）。</summary>
    public bool Success => Pasted;
}

/// <summary>
/// 「复制到剪贴板之后」的 QQ 自动化流程：置顶聚焦 → 校验目标会话 → 聚焦输入框 → Ctrl+V →
/// 确认图片进了输入框 →（可选）随机延迟后自动发送 → 确认输入框清空。
///
/// <para><b>安全设计</b>：任何一步不满足就<b>中止并返回原因</b>，绝不“猜着发”；图片始终留在剪贴板，
/// 用户随时可以手动 Ctrl+V。自动发送前会再确认前台仍是同一个 QQ 窗口。</para>
///
/// <para><b>线程</b>：本方法内部全是 Win32 / UIA 调用，<b>不要</b>在 UI 线程直接调用（ViewModel 用 Task.Run）。</para>
/// </summary>
public static class QqPasteService
{
    /// <summary>粘贴后确认图片进入输入框的最长等待（毫秒）。</summary>
    public const int PasteConfirmTimeoutMs = 2500;

    /// <summary>发送后确认输入框清空的最长等待（毫秒）。</summary>
    public const int SendConfirmTimeoutMs = 3000;

    /// <summary>
    /// 把剪贴板里的图片粘贴到目标 QQ 窗口（剪贴板内容由调用方在 UI 线程先设好）。
    /// </summary>
    /// <param name="target">目标 QQ 窗口（<c>ChatTargetMatcher.Resolve</c> 的结果）。</param>
    /// <param name="expectedChat">目标会话名（用于校验）。</param>
    /// <param name="verifyTargetChat">是否在粘贴前校验当前会话就是目标会话。</param>
    /// <param name="autoSend">粘贴确认后是否自动发送。</param>
    /// <param name="sendDelayMinMs">自动发送前随机等待下限（毫秒）。</param>
    /// <param name="sendDelayMaxMs">自动发送前随机等待上限（毫秒）。</param>
    /// <param name="log">过程日志回调（可空；ViewModel 传 base.Log）。</param>
    public static QqPasteResult Paste(
        QqWindowInfo target,
        string expectedChat,
        bool verifyTargetChat,
        bool autoSend,
        int sendDelayMinMs,
        int sendDelayMaxMs,
        Action<string>? log = null)
    {
        if (target is null) return new QqPasteResult(false, false, "没有目标窗口");
        if (target.Handle == IntPtr.Zero) return new QqPasteResult(false, false, "目标窗口句柄无效");

        void Log(string line) => log?.Invoke(line);

        // ① 置顶并聚焦 QQ
        Log($"正在把 QQ 窗口切到前台：{target.DisplayText}");
        if (!QqWindowService.FocusWindow(target.Handle))
            return new QqPasteResult(false, false, "无法把 QQ 窗口切到前台（可能被别的程序挡住），图片仍在剪贴板");

        // ② 校验目标会话：当前会话不是目标就中止（图片已复制好，用户可以手动粘贴）
        if (verifyTargetChat && !string.IsNullOrWhiteSpace(expectedChat))
        {
            var actual = QqWindowService.ReadChatName(target.Handle);
            if (!ChatTargetMatcher.IsMatch(actual, expectedChat))
            {
                var actualText = string.IsNullOrWhiteSpace(actual) ? "未识别" : actual;
                Log($"会话校验未通过：当前「{actualText}」，目标「{expectedChat}」");
                return new QqPasteResult(
                    false,
                    false,
                    $"当前会话是「{actualText}」，与目标「{expectedChat}」不一致，已中止（图片仍在剪贴板）");
            }
            Log($"会话校验通过：{actual}");
        }

        // ③ 聚焦聊天输入框
        Log("正在聚焦聊天输入框…");
        if (!QqWindowService.FocusEditor(target.Handle))
            return new QqPasteResult(false, false, "没找到 QQ 聊天输入框（请确认聊天窗口已打开），图片仍在剪贴板");

        // ④ Ctrl+V 粘贴
        Log("正在发送 Ctrl+V 粘贴…");
        if (!QqWindowService.SendCtrlV())
            return new QqPasteResult(false, false, "粘贴快捷键发送失败，图片仍在剪贴板");

        // ⑤ 确认图片进入输入框（输入框标题不再带 is-empty）
        if (!WaitForEditorState(target.Handle, targetEmpty: false, PasteConfirmTimeoutMs))
            return new QqPasteResult(false, false, "图片已复制到剪贴板，但没确认进入 QQ 输入框（可在 QQ 里手动 Ctrl+V）");

        Log("图片已经进入 QQ 输入框");

        // ⑥ 不允许自动发送就直接结束
        if (!autoSend)
            return new QqPasteResult(true, false, "已粘贴到 QQ 输入框（自动发送已关闭）");

        // ⑦ 随机延迟（0.5~1.5 秒可调），等待期间不碰 QQ
        var delay = MemeTiming.PickDelayMs(sendDelayMinMs, sendDelayMaxMs, Random.Shared);
        Log($"随机等待 {delay / 1000.0:0.0} 秒后自动发送…");
        var delayDeadline = Environment.TickCount64 + delay;
        while (Environment.TickCount64 < delayDeadline) Thread.Sleep(50);

        // ⑧ 发送前再确认前台仍是同一个 QQ 窗口（防止用户切走了还乱发）
        if (!QqWindowService.IsWindowForeground(target.Handle))
        {
            Log("前台已经不是 QQ 窗口，取消自动发送");
            return new QqPasteResult(true, false, "已粘贴到 QQ 输入框，但前台已经不是 QQ 窗口，未自动发送");
        }

        // ⑨ 发送：优先点 send-msg 按钮，失败则聚焦输入框按回车
        Log("正在自动发送…");
        if (!QqWindowService.TryInvokeSendButton(target.Handle))
        {
            Log("发送按钮不可用，改用回车发送…");
            QqWindowService.FocusEditor(target.Handle);
            if (!QqWindowService.SendEnter())
                return new QqPasteResult(true, false, "已粘贴，但发送失败（回车键没发出去），请手动发送");
        }

        // ⑩ 确认发送成功（输入框回到空状态）
        if (WaitForEditorState(target.Handle, targetEmpty: true, SendConfirmTimeoutMs))
        {
            Log("已自动发送");
            return new QqPasteResult(true, true, "已粘贴并自动发送");
        }

        return new QqPasteResult(true, false, "已粘贴并触发发送，但没确认消息发出（请看一眼 QQ）");
    }

    /// <summary>
    /// 轮询输入框状态，直到「空 / 非空」变成期望值或超时。
    /// 元素失效（UIA 重新生成输入框）时会重新查找。
    /// </summary>
    private static bool WaitForEditorState(IntPtr handle, bool targetEmpty, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        AutomationElement? editor = null;

        while (Environment.TickCount64 <= deadline)
        {
            editor ??= QqWindowService.FindEditor(handle);
            if (editor is not null)
            {
                var empty = QqWindowService.TryGetEditorEmpty(editor);
                if (empty is null) editor = null;          // 元素失效：下次重新找
                else if (empty.Value == targetEmpty) return true;
            }
            Thread.Sleep(80);
        }

        // 最后再量一次，避免刚好在超时边界上完成
        editor ??= QqWindowService.FindEditor(handle);
        var finalState = QqWindowService.TryGetEditorEmpty(editor);
        return finalState == targetEmpty;
    }
}
