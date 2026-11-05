namespace YuLiangToolkit.Tools.SoftwareUpdater.Winget;

/// <summary>列表里的批量勾选动作。</summary>
public enum SelectionAction
{
    /// <summary>全选：可见且可升级的全部勾上。</summary>
    All,

    /// <summary>全不选：可见项全部取消勾选。</summary>
    None,

    /// <summary>反选：可见且可升级的逐项取反。</summary>
    Invert,

    /// <summary>把「多选选中的行」勾上。</summary>
    CheckRows,

    /// <summary>把「多选选中的行」取消勾选。</summary>
    UncheckRows,
}

/// <summary>
/// 全选 / 全不选 / 反选 / 对多选行勾选的纯规则（不碰界面，自检直接断言）。
///
/// <para>约定：<paramref name="visible"/> 是「当前筛选后可见的行」在整份列表里的下标；
/// 批量动作只改这些行，被筛掉的行保持原样（升级前的确认框会列出全部勾选项，不会偷偷漏掉）。
/// 「不可升级」的行永远勾不上，避免把没有新版本的应用送进 winget。</para>
/// </summary>
public static class SelectionRules
{
    public static bool[] Apply(
        IReadOnlyList<bool> current,
        IReadOnlyList<int> visible,
        IReadOnlyList<bool> canUpgrade,
        SelectionAction action,
        IReadOnlyList<bool>? rowSelected = null)
    {
        var next = current.ToArray();
        if (visible.Count == 0) return next;

        foreach (var index in visible)
        {
            if (index < 0 || index >= next.Length) continue;
            var upgradable = index < canUpgrade.Count && canUpgrade[index];
            var selectedRow = rowSelected is not null && index < rowSelected.Count && rowSelected[index];

            switch (action)
            {
                case SelectionAction.All:
                    if (upgradable) next[index] = true;
                    break;
                case SelectionAction.None:
                    next[index] = false;
                    break;
                case SelectionAction.Invert:
                    if (upgradable) next[index] = !next[index];
                    break;
                case SelectionAction.CheckRows:
                    if (selectedRow && upgradable) next[index] = true;
                    break;
                case SelectionAction.UncheckRows:
                    if (selectedRow) next[index] = false;
                    break;
            }
        }
        return next;
    }

    /// <summary>
    /// 表头三态复选框状态：可见且可升级的全勾 = true，全不勾 = false，部分 = null。
    /// 没有可升级行时返回 false（点一下会变成全选）。
    /// </summary>
    public static bool? HeaderState(
        IReadOnlyList<bool> current,
        IReadOnlyList<int> visible,
        IReadOnlyList<bool> canUpgrade)
    {
        var checkedCount = 0;
        var total = 0;
        foreach (var index in visible)
        {
            if (index < 0 || index >= current.Count) continue;
            if (index >= canUpgrade.Count || !canUpgrade[index]) continue;
            total++;
            if (current[index]) checkedCount++;
        }

        if (total == 0 || checkedCount == 0) return false;
        return checkedCount == total ? true : null;
    }

    /// <summary>整份列表里勾中的行数（升级前的确认框 / 统计条用）。</summary>
    public static int CountChecked(IReadOnlyList<bool> current)
        => current.Count(flag => flag);
}
