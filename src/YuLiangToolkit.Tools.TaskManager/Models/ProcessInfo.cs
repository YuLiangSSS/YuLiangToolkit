using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.TaskManager.Native;

namespace YuLiangToolkit.Tools.TaskManager.Models;

/// <summary>
/// 进程列表里的一行（可观察对象）。<b>同一个 PID 始终复用同一个对象</b>：
/// 每轮采样只调 <see cref="Update"/> 原地改值，这样列表选中项、滚动位置、
/// 实时排序（<c>ListCollectionView.IsLiveSorting</c>）都不会因为刷新而跳掉。
/// </summary>
public sealed class ProcessInfo : ObservableObject
{
    private int _pid;
    private string _name = "";
    private string _path = "";
    private double _cpuPercent;
    private long _workingSetBytes;
    private long _privateBytes;
    private double _gpuPercent;
    private long _gpuMemoryBytes;
    private bool _hasVisibleWindow;
    private IReadOnlyList<PortBinding> _ports = Array.Empty<PortBinding>();

    public ProcessInfo(int pid) => _pid = pid;

    public int Pid
    {
        get => _pid;
        private set => Set(ref _pid, value);
    }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    /// <summary>可执行文件完整路径；读不到（权限不足）时是空串。</summary>
    public string Path
    {
        get => _path;
        private set => Set(ref _path, value);
    }

    /// <summary>CPU 占用（0 ~ 100）。</summary>
    public double CpuPercent
    {
        get => _cpuPercent;
        private set => Set(ref _cpuPercent, value);
    }

    /// <summary>物理内存工作集（字节）。</summary>
    public long WorkingSetBytes
    {
        get => _workingSetBytes;
        private set => Set(ref _workingSetBytes, value);
    }

    /// <summary>私有提交内存（字节）。</summary>
    public long PrivateBytes
    {
        get => _privateBytes;
        private set => Set(ref _privateBytes, value);
    }

    /// <summary>GPU 引擎占用（0 ~ 100；驱动不提供计数时是 0）。</summary>
    public double GpuPercent
    {
        get => _gpuPercent;
        private set => Set(ref _gpuPercent, value);
    }

    /// <summary>GPU 专用显存（字节）。</summary>
    public long GpuMemoryBytes
    {
        get => _gpuMemoryBytes;
        private set => Set(ref _gpuMemoryBytes, value);
    }

    public bool HasVisibleWindow
    {
        get => _hasVisibleWindow;
        private set => Set(ref _hasVisibleWindow, value);
    }

    /// <summary>这个进程占用的端口（TCP / UDP）。</summary>
    public IReadOnlyList<PortBinding> Ports
    {
        get => _ports;
        private set
        {
            if (Set(ref _ports, value)) OnPropertyChanged(nameof(PortsText));
        }
    }

    // ---------------------------------------------------------------- 界面文本

    public string PidText => _pid.ToString();

    /// <summary>占用的端口条数（排序 / 摘要用）。</summary>
    public int PortCount => _ports.Count;

    public string CpuText => DisplayFormat.Percent(_cpuPercent);

    public string MemoryText => DisplayFormat.Bytes(_workingSetBytes);

    /// <summary>没有 GPU 数据时显示「—」，避免让用户误以为「0% 就是没用显卡」。</summary>
    public string GpuText => _gpuPercent > 0 ? DisplayFormat.Percent(_gpuPercent) : "—";

    public string GpuMemoryText => _gpuMemoryBytes > 0 ? DisplayFormat.Bytes(_gpuMemoryBytes) : "—";

    /// <summary>列表里的端口摘要（最多 3 个，多了显示 +N）。</summary>
    public string PortsText => DisplayFormat.Ports(_ports, 3);

    /// <summary>是否算「系统进程」（隐藏系统进程的开关用它判断）。</summary>
    public bool IsSystemProcess => ProcessProtection.IsCritical(_name) || string.IsNullOrEmpty(_path);

    /// <summary>是否受保护的关键进程（结束任务前界面会直接拦下并说明原因）。</summary>
    public bool IsCriticalProcess => ProcessProtection.IsCritical(_name);

    // ---------------------------------------------------------------- 更新 / 筛选

    /// <summary>把一轮采样的原始数据合并进这一行；值没变就不发通知（省绑定刷新）。</summary>
    public void Update(ProcessSampleData data)
    {
        var portsChanged = !SamePorts(_ports, data.Ports);
        var changed = _pid != data.Pid
            || !string.Equals(_name, data.Name, StringComparison.Ordinal)
            || !string.Equals(_path, data.Path, StringComparison.Ordinal)
            || _cpuPercent != data.CpuPercent
            || _workingSetBytes != data.WorkingSetBytes
            || _privateBytes != data.PrivateBytes
            || _gpuPercent != data.GpuPercent
            || _gpuMemoryBytes != data.GpuMemoryBytes
            || _hasVisibleWindow != data.HasVisibleWindow
            || portsChanged;

        if (!changed) return;

        _pid = data.Pid;
        _name = data.Name;
        _path = data.Path;
        _cpuPercent = data.CpuPercent;
        _workingSetBytes = data.WorkingSetBytes;
        _privateBytes = data.PrivateBytes;
        _gpuPercent = data.GpuPercent;
        _gpuMemoryBytes = data.GpuMemoryBytes;
        _hasVisibleWindow = data.HasVisibleWindow;
        _ports = data.Ports;

        // 一次性把所有绑定到的属性都通知一遍：WPF 会重新求值，实时排序也会重新比较。
        // （每 2 秒 × 几百行，通知量完全撑得住；比逐个字段写 Set() 可靠。）
        OnPropertyChanged(nameof(Pid));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(CpuPercent));
        OnPropertyChanged(nameof(WorkingSetBytes));
        OnPropertyChanged(nameof(PrivateBytes));
        OnPropertyChanged(nameof(GpuPercent));
        OnPropertyChanged(nameof(GpuMemoryBytes));
        OnPropertyChanged(nameof(HasVisibleWindow));
        OnPropertyChanged(nameof(Ports));
        OnPropertyChanged(nameof(PidText));
        OnPropertyChanged(nameof(PortCount));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(GpuText));
        OnPropertyChanged(nameof(GpuMemoryText));
        OnPropertyChanged(nameof(PortsText));
        OnPropertyChanged(nameof(IsSystemProcess));
        OnPropertyChanged(nameof(IsCriticalProcess));
    }

    /// <summary>搜索框是否命中这一行（进程名 / PID / 路径 / 端口都会被搜）。</summary>
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        keyword = keyword.Trim();
        return _name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || _path.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || _pid.ToString().Contains(keyword, StringComparison.Ordinal)
            || PortsText.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePorts(IReadOnlyList<PortBinding> a, IReadOnlyList<PortBinding> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].DisplayText, b[i].DisplayText, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
