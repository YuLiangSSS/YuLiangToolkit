using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using YuLiangToolkit.Tools.TaskManager.Models;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 进程采样器：每 2 秒调一次 <see cref="Sample"/> 拿进程列表（名称 / 路径 / CPU / 内存 / GPU / 端口），
/// 用户选中某一行时调 <see cref="ReadDetails"/> 补详情（用户名 / 优先级 / 启动时间 / 句柄 / 虚拟内存）。
///
/// <para><b>CPU 怎么算</b>：进程的 <c>TotalProcessorTime</c> 是累计值，自己维护
/// 「PID → (上次累计 CPU 时间, 上次墙钟)」，用两次差值算占用率，并按
/// <see cref="Environment.ProcessorCount"/> 归一化到 0 ~ 100。第一次出现的进程 / 刚启动的进程
/// 没有上次基准，直接给 0（任务管理器也一样，第一格不准是正常的）。</para>
///
/// <para><b>路径为什么缓存</b>：读路径要 <c>OpenProcess</c> + <c>QueryFullProcessImageNameW</c>，
/// 几百个进程每 2 秒都查一遍太浪费；按 PID 缓存，进程消失后清掉。读不到（受保护的系统进程）
/// 缓存空串，界面会按「系统进程」处理。PID 被复用时用「CPU 累计时间变小」识别并清掉旧缓存。</para>
///
/// <para><b>GPU / 端口都可能不可用</b>：GPU 计数器依赖显卡驱动（虚拟机 / 老驱动没有），
/// 加不上时所有进程的 GPU 都是 0，<b>不报错</b>；端口表读取失败时
/// <see cref="ProcessSampleResult.PortsAvailable"/> 为 false 并带中文原因，进程列表照常返回。</para>
///
/// <para><b>不泄漏句柄</b>：<see cref="Process.GetProcesses"/> 返回的每个 <see cref="Process"/>
/// 都 <c>using</c> 掉；<c>OpenProcess</c> 句柄在 finally 里 <c>CloseHandle</c>；
/// 内部 PDH 查询在 <see cref="Dispose"/> 里关闭，重复 Dispose 安全。</para>
/// </summary>
public sealed class ProcessSampler : IDisposable
{
    /// <summary>GPU 引擎占用（通配实例，实例名里带 PID）。部分显卡驱动没有这个计数器。</summary>
    private const string GpuEngineCounter = @"\GPU Engine(*)\Utilization Percentage";

    /// <summary>每个进程的 GPU 专用显存（字节，通配实例，实例名里带 PID）。</summary>
    private const string GpuProcessMemoryCounter = @"\GPU Process Memory(*)\Dedicated Usage";

    private readonly object _sync = new();
    private readonly object _pathLock = new();

    /// <summary>PID → 上一次的累计 CPU 时间 / 墙钟，用来算两次采样之间的差值。</summary>
    private readonly Dictionary<int, CpuSample> _cpuSamples = new();

    /// <summary>PID → 可执行文件路径（读不到缓存空串，避免每轮都重复 OpenProcess）。</summary>
    private readonly Dictionary<int, string> _paths = new();

    private PdhQuery? _gpuQuery;
    private bool _gpuAttempted;
    private bool _disposed;

