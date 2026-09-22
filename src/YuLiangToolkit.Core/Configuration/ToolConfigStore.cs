using System.Collections.Concurrent;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Core.Configuration;

/// <summary>
/// 单个功能的配置存储：负责 <c>Config\&lt;功能名&gt;\&lt;文件名&gt;.json</c> 的读取 /
/// 自动保存 / 立即落盘。整个应用里同一个 <c>(功能名, 文件名)</c> 只会有一个实例（已缓存）。
///
/// <para><b>自动保存是怎么实现的</b>：跟踪的配置对象会被后台定时器周期性“序列化快照 → 与上次写盘内容比对”，
/// 一旦不同就原子写盘。这样<b>无论配置类用自动属性 <c>{ get; set; }</c> 还是 <c>Set(ref …)</c> 写法都能存下来</b>，
/// 不需要开发者记得调用任何 Save。<see cref="ToolConfig"/> 的属性变更通知只是“提前打招呼”，
/// 让改动更快被检查到，不是保存的必要条件。</para>
///
/// <para><b>两种用法</b>：</para>
/// <list type="bullet">
///   <item>强类型（推荐）：<c>Load&lt;FlashDownloaderConfig&gt;()</c>，属性变更全自动保存；</item>
///   <item>动态键值：<see cref="DynamicToolConfig"/>，不声明属性也能存，适合原型/临时开关。</item>
/// </list>
///
/// <para><b>落盘时机</b>：① 后台检查发现改动（最多滞后 <see cref="DebounceMs"/> 毫秒）；
/// ② 工具被关闭/切换（<c>ToolViewModelBase.NotifyClosed</c> 调 <see cref="Flush"/>）；
/// ③ 程序退出（<see cref="FlushAll"/>）。三条路径保证不丢改动。</para>
/// </summary>
public sealed class ToolConfigStore
{
    /// <summary>检查间隔（毫秒）：改动后最迟这么久写盘；同时也是连续输入时的合并窗口。</summary>
    public const int DefaultDebounceMs = 400;

    private static readonly ConcurrentDictionary<string, ToolConfigStore> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();      // 保护 _model / _snapshot / _lastJson / _watching
    private readonly object _writeGate = new(); // 串行化真正写盘
    private readonly Timer _watch;

    private object? _model;

    /// <summary>写盘时使用的快照函数（动态配置用它复制一份字典，避免序列化时被改动）。</summary>
    private Func<object>? _snapshot;

    /// <summary>上次成功写盘的内容（差异检测的基准）。</summary>
    private string? _lastJson;

    private bool _watching;

    /// <summary>已加载的强类型配置实例（按类型缓存，保证“一类型一实例”）。</summary>
    private readonly Dictionary<Type, object> _instances = new();

