using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 「双击 PDF 直接用本工具打开」要登记的启动请求：主程序启动时把命令行里的 PDF 路径放进来，
/// PDF 阅读器的 ViewModel 创建时取走（只生效一次）。
/// </summary>
public static class PdfFilePathLaunch
{
    /// <summary>待打开的 PDF 绝对路径（主程序解析命令行后写入；ViewModel 读取后清空）。</summary>
    public static string? PendingPdfPath { get; set; }
}

/// <summary>一次 PDF 文件关联操作的结果（成功与否 + 给人看的中文说明）。</summary>
public sealed record PdfAssociationResult(bool Success, bool Registered, string Message);

/// <summary>
/// 「PDF 阅读器」的 .pdf 文件关联（**只写 HKCU，不需要管理员**）：登记一个 ProgId + .pdf 后缀的
/// 「打开方式」，让双击 PDF / 右键「打开方式」能选到「PDF 阅读器」，并把 PDF 路径作为命令行参数交给主程序。
///
/// <para>实现照抄「图片连续阅读器」的 <c>PictureFileAssociation</c>（见同仓库 PictureViewer 项目），
/// 只把「8 种图片后缀」换成单个 <c>.pdf</c>，行为保持一致。</para>
///
/// <para><b>Windows 10/11 的坑</b>：每个后缀真正用哪个程序由
/// <c>Explorer\FileExts\.pdf\UserChoice</c> 决定，而这个键被系统保护（带哈希），程序**不能**可靠地直接改。
/// 所以这里做两件事：① 登记 ProgId 与 OpenWithProgids（「打开方式」里出现本工具、没有 UserChoice 的后缀直接生效）；
/// ② 界面提示用户去「默认应用」确认一次。硬抢 UserChoice 属于未公开行为，不做。</para>
///
/// <para>自检只测纯逻辑（后缀表 / 命令行拼装 / 路径判断 / 状态说明）；真正写注册表在用户的机器上做，
/// 测试调 <see cref="Register"/> / <see cref="Unregister"/> 会污染系统，所以自检里绝不调用。</para>
/// </summary>
public static class PdfFileAssociation
{
    /// <summary>本工具唯一支持的文件后缀（.pdf）。</summary>
    private const string PdfExtension = ".pdf";

    /// <summary>关联登记用的 ProgId（HKCU\Software\Classes\&lt;ProgId&gt;）。</summary>
    public const string ProgId = "YuLiangToolkit.PdfReader";

    /// <summary>显示给 Windows 的名字。</summary>
    public const string DisplayName = "PDF 阅读器";

    /// <summary>本工具支持的 PDF 后缀（只有 <c>.pdf</c>；保留列表形式，判断逻辑与图片关联完全一致）。</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = new[] { PdfExtension };

