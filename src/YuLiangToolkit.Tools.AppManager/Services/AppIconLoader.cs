using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.AppManager.Models;

namespace YuLiangToolkit.Tools.AppManager.Services;

/// <summary>
/// 应用图标加载器：用 Shell 的 <c>SHGetFileInfo</c> 取「和资源管理器里看到的一样」的图标，
/// 失败时退回「按扩展名给一个系统默认图标」（不空白、不崩）。
///
/// <para><b>线程</b>：<c>SHGetFileInfo</c> 与 <c>CreateBitmapSourceFromHIcon</c> 都通过
/// <see cref="AppStaRunner"/> 在 STA 线程上执行（COM / GDI 的老规矩）；返回的
/// <see cref="BitmapSource"/> 一律 <c>Freeze()</c>，所以可以安全地从后台线程交给界面绑定。</para>
///
/// <para><b>缓存</b>：同一批应用在筛选 / 排序 / 重扫时会反复请求图标，
/// 按「图标源 + 目标路径」缓存；<c>ConcurrentDictionary</c> 不收 null，
/// 所以失败结果不进缓存（下次刷新还会再试一次）。</para>
/// </summary>
public sealed class AppIconLoader
{
    /// <summary>界面最多显示 32px，取 48px 留高分屏余量。</summary>
    private const int IconSize = 48;

    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    private readonly ConcurrentDictionary<string, BitmapSource> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取一个应用的图标（可能为 null = 连系统默认图标都拿不到）。</summary>
    public BitmapSource? Load(InstalledApp? app)
    {
        if (app is null) return null;

        var target = ResolveIconTarget(app);
        var key = app.IconSource + "|" + target;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var icon = AppStaRunner.Run(() => LoadCore(target));
        if (icon is not null) _cache[key] = icon;
        return icon;
    }

    /// <summary>清空缓存（点「刷新」时用，软件换图标后能读到新的）。</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// 决定拿谁当图标源：<c>DisplayIcon</c>（去掉 <c>,0</c> 这种序号）→ 卸载程序 → 安装目录。
    /// 优先用 <c>.exe</c> / <c>.ico</c>（能拿到真图标），最后才退回目录图标。
    /// </summary>
    public static string ResolveIconTarget(InstalledApp app)
    {
        var icon = AppCatalogRules.StripIconIndex(app.IconSource);
        if (icon.Length > 0 && HasExecutableExtension(icon)) return icon;

        var uninstall = AppCatalogRules.SplitCommandLine(app.UninstallCommand).Executable;
        if (uninstall.Length > 0 && HasExecutableExtension(uninstall)) return uninstall;

        if (icon.Length > 0) return icon;
        if (app.InstallLocation.Length > 0) return app.InstallLocation;
        return uninstall;
    }

    /// <summary>是不是能直接取图标的文件（exe / dll / ico）。</summary>
    private static bool HasExecutableExtension(string path)
    {
        var extension = ExtensionOf(path);
        return extension is ".exe" or ".dll" or ".ico";
    }

    /// <summary>取路径的扩展名（小写；没有返回空串）。</summary>
    private static string ExtensionOf(string? path)
    {
        var value = (path ?? "").Trim().Trim('"');
        if (value.Length == 0) return "";
        var dot = value.LastIndexOf('.');
        if (dot < 0 || dot == value.Length - 1) return "";
        return value[dot..].ToLowerInvariant();
    }

    // ---------------------------------------------------------------- STA 线程上执行

    /// <summary>先试 <c>PrivateExtractIcons</c>（能拿到更大更清楚的图标），失败退回 <c>SHGetFileInfo</c>。</summary>
    private static BitmapSource? LoadCore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var extension = ExtensionOf(path);
            if (extension is ".exe" or ".dll" or ".ico")
            {
                var large = ExtractLargeIcon(path);
                if (large is not null) return large;
            }
        }
        catch
        {
            // 大图标这条路失败就往下走 SHGetFileInfo。
        }

        try
        {
            return ExtractSystemIcon(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用 <c>PrivateExtractIcons</c> 取指定尺寸的图标（<c>.exe / .dll / .ico</c>）。</summary>
    private static BitmapSource? ExtractLargeIcon(string path)
    {
        var icons = new[] { IntPtr.Zero };
        var iconIds = new uint[1];
        var count = PrivateExtractIcons(path, 0, IconSize, IconSize, icons, iconIds, 1, 0);
        if (count == 0 || icons[0] == IntPtr.Zero) return null;

        try
        {
            return ToBitmapSource(icons[0]);
        }
        finally
        {
            DestroyIcon(icons[0]);   // 必须销毁，否则每行都会漏一个图标句柄
        }
    }

    /// <summary>用 <c>SHGetFileInfo</c> 取系统图标（任何路径都能拿到一个）。</summary>
    private static BitmapSource? ExtractSystemIcon(string path)
    {
        var info = new SHFILEINFO { szDisplayName = "", szTypeName = "" };
        var flags = ShgfiIcon | ShgfiLargeIcon;
        if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) flags |= ShgfiUseFileAttributes;

        var result = SHGetFileInfo(path, FileAttributeNormal, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            return ToBitmapSource(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>HICON → 已冻结的 WPF 位图（可跨线程）。</summary>
    private static BitmapSource? ToBitmapSource(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string lpszFile,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        [Out] IntPtr[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>
/// 图标提取专用的 STA 工作线程：<c>SHGetFileInfo</c> / <c>PrivateExtractIcons</c> 要在 STA 线程上跑，
/// 而扫描是后台 <see cref="Task"/>（线程池是 MTA）。常驻一个后台 STA 线程按队列执行，调用方同步取结果。
///
/// <para><b>不会死锁</b>：调用方本来就是 STA（界面线程）时直接就地执行，避免自己等自己。</para>
/// </summary>
internal static class AppStaRunner
{
    private static readonly BlockingCollection<Action> Jobs = new();

    private static readonly Lazy<Thread> Worker =
        new(CreateWorker, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>在 STA 线程上执行 <paramref name="work"/> 并返回结果；失败 / 超时返回 default。</summary>
    public static T? Run<T>(Func<T> work, int timeoutMilliseconds = 20000)
    {
        if (work is null) return default;

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try { return work(); }
            catch { return default; }
        }

        _ = Worker.Value;

        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Jobs.Add(() =>
        {
            try { completion.TrySetResult(work()); }
            catch { completion.TrySetResult(default); }
        });

        try
        {
            return completion.Task.Wait(timeoutMilliseconds) ? completion.Task.Result : default;
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
            IsBackground = true,   // 不阻止程序退出
            Name = "YuLiangToolkit AppManager STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static void Pump()
    {
        var comInitialized = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded) >= 0;
        try
        {
            foreach (var job in Jobs.GetConsumingEnumerable())
            {
                try { job(); } catch { /* 单个任务失败不能弄死整个线程 */ }
            }
        }
        finally
        {
            if (comInitialized) CoUninitialize();
        }
    }

    private const uint CoInitApartmentThreaded = 0x2;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
