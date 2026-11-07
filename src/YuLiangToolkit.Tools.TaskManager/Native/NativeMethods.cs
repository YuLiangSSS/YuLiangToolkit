using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 「任务管理器」用到的全部 Windows P/Invoke（pdh / iphlpapi / kernel32 / advapi32 / ntdll）。
///
/// <para>这里只放<b>声明和 Win32 结构体</b>，不写业务逻辑；调用方是
/// <see cref="PdhQuery"/>（性能计数器）、<see cref="PortMapper"/>（端口表）、
/// <see cref="ProcessSampler"/>（进程路径 / 令牌用户名）、<see cref="SystemSampler"/>
/// （CPU 总时间 / 内存 / 系统版本）。每个调用点都包了 try/catch：读不到就降级，
/// 绝不把异常抛给界面。</para>
///
/// <para><b>结构体按 x64 布局书写</b>（本项目只跑 64 位 Windows）：</para>
/// <list type="bullet">
/// <item><c>PDH_FMT_COUNTERVALUE</c> 是 <c>{ DWORD CStatus; union { double … }; }</c>，
/// <c>double</c> 在偏移 8、总长 16 字节（用 <see cref="LayoutKind.Explicit"/> 手写偏移，
/// 不依赖运行时对 double 的自然对齐）。</item>
/// <item><c>PDH_FMT_COUNTERVALUE_ITEM_W</c> = 8 字节实例名指针 + 16 字节 <c>FmtValue</c>，
/// 每项 24 字节；<c>PdhGetFormattedCounterArrayW</c> 返回的是「定长结构体数组」，
/// 用 <c>Marshal.SizeOf</c> 算步长逐项读，绝对不能把实例名字符串也当步长的一部分。</item>
/// <item><c>MIB_TCP6ROW_OWNER_PID</c> 的字段顺序与 IPv4 行<b>不同</b>（本地地址在最前、
/// 状态在倒数第二），这里按 <c>tcpmib.h</c> 的真实定义写，别按 IPv4 的顺序抄。</item>
/// </list>
///
/// <para>部分机器不可用的情况：没有独立显卡 / 老驱动的机器上 GPU 计数器<b>加不上</b>
/// （<c>PdhAddEnglishCounterW</c> 返回 PDH_CSTATUS_NO_OBJECT 之类），虚拟机可能没有
/// <c>\PhysicalDisk(_Total)</c> 计数器；这些都由上层转成「不可用」而不是报错。</para>
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------ 常量

    /// <summary><c>PROCESS_QUERY_LIMITED_INFORMATION</c>：只读路径 / 令牌的最小权限，
    /// 比 <c>PROCESS_QUERY_INFORMATION</c> 更宽松，普通进程也能读。</summary>
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary><c>TOKEN_QUERY</c>：读进程令牌的用户信息。</summary>
    internal const uint TokenQuery = 0x0008;

    /// <summary><c>TokenUser = 1</c>：<c>GetTokenInformation</c> 的 <c>TOKEN_INFORMATION_CLASS</c>。</summary>
    internal const int TokenUser = 1;

    /// <summary><c>ERROR_INSUFFICIENT_BUFFER = 122</c>：<c>GetExtendedTcpTable</c> 第一次拿尺寸时的返回值。</summary>
    internal const int ErrorInsufficientBuffer = 122;

    /// <summary><c>AF_INET = 2</c>。</summary>
    internal const int AfInet = 2;

    /// <summary><c>AF_INET6 = 23</c>（注意不是 Winsock 里常见的 23 之外的数，就是 23）。</summary>
    internal const int AfInet6 = 23;

    /// <summary><c>TCP_TABLE_OWNER_PID_ALL = 5</c>：带 PID 的全部 TCP 连接 / 监听。</summary>
    internal const int TcpTableOwnerPidAll = 5;

    /// <summary><c>UDP_TABLE_OWNER_PID = 1</c>：带 PID 的 UDP 端点。</summary>
    internal const int UdpTableOwnerPid = 1;

    /// <summary><c>PDH_FMT_DOUBLE = 0x200</c>：让 PDH 直接返回 double。</summary>
    internal const uint PdhFmtDouble = 0x00000200;

    /// <summary><c>PDH_MORE_DATA = 0x800007D2</c>：缓冲区不够（先查尺寸时是正常现象）。</summary>
    internal const uint PdhMoreData = 0x800007D2;

    /// <summary><c>PDH_CSTATUS_VALID_DATA = 0</c>：计数器值有效。</summary>
    internal const uint PdhCStatusValidData = 0;

    /// <summary><c>PDH_CSTATUS_NEW_DATA = 1</c>：计数器值有效（新数据）。</summary>
    internal const uint PdhCStatusNewData = 1;

    /// <summary><c>PDH_INVALID_DATA = 0xC0000BC6</c>：比率型计数器第一次采样还没有差值时常见，
    /// 这时应当返回「无数据」而不是抛异常。</summary>
    internal const uint PdhInvalidData = 0xC0000BC6;

    /// <summary><c>PDH_NO_DATA = 0x800007D5</c>：查询里还没有可用的实例数据（没有显卡 / 没有磁盘计数器）。</summary>
    internal const uint PdhNoData = 0x800007D5;

    // ------------------------------------------------------------------ kernel32：进程 / 系统

    /// <summary>打开进程，拿只读句柄（读路径 / 令牌用）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    /// <summary>关闭句柄（进程 / 令牌等），必须配对，否则每 2 秒采样会累积句柄。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>取进程映像的完整路径；<paramref name="size"/> 传入缓冲区字符数，返回实际长度。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr processHandle, int flags, StringBuilder exeName, ref int size);

    /// <summary>释放 Windows 分配的字符串（<c>ConvertSidToStringSidW</c> 的结果必须用
    /// <c>LocalFree</c> 释放，不能用 <c>Marshal.FreeHGlobal</c>）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>取系统累计时间（Idle / Kernel / User），用于算整机 CPU 占用。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

    /// <summary>取物理内存总量 / 可用量。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX memoryStatus);

    /// <summary>开机以来的毫秒数（64 位，不会像 <c>GetTickCount</c> 那样 49 天回绕）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ulong GetTickCount64();

    // ------------------------------------------------------------------ advapi32：进程令牌

    /// <summary>打开进程令牌（读用户名）。</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    /// <summary>取令牌信息；第一次传 null 查所需长度。</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetTokenInformation(
        IntPtr tokenHandle, int informationClass, IntPtr information, int informationLength, out int returnLength);

    /// <summary>SID → 字符串（例如 <c>S-1-5-21-…</c>）；返回值要用 <c>LocalFree</c> 释放。</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    // ------------------------------------------------------------------ ntdll：真实系统版本

    /// <summary>
    /// <c>RtlGetVersion</c> 是拿「真实 Windows 版本号」最可靠的办法：
    /// <c>Environment.OSVersion</c> 在应用没有 Win10 兼容清单时会谎报成 6.2（Win8）。
    /// 这里返回 NTSTATUS（0 = 成功），失败时上层退回 <c>Environment.OSVersion</c>。
    /// </summary>
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW versionInfo);

    // ------------------------------------------------------------------ pdh：性能计数器

    /// <summary>打开一个 PDH 查询（一个查询可以挂多个计数器）。</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    /// <summary>
    /// 用「英文计数器路径」加计数器（中文系统也认英文路径，这样不用为每种语言维护映射表）。
    /// 返回 0 表示成功；没显卡 / 没磁盘计数器的机器上会返回 PDH 错误码。
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    /// <summary>采样一次：比率型计数器需要调用两次才有值，第一次可能返回无效数据。</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhCollectQueryData(IntPtr query);

    /// <summary>取单个计数器（非通配实例）的格式化值。</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhGetFormattedCounterValue(
        IntPtr counter, uint format, out uint valueType, ref PDH_FMT_COUNTERVALUE value);

    /// <summary>取通配实例计数器（例如 <c>GPU Engine(*)</c>）的全部实例值；先传 null 拿所需缓冲区大小。</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    /// <summary>关闭查询（会一并关掉挂在它上面的计数器句柄）。</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhCloseQuery(IntPtr query);

    // ------------------------------------------------------------------ iphlpapi：端口表

    /// <summary>
    /// 取 TCP 连接表（带所属 PID）。第一次传 <c>IntPtr.Zero</c> / size=0 会返回
    /// <see cref="ErrorInsufficientBuffer"/> 并填好所需字节数。
    /// </summary>
    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    /// <summary>取 UDP 端点表（带所属 PID），用法同 <see cref="GetExtendedTcpTable"/>。</summary>
    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetExtendedUdpTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    // ------------------------------------------------------------------ 结构体

    /// <summary><c>FILETIME</c>：100 纳秒为单位的时间值（这里只用低位 / 高位两个 DWORD 组合）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        /// <summary>组合成 64 位整数（100ns 单位）。</summary>
        internal readonly ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    /// <summary><c>MEMORYSTATUSEX</c>：物理内存 / 页面文件 / 虚拟内存状态（<c>dwLength</c> 必须先填结构体大小）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>
    /// <c>RTL_OSVERSIONINFOEXW</c>：<c>RtlGetVersion</c> 的输出。
    /// 用 <c>ByValTStr</c> 内联 128 个 WCHAR 的 <c>szCSDVersion</c>，不要改成指针。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RTL_OSVERSIONINFOEXW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;

        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    /// <summary>
    /// <c>PDH_FMT_COUNTERVALUE</c>：<c>DWORD CStatus</c> + 8 字节 union。
    /// x64 上 union 对齐到偏移 8，总长 16；用显式偏移保证不随运行时对齐规则漂移。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    /// <summary>
    /// <c>PDH_FMT_COUNTERVALUE_ITEM_W</c>：实例名指针 + <c>FmtValue</c>，x64 上每项 24 字节。
    /// 这里把 <c>FmtValue</c> 的两个关键字段直接铺平到 8 / 16 偏移，省掉嵌套结构体的对齐歧义。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        /// <summary>实例名（例如 <c>pid_1234_luid_0x…_engtype_3D</c>）的宽字符指针，字符串紧跟在结构体数组之后。</summary>
        [FieldOffset(0)] public IntPtr Name;

        [FieldOffset(8)] public uint CStatus;

        [FieldOffset(16)] public double DoubleValue;
    }

    /// <summary><c>MIB_TCPROW_OWNER_PID</c>：一条 IPv4 TCP 连接（24 字节）。端口 / 地址是网络字节序。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    /// <summary>
    /// <c>MIB_TCP6ROW_OWNER_PID</c>：一条 IPv6 TCP 连接（56 字节）。
    /// 字段顺序与 IPv4 行不同：本地地址 / scope / 端口 → 远端地址 / scope / 端口 → 状态 → PID。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;

        public uint dwLocalScopeId;
        public uint dwLocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucRemoteAddr;

        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    /// <summary><c>MIB_UDPROW_OWNER_PID</c>：一个 IPv4 UDP 端点（12 字节，没有状态）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    /// <summary><c>MIB_UDP6ROW_OWNER_PID</c>：一个 IPv6 UDP 端点（28 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;

        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }
}
