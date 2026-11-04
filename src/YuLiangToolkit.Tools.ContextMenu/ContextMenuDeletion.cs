using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace YuLiangToolkit.Tools.ContextMenu;

/// <summary>「删除前自动备份」的结果。</summary>
/// <param name="Success">全部目标键都导出成功。</param>
/// <param name="Files">生成的 .reg 备份文件路径。</param>
/// <param name="Failures">失败项说明（键 + 原因）。</param>
public sealed record ContextMenuBackupResult(bool Success, IReadOnlyList<string> Files, IReadOnlyList<string> Failures);

/// <summary>
/// 删除前的注册表备份：调用系统 <c>reg.exe export</c> 把要删的键导成 <c>.reg</c> 文件，
/// 放在 <c>Data\context-menu-manager\backups\</c>；双击 .reg 即可还原。
///
/// <para>只用系统自带的 <c>reg.exe</c>（不新增 NuGet、不用 Win32 API）：导出的是完整子树，
/// 用户误删后双击备份文件就能把菜单项恢复回来。文件名由
/// <see cref="BuildBackupFileName"/> 生成：时间 + HKCU/HKLM + 清洗后的键路径 + 短哈希，
/// 既能看懂来源，又不会因为注册表路径里的 <c>\</c> / 中文 / 超长路径导致保存失败或互相覆盖。</para>
/// </summary>
public sealed class ContextMenuBackupService
{
    /// <summary>备份文件名里路径部分的最大长度（避免整个文件路径超过 Windows 上限）。</summary>
    private const int MaxPathSegmentLength = 80;

    /// <summary>每个键的导出超时（毫秒）。</summary>
    private const int ExportTimeoutMilliseconds = 15_000;

    /// <summary>备份文件夹（不存在时由 <see cref="Backup"/> 创建）。</summary>
    public string BackupDirectory { get; }

    /// <summary>构造备份服务；<paramref name="backupDirectory"/> 为空时抛 <see cref="ArgumentNullException"/>。</summary>
    public ContextMenuBackupService(string backupDirectory)
    {
        BackupDirectory = backupDirectory ?? throw new ArgumentNullException(nameof(backupDirectory));
    }

    /// <summary>
    /// 备份一组注册表键；有任何一项失败时 <see cref="ContextMenuBackupResult.Success"/> 为 false。
    /// <para>做三件事：自动建备份目录 → 逐键 <c>reg.exe export "HKCU|HKLM\路径" "文件" /y</c> →
    /// 只有「进程退出码为 0 且文件真的存在」才算这一项成功。</para>
    /// </summary>
    public ContextMenuBackupResult Backup(IEnumerable<ContextMenuKeyRef> keys)
    {
        var files = new List<string>();
        var failures = new List<string>();

        var distinct = DistinctKeys(keys);
        if (distinct.Count == 0)
            return new ContextMenuBackupResult(false, files, new[] { "没有要备份的注册表键。" });

        try
        {
            Directory.CreateDirectory(BackupDirectory);
        }
        catch (Exception ex)
        {
            return new ContextMenuBackupResult(
                false, files, new[] { $"创建备份目录失败：{ex.Message}" });
        }

        var timestamp = DateTime.Now;
        foreach (var key in distinct)
        {
            var filePath = Path.Combine(BackupDirectory, BuildBackupFileName(key, timestamp));
            try
            {
                if (ExportKey(key, filePath, out var error))
                {
                    files.Add(filePath);
                }
                else
                {
                    failures.Add($"备份「{key}」失败：{error}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"备份「{key}」失败：{ex.Message}");
            }
        }

        return new ContextMenuBackupResult(files.Count > 0 && failures.Count == 0, files, failures);
    }

    /// <summary>
    /// 生成不重名、可读的备份文件名（时间 + 根 + 清洗后的键路径 + 短哈希 + <c>.reg</c>）。
    /// <para>纯函数（只依赖入参与字符表），自检可以直接断言。</para>
    /// <para>清洗规则：把 Windows 文件名非法字符（<c>\ / : * ? " &lt; &gt; |</c> 和控制字符）
    /// 逐个换成 <c>_</c>，去掉结尾的点 / 空格，路径部分限长；短哈希取
    /// <c>SHA256(根 + 键路径)</c> 的前 8 位，保证「清洗后撞名」的不同键也不会互相覆盖。</para>
    /// </summary>
    public static string BuildBackupFileName(ContextMenuKeyRef key, DateTime timestamp)
    {
        var rootLabel = key.Root == RegistryRoot.LocalMachine ? "HKLM" : "HKCU";
        var relativePath = (key.RelativePath ?? "").Trim();
        var shortHash = ShortHash(rootLabel + "\\" + relativePath);
        var cleanedPath = SanitizePathForFileName(relativePath);
        return $"{timestamp:yyyyMMdd-HHmmss}-{rootLabel}-{cleanedPath}-{shortHash}.reg";
    }

    /// <summary>把一个键的完整子树导出到 <paramref name="filePath"/>；失败时通过 <paramref name="error"/> 说明原因。</summary>
    private static bool ExportKey(ContextMenuKeyRef key, string filePath, out string error)
    {
        error = "";
        // reg.exe 接受 HKCU\... / HKLM\... 形式的注册表路径。
        var registryPath = (key.Root == RegistryRoot.LocalMachine ? "HKLM\\" : "HKCU\\") + key.RelativePath;

        var startInfo = new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = $"export \"{registryPath}\" \"{filePath}\" /y",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            error = "无法启动 reg.exe。";
            return false;
        }

        if (!process.WaitForExit(ExportTimeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已经退出时忽略 */ }
            error = "导出超时。";
            return false;
        }

        if (process.ExitCode != 0)
        {
            error = $"reg.exe 返回代码 {process.ExitCode}（键可能不存在或没有访问权限）。";
            return false;
        }

        if (!File.Exists(filePath))
        {
            error = "reg.exe 报告成功但没有生成备份文件。";
            return false;
        }

        return true;
    }

    /// <summary>把注册表路径清洗成合法的文件名片段（去掉非法字符、结尾点 / 空格，并限长）。</summary>
    private static string SanitizePathForFileName(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "key";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(relativePath.Length);
        foreach (var character in relativePath)
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);

        var cleaned = builder.ToString().TrimEnd('.', ' ');
        if (cleaned.Length > MaxPathSegmentLength)
            cleaned = cleaned[..MaxPathSegmentLength].TrimEnd('.', ' ');

        return cleaned.Length == 0 ? "key" : cleaned;
    }

