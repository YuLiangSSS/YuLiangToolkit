namespace YuLiangToolkit.Tools.Launcher.Models;

/// <summary>
/// 一次快捷方式解析结果（由 <c>IShellLink</c> 读出）。
///
/// <para><see cref="Resolved"/> 为 false 表示这个 <c>.lnk</c> 读不出来（文件损坏、权限不足等）；
/// 注意「目标程序不存在」不是未解析：那种情况 <see cref="Resolved"/> 是 true，
/// 由 <see cref="LauncherRules.CheckHealth(LauncherItemConfig, ILauncherProbe)"/> 再判断目标是否存在。</para>
/// </summary>
public readonly record struct ShortcutTarget(
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string IconPath,
    int IconIndex,
    bool Resolved)
{
    /// <summary>读不出目标时的占位值。</summary>
    public static ShortcutTarget Unresolved { get; } = new("", "", "", "", 0, false);
}

/// <summary>
/// 启动器探测文件 / 快捷方式用的最小接口。
///
/// <para>独立成接口的原因：真实实现要调 Windows Shell（<c>IShellLink</c>）与文件系统，
/// 自检里不能依赖「某台机器上刚好装着某个软件」，所以测试注入一个假探测（见 <c>LauncherChecks</c>）。
/// 生产实现是 <c>Services/SystemLauncherProbe.cs</c>。</para>
/// </summary>
public interface ILauncherProbe
{
    /// <summary>路径对应的文件或文件夹是否存在（快捷方式也可以指向文件夹）。</summary>
    bool Exists(string path);

    /// <summary>解析 <c>.lnk</c> 快捷方式的目标 / 参数 / 起始位置 / 图标位置。</summary>
    ShortcutTarget ResolveShortcut(string shortcutPath);
}
