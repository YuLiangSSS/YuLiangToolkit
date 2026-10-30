using System.IO;
using YuLiangToolkit.Tools.PdfReader;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「PDF 文件关联」的纯逻辑自检（被测代码见 <see cref="PdfFileAssociation"/>）。
///
/// <para><b>硬性约束</b>：这里<b>绝不调用</b> <see cref="PdfFileAssociation.Register"/> /
/// <see cref="PdfFileAssociation.Unregister"/> —— 它们会往当前用户的 HKCU 注册表里写真正的文件关联，
/// 跑一次自检就在用户机器上留垃圾（自检必须可重复、无副作用）。也只读不写的状态函数
/// （<see cref="PdfFileAssociation.IsRegistered"/> / <see cref="PdfFileAssociation.DescribeState"/>）可以测，
/// 它们只调用 <c>Registry.CurrentUser.OpenSubKey</c>。</para>
///
/// <para>文件系统部分只在自检临时目录（<see cref="SelfCheckScratch"/>）里造文件，不碰仓库 Data/Config；
/// 断言的是「有没有这个 .pdf 文件」的纯判断，不解析 PDF 内容。
/// 真正的注册表登记 / 取消留给用户手动点界面，Lead 最后统一验证。</para>
/// </summary>
internal static class PdfAssociationChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> checkPlain)
    {
        void Check(string name, bool ok, string detail = "") => check("[PDF关联] " + name, ok, detail);

        // ================================================================
        // 1. 常量与后缀表（ProgId / DisplayName / 只认 .pdf）
        // ================================================================
        Check("常量：ProgId = YuLiangToolkit.PdfReader（不含空白，能直接当注册表键名）",
            PdfFileAssociation.ProgId == "YuLiangToolkit.PdfReader"
            && !PdfFileAssociation.ProgId.Any(char.IsWhiteSpace),
            PdfFileAssociation.ProgId);

        Check("常量：DisplayName = PDF 阅读器（中文界面名，写进注册表显示用）",
            PdfFileAssociation.DisplayName == "PDF 阅读器",
            PdfFileAssociation.DisplayName);

        Check("后缀表只含 .pdf（1 项、带点、小写、无重复）",
            PdfFileAssociation.SupportedExtensions is { Count: 1 }
            && PdfFileAssociation.SupportedExtensions[0] == ".pdf"
            && PdfFileAssociation.SupportedExtensions.All(e => e.StartsWith('.') && e == e.ToLowerInvariant())
            && PdfFileAssociation.SupportedExtensions.Distinct().Count() == 1,
            string.Join(",", PdfFileAssociation.SupportedExtensions));

        // ================================================================
        // 2. IsSupportedExtension：大小写 / 前后空白容错，不认识的后缀一律拒绝
        // ================================================================
        Check("后缀判断：.pdf / .PDF / ' .Pdf ' 都算支持（大小写与空白容错）",
            PdfFileAssociation.IsSupportedExtension(".pdf")
            && PdfFileAssociation.IsSupportedExtension(".PDF")
            && PdfFileAssociation.IsSupportedExtension(" .Pdf "),
            ".pdf / .PDF /  .Pdf ");

        Check("后缀判断：不认识的后缀、缺点的 pdf、空串、纯空白、null 都拒绝",
            !PdfFileAssociation.IsSupportedExtension(".docx")
            && !PdfFileAssociation.IsSupportedExtension(".pdfx")
            && !PdfFileAssociation.IsSupportedExtension("pdf")
            && !PdfFileAssociation.IsSupportedExtension("")
            && !PdfFileAssociation.IsSupportedExtension("   ")
            && !PdfFileAssociation.IsSupportedExtension(null));

        // ================================================================
        // 3. IsSupportedPdfFile：真实文件系统判断（只在临时目录造文件）
        // ================================================================
        var folder = SelfCheckScratch.MakeDirectory("pdf-assoc");
        var pdfPath = Path.Combine(folder, "示例 文档.pdf");     // 文件名带空格，验证不是靠字符串前缀猜的
        var upperPdfPath = Path.Combine(folder, "大写后缀.PDF");
        var textPath = Path.Combine(folder, "说明.txt");
        File.WriteAllText(pdfPath, "%PDF-1.4");                 // 内容不重要：只判断存在 + 后缀
        File.WriteAllText(upperPdfPath, "%PDF-1.4");
        File.WriteAllText(textPath, "不是 PDF");

        try
        {
            Check("启动参数判断：真实存在的 .pdf 才算（文件名带空格也行）",
                PdfFileAssociation.IsSupportedPdfFile(pdfPath),
                pdfPath);

            Check("启动参数判断：大写 .PDF 后缀也算（双击来的路径大小写不固定）",
                PdfFileAssociation.IsSupportedPdfFile(upperPdfPath),
                upperPdfPath);

            Check("启动参数判断：不存在的 .pdf 不算（路径错了不能进阅读器）",
                !PdfFileAssociation.IsSupportedPdfFile(Path.Combine(folder, "不存在.pdf")));

            Check("启动参数判断：目录不算（File.Exists 对目录返回 false）",
                !PdfFileAssociation.IsSupportedPdfFile(folder),
                folder);

            Check("启动参数判断：真实存在但后缀不是 .pdf 的文件不算",
                !PdfFileAssociation.IsSupportedPdfFile(textPath),
                textPath);

            Check("启动参数判断：null / 空串 / 纯空白都不算（主程序命令行可能什么都拿不到）",
                !PdfFileAssociation.IsSupportedPdfFile(null)
                && !PdfFileAssociation.IsSupportedPdfFile("")
                && !PdfFileAssociation.IsSupportedPdfFile("   "));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* 临时目录清不掉无所谓 */ }
        }

        // ================================================================
        // 4. BuildOpenCommand：给 exe 与 %1 都加引号，路径含空格不会坏
        // ================================================================
        const string exeWithSpaces = @"C:\Program Files\YuLiang Toolkit\app.exe";
        var command = PdfFileAssociation.BuildOpenCommand(exeWithSpaces);
        Check("打开命令：exe 与 %1 都带引号（路径含空格也不会被拆成两个参数）",
            command == "\"C:\\Program Files\\YuLiang Toolkit\\app.exe\" \"%1\"",
            command);

        Check("打开命令：先 Trim 再剥外层引号（界面里手改过的路径不会变成三重引号）",
            PdfFileAssociation.BuildOpenCommand("  \"" + exeWithSpaces + "\"  ")
            == "\"" + exeWithSpaces + "\" \"%1\"",
            PdfFileAssociation.BuildOpenCommand("  \"" + exeWithSpaces + "\"  "));

        Check("打开命令：%1 恰好保留一次（命令行参数由资源管理器替换成 PDF 路径）",
            command.Count(c => c == '%') == 1 && command.EndsWith("\"%1\"", StringComparison.Ordinal));

        // ================================================================
        // 5. 当前程序路径 / 「打开方式」里的应用键名
        // ================================================================
        var exePath = PdfFileAssociation.ExecutablePath;
        var appKeyName = PdfFileAssociation.ApplicationKeyName;
        Check("当前程序路径可读、「打开方式」应用键名是纯 exe 文件名（带 .exe、不含路径分隔符）",
            !string.IsNullOrWhiteSpace(exePath)
            && !string.IsNullOrWhiteSpace(appKeyName)
            && appKeyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !appKeyName.Contains('\\') && !appKeyName.Contains('/')
            && appKeyName == Path.GetFileName(exePath),
            $"{exePath} / {appKeyName}");

        // ================================================================
        // 6. 结果记录 / 启动请求（只做类型与内存语义断言，不碰注册表）
        // ================================================================
        var result = new PdfAssociationResult(true, true, "已登记");
        Check("结果记录：位置属性 Success / Registered / Message 原样保留，类型是 sealed record",
            result.Success && result.Registered && result.Message == "已登记"
            && typeof(PdfAssociationResult).IsSealed
            && typeof(PdfAssociationResult).IsClass,
            result.ToString());

        Check("结果记录：record 值相等语义（字段全同即相等，有一项不同就不等）",
            new PdfAssociationResult(true, true, "已登记") == new PdfAssociationResult(true, true, "已登记")
            && new PdfAssociationResult(true, true, "已登记") != new PdfAssociationResult(false, true, "已登记"));

        PdfFilePathLaunch.PendingPdfPath = null;
        var launchDefaultEmpty = PdfFilePathLaunch.PendingPdfPath is null;
        PdfFilePathLaunch.PendingPdfPath = pdfPath;
        var launchReadBack = PdfFilePathLaunch.PendingPdfPath;
        PdfFilePathLaunch.PendingPdfPath = null;
        Check("启动请求：默认没有待打开 PDF，写入 / 读回 / 清空各一次（ViewModel 取走即清空）",
            launchDefaultEmpty && launchReadBack == pdfPath && PdfFilePathLaunch.PendingPdfPath is null,
            launchReadBack ?? "(null)");

        // ================================================================
        // 7. 只读状态函数：不抛异常、返回人话；DescribeState 与 IsRegistered 对得上
        //    （这两个函数只 OpenSubKey 读 HKCU，安全；注意本自检没有登记过关联）
        // ================================================================
        var registered = false;
        var isRegisteredThrew = false;
        try { registered = PdfFileAssociation.IsRegistered(); }
        catch { isRegisteredThrew = true; }

        Check("IsRegistered 只读注册表、不抛异常（返回 bool 即可，不要求已登记）",
            !isRegisteredThrew,
            $"IsRegistered={registered}");

        var stateText = "";
        var stateThrew = false;
        try { stateText = PdfFileAssociation.DescribeState(); }
        catch { stateThrew = true; }

        Check("DescribeState 不抛异常、返回非空中文说明且提到 PDF",
            !stateThrew
            && !string.IsNullOrWhiteSpace(stateText)
            && stateText.Contains("PDF", StringComparison.Ordinal),
            stateText);

        Check("DescribeState 与 IsRegistered 对得上（未登记说「未关联」/ 已登记说「已登记」）",
            !stateThrew
            && (registered
                ? stateText.StartsWith("已登记", StringComparison.Ordinal)
                : stateText.StartsWith("未关联", StringComparison.Ordinal)),
            $"IsRegistered={registered} -> {stateText}");

        var stateAgain = "";
        try { stateAgain = PdfFileAssociation.DescribeState(); } catch { /* 下面断言会失败 */ }
        Check("DescribeState 可重复读取（两次结果一致，不残留状态）",
            stateAgain == stateText,
            $"{stateText} / {stateAgain}");
    }
}
