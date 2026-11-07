using YuLiangToolkit.Tools.TaskManager.Native;

namespace YuLiangToolkit.Tools.TaskManager.Models;

/// <summary>
/// 一块「物理磁盘」的实时读写速度（PDH <c>\PhysicalDisk(*)\Disk Read/Write Bytes/sec</c> 的实例）。
///
/// <para>Windows 性能计数器里的实例名形如 <c>0 C:</c>（磁盘号 + 盘符）或 <c>2</c>（没有盘符），
/// 这里统一转成界面文字：<c>C:</c> / <c>磁盘 2</c>。多块盘各自一条，不再只显示一个总和。</para>
/// </summary>
public sealed class DiskPerformanceInfo
{
    /// <summary>PDH 原始实例名，例如 <c>0 C:</c>（排错 / 区分同盘符的虚拟盘时有用）。</summary>
    public string Instance { get; init; } = "";

    /// <summary>界面显示名：盘符（<c>C:</c> / <c>C: D:</c>）或 <c>磁盘 2</c>。</summary>
    public string Name { get; init; } = "";

    public double ReadBytesPerSecond { get; init; }

    public double WriteBytesPerSecond { get; init; }

    public string ReadText => DisplayFormat.Rate(ReadBytesPerSecond);

    public string WriteText => DisplayFormat.Rate(WriteBytesPerSecond);

    /// <summary>一行完整文案：<c>C:  读 5.6 MB/s · 写 232 KB/s</c>。</summary>
    public string DisplayText => $"{Name}  读 {ReadText} · 写 {WriteText}";

    /// <summary>
    /// 从 PDH 实例名生成显示名（纯函数）：
    /// <c>0 C:</c> → <c>C:</c>；<c>0 C: D:</c> → <c>C: D:</c>；<c>2</c> → <c>磁盘 2</c>；
    /// <c>_Total</c> / 空 → 空串（总和实例不单独显示）。
    /// </summary>
    public static string FormatName(string? instance)
    {
        var text = (instance ?? "").Trim();
        if (text.Length == 0) return "";
        if (string.Equals(text, "_Total", StringComparison.OrdinalIgnoreCase)) return "";

        var space = text.IndexOf(' ');
        if (space > 0)
        {
            var head = text[..space];
            var tail = text[(space + 1)..].Trim();
            // "0 c:" / "1 c: d:" → 盘符部分；"0 SomeVirtualDisk" 这种不是磁盘号开头的原样返回
            if (int.TryParse(head, out _) && tail.Length > 0) return NormalizeDriveLetters(tail);
            return text;
        }

        return int.TryParse(text, out _) ? "磁盘 " + text : NormalizeDriveLetters(text);
    }

    /// <summary>
    /// 把盘符统一成大写好看一点（性能计数器实例名常是小写 <c>c:</c>，任务管理器里习惯显示 <c>C:</c>）；
    /// 不是「单字母 + 冒号」的段（虚拟盘 / 描述性实例名）保持原样。
    /// </summary>
    private static string NormalizeDriveLetters(string value)
    {
        if (value.Length == 0) return value;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 2 && char.IsLetter(parts[i][0]) && parts[i][1] == ':')
            {
                parts[i] = char.ToUpperInvariant(parts[i][0]) + ":";
            }
        }
        return string.Join(" ", parts);
    }

    /// <summary>实例里的磁盘序号（排序用，纯函数）；不是「数字开头」的返回 <see cref="int.MaxValue"/>。</summary>
    public static int ParseDiskNumber(string? instance)
    {
        var text = (instance ?? "").Trim();
        if (text.Length == 0) return int.MaxValue;

        var space = text.IndexOf(' ');
        var head = space > 0 ? text[..space] : text;
        return int.TryParse(head, out var number) ? number : int.MaxValue;
    }

    /// <summary>把一次 PDH 采样值转成一条记录（负数 / NaN 按 0 处理）。</summary>
    public static DiskPerformanceInfo Create(string instance, double readBytesPerSecond, double writeBytesPerSecond)
        => new()
        {
            Instance = instance,
            Name = FormatName(instance),
            ReadBytesPerSecond = Sanitize(readBytesPerSecond),
            WriteBytesPerSecond = Sanitize(writeBytesPerSecond),
        };

    private static double Sanitize(double value)
        => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
}
