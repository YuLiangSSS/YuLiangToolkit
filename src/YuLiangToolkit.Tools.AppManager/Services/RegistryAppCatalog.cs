using System.IO;
using Microsoft.Win32;
using YuLiangToolkit.Tools.AppManager.Models;

namespace YuLiangToolkit.Tools.AppManager.Services;

/// <summary>扫描器的契约：给界面一个可替换的入口，自检可以注入假数据（不碰真实注册表）。</summary>
public interface IAppCatalog
{
    /// <summary>读出本机所有「注册表里登记过的桌面应用」（已过滤 + 去重，未排序）。</summary>
    IReadOnlyList<InstalledApp> Load();
}

/// <summary>
/// 真实的「已安装应用」扫描：读三处注册表卸载项（ARP），拼成 <see cref="InstalledApp"/>。
///
/// <para><b>为什么是这三处</b>：Windows「设置 → 应用 → 安装的应用」列桌面程序时看的也是这里 ——</para>
/// <list type="bullet">
///   <item><c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</c>（64 位机器上的 64 位程序，需要管理员的写在 HKLM）；</item>
///   <item><c>HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall</c>（32 位程序，用 <see cref="RegistryView.Registry32"/> 直接映射过去）；</item>
///   <item><c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</c>（只给当前用户装的程序）。</item>
/// </list>
///
/// <para><b>只读</b>：本工具不写任何注册表键、不删文件；<c>OpenSubKey</c> 一律不加写权限。</para>
///
/// <para><b>不抛异常</b>：某一路线读不出来（权限 / 键不存在）就跳过，其它照常；
/// 单条记录字段异常也只把那个字段当空值。</para>
/// </summary>
public sealed class RegistryAppCatalog : IAppCatalog
{
    /// <summary>卸载项相对路径（HKLM 64 位 / HKCU 用 64 位视图，32 位用 <see cref="RegistryView.Registry32"/> 自动落到 WOW6432Node）。</summary>
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>注册表来源标签（去重优先级见 <see cref="AppCatalogRules.ScopePriority"/>）。</summary>
    private const string ScopeMachine = "HKLM";
    private const string ScopeMachineWow64 = "HKLM-WOW64";
    private const string ScopeUser = "HKCU";

