namespace YuLiangToolkit.Tools.SoftwareUpdater.Winget;

/// <summary>
/// winget 命令行拼装与包 Id 安全校验（纯函数，自检覆盖）。
///
/// <para><b>安全约定</b>：所有参数都用 <c>ProcessStartInfo.ArgumentList</c> 逐项传，绝不拼成一个字符串；
/// 传给 <c>--id</c> 的值还会过 <see cref="IsSafePackageId"/> 白名单，防止表格里被污染的 ID
/// （例如以 <c>--</c> 开头）被 winget 当成另一个选项。</para>
/// </summary>
public static class WingetCommand
{
    /// <summary>扫描全部已安装应用（表格里同时带「可用」列）。</summary>
    public const string AllApps = "全部应用";

    /// <summary>只列有可用更新的应用（等价于 <c>winget upgrade</c> 不带参数）。</summary>
    public const string UpgradableOnly = "仅可更新";

    public static IReadOnlyList<string> ScanModes => new[] { AllApps, UpgradableOnly };

    /// <summary>配置里存了非法值时退回「全部应用」。</summary>
    public static string NormalizeScanMode(string? mode)
        => mode == UpgradableOnly ? UpgradableOnly : AllApps;

    /// <summary>
    /// 扫描命令：
    /// <list type="bullet">
    ///   <item>全部应用 → <c>winget list --disable-interactivity --nowarn</c></item>
    ///   <item>仅可更新 → <c>winget upgrade --disable-interactivity --nowarn [--include-unknown]</c></item>
    /// </list>
    /// <c>--disable-interactivity</c> 保证重定向时不会被交互提示卡住；<c>--nowarn</c> 去掉警告行。
    /// </summary>
    public static IReadOnlyList<string> BuildScanArguments(string? mode, bool includeUnknown)
    {
        var onlyUpgradable = NormalizeScanMode(mode) == UpgradableOnly;
        var args = new List<string>
        {
            onlyUpgradable ? "upgrade" : "list",
            "--disable-interactivity",
            "--nowarn",
        };
        // list 命令的 --include-unknown 只能与 --upgrade-available 一起用，所以只给 upgrade 模式加。
        if (onlyUpgradable && includeUnknown) args.Add("--include-unknown");
        return args;
    }

    /// <summary>
    /// 升级一个应用：<c>winget upgrade --id &lt;id&gt; --exact --disable-interactivity
    /// --accept-package-agreements --accept-source-agreements [--silent] [--include-unknown]</c>。
    /// 不加 <c>--all</c>（一次只处理一个，方便逐个显示进度 / 失败后继续）。
    /// </summary>
    public static IReadOnlyList<string> BuildUpgradeArguments(string? packageId, bool silent, bool includeUnknown)
    {
        if (!IsSafePackageId(packageId))
            throw new ArgumentException("包 Id 不合法，已拒绝拼装 winget 升级命令", nameof(packageId));

        var args = new List<string>
        {
            "upgrade",
            "--id", packageId!,
            "--exact",
            "--disable-interactivity",
            "--accept-package-agreements",
            "--accept-source-agreements",
        };
        if (silent) args.Add("--silent");
        if (includeUnknown) args.Add("--include-unknown");
        return args;
    }

    /// <summary>
    /// 包 Id 白名单：winget 官方 Id 只由 ASCII 字母数字与 <c>. _ - +</c> 组成。
    /// 注册表来源的 <c>ARP\Machine\…</c>、MSIX 的内部 Id、被截断的 Id 都在这里被挡下（它们本来也无法升级）。
    /// 首字符不能是 <c>-</c>，否则会被 winget 当成新选项。
    /// </summary>
    public static bool IsSafePackageId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return false;
        if (id[0] is '-' or '+' or '.') return false;
        foreach (var ch in id)
        {
            if (char.IsAsciiLetterOrDigit(ch)) continue;
            if (ch is '.' or '_' or '-' or '+') continue;
            return false;
        }
        return true;
    }

    /// <summary>这一行能不能升级：有可用版本 + Id 没被截断 + Id 安全。</summary>
    public static bool CanUpgrade(WingetPackage package)
        => package is not null
           && package.HasUpdate
           && !package.IsIdTruncated
           && IsSafePackageId(package.Id);
}
