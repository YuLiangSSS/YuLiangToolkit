using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace YuLiangToolkit.Core.Services;

/// <summary>Windows 的进程完整性级别（Mandatory Integrity Level）。</summary>
public enum IntegrityLevel
{
    /// <summary>读不到（不是 Windows / 令牌被拒）。</summary>
    Unknown = 0,

    /// <summary>比 Low 还低（0x0000，理论上只有沙箱里的受限令牌才这样）。</summary>
    Untrusted = 1,

    /// <summary>低完整性（0x1000）：沙箱、或 exe 被标了 <c>Low Mandatory Level</c>。</summary>
    Low = 2,

    /// <summary>普通用户（0x2000）：正常双击 / <c>dotnet run</c> 就是这一级。</summary>
    Medium = 3,

    /// <summary>管理员 / 已提权（0x3000）。</summary>
    High = 4,

    /// <summary>System（0x4000）。</summary>
    System = 5,
}

/// <summary>
/// 启动时的「完整性自检」：读**本进程**的完整性级别 + 找一下任务栏窗口，
/// 判断全屏时到底能不能把任务栏藏起来。
///
/// <para>为什么要它：v0.9.3 查「全屏藏不住任务栏」时，全屏代码完全正确，问题出在 exe 被别的沙箱工具
/// 标成了 <c>Low Mandatory Level</c> —— 低完整性进程动不了任务栏（UIPI 拒绝，Win32 错误码 <c>5</c>），
/// 而且这种情况**没有任何界面提示**，只能靠 <c>YLT_TASKBAR_TRACE</c> 猜。现在程序一启动就把结论打到
/// 控制台（同时写 <see cref="Debug"/> 输出；设了 <c>YLT_TASKBAR_TRACE</c> 也写一份），
/// 一眼就能分出是“权限不够”还是“代码不对”。</para>
///
/// <para>⚠ 这里**只读**：读自己的令牌 + <c>FindWindow</c>，绝不隐藏 / 显示任何窗口，
/// 所以自检程序可以放心调用（跑测试藏开发者任务栏太吓人）。</para>
///
/// <para>⚠ 完整性标签是标在**文件**上的（不是目录）：只 <c>icacls &lt;目录&gt; /setintegritylevel Medium</c>
/// 不够，要加 <c>/T</c> 递归；而且用**低权限沙箱重新编译**会把输出目录里的 exe/dll 再次标回 Low。</para>
/// </summary>
public static class IntegrityCheck
{
    /// <summary>TOKEN_QUERY：只要求读令牌，够用且不需要提权。</summary>
    private const uint TokenQuery = 0x0008;

    /// <summary>TokenIntegrityLevel = 25（winnt.h 的 TOKEN_INFORMATION_CLASS）。</summary>
    private const int TokenIntegrityLevel = 25;

