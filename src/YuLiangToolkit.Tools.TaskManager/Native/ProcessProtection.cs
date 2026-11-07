namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 「系统关键进程」名单（纯逻辑，自检可直接断言）。
///
/// <para>这些进程被 Windows 保护，常规方式（Process.Kill / taskkill / wmic）都会「拒绝访问」。
/// 界面在用户点「结束任务」前先拦下并解释原因，而不是等三次失败后再报错。</para>
/// </summary>
public static class ProcessProtection
{
    // 全部用「不带 .exe 的小写基名」比较：Process.ProcessName 本身不带扩展名，
    // 而注册表 / 菜单里的显示名经常带 .exe，两种写法都要认。
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "system idle process",
        "registry",
        "memory compression",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "svchost",
        "fontdrvhost",
        "audiodg",
    };

    // 不是「关键到不能结束」，但结束后果比较明显（桌面会闪一下 / 任务栏消失），确认框里要额外提醒。
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "dwm",
    };

    /// <summary>是否是系统关键进程（受保护，常规方式结束不了）。</summary>
    public static bool IsCritical(string? processName)
        => !string.IsNullOrWhiteSpace(processName) && CriticalNames.Contains(BaseName(processName));

    /// <summary>是否是桌面外壳进程（可以结束，但会丢桌面 / 任务栏，确认时要提醒）。</summary>
    public static bool IsShell(string? processName)
        => !string.IsNullOrWhiteSpace(processName) && ShellNames.Contains(BaseName(processName));

    /// <summary>给确认框用的一句话分类；普通进程返回空串。</summary>
    public static string DescribeKind(string? processName)
    {
        if (IsCritical(processName)) return "系统关键进程";
        if (IsShell(processName)) return "桌面外壳进程";
        return "";
    }

    /// <summary>关键进程的统一提示语（与任务管理器截图里的说法保持一致）。</summary>
    public const string CriticalWarning =
        "这是系统关键进程，受 Windows 保护，任何常规方式都无法结束（会提示「拒绝访问」）。";

    /// <summary>桌面外壳进程的额外提醒。</summary>
    public const string ShellWarning =
        "结束它会让桌面 / 任务栏短暂消失，Windows 通常会自动重启它。";

    private static string BaseName(string processName)
    {
        var name = processName.Trim().Trim('"');
        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }
}