    /// <summary>读出全部已安装应用（只读；任何一路失败都不影响其它）。</summary>
    public IReadOnlyList<InstalledApp> Load()
    {
        var all = new List<InstalledApp>();
        all.AddRange(ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, ScopeMachine));
        all.AddRange(ReadHive(RegistryHive.CurrentUser, RegistryView.Default, ScopeUser));
        all.AddRange(ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, ScopeMachineWow64));
        return AppCatalogRules.DedupeByName(all);
    }

    /// <summary>读一路注册表里的全部子键。</summary>
    private static List<InstalledApp> ReadHive(RegistryHive hive, RegistryView view, string scope)
    {
        var result = new List<InstalledApp>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(UninstallPath, writable: false);
            if (root is null) return result;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                InstalledApp? app;
                try
                {
                    using var subKey = root.OpenSubKey(subKeyName, writable: false);
                    if (subKey is null) continue;
                    app = ReadEntry(subKey, subKeyName, scope);
                }
                catch
                {
                    // 单条记录读坏（极少数键权限特殊）：跳过这一条，不影响整批。
                    continue;
                }

                if (app is not null) result.Add(app);
            }
        }
        catch
        {
            // 整路都读不出来（权限 / 键不存在）：返回已经收集到的部分。
        }

        return result;
    }

    /// <summary>把一条注册表项映射成 <see cref="InstalledApp"/>；被过滤的返回 null。</summary>
    private static InstalledApp? ReadEntry(RegistryKey key, string keyName, string scope)
    {
        // 文本一律过 AppCatalogRules.CleanText：实测有安装程序把值写成定长缓冲，
        // DisplayName / InstallDate 后面拖一长串 \0，直接 Trim() 去不掉。
        var name = AppCatalogRules.CleanText(ReadText(key, "DisplayName"));
        var uninstall = AppCatalogRules.CleanText(ReadText(key, "UninstallString"));
        var quietUninstall = AppCatalogRules.CleanText(ReadText(key, "QuietUninstallString"));
        var releaseType = AppCatalogRules.CleanText(ReadText(key, "ReleaseType"));
        var systemComponent = ReadInt(key, "SystemComponent") == 1;
        var parentKeyName = AppCatalogRules.CleanText(ReadText(key, "ParentKeyName"));
        var installLocation = AppCatalogRules.CleanText(ReadText(key, "InstallLocation"));
        var iconSource = AppCatalogRules.CleanText(ReadText(key, "DisplayIcon"));
        var installDateRaw = AppCatalogRules.CleanText(ReadText(key, "InstallDate"));

        if (!AppCatalogRules.ShouldInclude(name, systemComponent, parentKeyName, releaseType, uninstall))
            return null;

        // QuietUninstallString 是「能静默卸载」的那条；本工具不做静默卸载，
        // 但它是官方推荐入口，优先用它（它同样会弹安装程序自己的界面，除非应用本来就不弹）。
        var command = quietUninstall.Length > 0 ? quietUninstall : uninstall;

        return new InstalledApp
        {
            Name = name,
            Publisher = AppCatalogRules.CleanText(ReadText(key, "Publisher")),
            Version = AppCatalogRules.CleanText(ReadText(key, "DisplayVersion")),
            InstallDateRaw = installDateRaw,
            SizeInKilobytes = ReadInt(key, "EstimatedSize"),
            InstallLocation = installLocation,
            UninstallCommand = command,
            ModifyCommand = AppCatalogRules.CleanText(ReadText(key, "ModifyPath")),
            IconSource = iconSource,
            RegistryScope = scope,
            RegistryKeyName = keyName,
            IsUpdate = AppCatalogRules.IsPatchReleaseType(releaseType),
            InstallDate = AppCatalogRules.ParseInstallDate(installDateRaw),
            DriveLetter = AppCatalogRules.ResolveDrive(installLocation, iconSource, command),
        };
    }

    // ---------------------------------------------------------------- 读值（都不抛异常）

    /// <summary>读字符串值；不存在 / 类型不对 / 读失败 → 空串。</summary>
    private static string ReadText(RegistryKey key, string valueName)
    {
        try
        {
            return key.GetValue(valueName) switch
            {
                string text => text,
                string[] many => many.FirstOrDefault() ?? "",
                int number => number.ToString(),
                long number => number.ToString(),
                _ => "",
            };
        }
        catch
        {
            return "";
        }
    }

    /// <summary>读整数（DWORD / 字符串写成的数字都认）；不存在 / 解析失败 → 0。</summary>
    private static int ReadInt(RegistryKey key, string valueName)
    {
        try
        {
            return key.GetValue(valueName) switch
            {
                int number => number,
                long number => (int)Math.Clamp(number, int.MinValue, int.MaxValue),
                string text => int.TryParse(text.Trim(), out var parsed) ? parsed : 0,
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// 驱动器卷标读取器（「Acer (C:)」里的 <c>Acer</c>）：从 <see cref="DriveInfo"/> 读，读不到（没插盘 / 没权限）返回空串。
/// </summary>
public static class VolumeLabelReader
{
    /// <summary>一次性读出本机所有盘符 → 卷标。</summary>
    public static Dictionary<string, string> ReadAll()
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                string letter;
                string label;
                try
                {
                    letter = drive.Name.TrimEnd('\\', ':');
                    label = drive.VolumeLabel;
                }
                catch
                {
                    continue;
                }

                if (letter.Length == 0) continue;
                labels[letter] = label;
            }
        }
        catch
        {
            // 枚举驱动器失败：返回空表，界面退回「本地磁盘 (C:)」写法。
        }

        return labels;
    }
}
