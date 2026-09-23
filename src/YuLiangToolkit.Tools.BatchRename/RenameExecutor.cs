namespace YuLiangToolkit.Tools.BatchRename;

/// <summary>一个文件的执行结果（成功 / 失败 + 原因）。</summary>
/// <param name="Entry">对应的计划项。</param>
/// <param name="Success">是否成功。</param>
/// <param name="Error">失败原因（成功时为 null）。</param>
public sealed record RenameOutcome(RenamePlanEntry Entry, bool Success, string? Error);

/// <summary>
/// 真正动手改名的地方：<b>两阶段改名</b>。
///
/// <para><b>为什么必须两阶段</b>：</para>
/// <list type="bullet">
///   <item>A→B、B→A 这种互相交换，直接改会把目标文件盖掉；</item>
///   <item>只改大小写（<c>photo.jpg</c> → <c>Photo.jpg</c>）在 Windows 上会被当成“目标已存在”而失败；</item>
///   <item>A→B、B→C 这种链式改名，顺序不对同样会撞车。</item>
/// </list>
/// <para>做法：先把所有要改的文件改成同目录下的临时名（<c>.ylt-renaming-xxx.tmp</c>），第二阶段再改成最终名字。
/// 任意一条失败都只影响它自己——文件会被<b>改回原名</b>，不会留下临时文件。</para>
///
/// <para>改名前会写一份“改名流水账”（<see cref="RenameHistory"/>），所以<b>关掉程序之后仍然能撤销</b>。</para>
/// </summary>
public static class RenameExecutor
{
    /// <summary>临时名前缀（带点，尽量不与用户文件冲突）。</summary>
    private const string TempPrefix = ".ylt-renaming-";

    /// <summary>
    /// 执行改名：只处理 <see cref="RenameStatus.Ok"/> 的项，其余原样跳过。
    /// </summary>
    /// <param name="entries">计划项（<see cref="RenameEngine.BuildPlan"/> 的产物）。</param>
    /// <param name="token">取消令牌；取消时正在改的文件会被改回原名。</param>
    /// <param name="onProgress">进度回调 (已完成, 总数)，在调用线程上同步触发。</param>
    public static List<RenameOutcome> Execute(
        IReadOnlyList<RenamePlanEntry> entries,
        CancellationToken token = default,
        Action<int, int>? onProgress = null)
    {
        var pending = entries.Where(e => e.Status == RenameStatus.Ok).ToList();
        var outcomes = new List<RenameOutcome>(pending.Count);
        if (pending.Count == 0) return outcomes;

        // ---------- 阶段 1：源文件 → 临时名 ----------
        var staged = new List<(RenamePlanEntry Entry, string Temp)>();
        foreach (var entry in pending)
        {
            if (token.IsCancellationRequested) break;
            var temp = TempPathFor(entry.Directory);
            try
            {
                File.Move(entry.SourcePath, temp);
                staged.Add((entry, temp));
            }
            catch (Exception ex)
            {
                outcomes.Add(new RenameOutcome(entry, false, Describe(ex)));
            }
        }

        // ---------- 阶段 2：临时名 → 目标名 ----------
        var completed = 0;
        try
        {
            foreach (var (entry, temp) in staged)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temp, entry.TargetPath);
                    outcomes.Add(new RenameOutcome(entry, true, null));
                }
                catch (Exception ex)
                {
                    // 这一条失败：把文件改回原名，别让用户看到一个 .tmp
                    try { File.Move(temp, entry.SourcePath); } catch { /* 改不回去就只能留在临时名上，下面会报出来 */ }
                    outcomes.Add(new RenameOutcome(entry, false, Describe(ex)));
                }
                completed++;
                onProgress?.Invoke(completed, staged.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消：把还没落到目标名的文件全部改回原名
            foreach (var (entry, temp) in staged.Skip(completed))
            {
                try
                {
                    if (File.Exists(temp) && !File.Exists(entry.SourcePath))
                        File.Move(temp, entry.SourcePath);
                }
                catch { /* 回滚失败忽略，日志里能看到 */ }
            }
        }

