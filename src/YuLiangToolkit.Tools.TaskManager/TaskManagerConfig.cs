using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.TaskManager;

/// <summary>
/// 「任务管理器」的配置 → <c>Config\task-manager\config.json</c>。
/// 改一下自动保存，没有保存按钮。
/// </summary>
public sealed class TaskManagerConfig : ToolConfig
{
    public const string ToolId = "task-manager";

    /// <summary>监测刷新间隔（毫秒）。界面用下拉框选，<see cref="ClampRefreshInterval"/> 负责夹取。</summary>
    public int RefreshIntervalMs { get; set; } = 2000;

    /// <summary>进程列表排序方式：cpu / memory / gpu / name / pid / ports（见 <see cref="NormalizeSortMode"/>）。</summary>
    public string SortMode { get; set; } = "cpu";

    /// <summary>排序方向：true = 从大到小（CPU / 内存这类默认降序）。</summary>
    public bool SortDescending { get; set; } = true;

    /// <summary>搜索框内容（切走再回来保留）。</summary>
    public string SearchText { get; set; } = "";

    /// <summary>隐藏系统进程（系统目录里的 / 读不到路径的进程）。</summary>
    public bool HideSystemProcesses { get; set; }

    /// <summary>结束任务前弹确认框（默认开，避免误点）。</summary>
    public bool ConfirmBeforeKill { get; set; } = true;

    /// <summary>是否采集 GPU 占用（关掉可以少一点性能开销；显卡驱动不支持时自动显示「不可用」）。</summary>
    public bool MonitorGpu { get; set; } = true;

    /// <summary>是否采集端口占用（关掉后进程列表不显示端口，详情面板也不列端口）。</summary>
    public bool MonitorPorts { get; set; } = true;

    /// <summary>刷新间隔夹到 0.5 ~ 10 秒（太密会拖慢界面，太稀又不像任务管理器）。</summary>
    public static int ClampRefreshInterval(int milliseconds) => Math.Clamp(milliseconds, 500, 10000);

    /// <summary>排序方式归一：认不出来的值回退到 cpu（老配置 / 手改配置不会让界面崩）。</summary>
    public static string NormalizeSortMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "memory" => "memory",
        "gpu" => "gpu",
        "name" => "name",
        "pid" => "pid",
        "ports" => "ports",
        _ => "cpu",
    };

    /// <summary>排序方式的中文名（界面 / 自检共用）。</summary>
    public static string SortModeLabel(string? mode) => NormalizeSortMode(mode) switch
    {
        "memory" => "内存",
        "gpu" => "GPU",
        "name" => "名称",
        "pid" => "PID",
        "ports" => "端口数",
        _ => "CPU",
    };

    /// <summary>只读：当前排序方式的中文名（界面直接绑）。</summary>
    [JsonIgnore]
    public string SortModeText => SortModeLabel(SortMode);
}
