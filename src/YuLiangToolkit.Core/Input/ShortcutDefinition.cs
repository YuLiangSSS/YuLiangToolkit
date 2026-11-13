namespace YuLiangToolkit.Core.Input;

/// <summary>
/// 一条快捷键的「只读快照」：主页设置的快捷键汇总页按它渲染一行。
///
/// <para>它是**快照**而不是活对象：Provider 每次要改键 / 改形态 / 启停都通过
/// <see cref="SetCombo"/> / <see cref="SetEnabled"/> 这些回调写回自己的配置，
/// 然后注册表重新读一遍 provider 生成新快照 —— 界面永远只显示「配置里真实的样子」。</para>
///
/// <para><b>形态判定只有一条规则</b>：第二步是 <c>null</c> / 空 = 单步，有第二步 = 分步。
/// 拆分时会把第二步先填成第一步的副本（用户紧接着录新的就行），所以「刚拆开」不会退回单步。</para>
/// </summary>
public sealed class ShortcutDefinition
{
    /// <summary>构造（由 Provider 在自己体内创建，一般用 <see cref="ShortcutProviderAdapter"/>）。</summary>
    /// <param name="id">工具内唯一 Id（例如 <c>toggle-overlay</c>）。</param>
    /// <param name="toolId">所属工具 Id（<see cref="Tools.ITool.Id"/>）。</param>
    /// <param name="name">给人看的名字（开始计时 / 暂停 / 显示隐藏 Overlay…）。</param>
    /// <param name="primary">第一步（单步就是唯一那一步）。</param>
    /// <param name="secondary">第二步（单步传 null）。</param>
    /// <param name="enabled">这一条是否启用（禁用后不注册、按了没反应）。</param>
    /// <param name="canSplit">这个工具支不支持拆分 / 合并。</param>
    /// <param name="setCombo">写回组合（单步时第二段传 null）。</param>
    /// <param name="setEnabled">写回启用状态。</param>
    /// <param name="description">一句话说明（可以空）。</param>
    public ShortcutDefinition(
        string id,
        string toolId,
        string name,
        KeyCombo primary,
        KeyCombo? secondary,
        bool enabled,
        bool canSplit,
        Action<KeyCombo, KeyCombo?> setCombo,
        Action<bool> setEnabled,
        string description = "")
    {
        Id = id;
        ToolId = toolId;
        Name = name;
        Primary = primary;
        Secondary = secondary is { IsEmpty: false } ? secondary : null;
        Enabled = enabled;
        CanSplit = canSplit;
        SetCombo = setCombo;
        SetEnabledAction = setEnabled;
        Description = description;
    }

    /// <summary>工具内唯一（例如 <c>toggle-overlay</c>）。</summary>
    public string Id { get; }

    /// <summary>属于哪个工具（<see cref="Tools.ITool.Id"/>）。</summary>
    public string ToolId { get; }

    /// <summary>给人看的名字（开始计时 / 暂停 / 停止 / 显示隐藏 Overlay…）。</summary>
    public string Name { get; }

    /// <summary>一句话说明（可以空）。</summary>
    public string Description { get; }

    /// <summary>第一步（单步就是唯一那一步）。</summary>
    public KeyCombo Primary { get; }

    /// <summary>第二步（单步为 null）。</summary>
    public KeyCombo? Secondary { get; }

    /// <summary>现在是分步形态还是同时形态（有第二步 = 分步）。</summary>
    public ShortcutKind Kind => Secondary is null ? ShortcutKind.Single : ShortcutKind.Sequence;

    /// <summary>这条快捷键是否启用（禁用后不注册、按了没反应）。</summary>
    public bool Enabled { get; }

    /// <summary>这个工具支不支持分步（有些快捷键天生只该是单步，例如截图）。</summary>
    public bool CanSplit { get; }

    /// <summary>写回组合（分步传两段、单步第二段传 null）。</summary>
    public Action<KeyCombo, KeyCombo?> SetCombo { get; }

    /// <summary>写回启用状态（注册表内部用，界面调 <see cref="SetEnabled"/>）。</summary>
    public Action<bool> SetEnabledAction { get; }

    /// <summary>改启用状态（语义化封装，界面直接调）。</summary>
    public void SetEnabled(bool enabled) => SetEnabledAction(enabled);

    /// <summary>显示文本：单步 <c>Ctrl+F9</c>、分步 <c>Ctrl+U → Ctrl+K</c>、没设置 <c>（无）</c>。</summary>
    public string DisplayText
    {
        get
        {
            if (Primary.IsEmpty) return "（无）";
            return Secondary is null ? Primary.ToString() : $"{Primary} → {Secondary}";
        }
    }
}
