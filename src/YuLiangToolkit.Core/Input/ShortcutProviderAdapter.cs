using YuLiangToolkit.Core.Input;

namespace YuLiangToolkit.Core.Input;
/// <summary>
/// 「我这些快捷键来自几个字符串配置项」的通用实现：把 <see cref="IShortcutProvider"/> 里
/// 那段重复的读写逻辑收在一处，工具只需要给出「名字 + 读当前值 + 写回新值」。
///
/// <para><b>为什么要它</b>：Kps / 录屏 / Live2D / 秒表四个工具的配置结构各不相同
/// （有的两条、有的四条，有的还有第二步），但「汇总页要什么」完全一样。
/// 各自写一遍 <see cref="IShortcutProvider"/> 容易漏（禁用位忘了、重注册忘了）。</para>
///
/// <para><b>用法</b>：工具持有 <see cref="ShortcutProviderAdapter"/>，在
/// <see cref="IShortcutProvider.GetShortcuts"/> 里返回 <c>adapter.Build(toolId)</c>；
/// 组合或启用位变了由 adapter 自己回调 <c>apply</c> 重新注册。</para>
/// </summary>
public sealed class ShortcutProviderAdapter
{
    /// <summary>一条快捷键在工具配置里的「位置」：读取 / 写回都由这四个委托完成。</summary>
    /// <param name="Id">工具内唯一 Id。</param>
    /// <param name="Name">显示名。</param>
    /// <param name="Description">说明（可以空）。</param>
    /// <param name="ReadPrimary">读第一步组合文本。</param>
    /// <param name="WritePrimary">写第一步组合文本。</param>
    /// <param name="ReadSecondary">读第二步（没有分步就返回空串）。</param>
    /// <param name="WriteSecondary">写第二步（不支持分步就传 null）。</param>
    /// <param name="ReadEnabled">读单条启用位（没有这个概念的传 <c>() =&gt; true</c>）。</param>
    /// <param name="WriteEnabled">写单条启用位（不支持就传 null）。</param>
    /// <param name="CanSplit">这个工具支不支持拆分 / 合并。</param>
    public sealed record Slot(
        string Id,
        string Name,
        Func<string?> ReadPrimary,
        Action<string> WritePrimary,
        Func<string?>? ReadSecondary,
        Action<string>? WriteSecondary,
        Func<bool>? ReadEnabled = null,
        Action<bool>? WriteEnabled = null,
        bool CanSplit = false,
        string Description = "");

    private readonly Func<IReadOnlyList<Slot>> _slots;
    private readonly Action _apply;
    private readonly Action? _notifyChanged;

    /// <summary>构造。</summary>
    /// <param name="slots">这个工具的全部快捷键槽位（每次调用都现读配置）。</param>
    /// <param name="apply">改了之后重新注册（通常就是工具自己的 <c>ApplyShortcuts</c> 内部实现）。</param>
    /// <param name="notifyChanged">通知界面刷新（工具的事件 Invoke）。</param>
    public ShortcutProviderAdapter(Func<IReadOnlyList<Slot>> slots, Action apply, Action? notifyChanged = null)
    {
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _notifyChanged = notifyChanged;
    }

    /// <summary>按当前配置生成 <see cref="ShortcutDefinition"/> 列表。</summary>
    public IReadOnlyList<ShortcutDefinition> Build(string toolId)
    {
        var result = new List<ShortcutDefinition>();

        foreach (var slot in _slots())
        {
            KeyCombo.TryParse(slot.ReadPrimary(), out var primary);

            // 第二步：null / 空串都表示「这条是单步」（拆分时会先填成第一步的副本，不会留空）
            KeyCombo? secondary = slot.ReadSecondary is not null
                                  && KeyCombo.TryParse(slot.ReadSecondary(), out var parsed)
                                  && !parsed.IsEmpty
                ? parsed
                : null;

            var definition = new ShortcutDefinition(
                slot.Id,
                toolId,
                slot.Name,
                primary,
                secondary,
                slot.ReadEnabled?.Invoke() ?? true,
                slot.CanSplit,
                setCombo: (combo, second) =>
                {
                    slot.WritePrimary(combo.IsEmpty ? "" : combo.ToString());
                    slot.WriteSecondary?.Invoke(second is null || second.IsEmpty ? "" : second.ToString());
                    _apply();
                    _notifyChanged?.Invoke();
                },
                setEnabled: enabled =>
                {
                    slot.WriteEnabled?.Invoke(enabled);
                    _apply();
                    _notifyChanged?.Invoke();
                },
                description: slot.Description);

            result.Add(definition);
        }

        return result;
    }
}
