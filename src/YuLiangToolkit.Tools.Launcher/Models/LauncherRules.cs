using System.IO;
using System.Text;

namespace YuLiangToolkit.Tools.Launcher.Models;

/// <summary>启动项当前能不能用（每次打开页面 / 点「刷新状态」时重新探测）。</summary>
public enum LauncherHealth
{
    /// <summary>正常，可以启动。</summary>
    Ok,

    /// <summary>导入的那个 exe / .lnk 文件本身不在了（被删除 / 移动 / 改名）。</summary>
    MissingFile,

    /// <summary>.lnk 读不出目标（文件损坏、权限不足等）。</summary>
    BrokenShortcut,

    /// <summary>.lnk 能读出来，但它指向的程序已经不在了（软件被卸载 / 删除）。</summary>
    MissingTarget,

    /// <summary>不支持的文件类型（正常导入不会出现，只有手改配置才会）。</summary>
    Unsupported,
}

/// <summary>
/// 启动器的纯规则（不碰界面、不碰真实文件系统）：
/// 导入校验 / 默认显示名 / 路径去重 / 图标缓存路径 / 失效判断 / 清空无效项。
///
/// <para><b>为什么单独抽出来</b>：这些判断决定「哪些算无效、清空会不会误删」，必须能被自检覆盖；
/// 文件存在与快捷方式解析通过 <see cref="ILauncherProbe"/> 注入，自检里用假数据跑，
/// 不依赖本机装了什么软件。界面与真实 Shell 调用在其它文件里。</para>
/// </summary>
public static class LauncherRules
{
    /// <summary>图标下面显示的名字最长多少个字符（再长就截断；完整名字在右侧详情里看）。</summary>
    public const int MaxDisplayNameLength = 60;

    /// <summary>支持导入的扩展名（小写，含点）。</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = new[] { ".exe", ".lnk" };

    /// <summary>自定义图标相对 <c>Data\launcher\</c> 的子目录。</summary>
    public const string IconFolder = "icons";

    // ---------------------------------------------------------------- 路径 / 名字

