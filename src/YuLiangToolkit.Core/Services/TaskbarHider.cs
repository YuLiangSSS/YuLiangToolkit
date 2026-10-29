using System.Runtime.InteropServices;

namespace YuLiangToolkit.Core.Services;

/// <summary>
/// 全屏时把 Windows 任务栏**藏起来**（不是“盖住”，是真的 <c>ShowWindow(SW_HIDE)</c>）。
///
/// <para>为什么不用“窗口铺满 + 置顶”那套：任务栏自己是置顶窗口，普通窗口靠 z 序盖它并不可靠
/// （实测在部分机器/主题上照样露出来，v0.9.2 就是这么翻车的）。所以改成直接藏：
/// 主屏 <c>Shell_TrayWnd</c> + 每块副屏的 <c>Shell_SecondaryTrayWnd</c> 一起藏。</para>
///
/// <para><b>调用方要“盯着藏”</b>：explorer 有时会把任务栏自己显示回来（切换激活窗口、Win 键、
/// 桌面刷新……），所以外壳在全屏期间每隔几百毫秒调一次 <see cref="Hide"/>。
/// <see cref="Hide"/> 会先检查上次藏的那些窗口有没有又冒出来，冒出来就重新藏 —— 重复调用很便宜、安全。</para>
///
/// <para>⚠ 这个操作会影响整个桌面的外观，所以做了几道保险，保证不把用户的任务栏“留没”：</para>
/// <list type="bullet">
///   <item><see cref="Restore"/> 是幂等的，随时调都行；</item>
///   <item>进程退出 / 未处理异常时自动还原（首次 <see cref="Hide"/> 时挂钩子）；</item>
///   <item>外壳还要在「退出全屏 / 窗口失活 / 最小化 / 关窗口」时主动调 <see cref="Restore"/>。</item>
/// </list>
///
/// <para>⚠ <b>藏不住时第一件事看 <see cref="LastError"/>（配合 <c>YLT_TASKBAR_TRACE</c>）</b>：
/// 如果是 <c>5</c>（拒绝访问），说明**本进程的完整性级别比任务栏低**，Windows 的 UIPI 不允许我们
/// 去动别人的窗口 —— 这是“整个文件夹被标成 Low 完整性”的环境问题（v0.9.3 真机上遇到过：
/// 工作区目录继承了别的沙箱工具留下的 <c>Low Mandatory Level</c> 标记，exe 于是以 Low 权限运行，
/// ShowWindow / SHAppBarMessage 一律返回 5）。换个文件夹、或把该目录的完整性级别改回 Medium
/// （<c>icacls &lt;目录&gt; /setintegritylevel "(OI)(CI)Medium"</c>）就好，代码这边无解。</para>
/// <para>⚠ 自检**只测只读的完整性判定**（<see cref="IntegrityCheck"/>），**不真藏任务栏**：
/// 跑测试时把开发者自己的任务栏藏起来太吓人（见 docs/dev/pitfalls.md）。藏不住时先看程序启动时打的控制台
/// 「完整性自检」—— 错误码 <c>5</c> 它已经替你翻译成“进程级别不够”。</para>
/// </summary>
public static class TaskbarHider
{
    private const int SwHide = 0;
    private const int SwShow = 5;   // SW_SHOW：按原来的位置/大小显示

    private const int ErrorSuccess = 0;
    private const int ErrorAccessDenied = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpHideWindow = 0x0080;
    private const uint SwpShowWindow = 0x0040;

    private static readonly List<IntPtr> Hidden = new();
    private static bool _hooksInstalled;

    private static int _lastError;
    private static int _failureStreak;

    /// <summary>现在是不是处于“任务栏被我们藏起来”的状态。</summary>
    public static bool IsHidden => Hidden.Count > 0;

    /// <summary>
    /// 上一次 <see cref="Hide"/> 失败时 Windows 给的错误码（0 = 没失败过）。
    /// <c>5</c>（拒绝访问）= 本进程完整性级别低于任务栏，见类注释。
    /// </summary>
    public static int LastError => _lastError;

