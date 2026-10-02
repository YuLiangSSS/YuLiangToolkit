using System.IO;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 自检用的临时目录/文件。
///
/// <para><b>为什么不直接用 <see cref="Path.GetTempPath"/></b>：自检有可能跑在“系统临时目录不可写”的环境里
/// （受限沙箱、被安全软件拦住的机器…）。那种情况下 <c>File.WriteAllBytes</c> 直接抛
/// <c>UnauthorizedAccessException</c>，整份自检在第一组断言就崩了 —— 而这个失败和被测代码毫无关系。
/// 所以这里先试系统临时目录，写不进去就退到<b>自检程序自己旁边</b>（那个目录必然可写，否则程序都跑不起来）。</para>
///
/// <para>真正的写盘探测（能建目录 + 能写文件才算数），不是“猜一猜”。</para>
/// </summary>
public static class SelfCheckScratch
{
    private static readonly Lazy<string> LazyRoot = new(ResolveRoot, isThreadSafe: true);

    /// <summary>可写的临时根目录（进程级解析一次）。</summary>
    public static string Root => LazyRoot.Value;

    /// <summary>在临时根下建一个带时间戳的子目录并返回它的完整路径。</summary>
    public static string MakeDirectory(string prefix)
    {
        var path = Path.Combine(Root, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}");
        try { Directory.CreateDirectory(path); } catch { /* 建不出来就用根目录 */ }
        return path;
    }

    /// <summary>在临时根下取一个可写的文件路径（每次调用都带唯一后缀，避免互相踩）。</summary>
    public static string MakeFile(string fileName)
        => Path.Combine(Root, $"{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");

    private static string ResolveRoot()
    {
        foreach (var candidate in Candidates())
        {
            if (IsWritable(candidate)) return candidate;
        }
        // 全都不行就用 exe 目录（再不行也没办法了，让它照常报错）
        return AppContext.BaseDirectory;
    }

    private static IEnumerable<string> Candidates()
    {
        // 1) 普通临时目录（正常机器上就是它）
        yield return Path.Combine(Path.GetTempPath(), "ylt-selfcheck");
        // 2) 自检程序旁边（受限环境下的兜底：这个目录必然可写）
        yield return Path.Combine(AppContext.BaseDirectory, "selfcheck-scratch");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
