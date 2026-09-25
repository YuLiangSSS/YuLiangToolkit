namespace YuLiangToolkit.Tools.FlashDownloader.Core;

/// <summary>批量下载里一行的处理结果（用来解释“为什么这个没下”）。</summary>
public enum BatchLineStatus
{
    /// <summary>会去下载。</summary>
    Ready = 0,

    /// <summary>路径不规范（含 <c>..</c>、空格、特殊字符…）——原始工具统计里的「不规范名称」。</summary>
    InvalidPath = 1,

    /// <summary>和前面某一行重复——统计里的「重复」。</summary>
    Duplicate = 2,

    /// <summary>本地已经有这个文件了（勾了「跳过已存在」）——统计里的「已存在」。</summary>
    AlreadyExists = 3,
}

/// <summary>批量下载计划里的一行。</summary>
/// <param name="Input">用户输入的原样文本。</param>
/// <param name="RelativePath">规范化后的相对路径（净路径，不含查询串）。</param>
/// <param name="LocalPath">本地要写到的绝对路径。</param>
/// <param name="Status">这一行的处理结果。</param>
/// <param name="Message">给用户看的原因（出问题时才有）。</param>
public sealed record BatchDownloadEntry(
    string Input,
    string RelativePath,
    string LocalPath,
    BatchLineStatus Status,
    string Message);

/// <summary>
/// 一次批量下载的统计文本（控制台 / 界面都靠它，对应原始工具那句
/// 「下载完成，总共 N 个，成功 M 个，404 有 K 个…」）。
/// </summary>
public sealed class BatchDownloadSummary
{
    public int Total { get; set; }
    public int Success { get; set; }
    public int NotFound { get; set; }
    public int Failed { get; set; }
    public int InvalidName { get; set; }
    public int Duplicate { get; set; }
    public int AlreadyExists { get; set; }
    public int NotDownloaded { get; set; }

    /// <summary>去掉所有 0 项之后的一句话统计（<paramref name="prefix"/> 放在最前面）。</summary>
    public string Describe(string prefix = "")
    {
        var parts = new List<string> { $"总共 {Total} 个" };
        if (Success > 0) parts.Add($"成功 {Success} 个");
        if (InvalidName > 0) parts.Add($"不规范名称 {InvalidName} 个");
        if (Duplicate > 0) parts.Add($"重复 {Duplicate} 个");
        if (AlreadyExists > 0) parts.Add($"已存在 {AlreadyExists} 个");
        if (NotDownloaded > 0) parts.Add($"无有效路径 {NotDownloaded} 个");
        if (NotFound > 0) parts.Add($"404 有 {NotFound} 个");
        if (Failed > 0) parts.Add($"其余失败 {Failed} 个");
        return prefix + string.Join("，", parts);
    }
}

/// <summary>
/// 批量下载的「预处理」：合规 → 去重 → 去掉已存在（纯逻辑，不碰网络）。
/// 对应原始 Python 项目 <c>RelatedDownloader.thread_batchDownload</c> 里的那段统计。
/// </summary>
public static class BatchDownloadPlanner
{
    /// <summary>
    /// 把用户粘贴的一堆相对路径整理成下载计划。
    /// </summary>
    /// <param name="lines">用户输入（一行一个相对路径，空行会被忽略）。</param>
    /// <param name="localFolder">游戏文件夹（写文件的根目录）。</param>
    /// <param name="skipExisting">勾了「不覆盖已有文件」时跳过本地已存在的。</param>
    /// <param name="fileExists">文件是否已存在的判定（自检里可以传假实现）。</param>
    public static (IReadOnlyList<BatchDownloadEntry> Entries, BatchDownloadSummary Summary) Plan(
        IEnumerable<string>? lines,
        string localFolder,
        bool skipExisting = true,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var entries = new List<BatchDownloadEntry>();
        var summary = new BatchDownloadSummary();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines ?? Array.Empty<string>())
        {
            var input = (raw ?? "").Trim();
            if (input.Length == 0) continue; // 空行 / 空白行直接忽略，不计入总数
            summary.Total++;

            var safe = GamePageParser.SanitizeBatchPath(input);
            if (safe.Length == 0)
            {
                summary.InvalidName++;
                entries.Add(new BatchDownloadEntry(input, "", "",
                    BatchLineStatus.InvalidPath, "路径不规范（含 .. 或空格 / 特殊字符）"));
                continue;
            }

            if (!seen.Add(safe))
            {
                summary.Duplicate++;
                entries.Add(new BatchDownloadEntry(input, safe, "",
                    BatchLineStatus.Duplicate, "与前面某一行重复"));
                continue;
            }

            var local = GamePageParser.JoinBatchLocalPath(localFolder, safe);
            if (local.Length == 0)
            {
                summary.InvalidName++;
                entries.Add(new BatchDownloadEntry(input, safe, "",
                    BatchLineStatus.InvalidPath, "路径转成本地文件名后仍不合法"));
                continue;
            }

            if (skipExisting && fileExists(local))
            {
                summary.AlreadyExists++;
                entries.Add(new BatchDownloadEntry(input, safe, local,
                    BatchLineStatus.AlreadyExists, "本地已存在"));
                continue;
            }

            entries.Add(new BatchDownloadEntry(input, safe, local, BatchLineStatus.Ready, ""));
        }

        return (entries, summary);
    }

    /// <summary>
    /// 生成一个不撞车的空目录路径（用于「装入文件夹」时建 <c>xxx_?123s</c>）。
    /// 目标已存在就依次试 <c>xxx_?123s2</c>、<c>s3</c>…；与 Python 版“同名就 +counter”一个意思。
    /// </summary>
    public static string MakeUniqueDirectory(string desiredPath)
    {
        if (!Directory.Exists(desiredPath) && !File.Exists(desiredPath)) return desiredPath;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = desiredPath + i;
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        return desiredPath + Guid.NewGuid().ToString("N")[..6];
    }
}