    /// <summary>
    /// 藏起所有任务栏（主屏 + 副屏）。重复调用安全、也很便宜（可以每几百毫秒调一次）：
    /// 已经藏着的窗口只在“又冒出来了”时才重新藏完再返回。
    /// 返回**是否已经全部藏住**；一个都没藏成（权限不够 / 系统用的是第三方任务栏 / 还没建好）返回 false。
    /// </summary>
    public static bool Hide()
    {
        InstallSafetyHooks();
        DropReappearedWindows();   // explorer 有时会自己把任务栏显示回来 → 划掉，下面重新藏

        if (Hidden.Count > 0)
        {
            _failureStreak = 0;
            return true;
        }

        _lastError = ErrorSuccess;
        var hidden = 0;

        var main = FindWindow("Shell_TrayWnd", null);
        if (TryHide(main)) hidden++;

        // 多显示器：每块副屏都有自己的 Shell_SecondaryTrayWnd
        var after = IntPtr.Zero;
        while ((after = FindWindowEx(IntPtr.Zero, after, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (TryHide(after)) hidden++;
        }

        if (hidden > 0)
        {
            _failureStreak = 0;
            Trace($"Hide: 藏掉 {hidden} 个任务栏窗口");
            return true;
        }

        // 只在“刚开始失败”时写一行，免得全屏期间每 400ms 就刷一条（排查日志要能一眼看出原因）
        _failureStreak++;
        if (_failureStreak == 1)
        {
            Trace($"Hide 失败：{Describe(_lastError)}（之后每 400ms 还会重试，成功才再写日志）");
        }
        return false;
    }

    /// <summary>把藏起来的任务栏恢复回去。幂等，没藏过就是空操作。</summary>
    public static void Restore()
    {
        Trace($"Restore: 待恢复 {Hidden.Count} 个");
        _failureStreak = 0;
        if (Hidden.Count == 0) return;

        foreach (var hwnd in Hidden.ToArray())
        {
            try
            {
                ShowWindowAsync(hwnd, SwShow);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpShowWindow);
                ShowWindow(hwnd, SwShow);
            }
            catch { /* 窗口没了就算了 */ }
        }

        Hidden.Clear();
    }

    /// <summary>藏过、但现在又可见了 → 从清单里划掉（下一轮 <see cref="Hide"/> 会重新藏）。</summary>
    private static void DropReappearedWindows()
    {
        for (var i = Hidden.Count - 1; i >= 0; i--)
        {
            if (!IsWindowVisible(Hidden[i])) continue;
            Trace($"  Hide: hwnd={Hidden[i]} 又冒出来了，重新藏");
            Hidden.RemoveAt(i);
        }
    }

    private static bool TryHide(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!IsWindowVisible(hwnd)) return false;   // 本来就看不见（隐藏 / 不在这个桌面），不用记

        // ⚠ 跨进程直接 ShowWindow(SW_HIDE) 有可能被“吞掉”：返回 False、窗口其实还可见。
        // 所以这里三管齐下 + **以真实可见性为准**：异步 ShowWindow → SetWindowPos → 直接 ShowWindow。
        // ⚠ 别在这里 Thread.Sleep 等它生效：这是 UI 线程上的一次性动作，卡住界面得不偿失。
        //   异步那一发稍晚生效没关系 —— 外面的看门狗（MainWindow 的全屏计时器）会再调一次 Hide。
        ShowWindowAsync(hwnd, SwHide);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpHideWindow);
        var posError = Marshal.GetLastWin32Error();
        ShowWindow(hwnd, SwHide);
        var showError = Marshal.GetLastWin32Error();

        if (showError != ErrorSuccess) _lastError = showError;
        else if (posError != ErrorSuccess) _lastError = posError;

        var visible = IsWindowVisible(hwnd);
        Trace($"  TryHide: hwnd={hwnd} SetWindowPos err={posError}, ShowWindow err={showError}, 现在可见={visible}");
        if (visible) return false;

        Hidden.Add(hwnd);
        return true;
    }

    /// <summary>错误码 → 一句人话（失败时写进排查日志）。</summary>
    public static string Describe(int errorCode) => errorCode switch
    {
        ErrorSuccess => "窗口没找到 / 本来就不可见（多半是第三方任务栏或还没建好）",
        ErrorAccessDenied => "拒绝访问（5）—— 本进程完整性级别低于任务栏，Windows 不允许跨级别操作别人的窗口",
        _ => $"错误码 {errorCode}",
    };

    /// <summary>
    /// 排查用：设了 <c>YLT_TASKBAR_TRACE=&lt;文件&gt;</c> 就把每次藏/还的细节写进文件（不设就没有任何开销）。
    /// 任务栏藏不住时先看这个日志：是没找到窗口、ShowWindow 没生效、还是被还回去了。
    /// </summary>
    private static void Trace(string line)
    {
        var path = Environment.GetEnvironmentVariable("YLT_TASKBAR_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}\r\n");
        }
        catch { /* 排查日志写不进去就算了 */ }
    }

    /// <summary>进程不管怎么退（正常退出 / 崩了），都要把任务栏还回去。</summary>
    private static void InstallSafetyHooks()
    {
        if (_hooksInstalled) return;
        _hooksInstalled = true;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Restore();
    }
}