    /// <summary>
    /// 采样一轮进程。
    /// </summary>
    /// <param name="includePorts">
    /// 是否同时抓端口占用。关掉可以省一点开销（<c>GetExtendedTcpTable</c> 在进程很多时也要几毫秒）；
    /// 打开时端口表失败不会影响进程列表，只会体现在 <see cref="ProcessSampleResult.PortsAvailable"/> / <see cref="ProcessSampleResult.PortError"/>。
    /// </param>
    public ProcessSampleResult Sample(bool includePorts)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return new ProcessSampleResult { PortsAvailable = !includePorts };
            }

            // ① 端口表：先抓，后面按 PID 挂到每一行上。失败只降级，不打断进程采样。
            IReadOnlyList<PortBinding> allPorts = Array.Empty<PortBinding>();
            var portsAvailable = true;
            var portError = "";
            if (includePorts)
            {
                try
                {
                    allPorts = PortMapper.Scan();
                }
                catch (Exception exception)
                {
                    portsAvailable = false;
                    portError = DescribePortError(exception);
                }
            }

            var portsByPid = new Dictionary<int, List<PortBinding>>();
            foreach (var binding in allPorts)
            {
                if (!portsByPid.TryGetValue(binding.Pid, out var list))
                {
                    list = new List<PortBinding>();
                    portsByPid[binding.Pid] = list;
                }

                list.Add(binding);
            }

            foreach (var list in portsByPid.Values)
            {
                list.Sort(static (left, right) => left.LocalPort.CompareTo(right.LocalPort));
            }

            // ② GPU：内部持有一个 PDH 查询（只创建一次），拿不到数据就全 0。
            var (gpuPercentByPid, gpuMemoryByPid) = CollectGpu();

            // ③ 进程列表：每个 Process 都 Dispose；单项失败只跳过该项。
            var samples = new List<ProcessSampleData>(512);
            var alivePids = new HashSet<int>();
            var totalThreads = 0;
            var timestamp = Stopwatch.GetTimestamp();

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                processes = Array.Empty<Process>();
            }

            foreach (var process in processes)
            {
                try
                {
                    using (process)
                    {
                        int pid;
                        try
                        {
                            pid = process.Id;
                        }
                        catch
                        {
                            continue;
                        }

                        alivePids.Add(pid);

                        var cpuPercent = 0.0;
                        try
                        {
                            cpuPercent = UpdateCpu(pid, process.TotalProcessorTime, timestamp);
                        }
                        catch
                        {
                            // 受保护 / 刚好退出的进程读不到累计 CPU 时间，这一轮按 0 处理
                        }

                        var path = GetCachedPath(pid);
                        var name = ReadProcessName(process, path);

                        long workingSet = 0;
                        try
                        {
                            workingSet = process.WorkingSet64;
                        }
                        catch
                        {
                            // 读不到就给 0，界面显示 0 B 总比整行消失好
                        }

                        long privateBytes = 0;
                        try
                        {
                            privateBytes = process.PrivateMemorySize64;
                        }
                        catch
                        {
                        }

                        var hasVisibleWindow = false;
                        try
                        {
                            hasVisibleWindow = process.MainWindowHandle != IntPtr.Zero;
                        }
                        catch
                        {
                        }

                        try
                        {
                            totalThreads += process.Threads.Count;
                        }
                        catch
                        {
                            // 线程数读不到就跳过（总数会略偏小，不影响功能）
                        }

                        gpuPercentByPid.TryGetValue(pid, out var gpuPercent);
                        gpuMemoryByPid.TryGetValue(pid, out var gpuMemory);

                        samples.Add(new ProcessSampleData
                        {
                            Pid = pid,
                            Name = name,
                            Path = path,
                            CpuPercent = cpuPercent,
                            WorkingSetBytes = workingSet,
                            PrivateBytes = privateBytes,
                            GpuPercent = gpuPercent,
                            GpuMemoryBytes = gpuMemory,
                            HasVisibleWindow = hasVisibleWindow,
                            Ports = portsByPid.TryGetValue(pid, out var ports) ? ports : Array.Empty<PortBinding>(),
                        });
                    }
                }
                catch
                {
                    // 单个进程对象异常（枚举过程中退出等）不影响其它行
                }
            }

            // ④ 清理已经消失的进程缓存，避免长期运行时字典越涨越大。
            PruneMissing(_cpuSamples, alivePids);
            PrunePaths(alivePids);

            return new ProcessSampleResult
            {
                Processes = samples,
                Ports = allPorts,
                TotalProcessCount = samples.Count,
                TotalThreadCount = totalThreads,
                PortsAvailable = portsAvailable,
                PortError = portError,
            };
        }
    }

    /// <summary>
    /// 读单个进程的详情。读取代价高（要开令牌、查线程 / 句柄），只在用户选中某一行时调用。
    /// <b>任何一项失败都不抛异常</b>：能读到的照常填，读不到的写进
    /// <see cref="ProcessDetailsData.Error"/>（中文说明，例如「拒绝访问，进程受保护」）。
    /// </summary>
    public ProcessDetailsData ReadDetails(int pid)
    {
        if (pid <= 0)
        {
            return new ProcessDetailsData { Pid = pid, Error = "无效的 PID。" };
        }

        if (_disposed)
        {
            return new ProcessDetailsData { Pid = pid, Error = "采样器已释放，请重新打开工具。" };
        }

        var error = "";
        var path = GetCachedPath(pid);
        var windowTitle = "";
        var priorityText = "";
        DateTime? startTime = null;
        var threadCount = 0;
        var handleCount = 0;
        long virtualBytes = 0;

        try
        {
            using var process = Process.GetProcessById(pid);

            try
            {
                windowTitle = process.MainWindowTitle ?? "";
            }
            catch
            {
                AppendError(ref error, "读不到主窗口标题（或进程受保护）");
            }

            try
            {
                priorityText = FormatPriority(process.PriorityClass);
            }
            catch
            {
                AppendError(ref error, "拒绝访问（读不到优先级，进程受保护）");
            }

            try
            {
                startTime = process.StartTime;
            }
            catch
            {
                AppendError(ref error, "拒绝访问（读不到启动时间，进程受保护）");
            }

            try
            {
                threadCount = process.Threads.Count;
            }
            catch
            {
                AppendError(ref error, "拒绝访问（读不到线程数，进程受保护）");
            }

            try
            {
                handleCount = process.HandleCount;
            }
            catch
            {
                AppendError(ref error, "拒绝访问（读不到句柄数，进程受保护）");
            }

            try
            {
                virtualBytes = process.VirtualMemorySize64;
            }
            catch
            {
                AppendError(ref error, "拒绝访问（读不到虚拟内存，进程受保护）");
            }
        }
        catch (ArgumentException)
        {
            return new ProcessDetailsData { Pid = pid, Error = "进程不存在（可能刚刚退出）。" };
        }
        catch (Exception exception)
        {
            return new ProcessDetailsData { Pid = pid, Error = "读取进程详情失败：" + exception.Message };
        }

        var userName = TryReadUserName(pid);
        if (path.Length == 0)
        {
            AppendError(ref error, "读不到程序路径（系统进程或权限不足）");
        }

        return new ProcessDetailsData
        {
            Pid = pid,
            Path = path,
            UserName = userName,
            MainWindowTitle = windowTitle,
            PriorityText = priorityText,
            StartTime = startTime,
            ThreadCount = threadCount,
            HandleCount = handleCount,
            VirtualBytes = virtualBytes,
            Error = error,
        };
    }

    /// <summary>关闭内部 PDH 查询并清缓存；重复调用安全（采样线程可能刚好在 Dispose 后跑完一轮）。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            _gpuQuery?.Dispose();
            _gpuQuery = null;
            _cpuSamples.Clear();
        }

        lock (_pathLock)
        {
            _paths.Clear();
        }
    }

    // ------------------------------------------------------------------ CPU 差值

    /// <summary>
    /// 累计 CPU 时间 → 本轮占用率。第一次出现（没有上次基准）/ 墙钟太短 / PID 被复用 → 0。
    /// 必须在 <see cref="_sync"/> 锁内调用。
    /// </summary>
    private double UpdateCpu(int pid, TimeSpan totalProcessorTime, long timestamp)
    {
        var cpuTicks = totalProcessorTime.Ticks;

        if (!_cpuSamples.TryGetValue(pid, out var previous))
        {
            _cpuSamples[pid] = new CpuSample(cpuTicks, timestamp);
            return 0;
        }

        _cpuSamples[pid] = new CpuSample(cpuTicks, timestamp);

        if (cpuTicks < previous.CpuTicks)
        {
            // 累计时间变小 = 旧进程已退出、PID 被新进程复用：旧路径缓存作废，这一轮按 0 处理。
            lock (_pathLock)
            {
                _paths.Remove(pid);
            }

            return 0;
        }

        var wallSeconds = (timestamp - previous.Timestamp) / (double)Stopwatch.Frequency;
        if (wallSeconds <= 0.05) return 0;

        var cpuSeconds = (cpuTicks - previous.CpuTicks) / (double)TimeSpan.TicksPerSecond;
        if (cpuSeconds < 0) return 0;

        var percent = cpuSeconds / wallSeconds / Math.Max(1, Environment.ProcessorCount) * 100.0;
        return Math.Clamp(percent, 0, 100);
    }

    // ------------------------------------------------------------------ GPU

    /// <summary>第一次采样时才创建 GPU 查询；加不上计数器（没显卡 / 老驱动）就一直当 0，不再重试。</summary>
    private void EnsureGpuQuery()
    {
        if (_gpuAttempted) return;
        _gpuAttempted = true;

        try
        {
            var query = new PdhQuery();
            var engineAdded = query.AddCounter(GpuEngineCounter);
            var memoryAdded = query.AddCounter(GpuProcessMemoryCounter);
            if (engineAdded || memoryAdded)
            {
                _gpuQuery = query;
            }
            else
            {
                query.Dispose();
            }
        }
        catch
        {
            _gpuQuery = null;
        }
    }

    /// <summary>按 PID 汇总 GPU：引擎占用累加后夹到 0 ~ 100，专用显存按字节累加。</summary>
    private (Dictionary<int, double> Percent, Dictionary<int, long> Memory) CollectGpu()
    {
        var percentByPid = new Dictionary<int, double>();
        var memoryByPid = new Dictionary<int, long>();

        EnsureGpuQuery();
        var query = _gpuQuery;
        if (query is null || !query.Collect())
        {
            return (percentByPid, memoryByPid);
        }

        foreach (var (instance, value) in query.GetArray(GpuEngineCounter))
        {
            var pid = ParseGpuInstancePid(instance);
            if (pid <= 0) continue;

            percentByPid.TryGetValue(pid, out var current);
            percentByPid[pid] = Math.Clamp(current + value, 0, 100);
        }

        foreach (var (instance, value) in query.GetArray(GpuProcessMemoryCounter))
        {
            var pid = ParseGpuInstancePid(instance);
            if (pid <= 0) continue;

            memoryByPid.TryGetValue(pid, out var current);
            memoryByPid[pid] = SaturatingAdd(current, ToByteCount(value));
        }

        return (percentByPid, memoryByPid);
    }

    /// <summary>
    /// 从 GPU 实例名里抠出 PID，形如
    /// <c>pid_1234_luid_0x00000000_0x0000C5E4_phys_0_eng_0_engtype_3D</c>；
    /// 认不出返回 0（各驱动偶尔会改格式，认不出就当没有，不崩）。
    /// </summary>
    private static int ParseGpuInstancePid(string instance)
    {
        const string marker = "pid_";
        if (string.IsNullOrEmpty(instance)) return 0;

        var index = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return 0;

        var position = index + marker.Length;
        var value = 0;
        var digits = 0;
        while (position < instance.Length && char.IsDigit(instance[position]) && digits < 9)
        {
            value = value * 10 + (instance[position] - '0');
            position++;
            digits++;
        }

        return digits == 0 ? 0 : value;
    }

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

    // ------------------------------------------------------------------ 路径 / 名称

    /// <summary>按 PID 取路径（带缓存）。必须在 <see cref="_pathLock"/> 锁内访问缓存。</summary>
    private string GetCachedPath(int pid)
    {
        lock (_pathLock)
        {
            if (_paths.TryGetValue(pid, out var cached)) return cached;

            var path = TryQueryImageName(pid);
            _paths[pid] = path;
            return path;
        }
    }

    private void PrunePaths(HashSet<int> alivePids)
    {
        lock (_pathLock)
        {
            PruneMissing(_paths, alivePids);
        }
    }

    /// <summary>读进程映像路径；失败返回空串（拒绝访问 / 进程已退出都很常见）。</summary>
    private static string TryQueryImageName(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return "";

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return "";

            return buffer.ToString().TrimEnd('\0');
        }
        catch
        {
            return "";
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 进程名统一带 <c>.exe</c>（<see cref="ProcessSampleData.Name"/> 的约定）；
    /// <c>ProcessName</c> 读不到时退回路径的文件名。
    /// </summary>
    private static string ReadProcessName(Process process, string path)
    {
        var name = "";
        try
        {
            name = process.ProcessName;
        }
        catch
        {
        }

        if (name.Length == 0 && path.Length > 0)
        {
            try
            {
                name = Path.GetFileName(path);
            }
            catch
            {
            }
        }

        if (name.Length > 0 && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        return name;
    }

    // ------------------------------------------------------------------ 用户名 / 详情辅助

    /// <summary>
    /// 取进程所属用户（<c>OpenProcess</c> → <c>OpenProcessToken</c> → <c>GetTokenInformation(TokenUser)</c>
    /// → <c>ConvertSidToStringSidW</c> → 翻译成 <c>DOMAIN\User</c>）。任何一步失败返回空串。
    /// </summary>
    private static string TryReadUserName(int pid)
    {
        var processHandle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (processHandle == IntPtr.Zero) return "";

        var tokenHandle = IntPtr.Zero;
        var information = IntPtr.Zero;
        var sidString = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TokenQuery, out tokenHandle)
                || tokenHandle == IntPtr.Zero)
            {
                return "";
            }

            NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenUser, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return "";

            information = Marshal.AllocHGlobal(needed);
            if (!NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenUser, information, needed, out _))
            {
                return "";
            }

            // TOKEN_USER 的第一个字段是 SID_AND_ATTRIBUTES，它的第一个字段就是 SID 指针。
            var sid = Marshal.ReadIntPtr(information);
            if (sid == IntPtr.Zero) return "";
            if (!NativeMethods.ConvertSidToStringSidW(sid, out sidString) || sidString == IntPtr.Zero) return "";

            var text = Marshal.PtrToStringUni(sidString);
            if (string.IsNullOrEmpty(text)) return "";

            try
            {
                return new SecurityIdentifier(text).Translate(typeof(NTAccount)).Value;
            }
            catch
            {
                // 翻译失败（域信任问题 / 用户已被删）就退回 SID 字符串，至少能看出是哪个账户
                return text;
            }
        }
        catch
        {
            return "";
        }
        finally
        {
            if (sidString != IntPtr.Zero) NativeMethods.LocalFree(sidString);
            if (information != IntPtr.Zero) Marshal.FreeHGlobal(information);
            if (tokenHandle != IntPtr.Zero) NativeMethods.CloseHandle(tokenHandle);
            NativeMethods.CloseHandle(processHandle);
        }
    }

    /// <summary>优先级类 → 中文文本（任务管理器同款说法）。</summary>
    private static string FormatPriority(ProcessPriorityClass priority) => priority switch
    {
        ProcessPriorityClass.Idle => "低（空闲）",
        ProcessPriorityClass.BelowNormal => "低于正常",
        ProcessPriorityClass.Normal => "正常",
        ProcessPriorityClass.AboveNormal => "高于正常",
        ProcessPriorityClass.High => "高",
        ProcessPriorityClass.RealTime => "实时",
        _ => priority.ToString(),
    };

    private static void AppendError(ref string error, string message)
    {
        error = error.Length == 0 ? message : error + "；" + message;
    }

    private static string DescribePortError(Exception exception)
    {
        if (exception is Win32Exception win32 && win32.NativeErrorCode == 5)
        {
            return "需要管理员权限才能读取端口占用（拒绝访问）。";
        }

        return string.IsNullOrWhiteSpace(exception.Message)
            ? "读取端口占用失败。"
            : "读取端口占用失败：" + exception.Message;
    }

    // ------------------------------------------------------------------ 小工具

    /// <summary>删掉字典里已经不存在的 PID（先收集再删，避免遍历时改集合）。</summary>
    private static void PruneMissing<TValue>(Dictionary<int, TValue> map, HashSet<int> alivePids)
    {
        if (map.Count == 0) return;

        List<int>? dead = null;
        foreach (var key in map.Keys)
        {
            if (alivePids.Contains(key)) continue;
            (dead ??= new List<int>()).Add(key);
        }

        if (dead is null) return;

        foreach (var key in dead)
        {
            map.Remove(key);
        }
    }

    /// <summary>上一次采样时某个进程的累计 CPU 时间 + 当时的墙钟（<see cref="Stopwatch.GetTimestamp"/>）。</summary>
    private readonly struct CpuSample
    {
        public CpuSample(long cpuTicks, long timestamp)
        {
            CpuTicks = cpuTicks;
            Timestamp = timestamp;
        }

        /// <summary>累计 CPU 时间（100ns 单位）。</summary>
        public long CpuTicks { get; }

        /// <summary>采样时的 <see cref="Stopwatch.GetTimestamp"/>。</summary>
        public long Timestamp { get; }
    }
}
