using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using YuLiangToolkit.Tools.TaskManager.Models;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 整机采样器：CPU / 内存 / GPU / 磁盘读写 / 固定磁盘容量 / 硬件信息，每 2 秒调一次
/// <see cref="Sample"/>，生成一份 <see cref="SystemSnapshot"/>（界面整体替换，不做增量更新）。
///
/// <para><b>数据来源</b>：</para>
/// <list type="bullet">
/// <item>CPU：<c>GetSystemTimes</c> 两次差值（Kernel 增量里包含 Idle，所以占用 =
/// (总增量 − Idle 增量) / 总增量）；第一次没有基准，返回 0。</item>
/// <item>内存：<c>GlobalMemoryStatusEx</c> 的 <c>ullTotalPhys</c> / <c>ullAvailPhys</c>。</item>
/// <item>磁盘：PDH <c>\PhysicalDisk(_Total)\Disk Read/Write Bytes/sec</c>（虚拟机 / 精简系统可能没有）。</item>
/// <item>GPU：PDH <c>\GPU Engine(*)\Utilization Percentage</c> 全部实例求和后夹到 0 ~ 100；
/// 显存优先用 <c>\GPU Adapter Memory(*)\Dedicated Usage</c>，部分老驱动没有这个计数器时退回
/// <c>\GPU Process Memory(*)\Dedicated Usage</c>（两者都是字节，取到一个就够）。</item>
/// <item>硬件信息：注册表 CPU 型号 / 显卡驱动描述、<c>RtlGetVersion</c> 版本号、<c>GetTickCount64</c> 开机时长。</item>
/// </list>
///
/// <para><b>降级策略</b>：任何一项读不到都只影响那一项（GPU / 磁盘置「不可用」，硬件文本为空串），
/// 绝不抛异常；PDH 查询只在第一次采样时创建，<see cref="Dispose"/> 关闭，重复 Dispose 安全。</para>
/// </summary>
public sealed class SystemSampler : IDisposable
{
    private const string DiskReadCounter = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    private const string DiskWriteCounter = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    private const string GpuEngineCounter = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuAdapterMemoryCounter = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string GpuProcessMemoryCounter = @"\GPU Process Memory(*)\Dedicated Usage";

    private readonly object _sync = new();

    private PdhQuery? _diskQuery;
    private PdhQuery? _gpuQuery;
    private bool _diskAvailable;
    private bool _gpuAvailable;

    /// <summary>实际可用的显存计数器路径（优先 GPU Adapter Memory，退回 GPU Process Memory）。</summary>
    private string _gpuMemoryCounterPath = "";

    private bool _initialized;
    private bool _disposed;

    // CPU 总量需要两次采样做差：保存上一次的 Idle / 总时间。
    private bool _hasCpuBaseline;
    private ulong _previousIdle;
    private ulong _previousTotal;

    // 硬件信息不会变，读一次就缓存（注册表 / RtlGetVersion 不必每 2 秒查）。
    private string? _cpuName;
    private string? _gpuName;
    private string? _osText;

