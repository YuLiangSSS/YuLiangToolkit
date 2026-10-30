namespace YuLiangToolkit.Core.Storage;

/// <summary>
/// 数据根目录解析：<c>Data</c>（功能数据）与 <c>Config</c>（功能配置）两个大文件夹都在这个根目录下。
///
/// <para><b>默认就在 exe 旁边</b>（<c>bin\Debug\net9.0-windows10.0.19041.0\Data</c>、<c>...\Config</c>），
/// 这样测试 exe 与发布都一致：整个程序目录拷走 = 数据和配置一起带走。</para>
///
/// <para><b>解析顺序</b>（先命中先用）：</para>
/// <list type="number">
///   <item>环境变量 <c>YLT_HOME</c>：显式指定根目录（自检 / 特殊部署用，优先级最高；指向哪就用哪）；</item>
///   <item><b>exe 所在目录</b>（默认）：<c>AppContext.BaseDirectory</c>，即 exe 同级；</item>
///   <item>兜底：<c>%AppData%\YuLiangToolkit</c>——只有 exe 目录不可写时才会用到
///         （例如把程序装到 <c>C:\Program Files</c> 下）。状态栏会显示当前实际用的是哪个。</item>
/// </list>
///
/// <para><b>注意</b>：目录在首次访问时解析一次并缓存（进程级），之后改 <c>YLT_HOME</c> 不再生效；
/// 目录一律“用到才建”，所有 IO 失败都静默降级，不会因为存储问题让工具崩溃或无法启动。</para>
/// </summary>
public static class AppPaths
{
    /// <summary>功能数据文件夹名（每个功能一个同名小文件夹）。</summary>
    public const string DataFolderName = "Data";

    /// <summary>功能配置文件夹名（每个功能一个同名小文件夹）。</summary>
    public const string ConfigFolderName = "Config";

    private static readonly Lazy<string> LazyRoot = new(ResolveRoot, isThreadSafe: true);

    /// <summary>数据根目录（<see cref="DataRoot"/> 与 <see cref="ConfigRoot"/> 的父目录）。</summary>
    public static string Root => LazyRoot.Value;

    /// <summary>功能数据根目录：<c>&lt;Root&gt;\Data</c>。</summary>
    public static string DataRoot => Path.Combine(Root, DataFolderName);

    /// <summary>功能配置根目录：<c>&lt;Root&gt;\Config</c>。</summary>
    public static string ConfigRoot => Path.Combine(Root, ConfigFolderName);

    /// <summary>是否退到了 <c>%AppData%</c>（exe 目录不可写时的兜底方案）。</summary>
    public static bool IsUserDataFallback { get; private set; }

    /// <summary>是否由环境变量 <c>YLT_HOME</c> 显式指定。</summary>
    public static bool IsCustomRoot { get; private set; }

    /// <summary>某个功能的数据小文件夹：<c>Data\&lt;功能名&gt;</c>（只拼路径，不创建）。</summary>
    public static string ToolDataDir(string toolId)
        => Path.Combine(DataRoot, SanitizeFolderName(toolId));

    /// <summary>某个功能的配置小文件夹：<c>Config\&lt;功能名&gt;</c>（只拼路径，不创建）。</summary>
    public static string ToolConfigDir(string toolId)
        => Path.Combine(ConfigRoot, SanitizeFolderName(toolId));

    /// <summary>把工具 Id 规范成安全的文件夹名（去掉非法字符，空则回退 <c>unknown</c>）。</summary>
    public static string SanitizeFolderName(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(toolId.Length);
        foreach (var ch in toolId.Trim())
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
        var name = sb.ToString().Trim('.', ' ');
        return name.Length == 0 ? "unknown" : name;
    }

    /// <summary>确保 <c>Data</c> 与 <c>Config</c> 两个根文件夹存在（失败不影响运行）。</summary>
    public static void EnsureCreated()
    {
        TryCreateDirectory(DataRoot);
        TryCreateDirectory(ConfigRoot);
    }

    /// <summary>当前数据根目录的可读描述（状态栏 / 关于页 / 排错用）。</summary>
    public static string Describe()
    {
        var mode = IsCustomRoot ? "YLT_HOME 指定"
            : IsUserDataFallback ? "用户数据（%AppData%，exe 目录不可写）"
            : "程序目录（exe 同级）";
        return $"{Root}　[{mode}]";
    }

    private static string ResolveRoot()
    {
        // 1) 显式指定（自检、特殊部署）
        var custom = Environment.GetEnvironmentVariable("YLT_HOME");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                var full = Path.GetFullPath(custom.Trim().Trim('"'));
                if (IsWritable(full))
                {
                    IsCustomRoot = true;
                    return full;
                }
            }
            catch { /* 路径非法则继续后面的策略 */ }
        }

        // 2) 默认：exe 同级目录（bin\Debug\net9.0-windows10.0.19041.0\ 或发布出来的目录）
        var baseDir = AppContext.BaseDirectory;
        if (IsWritable(baseDir)) return baseDir;

        // 3) 兜底：exe 目录不可写（装在 Program Files 之类）时放到用户数据目录
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuLiangToolkit");
        TryCreateDirectory(appData);
        IsUserDataFallback = true;
        return appData;
    }

    /// <summary>真实写盘探测：能建目录并写入临时文件才算可写。</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".ylt-write-probe-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryCreateDirectory(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* 只读环境下静默忽略 */ }
    }
}
