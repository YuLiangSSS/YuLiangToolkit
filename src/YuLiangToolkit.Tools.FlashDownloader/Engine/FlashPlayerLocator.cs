namespace YuLiangToolkit.Tools.FlashDownloader.Engine;

/// <summary>
/// 找 Flash 独立播放器（<c>FlashPlayer.exe</c>）。
///
/// <para><b>为什么这么麻烦</b>：Flash 早就停止维护了，用户机器上不会自带独立播放器，
/// 这个 exe 是 11 MB 的第三方二进制、不适合塞进 git（见 <c>.gitignore</c> 里那条）。
/// 所以策略是「<b>发现哪里有一份就拷进功能自己的 tools 目录</b>」，找不到就明确告诉用户去下 Ruffle / 官方 projector，
/// 而不是默默失败。</para>
///
/// <para>查找顺序（先命中先用）：</para>
/// <list type="number">
///   <item>配置里手填的路径（<c>FlashDownloaderConfig.FlashPlayerPath</c>）</item>
///   <item>功能数据目录 <c>Data\flash-downloader\tools\</c>（第一次发现后就拷在这里）</item>
///   <item>程序根目录 / 程序目录（开发态就是仓库根，那里之前放了一份 <c>FlashPlayer.exe</c>）</item>
///   <item>从程序目录向上找仓库根（找 <c>YuLiangToolkit.sln</c>），看根目录有没有</item>
///   <item>常见安装位置（Flash Player projector 的老安装目录、Ruffle）</item>
/// </list>
/// </summary>
public static class FlashPlayerLocator
{
    /// <summary>支持的播放器文件名（第一个是官方独立播放器，第二个是 Ruffle 的独立 exe）。</summary>
    public static readonly string[] PreferredFileNames = { "FlashPlayer.exe", "flashplayer.exe", "ruffle.exe" };

    /// <summary>工具目录里自动搜索时认的“看起来像独立播放器”的名字前缀。</summary>
    private static readonly string[] NameHints = { "flashplayer", "ruffle", "flash_player", "flashplayer_sa" };

    /// <summary>一次查找的结果。</summary>
    /// <param name="Path">找到的播放器路径（没找到是空字符串）。</param>
    /// <param name="Source">从哪儿找到的（给用户看的说明）。</param>
    public readonly record struct Found(string Path, string Source)
    {
        /// <summary>找到了没有。</summary>
        public bool Ok => Path.Length > 0 && File.Exists(Path);
    }

    /// <summary>
    /// 找到一个可用的播放器（不拷贝）。<paramref name="toolsDirectory"/> 是功能自己的
    /// <c>Data\flash-downloader\tools</c>。
    /// </summary>
    public static Found Find(string? configuredPath = null, string? toolsDirectory = null)
    {
        // ① 用户手填
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = configuredPath.Trim().Trim('"');
            if (File.Exists(path)) return new Found(path, "配置里指定的路径");
        }

        // ② 功能 tools 目录（含“名字看起来像”的模糊匹配）
        if (!string.IsNullOrWhiteSpace(toolsDirectory) && Directory.Exists(toolsDirectory))
        {
            foreach (var name in PreferredFileNames)
            {
                var candidate = Path.Combine(toolsDirectory, name);
                if (File.Exists(candidate)) return new Found(candidate, "功能 tools 目录");
            }
            var fuzzy = FindByHint(toolsDirectory);
            if (fuzzy.Length > 0) return new Found(fuzzy, "功能 tools 目录");
        }

        // ③ 程序目录 / 程序根目录
        foreach (var baseDir in CandidateBaseDirectories())
        {
            foreach (var name in PreferredFileNames)
            {
                var candidate = Path.Combine(baseDir, name);
                if (File.Exists(candidate)) return new Found(candidate, baseDir);
            }
        }

        // ④ 常见安装位置
        foreach (var dir in WellKnownDirectories())
        {
            foreach (var name in PreferredFileNames)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return new Found(candidate, dir);
            }
        }

        return new Found("", "");
    }

    /// <summary>
    /// 找一份播放器并（需要的话）复制进 <paramref name="toolsDirectory"/>，
    /// 这样以后不用再满世界找。返回真正可用的路径。
    /// </summary>
    public static Found EnsureAvailable(string? configuredPath, string toolsDirectory)
    {
        var found = Find(configuredPath, toolsDirectory);
        if (!found.Ok) return found;

        try
        {
            Directory.CreateDirectory(toolsDirectory);
            var target = Path.Combine(toolsDirectory, Path.GetFileName(found.Path));
            var sourceFull = Path.GetFullPath(found.Path);
            if (!string.Equals(sourceFull, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                // 拷一份到自己家里；被占用 / 没权限就算了，直接用原地的
                if (!File.Exists(target))
                {
                    try { File.Copy(found.Path, target, overwrite: false); }
                    catch { return found; }
                }
                if (File.Exists(target)) return new Found(target, found.Source);
            }
        }
        catch { /* 拷不动就用原来的 */ }

        return found;
    }

    /// <summary>在目录里按名字前缀模糊找（用户可能把文件叫 <c>FlashPlayer_32_sa.exe</c> 之类）。</summary>
    private static string FindByHint(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file).ToLowerInvariant();
                // 浏览器插件的安装器不是独立播放器，别认
                if (name.StartsWith("flashutil", StringComparison.Ordinal)) continue;
                foreach (var hint in NameHints)
                {
                    if (name.StartsWith(hint, StringComparison.Ordinal)) return file;
                }
            }
        }
        catch { /* 忽略 */ }
        return "";
    }

    /// <summary>程序相关目录：程序目录 + 向上找到的仓库根。</summary>
    private static IEnumerable<string> CandidateBaseDirectories()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return baseDir;

        string? repoRoot = null;
        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "YuLiangToolkit.sln")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
            }
        }
        catch { /* 忽略 */ }

        // yield 不能写在 try/catch 里，所以先算出来再返回
        if (repoRoot is not null) yield return repoRoot;
    }

    /// <summary>
    /// 常见位置：<c>Program Files</c>、<c>%LocalAppData%</c>、桌面、下载、文档，以及它们下面一层
    /// 「听起来像放 Flash 播放器」的子文件夹。
    ///
    /// <para>覆盖这些是因为实测遇到过：仓库根那份 <c>FlashPlayer.exe</c> 被用户整理走了，
    /// 而他把「原工具 + 下载的游戏」搬到了 <c>桌面\Flash Games\</c>（里面 <c>tool\FlashPlayer.exe</c>
    /// 正好和原 Python 项目的目录约定一致）。用户自己整理过的目录也要能认出来，
    /// 否则每次都得手工「指定…」。</para>
    /// </summary>
    private static IEnumerable<string> WellKnownDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] folders =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            home is { Length: > 0 } ? Path.Combine(home, "Downloads") : "",
            home is { Length: > 0 } ? Path.Combine(home, "Documents") : "",
        };

        // 这些子文件夹名下面都可能直接躺着 FlashPlayer.exe；再下一层还有原工具那样的 tool\ 子目录
        string[] subFolders =
        {
            "", "tool", "Tools", "FlashPlayer", "Flash Player", "FlashGames", "Flash Games",
            "flash", "Ruffle", "bin",
        };

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            foreach (var sub in subFolders)
            {
                yield return sub.Length == 0 ? folder : Path.Combine(folder, sub);
            }
        }
    }
}
