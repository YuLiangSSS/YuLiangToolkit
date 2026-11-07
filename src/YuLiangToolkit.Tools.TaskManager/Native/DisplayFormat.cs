using System.Globalization;
using YuLiangToolkit.Tools.TaskManager.Models;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 界面上的数字格式化轮子（纯函数，自检可以直接断言）。
/// 统一用 1024 进制；CPU / 内存 / GPU / 速度的文案都从这里出，避免每个地方各写一套。
/// </summary>
public static class DisplayFormat
{
    private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>字节数 → <c>512 B</c> / <c>1.5 KB</c> / <c>3.2 GB</c>（负数 / 越界统一显示「—」）。</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes == 0) return "0 B";

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value:0.#} {ByteUnits[unit]}";
    }

    /// <summary>速度（字节/秒）→ <c>0 B/s</c> / <c>820 KB/s</c> / <c>1.5 MB/s</c>。</summary>
    public static string Rate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 0) return "—";
        if (bytesPerSecond < 1) return "0 B/s";
        return Bytes((long)bytesPerSecond) + "/s";
    }

    /// <summary>百分比 → <c>12.3%</c>（NaN 显示「—」，并按 0 ~ 100 夹取）。</summary>
    public static string Percent(double percent)
    {
        if (double.IsNaN(percent)) return "—";
        return $"{Math.Clamp(percent, 0, 100):0.0}%";
    }

    /// <summary>百分比数值（不带 %），用于进度条文字旁边。</summary>
    public static string PercentValue(double percent)
        => double.IsNaN(percent) ? "—" : Math.Clamp(percent, 0, 100).ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>运行时长 → <c>3 天 04:12:33</c> / <c>04:12:33</c>。</summary>
    public static string Uptime(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var days = (int)span.TotalDays;
        return days > 0
            ? $"{days} 天 {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>
    /// 端口列表摘要：<c>443, 8080 +2</c>。没有端口返回空串（列表里显示空白更干净）。
    /// </summary>
    public static string Ports(IEnumerable<PortBinding>? ports, int max = 3)
    {
        if (ports is null) return "";
        var numbers = new List<int>();
        foreach (var port in ports)
        {
            if (port.LocalPort <= 0) continue;
            if (!numbers.Contains(port.LocalPort)) numbers.Add(port.LocalPort);
        }
        if (numbers.Count == 0) return "";
        numbers.Sort();
        if (max < 1) max = 1;
        if (numbers.Count <= max) return string.Join(", ", numbers);
        return string.Join(", ", numbers.Take(max)) + $" +{numbers.Count - max}";
    }

    /// <summary>TCP 状态码 → 文本（见 <c>MIB_TCP_STATE</c> 枚举）；未知码原样显示数字。</summary>
    public static string TcpState(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTENING",
        3 => "SYN_SENT",
        4 => "SYN_RECEIVED",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => state.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>路径太长时中间省略（列表 / 详情都能用）。</summary>
    public static string ShortPath(string? path, int max = 60)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (max < 8) max = 8;
        if (path.Length <= max) return path;

        // 优先保留盘符 + 末尾文件名，中间用 … 省略
        var keepHead = Math.Min(12, max / 3);
        var keepTail = max - keepHead - 1;
        return path[..keepHead] + "…" + path[^keepTail..];
    }
}
