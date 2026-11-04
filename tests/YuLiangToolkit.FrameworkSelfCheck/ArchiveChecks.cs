using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Writers;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Archive;
using static YuLiangToolkit.Tools.Archive.ArchiveFormats;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「压缩包工具」自检：工具元数据、格式识别（扩展名 / 文件头 / Filter / EnsureExtension）、
/// 自造压缩包真跑 Probe + Extract、Create 真创建 ZIP / 7Z 再解压比对内容、testdata/archive 素材真解压、
/// 密码（正确 / 错误 / 缺失）、三种覆盖策略、路径穿越防护、单文件压缩命名、取消、MeasureSources、
/// SuggestArchivePath、ArchiveConfig 默认值与落盘，以及「普通文本文件绝不能当成压缩包」的容错。
/// <para>
/// <b>硬性约束</b>：全部在 <see cref="SelfCheckScratch"/> 的临时目录里造数据，跑完清理；
/// **不碰真实 Data / Config**（数据根已由 Program.cs 指向临时目录）、**不联网、不启外部 exe**。
/// 自造 ZIP / TAR.GZ / TAR.BZ2 / GZ / BZ2 用的是 BCL + SharpCompress 写入器（工具库本来就有这个依赖），
/// 这样「解压」测的是引擎自己的读取路径，不会被现成素材掩盖。
/// </para>
/// <para>
/// fixture（仓库 <c>testdata/archive/</c>）从 <see cref="AppContext.BaseDirectory"/> 与当前目录逐级上溯查找；
/// 找不到时对应断言记一条「跳过」PASS 并写明理由，绝不假装验证过。
/// </para>
/// </summary>
internal static class ArchiveChecks
{
    /// <summary>testdata 里 encrypted.* 的约定密码。</summary>
    private const string FixturePassword = "ylt123";

