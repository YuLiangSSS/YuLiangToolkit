using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using YuLiangToolkit.Tools.Launcher.Models;

namespace YuLiangToolkit.Tools.Launcher.Services;

/// <summary>
/// 启动器的 Windows 真实探测实现：文件 / 文件夹存在判断 + 用 Shell 的 <c>IShellLinkW</c> 解析 <c>.lnk</c>。
///
/// <para><b>为什么要有 STA 线程</b>：<c>IShellLink</c> 是 COM 组件，必须在 STA 线程上创建 / 调用；
/// 「软件启动器」的扫描是在后台 <see cref="Task"/> 里跑的（不能卡界面），线程池线程是 MTA，
/// 直接在那里 new COM 对象会失败或行为不确定。所以这里统一把 Shell 调用交给
/// <see cref="StaRunner"/> 的专用 STA 线程执行，调用方同步等结果（调用方通常是后台线程，
/// 不会卡界面；即使从 UI 线程调用，单次解析也很快）。</para>
///
/// <para><b>不抛异常</b>：<see cref="ResolveShortcut"/> 任何失败（文件不在、快捷方式损坏、权限不足、
/// COM 不可用）都返回 <see cref="ShortcutTarget.Unresolved"/>，由 <see cref="LauncherRules"/> 判定健康状态。</para>
///
/// <para><b>缓存</b>：同一个 <c>.lnk</c> 在一次扫描里可能被问好几次（健康检查 + 详情展示 + 图标），
/// 解析结果按规范化路径缓存；用户点「刷新状态」时由 ViewModel 调 <see cref="ClearCache"/> 清掉。</para>
/// </summary>
public sealed class SystemLauncherProbe : ILauncherProbe
{
    private readonly ConcurrentDictionary<string, ShortcutTarget> _shortcutCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>文件或文件夹是否存在。路径非法 / 空 → false，不抛异常。</summary>
    public bool Exists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var normalized = LauncherRules.NormalizePath(path);
            if (normalized.Length == 0) return false;
            return File.Exists(normalized) || Directory.Exists(normalized);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析 <c>.lnk</c> 的目标 / 参数 / 起始位置 / 图标位置；失败返回 <see cref="ShortcutTarget.Unresolved"/>。</summary>
    public ShortcutTarget ResolveShortcut(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath)) return ShortcutTarget.Unresolved;

        var normalized = LauncherRules.NormalizePath(shortcutPath);
        if (normalized.Length == 0) return ShortcutTarget.Unresolved;

        if (_shortcutCache.TryGetValue(normalized, out var cached)) return cached;

        // COM 必须在 STA 线程上跑；StaRunner 内部保证线程模型，失败返回 default（即 Unresolved）。
        var resolved = StaRunner.Run(() => ResolveCore(normalized));
        _shortcutCache[normalized] = resolved;
        return resolved;
    }

    /// <summary>清掉快捷方式解析缓存（点「刷新状态」时调用，避免读到软件更新前的旧目标）。</summary>
    public void ClearCache() => _shortcutCache.Clear();

    // ---------------------------------------------------------------- 内部实现

    /// <summary>在 STA 线程上执行真正的 COM 解析；所有失败都折成 <see cref="ShortcutTarget.Unresolved"/>。</summary>
    private static ShortcutTarget ResolveCore(string shortcutPath)
    {
        object? shellLinkObject = null;
        try
        {
            if (!File.Exists(shortcutPath)) return ShortcutTarget.Unresolved;

            // 用 CLSID 创建 ShellLink 示例：比 [ComImport] class 在各种 .NET 运行时上都稳。
            var type = Type.GetTypeFromCLSID(ShellInterop.ShellLinkClassId);
            if (type is null) return ShortcutTarget.Unresolved;

            shellLinkObject = Activator.CreateInstance(type);
            if (shellLinkObject is not ShellInterop.IShellLinkW link) return ShortcutTarget.Unresolved;
            if (shellLinkObject is not ShellInterop.IPersistFile persistFile) return ShortcutTarget.Unresolved;

            // STGM_READ：只读加载，不会改用户的快捷方式。
            if (persistFile.Load(shortcutPath, ShellInterop.StgmRead) != 0) return ShortcutTarget.Unresolved;

            var targetPath = ReadText((buffer, capacity) => link.GetPath(buffer, capacity, IntPtr.Zero, 0));
            if (string.IsNullOrWhiteSpace(targetPath)) return ShortcutTarget.Unresolved;

            var arguments = ReadText((buffer, capacity) => link.GetArguments(buffer, capacity));
            var workingDirectory = ReadText((buffer, capacity) => link.GetWorkingDirectory(buffer, capacity));

            var iconPath = "";
            var iconIndex = 0;
            try
            {
                var buffer = new StringBuilder(1024);
                if (link.GetIconLocation(buffer, buffer.Capacity, out iconIndex) >= 0)
                    iconPath = buffer.ToString();
            }
            catch
            {
                // 图标位置读不出来不影响启动，按「没设置图标」处理。
            }

            return new ShortcutTarget(
                LauncherRules.NormalizePath(targetPath),
                arguments,
                workingDirectory,
                iconPath,
                iconIndex,
                Resolved: true);
        }
        catch
        {
            return ShortcutTarget.Unresolved;
        }
        finally
        {
            if (shellLinkObject is not null && Marshal.IsComObject(shellLinkObject))
            {
                try { Marshal.ReleaseComObject(shellLinkObject); } catch { /* 释放失败忽略 */ }
            }
        }
    }

    /// <summary>读一个字符串属性（HRESULT 失败 / 异常都当空字符串）。</summary>
    private static string ReadText(Func<StringBuilder, int, int> read, int capacity = 1024)
    {
        try
        {
            var buffer = new StringBuilder(capacity);
            return read(buffer, buffer.Capacity) >= 0 ? buffer.ToString() : "";
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// 启动器专用的 STA 工作线程：<see cref="SystemLauncherProbe"/> 的 Shell 解析与
/// <see cref="IconLoader"/> 的图标提取都从这里排队执行。
///
/// <para><b>为什么用一个常驻线程而不是每次 new Thread</b>：导入 / 刷新时可能连续处理几十项，
/// 每次创建线程 + 初始化 COM 太贵。常驻一个后台 STA 线程按队列执行，调用方用
/// <see cref="Run{T}"/> 同步取结果。</para>
///
/// <para><b>不会死锁</b>：后台线程只做 Shell / GDI 调用，不回调 UI 线程，也不依赖调用方线程；
/// 当前线程本来就是 STA（例如界面线程）时直接就地执行，避免自己等自己。</para>
/// </summary>
internal static class StaRunner
{
    private static readonly BlockingCollection<Action> Jobs = new();

    private static readonly Lazy<Thread> Worker =
        new(CreateWorker, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>在 STA 线程上执行 <paramref name="work"/> 并返回结果；失败 / 超时返回 default。</summary>
    public static T? Run<T>(Func<T> work, int timeoutMilliseconds = 20000)
    {
        if (work is null) return default;

        // 调用方已经是 STA（UI 线程）就地执行：省一次排队，也不会自己等自己。
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try { return work(); }
            catch { return default; }
        }

        _ = Worker.Value;

        // 用 TCS 而不是 ManualResetEvent：不需要在热路径上分配内核对象，异常也不会冒到线程池。
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Jobs.Add(() =>
        {
            try { completion.TrySetResult(work()); }
            catch { completion.TrySetResult(default); }
        });

        try
        {
            if (!completion.Task.Wait(timeoutMilliseconds)) return default;
            return completion.Task.Result;
        }
        catch
        {
            return default;
        }
    }

    private static Thread CreateWorker()
    {
        var thread = new Thread(Pump)
        {
            IsBackground = true, // 不阻止程序退出
            Name = "YuLiangToolkit Launcher STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static void Pump()
    {
        // COINIT_APARTMENTTHREADED；返回值 < 0 表示系统已经用别的模型初始化过（极少见），
        // 这时不能调 CoUninitialize，直接跑任务即可（GDI / SHGetFileInfo 仍可用）。
        var comInitialized = ShellInterop.CoInitializeEx(IntPtr.Zero, ShellInterop.CoInitApartmentThreaded) >= 0;
        try
        {
            foreach (var job in Jobs.GetConsumingEnumerable())
            {
                try { job(); } catch { /* 单个任务失败不能弄死整个线程 */ }
            }
        }
        finally
        {
            if (comInitialized) ShellInterop.CoUninitialize();
        }
    }
}

/// <summary>Shell 相关的 P/Invoke 与 COM 声明（都集中在这里，启动器只用到这一份）。</summary>
internal static class ShellInterop
{
    /// <summary>ShellLink COM 类的 CLSID。</summary>
    public static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    /// <summary><c>STGM_READ</c>：以只读方式打开快捷方式。</summary>
    public const uint StgmRead = 0;

    /// <summary><c>COINIT_APARTMENTTHREADED</c>。</summary>
    public const uint CoInitApartmentThreaded = 0x2;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    /// <summary>
    /// <c>IShellLinkW</c>（宽字符版）。方法顺序必须与 Windows SDK 头文件完全一致，
    /// 否则 vtable 偏移错位，轻则读不到值、重则崩溃。
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        [PreserveSig]
        int GetIDList(out IntPtr ppidl);

        [PreserveSig]
        int SetIDList(IntPtr pidl);

        [PreserveSig]
        int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        [PreserveSig]
        int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        [PreserveSig]
        int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        [PreserveSig]
        int GetHotkey(out short pwHotkey);

        [PreserveSig]
        int SetHotkey(short wHotkey);

        [PreserveSig]
        int GetShowCmd(out int piShowCmd);

        [PreserveSig]
        int SetShowCmd(int iShowCmd);

        [PreserveSig]
        int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        [PreserveSig]
        int Resolve(IntPtr hwnd, uint fFlags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary><c>IPersistFile</c>：负责把 <c>.lnk</c> 文件加载进 <c>IShellLink</c>（含 IPersist 的 GetClassID）。</summary>
    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        [PreserveSig]
        int GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        [PreserveSig]
        int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        [PreserveSig]
        int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        [PreserveSig]
        int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
