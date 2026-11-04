using System.Security;
using Microsoft.Win32;

namespace YuLiangToolkit.Tools.ContextMenu;

/// <summary>
/// <see cref="IContextMenuRegistry"/> 的真实实现：把 <see cref="RegistryRoot"/> 分别映射到
/// <see cref="Registry.CurrentUser"/> / <see cref="Registry.LocalMachine"/>，用
/// <c>Microsoft.Win32.Registry</c> 读写 Windows 注册表。
///
/// <para><b>只读操作绝不抛异常</b>：枚举右键菜单时遇到某个键没有权限 / 被策略锁住 / 类型怪异，
/// 一律当作「读不到」返回空（或 null），由 <see cref="ContextMenuService"/> 继续枚举其它位置，
/// 避免一个坏键把整个扫描炸掉。</para>
///
/// <para>写入统一按 <c>REG_SZ</c> 处理；<see cref="SetValue"/> 用 <c>CreateSubKey</c> 自动补父键；
/// 删除值 / 键时「本来就不存在」按接口约定静默忽略。所有打开的 <see cref="RegistryKey"/>
/// 都用 <c>using</c> 关闭（静态根键本身不关闭、不释放）。</para>
/// </summary>
public sealed class RegistryContextMenuStore : IContextMenuRegistry
{
    /// <inheritdoc />
    public bool KeyExists(RegistryPath path)
    {
        try
        {
            var subPath = NormalizePath(path.Path);
            if (subPath.Length == 0) return true; // 根键永远存在
            using var key = GetRoot(path.Root).OpenSubKey(subPath);
            return key is not null;
        }
        catch
        {
            // 读不到就当作不存在（枚举时不能因为一个无权限键而崩掉）
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSubKeyNames(RegistryPath path)
    {
        try
        {
            var subPath = NormalizePath(path.Path);
            if (subPath.Length == 0) return GetRoot(path.Root).GetSubKeyNames();
            using var key = GetRoot(path.Root).OpenSubKey(subPath);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public string? GetValue(RegistryPath path, string valueName)
    {
        try
        {
            var name = valueName ?? "";
            var subPath = NormalizePath(path.Path);
            if (subPath.Length == 0) return ToText(GetRoot(path.Root).GetValue(name));
            using var key = GetRoot(path.Root).OpenSubKey(subPath);
            return key is null ? null : ToText(key.GetValue(name));
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RegistryValue> GetValues(RegistryPath path)
    {
        try
        {
            var subPath = NormalizePath(path.Path);
            if (subPath.Length == 0) return ReadValues(GetRoot(path.Root));
            using var key = GetRoot(path.Root).OpenSubKey(subPath);
            return key is null ? Array.Empty<RegistryValue>() : ReadValues(key);
        }
        catch
        {
            return Array.Empty<RegistryValue>();
        }
    }

    /// <inheritdoc />
    public void SetValue(RegistryPath path, string valueName, string value)
    {
        var name = valueName ?? "";
        var subPath = NormalizePath(path.Path);
        if (subPath.Length == 0)
        {
            GetRoot(path.Root).SetValue(name, value ?? "", RegistryValueKind.String);
            return;
        }

        using var key = GetRoot(path.Root).CreateSubKey(subPath);
        if (key is null) throw new IOException($"无法创建注册表键：{path}");
        key.SetValue(name, value ?? "", RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void DeleteValue(RegistryPath path, string valueName)
    {
        var subPath = NormalizePath(path.Path);
        if (subPath.Length == 0) return; // 不碰根键上的值
        using var key = GetRoot(path.Root).OpenSubKey(subPath, writable: true);
        // 键 / 值不存在时 throwOnMissingValue: false 静默忽略；无权限时抛异常，由 Service 转成中文失败消息。
        key?.DeleteValue(valueName ?? "", throwOnMissingValue: false);
    }

    /// <inheritdoc />
    public void DeleteKey(RegistryPath path)
    {
        try
        {
            var subPath = NormalizePath(path.Path);
            if (subPath.Length == 0) return; // 绝不删根键
            GetRoot(path.Root).DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            // 只用来清理本工具创建的空键；删不掉也不影响菜单显示（覆盖值已经删掉了）。
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 显式实现（不走接口默认实现）：删除后读回确认，只有确认键已经不在了才返回 true。
    /// 权限不足 / 键被占用 / 参数异常一律返回 false，<b>不抛异常、也不假装成功</b>，
    /// 由 <see cref="ContextMenuService.Delete"/> 区分 HKLM（界面再走一次 UAC 提权）与 HKCU（直接报失败）。
    /// </remarks>
    public bool TryDeleteKey(RegistryPath path)
    {
        var subPath = NormalizePath(path.Path);
        if (subPath.Length == 0) return false; // 绝不删根键

        try
        {
            GetRoot(path.Root).DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // 权限不足 / 键被占用：交给调用方按 HKLM 提权或 HKCU 失败处理。
            return false;
        }

        // 读回确认：键仍然存在（或读不到）就不能算删掉。
        return !KeyExists(path);
    }

    /// <summary><see cref="RegistryRoot"/> → 静态根键（不负责释放）。</summary>
    private static RegistryKey GetRoot(RegistryRoot root)
        => root == RegistryRoot.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;

    /// <summary>去掉首尾反斜杠；注册表 API 不接受空路径时由调用方另行处理根键。</summary>
    private static string NormalizePath(string? path)
        => (path ?? "").Trim().Trim('\\');

    /// <summary>把一个注册表值转成字符串：默认值 / REG_SZ 原样返回，多字符串用分号连接，其它类型走 ToString。</summary>
    private static string? ToText(object? value) => value switch
    {
        null => null,
        string text => text,
        string[] parts => string.Join(";", parts),
        _ => value.ToString(),
    };

    /// <summary>读一个键下的全部值（调用方负责键的打开与释放）。</summary>
    private static IReadOnlyList<RegistryValue> ReadValues(RegistryKey key)
    {
        var names = key.GetValueNames();
        var values = new List<RegistryValue>(names.Length);
        foreach (var name in names) values.Add(new RegistryValue(name, ToText(key.GetValue(name))));
        return values;
    }
}