    private ToolConfigStore(string toolId, string fileName, int debounceMs)
    {
        ToolId = toolId;
        Directory = AppPaths.ToolConfigDir(toolId);
        FilePath = Path.Combine(Directory, fileName);
        DebounceMs = debounceMs;
        _watch = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>功能 Id（同时是 Config 下的小文件夹名）。</summary>
    public string ToolId { get; }

    /// <summary>该功能的配置小文件夹：<c>Config\&lt;功能名&gt;</c>。</summary>
    public string Directory { get; }

    /// <summary>配置文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>自动保存的检查间隔（毫秒）。</summary>
    public int DebounceMs { get; }

    /// <summary>配置文件是否已存在（可用来判断“首次运行”，做旧配置迁移）。</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>是否有尚未写盘的改动。</summary>
    public bool IsDirty
    {
        get
        {
            var json = CurrentJson();
            if (json is null) return false;
            lock (_gate) return json != _lastJson;
        }
    }

    /// <summary>最近一次成功写盘时间。</summary>
    public DateTime? LastSavedAt { get; private set; }

    /// <summary>最近一次写盘失败原因（正常为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>写盘失败时触发（只读盘、磁盘满等；UI 可据此提示用户）。</summary>
    public event EventHandler<string>? SaveFailed;

    /// <summary>
    /// 每次成功写盘后触发（可能在后台线程）——想“跟着配置变化刷新界面”就订阅它，
    /// 记得用 <c>ToolViewModelBase.Ui(...)</c> 切回 UI 线程。
    /// </summary>
    public event EventHandler? Saved;

    /// <summary>取（或创建）某个功能的配置存储。</summary>
    /// <param name="toolId">功能 Id，例如 "flash-downloader"。</param>
    /// <param name="fileName">配置文件名，默认 config.json；同一功能需要多份配置时改这里。</param>
    /// <param name="debounceMs">自动保存检查间隔，默认 400ms。</param>
    public static ToolConfigStore For(string toolId, string fileName = "config.json", int debounceMs = DefaultDebounceMs)
    {
        var key = AppPaths.SanitizeFolderName(toolId) + "|" + fileName;
        return Cache.GetOrAdd(key, _ => new ToolConfigStore(toolId, fileName, debounceMs));
    }

    /// <summary>
    /// 读取强类型配置：文件不存在 → 用默认值（可用 <paramref name="createDefault"/> 从旧配置迁移），
    /// 并立刻写盘一次，保证配置文件马上出现在 <c>Config\&lt;功能名&gt;\</c> 里。
    /// 以后给配置类新增属性不会影响老文件（缺失的属性自动用类里的默认值）。
    ///
    /// <para>同一个 <typeparamref name="T"/> 在同一存储里只会有一个实例（重复调用返回同一对象），
    /// 避免两个实例互相覆盖对方的改动。</para>
    /// </summary>
    public T Load<T>(Func<T>? createDefault = null) where T : ToolConfig, new()
    {
        T? cached = null;
        lock (_gate)
        {
            if (_instances.TryGetValue(typeof(T), out var existing)) cached = (T)existing;
        }

        if (cached is not null)
        {
            // 之前可能因为工具被关闭而停掉了后台保存，这里恢复，
            // 否则“关闭功能再打开，改选项不落盘”。
            StartWatching();
            return cached;
        }

        var model = JsonFileStore.Read<T>(FilePath) ?? createDefault?.Invoke() ?? new T();
        return Track(model);
    }

    /// <summary>接管一个已经构造好的强类型配置对象（挂上自动保存并立即落盘一次）。</summary>
    public T Track<T>(T config) where T : ToolConfig
    {
        config.AttachTo(this);
        lock (_gate) _instances[typeof(T)] = config;
        TrackModel(config);
        return config;
    }

    /// <summary>
    /// 接管任意可序列化模型（<see cref="DynamicToolConfig"/> 这类非 <see cref="ToolConfig"/> 的场景）。
    /// <paramref name="snapshot"/> 可选：返回一份用于序列化的拷贝，避免写盘时模型正被改动。
    /// </summary>
    public void TrackModel(object model, Func<object>? snapshot = null)
    {
        lock (_gate)
        {
            _model = model;
            _snapshot = snapshot;
        }
        SaveNow();
        StartWatching();
    }

    /// <summary>启动后台自动保存检查（重复调用无副作用）。</summary>
    public void StartWatching()
    {
        lock (_gate)
        {
            if (_watching) return;
            _watching = true;
        }
        try { _watch.Change(DebounceMs, DebounceMs); } catch (ObjectDisposedException) { /* 忽略 */ }
    }

    /// <summary>停止后台自动保存检查（工具关闭时调用；之后仍可手动 <see cref="SaveNow"/>）。</summary>
    public void StopWatching()
    {
        lock (_gate) _watching = false;
        try { _watch.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { /* 忽略 */ }
    }

    /// <summary>
    /// 兼容用提示：配置对象属性变更时会调用它。真正的写盘由后台检查兜底，
    /// 所以即使配置类用的是自动属性（不发通知）也一样能自动保存。
    /// </summary>
    public void ScheduleSave() => StartWatching();

    /// <summary>立刻写盘（无论内容是否变化）。失败返回 false，并把原因记到 <see cref="LastError"/>。</summary>
    public bool SaveNow()
    {
        var json = CurrentJson();
        return json is not null && WriteJsonInternal(json);
    }

    /// <summary>如果有未写盘的改动就立刻写盘（用于工具关闭 / 程序退出 / 切换功能）。</summary>
    public void Flush()
    {
        var json = CurrentJson();
        if (json is null) return;
        lock (_gate)
        {
            if (json == _lastJson) return;
        }
        WriteJsonInternal(json);
    }

    /// <summary>在资源管理器里打开该功能的配置文件夹（不存在则先建）。</summary>
    public bool OpenInExplorer()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Directory,
                UseShellExecute = true,
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>删除配置文件（下次加载回到默认值）。</summary>
    public bool DeleteFile()
    {
        try
        {
            lock (_gate) _lastJson = null;
            if (!File.Exists(FilePath)) return true;
            File.Delete(FilePath);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"删除 {FilePath} 失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>当前进程里所有已创建的配置存储（诊断用）。</summary>
    public static IReadOnlyList<ToolConfigStore> All => Cache.Values.ToList();

    /// <summary>立刻落盘所有待写配置——程序退出前务必调用（<c>App</c> 已接好）。</summary>
    public static void FlushAll()
    {
        foreach (var store in Cache.Values)
        {
            try
            {
                store.Flush();
                store.StopWatching();
            }
            catch { /* 退出流程不抛异常 */ }
        }
    }

    // ---------- 内部实现 ----------

    /// <summary>后台检查：把当前内容序列化出来和上次写盘的内容比对，不同就写盘。</summary>
    private void Tick()
    {
        try
        {
            var json = CurrentJson();
            if (json is null) return;
            lock (_gate)
            {
                if (json == _lastJson) return;
            }
            WriteJsonInternal(json);
        }
        catch { /* 后台线程绝不抛异常 */ }
    }

    /// <summary>取当前模型的 JSON 文本（线程安全：动态配置用快照函数复制）。</summary>
    private string? CurrentJson()
    {
        object? model;
        Func<object>? snapshot;
        lock (_gate)
        {
            model = _model;
            snapshot = _snapshot;
        }
        if (model is null) return null;

        object payload;
        try { payload = snapshot?.Invoke() ?? model; }
        catch { payload = model; }
        return JsonFileStore.Serialize(payload);
    }

    private bool WriteJsonInternal(string json)
    {
        lock (_writeGate)
        {
            var ok = JsonFileStore.WriteJson(FilePath, json, msg =>
            {
                LastError = msg;
                SaveFailed?.Invoke(this, msg);
            });

            if (ok)
            {
                lock (_gate) _lastJson = json;
                LastSavedAt = DateTime.Now;
                LastError = null;
                try { Saved?.Invoke(this, EventArgs.Empty); } catch { /* 订阅方异常不影响保存 */ }
            }
            return ok;
        }
    }
}
