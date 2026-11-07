using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using YuLiangToolkit.Tools.TaskManager.Models;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 开机启动项管理：扫描「注册表 Run / RunOnce」与「用户 / 公共启动文件夹」，
/// 用 Windows 自己的 <c>StartupApproved</c> 记录（任务管理器里那个开关就是它）启用 / 禁用，
/// 或者直接删掉启动项。
///
/// <para><b>扫哪些位置</b>（与任务管理器的「启动」页对齐）：</para>
/// <list type="bullet">
/// <item>HKCU <c>…\CurrentVersion\Run</c>（当前用户）</item>
/// <item>HKLM <c>…\CurrentVersion\Run</c> 的 64 位视图（所有用户）与 32 位视图（所有用户（32 位））</item>
/// <item>HKCU / HKLM 的 <c>RunOnce</c>（Scope 带「（仅一次）」后缀）</item>
/// <item>用户启动文件夹（<c>…\Start Menu\Programs\Startup</c>）与公共启动文件夹</item>
/// </list>
///
/// <para><b>启用 / 禁用</b>：写 <c>…\Explorer\StartupApproved\Run</c>（32 位项是 <c>Run32</c>、
/// 启动文件夹项是 <c>StartupFolder</c>）下的 12 字节 <c>REG_BINARY</c>；没有记录 = 启用。
/// Windows 对 <c>RunOnce</c> <b>没有</b>单独的 StartupApproved 键，这里借用 Run / Run32，
/// 所以对 RunOnce 项「禁用」实际不会生效（它下次开机仍会执行一次、然后自我删除），
/// 要阻止它请直接用「删除」。</para>
///
/// <para><b>StartupApproved 的注册表视图</b>：它固定放在注册表的原生视图里（不是 Wow6432Node），
/// 32 位项靠值所在键名 <c>Run32</c> 区分，所以这里统一用 64 位 / 默认视图打开它，
/// 不用 Run 项自身的 <c>Registry32</c> 视图（否则会读到 <c>Wow6432Node</c> 下的空键）。</para>
///
/// <para><b>权限</b>：HKCU / 用户启动文件夹可以直接改；HKLM / 公共启动文件夹需要管理员，
/// 权限不足时返回 false + 中文提示，<b>不抛异常</b>。</para>
/// </summary>
public sealed class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    private const string ApprovedStartupFolderKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    /// <summary>
    /// 扫描全部开机启动项。单个来源读不到（组策略锁住 / 权限不足）只跳过该来源，
    /// 其余来源照常返回；本方法本身不会抛异常。
    /// </summary>
    public IReadOnlyList<StartupEntry> Scan()
    {
        var entries = new List<StartupEntry>();
        var elevated = IsCurrentProcessElevated();

        // ── 注册表 Run（当前用户 / 所有用户 64 位 / 所有用户 32 位）
        ScanRegistryRun(RegistryHive.CurrentUser, RegistryView.Default, "HKCU", "Default",
            "当前用户", RunKeyPath, ApprovedRunKeyPath, elevated, entries);
        ScanRegistryRun(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM", "Registry64",
            "所有用户", RunKeyPath, ApprovedRunKeyPath, elevated, entries);
        ScanRegistryRun(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM", "Registry32",
            "所有用户（32 位）", RunKeyPath, ApprovedRun32KeyPath, elevated, entries);

        // ── 注册表 RunOnce（Scope 带「（仅一次）」；Windows 只执行一次后自动删除）
        ScanRegistryRun(RegistryHive.CurrentUser, RegistryView.Default, "HKCU", "Default",
            "当前用户（仅一次）", RunOnceKeyPath, ApprovedRunKeyPath, elevated, entries);
        ScanRegistryRun(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM", "Registry64",
            "所有用户（仅一次）", RunOnceKeyPath, ApprovedRunKeyPath, elevated, entries);
        ScanRegistryRun(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM", "Registry32",
            "所有用户（32 位）（仅一次）", RunOnceKeyPath, ApprovedRun32KeyPath, elevated, entries);

        // ── 启动文件夹
        ScanStartupFolder(GetFolderPath(Environment.SpecialFolder.Startup), "HKCU", "当前用户", true, entries);
        ScanStartupFolder(GetFolderPath(Environment.SpecialFolder.CommonStartup), "HKLM", "所有用户", elevated, entries);

        return entries;
    }

    /// <summary>
    /// 启用 / 禁用一条启动项：往 <c>StartupApproved</c> 写 12 字节 <c>REG_BINARY</c>。
    /// 权限不足 / 写失败返回 false，并通过 <paramref name="message"/> 给出中文原因。
    /// </summary>
    public bool SetEnabled(StartupEntry entry, bool enabled, out string message)
    {
        message = "";

        if (entry is null)
        {
            message = "启动项为空。";
            return false;
        }

        var valueName = entry.ApprovedValueName ?? entry.ValueName ?? entry.Name;
        if (string.IsNullOrEmpty(valueName))
        {
            message = "这一项没有可写入的 StartupApproved 值名。";
            return false;
        }

        if (string.IsNullOrEmpty(entry.ApprovedKeyPath))
        {
            message = "这一项缺少 StartupApproved 注册表位置。";
            return false;
        }

        try
        {
            var hive = ParseHive(entry.HiveName);
            using var baseKey = RegistryKey.OpenBaseKey(hive, ApprovedView(hive));
            using var approvedKey = baseKey.OpenSubKey(entry.ApprovedKeyPath, writable: true)
                                    ?? baseKey.CreateSubKey(entry.ApprovedKeyPath);

            if (approvedKey is null)
            {
                message = "打不开 StartupApproved 注册表键（可能需要管理员权限）。";
                return false;
            }

            approvedKey.SetValue(
                valueName,
                StartupApprovedState.Create(enabled, DateTime.UtcNow),
                RegistryValueKind.Binary);

            message = enabled ? $"已启用「{entry.Name}」。" : $"已禁用「{entry.Name}」。";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            message = "需要管理员权限才能修改这一项，请用「以管理员身份运行」重启本程序后再试。";
            return false;
        }
        catch (SecurityException)
        {
            message = "系统安全策略不允许修改这一项（可能需要管理员权限）。";
            return false;
        }
        catch (Exception exception)
        {
            message = $"修改启动项失败：{exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// 删除一条启动项：注册表项 <c>DeleteValue</c>，启动文件夹项删文件。
    /// 失败返回 false + 中文原因，不抛异常。
    /// </summary>
    public bool Delete(StartupEntry entry, out string message)
    {
        message = "";

        if (entry is null)
        {
            message = "启动项为空。";
            return false;
        }

        if (entry.Kind == StartupEntryKind.StartupFolder)
        {
            return DeleteStartupFolderEntry(entry, out message);
        }

        var valueName = entry.ValueName ?? entry.ApprovedValueName ?? entry.Name;
        if (string.IsNullOrEmpty(valueName))
        {
            message = "这一项没有可删除的注册表值名。";
            return false;
        }

        try
        {
            var hive = ParseHive(entry.HiveName);
            using var baseKey = RegistryKey.OpenBaseKey(hive, ParseView(entry.RegistryViewName));
            using var runKey = baseKey.OpenSubKey(entry.Location, writable: true);
            if (runKey is null)
            {
                message = "注册表键不存在或打不开（可能需要管理员权限）。";
                return false;
            }

            runKey.DeleteValue(valueName, throwOnMissingValue: false);
            message = $"已删除「{entry.Name}」，下次开机不再启动。";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            message = "需要管理员权限才能删除这一项，请用「以管理员身份运行」重启本程序后再试。";
            return false;
        }
        catch (SecurityException)
        {
            message = "系统安全策略不允许删除这一项（可能需要管理员权限）。";
            return false;
        }
        catch (Exception exception)
        {
            message = $"删除注册表启动项失败：{exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// 当前进程是否以管理员身份运行：决定 HKLM / 公共启动文件夹的项能不能改
    /// （<see cref="StartupEntry.CanModify"/> 就是用它算的）。
    /// </summary>
    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 扫描：注册表

    private static void ScanRegistryRun(
        RegistryHive hive,
        RegistryView view,
        string hiveName,
        string viewName,
        string scope,
        string keyPath,
        string approvedKeyPath,
        bool elevated,
        List<StartupEntry> entries)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var runKey = baseKey.OpenSubKey(keyPath);
            if (runKey is null) return;

            var canModify = hive == RegistryHive.CurrentUser || elevated;

            string[] valueNames;
            try
            {
                valueNames = runKey.GetValueNames();
            }
            catch
            {
                return;
            }

            foreach (var valueName in valueNames)
            {
                if (string.IsNullOrEmpty(valueName)) continue; // 跳过默认值（名为空）

                try
                {
                    var raw = runKey.GetValue(valueName);
                    var command = raw as string ?? raw?.ToString() ?? "";
                    var approved = ReadApprovedValue(hive, approvedKeyPath, valueName);

                    entries.Add(new StartupEntry
                    {
                        Kind = StartupEntryKind.RegistryRun,
                        Name = valueName,
                        Command = command,
                        Scope = scope,
                        Location = keyPath,
                        ValueName = valueName,
                        FilePath = StartupCommandParser.ExtractExecutable(command),
                        HiveName = hiveName,
                        RegistryViewName = viewName,
                        ApprovedKeyPath = approvedKeyPath,
                        ApprovedValueName = valueName,
                        FolderPath = "",
                        Enabled = StartupApprovedState.ParseEnabled(approved) ?? true,
                        CanModify = canModify,
                    });
                }
                catch
                {
                    // 单个值的类型 / 权限异常不影响其它启动项
                }
            }
        }
        catch
        {
            // 整个来源读不到（组策略 / 权限 / 32 位视图在 32 位系统上不存在）就跳过
        }
    }

    // ------------------------------------------------------------------ 扫描：启动文件夹

    private void ScanStartupFolder(
        string folderPath,
        string hiveName,
        string scope,
        bool canModify,
        List<StartupEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        try
        {
            if (!Directory.Exists(folderPath)) return;

            foreach (var filePath in Directory.EnumerateFiles(folderPath))
            {
                try
                {
                    var fileName = Path.GetFileName(filePath);
                    if (fileName.Length == 0) continue;
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    var hive = ParseHive(hiveName);
                    var approved = ReadApprovedValue(hive, ApprovedStartupFolderKeyPath, fileName);

                    // 启动文件夹项本身就是「命令」；用引号包起来再解析，避免路径里的空格把文件名截断。
                    var command = filePath;

                    entries.Add(new StartupEntry
                    {
                        Kind = StartupEntryKind.StartupFolder,
                        Name = fileName,
                        Command = command,
                        Scope = scope,
                        Location = folderPath,
                        ValueName = null,
                        FilePath = StartupCommandParser.ExtractExecutable("\"" + command + "\""),
                        HiveName = hiveName,
                        RegistryViewName = "Default",
                        ApprovedKeyPath = ApprovedStartupFolderKeyPath,
                        ApprovedValueName = fileName,
                        FolderPath = filePath,
                        Enabled = StartupApprovedState.ParseEnabled(approved) ?? true,
                        CanModify = canModify,
                    });
                }
                catch
                {
                    // 启动文件夹里的单个文件读不到（权限 / 链接损坏）就跳过
                }
            }
        }
        catch
        {
            // 文件夹枚举失败（不存在 / 权限）整段跳过
        }
    }

    // ------------------------------------------------------------------ 删除：启动文件夹

    private static bool DeleteStartupFolderEntry(StartupEntry entry, out string message)
    {
        message = "";

        if (string.IsNullOrWhiteSpace(entry.FolderPath))
        {
            message = "找不到启动文件夹项的文件路径。";
            return false;
        }

        try
        {
            if (!File.Exists(entry.FolderPath))
            {
                message = $"「{entry.Name}」的文件已经不存在，视为已删除。";
                return true;
            }

            File.Delete(entry.FolderPath);
            message = $"已删除「{entry.Name}」。";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            message = "需要管理员权限才能删除公共启动文件夹里的项，请用「以管理员身份运行」重启本程序后再试。";
            return false;
        }
        catch (IOException exception)
        {
            message = $"删除失败：{exception.Message}";
            return false;
        }
        catch (Exception exception)
        {
            message = $"删除失败：{exception.Message}";
            return false;
        }
    }

    // ------------------------------------------------------------------ 注册表辅助

    /// <summary>
    /// 读 StartupApproved 的 <c>REG_BINARY</c>；没有记录 / 读不到返回 null（上层按「启用」处理）。
    /// <para>视图固定用注册表原生视图：StartupApproved 不在 Wow6432Node 下，32 位项靠 <c>Run32</c> 区分。</para>
    /// </summary>
    private static byte[]? ReadApprovedValue(RegistryHive hive, string approvedKeyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, ApprovedView(hive));
            using var key = baseKey.OpenSubKey(approvedKeyPath);
            return key?.GetValue(valueName) as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static string GetFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch
        {
            return "";
        }
    }

    private static RegistryHive ParseHive(string? hiveName)
        => string.Equals(hiveName, "HKLM", StringComparison.OrdinalIgnoreCase)
            ? RegistryHive.LocalMachine
            : RegistryHive.CurrentUser;

    private static RegistryView ParseView(string? viewName) => viewName switch
    {
        "Registry64" => RegistryView.Registry64,
        "Registry32" => RegistryView.Registry32,
        _ => RegistryView.Default,
    };

    /// <summary>StartupApproved 所在视图：HKLM 固定用原生 64 位视图（32 位系统退回默认视图）。</summary>
    private static RegistryView ApprovedView(RegistryHive hive)
        => hive == RegistryHive.LocalMachine && Environment.Is64BitOperatingSystem
            ? RegistryView.Registry64
            : RegistryView.Default;
}
