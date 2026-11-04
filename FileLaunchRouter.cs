using System.Diagnostics;
using System.IO;
using System.Windows;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App;

/// <summary>
/// 「用本工具打开某个文件」的启动路由：解析命令行里的打开方式 + 文件路径，
/// 打开对应工具页，再把文件交给实现了 <see cref="IFileLaunchTarget"/> 的 ViewModel。
///
/// <para>启动路径与单实例转发都走这里（见 <c>App.xaml.cs</c>），所以「第一次启动」和
/// 「已经开着窗口时再双击一个文件」行为完全一致。历史 PDF / 图片双击不带宽模式参数，
/// 走 <see cref="OpenWithMode.Auto"/> 自动分流（<see cref="OpenWithCatalog"/>）。</para>
/// </summary>
internal static class FileLaunchRouter
{
    /// <summary>
    /// 命令行里如果带了「打开方式请求」就打开并返回 true；否则返回 false（正常启动主界面）。
    /// </summary>
    public static bool TryHandle(string[]? args, IReadOnlyList<ITool> tools, MainViewModel viewModel)
    {
        if (args is null || args.Length == 0) return false;

        if (!OpenWithArguments.TryParse(args, out var mode, out var filePath)
            || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        // 兜底：如果是从 text / archive 入口副本启动、但命令行没带 --open-as（被某些外壳改写 / 手动运行），
        // 按 exe 文件名判断模式。显式带了模式时以命令行为准。
        if (mode == OpenWithMode.Auto
            && OpenWithRegistration.InferModeFromExecutable(Environment.ProcessPath) is { } inferred)
        {
            mode = inferred;
        }

        return Launch(mode, filePath, tools, viewModel);
    }

    /// <summary>按模式 + 文件打开工具页并交出文件（界面路径与单实例转发共用）。</summary>
    public static bool Launch(OpenWithMode mode, string filePath, IReadOnlyList<ITool> tools, MainViewModel viewModel)
    {
        var fullPath = NormalizePath(filePath);

        var toolId = mode switch
        {
            OpenWithMode.Text => OpenWithCatalog.TextToolId,
            OpenWithMode.Archive => OpenWithCatalog.ArchiveToolId,
            _ => OpenWithCatalog.ResolveToolId(fullPath) ?? OpenWithCatalog.FallbackToolId,
        };

        var tool = tools.FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
        if (tool is null) return false;

        try
        {
            viewModel.OpenToolCommand.Execute(tool);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileLaunchRouter] 打开工具 {toolId} 失败：{ex}");
            return false;
        }

        // 视图可能是新建的，也可能是缓存复用的；两种都靠 ViewModel 上的契约接文件。
        if (viewModel.CurrentView is FrameworkElement view && view.DataContext is IFileLaunchTarget target)
        {
            try
            {
                target.OpenLaunchedFile(fullPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileLaunchRouter] 工具 {toolId} 打开文件失败：{ex}");
                return false;
            }
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return path.Trim().Trim('"'); }
    }
}
