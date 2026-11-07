using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// PDH（Windows 性能计数器）查询的薄封装：一个 query + 若干个「英文路径」计数器。
///
/// <para><b>为什么用英文路径</b>：<c>PdhAddEnglishCounterW</c> 在中文系统上同样能解析
/// <c>\GPU Engine(*)\Utilization Percentage</c> 这种英文名，省掉每种语言的映射表；
/// 加不上（没有显卡 / 驱动太老 / 虚拟机没有物理磁盘计数器）就返回 false，上层显示「不可用」。</para>
///
/// <para><b>为什么首采要容忍失败</b>：Utilization / Bytes/sec 这类比率计数器必须采样两次才能算差值，
/// 第一次 <c>Collect</c> 之后 <c>PdhGetFormattedCounterValue</c> / <c>PdhGetFormattedCounterArrayW</c>
/// 会返回 <c>PDH_INVALID_DATA</c> / <c>PDH_NO_DATA</c>，所以这里统一转成 null / 空列表，
/// 绝不把异常抛给界面。</para>
///
/// <para><b>线程安全</b>：采样线程每 2 秒调一次，窗口关闭时可能和 <see cref="Dispose"/> 并发，
/// 所以所有方法都在锁里跑；<see cref="Dispose"/> 幂等（重复调用安全）。</para>
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private readonly object _sync = new();

    /// <summary>英文计数器路径 → PDH 计数器句柄（关闭 query 时由 PDH 统一释放）。</summary>
    private readonly Dictionary<string, IntPtr> _counters = new(StringComparer.Ordinal);

    private IntPtr _query;
    private bool _disposed;

    /// <summary>打开一个空查询；<c>pdh.dll</c> 不可用（理论上不会）时退化为「什么都加不上」。</summary>
    public PdhQuery()
    {
        try
        {
            if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out var query) == 0)
            {
                _query = query;
            }
        }
        catch
        {
            _query = IntPtr.Zero;
        }
    }

    /// <summary>加一个计数器（英文路径）。成功返回 true；没有这个计数器就返回 false，不抛异常。</summary>
    public bool AddCounter(string englishCounterPath)
    {
        if (string.IsNullOrWhiteSpace(englishCounterPath)) return false;

        lock (_sync)
        {
            if (_disposed || _query == IntPtr.Zero) return false;
            if (_counters.ContainsKey(englishCounterPath)) return true;

            try
            {
                if (NativeMethods.PdhAddEnglishCounterW(_query, englishCounterPath, IntPtr.Zero, out var counter) != 0
                    || counter == IntPtr.Zero)
                {
                    return false;
                }

                _counters[englishCounterPath] = counter;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>采样一次（每轮采样调一次）。查询已关闭 / 已释放时返回 false。</summary>
    public bool Collect()
    {
        lock (_sync)
        {
            if (_disposed || _query == IntPtr.Zero) return false;

            try
            {
                return NativeMethods.PdhCollectQueryData(_query) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 取单个计数器的值（非通配实例）。失败 / 首采无效 / 没有这个计数器 → null。
    /// </summary>
    public double? GetSingleValue(string englishCounterPath)
    {
        lock (_sync)
        {
            if (_disposed || _query == IntPtr.Zero) return null;
            if (!_counters.TryGetValue(englishCounterPath, out var counter)) return null;

            try
            {
                var value = new NativeMethods.PDH_FMT_COUNTERVALUE();
                if (NativeMethods.PdhGetFormattedCounterValue(
                        counter, NativeMethods.PdhFmtDouble, out _, ref value) != 0)
                {
                    return null;
                }

                if (!IsValidStatus(value.CStatus)) return null;
                return Normalize(value.DoubleValue);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 取通配实例计数器的全部实例值（实例名 + 数值）。失败 / 无数据 / 首采 → 空列表。
    ///
    /// <para>流程：先传 null 拿 <c>PDH_MORE_DATA</c> 与所需字节数 → 分配非托管缓冲区 →
    /// 再调用一次 → 按 24 字节步长用 <c>Marshal.PtrToStructure</c> 逐项读，实例名用
    /// <c>Marshal.PtrToStringUni</c>。缓冲区一定在 finally 里 <c>FreeHGlobal</c>（每 2 秒一次，
    /// 漏了就会稳定涨内存）。</para>
    /// </summary>
    public IReadOnlyList<(string Instance, double Value)> GetArray(string englishCounterPath)
    {
        var empty = Array.Empty<(string Instance, double Value)>();

        lock (_sync)
        {
            if (_disposed || _query == IntPtr.Zero) return empty;
            if (!_counters.TryGetValue(englishCounterPath, out var counter)) return empty;

            var buffer = IntPtr.Zero;
            try
            {
                uint bufferSize = 0;
                var status = NativeMethods.PdhGetFormattedCounterArrayW(
                    counter, NativeMethods.PdhFmtDouble, ref bufferSize, out var itemCount, IntPtr.Zero);

                // 首采（比率计数器还没有差值）会返回 PDH_INVALID_DATA / PDH_NO_DATA，
                // 缓冲区大小也没意义 —— 直接当「本轮没有数据」。
                if (status != NativeMethods.PdhMoreData || bufferSize == 0 || itemCount == 0) return empty;
                if (bufferSize > (uint)int.MaxValue) return empty;

                buffer = Marshal.AllocHGlobal((int)bufferSize);
                var size = bufferSize;
                status = NativeMethods.PdhGetFormattedCounterArrayW(
                    counter, NativeMethods.PdhFmtDouble, ref size, out itemCount, buffer);
                if (status != 0 || itemCount == 0) return empty;

                var stride = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>();
                var result = new List<(string Instance, double Value)>();
                for (uint index = 0; index < itemCount; index++)
                {
                    var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>(
                        IntPtr.Add(buffer, checked((int)(index * (uint)stride))));

                    if (!IsValidStatus(item.CStatus)) continue;

                    var value = Normalize(item.DoubleValue);
                    if (value is null) continue;

                    var instance = Marshal.PtrToStringUni(item.Name) ?? "";
                    result.Add((instance, value.Value));
                }

                return result;
            }
            catch
            {
                return empty;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>关闭 query（PDH 会一并释放计数器句柄）；重复调用安全。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _counters.Clear();

            if (_query != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.PdhCloseQuery(_query);
                }
                catch
                {
                    // 关闭失败也不影响进程退出，忽略
                }

                _query = IntPtr.Zero;
            }
        }
    }

    /// <summary>PDH 的 CStatus 只有 VALID_DATA(0) / NEW_DATA(1) 两个值算有效。</summary>
    private static bool IsValidStatus(uint status)
        => status is NativeMethods.PdhCStatusValidData or NativeMethods.PdhCStatusNewData;

    /// <summary>把 NaN / Infinity 归一成 null（WPF 进度条拿到 NaN 会画不出来）。</summary>
    private static double? Normalize(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? null : value;
}
