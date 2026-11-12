using System.IO;
using System.Security;
using Microsoft.Win32;

namespace YuLiangToolkit.Core.Shell;

/// <summary>
/// Windows「开机自启」的登记服务（**只写 HKCU，不需要管理员权限**）。
///
/// <para><b>怎么实现</b>：往 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 写一个
/// <see cref="ValueName"/> 值，命令行就是带引号的主程序 exe 路径（路径带空格也不会坏）。
/// 这是 Windows 自己的用户级启动项：任务管理器「启动」页里能看到它，右键「禁用」也管用
/// （那一页的开关记在 <see cref="ApprovedRunKeyPath"/> 下）。</para>
///
/// <para><b>为什么写 Run 项而不是启动文件夹 / 计划任务</b>：只改当前用户、一次注册表写入、
/// 可见可撤销，不需要管理员也不需要额外文件；本程序是普通桌面程序，没有提权需求。</para>
///
/// <para><b>开关状态存哪</b>：开关本身是 <c>Config\shell\config.json</c> 的 <c>startWithWindows</c>
/// （默认关）；系统里的实际登记以 Run 项为准。启动、重建配置、导入配置后会按配置把两边对齐：
/// 配置开 = 写 Run 项并刷新成当前 exe 路径；配置关 = 清掉残留。</para>
///
/// <para><b>和任务管理器「禁用」的关系</b>：在任务管理器里禁用会留下
/// <see cref="ApprovedRunKeyPath"/> 里的 12 字节记录，光删 / 写 Run 项是不够的。
/// 所以开启时顺手删掉同名记录（没有记录 = 启用），关闭时也一并清掉。</para>
///
/// <para><b>自检绝不调用 <see cref="Register"/> / <see cref="Unregister"/> / <see cref="EnsureUpToDate"/></b>：
/// 那会往用户真实注册表写东西。只允许测纯逻辑 + 只读的 <see cref="ReadCommand"/> / <see cref="Describe"/>。</para>
/// </summary>
public static class StartupRegistration
{
    /// <summary>当前用户的开机启动项（HKCU，不需要管理员权限）。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>任务管理器「启动」页记「这一项被禁用」的地方（写启动项时顺手清掉同名记录）。</summary>
    public const string ApprovedRunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Run 项里的值名（任务管理器「启动」页显示的就是它）。</summary>
    public const string ValueName = "YuLiangToolkit";

    /// <summary>给界面 / 日志用的名字。</summary>
    public const string DisplayName = "凉雨工具集";

    /// <summary>
    /// 登记 / 比较用的主程序路径：从「打开方式」入口副本（<c>*.open-text.exe</c>）启动时也还原成主 exe；
    /// <paramref name="executablePath"/> 为空时用当前进程的 exe。
    /// </summary>
    public static string ResolveExecutablePath(string? executablePath)
    {
        var path = string.IsNullOrWhiteSpace(executablePath) ? Environment.ProcessPath : executablePath;
        return OpenWithRegistration.ResolveCanonicalExecutablePath(path);
    }

    /// <summary>拼 Run 项的命令行：只有带引号的 exe 路径（开机启动时不需要额外参数）。</summary>
    public static string BuildCommand(string executablePath)
        => $"\"{executablePath.Trim().Trim('"')}\"";

    /// <summary>
    /// 从一条启动命令行里取出 exe 路径（支持带引号 / 不带引号；忽略后面的参数）。纯函数。
    /// <para>不带引号又带空格的路径没法可靠拆分（Windows 自己也是这么含糊），
    /// 所以本程序写进注册表的命令行<b>一律带引号</b>（见 <see cref="BuildCommand"/>）。</para>
    /// </summary>
    public static string ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var text = command.Trim();

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>
    /// 这条命令行是不是就是指向 <paramref name="executablePath"/>（比路径，不比参数）。纯函数。
    /// <para><paramref name="executablePath"/> 按「就是一个完整路径」处理、不再分词，
    /// 否则程序目录带空格（如 <c>C:\Apps\凉雨 工具集\</c>）会被截断成 <c>C:\Apps\凉雨</c>、
    /// 每次启动都误判成「登记的是另一个位置」。</para>
    /// </summary>
    public static bool IsSameExecutable(string? command, string? executablePath)
    {
        var left = ExtractExecutablePath(command);
        var right = string.IsNullOrWhiteSpace(executablePath) ? "" : executablePath.Trim().Trim('"');
        return left.Length > 0 && right.Length > 0
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前登记的启动命令行；没有登记 / 读不到返回 <c>null</c>。只读，自检可以调。</summary>
    public static string? ReadCommand()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return run?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (IsRegistryIoException(ex))
        {
            return null;
        }
    }

