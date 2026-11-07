using YuLiangToolkit.Tools.TaskManager.Models;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 结束任务的「三级后备」规则（纯逻辑，自检可以直接断言；真正调 <c>Process.Kill</c> /
/// <c>taskkill</c> / <c>wmic</c> 的是 <see cref="ProcessTerminator"/>）。
///
/// <para>顺序固定：① <c>Process.Kill()</c> → ② <c>taskkill /PID {pid} /T /F</c> →
/// ③ <c>wmic process where name='…' delete</c>。前一级成功就停，不继续往下试。</para>
///
/// <para>系统关键进程（csrss / winlogon 等）受保护，任何常规方式都会「拒绝访问」，
/// 调用方应当先拦下并提示，而不是直接崩掉。</para>
/// </summary>
public static class KillPlan
{
    /// <summary>固定顺序的三级方案。</summary>
    public static IReadOnlyList<KillStage> Stages { get; } = new[] { KillStage.ProcessKill, KillStage.Taskkill, KillStage.Wmic };

    /// <summary>第 i 级（0 基）的展示名，越界返回空串。</summary>
    public static string StageName(KillStage stage) => stage switch
    {
        KillStage.ProcessKill => "① Process.Kill()",
        KillStage.Taskkill => "② taskkill /T /F",
        KillStage.Wmic => "③ wmic delete",
        _ => "未知方案",
    };

    /// <summary>给用户看的说明（为什么要这么麻烦）。</summary>
    public static string StageDescription(KillStage stage) => stage switch
    {
        KillStage.ProcessKill => "最直接的方式；对以管理员权限启动的进程往往失败。",
        KillStage.Taskkill => "调用系统 taskkill，/T 会连子进程树一起结束。",
        KillStage.Wmic => "最后的兜底；Windows 11 24H2 起部分系统已移除 wmic。",
        _ => "",
    };

    /// <summary>>下一级是否还要尝试：上一级失败才继续。</summary>
    public static bool ShouldContinue(bool previousSucceeded) => !previousSucceeded;

    /// <summary>taskkill 的参数（一个字符串版本，日志 / 自检用）。</summary>
    public static string BuildTaskkillArguments(int pid) => $"/PID {pid} /T /F";

    /// <summary>taskkill 的参数列表（真正启动进程时用 <c>ArgumentList</c>，避免引号问题）。</summary>
    public static IReadOnlyList<string> BuildTaskkillArgumentList(int pid)
        => new[] { "/PID", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "/T", "/F" };

    /// <summary>wmic 的参数：<c>process where name='chrome.exe' delete</c>。</summary>
    public static string BuildWmicArguments(string? processName)
        => $"process where name='{NormalizeProcessName(processName)}' delete";

    /// <summary>
    /// 把进程名规范成 wmic 要的形式：去路径 / 引号 / 首尾空白，缺扩展名补 <c>.exe</c>，
    /// 单引号转义成两个（避免拼坏 WQL）。空名字返回空串。
    /// </summary>
    public static string NormalizeProcessName(string? processName)
    {
        var name = (processName ?? "").Trim().Trim('"');
        if (name.Length == 0) return "";

        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return name.Replace("'", "''");
    }

    /// <summary>把已尝试的结果整理成最终结果（第一级成功时 <see cref="KillOutcome.FromAttempts"/> 用最后一条判成功）。</summary>
    public static KillOutcome Summarize(int pid, IReadOnlyList<KillAttempt> attempts)
        => KillOutcome.FromAttempts(pid, attempts);
}
