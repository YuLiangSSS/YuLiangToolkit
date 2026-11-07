using System.Net;
using System.Runtime.InteropServices;
using YuLiangToolkit.Tools.TaskManager.Models;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 端口 → PID 的映射：直接调 IP Helper 的 <c>GetExtendedTcpTable</c> / <c>GetExtendedUdpTable</c>
/// （这是任务管理器 / <c>netstat -ano</c> 背后用的同一张表）。
///
/// <para>TCP 有状态（LISTENING / ESTABLISHED …），UDP 没有连接状态、远端一律为空；
/// IPv4 与 IPv6 各查一次，查询失败（老系统不支持 IPv6、组策略限制等）只记一条原因，
/// 其它表照常返回；<b>四张表全失败</b>才抛 <see cref="InvalidOperationException"/>，
/// 由 <see cref="ProcessSampler"/> 转成界面上的中文提示，不影响进程列表本身。</para>
///
/// <para><b>字节序</b>：表里端口字段是网络字节序（高字节在前），必须转回 16 位端口；
/// IPv4 地址是「网络序 u32」，按内存字节顺序重新构造 <see cref="IPAddress"/> 才是
/// <c>127.0.0.1</c>（直接 <c>new IPAddress((long)value)</c> 会得到反过来的地址）；
/// IPv6 地址是 16 字节数组，直接构造即可。</para>
///
/// <para>部分机器不可用：UDPv6 表在禁用 IPv6 的系统上返回 <c>ERROR_NOT_SUPPORTED</c>，
/// 属于正常降级；权限不足（普通用户读不到别的用户的端口）会以 Win32 错误码体现。</para>
/// </summary>
public static class PortMapper
{
    /// <summary>
    /// 扫描本机全部 TCP / UDP 端点并生成 <see cref="PortBinding"/> 列表（未按 PID 分组）。
    /// 个别表失败会跳过；四张表都失败时抛异常（中文原因），由调用方决定如何提示。
    /// </summary>
    public static IReadOnlyList<PortBinding> Scan()
    {
        var bindings = new List<PortBinding>(512);
        var failures = new List<string>();
        var succeededTables = 0;

        ScanTcpTable(NativeMethods.AfInet, "TCP", ipv6: false, bindings, failures, ref succeededTables);
        ScanTcpTable(NativeMethods.AfInet6, "TCPv6", ipv6: true, bindings, failures, ref succeededTables);
        ScanUdpTable(NativeMethods.AfInet, "UDP", ipv6: false, bindings, failures, ref succeededTables);
        ScanUdpTable(NativeMethods.AfInet6, "UDPv6", ipv6: true, bindings, failures, ref succeededTables);

        if (succeededTables == 0 && failures.Count > 0)
        {
            throw new InvalidOperationException("读取端口占用失败：" + string.Join("；", failures));
        }

        return bindings;
    }

    // ------------------------------------------------------------------ TCP

