namespace YuLiangToolkit.Tools.ContextMenu;

/// <summary>
/// 右键菜单条目的类型。
/// </summary>
public enum ContextMenuEntryKind
{
    /// <summary>普通菜单命令：注册表 <c>...\shell\&lt;动词名&gt;</c>（例如「上传到百度网盘」）。</summary>
    Verb,

    /// <summary>COM 外壳扩展：<c>...\shellex\ContextMenuHandlers\&lt;名字&gt;</c>，默认值是一个 CLSID。</summary>
    ShellExtension,
}

/// <summary>
/// 条目当前「隐藏」状态是谁造成的。用来区分「本工具隐藏的（可以恢复）」和
/// 「其他程序 / 系统隐藏的（不该被本工具悄悄改掉）」。
/// </summary>
public enum ContextMenuHideSource
{
    /// <summary>没有隐藏，正常显示。</summary>
    None,

    /// <summary>本工具隐藏的：值数据里带着 <see cref="ContextMenuRegistryLayout.Marker"/>。</summary>
    ThisTool,

    /// <summary>其他程序 / 系统隐藏的（HKCU 里别的程序写的值，或 HKLM 电脑级值）。</summary>
    OtherProgram,
}

/// <summary>
/// 注册表根。真实实现映射到 <c>Registry.CurrentUser</c> / <c>Registry.LocalMachine</c>；
/// 自检用的内存假实现也按这两个根存数据，这样断言逻辑和真实实现一致。
/// </summary>
public enum RegistryRoot
{
    CurrentUser,
    LocalMachine,
}

/// <summary>
/// 一条注册表路径：根 + 相对路径（例如 <c>Software\Classes\*\shell\BaiduNetdisk</c>）。
/// </summary>
public readonly record struct RegistryPath(RegistryRoot Root, string Path)
{
    /// <summary>给界面 / 日志用的绝对路径文本。</summary>
    public override string ToString()
        => (Root == RegistryRoot.LocalMachine ? "HKLM\\" : "HKCU\\") + Path;
}

/// <summary>注册表里的一个值；<see cref="Name"/> 为空串表示默认值。</summary>
public sealed record RegistryValue(string Name, string? Value);

/// <summary>
/// 注册表读写抽象。
/// <para>真实实现：<see cref="RegistryContextMenuStore"/>（Microsoft.Win32.Registry）。</para>
/// <para>自检实现：<c>ContextMenuChecks</c> 里的内存假注册表 —— 自检<b>绝不</b>写用户真实的右键菜单。</para>
/// </summary>
public interface IContextMenuRegistry
{
    /// <summary>键（或它的父键）是否存在。</summary>
    bool KeyExists(RegistryPath path);

    /// <summary>直接子键名（不含路径）。</summary>
    IReadOnlyList<string> GetSubKeyNames(RegistryPath path);

    /// <summary>读一个值；不存在返回 null。默认值的名字传空串。</summary>
    string? GetValue(RegistryPath path, string valueName);

    /// <summary>读这个键下的全部值（不含子键）。</summary>
    IReadOnlyList<RegistryValue> GetValues(RegistryPath path);

    /// <summary>写一个值（父键不存在时自动创建）。值类型统一按字符串（REG_SZ）处理。</summary>
    void SetValue(RegistryPath path, string valueName, string value);

    /// <summary>删除一个值；键 / 值不存在时静默忽略。</summary>
    void DeleteValue(RegistryPath path, string valueName);

    /// <summary>删除一个键（含子键）；不存在时静默忽略。</summary>
    void DeleteKey(RegistryPath path);
}

/// <summary>
/// 一个右键菜单入口位置（作用于哪类对象）。
/// <para><see cref="VerbRelativePath"/> 与 <see cref="HandlerRelativePath"/> 都是相对
/// <c>HKCU\Software\Classes\</c> / <c>HKLM\Software\Classes\</c> 的路径；null = 这类位置没有这种条目。</para>
/// </summary>
public sealed record ContextMenuScope(
    string Key,
    string Label,
    string? VerbRelativePath,
    string? HandlerRelativePath);