    /// <summary>当前 exe 的完整路径（关联命令里要用它；发布后换目录只需重新勾一下）。</summary>
    public static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path)) return path;

            try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>Windows「打开方式」里显示的程序名（用 exe 文件名，多个版本不会打架）。</summary>
    public static string ApplicationKeyName
    {
        get
        {
            var name = Path.GetFileName(ExecutablePath);
            return string.IsNullOrWhiteSpace(name) ? "YuLiangTookit.exe" : name;
        }
    }

    /// <summary>是否登记过（ProgId 的打开命令还在，且非空）。</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var prog = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return prog?.GetValue("") is string command && command.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>登记 .pdf 关联到本工具（可重复调用，幂等）。</summary>
    public static PdfAssociationResult Register()
    {
        var exe = ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe))
            return new(false, false, "找不到本程序的可执行文件路径，无法登记文件关联。");

        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

            using (var prog = classes.CreateSubKey(ProgId))
            {
                prog.SetValue("", DisplayName);
                prog.SetValue("FriendlyTypeName", DisplayName);
                using var icon = prog.CreateSubKey("DefaultIcon");
                icon.SetValue("", $"\"{exe}\",0");
                using var command = prog.CreateSubKey(@"shell\open\command");
                command.SetValue("", BuildOpenCommand(exe));
            }

            // 「打开方式」列表里的应用条目（老式列表 / 需要 SupportedTypes 才会列出来）
            using (var app = classes.CreateSubKey($@"Applications\{ApplicationKeyName}"))
            {
                app.SetValue("FriendlyAppName", "凉雨工具集");
                using var icon = app.CreateSubKey("DefaultIcon");
                icon.SetValue("", $"\"{exe}\",0");
                using var command = app.CreateSubKey(@"shell\open\command");
                command.SetValue("", BuildOpenCommand(exe));
                using var supported = app.CreateSubKey("SupportedTypes");
                foreach (var extension in SupportedExtensions) supported.SetValue(extension, "");
            }

            foreach (var extension in SupportedExtensions)
            {
                using (var ext = classes.CreateSubKey(extension))
                {
                    // 记下原来的默认程序（取消关联时还原），然后把默认指向本工具。
                    // 已经是本工具就不要再备份，否则第二次登记会把备份覆盖成自己。
                    // 注意：Windows 10/11 若已有 UserChoice，系统仍会优先用它 —— 见类注释，界面会提示去确认。
                    var current = ext.GetValue("") as string;
                    if (!string.IsNullOrWhiteSpace(current)
                        && !string.Equals(current, ProgId, StringComparison.OrdinalIgnoreCase))
                    {
                        using var prog = classes.CreateSubKey(ProgId);
                        using var previous = prog.CreateSubKey("PreviousDefaults");
                        previous.SetValue(extension, current);
                    }

                    ext.SetValue("", ProgId);

                    using var openWith = ext.CreateSubKey("OpenWithProgids");
                    openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                }

                using var fileExt = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\OpenWithProgids");
                fileExt.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            NotifyShell();
            return new(true, true, DescribeState());
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            return new(false, IsRegistered(), $"登记文件关联失败：{ex.Message}（只写当前用户注册表，正常不需要管理员）");
        }
    }

    /// <summary>取消登记（只删自己写的东西；不碰别的程序的默认值，有备份就还原原来的默认程序）。</summary>
    public static PdfAssociationResult Unregister()
    {
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

            foreach (var extension in SupportedExtensions)
            {
                // 先取出「关联前原来的默认程序」，取消关联时还原（没有备份就删掉我们的默认值）
                string? previousDefault;
                using (var prog = classes.OpenSubKey(ProgId))
                using (var previous = prog?.OpenSubKey("PreviousDefaults"))
                {
                    previousDefault = previous?.GetValue(extension) as string;
                }

                using (var ext = classes.OpenSubKey(extension, writable: true))
                {
                    if (ext is not null)
                    {
                        if (ext.GetValue("") is string current
                            && string.Equals(current, ProgId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(previousDefault)) ext.SetValue("", previousDefault);
                            else ext.DeleteValue("", throwOnMissingValue: false);
                        }

                        using var openWith = ext.OpenSubKey("OpenWithProgids", writable: true);
                        openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
                    }
                }

                using var fileExt = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\OpenWithProgids", writable: true);
                fileExt?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree($@"Applications\{ApplicationKeyName}", throwOnMissingSubKey: false);

            NotifyShell();
            return new(true, false, "已取消 PDF 文件关联（双击 PDF 恢复系统原来的阅读器）。");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            return new(false, IsRegistered(), $"取消文件关联失败：{ex.Message}");
        }
    }

    /// <summary>关联状态的说明文字（设置面板里显示；没登记就提示怎么开）。</summary>
    public static string DescribeState()
    {
        if (!IsRegistered()) return "未关联：双击 PDF 仍用系统默认阅读器。";

        return IsSystemDefaultOurs()
            ? "已登记 .pdf 文件关联：双击 PDF 即可用本工具打开。"
            : "已登记 .pdf 文件关联，但系统的默认应用里 .pdf 还是别的程序 —— 双击没打开本工具时，点下面按钮在「默认应用」里选一次即可。";
    }

    /// <summary>打开 Windows「默认应用」设置页（UserChoice 只能由用户自己确认）。</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了（设置面板里还有文字说明）
        }
    }

    /// <summary>命令行里这个路径是不是一个受支持的 PDF（主程序判断要不要自动进 PDF 阅读器）。</summary>
    public static bool IsSupportedPdfFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (!File.Exists(path)) return false;
            return IsSupportedExtension(Path.GetExtension(path));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>后缀（大小写、前后空格都容忍）是否在支持列表里。</summary>
    public static bool IsSupportedExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;
        var trimmed = extension.Trim();
        return SupportedExtensions.Any(e => string.Equals(e, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>拼「用本程序打开这个 PDF」的命令行：<c>"exe" "%1"</c>（路径带空格也不会坏）。</summary>
    public static string BuildOpenCommand(string executablePath)
        => $"\"{executablePath.Trim().Trim('"')}\" \"%1\"";

    /// <summary>
    /// 系统「默认应用」里 .pdf 现在是不是已经指向本工具：
    /// 有 UserChoice 就看它；没有 UserChoice 时系统会用 <c>HKCU\Software\Classes\.pdf</c> 的默认值（也就是我们登记的值）。
    /// </summary>
    private static bool IsSystemDefaultOurs()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{PdfExtension}\UserChoice");
            if (choice?.GetValue("ProgId") is string choiceProgId && choiceProgId.Length > 0)
                return string.Equals(choiceProgId, ProgId, StringComparison.OrdinalIgnoreCase);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>通知资源管理器刷新图标 / 打开方式缓存（不刷新的话要等一会儿才生效）。</summary>
    private static void NotifyShell()
    {
        try { SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero); }
        catch { /* shell32 一直都在，失败也无所谓 */ }
    }

    private const int ShcneAssocChanged = 0x08000000;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
