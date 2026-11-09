namespace YuLiangToolkit.Tools.AppManager.Models;

/// <summary>
/// 「应用管理器」的纯逻辑规则：哪些注册表项算「应用」、安装日期怎么解析、大小怎么显示、
/// 应用装在哪个盘、卸载命令行怎么拆成「exe + 参数」、以及排序比较。
///
/// <para><b>全部是纯函数</b>（不碰注册表、不碰文件、不碰界面），自检可以直接断言；
/// 真正的注册表读取在 <c>Services/RegistryAppCatalog</c>。</para>
/// </summary>
public static class AppCatalogRules
{
    /// <summary>驱动器筛选里「所有驱动器」的键。</summary>
    public const string AllDrives = "";

    /// <summary>驱动器筛选里「未记录安装位置」的键（老软件常不写 <c>InstallLocation</c>）。</summary>
    public const string UnknownDrive = "?";

    /// <summary>排序依据。</summary>
    public enum SortKey
    {
        /// <summary>按名称（默认）。</summary>
        Name,

        /// <summary>按安装日期（没记日期的排最后）。</summary>
        InstallDate,

        /// <summary>按占用大小（没记大小的排最后）。</summary>
        Size,

        /// <summary>按发布者。</summary>
        Publisher,
    }

    // ---------------------------------------------------------------- 过滤 / 去重

    /// <summary>
    /// 一条注册表卸载项该不该出现在列表里。规则对齐 Windows「安装的应用」：
    /// 没名字、标了「系统组件」、是别的产品的一部分（<c>ParentKeyName</c>）、
    /// 是补丁 / 更新 / 服务包（<c>ReleaseType</c>）的都不算用户装的应用。
    /// </summary>
    /// <param name="name"><c>DisplayName</c>。</param>
    /// <param name="systemComponent"><c>SystemComponent</c> = 1 表示系统组件。</param>
    /// <param name="parentKeyName"><c>ParentKeyName</c> 非空表示属于另一个产品的子项。</param>
    /// <param name="releaseType"><c>ReleaseType</c>，例如 <c>Security Update</c> / <c>Update Rollup</c>。</param>
    /// <param name="uninstallCommand">卸载命令（用来兜底排除光秃秃的 KB 补丁项）。</param>
    public static bool ShouldInclude(
        string? name,
        bool systemComponent,
        string? parentKeyName,
        string? releaseType,
        string? uninstallCommand)
    {
        var displayName = (name ?? "").Trim();
        if (displayName.Length == 0) return false;
        if (systemComponent) return false;
        if (!string.IsNullOrWhiteSpace(parentKeyName)) return false;
        if (IsPatchReleaseType(releaseType)) return false;

        // 没卸载命令、名字又像 KB 补丁的，基本是系统更新的残留项。
        if (string.IsNullOrWhiteSpace(uninstallCommand) && LooksLikePatchName(displayName)) return false;

        return true;
    }