    /// <summary><c>SHA256</c> 前 8 位十六进制（小写），用来防止清洗后重名。</summary>
    private static string ShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    /// <summary>去重（根 + 路径，忽略大小写）后的键清单。</summary>
    private static List<ContextMenuKeyRef> DistinctKeys(IEnumerable<ContextMenuKeyRef>? keys)
    {
        var result = new List<ContextMenuKeyRef>();
        if (keys is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (key is null || string.IsNullOrWhiteSpace(key.RelativePath)) continue;
            if (seen.Add(key.Root + "|" + key.RelativePath.Trim().Trim('\\'))) result.Add(key);
        }
        return result;
    }
}

/// <summary>提权删除 HKLM 键的结果。</summary>
/// <param name="Success">全部目标键都确认删掉了。</param>
/// <param name="DeletedKeys">确认已不存在的键。</param>
/// <param name="Messages">失败 / 取消授权等说明。</param>
public sealed record ContextMenuElevatedDeleteResult(
    bool Success,
    IReadOnlyList<ContextMenuKeyRef> DeletedKeys,
    IReadOnlyList<string> Messages);

/// <summary>
/// 用 UAC 管理员权限删除 HKLM 下的注册表键（HKCU 不需要走这里）。
/// <para>做法：把要删的键写进一个临时 .cmd，用 <c>runas</c> 起 <c>cmd.exe /c</c> 批量删，
/// 只弹一次 UAC；删完按注册表读回验证。</para>
/// <para>只处理 HKLM：HKCU 的键由 <see cref="ContextMenuService.Delete"/> 直接删，不归这里管。</para>
/// </summary>
public static class ElevatedContextMenuDelete
{
    /// <summary>提权删除脚本里每行统一的 <c>reg.exe</c> 前缀（自检直接断言这一行文本）。</summary>
    public const string ScriptHeaderLine = "@echo off";

    /// <summary>等提权进程退出的上限（毫秒）。</summary>
    private const int ElevatedDeleteTimeoutMilliseconds = 60_000;

    /// <summary>UAC 被用户取消时 <c>Process.Start</c> 抛出的 Win32 错误码（ERROR_CANCELLED）。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>注册表键路径是否可以安全写进 .cmd 脚本（不能含引号 / 换行）。</summary>
    public static bool IsSafeKeyPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && !path.Contains('"')
           && !path.Contains('\r')
           && !path.Contains('\n');