/// <summary>
/// 本工具扫描的注册表位置清单（顺序就是界面筛选下拉框的顺序）。
/// <para>覆盖：所有文件、所有文件系统对象、文件夹、文件夹空白处、驱动器、Folder、桌面背景。
/// 这几处是第三方软件（网盘 / 压缩 / 杀毒 / 右键增强）最常挂菜单的地方；
/// Win11「显示更多选项」里的老菜单同样由这些键决定。</para>
/// </summary>
public static class ContextMenuScopes
{
    /// <summary>全部扫描位置。</summary>
    public static IReadOnlyList<ContextMenuScope> All { get; } = new[]
    {
        new ContextMenuScope("files", "所有文件", @"*\shell", @"*\shellex\ContextMenuHandlers"),
        new ContextMenuScope("all-objects", "所有文件系统对象", null, @"AllFilesystemObjects\shellex\ContextMenuHandlers"),
        new ContextMenuScope("directory", "文件夹", @"Directory\shell", @"Directory\shellex\ContextMenuHandlers"),
        new ContextMenuScope("directory-background", "文件夹空白处", @"Directory\Background\shell", @"Directory\Background\shellex\ContextMenuHandlers"),
        new ContextMenuScope("drive", "驱动器", @"Drive\shell", @"Drive\shellex\ContextMenuHandlers"),
        new ContextMenuScope("folder", "文件夹（Folder）", @"Folder\shell", @"Folder\shellex\ContextMenuHandlers"),
        new ContextMenuScope("desktop", "桌面背景", @"DesktopBackground\Shell", null),
    };
}

/// <summary>
/// 注册表布局常量、关键项名单和 CLSID 规范化（纯逻辑，自检直接断言）。
/// </summary>
public static class ContextMenuRegistryLayout
{
    /// <summary>
    /// 本工具写进注册表值的「标记」：值数据等于它 = 这一项是本工具隐藏的。
    /// 这样「恢复全部」只恢复自己写的，不会把别的软件的隐藏设置一起清掉。
    /// </summary>
    public const string Marker = "YuLiangToolkit.ContextMenuManager";

    /// <summary>隐藏普通菜单动词用的值名（空 REG_SZ：菜单里不显示，但仍可被程序调用）。</summary>
    public const string ProgrammaticAccessOnly = "ProgrammaticAccessOnly";

    /// <summary>HKCU / HKLM 下的 Classes 前缀。</summary>
    public const string ClassesPath = @"Software\Classes";

    /// <summary>隐藏 COM 外壳扩展用的「黑名单」键（值名 = CLSID，值数据随便写）。</summary>
    public const string BlockedListPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    /// <summary>「新建」菜单的 COM 扩展 CLSID：隐藏它会把整个「新建」子菜单干掉，必须排除。</summary>
    public const string NewMenuClsid = "{D969A300-E7FF-11d0-A93B-00A0C90F2719}";

    /// <summary>不允许隐藏的 COM 扩展 CLSID（系统关键菜单）。</summary>
    public static IReadOnlySet<string> CriticalClsids { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NewMenuClsid };

    /// <summary>不允许隐藏的动词名：删掉主操作（打开）会让用户以为文件坏了。</summary>
    public static IReadOnlySet<string> CriticalVerbNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "open" };

    /// <summary>
    /// 是不是一个能用的 CLSID：形如 <c>{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}</c>。
    /// <para>空串 / 资源字符串（<c>@dll,-123</c>）/ 乱七八糟的名字一律返回 false。</para>
    /// </summary>
    public static bool IsValidClsid(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0 || text.StartsWith('@')) return false;
        return Guid.TryParse(text.Trim('{', '}'), out _);
    }

    /// <summary>
    /// 把一个 CLSID 规范成 <c>{大写 GUID}</c>；不是合法 CLSID 时返回空串。
    /// <para>注册表里同一个 CLSID 大小写 / 花括号写法可能不同，去重和写黑名单前必须规范化。</para>
    /// </summary>
    public static string CanonicalClsid(string? raw)
    {
        if (!IsValidClsid(raw)) return "";
        var guid = Guid.Parse((raw ?? "").Trim().Trim('{', '}'));
        return "{" + guid.ToString("D").ToUpperInvariant() + "}";
    }

    /// <summary>这个动词名是不是系统关键项（不允许隐藏）。</summary>
    public static bool IsCriticalVerbName(string? verbName)
        => !string.IsNullOrWhiteSpace(verbName) && CriticalVerbNames.Contains(verbName.Trim());
}

