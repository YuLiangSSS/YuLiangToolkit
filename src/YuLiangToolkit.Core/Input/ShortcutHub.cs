using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Core.Input;

/// <summary>
/// 快捷键体系的全局入口：外壳启动时把引擎与注册表放进来，各工具通过它注册自己的快捷键。
///
/// <para><b>为什么要一个静态入口</b>：工具（在各自的项目里）需要「按当前配置重新注册快捷键」，
/// 但它拿不到外壳持有 <see cref="GlobalShortcutEngine"/>。和 <see cref="NotificationCenter"/>
/// 一样，用一个静态入口注入，工具只依赖 Core，不用反向依赖外壳。</para>
///
/// <para><b>重入保护</b>：重新注册会停掉并重建所有绑定，而注册过程中工具配置的 setter 可能又
/// 触发一次「重新注册」。这里用一个标志把嵌套调用压成一次，避免无限递归。</para>
/// </summary>
public static class ShortcutHub
{
    private static bool _applying;

    /// <summary>全局引擎（外壳启动时注入；为 null 时所有注册请求静默忽略）。</summary>
    public static GlobalShortcutEngine? Engine { get; private set; }

    /// <summary>全局注册表（汇总页 / 冲突检测用它）。</summary>
    public static ShortcutRegistry? Registry { get; private set; }

    /// <summary>注入引擎与注册表（外壳启动时调一次）。</summary>
    public static void Initialize(GlobalShortcutEngine engine, ShortcutRegistry registry)
    {
        Engine = engine;
        Registry = registry;
    }

    /// <summary>把某个工具登记进注册表（工具被发现后调一次）。</summary>
    public static void AddProvider(IShortcutProvider provider)
    {
        if (provider is null || Registry is null) return;
        Registry.RegisterProvider(provider);
    }

    /// <summary>重新把「所有工具当前启用的快捷键」注册到引擎（改键 / 启停后调）。</summary>
    public static void ApplyAll()
    {
        if (_applying) return;
        if (Engine is null || Registry is null) return;

        _applying = true;
        try
        {
            Registry.ApplyTo(Engine);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>释放（程序退出时调：注销所有热键并卸下钩子）。</summary>
    public static void Shutdown()
    {
        try { Engine?.Dispose(); } catch { /* 退出流程忽略 */ }
        Engine = null;
        Registry = null;
    }
}