    /// <summary>系统里现在是否登记过本程序的开机自启。只读。</summary>
    public static bool IsRegistered() => !string.IsNullOrWhiteSpace(ReadCommand());

    /// <summary>登记状态说明（界面显示 / 自检只读）。</summary>
    public static StartupRegistrationState Describe(string? executablePath)
    {
        var main = ResolveExecutablePath(executablePath);
        var command = ReadCommand();

        if (string.IsNullOrWhiteSpace(command))
        {
            return new StartupRegistrationState(
                false, false, "",
                $"未开启：开机后不会自动启动{DisplayName}。勾选上面的复选框后会写一条当前用户的开机启动项"
                + "（HKCU\\…\\Run），不需要管理员。");
        }

        if (main.Length == 0)
        {
            return new StartupRegistrationState(
                true, false, command,
                $"已登记开机自启，但读不到当前程序路径，无法核对：{command}");
        }

        return IsSameExecutable(command, main)
            ? new StartupRegistrationState(
                true, true, command,
                $"已开启：开机后会启动 {main}"
                + "（任务管理器 → 启动 里也能看到这一项）。")
            : new StartupRegistrationState(
                true, false, command,
                $"已开启，但登记的是另一个位置：{command}（当前程序是 {main}）。"
                + "程序换过目录 / 重新编译过就会这样，重新勾选一次即可刷新成当前路径。");
    }

    /// <summary>
    /// 登记 / 刷新开机自启（幂等）。成功时顺带清掉任务管理器里的「已禁用」记录，
    /// 免得界面显示已开启、开机却不启动。
    /// </summary>
    public static StartupRegistrationResult Register(string? executablePath)
    {
        var main = ResolveExecutablePath(executablePath);
        if (string.IsNullOrWhiteSpace(main) || !File.Exists(main))
            return new StartupRegistrationResult(false, $"找不到主程序 exe：{main}");

        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (run is null) return new StartupRegistrationResult(false, "打不开当前用户的 Run 注册表键。");

            run.SetValue(ValueName, BuildCommand(main), RegistryValueKind.String);
            ClearApprovedRecord();
            return new StartupRegistrationResult(true, $"已开启：开机后会启动 {main}");
        }
        catch (Exception ex) when (IsRegistryIoException(ex))
        {
            return new StartupRegistrationResult(
                false, $"开启开机自启失败：{ex.Message}（只写当前用户注册表，正常不需要管理员）");
        }
    }

    /// <summary>取消登记（没登记时也算成功，方便开关关掉时无脑调一次）。</summary>
    public static StartupRegistrationResult Unregister()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run?.GetValue(ValueName) is null)
                return new StartupRegistrationResult(true, "未开启开机自启（无需清理）。");

            run.DeleteValue(ValueName, throwOnMissingValue: false);
            ClearApprovedRecord();
            return new StartupRegistrationResult(true, $"已关闭：开机后不会自动启动{DisplayName}。");
        }
        catch (Exception ex) when (IsRegistryIoException(ex))
        {
            return new StartupRegistrationResult(
                false, $"关闭开机自启失败：{ex.Message}（只写当前用户注册表，正常不需要管理员）");
        }
    }

    /// <summary>
    /// 程序更新 / 换目录后把 Run 项刷新成当前 exe（未登记时什么都不做）。
    /// 主程序启动时若配置开着会自动调一次。
    /// </summary>
    public static StartupRegistrationResult EnsureUpToDate(string? executablePath)
    {
        var command = ReadCommand();
        if (string.IsNullOrWhiteSpace(command))
            return new StartupRegistrationResult(true, "未开启开机自启，跳过刷新。");

        var main = ResolveExecutablePath(executablePath);
        if (main.Length == 0) return new StartupRegistrationResult(false, "读不到当前程序路径，跳过刷新。");
        if (IsSameExecutable(command, main))
            return new StartupRegistrationResult(true, $"已开启：开机后会启动 {main}");

        return Register(main);
    }

    // ---------------------------------------------------------------- 内部实现

    /// <summary>删掉任务管理器里的「已禁用」记录（没有记录 = 启用）。失败不影响启动项本身。</summary>
    private static void ClearApprovedRecord()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedRunKeyPath, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 没有这条记录 / 权限不足：启动项照常是启用的
        }
    }

    private static bool IsRegistryIoException(Exception ex)
        => ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException;
}

/// <summary>开机自启当前的登记状态（给界面显示用；全部只读，不改系统）。</summary>
public sealed record StartupRegistrationState(
    bool Registered,
    bool MatchesCurrentExecutable,
    string Command,
    string Message);

/// <summary>一次开启 / 关闭 / 刷新操作的结果。</summary>
public sealed record StartupRegistrationResult(bool Success, string Message);
