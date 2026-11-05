namespace YuLiangToolkit.App.ViewModels;

/// <summary>
/// 左侧「快捷功能栏」的**纯逻辑**：清洗 Id 列表、插入 / 移动 / 删除顺序。
///
/// <para>为什么不写在 <see cref="MainViewModel"/> 里：这段逻辑不碰 WPF，放在这里就能被自检直接断言
/// （拖拽本身要窗口，没法在无头自检里跑真鼠标；但“放到第几个位置之后列表长什么样”可以）。
/// 所有比较都忽略大小写（工具 Id 本身大小写不敏感，和 <c>ToolConfigStore</c> 的约定一致）。</para>
/// </summary>
public static class QuickSidebarList
{
    /// <summary>
    /// 清洗一份快捷栏 Id 列表：去掉空白 / 重复项；给了 <paramref name="availableToolIds"/> 时
    /// 再过滤掉已经不存在的工具（插件被删掉、工具改名后的旧 Id）。**保持原顺序**。
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? ids, IEnumerable<string>? availableToolIds = null)
    {
        var result = new List<string>();
        if (ids is null) return result;

        HashSet<string>? available = null;
        if (availableToolIds is not null)
            available = new HashSet<string>(availableToolIds, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ids)
        {
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            if (available is not null && !available.Contains(id)) continue;
            if (!seen.Add(id)) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>工具在列表里的下标（忽略大小写）；没有返回 -1。</summary>
    public static int IndexOf(IReadOnlyList<string>? ids, string? toolId)
    {
        if (ids is null || string.IsNullOrWhiteSpace(toolId)) return -1;
        for (var i = 0; i < ids.Count; i++)
        {
            if (string.Equals(ids[i], toolId, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>
    /// 把 <paramref name="toolId"/> 插入 / 移动到 <paramref name="insertIndex"/>。
    ///
    /// <para><b>insertIndex 是「还没把拖动项拿出去时」的落点下标</b>（0 ~ Count，插入到第几项之前；
    /// 等于 Count = 放到最后）。例：[A,B,C] 把 B 拖到 C 后面 → 落点 3，先移除 B 得到 [A,C]，
    /// 再把落点改成 2，结果 [A,C,B]；拖到原处（落点 2）则还是 [A,B,C]。</para>
    ///
    /// <para>新工具（不在列表里）直接按落点插入；已在列表里的工具就是重排，不会出现重复项。</para>
    /// </summary>
    public static List<string> Move(IReadOnlyList<string>? ids, string toolId, int insertIndex)
    {
        var list = ids is null ? new List<string>() : new List<string>(ids);
        var existing = IndexOf(list, toolId);
        if (existing >= 0) list.RemoveAt(existing);

        // 把「原列表里的落点」换算成「移除自己之后」的落点
        if (existing >= 0 && existing < insertIndex) insertIndex--;
        insertIndex = Math.Clamp(insertIndex, 0, list.Count);
        list.Insert(insertIndex, toolId);
        return list;
    }

    /// <summary>从列表里移除一个 Id（忽略大小写；不存在就原样返回一份拷贝）。</summary>
    public static List<string> Remove(IReadOnlyList<string>? ids, string? toolId)
    {
        var list = ids is null ? new List<string>() : new List<string>(ids);
        var index = IndexOf(list, toolId);
        if (index >= 0) list.RemoveAt(index);
        return list;
    }
}