    /// <summary>是不是启动器支持的导入文件（<c>.exe</c> / <c>.lnk</c>，忽略大小写）。</summary>
    public static bool IsSupportedPath(string? path)
    {
        var extension = ExtensionOf(path);
        foreach (var supported in SupportedExtensions)
        {
            if (string.Equals(extension, supported, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>是不是 <c>.lnk</c> 快捷方式（忽略大小写）。</summary>
    public static bool IsShortcutPath(string? path)
        => string.Equals(ExtensionOf(path), ".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>扩展名（含点，原样大小写；路径为空 / 非法时返回空字符串）。</summary>
    public static string ExtensionOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetExtension(path.Trim()) ?? ""; }
        catch { return ""; }
    }

    /// <summary>文件名（不含扩展名；路径非法时返回空字符串）。</summary>
    public static string FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFileNameWithoutExtension(path.Trim()) ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// 把用户拖进来 / 选进来的路径规范化：去掉首尾空白与成对引号，能转绝对路径就转。
    /// 转不了（非法字符等）时退回清理后的原文本，不抛异常。
    /// </summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.Trim().Trim('"').Trim();
        if (trimmed.Length == 0) return "";
        try { return Path.GetFullPath(trimmed); }
        catch { return trimmed; }
    }

    /// <summary>判断两个路径是不是同一个文件（规范化后忽略大小写；任一侧为空 = 不是）。</summary>
    public static bool SamePath(string? left, string? right)
    {
        var a = NormalizePath(left);
        var b = NormalizePath(right);
        if (a.Length == 0 || b.Length == 0) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 默认显示名：<b>.lnk 用快捷方式自己的文件名</b>（用户建快捷方式时起的名字最直观）；
    /// <b>.exe 优先用版本信息里的「文件说明」</b>（例如 WeChat.exe → 微信），没有才退回文件名。
    /// </summary>
    /// <param name="fileDescription"><c>FileVersionInfo.FileDescription</c>；可空。</param>
    public static string DefaultDisplayName(string? path, string? fileDescription = null)
    {
        if (IsShortcutPath(path)) return SanitizeDisplayName(FileNameOf(path), "未命名");

        var description = CleanText(fileDescription);
        if (description.Length > 0) return SanitizeDisplayName(description, FileNameOf(path));

        return SanitizeDisplayName(FileNameOf(path), "未命名");
    }

    /// <summary>
    /// 清理用户输入的显示名：去首尾空白、去掉控制字符、超长截断；空的话用 <paramref name="fallback"/>，
    /// 还空就返回「未命名」。重命名与默认命名都走这里，保证配置里永远不会出现纯空白名字。
    /// </summary>
    public static string SanitizeDisplayName(string? name, string? fallback = null)
    {
        var clean = CleanText(name);
        if (clean.Length > MaxDisplayNameLength)
        {
            clean = clean[..MaxDisplayNameLength].TrimEnd();
        }
        if (clean.Length > 0) return clean;

        var cleanFallback = CleanText(fallback);
        if (cleanFallback.Length > MaxDisplayNameLength)
        {
            cleanFallback = cleanFallback[..MaxDisplayNameLength].TrimEnd();
        }
        return cleanFallback.Length > 0 ? cleanFallback : "未命名";
    }

    // ---------------------------------------------------------------- 图标缓存

    /// <summary>某个启动项自定义图标在 <c>Data\launcher\</c> 下的相对路径（总是 <c>icons\&lt;安全 Id&gt;.png</c>）。</summary>
    public static string IconRelativePath(string? itemId)
    {
        var safe = new StringBuilder();
        foreach (var ch in (itemId ?? "").Trim())
        {
            safe.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        var name = safe.ToString().Trim('-');
        if (name.Length == 0) name = "item";
        return Path.Combine(IconFolder, name + ".png");
    }

    /// <summary>
    /// 配置里记录的自定义图标相对路径是否安全：不能是绝对路径、不能有 <c>..</c> 段、不能为空。
    /// （<see cref="Core.Storage.ToolStorage"/> 本身也会拒绝逃逸路径，这里提前判断，坏配置直接当没设置。）
    /// </summary>
    public static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var text = relativePath.Trim();
        try
        {
            if (Path.IsPathRooted(text)) return false;
        }
        catch { return false; }

        foreach (var segment in text.Replace('/', '\\').Split('\\'))
        {
            if (segment.Length == 0) continue;
            if (segment == "..") return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- 健康状态

    /// <summary>检查一条启动项现在能不能用（只读探测，不改配置）。</summary>
    public static LauncherHealth CheckHealth(LauncherItemConfig item, ILauncherProbe probe)
        => CheckHealth(item?.Path, probe);

    /// <summary>检查一个导入路径现在能不能用（只读探测，不改配置）。</summary>
    public static LauncherHealth CheckHealth(string? path, ILauncherProbe probe)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0) return LauncherHealth.MissingFile;
        if (!IsSupportedPath(normalized)) return LauncherHealth.Unsupported;

        // ① 导入的那个文件本身还在不在
        if (!probe.Exists(normalized)) return LauncherHealth.MissingFile;

        // ② 快捷方式还要能读出目标，并且目标还在（目标可以是文件夹）
        if (IsShortcutPath(normalized))
        {
            var target = probe.ResolveShortcut(normalized);
            if (!target.Resolved || string.IsNullOrWhiteSpace(target.TargetPath)) return LauncherHealth.BrokenShortcut;

            var targetPath = NormalizePath(target.TargetPath);
            if (targetPath.Length == 0 || !probe.Exists(targetPath)) return LauncherHealth.MissingTarget;
        }

        return LauncherHealth.Ok;
    }

    /// <summary>把健康状态翻译成给用户看的一句话（右侧详情 / 状态栏用）。</summary>
    public static string DescribeHealth(LauncherHealth health) => health switch
    {
        LauncherHealth.Ok => "正常",
        LauncherHealth.MissingFile => "文件不存在（可能已删除 / 移动）",
        LauncherHealth.BrokenShortcut => "快捷方式已损坏（读不出目标）",
        LauncherHealth.MissingTarget => "目标程序已卸载 / 删除",
        _ => "不支持的文件类型",
    };

    /// <summary>挑出全部失效项（保持原列表顺序）——「清空无效项」删的就是这些。</summary>
    public static IReadOnlyList<LauncherItemConfig> FindInvalid(
        IEnumerable<LauncherItemConfig>? items, ILauncherProbe probe)
    {
        var result = new List<LauncherItemConfig>();
        if (items is null) return result;
        foreach (var item in items)
        {
            if (item is null) continue;
            if (CheckHealth(item, probe) != LauncherHealth.Ok) result.Add(item);
        }
        return result;
    }

    /// <summary>列表里是否已经导入过同一个路径（导入去重用，忽略大小写 / 相对路径差异）。</summary>
    public static bool ContainsPath(IEnumerable<LauncherItemConfig>? items, string? path)
    {
        if (items is null) return false;
        foreach (var item in items)
        {
            if (item is not null && SamePath(item.Path, path)) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 内部工具

    /// <summary>去首尾空白 + 去掉控制字符（含换行 / Tab），不截断长度。</summary>
    private static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsControl(ch)) builder.Append(ch);
        }
        return builder.ToString().Trim();
    }
}
