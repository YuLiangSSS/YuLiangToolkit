using System.Diagnostics;
using System.IO;
using YuLiangToolkit.Tools.Launcher.Models;

namespace YuLiangToolkit.Tools.Launcher.Services;

/// <summary>
/// 一次启动请求：具体启动哪个文件、用什么起始位置。
/// <para>快捷方式（<c>.lnk</c>）的起始位置为空——参数 / 起始位置由 Windows Shell 从快捷方式里读，
/// 我们自己再设一个反而可能盖掉快捷方式里的设定。</para>
/// </summary>
/// <param name="FileName">要启动的完整路径（exe 或 lnk）。</param>
/// <param name="WorkingDirectory">起始位置（exe = 它自己所在目录；lnk 为空）。</param>
public readonly record struct LauncherStartRequest(string FileName, string WorkingDirectory);

/// <summary>
/// 「真正把进程拉起来」的最小接口。
/// <para>抽成接口是为了自检能注入假实现（<see cref="LauncherStartService.StartMany"/> 的纯逻辑断言），
/// 不让自检真的在本机开一堆程序。生产实现是 <see cref="SystemLauncherProcessStarter"/>。</para>
/// </summary>
public interface ILauncherProcessStarter
{
    /// <summary>启动一次；失败抛异常（由调用方折成失败项）。</summary>
    void Start(LauncherStartRequest request);
}

/// <summary>真实启动：交给 Shell（<c>UseShellExecute = true</c>），与在资源管理器里双击等价。</summary>
public sealed class SystemLauncherProcessStarter : ILauncherProcessStarter
{
    /// <summary>共用实例（无状态，随便用）。</summary>
    public static SystemLauncherProcessStarter Shared { get; } = new();

    public void Start(LauncherStartRequest request)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = true,
            WorkingDirectory = request.WorkingDirectory,
        });
    }
}

/// <summary>一次批量启动的结果。</summary>
/// <param name="Started">成功启动的数量。</param>
/// <param name="Failures">失败项的说明（<c>名字：原因</c>，可直接写日志）。</param>
public sealed record LauncherStartOutcome(int Started, IReadOnlyList<string> Failures)
{
    /// <summary>全部成功（没有失败项）。</summary>
    public bool AllStarted => Failures.Count == 0;
}

/// <summary>
/// 启动器「怎么启动一个程序」的唯一实现处：单个启动（双击 / 启动按钮）和
/// 联动启动（随工具集启动）共用这里，避免两套 Process.Start 参数慢慢走偏。
///
/// <para><b>联动启动的挑选规则</b>（<see cref="SelectStartupItems"/>，纯逻辑、自检覆盖）：
/// 只看配置里勾了「联动启动」的项；失效项（文件没了 / 快捷方式坏了 / 目标已卸载）跳过——
/// 开机时弹一个「找不到程序」比不启动更烦；同一路径只启动一次；保持列表顺序。</para>
/// </summary>
public static class LauncherStartService
{
    /// <summary>把启动项路径翻译成启动请求；路径为空 / 非法时返回 null。</summary>
    public static LauncherStartRequest? BuildRequest(string? path)
    {
        var normalized = LauncherRules.NormalizePath(path);
        if (normalized.Length == 0) return null;

        var workingDirectory = "";
        if (!LauncherRules.IsShortcutPath(normalized))
        {
            try { workingDirectory = Path.GetDirectoryName(normalized) ?? ""; }
            catch { workingDirectory = ""; }
        }

        return new LauncherStartRequest(normalized, workingDirectory);
    }

    /// <summary>
    /// 挑出「这次程序启动要联动启动」的项：勾了开关 + 当前健康 + 路径不重复，保持列表顺序。
    /// </summary>
    /// <param name="items">配置里的启动项（可以是 null / 含 null 元素）。</param>
    /// <param name="probe">健康状态探测（自检注入假 probe）。</param>
    public static IReadOnlyList<LauncherItemConfig> SelectStartupItems(
        IEnumerable<LauncherItemConfig>? items, ILauncherProbe probe)
    {
        var result = new List<LauncherItemConfig>();
        if (items is null || probe is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is null || !item.StartWithToolkit) continue;

            var path = LauncherRules.NormalizePath(item.Path);
            if (path.Length == 0) continue;
            if (!seen.Add(path)) continue;

            if (LauncherRules.CheckHealth(item, probe) != LauncherHealth.Ok) continue;
            result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// 逐个启动（失败按项隔离：一项失败不影响后面的项）。
    /// </summary>
    /// <param name="items">要启动的项（一般来自 <see cref="SelectStartupItems"/>）。</param>
    /// <param name="starter">真正的启动实现。</param>
    /// <param name="onItem">每项结束后的回调：<c>error</c> 为 null 表示成功，否则是失败原因。</param>
    public static LauncherStartOutcome StartMany(
        IEnumerable<LauncherItemConfig>? items,
        ILauncherProcessStarter starter,
        Action<LauncherItemConfig, string?>? onItem = null)
    {
        var failures = new List<string>();
        var started = 0;
        if (items is null || starter is null) return new LauncherStartOutcome(0, failures);

        foreach (var item in items)
        {
            if (item is null) continue;

            string? error = null;
            var request = BuildRequest(item.Path);
            if (request is null)
            {
                error = "路径为空";
            }
            else
            {
                try
                {
                    starter.Start(request.Value);
                    started++;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }

            if (error is not null)
            {
                var name = string.IsNullOrWhiteSpace(item.Name) ? item.Path : item.Name;
                failures.Add($"{name}：{error}");
            }

            try { onItem?.Invoke(item, error); } catch { /* 回调失败不影响后续启动 */ }
        }

        return new LauncherStartOutcome(started, failures);
    }
}