    public static void Run(Action<string, bool, string> check, Action<string, bool> checkPlain)
    {
        var scratch = SelfCheckScratch.MakeDirectory("archive-checks");
        try
        {
            var fixtureDirectory = FindFixtureDirectory();

            CheckMetadata(check);
            CheckFormatTable(check, checkPlain);
            CheckFormatDetection(check);
            CheckContentDetection(check, scratch, fixtureDirectory);
            CheckCreateAndExtract(check, scratch);
            CheckFixtureExtraction(check, scratch, fixtureDirectory);
            CheckPasswords(check, scratch, fixtureDirectory);
            CheckOverwriteModes(check, scratch);
            CheckZipSlip(check, scratch);
            CheckSingleFileNaming(check, scratch, fixtureDirectory);
            CheckCancellation(check, scratch);
            CheckMeasureSources(check, scratch);
            CheckSuggestArchivePath(check, scratch);
            CheckConfigDefaultsAndRoundTrip(check);
            CheckInvalidArchiveTolerance(check, scratch);
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    // =====================================================================================
    // 1. 工具元数据
    // =====================================================================================

    private static void CheckMetadata(Action<string, bool, string> check)
    {
        var tool = new ArchiveTool();
        check("工具元数据：Id=archive / 名称=压缩包工具 / 分类=文件 / Order=42",
            tool.Id == "archive" && tool.Name == "压缩包工具"
            && tool.Category == "文件" && tool.Order == 42,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("工具标签含「压缩 / 解压」，描述点明 RAR 只能解不能压",
            tool.Tags.Contains("压缩") && tool.Tags.Contains("解压")
            && tool.Description.Contains("RAR", StringComparison.Ordinal)
            && tool.Description.Contains("只能解", StringComparison.Ordinal),
            string.Join("/", tool.Tags));

        check("工具图标是 📦（首页卡片 / 左侧导航用）", tool.EmojiIcon == "📦", tool.EmojiIcon);
    }

    // =====================================================================================
    // 2. 格式表与纯函数（Detect / GetBaseName / CanCreate / Filter / EnsureExtension）
    // =====================================================================================

    private static void CheckFormatTable(Action<string, bool, string> check, Action<string, bool> checkPlain)
    {
        var all = All;
        checkPlain("格式表至少有 12 种格式", all.Count >= 12);

        check("格式表：核心格式（ZIP / 7Z / RAR / TAR / TAR.* / GZ / BZ2 / XZ / ZST）都标为可解压",
            all.Count >= 12
            && new[]
                {
                    ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Rar, ArchiveFormat.Tar,
                    ArchiveFormat.TarGz, ArchiveFormat.TarBz2, ArchiveFormat.TarXz, ArchiveFormat.TarZstd,
                    ArchiveFormat.GZip, ArchiveFormat.BZip2, ArchiveFormat.Xz, ArchiveFormat.Zstd,
                }.All(format => all.Any(info => info.Format == format && info.CanExtract)),
            $"{all.Count} 种：{string.Join(",", all.Select(info => info.Format))}");

        check("格式表：只有 ZIP / 7Z 标记为能创建（RAR 不能）",
            CanCreate(ArchiveFormat.Zip) && CanCreate(ArchiveFormat.SevenZip)
            && !CanCreate(ArchiveFormat.Rar) && !CanCreate(ArchiveFormat.Tar)
            && all.Count(info => info.CanCreate) == 2,
            string.Join(",", all.Where(info => info.CanCreate).Select(info => info.Format)));

        check("格式表：GZ / BZ2 / XZ / ZST 标为单文件压缩，ZIP 不是",
            new[] { ArchiveFormat.GZip, ArchiveFormat.BZip2, ArchiveFormat.Xz, ArchiveFormat.Zstd }
                .All(format => all.Any(info => info.Format == format && info.IsSingleStream))
            && all.Where(info => info.Format == ArchiveFormat.Zip).All(info => !info.IsSingleStream),
            string.Join(",", all.Where(info => info.IsSingleStream).Select(info => info.Format)));

        var zipInfo = Info(ArchiveFormat.Zip);
        var sevenZipInfo = Info(ArchiveFormat.SevenZip);
        check("格式信息：Info(ZIP) / Info(7Z) 有可读展示名与主扩展名",
            zipInfo.DisplayName.Length > 0 && zipInfo.Extension == ".zip"
            && sevenZipInfo.DisplayName.Length > 0 && sevenZipInfo.Extension == ".7z",
            $"{zipInfo.DisplayName} / {sevenZipInfo.DisplayName}");
    }

    private static void CheckFormatDetection(Action<string, bool, string> check)
    {
        // 扩展名识别：契约要求按扩展名就能认出所有主格式（双扩展名 .tar.* 也要能整体吃掉）。
        var cases = new (string Path, ArchiveFormat Expected)[]
        {
            ("a.zip", ArchiveFormat.Zip),
            ("a.7z", ArchiveFormat.SevenZip),
            ("a.rar", ArchiveFormat.Rar),
            ("a.tar", ArchiveFormat.Tar),
            ("a.tar.gz", ArchiveFormat.TarGz),
            ("a.tar.bz2", ArchiveFormat.TarBz2),
            ("a.tar.xz", ArchiveFormat.TarXz),
            ("a.tar.zst", ArchiveFormat.TarZstd),
            ("a.gz", ArchiveFormat.GZip),
            ("a.bz2", ArchiveFormat.BZip2),
            ("a.xz", ArchiveFormat.Xz),
            ("a.zst", ArchiveFormat.Zstd),
        };
        var wrong = cases
            .Where(item => Detect(item.Path) != item.Expected)
            .Select(item => $"{item.Path}→{Detect(item.Path)}(期望 {item.Expected})")
            .ToList();
        check("扩展名识别：12 种主扩展名全部认对（含 .tar.* 双扩展名）",
            wrong.Count == 0, string.Join(" | ", wrong));

        check("扩展名识别：大小写不敏感",
            Detect("照片.ZIP") == ArchiveFormat.Zip
            && Detect("A.7Z") == ArchiveFormat.SevenZip
            && Detect("b.TAR.GZ") == ArchiveFormat.TarGz,
            $"{Detect("照片.ZIP")} / {Detect("A.7Z")} / {Detect("b.TAR.GZ")}");

        check("扩展名识别：常见别名 .tgz / .tbz2 / .txz / .tzst 也归到 TAR 系列",
            Detect("a.tgz") == ArchiveFormat.TarGz
            && Detect("a.tbz2") == ArchiveFormat.TarBz2
            && Detect("a.txz") == ArchiveFormat.TarXz
            && Detect("a.tzst") == ArchiveFormat.TarZstd,
            $"{Detect("a.tgz")} / {Detect("a.tbz2")} / {Detect("a.txz")} / {Detect("a.tzst")}");

        check("扩展名识别：普通文本 / 没有扩展名 → Unknown",
            Detect("a.txt") == ArchiveFormat.Unknown && Detect("README") == ArchiveFormat.Unknown,
            $"{Detect("a.txt")} / {Detect("README")}");

        check("GetBaseName：.tar.gz 去掉两层扩展名，单层扩展名去一层",
            GetBaseName("a.tar.gz") == "a"
            && GetBaseName("a.zip") == "a"
            && GetBaseName(@"D:\下载\b.7z") == "b"
            && GetBaseName("c.tar.bz2") == "c",
            $"{GetBaseName("a.tar.gz")} / {GetBaseName("a.zip")} / {GetBaseName(@"D:\下载\b.7z")}");

        var openFilter = BuildOpenFileFilter();
        check("打开对话框 Filter：含各格式扩展名与「所有支持的压缩包」聚合项",
            openFilter.Contains("*.zip", StringComparison.Ordinal)
            && openFilter.Contains("*.rar", StringComparison.Ordinal)
            && openFilter.Contains("*.7z", StringComparison.Ordinal)
            && openFilter.Contains("*.tar.gz", StringComparison.Ordinal)
            && openFilter.Contains("所有", StringComparison.Ordinal),
            openFilter);

        var saveFilter = BuildSaveFileFilter();
        check("保存对话框 Filter：只列 ZIP / 7Z，不列 RAR（不能创建 RAR）",
            saveFilter.Contains("*.zip", StringComparison.Ordinal)
            && saveFilter.Contains("*.7z", StringComparison.Ordinal)
            && !saveFilter.Contains("*.rar", StringComparison.Ordinal),
            saveFilter);

        var patterns = ExtractExtensionPatterns;
        check("拖放扩展名清单：包含 *.zip / *.rar / *.7z / *.tar.gz，且都是 *.xxx 形式",
            patterns.Count >= 10
            && patterns.Contains("*.zip") && patterns.Contains("*.rar")
            && patterns.Contains("*.7z") && patterns.Contains("*.tar.gz")
            && patterns.All(pattern => pattern.StartsWith("*.", StringComparison.Ordinal)),
            string.Join(",", patterns));

        check("EnsureExtension：没有扩展名时补上格式扩展名",
            EnsureExtension("a", ArchiveFormat.Zip).EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && EnsureExtension(@"D:\x\b", ArchiveFormat.SevenZip).EndsWith(".7z", StringComparison.OrdinalIgnoreCase),
            $"{EnsureExtension("a", ArchiveFormat.Zip)} / {EnsureExtension(@"D:\x\b", ArchiveFormat.SevenZip)}");

        check("EnsureExtension：已有同格式扩展名时不动（大小写不敏感）",
            EnsureExtension("a.zip", ArchiveFormat.Zip) == "a.zip"
            && EnsureExtension("a.ZIP", ArchiveFormat.Zip) == "a.ZIP"
            && EnsureExtension("a.7z", ArchiveFormat.SevenZip) == "a.7z",
            $"{EnsureExtension("a.zip", ArchiveFormat.Zip)} / {EnsureExtension("a.ZIP", ArchiveFormat.Zip)}");

        check("EnsureExtension：扩展名与目标格式不同时再补一个",
            EnsureExtension("a.zip", ArchiveFormat.SevenZip).EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            && !EnsureExtension("a.zip", ArchiveFormat.SevenZip).EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
            EnsureExtension("a.zip", ArchiveFormat.SevenZip));
    }

    // =====================================================================================
    // 3. 文件头识别（内容优先）+ 改名副本 + 普通文件不误判
    // =====================================================================================

    private static void CheckContentDetection(Action<string, bool, string> check, string scratch, string? fixtureDirectory)
    {
        var plainText = Path.Combine(scratch, "普通文本.txt");
        File.WriteAllText(plainText, "这不是压缩包，只是一段普通文本。", new UTF8Encoding(false));

        check("内容识别：普通文本文件 → Unknown（拖入时不会误判成压缩包）",
            DetectByContent(plainText) == ArchiveFormat.Unknown
            && DetectBest(plainText) == ArchiveFormat.Unknown
            && !IsArchivePath(plainText),
            $"{DetectByContent(plainText)} / {DetectBest(plainText)}");

        var missing = Path.Combine(scratch, "根本不存在的文件.zip");
        var missingThrew = false;
        var missingFormat = ArchiveFormat.Unknown;
        try { missingFormat = DetectByContent(missing); }
        catch { missingThrew = true; }
        check("内容识别：读不了的文件返回 Unknown 且不抛异常（DetectByContent 容错）",
            !missingThrew && missingFormat == ArchiveFormat.Unknown,
            missingThrew ? "抛了异常" : missingFormat.ToString());

        if (fixtureDirectory is null)
        {
            check("testdata/archive 测试素材", true,
                "跳过（找不到 fixture）：从 AppContext.BaseDirectory / 当前目录向上未找到 testdata/archive，"
                + "涉及真实素材的断言全部跳过（自造压缩包断言已照常跑）");
            return;
        }

        // GZ / BZ2 / XZ / ZST 的头只能认出「外层压缩」；TAR 包装的，内容识别至少要能认出内层 TAR。
        var expectations = new (string File, ArchiveFormat Expected)[]
        {
            ("sample.zip", ArchiveFormat.Zip),
            ("sample.7z", ArchiveFormat.SevenZip),
            ("sample.rar", ArchiveFormat.Rar),
            ("sample.tar.gz", ArchiveFormat.TarGz),
            ("sample.tar.bz2", ArchiveFormat.TarBz2),
            ("sample.tar.xz", ArchiveFormat.TarXz),
            ("sample.tar.zst", ArchiveFormat.TarZstd),
            ("single.txt.gz", ArchiveFormat.GZip),
            ("single.txt.bz2", ArchiveFormat.BZip2),
            ("single.txt.xz", ArchiveFormat.Xz),
            ("single.txt.zst", ArchiveFormat.Zstd),
        };
        var wrong = expectations
            .Where(item => DetectBest(FixturePath(fixtureDirectory, item.File)) != item.Expected)
            .Select(item => $"{item.File}→{DetectBest(FixturePath(fixtureDirectory, item.File))}(期望 {item.Expected})")
            .ToList();
        check("内容识别（DetectBest）：11 个 fixture 都认成正确格式（内容优先 + 扩展名兜底）",
            wrong.Count == 0, string.Join(" | ", wrong));

        // .tar.gz 的「先看 GZ 头、再 peek 里面是不是 TAR」是契约明确写了的，单独钉死。
        check("内容识别：sample.tar.gz 的 GZ 头能 peek 出里面是 TAR → TarGz",
            DetectByContent(FixturePath(fixtureDirectory, "sample.tar.gz")) == ArchiveFormat.TarGz,
            DetectByContent(FixturePath(fixtureDirectory, "sample.tar.gz")).ToString());

        // BZ2 / XZ / ZST 头在契约里只要求认魔数，是否额外 peek TAR 不作强约束（DetectBest 已在上一条兜住）。
        var ambiguous = new (string File, ArchiveFormat Tar, ArchiveFormat Single)[]
        {
            ("sample.tar.bz2", ArchiveFormat.TarBz2, ArchiveFormat.BZip2),
            ("sample.tar.xz", ArchiveFormat.TarXz, ArchiveFormat.Xz),
            ("sample.tar.zst", ArchiveFormat.TarZstd, ArchiveFormat.Zstd),
        };
        var ambiguousWrong = ambiguous
            .Where(item =>
            {
                var actual = DetectByContent(FixturePath(fixtureDirectory, item.File));
                return actual != item.Tar && actual != item.Single;
            })
            .Select(item => $"{item.File}→{DetectByContent(FixturePath(fixtureDirectory, item.File))}")
            .ToList();
        check("内容识别：TAR.BZ2 / TAR.XZ / TAR.ZST 的魔数至少认得出外层压缩或内层 TAR",
            ambiguousWrong.Count == 0, string.Join(" | ", ambiguousWrong));

        var sampleZip = FixturePath(fixtureDirectory, "sample.zip");
        check("IsArchivePath：真实压缩包为真，普通文本为假",
            IsArchivePath(sampleZip)
            && IsArchivePath(FixturePath(fixtureDirectory, "sample.rar"))
            && !IsArchivePath(plainText),
            $"{IsArchivePath(sampleZip)} / {IsArchivePath(plainText)}");

        // 临时改名的副本：扩展名不可信时，内容识别仍要认得出（用户改错扩展名 / SFX 也能拖）。
        var disguised = Path.Combine(scratch, "改了扩展名的伪装.dat");
        File.Copy(sampleZip, disguised, overwrite: true);
        check("内容识别：把 ZIP 改名成 .dat 后，扩展名识别不了、内容识别 / DetectBest 仍认成 ZIP",
            Detect(disguised) == ArchiveFormat.Unknown
            && DetectByContent(disguised) == ArchiveFormat.Zip
            && DetectBest(disguised) == ArchiveFormat.Zip
            && IsArchivePath(disguised),
            $"{Detect(disguised)} / {DetectByContent(disguised)} / {DetectBest(disguised)}");
    }

    // =====================================================================================
    // 4. 自造压缩包：真 Probe + 真 Extract；再真 Create ZIP / 7Z 后 Probe + Extract 比对内容
    // =====================================================================================

    private static void CheckCreateAndExtract(Action<string, bool, string> check, string scratch)
    {
        // 准备源：中文文件名 + 子目录里的文件（两层都要能保留）。
        var sourceDirectory = Path.Combine(scratch, "素材");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "子目录"));
        const string chineseText = "压缩包自检内容：你好，世界（中文文件名要能保留）";
        const string nestedText = "# 自检 readme\n\n这是嵌套目录里的文件。";
        File.WriteAllText(Path.Combine(sourceDirectory, "你好.txt"), chineseText, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(sourceDirectory, "子目录", "readme.md"), nestedText, new UTF8Encoding(false));

        // ---------- 4.1 自造 ZIP → Probe + Extract --------------------------------
        var homemadeZip = Path.Combine(scratch, "自造.zip");
        using (var archive = ZipFile.Open(homemadeZip, ZipArchiveMode.Create))
        {
            WriteZipText(archive, "你好.txt", chineseText);
            WriteZipText(archive, "子目录/readme.md", nestedText);
        }

        var zipProbe = ArchiveEngine.Probe(homemadeZip);
        check("自造 ZIP：Probe 成功、格式=ZIP、列出中文文件名",
            zipProbe.Success && zipProbe.Format == ArchiveFormat.Zip && zipProbe.FileCount == 2
            && zipProbe.Entries.Any(entry => NormalizeKey(entry.Key) == "你好.txt")
            && zipProbe.TotalSize > 0,
            DescribeProbe(zipProbe));

        var zipDestination = Path.Combine(scratch, "解压-自造-zip");
        var zipExtract = ArchiveEngine.Extract(homemadeZip,
            new ArchiveExtractOptions { DestinationDirectory = zipDestination });
        check("自造 ZIP：Extract 解出中文文件名且内容一致",
            zipExtract.Success
            && File.Exists(Path.Combine(zipDestination, "你好.txt"))
            && ReadTextOrEmpty(Path.Combine(zipDestination, "你好.txt")) == chineseText
            && ReadTextOrEmpty(Path.Combine(zipDestination, "子目录", "readme.md")) == nestedText,
            zipExtract.Error ?? zipExtract.OutputPath ?? "");

        check("Extract：OutputPath 返回实际解压目录（绝对路径）",
            SamePath(zipExtract.OutputPath, zipDestination), zipExtract.OutputPath ?? "(null)");

        // 每个包单独子文件夹：多套一层「压缩包主名」。
        var subfolderDestination = Path.Combine(scratch, "解压-每个包一个子文件夹");
        var subfolderResult = ArchiveEngine.Extract(homemadeZip, new ArchiveExtractOptions
        {
            DestinationDirectory = subfolderDestination,
            CreateSubfolderPerArchive = true,
        });
        check("CreateSubfolderPerArchive：解压目标多套一层「压缩包主名」目录",
            subfolderResult.Success && File.Exists(Path.Combine(subfolderDestination, "自造", "你好.txt")),
            subfolderResult.Error ?? subfolderResult.OutputPath ?? "");

        // ---------- 4.2 自造 TAR.GZ / TAR.BZ2（压缩版 TAR 必须能列条目） ------------------
        var homemadeTarGz = Path.Combine(scratch, "自造.tar.gz");
        WriteTarArchive(homemadeTarGz, CompressionType.GZip,
            ("你好.txt", chineseText), ("子目录/readme.md", nestedText));
        var tarGzProbe = ArchiveEngine.Probe(homemadeTarGz, maxEntries: 0);
        check("自造 TAR.GZ：Probe 能列出压缩 TAR 里的条目（ArchiveFactory 不支持时走 ReaderFactory）",
            tarGzProbe.Success && tarGzProbe.Format == ArchiveFormat.TarGz && tarGzProbe.FileCount == 2
            && tarGzProbe.Entries.Any(entry => NormalizeKey(entry.Key) == "子目录/readme.md"),
            DescribeProbe(tarGzProbe));
        var tarGzDestination = Path.Combine(scratch, "解压-自造-targz");
        var tarGzExtract = ArchiveEngine.Extract(homemadeTarGz,
            new ArchiveExtractOptions { DestinationDirectory = tarGzDestination });
        check("自造 TAR.GZ：Extract 解出两份文件且内容一致",
            tarGzExtract.Success
            && ReadTextOrEmpty(Path.Combine(tarGzDestination, "你好.txt")) == chineseText
            && ReadTextOrEmpty(Path.Combine(tarGzDestination, "子目录", "readme.md")) == nestedText,
            tarGzExtract.Error ?? tarGzExtract.OutputPath ?? "");

        var homemadeTarBz2 = Path.Combine(scratch, "自造.tar.bz2");
        WriteTarArchive(homemadeTarBz2, CompressionType.BZip2,
            ("你好.txt", chineseText), ("子目录/readme.md", nestedText));
        var tarBz2Probe = ArchiveEngine.Probe(homemadeTarBz2, maxEntries: 0);
        check("自造 TAR.BZ2：Probe 能列出条目、Format=TarBz2",
            tarBz2Probe.Success && tarBz2Probe.Format == ArchiveFormat.TarBz2 && tarBz2Probe.FileCount == 2,
            DescribeProbe(tarBz2Probe));
        var tarBz2Destination = Path.Combine(scratch, "解压-自造-tarbz2");
        var tarBz2Extract = ArchiveEngine.Extract(homemadeTarBz2,
            new ArchiveExtractOptions { DestinationDirectory = tarBz2Destination });
        check("自造 TAR.BZ2：Extract 解出中文文件且内容一致",
            tarBz2Extract.Success
            && ReadTextOrEmpty(Path.Combine(tarBz2Destination, "你好.txt")) == chineseText,
            tarBz2Extract.Error ?? tarBz2Extract.OutputPath ?? "");

        // ---------- 4.3 自造单文件 GZ / BZ2（输出文件名优先取条目名，没有条目名用 GetBaseName） ----
        var homemadeGz = Path.Combine(scratch, "自造单文件.txt.gz");
        using (var file = File.Create(homemadeGz))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            var payload = Encoding.UTF8.GetBytes(chineseText);
            gzip.Write(payload, 0, payload.Length);
        }
        var gzProbe = ArchiveEngine.Probe(homemadeGz);
        check("自造 GZ：Probe 成功、格式=GZip、只有一个文件条目",
            gzProbe.Success && gzProbe.Format == ArchiveFormat.GZip && gzProbe.FileCount == 1,
            DescribeProbe(gzProbe));
        var gzDestination = Path.Combine(scratch, "解压-自造-gz");
        var gzExtract = ArchiveEngine.Extract(homemadeGz,
            new ArchiveExtractOptions { DestinationDirectory = gzDestination });
        check("自造 GZ：解出单个文件、文件名用 GetBaseName（不会再套同名目录）",
            gzExtract.Success
            && File.Exists(Path.Combine(gzDestination, "自造单文件.txt"))
            && ReadTextOrEmpty(Path.Combine(gzDestination, "自造单文件.txt")) == chineseText
            && !Directory.Exists(Path.Combine(gzDestination, "自造单文件")),
            gzExtract.Error ?? gzExtract.OutputPath ?? "");

        var homemadeBz2 = Path.Combine(scratch, "自造单文件2.txt.bz2");
        using (var file = File.Create(homemadeBz2))
        using (var bzip2 = BZip2Stream.Create(file,
                   SharpCompress.Compressors.CompressionMode.Compress, decompressConcatenated: false))
        {
            var payload = Encoding.UTF8.GetBytes(chineseText);
            bzip2.Write(payload, 0, payload.Length);
        }
        var bz2Destination = Path.Combine(scratch, "解压-自造-bz2");
        var bz2Extract = ArchiveEngine.Extract(homemadeBz2,
            new ArchiveExtractOptions { DestinationDirectory = bz2Destination });
        check("自造 BZ2：解出单个文件、文件名用 GetBaseName",
            bz2Extract.Success
            && File.Exists(Path.Combine(bz2Destination, "自造单文件2.txt"))
            && ReadTextOrEmpty(Path.Combine(bz2Destination, "自造单文件2.txt")) == chineseText,
            bz2Extract.Error ?? bz2Extract.OutputPath ?? "");

        // ---------- 4.4 Create 真创建 ZIP：自动补扩展名 + 递归 + 保留源文件夹层 + 进度 -------
        var outputDirectory = Path.Combine(scratch, "创建输出");
        Directory.CreateDirectory(outputDirectory);
        var zipOutputWithoutExtension = Path.Combine(outputDirectory, "创建结果");
        var zipProgress = new ListProgress();
        var zipCreate = ArchiveEngine.Create(zipOutputWithoutExtension,
            new ArchiveCreateOptions { Format = ArchiveFormat.Zip, Level = CompressionPreset.Normal },
            new[] { sourceDirectory }, zipProgress);
        var zipOutputPath = zipCreate.OutputPath ?? zipOutputWithoutExtension;
        check("Create ZIP：成功、自动补 .zip 扩展名、文件真的生成、文件数正确",
            zipCreate.Success && zipCreate.FileCount == 2 && File.Exists(zipOutputPath)
            && zipOutputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
            $"{zipCreate.Error ?? zipCreate.OutputPath ?? ""} / FileCount={zipCreate.FileCount}");

        check("Create ZIP：进度回调有上报，最后一次 FilesDone 等于文件数",
            zipProgress.Items.Count > 0 && zipProgress.Items[^1].FilesDone == zipCreate.FileCount,
            $"{zipProgress.Items.Count} 次上报 / 最后 FilesDone={zipProgress.Items.LastOrDefault()?.FilesDone}");

        var createdZipProbe = ArchiveEngine.Probe(zipOutputPath);
        check("Create ZIP → Probe：包内保留「源文件夹名」这一层，中文文件名正确",
            createdZipProbe.Success && createdZipProbe.Format == ArchiveFormat.Zip
            && createdZipProbe.FileCount == 2
            && createdZipProbe.Entries.Any(entry => NormalizeKey(entry.Key) == "素材/你好.txt")
            && createdZipProbe.Entries.Any(entry => NormalizeKey(entry.Key) == "素材/子目录/readme.md"),
            DescribeProbe(createdZipProbe));

        var createdZipDestination = Path.Combine(scratch, "解压-创建-zip");
        var createdZipExtract = ArchiveEngine.Extract(zipOutputPath,
            new ArchiveExtractOptions { DestinationDirectory = createdZipDestination });
        check("Create ZIP → Extract：解出的中文文件名与内容都和源一致",
            createdZipExtract.Success
            && ReadTextOrEmpty(Path.Combine(createdZipDestination, "素材", "你好.txt")) == chineseText
            && ReadTextOrEmpty(Path.Combine(createdZipDestination, "素材", "子目录", "readme.md")) == nestedText,
            createdZipExtract.Error ?? createdZipExtract.OutputPath ?? "");

        // ---------- 4.5 Create 真创建 7Z（契约硬要求：必须真创建、真 Probe、真解压） ----------
        var sevenZipOutput = Path.Combine(outputDirectory, "创建结果7z");
        var sevenZipCreate = ArchiveEngine.Create(sevenZipOutput,
            new ArchiveCreateOptions { Format = ArchiveFormat.SevenZip, Level = CompressionPreset.Normal },
            new[] { sourceDirectory });
        var sevenZipPath = sevenZipCreate.OutputPath ?? sevenZipOutput;
        check("Create 7Z：成功、生成 .7z 文件、文件数正确",
            sevenZipCreate.Success && File.Exists(sevenZipPath) && sevenZipCreate.FileCount == 2
            && sevenZipPath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase),
            $"{sevenZipCreate.Error ?? sevenZipCreate.OutputPath ?? ""} / FileCount={sevenZipCreate.FileCount}");