    /// <summary>0x1000 = Low，0x2000 = Medium，0x3000 = High，0x4000 = System（子权限 RID）。</summary>
    private const int LowRid = 0x1000;
    private const int MediumRid = 0x2000;
    private const int HighRid = 0x3000;
    private const int SystemRid = 0x4000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static readonly Lazy<(IntegrityLevel Level, int Rid)> Current =
        new(ReadCurrentProcessIntegrity, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>本进程的完整性级别（进程级缓存：一个进程不会中途变级别）。</summary>
    public static IntegrityLevel CurrentProcess => Current.Value.Level;

    /// <summary>本进程完整性级别的子权限 RID（例如 <c>0x2000</c>）；读不到为 0。</summary>
    public static int CurrentProcessRid => Current.Value.Rid;

    /// <summary>主屏任务栏窗口（<c>Shell_TrayWnd</c>）的句柄；没有就是 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr TaskbarHandle
    {
        get
        {
            try { return FindWindow("Shell_TrayWnd", null); }
            catch { return IntPtr.Zero; }
        }
    }

    /// <summary>找得到任务栏窗口（第三方任务栏 / explorer 没起来时是 false）。</summary>
    public static bool TaskbarFound => TaskbarHandle != IntPtr.Zero;

    /// <summary>任务栏现在可见吗（只读查询）。</summary>
    public static bool TaskbarVisible
    {
        get
        {
            var hwnd = TaskbarHandle;
            return hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
        }
    }

    /// <summary>
    /// 本进程的级别**够不够**去操作任务栏（Medium 及以上才够；Low / Untrusted 会被 UIPI 拒）。
    /// 这是纯级别判断，不看任务栏存不存在。
    /// </summary>
    public static bool IntegrityAllowsTaskbarControl => CurrentProcess >= IntegrityLevel.Medium;

    /// <summary>级别够 + 任务栏窗口找得到 → 全屏时才有机会把任务栏藏起来。</summary>
    public static bool CanHideTaskbar => IntegrityAllowsTaskbarControl && TaskbarFound;

    /// <summary>完整性 RID → 级别。纯函数（自检直接断言；0x2100 这种 Medium Plus 也算 Medium）。</summary>
    public static IntegrityLevel Classify(int rid)
    {
        if (rid < LowRid) return IntegrityLevel.Untrusted;     // 0x0000 = Untrusted
        if (rid < MediumRid) return IntegrityLevel.Low;
        if (rid < HighRid) return IntegrityLevel.Medium;
        if (rid < SystemRid) return IntegrityLevel.High;
        return IntegrityLevel.System;
    }

    /// <summary>级别的英文名（命令行里看最直观）。</summary>
    public static string LevelName(IntegrityLevel level) => level switch
    {
        IntegrityLevel.Untrusted => "Untrusted",
        IntegrityLevel.Low => "Low",
        IntegrityLevel.Medium => "Medium",
        IntegrityLevel.High => "High",
        IntegrityLevel.System => "System",
        _ => "Unknown",
    };

    /// <summary>级别 → 一句人话（尤其 Low 那句要点明“错误码 5、改代码没用”）。</summary>
    public static string Describe(IntegrityLevel level) => level switch
    {
        IntegrityLevel.Untrusted => "比 Low 还低（受限令牌），不能操作任务栏",
        IntegrityLevel.Low => "低完整性（沙箱 / 文件被标成 Low）：不能操作任务栏，ShowWindow 会被 UIPI 拒绝（Win32 错误码 5）",
        IntegrityLevel.Medium => "普通用户权限（正常，可以操作任务栏）",
        IntegrityLevel.High => "管理员权限（可以操作任务栏）",
        IntegrityLevel.System => "System 权限（可以操作任务栏）",
        _ => "未知（读不到进程令牌）",
    };

    /// <summary>一行摘要（控制台 / 自检里直接打）。</summary>
    public static string Summary()
    {
        var hwnd = TaskbarHandle;
        var taskbar = hwnd == IntPtr.Zero
            ? "没找到任务栏窗口（Shell_TrayWnd）"
            : $"任务栏窗口 0x{hwnd.ToInt64():X}（可见={IsWindowVisible(hwnd)}）";

        return $"进程完整性={LevelName(CurrentProcess)}（0x{CurrentProcessRid:X4}）· {taskbar} · 全屏可藏任务栏={YesNo(CanHideTaskbar)}";
    }

    /// <summary>
    /// 多行报告：级别 / 任务栏 / 结论，级别不够时直接给出修复命令。
    /// 最后一行固定以「结论」开头（自检据此断言）。
    /// </summary>
    public static IReadOnlyList<string> ReportLines()
    {
        var hwnd = TaskbarHandle;
        var found = hwnd != IntPtr.Zero;
        var lines = new List<string>
        {
            $"[完整性自检] 进程完整性级别：{LevelName(CurrentProcess)}（0x{CurrentProcessRid:X4}）—— {Describe(CurrentProcess)}",
            found
                ? $"[完整性自检] 任务栏窗口：Shell_TrayWnd = 0x{hwnd.ToInt64():X}，当前可见 = {IsWindowVisible(hwnd)}"
                : "[完整性自检] 任务栏窗口：没找到 Shell_TrayWnd（第三方任务栏 / explorer 还没起来）",
        };

        if (!IntegrityAllowsTaskbarControl)
        {
            lines.Add("[完整性自检] 结论：全屏时藏不住任务栏 —— 低完整性进程被 UIPI 拦住（ShowWindow 返回错误码 5），改全屏代码没用。");
            lines.Add($"[完整性自检] 处理：icacls \"{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}\" " +
                      "/setintegritylevel \"(OI)(CI)Medium\" /T，然后重新启动程序。");
        }
        else if (!found)
        {
            lines.Add("[完整性自检] 结论：权限够，但没找到任务栏窗口（可能装了第三方任务栏），全屏藏任务栏会失败。");
        }
        else
        {
            lines.Add("[完整性自检] 结论：全屏时可以隐藏任务栏。");
        }

        return lines;
    }

    /// <summary>
    /// 把报告写到控制台 + <see cref="Debug"/> 输出；设了 <c>YLT_TASKBAR_TRACE</c> 再追加到那个文件。
    /// 从终端 / <c>dotnet run</c> 启动时控制台能看到；双击启动没有控制台，这几行自然看不到（也不会报错）。
    /// </summary>
    public static void WriteReport()
    {
        EnsureConsoleEncoding();

        var lines = ReportLines();
        foreach (var line in lines)
        {
            try { Console.WriteLine(line); } catch { /* 没有控制台就算了 */ }
            Debug.WriteLine(line);
        }

        var trace = Environment.GetEnvironmentVariable("YLT_TASKBAR_TRACE");
        if (string.IsNullOrWhiteSpace(trace)) return;
        try
        {
            File.AppendAllText(trace, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch { /* 排查日志写不进去就算了 */ }
    }

    private static bool _consoleEncodingReady;

    /// <summary>
    /// IDE（Rider / Visual Studio）和管道都按 **UTF-8** 读 stdout，而 .NET 默认按**控制台代码页**
    /// （中文系统是 936/GBK）写 —— 于是从 IDE 里跑，这几行中文全是乱码（v0.9.5 用户实测）。
    /// 这里只在 stdout 被重定向（IDE / 管道 / 重定向到文件）时切成 UTF-8；**真正的控制台保持原样**，
    /// 免得在 cmd / Windows Terminal 里反过来变乱码。
    /// </summary>
    private static void EnsureConsoleEncoding()
    {
        if (_consoleEncodingReady) return;
        _consoleEncodingReady = true;

        try
        {
            if (Console.IsOutputRedirected) Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 没有控制台 / 编码不支持：静默跳过，不能因为打一行日志把程序启动搞崩
        }
    }

    private static string YesNo(bool value) => value ? "是" : "否";

    /// <summary>读本进程令牌里的完整性级别；任何一步失败都返回 Unknown（不抛异常）。</summary>
    private static (IntegrityLevel Level, int Rid) ReadCurrentProcessIntegrity()
    {
        if (!OperatingSystem.IsWindows()) return (IntegrityLevel.Unknown, 0);

        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            return (IntegrityLevel.Unknown, 0);
        }

        try
        {
            // 先问需要多大，再拿一块缓冲区接 TOKEN_MANDATORY_LABEL（里面只有 SID_AND_ATTRIBUTES.Label）
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size <= 0) return (IntegrityLevel.Unknown, 0);

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                {
                    return (IntegrityLevel.Unknown, 0);
                }

                var sid = Marshal.ReadIntPtr(buffer);   // TOKEN_MANDATORY_LABEL.Label.Sid（结构体第一个字段）
                if (sid == IntPtr.Zero) return (IntegrityLevel.Unknown, 0);

                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                if (count == 0) return (IntegrityLevel.Unknown, 0);

                // 完整性级别固定放在 SID 的最后一个子权限里：S-1-16-<RID>
                var rid = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                return (Classify(rid), rid);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