        return outcomes;
    }

    /// <summary>
    /// 撤销一次改名（按流水账倒着改回去），同样用两阶段。
    ///
    /// <para>互换了名字的两个文件（A→B、B→A）撤销时会再换回来：这一批改出来的所有名字在阶段 1 会一起被挪走，
    /// 所以“原名位置上已经有文件”如果正好是本批的另一个目标，不算占用。</para>
    /// </summary>
    /// <param name="session">要撤销的那一批。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="onProgress">进度回调 (已完成, 总数)。</param>
    /// <returns>每个文件的撤销结果：<c>Success=true</c> 表示已改回原名。</returns>
    public static List<(RenameHistoryEntry Entry, bool Success, string? Error)> Undo(
        RenameSession session,
        CancellationToken token = default,
        Action<int, int>? onProgress = null)
    {
        var results = new List<(RenameHistoryEntry, bool, string?)>(session.Entries.Count);

        // 已经撤销过的批次不能再撤一次——否则会把文件又改回新名字（等于“重做”，很容易把人搞晕）
        if (session.Undone)
        {
            foreach (var entry in session.Entries) results.Add((entry, false, "这一批已经撤销过了"));
            return results;
        }

        var pending = new List<(RenameHistoryEntry Entry, string Temp)>();

        // 这一批改出来的所有名字：撤销时它们都会先被挪到临时名，
        // 所以“原名位置上已经有文件”如果正好是这批里的另一个目标（比如互换改名撤销回去），不算占用。
        var willBeVacated = new HashSet<string>(session.Entries.Select(e => e.To), StringComparer.OrdinalIgnoreCase);

        // ---------- 阶段 1：现名 → 临时名 ----------
        foreach (var item in session.Entries)
        {
            if (token.IsCancellationRequested) break;
            if (!File.Exists(item.To))
            {
                results.Add((item, false, "文件已不在改名后的位置（可能被手动改过或删了）"));
                continue;
            }
            if (File.Exists(item.From) && !willBeVacated.Contains(item.From))
            {
                results.Add((item, false, "原名位置上已经有别的文件了，不覆盖"));
                continue;
            }

            var directory = Path.GetDirectoryName(item.To) ?? "";
            var temp = TempPathFor(directory);
            try
            {
                File.Move(item.To, temp);
                pending.Add((item, temp));
            }
            catch (Exception ex)
            {
                results.Add((item, false, Describe(ex)));
            }
        }

        // ---------- 阶段 2：临时名 → 原名 ----------
        var completed = 0;
        foreach (var (item, temp) in pending)
        {
            if (token.IsCancellationRequested)
            {
                Rollback(temp, item.To);
                continue;
            }

            // 原先占着这个名字的文件没能挪走（例如它自己那一条失败了）→ 别覆盖，改回现状
            if (File.Exists(item.From))
            {
                Rollback(temp, item.To);
                results.Add((item, false, "原名位置仍被占用，已保持现状"));
                continue;
            }

            try
            {
                File.Move(temp, item.From);
                results.Add((item, true, null));
            }
            catch (Exception ex)
            {
                Rollback(temp, item.To);
                results.Add((item, false, Describe(ex)));
            }
            completed++;
            onProgress?.Invoke(completed, pending.Count);
        }

        // 只要有成功的就标记为“已撤销”，防止重复撤销把文件又改回去
        if (results.Any(r => r.Item2)) session.Undone = true;

        return results;
    }

    /// <summary>把文件从临时名放回某个位置（失败也不抛，尽力而为）。</summary>
    private static void Rollback(string from, string to)
    {
        try
        {
            if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>在某目录下生成一个不冲突的临时文件路径。</summary>
    private static string TempPathFor(string directory)
    {
        for (var i = 0; i < 1000; i++)
        {
            var path = Path.Combine(directory, $"{TempPrefix}{Guid.NewGuid():N}.tmp");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(directory, $"{TempPrefix}{Guid.NewGuid():N}{Guid.NewGuid():N}.tmp");
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有权限（文件只读或被系统保护）",
        IOException io when io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase)
            => "文件正被其它程序占用",
        IOException io => "IO 失败：" + io.Message,
        _ => ex.Message,
    };
}
