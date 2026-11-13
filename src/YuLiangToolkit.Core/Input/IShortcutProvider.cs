namespace YuLiangToolkit.Core.Input;

/// <summary>
/// 「我这个工具有全局快捷键」——实现它，快捷键就会自动出现在主页设置 → 快捷键汇总里，
/// 可改键、可拆分 / 合并、可单条禁用、可整工具禁用。
///
/// <para><b>为什么要有这个接口</b>：设置页不能认识具体工具（外壳里没有、也不该有对
/// <c>StopwatchViewModel</c> 的引用）。工具自己声明、自己读写配置，
/// 设置页只负责渲染 <see cref="ShortcutDefinition"/> 列表。</para>
///
/// <para><b>实现要点</b>：</para>
/// <list type="bullet">
///   <item><see cref="GetShortcuts"/> 每次调用都返回**按当前配置实时生成的快照**（不要缓存），
///         设置页改完就重新读一次；</item>
///   <item>每个 <see cref="ShortcutDefinition"/> 的 <c>SetCombo</c> / <c>SetEnabled</c> 回调里，
///         写自己的配置 + 重新向引擎注册，然后调用 <see cref="ShortcutsChanged"/> 通知界面刷新；</item>
///   <item><b>必须线程安全地容忍重复调用</b>：改键时界面会连续改几次配置。</item>
/// </list>
///
/// <para><b>注册时机</b>：工具在程序启动时就该把启用的快捷键注册上（不依赖它的页面被打开），
/// 否则「软件在后台按快捷键」就不成立。参考 <see cref="Tools.IStartupTask"/>。</para>
/// </summary>
public interface IShortcutProvider
{
    /// <summary>这些快捷键属于哪个工具（<see cref="Tools.ITool.Id"/>）。</summary>
    string ShortcutToolId { get; }

    /// <summary>工具名（汇总页分组标题）。</summary>
    string ShortcutToolName { get; }

    /// <summary>工具所属分类路径（汇总页按它分组，例如 <c>["音游工具"]</c>）；空数组 = 根。</summary>
    IReadOnlyList<string> ShortcutCategoryPath { get; }

    /// <summary>整个工具的快捷键总开关（禁用后全部不注册，但各自的状态保留）。</summary>
    bool ShortcutsEnabled { get; set; }

    /// <summary>当前这些快捷键的实时快照。</summary>
    IReadOnlyList<ShortcutDefinition> GetShortcuts();

    /// <summary>某条快捷键的配置变了（设置页据此刷新，也便于工具自己刷新界面）。</summary>
    event EventHandler? ShortcutsChanged;

    /// <summary>把当前启用的快捷键重新注册到引擎（改配置 / 改开关后调用）。</summary>
    void ApplyShortcuts();
}