    /// <summary>
    /// 生成 .cmd 脚本内容（只处理 HKLM 键；纯函数，自检可断言）。
    /// <para>第一行固定 <c>@echo off</c>；之后每个「合法且安全的 HKLM 键」一行
    /// <c>reg.exe delete "HKLM\路径" /f &gt;nul 2&gt;nul</c>；HKCU 键和不安全路径直接跳过。</para>
    /// </summary>
    public static IReadOnlyList<string> BuildScriptLines(IEnumerable<ContextMenuKeyRef> keys)
    {
        var lines = new List<string> { ScriptHeaderLine };
        if (keys is null) return lines;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (key is null || key.Root != RegistryRoot.LocalMachine) continue;

            var relativePath = (key.RelativePath ?? "").Trim().Trim('\\');
            if (!IsSafeKeyPath(relativePath)) continue;

            var registryPath = "HKLM\\" + relativePath;
            if (!seen.Add(registryPath)) continue;
            lines.Add($"reg.exe delete \"{registryPath}\" /f >nul 2>nul");
        }

        return lines;
    }

    /// <summary>
    /// 提权删除一组 HKLM 键；取消 UAC 或没有管理员权限时返回失败说明。
    /// <para>流程：过滤出安全的 HKLM 键 → 写临时 .cmd（<see cref="Encoding.Default"/>，兼容中文键名）→
    /// <c>cmd.exe /c 脚本</c>（<c>runas</c>）→ 等退出 → 用 <see cref="RegistryContextMenuStore.KeyExists"/>
    /// 复核每个键是不是真的没了 → 删临时脚本。全程不抛异常。</para>
    /// </summary>
    public static ContextMenuElevatedDeleteResult Delete(IReadOnlyList<ContextMenuKeyRef> keys)
    {
        var deleted = new List<ContextMenuKeyRef>();
        var messages = new List<string>();
        var targets = new List<ContextMenuKeyRef>();

        if (keys is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                if (key is null || key.Root != RegistryRoot.LocalMachine) continue;

                var relativePath = (key.RelativePath ?? "").Trim().Trim('\\');
                if (!IsSafeKeyPath(relativePath))
                {
                    messages.Add($"「{key}」的路径不安全（含引号或换行），已跳过提权删除。");
                    continue;
                }

                if (seen.Add(key.Root + "|" + relativePath))
                    targets.Add(new ContextMenuKeyRef(key.Root, relativePath));
            }
        }

        if (targets.Count == 0)
        {
            if (messages.Count == 0) messages.Add("没有需要管理员权限删除的 HKLM 注册表键。");
            return new ContextMenuElevatedDeleteResult(false, deleted, messages);
        }

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            "YuLiangToolkit-ContextMenu-" + Guid.NewGuid().ToString("N") + ".cmd");

        try
        {
            // Encoding.Default：.cmd 是给系统 cmd.exe 读的，用系统默认编码写中文键名最不容易乱码。
            File.WriteAllLines(scriptPath, BuildScriptLines(targets), Encoding.Default);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return new ContextMenuElevatedDeleteResult(
                    false, deleted, new[] { "已取消管理员授权，删除没有执行。" });
            }
            catch (Win32Exception ex)
            {
                return new ContextMenuElevatedDeleteResult(
                    false, deleted, new[] { $"以管理员权限启动删除程序失败：{ex.Message}" });
            }

            if (process is null)
                return new ContextMenuElevatedDeleteResult(false, deleted, new[] { "无法启动提权删除程序。" });

            using (process)
            {
                if (!process.WaitForExit(ElevatedDeleteTimeoutMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* 已经退出时忽略 */ }
                    messages.Add("提权删除等待超时，结果可能不完整。");
                }
                else if (process.ExitCode != 0)
                {
                    messages.Add($"提权删除脚本返回代码 {process.ExitCode}。");
                }
            }

            // 读回复核：只有确认键不存在才算删掉。
            var store = new RegistryContextMenuStore();
            foreach (var key in targets)
            {
                try
                {
                    if (!store.KeyExists(key.ToRegistryPath()))
                    {
                        deleted.Add(key);
                    }
                    else
                    {
                        messages.Add($"「{key}」仍然存在（可能被占用或权限不足），没有删除。");
                    }
                }
                catch (Exception ex)
                {
                    messages.Add($"复核「{key}」失败：{ex.Message}");
                }
            }

            return new ContextMenuElevatedDeleteResult(
                deleted.Count == targets.Count && messages.Count == 0,
                deleted,
                messages);
        }
        catch (Exception ex)
        {
            messages.Add($"提权删除失败：{ex.Message}");
            return new ContextMenuElevatedDeleteResult(false, deleted, messages);
        }
        finally
        {
            try
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
            catch
            {
                // 临时脚本删不掉不影响已经执行的删除，下次系统清理临时目录会带走。
            }
        }
    }
}
