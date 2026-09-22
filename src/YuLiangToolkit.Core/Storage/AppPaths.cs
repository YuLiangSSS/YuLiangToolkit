namespace YuLiangToolkit.Core.Storage;

/// <summary>
/// 数据根目录解析：<c>Data</c>（功能数据）与 <c>Config</c>（功能配置）两个大文件夹都位于同一个根目录下。
///
/// <para><b>解析顺序</b>（先命中先用）：</para>
/// <list type="number">
///   <item>环境变量 <c>YLT_HOME</c>：显式指定根目录（测试 / 特殊部署用，优先级最高）；</item>
///   <item>开发态：从程序目录向上查找含 <c>YuLiangToolkit.sln</c> 的仓库根 →
///         直接用源码目录里的 <c>Data/</c> 和 <c>Config/</c>（这样你在 Rider/VS 里按运行，
///         读写的就是仓库里那两个文件夹，塞进去的 swf 等资源立刻可用）；</item>
///   <item>便携态：exe 旁边的 <c>Data/</c> <c>Config/</c>（发布后随程序走，拷贝整个目录即可迁移）；</item>
///   <item>兜底：<c>%AppData%\YuLiangToolkit</c>（exe 所在目录不可写时，例如装在 Program Files）。</item>
/// </list>
///
/// <para>目录一律“用到才建”，所有 IO 失败都静默降级，绝不因为存储问题让工具崩溃或无法启动。</para>
/// </summary>
public static class AppPaths
{
    /// <summary>功能数据文件夹名（每个功能一个同名小文件夹）。</summary>
    public const string DataFolderName = "Data";

    /// <summary>功能配置文件夹名（每个功能一个同名小文件夹）。</summary>
    public const string ConfigFolderName = "Config";

    /// <summary>便携模式标记文件名：放在 exe 旁边可强制使用 exe 同级目录（跳过仓库根探测）。</summary>
    public const string PortableMarkerFileName = ".ylt-portable";

    /// <summary>用于识别“仓库根/开发态”的文件名。</summary>
    private static readonly string[] RepoMarkers = { "YuLiangToolkit.sln" };

    private static readonly Lazy<string> LazyRoot = new(ResolveRoot, isThreadSafe: true);

    /// <summary>数据根目录（<see cref="DataRoot"/> 与 <see cref="ConfigRoot"/> 的父目录）。</summary>
    public static string Root => LazyRoot.Value;

    /// <summary>功能数据根目录：<c>&lt;Root&gt;\Data</c>。</summary>
    public static string DataRoot => Path.Combine(Root, DataFolderName);

    /// <summary>功能配置根目录：<c>&lt;Root&gt;\Config</c>。</summary>
    public static string ConfigRoot => Path.Combine(Root, ConfigFolderName);

    /// <summary>是否运行在“开发态”（数据目录 = 仓库根，而不是 exe 同级或 AppData）。</summary>
    public static bool IsDevelopmentLayout { get; private set; }

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

    /// <summary>当前数据根目录的可读描述（用于状态栏 / 关于页 / 排错）。</summary>
    public static string Describe()
    {
        var mode = IsDevelopmentLayout ? "开发态（仓库根）"
            : Root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
                ? "用户数据（%AppData%）"
                : "便携态（程序目录）";
        return $"{Root}　[{mode}]";
    }

    private static string ResolveRoot()
    {
        // 1) 显式指定
        var custom = Environment.GetEnvironmentVariable("YLT_HOME");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                var full = Path.GetFullPath(custom.Trim().Trim('"'));
                if (IsWritable(full)) return full;
            }
            catch { /* 路径非法则继续后面的策略 */ }
        }

        var baseDir = AppContext.BaseDirectory;

        // 2) 便携标记：exe 旁边放了 .ylt-portable 就强制用程序目录
        try
        {
            if (File.Exists(Path.Combine(baseDir, PortableMarkerFileName)) && IsWritable(baseDir))
                return baseDir;
        }
        catch { /* 忽略 */ }

        // 3) 开发态：向上找仓库根（含 sln）
        var repoRoot = FindRepositoryRoot(baseDir);
        if (repoRoot is not null && IsWritable(repoRoot))
        {
            IsDevelopmentLayout = true;
            return repoRoot;
        }

        // 4) 便携态：exe 同级
        if (IsWritable(baseDir)) return baseDir;

        // 5) 兜底：%AppData%\YuLiangToolkit
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuLiangToolkit");
        TryCreateDirectory(appData);
        return appData;
    }

    /// <summary>从程序目录逐级向上查找含仓库标记文件（sln）的目录。</summary>
    private static string? FindRepositoryRoot(string startDirectory)
    {
        try
        {
            var dir = new DirectoryInfo(startDirectory);
            for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            {
                foreach (var marker in RepoMarkers)
                {
                    if (File.Exists(Path.Combine(dir.FullName, marker))) return dir.FullName;
                }
            }
        }
        catch { /* 忽略 */ }
        return null;
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