/// <summary>
/// 一个右键菜单条目（枚举结果）。
/// <para><see cref="Id"/> 稳定且唯一：动词 = <c>verb:&lt;相对路径&gt;</c>；
/// COM 扩展 = <c>handler:&lt;规范化 CLSID&gt;</c>（同一个 CLSID 在多个位置出现会合并成一行）。</para>
/// </summary>
public sealed record ContextMenuEntry(
    string Id,
    ContextMenuEntryKind Kind,
    string Name,
    string ScopeLabel,
    string RegistryLocation,
    string? Clsid,
    string Detail,
    bool IsHidden,
    ContextMenuHideSource HideSource,
    bool CanToggle,
    string StatusNote)
{
    /// <summary>界面上显示的类型文本。</summary>
    public string KindLabel => Kind == ContextMenuEntryKind.Verb ? "菜单命令" : "扩展模块";

    /// <summary>切换按钮的文案。</summary>
    public string ToggleActionLabel => IsHidden ? "恢复显示" : "隐藏";

    /// <summary>当前状态文本。</summary>
    public string StatusLabel => IsHidden ? "已隐藏" : "显示中";

    /// <summary>如果是隐藏状态，说明是谁隐藏的（空串 = 没隐藏）。</summary>
    public string SourceLabel => HideSource switch
    {
        ContextMenuHideSource.ThisTool => "本工具隐藏",
        ContextMenuHideSource.OtherProgram => "其他程序隐藏",
        _ => "",
    };

    /// <summary>是不是本工具隐藏的（「恢复全部」只处理这种）。</summary>
    public bool IsHiddenByThisTool => IsHidden && HideSource == ContextMenuHideSource.ThisTool;
}

/// <summary>一次隐藏 / 恢复操作的结果（失败不抛异常，界面直接显示 <see cref="Message"/>）。</summary>
public sealed record ContextMenuOperationResult(bool Success, string Message)
{
    public static ContextMenuOperationResult Ok(string message) => new(true, message);
    public static ContextMenuOperationResult Fail(string message) => new(false, message);
}

/// <summary>「恢复全部（仅本工具隐藏的）」的结果。</summary>
public sealed record ContextMenuRestoreResult(int RestoredCount, IReadOnlyList<string> Failures)
{
    /// <summary>全部恢复成功（没有失败项）。</summary>
    public bool Success => Failures.Count == 0;
}

/// <summary>
/// 条目筛选（纯函数，界面和自检共用）。
/// </summary>
public static class ContextMenuFilter
{
    /// <summary>
    /// 按关键词 / 位置 / 类型 / 只看已隐藏筛选。
    /// <para>关键词忽略大小写，命中名称、位置名、类型、注册表路径、细节任意一项即可；
    /// <paramref name="scopeLabel"/> 为 null / 空 / "全部" 表示不限；
    /// <paramref name="kind"/> 为 null 表示不限。</para>
    /// </summary>
    public static IReadOnlyList<ContextMenuEntry> Apply(
        IEnumerable<ContextMenuEntry>? entries,
        string? keyword,
        string? scopeLabel,
        ContextMenuEntryKind? kind,
        bool onlyHidden)
    {
        if (entries is null) return Array.Empty<ContextMenuEntry>();

        var trimmedKeyword = (keyword ?? "").Trim();
        var scope = (scopeLabel ?? "").Trim();
        var scopeFiltered = scope.Length > 0 && !string.Equals(scope, "全部", StringComparison.Ordinal);

        var result = new List<ContextMenuEntry>();
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            if (kind is not null && entry.Kind != kind.Value) continue;
            if (scopeFiltered && !string.Equals(entry.ScopeLabel, scope, StringComparison.OrdinalIgnoreCase)) continue;
            if (onlyHidden && !entry.IsHidden) continue;

            if (trimmedKeyword.Length > 0
                && !Contains(entry.Name, trimmedKeyword)
                && !Contains(entry.ScopeLabel, trimmedKeyword)
                && !Contains(entry.KindLabel, trimmedKeyword)
                && !Contains(entry.RegistryLocation, trimmedKeyword)
                && !Contains(entry.Detail, trimmedKeyword)
                && !Contains(entry.StatusLabel, trimmedKeyword))
            {
                continue;
            }

            result.Add(entry);
        }
        return result;
    }

    private static bool Contains(string? text, string keyword)
        => !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}
