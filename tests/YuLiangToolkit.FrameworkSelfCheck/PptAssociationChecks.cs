using System.IO;
using YuLiangToolkit.Tools.PptViewer;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「PPT 阅读器」的文件关联自检（被测代码见 <see cref="PptFileAssociation"/>）。
///
/// <para><b>硬性约束</b>：这里<b>绝不调用</b> <see cref="PptFileAssociation.Register"/> /
/// <see cref="PptFileAssociation.Unregister"/> —— 它们会往当前用户的 HKCU 注册表写真正的文件关联，
/// 跑一次自检就在用户机器上留垃圾（自检必须可重复、无副作用）。也只读不写的状态函数
/// （<see cref="PptFileAssociation.IsRegistered"/> / <see cref="PptFileAssociation.DescribeState"/>）可以测，
/// 它们只调用 <c>Registry.CurrentUser.OpenSubKey</c>。</para>
///
/// <para>文件系统部分只在自检临时目录（<see cref="SelfCheckScratch"/>）里造文件，不碰仓库 Data/Config；
/// 断言的是「有没有这个演示文稿文件」的纯判断，不解析 pptx 内容。
/// 真正的注册表登记 / 取消留给用户手动点界面。</para>
/// </summary>
internal static class PptAssociationChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> checkPlain)
    {
        void Check(string name, bool ok, string detail = "") => check("[PPT关联] " + name, ok, detail);

        // ================================================================
        // 1. 常量与后缀表（ProgId / DisplayName / 三种 PPT 后缀）
        // ================================================================
        Check("常量：ProgId = YuLiangToolkit.PptViewer（不含空白，能直接当注册表键名）",
            PptFileAssociation.ProgId == "YuLiangToolkit.PptViewer"
            && !PptFileAssociation.ProgId.Any(char.IsWhiteSpace),
            PptFileAssociation.ProgId);

        Check("常量：DisplayName = PPT 阅读器（中文界面名，写进注册表显示用）",
            PptFileAssociation.DisplayName == "PPT 阅读器",
            PptFileAssociation.DisplayName);

        Check("后缀表与工具配置同源：.pptx / .pptm / .ppsx（3 项、带点、小写、无重复）",
            ReferenceEquals(PptFileAssociation.SupportedExtensions, PptViewerConfig.SupportedExtensions)
            && PptFileAssociation.SupportedExtensions.Count == 3
            && PptFileAssociation.SupportedExtensions.Contains(".pptx")
            && PptFileAssociation.SupportedExtensions.Contains(".pptm")
            && PptFileAssociation.SupportedExtensions.Contains(".ppsx")
            && PptFileAssociation.SupportedExtensions.All(e => e.StartsWith('.') && e == e.ToLowerInvariant())
            && PptFileAssociation.SupportedExtensions.Distinct().Count() == 3,
            string.Join(",", PptFileAssociation.SupportedExtensions));

        // ================================================================
        // 2. IsSupportedExtension：大小写 / 前后空白容错，不认识的后缀一律拒绝
        // ================================================================
        Check("后缀判断：.pptx / .PPTM / ' .Ppsx ' 都算支持（大小写与空白容错）",
            PptFileAssociation.IsSupportedExtension(".pptx")
            && PptFileAssociation.IsSupportedExtension(".PPTM")
            && PptFileAssociation.IsSupportedExtension(" .Ppsx "),
            ".pptx / .PPTM /  .Ppsx ");

        Check("后缀判断：不认识的后缀、缺点的 pptx、空串、纯空白、null 都拒绝",
            !PptFileAssociation.IsSupportedExtension(".ppt")
            && !PptFileAssociation.IsSupportedExtension(".pdf")
            && !PptFileAssociation.IsSupportedExtension("pptx")
            && !PptFileAssociation.IsSupportedExtension("")
            && !PptFileAssociation.IsSupportedExtension("   ")
            && !PptFileAssociation.IsSupportedExtension(null));

        // ================================================================
        // 3. IsSupportedPresentationFile：真实文件系统判断（只在临时目录造文件）
        // ================================================================
        var folder = SelfCheckScratch.MakeDirectory("ppt-assoc");
        var pptxPath = Path.Combine(folder, "示例 演示.pptx");     // 文件名带空格，验证不是靠字符串前缀猜的
        var upperPptmPath = Path.Combine(folder, "大写后缀.PPTM");
        var ppsxPath = Path.Combine(folder, "放映.ppsx");
        var textPath = Path.Combine(folder, "说明.txt");
        var oldPptPath = Path.Combine(folder, "老格式.ppt");
        File.WriteAllText(pptxPath, "PK");                 // 内容不重要：只判断存在 + 后缀
        File.WriteAllText(upperPptmPath, "PK");
        File.WriteAllText(ppsxPath, "PK");
        File.WriteAllText(textPath, "不是 PPT");
        File.WriteAllText(oldPptPath, "旧格式");

        try
        {
            Check("启动参数判断：真实存在的 .pptx 才算（文件名带空格也行）",
                PptFileAssociation.IsSupportedPresentationFile(pptxPath),
                pptxPath);

            Check("启动参数判断：大写 .PPTM 与 .ppsx 也算（双击来的路径大小写不固定）",
                PptFileAssociation.IsSupportedPresentationFile(upperPptmPath)
                && PptFileAssociation.IsSupportedPresentationFile(ppsxPath),
                upperPptmPath + " / " + ppsxPath);

            Check("启动参数判断：不存在的文件不算（路径错了不能进阅读器）",
                !PptFileAssociation.IsSupportedPresentationFile(Path.Combine(folder, "不存在.pptx")));

            Check("启动参数判断：目录不算（File.Exists 对目录返回 false）",
                !PptFileAssociation.IsSupportedPresentationFile(folder),
                folder);

            Check("启动参数判断：真实存在但后缀不是 PPT 的文件不算（.txt / 旧版 .ppt）",
                !PptFileAssociation.IsSupportedPresentationFile(textPath)
                && !PptFileAssociation.IsSupportedPresentationFile(oldPptPath),
                textPath + " / " + oldPptPath);

            Check("启动参数判断：null / 空串 / 纯空白都不算（主程序命令行可能什么都拿不到）",
                !PptFileAssociation.IsSupportedPresentationFile(null)
                && !PptFileAssociation.IsSupportedPresentationFile("")
                && !PptFileAssociation.IsSupportedPresentationFile("   "));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* 临时目录清不掉无所谓 */ }
        }

        // ================================================================
        // 4. BuildOpenCommand：给 exe 与 %1 都加引号，路径含空格不会坏
        // ================================================================
        const string exeWithSpaces = @"C:\Program Files\YuLiang Toolkit\app.exe";
        var command = PptFileAssociation.BuildOpenCommand(exeWithSpaces);
        Check("打开命令：exe 与 %1 都带引号（路径含空格也不会被拆成两个参数）",
            command == "\"C:\\Program Files\\YuLiang Toolkit\\app.exe\" \"%1\"",
            command);

        Check("打开命令：先 Trim 再剥外层引号（界面里手改过的路径不会变成三重引号）",
            PptFileAssociation.BuildOpenCommand("  \"" + exeWithSpaces + "\"  ")
            == "\"" + exeWithSpaces + "\" \"%1\"",
            PptFileAssociation.BuildOpenCommand("  \"" + exeWithSpaces + "\"  "));

        Check("打开命令：%1 恰好保留一次（命令行参数由资源管理器替换成演示文稿路径）",
            command.Count(c => c == '%') == 1 && command.EndsWith("\"%1\"", StringComparison.Ordinal));

        // ================================================================
        // 5. 当前程序路径 / 「打开方式」里的应用键名
        // ================================================================
        var exePath = PptFileAssociation.ExecutablePath;
        var appKeyName = PptFileAssociation.ApplicationKeyName;
        Check("当前程序路径可读、「打开方式」应用键名是纯 exe 文件名（带 .exe、不含路径分隔符）",
            !string.IsNullOrWhiteSpace(exePath)
            && !string.IsNullOrWhiteSpace(appKeyName)
            && appKeyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !appKeyName.Contains('\\') && !appKeyName.Contains('/')
            && appKeyName == Path.GetFileName(exePath),
            $"{exePath} / {appKeyName}");

        // ================================================================
        // 6. 结果记录（只做类型与内存语义断言，不碰注册表）
        // ================================================================
        var result = new PptAssociationResult(true, true, "已登记");
        Check("结果记录：位置属性 Success / Registered / Message 原样保留，类型是 sealed record",
            result.Success && result.Registered && result.Message == "已登记"
            && typeof(PptAssociationResult).IsSealed
            && typeof(PptAssociationResult).IsClass,
            result.ToString());

        Check("结果记录：record 值相等语义（字段全同即相等，有一项不同就不等）",
            new PptAssociationResult(true, true, "已登记") == new PptAssociationResult(true, true, "已登记")
            && new PptAssociationResult(true, true, "已登记") != new PptAssociationResult(false, true, "已登记"));

        // ================================================================
        // 7. 只读状态函数：不抛异常、返回人话；DescribeState 与 IsRegistered 对得上
        //    （这两个函数只 OpenSubKey 读 HKCU，安全；注意本自检没有登记过关联）
        // ================================================================
        var registered = false;
        var isRegisteredThrew = false;
        try { registered = PptFileAssociation.IsRegistered(); }
        catch { isRegisteredThrew = true; }

        Check("IsRegistered 只读注册表、不抛异常（返回 bool 即可，不要求已登记）",
            !isRegisteredThrew,
            $"IsRegistered={registered}");

        var stateText = "";
        var stateThrew = false;
        try { stateText = PptFileAssociation.DescribeState(); }
        catch { stateThrew = true; }

        Check("DescribeState 不抛异常、返回非空中文说明且提到「关联」（未登记也会说清楚双击行为）",
            !stateThrew
            && !string.IsNullOrWhiteSpace(stateText)
            && stateText.Contains("关联", StringComparison.Ordinal),
            stateText);

        Check("DescribeState 与 IsRegistered 对得上（未登记说「未关联」/ 已登记说「已登记」）",
            !stateThrew
            && (registered
                ? stateText.StartsWith("已登记", StringComparison.Ordinal)
                : stateText.StartsWith("未关联", StringComparison.Ordinal)),
            $"IsRegistered={registered} -> {stateText}");

        var stateAgain = "";
        try { stateAgain = PptFileAssociation.DescribeState(); } catch { /* 下面断言会失败 */ }
        Check("DescribeState 可重复读取（两次结果一致，不残留状态）",
            stateAgain == stateText,
            $"{stateText} / {stateAgain}");

        checkPlain("[PPT关联] 只读自检完成（未写注册表）", true);
    }
}
