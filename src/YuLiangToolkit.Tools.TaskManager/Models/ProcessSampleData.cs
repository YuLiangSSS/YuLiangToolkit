namespace YuLiangToolkit.Tools.TaskManager.Models;

/// <summary>
/// 一次采样里「一个进程」的原始数据（纯数据，不带界面通知）。
/// 由 <c>System.ProcessSampler</c> 填出来，再交给 <see cref="ProcessInfo"/> 更新到界面上。
/// </summary>
public sealed class ProcessSampleData
{
    public int Pid { get; init; }

    /// <summary>进程名（带扩展名，例如 <c>chrome.exe</c>）。</summary>
    public string Name { get; init; } = "";

    /// <summary>可执行文件完整路径；系统关键进程读不到时为空串。</summary>
    public string Path { get; init; } = "";

    /// <summary>CPU 占用（0 ~ 100，按「本机全部逻辑核心」归一化）。</summary>
    public double CpuPercent { get; init; }

    /// <summary>物理内存工作集（字节）。</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>私有提交内存（字节）。</summary>
    public long PrivateBytes { get; init; }

    /// <summary>GPU 引擎占用（0 ~ 100，本机所有 GPU 引擎合计；没有数据时为 0）。</summary>
    public double GpuPercent { get; init; }

    /// <summary>GPU 专用显存占用（字节；没有数据时为 0）。</summary>
    public long GpuMemoryBytes { get; init; }

    /// <summary>是否有可见主窗口（用来区分后台进程）。</summary>
    public bool HasVisibleWindow { get; init; }

    /// <summary>这个进程占用的端口（可能为空）。</summary>
    public IReadOnlyList<PortBinding> Ports { get; init; } = Array.Empty<PortBinding>();
}

/// <summary>
/// 选中进程的「详情」数据。这些属性读取代价高 / 部分进程会拒绝访问，
/// 所以只在用户选中某一行时才单独查一次，不参与每轮采样。
/// </summary>
public sealed class ProcessDetailsData
{
    public int Pid { get; init; }
    public string Path { get; init; } = "";
    public string UserName { get; init; } = "";
    public string MainWindowTitle { get; init; } = "";
    public string PriorityText { get; init; } = "";
    public DateTime? StartTime { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public long VirtualBytes { get; init; }

    /// <summary>读取过程中的问题（例如「拒绝访问」）；为空表示正常。</summary>
    public string Error { get; init; } = "";
}

/// <summary>
/// 一轮进程采样的聚合结果：进程列表 + 这一轮抓到的全部端口占用。
/// 端口单独放在这里，是为了让界面可以复用同一份快照算「选中进程的端口」。
/// </summary>
public sealed class ProcessSampleResult
{
    public IReadOnlyList<ProcessSampleData> Processes { get; init; } = Array.Empty<ProcessSampleData>();

    /// <summary>本轮抓到的全部端口（TCP + UDP，含 IPv4 / IPv6）。</summary>
    public IReadOnlyList<PortBinding> Ports { get; init; } = Array.Empty<PortBinding>();

    /// <summary>本轮枚举到的进程总数（含被界面筛选隐藏的）。</summary>
    public int TotalProcessCount { get; init; }

    /// <summary>本轮枚举到的线程总数。</summary>
    public int TotalThreadCount { get; init; }

    /// <summary>端口表是否抓取成功（失败时界面提示「需要管理员权限」之类的说明）。</summary>
    public bool PortsAvailable { get; init; } = true;

    /// <summary>端口抓取失败原因（<see cref="PortsAvailable"/> 为 false 时才有内容）。</summary>
    public string PortError { get; init; } = "";
}
