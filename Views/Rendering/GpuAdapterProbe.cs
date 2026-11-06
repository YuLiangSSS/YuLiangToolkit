using System.Globalization;
using System.Runtime.InteropServices;

namespace YuLiangToolkit.App.Rendering;

/// <summary>一块图形适配器（显卡）的只读信息，来自 DXGI 枚举（v0.96.0「渲染与性能」用）。</summary>
public sealed record GpuAdapterInfo(
    string Name,
    ulong DedicatedVideoMemory,
    ulong SharedSystemMemory,
    uint VendorId,
    bool IsSoftware,
    bool IsRemote)
{
    /// <summary>是不是真正能画图的硬件显卡（排除 Microsoft Basic Render Driver 这类软件适配器 / 远程桌面适配器）。</summary>
    public bool IsHardware => !IsSoftware && !IsRemote;
}

/// <summary>
/// 枚举机器上的显卡（DXGI 1.1，系统自带的 <c>dxgi.dll</c>，不引第三方包）。
///
/// <para><b>为什么不走 WMI</b>：WMI 的 <c>Win32_VideoController</c> 需要 System.Management 包，
/// 而且拿不到「独占显存」这类判断信息；DXGI 是 Windows 图形栈自己用的接口，读到的就是真实适配器列表。</para>
///
/// <para><b>只读</b>：不创建设备、不写注册表，失败（远程桌面 / 老系统 / 无显卡驱动）时返回空列表，
/// 界面上只少一行硬件信息，不影响设置本身。WPF 自己的渲染轮不到这里决定，这里只用来「告诉用户机器上有哪些卡」。</para>
/// </summary>
public static class GpuAdapterProbe
{
    private static IReadOnlyList<GpuAdapterInfo>? _cached;

    /// <summary>枚举显卡（结果缓存；传 <paramref name="refresh"/> = true 强制重新读）。</summary>
    public static IReadOnlyList<GpuAdapterInfo> Enumerate(bool refresh = false)
    {
        if (!refresh && _cached is not null) return _cached;

        try
        {
            _cached = EnumerateCore();
        }
        catch
        {
            _cached = Array.Empty<GpuAdapterInfo>();
        }

        return _cached;
    }

    /// <summary>把枚举结果拼成设置页那句「检测到 N 块显卡：…」。</summary>
    public static string DescribeAdapters(IReadOnlyList<GpuAdapterInfo>? adapters)
    {
        if (adapters is null || adapters.Count == 0)
            return "没检测到显卡信息（远程桌面 / 虚拟机 / 老驱动下可能读不到；不影响下面的设置）。";

        // 有的环境（远程桌面 / 多显示器驱动）会把同一块卡枚举出好几份，按名字去重，免得显示成「5 块显卡」。
        var unique = new List<GpuAdapterInfo>();
        foreach (var adapter in adapters)
        {
            if (unique.Any(existing => string.Equals(existing.Name, adapter.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            unique.Add(adapter);
        }

        // 有真显卡就只列真显卡；只剩软件适配器（Microsoft Basic Render Driver 等）时才列出来提醒用户。
        var hardware = unique.Where(adapter => adapter.IsHardware).ToList();
        var display = hardware.Count > 0 ? hardware : unique;

        var prefix = hardware.Count switch
        {
            >= 2 => $"检测到 {hardware.Count} 块显卡：",
            1 => "检测到 1 块显卡：",
            _ => "没检测到硬件显卡，只有：",
        };
        return prefix + string.Join("；", display.Select(DescribeAdapter)) + "。";
    }

    /// <summary>单块显卡的一句话描述。</summary>
    public static string DescribeAdapter(GpuAdapterInfo adapter)
    {
        var kind = adapter.IsSoftware
            ? "，软件适配器"
            : adapter.IsRemote
                ? "，远程桌面"
                : string.Empty;
        return $"{adapter.Name}（{FormatMemory(adapter.DedicatedVideoMemory)}{kind}）";
    }

    /// <summary>显存大小的可读文本（8 GB → 「8.0 GB 显存」；0 → 「共享内存」）。</summary>
    public static string FormatMemory(ulong bytes)
    {
        if (bytes == 0) return "共享内存";
        const double gigabyte = 1024d * 1024 * 1024;
        const double megabyte = 1024d * 1024;
        return bytes >= gigabyte
            ? (bytes / gigabyte).ToString("F1", CultureInfo.InvariantCulture) + " GB 显存"
            : (bytes / megabyte).ToString("F0", CultureInfo.InvariantCulture) + " MB 显存";
    }

    // ---------------------------------------------------------------- DXGI 枚举

    private static IReadOnlyList<GpuAdapterInfo> EnumerateCore()
    {
        var iid = IidIDXGIFactory1;
        var hr = CreateDXGIFactory1(ref iid, out var factoryPointer);
        if (hr < 0 || factoryPointer == IntPtr.Zero) return Array.Empty<GpuAdapterInfo>();

        var result = new List<GpuAdapterInfo>();
        try
        {
            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPointer);
            try
            {
                for (uint index = 0; ; index++)
                {
                    // 枚举到头会返回 DXGI_ERROR_NOT_FOUND（HRESULT 负数）
                    if (factory.EnumAdapters1(index, out var adapterPointer) < 0
                        || adapterPointer == IntPtr.Zero)
                    {
                        break;
                    }

                    try
                    {
                        var adapter = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(adapterPointer);
                        try
                        {
                            if (adapter.GetDesc1(out var desc) < 0) continue;
                            var name = (desc.Description ?? string.Empty).Trim();
                            if (name.Length == 0) continue;
                            result.Add(new GpuAdapterInfo(
                                name,
                                (ulong)desc.DedicatedVideoMemory,
                                (ulong)desc.SharedSystemMemory,
                                desc.VendorId,
                                IsSoftware: (desc.Flags & 2) != 0,
                                IsRemote: (desc.Flags & 1) != 0));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(adapter);
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapterPointer);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        finally
        {
            Marshal.Release(factoryPointer);
        }

        return result;
    }

    private static readonly Guid IidIDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    // ---- COM 接口（只声明用得到的方法；vtable 顺序必须和 dxgi.h 一致） ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);

        // IDXGIFactory
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);

        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, out IntPtr adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);

        // IDXGIAdapter
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid name, out long umdVersion);

        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
