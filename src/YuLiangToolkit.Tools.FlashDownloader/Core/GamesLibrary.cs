using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.FlashDownloader.Core;

/// <summary>一次文件操作的结果（成功 / 失败 + 给用户看的原因）。</summary>
/// <param name="Ok">成功没有。</param>
/// <param name="Message">失败原因或成功提示。</param>
public readonly record struct LibraryResult(bool Ok, string Message)
{
    public static LibraryResult Success(string message = "") => new(true, message);
    public static LibraryResult Fail(string message) => new(false, message);
}

/// <summary>
/// 游戏目录的“文件系统那一层”：扫描 / 删除 / 建目录 / 写 <c>SWFNetPath.ini</c> /
/// 「装入文件夹」与「改为 htm 打开」的目录改名。
///
/// <para>不依赖 WPF，可以直接在自检里造一个临时目录来回跑。对应原始 Python 项目的
/// <c>Application.refreshGameList / deleteGame / putIntoFolder / real_swfToHtm</c>。</para>
/// </summary>
public sealed partial class GamesLibrary
{
    /// <summary>正在往目录里写文件时用的临时后缀（避免列表扫描到半成品）。</summary>
    public const string TempSuffix = ".ylt-part";

    /// <summary>游戏目录（界面上可改；默认是 <c>Data\flash-downloader\games</c>）。</summary>
    public string Root { get; }

    public GamesLibrary(string root) => Root = root ?? "";

    /// <summary>游戏目录存在不。</summary>
    public bool Exists => Root.Length > 0 && Directory.Exists(Root);

    /// <summary>确保目录存在。</summary>
    public void EnsureCreated()
    {
        if (Root.Length == 0) return;
        try { Directory.CreateDirectory(Root); } catch { /* 只读环境忽略 */ }
    }

