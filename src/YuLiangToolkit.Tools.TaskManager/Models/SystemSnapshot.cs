using YuLiangToolkit.Tools.TaskManager.Native;

namespace YuLiangToolkit.Tools.TaskManager.Models;

/// <summary>
/// 系统整体快照（CPU / 内存 / GPU / 硬盘 / 整机信息）。
/// 每轮采样生成一个新对象、整体替换，所以这里不需要逐属性通知（见 ViewModel 的 <c>Overview</c>）。
/// </summary>
public sealed class SystemSnapshot
{
    public DateTime SampledAt { get; init; } = DateTime.Now;

    /// <summary>CPU 总占用（0 ~ 100）。</summary>
    public double CpuPercent { get; init; }

    /// <summary>物理内存总量（字节）。</summary>
    public long TotalMemoryBytes { get; init; }

    /// <summary>可用物理内存（字节）。</summary>
    public long AvailableMemoryBytes { get; init; }

    /// <summary>GPU 总占用（0 ~ 100）；没有显卡计数器时为 0。</summary>
    public double GpuPercent { get; init; }

    /// <summary>GPU 计数器是否可用（集显 / 老驱动 / 虚拟机可能没有）。</summary>
    public bool GpuAvailable { get; init; } = true;

    /// <summary>所有进程的 GPU 专用显存合计（字节）。</summary>
    public long GpuDedicatedBytes { get; init; }

    /// <summary>磁盘读取速度（字节/秒）；没有性能计数器时为 0。</summary>
    public double DiskReadBytesPerSecond { get; init; }

    /// <summary>磁盘写入速度（字节/秒）。</summary>
    public double DiskWriteBytesPerSecond { get; init; }

    /// <summary>磁盘计数器是否可用。</summary>
    public bool DiskAvailable { get; init; } = true;

    public string CpuName { get; init; } = "";
    public string GpuName { get; init; } = "";
    public string OsText { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public TimeSpan Uptime { get; init; }

    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }

    /// <summary>固定磁盘（系统盘 / 数据盘）的容量信息。</summary>
    public IReadOnlyList<DiskVolumeInfo> Volumes { get; init; } = Array.Empty<DiskVolumeInfo>();

    // ---------------------------------------------------------------- 计算 / 文本

    public long UsedMemoryBytes => Math.Max(0, TotalMemoryBytes - AvailableMemoryBytes);

    public double MemoryPercent => TotalMemoryBytes <= 0 ? 0 : UsedMemoryBytes * 100.0 / TotalMemoryBytes;

    public string CpuText => DisplayFormat.Percent(CpuPercent);

    public string MemoryText => TotalMemoryBytes <= 0
        ? "—"
        : $"{DisplayFormat.Bytes(UsedMemoryBytes)} / {DisplayFormat.Bytes(TotalMemoryBytes)}";

    public string MemoryPercentText => DisplayFormat.Percent(MemoryPercent);

    public string GpuText => GpuAvailable ? DisplayFormat.Percent(GpuPercent) : "不可用";

    public string GpuMemoryText => GpuDedicatedBytes > 0 ? DisplayFormat.Bytes(GpuDedicatedBytes) : "—";

    public string DiskReadText => DiskAvailable ? DisplayFormat.Rate(DiskReadBytesPerSecond) : "不可用";

    public string DiskWriteText => DiskAvailable ? DisplayFormat.Rate(DiskWriteBytesPerSecond) : "不可用";

    public string UptimeText => DisplayFormat.Uptime(Uptime);

    public string ProcessCountText => $"{ProcessCount:N0} 个进程 · {ThreadCount:N0} 个线程";

    /// <summary>概览卡片底部的一句话（CPU 型号 / 显卡型号 / 系统版本三选一，界面用标签页或轮换都行）。</summary>
    public string HardwareText
    {
        get
        {
            var parts = new[] { CpuName, GpuName, OsText }.Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>一个固定磁盘（分区）的容量信息。</summary>
public sealed class DiskVolumeInfo
{
    /// <summary>盘符，例如 <c>C:\</c>。</summary>
    public string Name { get; init; } = "";

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;

    public string UsedText => $"{DisplayFormat.Bytes(UsedBytes)} / {DisplayFormat.Bytes(TotalBytes)}";

    public string FreeText => $"剩余 {DisplayFormat.Bytes(FreeBytes)}";

    public string PercentText => DisplayFormat.Percent(UsedPercent);

    public override string ToString() => $"{Name} {PercentText}";
}