    /// <summary>采样一轮整机状态。第一次调用时 CPU / 磁盘 / GPU 的速度类数值可能为 0（差值计数器需要两次采样）。</summary>
    public SystemSnapshot Sample()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return new SystemSnapshot { SampledAt = DateTime.Now };
            }

            EnsureInitialized();

            var cpuPercent = ReadCpuPercent();
            var memory = ReadMemory();
            var disk = ReadDisk();
            var gpu = ReadGpu();
            var (processCount, threadCount) = CountProcesses();

            return new SystemSnapshot
            {
                SampledAt = DateTime.Now,
                CpuPercent = cpuPercent,
                TotalMemoryBytes = memory.Total,
                AvailableMemoryBytes = memory.Available,
                GpuPercent = gpu.Percent,
                GpuAvailable = gpu.Available,
                GpuDedicatedBytes = gpu.Bytes,
                DiskReadBytesPerSecond = disk.Read,
                DiskWriteBytesPerSecond = disk.Write,
                DiskAvailable = disk.Available,
                CpuName = CpuName,
                GpuName = GpuName,
                OsText = OsText,
                ComputerName = ReadComputerName(),
                Uptime = ReadUptime(),
                ProcessCount = processCount,
                ThreadCount = threadCount,
                Volumes = ReadVolumes(),
            };
        }
    }

    /// <summary>关闭 PDH 查询；重复调用安全（采样定时器可能在 Dispose 之后才跑完最后一轮）。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            _diskQuery?.Dispose();
            _gpuQuery?.Dispose();
            _diskQuery = null;
            _gpuQuery = null;
        }
    }

    // ------------------------------------------------------------------ 初始化

    /// <summary>第一次采样时才创建 PDH 查询；加不上计数器的机器上永久降级，不再反复重试。</summary>
    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var disk = new PdhQuery();
            var readAdded = disk.AddCounter(DiskReadCounter);
            var writeAdded = disk.AddCounter(DiskWriteCounter);
            if (readAdded && writeAdded)
            {
                _diskQuery = disk;
                _diskAvailable = true;
            }
            else
            {
                _diskAvailable = false;
                disk.Dispose();
            }
        }
        catch
        {
            _diskAvailable = false;
        }

        try
        {
            var gpu = new PdhQuery();
            var engineAdded = gpu.AddCounter(GpuEngineCounter);
            if (gpu.AddCounter(GpuAdapterMemoryCounter))
            {
                _gpuMemoryCounterPath = GpuAdapterMemoryCounter;
            }
            else if (gpu.AddCounter(GpuProcessMemoryCounter))
            {
                _gpuMemoryCounterPath = GpuProcessMemoryCounter;
            }

            if (engineAdded || _gpuMemoryCounterPath.Length > 0)
            {
                _gpuQuery = gpu;
                _gpuAvailable = engineAdded;
            }
            else
            {
                gpu.Dispose();
            }
        }
        catch
        {
            _gpuAvailable = false;
        }
    }

    // ------------------------------------------------------------------ CPU / 内存

    /// <summary>
    /// 整机 CPU 占用：<c>GetSystemTimes</c> 的 Kernel 增量包含 Idle 增量，
    /// 所以占用 = (总增量 − Idle 增量) / 总增量 × 100。
    /// </summary>
    private double ReadCpuPercent()
    {
        try
        {
            if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return 0;

            var idle = idleTime.ToUInt64();
            var total = kernelTime.ToUInt64() + userTime.ToUInt64();

            var percent = 0.0;
            if (_hasCpuBaseline && total > _previousTotal && idle >= _previousIdle)
            {
                var totalDelta = total - _previousTotal;
                var idleDelta = idle - _previousIdle;
                if (totalDelta > 0)
                {
                    var busyDelta = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
                    percent = busyDelta * 100.0 / totalDelta;
                }
            }

            _previousTotal = total;
            _previousIdle = idle;
            _hasCpuBaseline = true;
            return Math.Clamp(percent, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private static (long Total, long Available) ReadMemory()
    {
        try
        {
            var status = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
            };
            if (!NativeMethods.GlobalMemoryStatusEx(ref status)) return (0, 0);
            return (ToInt64(status.ullTotalPhys), ToInt64(status.ullAvailPhys));
        }
        catch
        {
            return (0, 0);
        }
    }

    // ------------------------------------------------------------------ 磁盘 / GPU

    private (double Read, double Write, bool Available) ReadDisk()
    {
        if (_diskQuery is null || !_diskAvailable) return (0, 0, false);

        try
        {
            if (!_diskQuery.Collect()) return (0, 0, true);

            var read = _diskQuery.GetSingleValue(DiskReadCounter) ?? 0;
            var write = _diskQuery.GetSingleValue(DiskWriteCounter) ?? 0;
            return (Math.Max(0, read), Math.Max(0, write), true);
        }
        catch
        {
            return (0, 0, true);
        }
    }

    private (double Percent, long Bytes, bool Available) ReadGpu()
    {
        if (_gpuQuery is null) return (0, 0, false);

        try
        {
            if (!_gpuQuery.Collect()) return (0, 0, _gpuAvailable);

            var percent = 0.0;
            foreach (var (_, value) in _gpuQuery.GetArray(GpuEngineCounter))
            {
                percent += value;
            }

            long bytes = 0;
            if (_gpuMemoryCounterPath.Length > 0)
            {
                foreach (var (_, value) in _gpuQuery.GetArray(_gpuMemoryCounterPath))
                {
                    bytes = SaturatingAdd(bytes, ToByteCount(value));
                }
            }

            return (Math.Clamp(percent, 0, 100), bytes, _gpuAvailable);
        }
        catch
        {
            return (0, 0, _gpuAvailable);
        }
    }

    // ------------------------------------------------------------------ 进制 / 数值小工具

    private static long ToInt64(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static long ToByteCount(double value)
    {
        if (double.IsNaN(value) || value <= 0) return 0;
        return value >= 9.0e18 ? long.MaxValue : (long)value;
    }

    private static long SaturatingAdd(long current, long addition)
    {
        if (addition <= 0) return current;
        return current > long.MaxValue - addition ? long.MaxValue : current + addition;
    }

    // ------------------------------------------------------------------ 硬件 / 系统信息

    private string CpuName => _cpuName ??= ReadCpuName();

    private string GpuName => _gpuName ??= ReadGpuName();

    private string OsText => _osText ??= ReadOsText();

    /// <summary>CPU 型号：<c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0</c> 的
    /// <c>ProcessorNameString</c>（这一项不随语言变化，中文系统也是英文型号）。</summary>
    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var value = key?.GetValue("ProcessorNameString") as string;
            return value?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 显卡描述：设备类键 <c>{4d36e968-…}</c> 下每个子键的 <c>DriverDesc</c> 去重后用 " / " 合并。
    /// 多显卡 / 虚拟机（Basic Display）会得到多条；读不到返回空串。
    /// </summary>
    private static string ReadGpuName()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null) return "";

            var names = new List<string>();
            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                try
                {
                    using var child = classKey.OpenSubKey(subKeyName);
                    var description = child?.GetValue("DriverDesc") as string;
                    if (description is null) continue;

                    description = description.Trim();
                    if (description.Length == 0) continue;
                    if (names.Exists(existing => string.Equals(existing, description, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    names.Add(description);
                }
                catch
                {
                    // 某个子键读不到不影响其它显卡
                }
            }

            return string.Join(" / ", names);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>系统版本：优先 <c>RtlGetVersion</c>（真实版本号），失败退回 <c>Environment.OSVersion</c>。</summary>
    private static string ReadOsText()
    {
        try
        {
            var info = new NativeMethods.RTL_OSVERSIONINFOEXW
            {
                dwOSVersionInfoSize = (uint)Marshal.SizeOf<NativeMethods.RTL_OSVERSIONINFOEXW>(),
                szCSDVersion = "",
            };
            if (NativeMethods.RtlGetVersion(ref info) == 0)
            {
                return $"Windows {info.dwMajorVersion}.{info.dwMinorVersion}.{info.dwBuildNumber}";
            }
        }
        catch
        {
        }

        try
        {
            var version = Environment.OSVersion.Version;
            return $"Windows {version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadComputerName()
    {
        try
        {
            return Environment.MachineName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static TimeSpan ReadUptime()
    {
        try
        {
            return TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64());
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>固定磁盘（系统盘 / 数据盘）容量；光驱 / U 盘 / 虚拟盘跳过（<c>TotalSize</c> 读不到也跳过）。</summary>
    private static IReadOnlyList<DiskVolumeInfo> ReadVolumes()
    {
        var volumes = new List<DiskVolumeInfo>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed) continue;
                    if (!drive.IsReady) continue;

                    var total = drive.TotalSize;
                    if (total <= 0) continue;

                    volumes.Add(new DiskVolumeInfo
                    {
                        Name = drive.Name,
                        TotalBytes = total,
                        FreeBytes = Math.Max(0, drive.AvailableFreeSpace),
                    });
                }
                catch
                {
                    // 单个分区读不到（权限 / 刚好弹出）就跳过
                }
            }
        }
        catch
        {
        }

        return volumes;
    }

    /// <summary>进程数 / 线程数（线程数读不到的进程只跳过线程计数）。</summary>
    private static (int Processes, int Threads) CountProcesses()
    {
        var processCount = 0;
        var threadCount = 0;
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                processCount++;
                try
                {
                    threadCount += process.Threads.Count;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }

        return (processCount, threadCount);
    }
}
