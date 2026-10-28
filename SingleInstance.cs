using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.PictureViewer;

namespace YuLiangToolkit.App;

/// <summary>
/// 单实例唤醒：**再启动一次时不再开新窗口**，而是把命令行（例如双击图片的路径）交给已经开着的那个窗口，
/// 由它把窗口叫到前面并打开图片 / 文件夹。文件关联（见 AGENTS 2.19）靠它才不会每双击一张图就多一个窗口。
///
/// <para><b>做法</b>（不引第三方库）：</para>
/// <list type="number">
///   <item>启动时按「exe 所在程序集的路径」派生一个名字，创建 <see cref="Mutex"/>：只有第一个进程 CreateNew 成功；</item>
///   <item>第一个进程在 UI 线程就绪后开一条**命名管道**服务端收消息（JSON 字符串数组 = 第二实例的命令行参数）；</item>
///   <item>第二个进程连管道把参数发过去，然后直接退出（不建窗口）；</item>
///   <item>第一个进程收到消息后在 UI 线程：把窗口从托盘 / 最小化恢复并置前；参数里有受支持的图片就进「图片连续阅读器」并定位到那张。</item>
/// </list>
///
/// <para><b>为什么不用 WM_COPYDATA / FindWindow</b>：窗口标题 / 类名会变，多窗口时找不到“那个”窗口；
/// 命名管道按用户会话命名，稳定且不需要 Win32 消息钩子。</para>
///
/// <para><b>调试 / 截图时要关掉</b>：设了 <c>YLT_SHOT</c>、<c>YLT_NAV_TOOL</c>、<c>YLT_SHOT_TOOL</c>、
/// <c>YLT_NAV_CATEGORY</c>、<c>YLT_OPEN_SETTINGS</c> 或 <c>YLT_NO_SINGLE_INSTANCE=1</c> 时视为“独立测试进程”，
/// 不抢已有窗口，免得截图 / UIA 测试被正在用的程序截胡（见 <see cref="IsEnabled"/>）。</para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>消息协议版本；以后要改格式时加 1，老版本会忽略不认识的消息。</summary>
    public const int ProtocolVersion = 1;

    private static readonly string AppToken = BuildAppToken();
    private static string MutexName => $@"Local\YuLiangToolkit.SingleInstance.{AppToken}";
    private static string PipeName => $"YuLiangToolkit.SingleInstance.{AppToken}";

    private readonly Mutex _mutex;
    private readonly bool _isFirstInstance;
    private CancellationTokenSource? _serverCts;
    private bool _disposed;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out _isFirstInstance);
    }

    /// <summary>这个进程是不是“第一个”（只有它需要开管道服务端；第二实例只负责发送）。</summary>
    public bool IsFirstInstance => _isFirstInstance;

    /// <summary>
    /// 当前启动是否启用单实例：先看调试环境变量（截图 / UIA 测试要独立进程），
    /// 再看「图片连续阅读器」设置里的**单实例模式**开关（默认开；关掉 = 每次双击都新开窗口）。
    /// 这是启动级设置，运行中改了下次启动才生效。
    /// </summary>
    public static bool IsEnabled => !IsEnvironmentBypass() && ReadUserSetting();

    /// <summary>截图 / 调试环境变量会让每个进程独立启动（否则测试会被正在用的窗口吸走）。</summary>
    public static bool IsEnvironmentBypass()
    {
        foreach (var name in new[]
                 {
                     "YLT_SHOT", "YLT_NAV_TOOL", "YLT_SHOT_TOOL", "YLT_NAV_CATEGORY",
                     "YLT_OPEN_SETTINGS", "YLT_NO_SINGLE_INSTANCE",
                 })
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) return true;
        }

        return false;
    }

    /// <summary>
    /// 直接读「图片连续阅读器」的 config.json（不经过 ToolConfigStore，避免在 UI 起来之前
    /// 就把它缓存 / 挂上自动保存）。读不到（首次运行 / 文件坏）时按默认「单实例开」处理。
    /// </summary>
    private static bool ReadUserSetting()
    {
        try
        {
            var path = Path.Combine(
                AppPaths.ToolConfigDir(PictureViewerConfig.ToolId), "config.json");
            var config = JsonFileStore.Read<PictureViewerConfig>(path);
            return config?.SingleInstanceEnabled ?? true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 把命令行参数发给已有实例。连不上（比如旧实例还在启动 / 已经卡死）时会在
    /// <paramref name="timeout"/> 内重试；返回 false 表示没发出去，调用方可以退回“正常开新窗口”。
    /// </summary>
    public bool TrySendToExisting(IEnumerable<string> arguments, TimeSpan timeout)
    {
        var payload = SerializeMessage(arguments);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(300);
                using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(payload);
                writer.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                // 服务端还没建好：等下一轮
            }
            catch (IOException)
            {
                // 服务端正忙 / 刚退出：等下一轮
            }
            catch (UnauthorizedAccessException)
            {
                // 别的用户占着同名管道（理论上名字里带了程序集路径，极少发生）：放弃
                return false;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    /// <summary>
    /// 开命名管道服务端（后台线程循环）。收到一条消息就在 UI 线程回调一次；
    /// <paramref name="handler"/> 里不要再 await 长任务，只做“恢复窗口 + 打开图片”。
    /// </summary>
    public void StartServer(Action<string[]> handler)
    {
        if (_serverCts is not null) return;

        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    string json;
                    using (var reader = new StreamReader(server, Encoding.UTF8))
                    {
                        json = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                    }

                    if (!TryDeserializeMessage(json, out var arguments)) continue;

                    // 切 UI 线程执行；第二实例这时候已经退出，不会等我们
                    await Application.Current.Dispatcher.InvokeAsync(() => handler(arguments));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SingleInstance] 管道服务端异常（稍后重试）：{ex.Message}");
                    try { await Task.Delay(200, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, token);
    }

    /// <summary>第二实例发给第一实例的消息（JSON 对象：{ protocolVersion, arguments }）。</summary>
    public static string SerializeMessage(IEnumerable<string> arguments)
        => JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolVersion,
            arguments = arguments.ToArray(),
        });

    /// <summary>解析消息；格式 / 版本不对返回 false（不抛异常，坏消息直接忽略）。</summary>
    public static bool TryDeserializeMessage(string? json, out string[] arguments)
    {
        arguments = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("protocolVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != ProtocolVersion)
            {
                return false;
            }

            if (!root.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Array)
                return false;

            var list = new List<string>();
            foreach (var item in args.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return false;
                list.Add(item.GetString() ?? "");
            }

            arguments = list.ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 尽量让已有窗口能被置前。Windows 的“前台窗口锁”会拦跨进程 SetForegroundWindow；
    /// 第二实例是用户刚启动的（有前台权限），调用一下 AllowSetForegroundWindow 把权限放给所有进程，
    /// 第一实例再 SetForegroundWindow 成功率更高（失败也不影响，窗口至少会恢复 / 闪烁）。
    /// </summary>
    public static void AllowForegroundActivation()
    {
        try { AllowSetForegroundWindow(AsfwAny); }
        catch { /* 老系统 / 权限不足就跳过 */ }
    }

    /// <summary>把已经开着的窗口恢复并叫到前面（托盘 / 最小化 / 正常三种状态都处理）。</summary>
    public static void ActivateWindow(MainWindow window)
    {
        try
        {
            if (!window.IsVisible) window.ShowFromTray();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

            window.Show();
            window.Activate();

            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SingleInstance] 恢复窗口失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _serverCts?.Cancel();
        _serverCts?.Dispose();
        _serverCts = null;

        try { _mutex.Dispose(); }
        catch { /* 退出阶段忽略 */ }
    }

    /// <summary>按程序集路径（= 这一次安装 / 发布目录）生成一个稳定短标记，不同目录的程序互不干扰。</summary>
    private static string BuildAppToken()
    {
        try
        {
            var location = Assembly.GetEntryAssembly()?.Location ?? typeof(SingleInstance).Assembly.Location;
            if (string.IsNullOrWhiteSpace(location)) return "default";

            var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(location).ToLowerInvariant()));
            return Convert.ToHexString(bytes)[..12];
        }
        catch
        {
            return "default";
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private const int AsfwAny = -1;
}
