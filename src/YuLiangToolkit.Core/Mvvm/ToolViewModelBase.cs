using System.Windows;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Core.Mvvm;

/// <summary>
/// 工具 ViewModel 基类：把「数据文件夹 + 配置存储 + 忙碌状态 + 进度 + 日志 + 跨线程调度」
/// 这些每个功能都要写一遍的东西一次性备好。<b>新功能的 ViewModel 继承它，只写自己的业务即可。</b>
///
/// <para><b>最小实现</b>：</para>
/// <code>
/// public sealed class FlashDownloaderViewModel : ToolViewModelBase
/// {
///     public FlashDownloaderViewModel() : base("flash-downloader")   // Id 即 Data/Config 下的小文件夹名
///     {
///         Cfg = LoadConfig&lt;FlashDownloaderConfig&gt;();  // Config\flash-downloader\config.json
///         DownloadCommand = new AsyncRelayCommand(_ =&gt; DownloadAsync());
///     }
///
///     public FlashDownloaderConfig Cfg { get; }
///     public AsyncRelayCommand DownloadCommand { get; }
///
///     private async Task DownloadAsync()
///     {
///         IsBusy = true;
///         try
///         {
///             Log("开始下载…");
///             var swf = Storage.PathOf("game.swf");        // Data\flash-downloader\game.swf
///             await Task.Delay(1000);
///             StatusText = $"已保存到 {swf}";
///         }
///         finally { IsBusy = false; }
///     }
/// }
/// </code>
///
/// <para><b>生命周期</b>：主程序在「切换功能 / 返回主页」时会调用 <see cref="NotifyClosed"/>
/// （自动 Flush 配置），程序退出时 <see cref="ToolConfigStore.FlushAll"/> 兜底。</para>
/// </summary>
public abstract class ToolViewModelBase : ObservableObject, IDisposable
{
    private const int LogMaxChars = 20000;

    protected ToolViewModelBase(string toolId)
    {
        ToolId = toolId;
        Storage = ToolStorage.For(toolId);
        ConfigStore = ToolConfigStore.For(toolId);
        AppSettings = new AppSettingsService();
        IsFirstRun = !ConfigStore.Exists; // 注意：LoadConfig 之后该文件必然存在，所以这里提前判断
    }

    // ---------- 基础设施 ----------

    /// <summary>功能 Id（同时是 <c>Data\</c> / <c>Config\</c> 下的小文件夹名）。</summary>
    public string ToolId { get; }

    /// <summary>功能数据文件夹：<c>Data\&lt;功能名&gt;</c>（放 swf、模板、缓存等资源）。</summary>
    public ToolStorage Storage { get; }

    /// <summary>功能配置存储：<c>Config\&lt;功能名&gt;\config.json</c>。</summary>
    public ToolConfigStore ConfigStore { get; }

    /// <summary>全局设置（FFmpeg 路径等跨功能共享项），持久化在 <c>%AppData%\YuLiangToolkit\settings.json</c>。</summary>
    public AppSettingsService AppSettings { get; }

    /// <summary>是否是本功能第一次运行（配置文件还不存在）——可用来弹引导提示。</summary>
    public bool IsFirstRun { get; }

    /// <summary>数据文件夹路径（给界面展示 / 复制）。</summary>
    public string StoragePathText => Storage.Directory;

    /// <summary>配置文件路径（给界面展示 / 复制）。</summary>
    public string ConfigPathText => ConfigStore.FilePath;

    /// <summary>
    /// 加载强类型配置并挂上自动保存。
    /// <paramref name="createDefault"/> 只在“首次运行、配置文件还不存在”时调用，
    /// 可用来从老版本设置里迁移选项（返回一份带默认值的配置对象）。
    /// </summary>
    protected T LoadConfig<T>(Func<T>? createDefault = null) where T : ToolConfig, new()
        => createDefault is null ? ConfigStore.Load<T>() : ConfigStore.Load(createDefault);

    /// <summary>加载动态键值配置（<c>Config\&lt;功能名&gt;\options.json</c>），XAML 可用 <c>{Binding Dyn[键名]}</c> 绑定。</summary>
    protected DynamicToolConfig LoadDynamicConfig(string fileName = "options.json")
        => DynamicToolConfig.Load(ToolId, fileName);

    /// <summary>
    /// 加载动态键值配置，并给 <paramref name="defaults"/> 里声明过的键补上默认值
    /// （推荐：绑 CheckBox 时不会因为键不存在导致第一次点击没反应，配置文件也自带“有哪些选项”的说明）。
    /// </summary>
    protected DynamicToolConfig LoadDynamicConfigWithDefaults(
        IReadOnlyDictionary<string, object?> defaults,
        string fileName = "options.json")
        => DynamicToolConfig.LoadWithDefaults(ToolId, defaults, fileName);

    // ---------- 界面状态 ----------

    private bool _isBusy;
    /// <summary>是否正在执行耗时操作（用来禁用按钮 / 显示取消）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    private double _progress;
    /// <summary>进度 0.0 ~ 1.0（直接绑定 ProgressBar.Value）。</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, Math.Clamp(value, 0, 1));
    }

    private string _progressText = "0 / 0";
    /// <summary>进度文本，例如 "3 / 10"。</summary>
    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    private string _statusText = "就绪";
    /// <summary>状态栏文本（给用户看的一句话）。</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private string _logText = "";
    /// <summary>日志文本（绑定到只读 TextBox 即可）。</summary>
    public string LogText
    {
        get => _logText;
        set => Set(ref _logText, value);
    }

    /// <summary>追加一行日志（自动补换行、自动切回 UI 线程、超长自动截断）。</summary>
    public void Log(string line)
    {
        void Append()
        {
            var text = LogText;
            if (text.Length > LogMaxChars) text = text[^(LogMaxChars / 2)..];
            LogText = text + line + Environment.NewLine;
        }

        Ui(Append);
    }

    /// <summary>清空日志。</summary>
    public void ClearLog() => Ui(() => LogText = "");

    /// <summary>把动作切到 UI 线程执行（后台线程里改界面状态的统一入口）。</summary>
    public void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.InvokeAsync(action);
    }

    /// <summary>把异步动作切到 UI 线程执行，并可 await。</summary>
    public Task UiAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) return action();
        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    // ---------- 生命周期 ----------

    private bool _closed;

    /// <summary>工具被切换走 / 关闭：立刻落盘配置、停止后台保存检查并触发 <see cref="OnClosed"/>（可重入安全）。</summary>
    public void NotifyClosed()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            ConfigStore.Flush();
            ConfigStore.StopWatching();
        }
        catch { /* 退出流程忽略 */ }
        try { OnClosed(); } catch { /* 不因为清理逻辑影响主流程 */ }
    }

    /// <summary>子类可重写：停止后台任务、释放资源等。配置已由基类 Flush。</summary>
    protected virtual void OnClosed() { }

    public void Dispose()
    {
        NotifyClosed();
        GC.SuppressFinalize(this);
    }
}