    private static void ScanTcpTable(
        int addressFamily,
        string protocol,
        bool ipv6,
        List<PortBinding> bindings,
        List<string> failures,
        ref int succeededTables)
    {
        var buffer = IntPtr.Zero;
        try
        {
            var size = 0;
            var status = NativeMethods.GetExtendedTcpTable(
                IntPtr.Zero, ref size, true, addressFamily, NativeMethods.TcpTableOwnerPidAll, 0);
            if (status != NativeMethods.ErrorInsufficientBuffer || size <= 0)
            {
                failures.Add($"{protocol} 表不可用（错误码 {status}）");
                return;
            }

            buffer = Marshal.AllocHGlobal(size);
            var actualSize = size;
            status = NativeMethods.GetExtendedTcpTable(
                buffer, ref actualSize, true, addressFamily, NativeMethods.TcpTableOwnerPidAll, 0);
            if (status != 0)
            {
                failures.Add($"{protocol} 表读取失败（错误码 {status}）");
                return;
            }

            // 缓冲区开头 4 字节是行数，后面紧跟定长行结构体（行之间没有额外填充）。
            var rowCount = Marshal.ReadInt32(buffer);
            if (rowCount <= 0)
            {
                succeededTables++;
                return;
            }

            if (ipv6)
            {
                var stride = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
                for (var index = 0; index < rowCount; index++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(
                        IntPtr.Add(buffer, 4 + index * stride));

                    var remotePort = NetworkPortToHost(row.dwRemotePort);
                    bindings.Add(new PortBinding
                    {
                        Protocol = protocol,
                        LocalAddress = FormatIPv6(row.ucLocalAddr),
                        LocalPort = NetworkPortToHost(row.dwLocalPort),
                        RemoteAddress = remotePort == 0 ? "" : FormatIPv6(row.ucRemoteAddr),
                        RemotePort = remotePort,
                        State = DisplayFormat.TcpState(row.dwState),
                        Pid = (int)row.dwOwningPid,
                    });
                }
            }
            else
            {
                var stride = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                for (var index = 0; index < rowCount; index++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(
                        IntPtr.Add(buffer, 4 + index * stride));

                    var remotePort = NetworkPortToHost(row.dwRemotePort);
                    bindings.Add(new PortBinding
                    {
                        Protocol = protocol,
                        LocalAddress = FormatIPv4(row.dwLocalAddr),
                        LocalPort = NetworkPortToHost(row.dwLocalPort),
                        RemoteAddress = remotePort == 0 ? "" : FormatIPv4(row.dwRemoteAddr),
                        RemotePort = remotePort,
                        State = DisplayFormat.TcpState(row.dwState),
                        Pid = (int)row.dwOwningPid,
                    });
                }
            }

            succeededTables++;
        }
        catch (Exception exception)
        {
            failures.Add($"{protocol} 表读取异常：{exception.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    // ------------------------------------------------------------------ UDP

    private static void ScanUdpTable(
        int addressFamily,
        string protocol,
        bool ipv6,
        List<PortBinding> bindings,
        List<string> failures,
        ref int succeededTables)
    {
        var buffer = IntPtr.Zero;
        try
        {
            var size = 0;
            var status = NativeMethods.GetExtendedUdpTable(
                IntPtr.Zero, ref size, true, addressFamily, NativeMethods.UdpTableOwnerPid, 0);
            if (status != NativeMethods.ErrorInsufficientBuffer || size <= 0)
            {
                failures.Add($"{protocol} 表不可用（错误码 {status}）");
                return;
            }

            buffer = Marshal.AllocHGlobal(size);
            var actualSize = size;
            status = NativeMethods.GetExtendedUdpTable(
                buffer, ref actualSize, true, addressFamily, NativeMethods.UdpTableOwnerPid, 0);
            if (status != 0)
            {
                failures.Add($"{protocol} 表读取失败（错误码 {status}）");
                return;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            if (rowCount <= 0)
            {
                succeededTables++;
                return;
            }

            if (ipv6)
            {
                var stride = Marshal.SizeOf<NativeMethods.MIB_UDP6ROW_OWNER_PID>();
                for (var index = 0; index < rowCount; index++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_UDP6ROW_OWNER_PID>(
                        IntPtr.Add(buffer, 4 + index * stride));

                    bindings.Add(new PortBinding
                    {
                        Protocol = protocol,
                        LocalAddress = FormatIPv6(row.ucLocalAddr),
                        LocalPort = NetworkPortToHost(row.dwLocalPort),
                        RemoteAddress = "",
                        RemotePort = 0,
                        State = "",
                        Pid = (int)row.dwOwningPid,
                    });
                }
            }
            else
            {
                var stride = Marshal.SizeOf<NativeMethods.MIB_UDPROW_OWNER_PID>();
                for (var index = 0; index < rowCount; index++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_UDPROW_OWNER_PID>(
                        IntPtr.Add(buffer, 4 + index * stride));

                    bindings.Add(new PortBinding
                    {
                        Protocol = protocol,
                        LocalAddress = FormatIPv4(row.dwLocalAddr),
                        LocalPort = NetworkPortToHost(row.dwLocalPort),
                        RemoteAddress = "",
                        RemotePort = 0,
                        State = "",
                        Pid = (int)row.dwOwningPid,
                    });
                }
            }

            succeededTables++;
        }
        catch (Exception exception)
        {
            failures.Add($"{protocol} 表读取异常：{exception.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    // ------------------------------------------------------------------ 字节序 / 地址转换

    /// <summary>
    /// 表里的端口是「网络字节序的 16 位值塞在 DWORD 里」：例如 443 在内存里是
    /// <c>01 BB</c>，按小端读成 DWORD 就是 <c>0x0000BB01</c>，所以要交换低 16 位的两个字节。
    /// </summary>
    private static int NetworkPortToHost(uint rawPort)
    {
        var port = ((rawPort & 0x000000FFu) << 8) | ((rawPort >> 8) & 0x000000FFu);
        return (int)(port & 0xFFFFu);
    }

    /// <summary>
    /// 网络序 u32 → 点分 IPv4。按内存字节重新构造 <see cref="IPAddress"/>：
    /// <c>BitConverter.GetBytes</c> 取到的就是那块内存的原始顺序，正好是网络序的 4 个字节。
    /// </summary>
    private static string FormatIPv4(uint address)
    {
        try
        {
            return new IPAddress(BitConverter.GetBytes(address)).ToString();
        }
        catch
        {
            return "0.0.0.0";
        }
    }

    /// <summary>16 字节网络序数组 → IPv6 文本；数组异常时退回 <c>::</c>。</summary>
    private static string FormatIPv6(byte[]? address)
    {
        try
        {
            return address is { Length: 16 } ? new IPAddress(address).ToString() : "::";
        }
        catch
        {
            return "::";
        }
    }
}