        var sevenZipProbe = ArchiveEngine.Probe(sevenZipPath);
        check("Create 7Z → Probe：7Z 走 ArchiveFactory 也能列目录与中文名",
            sevenZipProbe.Success && sevenZipProbe.Format == ArchiveFormat.SevenZip
            && sevenZipProbe.FileCount == 2
            && sevenZipProbe.Entries.Any(entry => NormalizeKey(entry.Key) == "素材/你好.txt"),
            DescribeProbe(sevenZipProbe));

        var sevenZipDestination = Path.Combine(scratch, "解压-创建-7z");
        var sevenZipExtract = ArchiveEngine.Extract(sevenZipPath,
            new ArchiveExtractOptions { DestinationDirectory = sevenZipDestination });
        check("Create 7Z → Extract：7Z 真解压，解出的中文文件名与内容都对",
            sevenZipExtract.Success
            && ReadTextOrEmpty(Path.Combine(sevenZipDestination, "素材", "你好.txt")) == chineseText
            && ReadTextOrEmpty(Path.Combine(sevenZipDestination, "素材", "子目录", "readme.md")) == nestedText,
            sevenZipExtract.Error ?? sevenZipExtract.OutputPath ?? "");

        // ---------- 4.6 Create RAR：必须明确拒绝（版权原因只能解不能压），不留半成品 ----------
        var rarOutput = Path.Combine(outputDirectory, "不允许创建.rar");
        var rarCreate = ArchiveEngine.Create(rarOutput,
            new ArchiveCreateOptions { Format = ArchiveFormat.Rar }, new[] { sourceDirectory });
        check("Create RAR：返回失败并说明版权原因（只解不压），不留半成品文件",
            !rarCreate.Success && !string.IsNullOrWhiteSpace(rarCreate.Error)
            && rarCreate.Error!.Contains("RAR", StringComparison.Ordinal)
            && !File.Exists(rarOutput),
            rarCreate.Error ?? "(没有错误消息)");
    }

    // =====================================================================================
    // 5. testdata/archive 素材真实解压（RAR5 / TAR.XZ / TAR.ZST）
    // =====================================================================================

    private static void CheckFixtureExtraction(Action<string, bool, string> check, string scratch, string? fixtureDirectory)
    {
        if (fixtureDirectory is null) return;

        var sampleZip = FixturePath(fixtureDirectory, "sample.zip");
        var fullProbe = ArchiveEngine.Probe(sampleZip, maxEntries: 0);
        check("fixture sample.zip：Probe 统计正确（2 个文件 + 2 个目录，含中文名与空目录）",
            fullProbe.Success && fullProbe.Format == ArchiveFormat.Zip && fullProbe.FileCount == 2
            && fullProbe.EntryCount == fullProbe.Entries.Count && !fullProbe.EntriesTruncated
            && fullProbe.Entries.Any(entry => NormalizeKey(entry.Key) == "你好.txt")
            && fullProbe.Entries.Any(entry =>
                entry.IsDirectory && NormalizeKey(entry.Key).TrimEnd('/') == "空文件夹")
            && fullProbe.TotalSize > 0,
            DescribeProbe(fullProbe));

        var limitedProbe = ArchiveEngine.Probe(sampleZip, maxEntries: 2);
        check("Probe maxEntries：只返回前 N 条并标记 EntriesTruncated，FileCount 仍统计全部",
            limitedProbe.Success && limitedProbe.EntriesTruncated
            && limitedProbe.Entries.Count == 2 && limitedProbe.FileCount == 2,
            DescribeProbe(limitedProbe));

        // RAR（RAR5）：扩展名 + 内容 + 真解压都要过。
        var rarPath = FixturePath(fixtureDirectory, "sample.rar");
        var rarProbe = ArchiveEngine.Probe(rarPath, maxEntries: 0);
        var rarDestination = Path.Combine(scratch, "fixture-rar");
        var rarExtract = ArchiveEngine.Extract(rarPath,
            new ArchiveExtractOptions { DestinationDirectory = rarDestination });
        check("fixture sample.rar：Probe 认成 RAR、真实解压成功（RAR5 必须过）",
            rarProbe.Success && rarProbe.Format == ArchiveFormat.Rar
            && rarExtract.Success
            && File.Exists(Path.Combine(rarDestination, "你好.txt"))
            && File.Exists(Path.Combine(rarDestination, "子目录", "readme.md")),
            rarExtract.Error ?? DescribeProbe(rarProbe));

        foreach (var (file, expectedFormat) in new[]
                 {
                     ("sample.tar.xz", ArchiveFormat.TarXz),
                     ("sample.tar.zst", ArchiveFormat.TarZstd),
                 })
        {
            var path = FixturePath(fixtureDirectory, file);
            var probe = ArchiveEngine.Probe(path);
            var destination = Path.Combine(scratch, "fixture-" + file.Replace('.', '-'));
            var extract = ArchiveEngine.Extract(path,
                new ArchiveExtractOptions { DestinationDirectory = destination });
            check($"fixture {file}：Probe 认成 {expectedFormat}、真实解压出中文文件与嵌套目录",
                probe.Success && probe.Format == expectedFormat && extract.Success
                && ReadTextOrEmpty(Path.Combine(destination, "你好.txt")).Contains("压缩包测试文本", StringComparison.Ordinal)
                && ReadTextOrEmpty(Path.Combine(destination, "子目录", "readme.md")).Contains("嵌套目录", StringComparison.Ordinal),
                extract.Error ?? DescribeProbe(probe));
        }
    }

    // =====================================================================================
    // 6. 密码：三个加密 fixture 的「正确 / 错误 / 不传」三种情况
    // =====================================================================================

    private static void CheckPasswords(Action<string, bool, string> check, string scratch, string? fixtureDirectory)
    {
        if (fixtureDirectory is null) return;

        foreach (var file in new[] { "encrypted.zip", "encrypted.7z", "encrypted.rar" })
        {
            var path = FixturePath(fixtureDirectory, file);
            // 目的地必须带「全名」（含扩展名）：三个 encrypted.* 去掉扩展名都叫 encrypted，
            // 共用一个目录的话后两个会被默认 Rename 策略解成「你好 (1).txt」，目录里就不止一个文件了。
            var stem = file.Replace('.', '-');

            var correctDestination = Path.Combine(scratch, "密码-" + stem + "-正确");
            var correct = ArchiveEngine.Extract(path, new ArchiveExtractOptions
            {
                DestinationDirectory = correctDestination,
                Password = FixturePassword,
            });
            check($"{file}：密码 {FixturePassword} 解压成功并解出文件",
                correct.Success && Directory.Exists(correctDestination)
                && Directory.GetFiles(correctDestination, "*", SearchOption.AllDirectories).Length == 1,
                correct.Error ?? correct.OutputPath ?? "");

            var wrongDestination = Path.Combine(scratch, "密码-" + stem + "-错误");
            var wrong = ArchiveEngine.Extract(path, new ArchiveExtractOptions
            {
                DestinationDirectory = wrongDestination,
                Password = "这不是密码",
            });
            check($"{file}：错密码返回失败（中文原因、不抛异常）",
                !wrong.Success && !string.IsNullOrWhiteSpace(wrong.Error),
                wrong.Error ?? "(没有错误消息)");

            var missingDestination = Path.Combine(scratch, "密码-" + stem + "-缺失");
            var missing = ArchiveEngine.Extract(path, new ArchiveExtractOptions
            {
                DestinationDirectory = missingDestination,
            });
            check($"{file}：不传密码也返回失败",
                !missing.Success && !string.IsNullOrWhiteSpace(missing.Error),
                missing.Error ?? "(没有错误消息)");

            var probe = ArchiveEngine.Probe(path, FixturePassword);
            check($"{file}：Probe 带密码成功并标记 IsEncrypted",
                probe.Success && probe.IsEncrypted && probe.FileCount == 1,
                DescribeProbe(probe));
        }
    }

    // =====================================================================================
    // 7. 覆盖策略：Overwrite / Skip / Rename
    // =====================================================================================

    private static void CheckOverwriteModes(Action<string, bool, string> check, string scratch)
    {
        var sourceZip = Path.Combine(scratch, "覆盖测试.zip");
        using (var archive = ZipFile.Open(sourceZip, ZipArchiveMode.Create))
            WriteZipText(archive, "同名.txt", "新内容");

        // Overwrite：直接覆盖。
        var overwriteDestination = Path.Combine(scratch, "覆盖-Overwrite");
        Directory.CreateDirectory(overwriteDestination);
        File.WriteAllText(Path.Combine(overwriteDestination, "同名.txt"), "旧内容", new UTF8Encoding(false));
        var overwriteResult = ArchiveEngine.Extract(sourceZip, new ArchiveExtractOptions
        {
            DestinationDirectory = overwriteDestination,
            Overwrite = OverwriteMode.Overwrite,
        });
        check("覆盖策略 Overwrite：已存在文件被压缩包里的内容覆盖",
            overwriteResult.Success
            && File.ReadAllText(Path.Combine(overwriteDestination, "同名.txt")) == "新内容"
            && overwriteResult.SkippedCount == 0,
            $"{File.ReadAllText(Path.Combine(overwriteDestination, "同名.txt"))} / Skipped={overwriteResult.SkippedCount}");

        // Skip：保留原文件，计入 SkippedCount。
        var skipDestination = Path.Combine(scratch, "覆盖-Skip");
        Directory.CreateDirectory(skipDestination);
        File.WriteAllText(Path.Combine(skipDestination, "同名.txt"), "旧内容", new UTF8Encoding(false));
        var skipResult = ArchiveEngine.Extract(sourceZip, new ArchiveExtractOptions
        {
            DestinationDirectory = skipDestination,
            Overwrite = OverwriteMode.Skip,
        });
        check("覆盖策略 Skip：已存在文件保留、计入 SkippedCount",
            skipResult.Success
            && File.ReadAllText(Path.Combine(skipDestination, "同名.txt")) == "旧内容"
            && skipResult.SkippedCount > 0,
            $"内容={File.ReadAllText(Path.Combine(skipDestination, "同名.txt"))} / Skipped={skipResult.SkippedCount}");

        // Rename：原文件不动，新文件叫「同名 (1).txt」。
        var renameDestination = Path.Combine(scratch, "覆盖-Rename");
        Directory.CreateDirectory(renameDestination);
        File.WriteAllText(Path.Combine(renameDestination, "同名.txt"), "旧内容", new UTF8Encoding(false));
        var renameResult = ArchiveEngine.Extract(sourceZip, new ArchiveExtractOptions
        {
            DestinationDirectory = renameDestination,
            Overwrite = OverwriteMode.Rename,
        });
        var renamedPath = Path.Combine(renameDestination, "同名 (1).txt");
        check("覆盖策略 Rename：自动改名成「同名 (1).txt」，原文件不动",
            renameResult.Success
            && File.ReadAllText(Path.Combine(renameDestination, "同名.txt")) == "旧内容"
            && File.Exists(renamedPath) && File.ReadAllText(renamedPath) == "新内容",
            renameResult.Error ?? string.Join(",", Directory.GetFiles(renameDestination).Select(Path.GetFileName)));
    }

    // =====================================================================================
    // 8. 路径穿越（zip-slip）：越界条目必须跳过，绝不写到目标目录外
    // =====================================================================================

    private static void CheckZipSlip(Action<string, bool, string> check, string scratch)
    {
        var maliciousZip = Path.Combine(scratch, "路径穿越.zip");
        using (var archive = ZipFile.Open(maliciousZip, ZipArchiveMode.Create))
        {
            WriteZipText(archive, "安全.txt", "安全内容");
            WriteZipText(archive, "../evil.txt", "逃逸 1");
            WriteZipText(archive, @"..\evil.txt", "逃逸 2");
            WriteZipText(archive, @"C:\evil-abs.txt", "逃逸 3");
            WriteZipText(archive, "C:/evil-abs2.txt", "逃逸 4");
        }

        var destination = Path.Combine(scratch, "穿越输出", "安全目录");
        var result = ArchiveEngine.Extract(maliciousZip,
            new ArchiveExtractOptions { DestinationDirectory = destination });
        check("路径穿越：安全条目正常解出，越界条目全部跳过（SkippedCount>0 且写了告警）",
            result.Success && File.Exists(Path.Combine(destination, "安全.txt"))
            && result.SkippedCount >= 4 && result.Warnings.Count > 0,
            $"跳过 {result.SkippedCount} / 告警 {result.Warnings.Count} / {result.Error ?? ""}");

        var escaped = Directory.Exists(scratch)
            ? Directory.GetFiles(scratch, "evil*.txt", SearchOption.AllDirectories)
            : Array.Empty<string>();
        check("路径穿越：目标目录外没有任何逃逸文件（C:\\ 根也没有）",
            escaped.Length == 0 && !File.Exists(@"C:\evil-abs.txt") && !File.Exists(@"C:\evil-abs2.txt"),
            escaped.Length == 0 ? "(没有越界文件)" : string.Join(" | ", escaped));
    }

    // =====================================================================================
    // 9. 单文件压缩命名（fixture：single.txt.xz / single.txt.zst）
    // =====================================================================================

    private static void CheckSingleFileNaming(Action<string, bool, string> check, string scratch, string? fixtureDirectory)
    {
        if (fixtureDirectory is null) return;

        foreach (var file in new[] { "single.txt.xz", "single.txt.zst" })
        {
            var destination = Path.Combine(scratch, "单文件-" + file.Replace('.', '-'));
            var result = ArchiveEngine.Extract(FixturePath(fixtureDirectory, file),
                new ArchiveExtractOptions { DestinationDirectory = destination });
            var extracted = Directory.Exists(destination)
                ? Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            check($"fixture {file}：单文件解压直接得到 single.txt（不再套一层同名目录）",
                result.Success && extracted.Length == 1
                && Path.GetFileName(extracted[0]) == "single.txt"
                && new FileInfo(extracted[0]).Length > 0,
                extracted.Length == 0
                    ? result.Error ?? "(什么都没解出来)"
                    : string.Join(",", extracted.Select(Path.GetFileName)));
        }
    }

    // =====================================================================================
    // 10. 取消：Canceled=true、不抛异常（解压保留已解文件，创建不留半成品）
    // =====================================================================================

    private static void CheckCancellation(Action<string, bool, string> check, string scratch)
    {
        // 造一个 2MB 的源，确保「开始处理后才取消」也有东西可处理。
        var bigSourceDirectory = Path.Combine(scratch, "取消源");
        Directory.CreateDirectory(bigSourceDirectory);
        var bigFile = Path.Combine(bigSourceDirectory, "大文件.bin");
        File.WriteAllBytes(bigFile, new byte[2 * 1024 * 1024]);
        var bigZip = Path.Combine(scratch, "取消测试.zip");
        using (var archive = ZipFile.Open(bigZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("大文件.bin", CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            using var sourceStream = File.OpenRead(bigFile);
            sourceStream.CopyTo(entryStream);
        }

        // 解压：立即取消。
        using var extractCts = new CancellationTokenSource();
        extractCts.Cancel();
        var extractThrew = false;
        ArchiveOperationResult canceledExtract = new();
        try
        {
            canceledExtract = ArchiveEngine.Extract(bigZip,
                new ArchiveExtractOptions { DestinationDirectory = Path.Combine(scratch, "取消解压输出") },
                cancellationToken: extractCts.Token);
        }
        catch
        {
            extractThrew = true;
        }
        check("取消解压：返回 Canceled=true、Success=false、不抛异常",
            !extractThrew && canceledExtract.Canceled && !canceledExtract.Success
            && !string.IsNullOrWhiteSpace(canceledExtract.Error),
            extractThrew ? "抛了异常" : canceledExtract.Error ?? "(没有错误消息)");

        // 压缩：立即取消，且不留半成品文件。
        using var createCts = new CancellationTokenSource();
        createCts.Cancel();
        var createThrew = false;
        ArchiveOperationResult canceledCreate = new();
        var cancelOutput = Path.Combine(scratch, "取消创建.zip");
        try
        {
            canceledCreate = ArchiveEngine.Create(cancelOutput,
                new ArchiveCreateOptions { Format = ArchiveFormat.Zip },
                new[] { bigFile }, cancellationToken: createCts.Token);
        }
        catch
        {
            createThrew = true;
        }
        check("取消压缩：返回 Canceled=true、不抛异常、不留半成品文件",
            !createThrew && canceledCreate.Canceled && !canceledCreate.Success
            && !File.Exists(cancelOutput),
            createThrew ? "抛了异常" : canceledCreate.Error ?? "(没有错误消息)");
    }

    // =====================================================================================
    // 11. MeasureSources / SuggestArchivePath
    // =====================================================================================

    private static void CheckMeasureSources(Action<string, bool, string> check, string scratch)
    {
        var directory = Path.Combine(scratch, "统计源");
        Directory.CreateDirectory(Path.Combine(directory, "子目录"));
        File.WriteAllBytes(Path.Combine(directory, "a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(directory, "子目录", "b.bin"), new byte[200]);

        var folderMeasure = ArchiveEngine.MeasureSources(new[] { directory });
        check("MeasureSources：文件夹递归统计文件数与总字节数",
            folderMeasure.FileCount == 2 && folderMeasure.TotalBytes == 300,
            $"{folderMeasure.FileCount} 个文件 / {folderMeasure.TotalBytes} 字节");

        var withMissing = ArchiveEngine.MeasureSources(new[] { directory, Path.Combine(scratch, "不存在的源") });
        check("MeasureSources：不存在的路径被跳过、不抛异常",
            withMissing.FileCount == 2 && withMissing.TotalBytes == 300,
            $"{withMissing.FileCount} 个文件 / {withMissing.TotalBytes} 字节");

        var singleMeasure = ArchiveEngine.MeasureSources(new[] { Path.Combine(directory, "a.bin") });
        check("MeasureSources：单个文件直接统计",
            singleMeasure.FileCount == 1 && singleMeasure.TotalBytes == 100,
            $"{singleMeasure.FileCount} / {singleMeasure.TotalBytes}");
    }

    private static void CheckSuggestArchivePath(Action<string, bool, string> check, string scratch)
    {
        var suggestDirectory = Path.Combine(scratch, "建议路径");
        Directory.CreateDirectory(suggestDirectory);
        var photo = Path.Combine(suggestDirectory, "照片.jpg");
        File.WriteAllText(photo, "x", new UTF8Encoding(false));

        var suggestedFile = ArchiveEngine.SuggestArchivePath(new[] { photo }, ArchiveFormat.Zip);
        check("SuggestArchivePath：单文件 → 同目录 + 文件主名 + .zip",
            SamePath(Path.GetDirectoryName(suggestedFile), suggestDirectory)
            && Path.GetFileName(suggestedFile) == "照片.zip",
            suggestedFile);

        var demoFolder = Path.Combine(suggestDirectory, "演示文件夹");
        Directory.CreateDirectory(demoFolder);
        File.WriteAllText(Path.Combine(demoFolder, "x.txt"), "x", new UTF8Encoding(false));
        var suggestedFolder = ArchiveEngine.SuggestArchivePath(new[] { demoFolder }, ArchiveFormat.SevenZip);
        check("SuggestArchivePath：单文件夹 → 文件夹名 + .7z",
            SamePath(Path.GetDirectoryName(suggestedFolder), suggestDirectory)
            && Path.GetFileName(suggestedFolder) == "演示文件夹.7z",
            suggestedFolder);

        var suggestedMany = ArchiveEngine.SuggestArchivePath(
            new[] { photo, Path.Combine(demoFolder, "x.txt") }, ArchiveFormat.Zip);
        check("SuggestArchivePath：多源 → 「压缩包-yyyyMMdd-HHmmss」",
            Path.GetFileName(suggestedMany).StartsWith("压缩包-", StringComparison.Ordinal)
            && suggestedMany.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
            suggestedMany);

        var overrideDirectory = Path.Combine(scratch, "建议输出目录");
        Directory.CreateDirectory(overrideDirectory);
        var suggestedOverride = ArchiveEngine.SuggestArchivePath(new[] { photo }, ArchiveFormat.Zip, overrideDirectory);
        check("SuggestArchivePath：指定输出目录时放在指定目录",
            SamePath(Path.GetDirectoryName(suggestedOverride), overrideDirectory),
            suggestedOverride);

        File.WriteAllText(Path.Combine(suggestDirectory, "照片.zip"), "占位", new UTF8Encoding(false));
        var suggestedCollision = ArchiveEngine.SuggestArchivePath(new[] { photo }, ArchiveFormat.Zip);
        check("SuggestArchivePath：与已有文件重名时自动加「 (1)」",
            Path.GetFileName(suggestedCollision) == "照片 (1).zip",
            suggestedCollision);
    }

    // =====================================================================================
    // 12. ArchiveConfig 默认值 + 自动落盘读回（只写自检临时数据根，不碰真实 Config）
    // =====================================================================================

    private static void CheckConfigDefaultsAndRoundTrip(Action<string, bool, string> check)
    {
        var defaults = new ArchiveConfig();
        check("配置默认值：解压到压缩包旁边、每个包单独子文件夹、同名自动改名、保留时间戳",
            defaults.ExtractDirectory == ""
            && defaults.SubfolderPerArchive
            && defaults.Overwrite == OverwriteMode.Rename
            && defaults.PreserveTimestamps,
            $"{defaults.ExtractDirectory} / 子文件夹={defaults.SubfolderPerArchive} / {defaults.Overwrite}");

        check("配置默认值：默认创建 7Z、标准压缩、完成后打开输出位置、不存密码",
            defaults.CreateFormat == ArchiveFormat.SevenZip
            && defaults.CompressionLevel == CompressionPreset.Normal
            && defaults.OutputDirectory == ""
            && defaults.OpenFolderAfterDone,
            $"{defaults.CreateFormat} / {defaults.CompressionLevel}");

        var store = ToolConfigStore.For("archive");
        var config = store.Load<ArchiveConfig>();
        check("配置落在 Config\\archive\\config.json，首次加载已落盘",
            Path.GetFileName(Path.GetDirectoryName(config.FilePath)!) == "archive"
            && File.Exists(config.FilePath),
            config.FilePath);

        var targetDirectory = Path.Combine(SelfCheckScratch.Root, "归档配置目标目录");
        Directory.CreateDirectory(targetDirectory);
        config.ExtractDirectory = targetDirectory;
        config.SubfolderPerArchive = false;
        config.Overwrite = OverwriteMode.Skip;
        config.CreateFormat = ArchiveFormat.Zip;
        config.CompressionLevel = CompressionPreset.Maximum;
        config.OpenFolderAfterDone = false;
        ToolConfigStore.FlushAll();

        var saved = JsonFileStore.Read<ArchiveConfig>(store.FilePath);
        check("配置改完 FlushAll 立即落盘并读回（枚举按 C# 原名保存）",
            saved is
            {
                SubfolderPerArchive: false,
                Overwrite: OverwriteMode.Skip,
                CreateFormat: ArchiveFormat.Zip,
                CompressionLevel: CompressionPreset.Maximum,
                OpenFolderAfterDone: false,
            }
            && saved.ExtractDirectory == targetDirectory
            && File.ReadAllText(store.FilePath).Contains("\"Skip\"", StringComparison.Ordinal),
            JsonFileStore.Serialize(saved).Replace(Environment.NewLine, " "));
    }

    // =====================================================================================
    // 13. 失败容错：普通文本 / 不存在的文件 Probe 只能返回失败，不能抛给界面
    // =====================================================================================

    private static void CheckInvalidArchiveTolerance(Action<string, bool, string> check, string scratch)
    {
        var plainText = Path.Combine(scratch, "不是压缩包.txt");
        File.WriteAllText(plainText, "这不是压缩包，只是一段普通文本。", new UTF8Encoding(false));

        var probe = ArchiveEngine.Probe(plainText);
        check("失败容错：普通文本文件 Probe 返回 Success=false + 非空中文 Error，不抛异常",
            !probe.Success && !string.IsNullOrWhiteSpace(probe.Error),
            probe.Error ?? "(没有错误消息)");

        var missing = ArchiveEngine.Probe(Path.Combine(scratch, "不存在.zip"));
        check("失败容错：不存在的文件 Probe 返回失败而不是抛异常",
            !missing.Success && !string.IsNullOrWhiteSpace(missing.Error),
            missing.Error ?? "(没有错误消息)");
    }

    // =====================================================================================
    // 工具：造测试素材 / 查 fixture / 展示结果
    // =====================================================================================

    /// <summary>往 ZIP 里写一段 UTF-8 文本（条目名原样保留，中文名靠 ZIP 的 UTF-8 标记）。</summary>
    private static void WriteZipText(ZipArchive archive, string entryName, string text)
    {
        using var stream = archive.CreateEntry(entryName).Open();
        var payload = Encoding.UTF8.GetBytes(text);
        stream.Write(payload, 0, payload.Length);
    }

    /// <summary>用 SharpCompress 的 TAR 写入器造压缩 TAR（GZip / BZip2），条目名与内容按 UTF-8。</summary>
    private static void WriteTarArchive(string path, CompressionType compressionType,
        params (string Name, string Text)[] entries)
    {
        using var file = File.Create(path);
        using var writer = WriterFactory.OpenWriter(file, ArchiveType.Tar, new WriterOptions(compressionType));
        foreach (var (name, text) in entries)
        {
            using var payload = new MemoryStream(Encoding.UTF8.GetBytes(text));
            writer.Write(name, payload, DateTime.UtcNow);
        }
    }

    /// <summary>从自检程序目录 / 当前目录逐级上溯找仓库的 testdata/archive；找不到返回 null。</summary>
    private static string? FindFixtureDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = start;
            for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(directory); depth++)
            {
                var candidate = Path.Combine(directory, "testdata", "archive");
                if (Directory.Exists(candidate)
                    && File.Exists(Path.Combine(candidate, "sample.zip"))
                    && File.Exists(Path.Combine(candidate, "sample.rar")))
                {
                    return candidate;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        return null;
    }

    private static string FixturePath(string fixtureDirectory, string fileName)
        => Path.Combine(fixtureDirectory, fileName);

    /// <summary>条目 key 统一成 <c>/</c> 分隔、去掉开头的分隔符（引擎约定条目 key 用 <c>/</c>）。</summary>
    private static string NormalizeKey(string key) => key.Replace('\\', '/').TrimStart('/');

    /// <summary>比较两个路径是不是同一处（忽略分隔符与大小写），null / 空一律 false。</summary>
    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeProbe(ArchiveProbeResult probe)
        => probe.Success
            ? $"{probe.Format} / 文件 {probe.FileCount} / 条目 {probe.EntryCount} / {probe.TotalSize} 字节 / 加密={probe.IsEncrypted}"
            : $"失败：{probe.Error}";

    /// <summary>读文件文本；文件不存在 / 读不了返回空串（让断言给出 FAIL 而不是把整组自检炸掉）。</summary>
    private static string ReadTextOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch
        {
            return "";
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响自检结论（下次运行会用新的时间戳目录）。
        }
    }

    /// <summary>同步进度收集器：<see cref="Progress{T}"/> 会把回调排到线程池，断言会读到空列表。</summary>
    private sealed class ListProgress : IProgress<ArchiveProgress>
    {
        public List<ArchiveProgress> Items { get; } = new();

        public void Report(ArchiveProgress value) => Items.Add(value);
    }
}