    /// <summary><c>ReleaseType</c> 是否是「补丁 / 更新」类（这些不是用户主动装的应用）。</summary>
    public static bool IsPatchReleaseType(string? releaseType)
    {
        if (string.IsNullOrWhiteSpace(releaseType)) return false;
        var value = releaseType.Trim();
        return value.Contains("Update", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
               || value.Contains("ServicePack", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Security", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>名字看起来像 KB 补丁（<c>KB5031354</c> 这种）。</summary>
    public static bool LooksLikePatchName(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.Length < 3) return false;
        if (!value.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) return false;
        for (var i = 2; i < value.Length; i++)
            if (!char.IsDigit(value[i])) return false;
        return true;
    }

    /// <summary>
    /// 同名去重：同一个应用在 HKLM / HKLM-WOW64 / HKCU 里可能各有一条（32 / 64 位安装包最常见），
    /// 按 <c>HKLM(64) → HKCU → HKLM(WOW64)</c> 的优先级留一条，顺带把完全重名的都收掉。
    /// 返回保持输入顺序（每组取第一次出现的位置）。
    /// </summary>
    public static IReadOnlyList<InstalledApp> DedupeByName(IEnumerable<InstalledApp> apps)
    {
        var best = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var app in apps)
        {
            var key = app.Name.Trim();
            if (key.Length == 0) continue;

            if (!best.TryGetValue(key, out var existing))
            {
                best[key] = app;
                order.Add(key);
                continue;
            }

            if (ScopePriority(app.RegistryScope) < ScopePriority(existing.RegistryScope)) best[key] = app;
        }

        return order.Select(key => best[key]).ToList();
    }

    /// <summary>注册表来源优先级（越小越优先）：HKLM(64) 1 → HKCU 2 → HKLM(WOW64) 3 → 其它 4。</summary>
    public static int ScopePriority(string? scope) => (scope ?? "").Trim().ToUpperInvariant() switch
    {
        "HKLM" => 1,
        "HKCU" => 2,
        "HKLM-WOW64" => 3,
        _ => 4,
    };

    // ---------------------------------------------------------------- 字段解析 / 格式化

    /// <summary>
    /// 解析安装日期：注册表里常见 <c>20260626</c>，少数工具写 <c>2026-06-26</c> / <c>2026/06/26</c>。
    /// 空串 / 位数不对 / 月日越界一律返回 <c>null</c>（界面上显示「未知」，排序时排最后）。
    /// </summary>
    public static DateOnly? ParseInstallDate(string? raw)
    {
        var value = CleanText(raw);
        if (value.Length == 0) return null;

        if (value.Length == 8 && value.All(char.IsDigit))
        {
            var year = int.Parse(value[..4]);
            var month = int.Parse(value[4..6]);
            var day = int.Parse(value[6..8]);
            return TryMakeDate(year, month, day);
        }

        // 分隔符写法：只认「数字 + 分隔符 + 数字 + 分隔符 + 数字」的严格形式。
        var parts = value.Split('-', '/', '.');
        if (parts.Length == 3
            && parts[0].Length == 4 && parts[0].All(char.IsDigit)
            && parts[1].Length <= 2 && parts[1].Length > 0 && parts[1].All(char.IsDigit)
            && parts[2].Length <= 2 && parts[2].Length > 0 && parts[2].All(char.IsDigit))
        {
            return TryMakeDate(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        }

        return null;
    }

    /// <summary>
    /// 清理注册表字符串里常见的「填充物」：首尾空白 + 结尾的 <c>\0</c>。
    /// 实测有些安装程序（例如 osu!lazer）把值写成定长缓冲，<c>DisplayName</c> 后面跟着一长串 NUL，
    /// 直接 <c>Trim()</c> 去不掉，界面上会显示成「osu!(lazer)        」。
    /// </summary>
    public static string CleanText(string? raw)
        => (raw ?? "").Trim().TrimEnd('\0').Trim();

    /// <summary>年月日合法就造一个 <see cref="DateOnly"/>，越界返回 null（不抛异常）。</summary>
    private static DateOnly? TryMakeDate(int year, int month, int day)
    {
        if (year < 1900 || year > 9999) return null;
        if (month < 1 || month > 12) return null;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }

    /// <summary>界面上的安装日期文本（<c>2026/06/26</c>）；没记日期时返回「未知」。</summary>
    public static string FormatInstallDate(DateOnly? date)
        => date is null ? "未知" : $"{date.Value.Year:D4}/{date.Value.Month:D2}/{date.Value.Day:D2}";

    /// <summary>
    /// 占用大小（注册表 <c>EstimatedSize</c> 单位是 KB）→ <c>1.2 GB</c> / <c>482 MB</c> / <c>870 KB</c>。
    /// 没记大小返回「未知」。
    /// </summary>
    public static string FormatSize(long sizeInKilobytes)
    {
        if (sizeInKilobytes <= 0) return "未知";

        const double kb = 1;
        const double mb = 1024 * kb;
        const double gb = 1024 * mb;

        var size = (double)sizeInKilobytes;
        if (size >= gb) return (size / gb).ToString("0.##") + " GB";
        if (size >= mb) return (size / mb).ToString("0.#") + " MB";
        return Math.Max(1, size).ToString("0") + " KB";
    }

    /// <summary>
    /// 从安装目录 / 图标路径 / 卸载命令里推断应用装在哪个盘：<c>C:\Program Files\7-Zip\</c> → <c>C</c>。
    /// 拿不到盘符返回空串（界面显示「未记录位置」）。
    /// </summary>
    public static string DriveOf(string? path)
    {
        var value = (path ?? "").Trim().Trim('"');
        if (value.Length < 2) return "";
        if (!char.IsLetter(value[0]) || value[1] != ':') return "";
        return char.ToUpperInvariant(value[0]).ToString();
    }

    /// <summary>
    /// 按「安装目录 → 图标路径 → 卸载命令」的顺序推断盘符（前一个拿不到才看后一个）。
    /// </summary>
    public static string ResolveDrive(string? installLocation, string? iconSource, string? uninstallCommand)
    {
        var drive = DriveOf(installLocation);
        if (drive.Length > 0) return drive;

        drive = DriveOf(StripIconIndex(iconSource));
        if (drive.Length > 0) return drive;

        return DriveOf(SplitCommandLine(uninstallCommand).Executable);
    }

    /// <summary>
    /// <c>C:\App\app.exe,0</c> → <c>C:\App\app.exe</c>（<c>DisplayIcon</c> 末尾的图标序号）。
    /// 先切序号、再去引号：<c>"C:\App\app.exe",12</c> 也能得到干净路径。
    /// </summary>
    public static string StripIconIndex(string? iconSource)
    {
        var value = (iconSource ?? "").Trim();
        if (value.Length == 0) return "";

        var comma = value.LastIndexOf(',');
        if (comma > 0)
        {
            var tail = value[(comma + 1)..].Trim();
            if (tail.Length > 0 && tail.All(char.IsDigit)) value = value[..comma].Trim();
        }

        return value.Trim('"').Trim();
    }

    /// <summary>驱动器下拉框的显示文本：有卷标 → <c>Acer (C:)</c>，没卷标 → <c>本地磁盘 (C:)</c>。</summary>
    public static string DriveDisplayName(string? driveLetter, string? volumeLabel)
    {
        var letter = (driveLetter ?? "").Trim().TrimEnd(':').ToUpperInvariant();
        if (letter.Length == 0) return "未记录位置";

        var label = (volumeLabel ?? "").Trim();
        return (label.Length == 0 ? "本地磁盘" : label) + " (" + letter + ":)";
    }

    // ---------------------------------------------------------------- 命令行拆分

    /// <summary>卸载命令行拆出来的结果：可执行文件 + 参数（前者一定非空，除非原串是空的）。</summary>
    public readonly record struct CommandLine(string Executable, string Arguments)
    {
        /// <summary>有没有真的拆出东西。</summary>
        public bool HasExecutable => Executable.Length > 0;
    }

    /// <summary>
    /// 把注册表里的 <c>UninstallString</c> / <c>ModifyPath</c> 拆成「可执行文件」和「参数」，
    /// 交给 <c>ProcessStartInfo.FileName / Arguments</c> 分别使用。
    ///
    /// <para>要处理的三种真实写法：</para>
    /// <list type="number">
    ///   <item><c>"C:\Program Files\draw.io\Uninstall draw.io.exe" /allusers</c>（带引号，最常见）；</item>
    ///   <item><c>C:\Program Files\7-Zip\Uninstall.exe</c>（不带引号但路径含空格）；</item>
    ///   <item><c>msiexec /I{GUID}</c>（不带路径、也没有 .exe 后缀的 MSI 入口）。</item>
    /// </list>
    /// </summary>
    public static CommandLine SplitCommandLine(string? commandLine)
    {
        var text = (commandLine ?? "").Trim();
        if (text.Length == 0) return new CommandLine("", "");

        // ① 带引号：引号里就是可执行文件（路径里能安稳放空格）。
        if (text[0] == '"')
        {
            var closing = text.IndexOf('"', 1);
            if (closing > 1)
            {
                var quoted = text[1..closing].Trim();
                var rest = text[(closing + 1)..].Trim();
                return new CommandLine(quoted, rest);
            }

            // 只有开头一个引号（写坏了）：去掉引号按无引号处理。
            text = text[1..].Trim();
        }

        // ② 无引号：以「.exe / .com / .bat / .cmd」结尾的第一个词边界当可执行文件，
        //    这样 C:\Program Files\7-Zip\Uninstall.exe /S 不会被空格切断。
        var boundary = FindExecutableBoundary(text);
        if (boundary > 0)
        {
            var executable = text[..boundary].Trim();
            var rest = text[boundary..].Trim();
            return new CommandLine(executable, rest);
        }

        // ③ 都没有（例如 msiexec /X{GUID}）：退回「第一个空格前是可执行文件」。
        var space = text.IndexOf(' ');
        if (space <= 0) return new CommandLine(text, "");
        return new CommandLine(text[..space].Trim(), text[(space + 1)..].Trim());
    }

    /// <summary>找出「可执行文件」在无引号命令行里结束的位置（0 = 没找到）。</summary>
    private static int FindExecutableBoundary(string text)
    {
        ReadOnlySpan<string> extensions = [".exe", ".com", ".bat", ".cmd"];

        foreach (var extension in extensions)
        {
            var searchFrom = 0;
            while (true)
            {
                var index = text.IndexOf(extension, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;

                var end = index + extension.Length;
                // 必须是「词边界」结尾：整串结束，或者后面就是空格。
                if (end == text.Length || text[end] == ' ' || text[end] == '\t') return end;
                searchFrom = end;
            }
        }

        return 0;
    }

    // ---------------------------------------------------------------- 排序

    /// <summary>
    /// 「未知值排最后」的固定顺序：有值的在前，没值的在后（与升逆序无关 ——
    /// 不然逆序时「未知」会跑到最上面，看着像坏了）。
    /// </summary>
    private static int CompareUnknown(InstalledApp left, InstalledApp right, SortKey key)
    {
        return key switch
        {
            SortKey.InstallDate => (left.InstallDate is null ? 1 : 0) - (right.InstallDate is null ? 1 : 0),
            SortKey.Size => (left.HasSize ? 0 : 1) - (right.HasSize ? 0 : 1),
            _ => 0,
        };
    }

    private static int CompareNullableDate(DateOnly? left, DateOnly? right)
        => (left?.DayNumber ?? 0).CompareTo(right?.DayNumber ?? 0);

    private static int CompareText(string left, string right)
        => string.Compare(left ?? "", right ?? "", StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// 按排序依据 + 方向给整批应用排序（纯函数，返回新列表）。
    /// 逆序时「未知值」仍然沉底（见 <see cref="Compare"/>），再按名称倒序。
    /// </summary>
    public static List<InstalledApp> Sort(IEnumerable<InstalledApp> apps, SortKey key, bool descending)
    {
        var list = apps.ToList();
        list.Sort((left, right) =>
        {
            var unknownOrder = CompareUnknown(left, right, key);
            if (unknownOrder != 0) return unknownOrder < 0 ? -1 : 1;

            // 两边都有值（或都不需要值）时才真正比较，并按方向取反。
            var ordered = CompareByValue(left, right, key);
            if (ordered != 0) return descending ? -ordered : ordered;

            // 同值兜底按名称（方向一致），保证顺序稳定、不会每次刷新都乱跳。
            var byName = CompareText(left.Name, right.Name);
            if (byName != 0) return descending ? -byName : byName;
            return CompareText(left.Publisher, right.Publisher);
        });
        return list;
    }

    private static int CompareByValue(InstalledApp left, InstalledApp right, SortKey key) => key switch
    {
        SortKey.InstallDate => CompareNullableDate(left.InstallDate, right.InstallDate),
        SortKey.Size => left.SizeInKilobytes.CompareTo(right.SizeInKilobytes),
        SortKey.Publisher => CompareText(left.Publisher, right.Publisher),
        _ => CompareText(left.Name, right.Name),
    };

    /// <summary>把配置里存的字符串解析成 <see cref="SortKey"/>（不认识的值回退到按名称）。</summary>
    public static SortKey ParseSortKey(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "installdate" => SortKey.InstallDate,
        "size" => SortKey.Size,
        "publisher" => SortKey.Publisher,
        _ => SortKey.Name,
    };

    /// <summary><see cref="SortKey"/> → 存进配置的稳定字符串。</summary>
    public static string SortKeyToConfig(SortKey key) => key switch
    {
        SortKey.InstallDate => "installdate",
        SortKey.Size => "size",
        SortKey.Publisher => "publisher",
        _ => "name",
    };

    /// <summary>按关键字过滤（名称 / 发布者 / 版本，忽略大小写；空关键字 = 全过）。</summary>
    public static bool MatchesKeyword(InstalledApp app, string? keyword)
    {
        var value = (keyword ?? "").Trim();
        if (value.Length == 0) return true;

        return app.Name.Contains(value, StringComparison.CurrentCultureIgnoreCase)
               || app.Publisher.Contains(value, StringComparison.CurrentCultureIgnoreCase)
               || app.Version.Contains(value, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>这一行是否属于选中的驱动器（<see cref="AllDrives"/> = 全过）。</summary>
    public static bool MatchesDrive(InstalledApp app, string? drive)
    {
        var selected = (drive ?? "").Trim();
        if (selected.Length == 0) return true;
        if (selected == UnknownDrive) return app.DriveLetter.Length == 0;
        return string.Equals(app.DriveLetter, selected, StringComparison.OrdinalIgnoreCase);
    }
}
