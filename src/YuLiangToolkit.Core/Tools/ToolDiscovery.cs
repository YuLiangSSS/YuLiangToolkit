using System.Reflection;

namespace YuLiangToolkit.Core.Tools;

/// <summary>
/// 工具自动发现机制（基于反射，零外部依赖）：
/// 1. 扫描本程序目录下所有 YuLiangToolkit.*.dll；
/// 2. 扫描 plugins 目录下的所有 dll（外部插件）；
/// 3. 收集所有实现 <see cref="ITool"/> 且有公共无参构造函数的类型并实例化。
/// </summary>
public static class ToolDiscovery
{
    public static IReadOnlyList<ITool> Discover()
    {
        var alreadyLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
            alreadyLoaded.Add(asm.Location);
        }

        var candidates = new List<Assembly>();
        var baseDir = AppContext.BaseDirectory;

        // 1) 主程序目录下的工具程序集（约定前缀）
        try
        {
            foreach (var dll in Directory.GetFiles(baseDir, "YuLiangToolkit.*.dll"))
            {
                if (alreadyLoaded.Add(dll)) candidates.Add(Assembly.LoadFrom(dll));
            }
        }
        catch { /* 单个程序集失败不影响整体 */ }

        // 2) plugins 目录（外部插件，命名自由）
        var pluginDir = Path.Combine(baseDir, "plugins");
        try
        {
            if (Directory.Exists(pluginDir))
            {
                foreach (var dll in Directory.GetFiles(pluginDir, "*.dll"))
                {
                    if (alreadyLoaded.Add(dll)) candidates.Add(Assembly.LoadFrom(dll));
                }
            }
        }
        catch { /* 忽略 */ }

        // 3) 汇总扫描
        var seen = new HashSet<Type>();
        var result = new List<ITool>();
        foreach (var asm in candidates.Concat(AppDomain.CurrentDomain.GetAssemblies()))
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.IsAbstract || t.IsInterface || t.ContainsGenericParameters) continue;
                    if (!typeof(ITool).IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                    if (!seen.Add(t)) continue;
                    result.Add((ITool)Activator.CreateInstance(t)!);
                }
            }
            catch (ReflectionTypeLoadException) { /* 某些程序集无法完全加载，跳过 */ }
            catch { /* 忽略单个程序集的异常 */ }
        }

        return result
            .OrderBy(t => t.Order)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
