namespace YuLiangToolkit.Core.Storage;

/// <summary>
/// 在**运行时数据目录**（exe 旁边的 <c>Data\</c> 与 <c>Config\</c>）里放一份简短说明，
/// 只在文件不存在时写一次，用户改过/删过都不会被覆盖。
///
/// <para><b>为什么由程序写而不是构建时拷</b>：曾经用 MSBuild 的
/// <c>&lt;None CopyToOutputDirectory&gt;</c> 把仓库里的说明拷到输出目录，结果这些文件被记进
/// <c>obj\...\*.FileListAbsolute.txt</c>，仓库里的源文件一删，下次构建的 IncrementalClean
/// 就会把 exe 旁边的同名文件当垃圾删掉（可能连带用户数据）。所以改成程序自己写，构建过程完全不碰这两个目录。</para>
/// </summary>
public static class RuntimeReadme
{
    /// <summary>说明文件名（三个目录各一份）。</summary>
    public const string FileName = "怎么用这两个文件夹.md";

    /// <summary>缓存目录的说明文件名（缓存说明只有一份，所以名字不同）。</summary>
    public const string CacheReadmeFileName = "怎么用这个文件夹.md";

    /// <summary>确保 Data / Config / Cache 目录里有说明文件（已存在则跳过）。</summary>
    public static void EnsureSeeded()
    {
        WriteIfMissing(Path.Combine(AppPaths.DataRoot, FileName), BuildDataReadme());
        WriteIfMissing(Path.Combine(AppPaths.ConfigRoot, FileName), BuildConfigReadme());
        WriteIfMissing(Path.Combine(AppPaths.CacheRoot, CacheReadmeFileName), BuildCacheReadme());
    }

    private static void WriteIfMissing(string path, string content)
    {
        try
        {
            if (File.Exists(path)) return;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
        }
        catch { /* 只读环境忽略 */ }
    }

    private static string BuildDataReadme() => $"""
        # Data —— 功能数据目录（就在 exe 旁边）

        这个目录和 YuLiangTookit.exe 在同一个文件夹里，**每个功能一个同名小文件夹**：

            Data\
            ├─ flash-downloader\    Flash 下载器：下载的游戏（games\）、Flash 播放器（tools\）
            ├─ video-to-audio\      视频转音频（暂时没用到数据目录）
            └─ <功能Id>\            以后新功能自己的资源（swf、模板、缓存…）

        当前实际位置：{AppPaths.DataRoot}

        ## 注意

        * 这里的数据**不属于源码**（仓库里那份 Data 只有说明文件）。备份/迁移请整个程序目录一起拷走。
        * `dotnet clean`、手动删除 `bin` 或重新克隆仓库都会**丢掉这里的下载内容**，
          因为下载的游戏、播放器等都在这里。要长期保存请自己复制一份出去。
        * 程序怎么找这个目录：默认就是 exe 同级；设了环境变量 `YLT_HOME` 时以它为准；
          exe 目录不可写（例如装在 Program Files）才退到 `%AppData%\YuLiangToolkit`。
          状态栏最右边一直显示当前实际使用的目录。
        """;

    private static string BuildConfigReadme() => $"""
        # Config —— 功能配置目录（就在 exe 旁边）

        界面上的每个选项（Checkbox / Textbox / 下拉框 / 滑块…）改一下就会自动写进这里，
        **每个功能一个同名小文件夹**：

            Config\
            ├─ shell\config.json            主页：排序偏好 + 各功能上次使用时间
            ├─ video-to-audio\config.json   视频转音频的全部选项
            └─ <功能Id>\config.json         以后新功能自己的选项

        当前实际位置：{AppPaths.ConfigRoot}

        ## 注意

        * 文件是 camelCase、带缩进、中文不转义的 JSON，**可以直接用记事本改**，改完重开功能即生效。
        * 想恢复某个功能的默认选项：把它那个 `config.json` 删掉，下次打开会自动重建；
          也可以在「主页设置 → 重建配置」里勾选功能一键恢复默认（会同时重置内存里已加载的配置）。
        * 文件写坏了也不怕：会被自动改名备份成 `xxx.bad-时间戳.json` 并回退默认值。
          重建配置**不会删**这些损坏备份。
        * 这里的内容是**每台机器自己的状态**，不进版本库（仓库里那份 Config 只有说明文件）。
        * 缓存类的东西不放在这里（放 Cache 目录），见隔壁 `Cache\怎么用这个文件夹.md`。

        详细约定（配置类怎么写、有哪些轮子）见仓库根目录的 `AGENTS.md`（开发入口）与源码注释。
        """;

    private static string BuildCacheReadme() => $"""
        # Cache —— 缓存目录（就在 exe 旁边，**随时可以删**）

        这里放「丢了不心疼」的中间产物：缩略图、解压出来的临时文件、网络请求缓存之类。
        删了最多下次重新生成，**不会丢你的数据或设置**（用户数据在 `Data\`，设置在 `Config\`）。

            Cache\
            ├─ picture-viewer\     某个功能的缓存（按功能 Id 分文件夹）
            └─ <功能Id>\           以后新功能自己的缓存

        当前实际位置：{AppPaths.CacheRoot}

        ## 怎么清理

        * 简单粗暴：在资源管理器里直接删掉某个功能的小文件夹（或整个 Cache 文件夹），程序下次用到会重新建。
        * 方便一点：主页右上角「⚙ 主页设置 → 清理缓存」，可以逐项勾选 / 全选后确认清理；
          图片阅读器 / PDF 阅读器的「最近浏览」、聊天室的「最近会话」也会列在那里（它们存在 Config 里）。
        * 程序只删「缓存」：Data 里的下载内容、聊天记录等用户数据不受影响。

        ## 给以后写新功能的人

        缓存请写到 `Cache\<功能Id>\`（代码里用 `ToolCache.For(工具Id)` / `AppPaths.ToolCacheDir`），
        「清理缓存」会按功能 Id **自动认出来**，不用改设置页。
        如果缓存混在 Config 里（例如「最近浏览」这种列表），让工具类实现 `IToolCacheSource` 报告清理项即可。
        详细约定见仓库根目录的 `AGENTS.md`。
        """;
}
