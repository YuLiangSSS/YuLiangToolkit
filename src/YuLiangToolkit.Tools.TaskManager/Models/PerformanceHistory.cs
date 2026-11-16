namespace YuLiangToolkit.Tools.TaskManager.Models;

/// <summary>
/// 一条性能曲线的历史缓冲（环形队列的思想，但用简单 List + 上限裁剪，量很小不值当写环形）。
/// 每条曲线固定保留最近 <see cref="Capacity"/> 个 0~100 的百分比点，供底部「性能」卡片的折线图渲染。
///
/// <para><b>线程</b>：只在 UI 线程写（<c>ApplySample</c> 里 Push）与读（折线控件 Redraw），不做加锁。
/// 每轮采样只 <see cref="Push"/> 一次 4 条曲线，负担可忽略。</para>
/// </summary>
public sealed class PerformanceHistory
{
    /// <summary>每条曲线保留的点数（约 = 采样轮数；2 秒一轮 ≈ 最近 2 分钟）。</summary>
    public const int Capacity = 60;

    private readonly double[] _cpu = new double[Capacity];
    private readonly double[] _memory = new double[Capacity];
    private readonly double[] _gpu = new double[Capacity];
    private readonly double[] _disk = new double[Capacity];

    private int _count;
    private int _head;

    /// <summary>当前存了多少个点（0 ~ <see cref="Capacity"/>）。</summary>
    public int Count => _count;

    /// <summary>某个序列按下标（0 = 最旧，Count-1 = 最新）取值；下标越界返回 0。</summary>
    public double this[PerformanceSeries series, int index]
    {
        get
        {
            if (index < 0 || index >= _count) return 0;
            var buffer = Buffer(series);
            var physical = (_head - _count + index + Capacity * 2) % Capacity;
            return buffer[physical];
        }
    }

    /// <summary>追加一轮采样（4 条曲线同进）。</summary>
    public void Push(double cpu, double memory, double gpu, double disk)
    {
        _cpu[_head] = Clamp(cpu);
        _memory[_head] = Clamp(memory);
        _gpu[_head] = Clamp(gpu);
        _disk[_head] = Clamp(disk);

        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>最新一个点（没有数据时返回 0）。</summary>
    public double Latest(PerformanceSeries series)
        => _count == 0 ? 0 : this[series, _count - 1];

    /// <summary>清空（切换监测开关 / 重开页面时用）。</summary>
    public void Clear()
    {
        _count = 0;
        _head = 0;
    }

    private double[] Buffer(PerformanceSeries series) => series switch
    {
        PerformanceSeries.Memory => _memory,
        PerformanceSeries.Gpu => _gpu,
        PerformanceSeries.Disk => _disk,
        _ => _cpu,
    };

    private static double Clamp(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Clamp(value, 0, 100);
}

/// <summary>性能曲线序列（CPU / 内存 / GPU / 磁盘）。</summary>
public enum PerformanceSeries
{
    Cpu,
    Memory,
    Gpu,
    Disk,
}