    /// <summary>
    /// 扫描游戏目录：文件夹 + 顶层的 <c>.swf</c> 文件，按名字排序。
    /// （与 Python 的 <c>os.listdir</c> 一样只看第一层；<c>.ylt-part</c> 半成品会被跳过。）
    /// </summary>
    public IReadOnlyList<GameEntry> List()
    {
        var list = new List<GameEntry>();
        if (!Exists) return list;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;
                list.Add(GameEntry.FromFolder(dir, name));
            }
        }
        catch { /* 目录不可读就只返回已经拿到的 */ }

        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*.swf"))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(GameEntry.FromSwfFile(file, name));
            }
        }
        catch { /* 同上 */ }

        return list.OrderBy(e => e.FileOrFolderName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>按“文件 / 文件夹名”找一个条目（找不到返回 null）。</summary>
    public GameEntry? Find(string fileOrFolderName)
        => List().FirstOrDefault(e => string.Equals(e.FileOrFolderName, fileOrFolderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>条目对应的完整路径。</summary>
    public string FullPathOf(GameEntry entry) => Path.Combine(Root, entry.FileOrFolderName);

    /// <summary>删除一个游戏（文件直接删，文件夹连同里面全部内容一起删）。</summary>
    public LibraryResult Delete(GameEntry entry)
    {
        var full = FullPathOf(entry);
        if (!File.Exists(full) && !Directory.Exists(full)) return LibraryResult.Fail($"找不到 {full}");
        try
        {
            if (entry.IsFolder) Directory.Delete(full, recursive: true);
            else File.Delete(full);
            return LibraryResult.Success($"已删除 {entry.DisplayText}");
        }
        catch (Exception ex)
        {
            return LibraryResult.Fail($"删除失败：{ex.Message}");
        }
    }

    /// <summary>把游戏目录里的旧命名（<c>xxx_a123</c>，没有 s / h）列出来，供界面提示用户改名。</summary>
    public IReadOnlyList<string> FindLegacyFolderNames()
        => List().Where(e => e.IsFolder && e.IsLegacyFolderName).Select(e => e.FileOrFolderName).ToList();

    // ---------------------------------------------------------------- 装入文件夹

    /// <summary>
    /// 把散放的 <c>游戏名_a123.swf</c> 收进 <c>游戏名_a123s/</c> 文件夹，并写好 ini。
    /// 对应 Python 的 <c>Application.putIntoFolder</c>。
    /// </summary>
    /// <param name="entry">要收起来的 swf 条目（必须是文件）。</param>
    /// <param name="meta">重新解析出来的游戏信息（拿不到就传 null，ini 里写空路径）。</param>
    public LibraryResult MoveIntoFolder(GameEntry entry, GameMeta? meta)
    {
        if (entry.IsFolder) return LibraryResult.Fail("只能对散放的 swf 文件操作");
        if (entry.GameName.Length == 0) return LibraryResult.Fail("文件名太短，识别不出游戏名（应为 游戏名_a123.swf）");

        var sourceFull = FullPathOf(entry);
        if (!File.Exists(sourceFull)) return LibraryResult.Fail($"找不到 {sourceFull}");

        var desiredFolder = Path.Combine(Root, entry.GameName + "s");
        var folder = BatchDownloadPlanner.MakeUniqueDirectory(desiredFolder);
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, entry.GameName + ".swf");
            MoveFile(sourceFull, target);

            var ini = new SwfNetPathIni
            {
                FilePath = Path.Combine(folder, "SWFNetPath.ini"),
                FolderExt = ".swf",
                Kind = GameFolderKind.Swf,
                NetPath = meta?.ContentUrl ?? "",
                Width = meta?.Width ?? SwfNetPathIni.UnknownSize,
                Height = meta?.Height ?? SwfNetPathIni.UnknownSize,
                IsNewPage = meta is null
                    ? SwfNetPathIni.NewPageUnknown
                    : (meta.IsNewPage ? SwfNetPathIni.NewPageTrue : SwfNetPathIni.NewPageFalse),
                NewPageUrl = meta?.IsNewPage == true ? meta.NewPageUrl : "",
            };
            ini.Save();
            return LibraryResult.Success($"已装入文件夹 {Path.GetFileName(folder)}");
        }
        catch (Exception ex)
        {
            return LibraryResult.Fail($"装入文件夹失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 改为 htm 打开

    /// <summary>「改为 htm 打开」需要的东西（由调用方去网上补齐，或者从已有 ini 里读出来）。</summary>
    /// <param name="NewPageUrl">网页游戏时要下载的主页面网址。</param>
    /// <param name="InnerSwfName">swf 在网页里应该叫什么（新页面模式取 URL 里的文件名）。</param>
    /// <param name="Width">宽度。</param>
    /// <param name="Height">高度。</param>
    /// <param name="FromNewPage">true = 从新页面加载；false = 无新页面，直接用本地 FlashPage 模板。</param>
    public sealed record HtmConversion(
        string NewPageUrl, string InnerSwfName, int Width, int Height, bool FromNewPage);

    /// <summary>
    /// 把 <c>游戏名_a123s/</c> 改成 <c>游戏名_a123h/</c> 并重排里面文件所需的两步准备：
    /// 先改目录名与 swf 名。网页正文由调用方写（可能要联网下载）。
    /// 对应 Python 的 <c>Application.real_swfToHtm</c> 前半段。
    /// </summary>
    public LibraryResult RenameFolderToHtm(GameEntry entry, HtmConversion conversion)
    {
        if (!entry.IsFolder) return LibraryResult.Fail("只能对文件夹操作（先点「装入文件夹」）");
        if (entry.FolderExt != ".swf")
            return LibraryResult.Fail($"只能对 swf 文件夹操作，而它的扩展名为 {entry.FolderExt}");

        var oldFolderFull = FullPathOf(entry);
        if (!Directory.Exists(oldFolderFull)) return LibraryResult.Fail($"找不到文件夹 {oldFolderFull}");

        var desiredFolder = Path.Combine(Root, entry.GameName + "h");
        var newFolderFull = BatchDownloadPlanner.MakeUniqueDirectory(desiredFolder);

        try
        {
            Directory.Move(oldFolderFull, newFolderFull);

            var oldSwf = Path.Combine(newFolderFull, entry.GameName + ".swf");
            var newSwfName = conversion.InnerSwfName.Length > 0 ? conversion.InnerSwfName : "mainSwfGame.swf";
            var newSwf = Path.Combine(newFolderFull, newSwfName);
            if (File.Exists(oldSwf) && !string.Equals(oldSwf, newSwf, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    MoveFile(oldSwf, newSwf);
                }
                catch
                {
                    // 更名失败就复制一份（可能是被 FlashPlayer 占着），旧文件留给用户自己删
                    File.Copy(oldSwf, newSwf, overwrite: true);
                }
            }

            // swf 的「被谁加载」元信息换成 htm 版
            var ini = new SwfNetPathIni
            {
                FilePath = Path.Combine(newFolderFull, "SWFNetPath.ini"),
                FolderExt = ".htm",
                Kind = GameFolderKind.Htm,
                NetPath = conversion.NewPageUrl,
                Width = conversion.Width,
                Height = conversion.Height,
            };
            ini.Save();
            return LibraryResult.Success(Path.GetFileName(newFolderFull));
        }
        catch (Exception ex)
        {
            return LibraryResult.Fail($"改名失败：{ex.Message}");
        }
    }

    /// <summary>把 htm 正文写进文件夹（<c>游戏名_a123h/游戏名_a123.htm</c>）。</summary>
    public LibraryResult WriteMainPage(string folderName, string gameName, byte[] content)
    {
        try
        {
            var folder = Path.Combine(Root, folderName);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, gameName + ".htm"), content);
            return LibraryResult.Success();
        }
        catch (Exception ex)
        {
            return LibraryResult.Fail($"写入网页失败：{ex.Message}");
        }
    }

    /// <summary>磁盘上有没有这个文件（相对游戏目录）。</summary>
    public bool FileExists(string relativePath)
        => Root.Length > 0 && File.Exists(Path.Combine(Root, relativePath));

    // ---------------------------------------------------------------- 命名规则（纯函数）

    /// <summary>
    /// 生成不撞车的游戏文件名：<c>游戏名_a123</c> → 撞了就 <c>游戏名_1_a123</c>、<c>游戏名_2_a123</c>…
    /// 对应 Python 里那段 <c>while True: gameName = f"{clean}_{counter}_{appendix}"</c>。
    /// </summary>
    public static string BuildUniqueGameName(string cleanName, string appendix, Func<string, bool> exists)
    {
        var name = cleanName.Length > 0 ? cleanName : "未命名";
        for (var counter = 0; counter < 10000; counter++)
        {
            var candidate = counter == 0 ? $"{name}_{appendix}" : $"{name}_{counter}_{appendix}";
            if (!exists(candidate)) return candidate;
        }
        return $"{name}_{Guid.NewGuid():N}_{appendix}";
    }

    /// <summary>文件夹名结尾的 <c>s</c> 表示“用 Flash 播放器打开”的标记。</summary>
    public static string FolderNameForSwf(string gameName) => gameName + "s";

    /// <summary>文件夹名结尾的 <c>h</c> 表示“用网页打开”的标记。</summary>
    public static string FolderNameForHtm(string gameName) => gameName + "h";

    /// <summary>
    /// 把相对路径解析成游戏目录下的绝对路径，并阻止越界（含 <c>..</c> 或绝对路径返回 false）。
    /// </summary>
    public bool TryResolve(string relativePath, out string fullPath)
    {
        fullPath = "";
        if (Root.Length == 0 || string.IsNullOrWhiteSpace(relativePath)) return false;
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (normalized.Length == 0 || Path.IsPathRooted(normalized)) return false;
        try
        {
            var full = Path.GetFullPath(Path.Combine(Root, normalized));
            var prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = full;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 移动文件，绕开 Windows「只改大小写 / 目标暂时被占」的坑：先改临时名，再改成目标名。
    /// </summary>
    private static void MoveFile(string source, string target)
    {
        var dir = Path.GetDirectoryName(target) ?? ".";
        var temp = Path.Combine(dir, Path.GetFileName(target) + TempSuffix);
        if (File.Exists(temp)) File.Delete(temp);
        File.Move(source, temp);
        try
        {
            File.Move(temp, target);
        }
        catch
        {
            // 目标已存在（并发改名）就退回去
            if (!File.Exists(target)) File.Move(temp, source);
            throw;
        }
    }
}
