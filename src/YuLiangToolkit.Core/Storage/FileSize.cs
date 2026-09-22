namespace YuLiangToolkit.Core.Storage;

/// <summary>字节数 → 人类可读大小（B / KB / MB / GB / TB）。</summary>
public static class FileSize
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>格式化字节数，例如 <c>1536</c> → <c>"1.5 KB"</c>。</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "-";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {Units[unit]}" : $"{value:0.0} {Units[unit]}";
    }
}
