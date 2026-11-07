using YuLiangToolkit.Tools.TaskManager.Native;

namespace YuLiangToolkit.Tools.TaskManager.Models;

/// <summary>结束任务的「第几级后备方案」（与任务管理器截图里的三级方案一致）。</summary>
public enum KillStage
{
    /// <summary>① <c>Process.Kill()</c>：最直接，但对管理员权限进程常失败。</summary>
    ProcessKill = 1,

    /// <summary>② <c>taskkill /PID {pid} /T /F</c>：/T 会连子进程树一起结束。</summary>
    Taskkill = 2,

    /// <summary>③ <c>wmic process where name='…' delete</c>：最后的兜底（新系统可能已移除 wmic）。</summary>
    Wmic = 3,
}

/// <summary>一次结束尝试的结果（成功 / 失败 + 说明）。</summary>
public sealed class KillAttempt
{
    public KillAttempt(KillStage stage, bool success, string message)
    {
        Stage = stage;
        Success = success;
        Message = message;
    }

    public KillStage Stage { get; }

    public bool Success { get; }

    /// <summary>人类可读的结果（例如「拒绝访问」「进程不存在」「退出码 0」）。</summary>
    public string Message { get; }

    public string StageText => KillPlan.StageName(Stage);

    public override string ToString() => $"{StageText}：{(Success ? "成功" : "失败")}（{Message}）";
}

/// <summary>
/// 结束任务的最终结果：三级方案按顺序尝试，第一级成功就停。
/// 界面据 <see cref="Success"/> 决定提示，据 <see cref="Attempts"/> 展示「试过哪些办法、分别为什么失败」。
/// </summary>
public sealed class KillOutcome
{
    public KillOutcome(int pid, bool success, IReadOnlyList<KillAttempt> attempts, string summary)
    {
        Pid = pid;
        Success = success;
        Attempts = attempts;
        Summary = summary;
    }

    public int Pid { get; }

    public bool Success { get; }

    public IReadOnlyList<KillAttempt> Attempts { get; }

    /// <summary>给状态栏 / 弹窗的一句话总结。</summary>
    public string Summary { get; }

    /// <summary>失败时建议用户怎么做（例如「以管理员身份重启本程序后再试」）。</summary>
    public static KillOutcome AlreadyExited(int pid)
        => new(pid, true, Array.Empty<KillAttempt>(), "进程已经退出，无需结束。");

    /// <summary>把一次成功 / 失败尝试整理成结果：失败时按「拒绝访问」给建议。</summary>
    public static KillOutcome FromAttempts(int pid, IReadOnlyList<KillAttempt> attempts)
    {
        var success = attempts.Count > 0 && attempts[^1].Success;
        var summary = success
            ? $"已结束 PID {pid}（{attempts[^1].StageText}）。"
            : $"结束 PID {pid} 失败：{attempts[^1].Message}。可以试试「以管理员身份运行」本程序后再操作。";
        return new KillOutcome(pid, success, attempts, summary);
    }
}
