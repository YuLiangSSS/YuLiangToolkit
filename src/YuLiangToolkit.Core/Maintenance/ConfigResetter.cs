using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Core.Maintenance;

/// <summary>重建列表里的一个功能配置：<c>Config\&lt;功能Id&gt;\</c> 的现状（有哪些文件 / 多大 / 有没有配置）。</summary>
public sealed class ConfigTarget
{
    /// <summary>功能（Id + 展示名）。</summary>
    public required ToolRef Tool { get; init; }

    /// <summary>配置小文件夹：<c>Config\&lt;功能Id&gt;</c>。</summary>
    public required string Directory { get; init; }

    /// <summary>目录里的文件名（只列一层，已排序）。</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>目录里文件总大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>有没有**有效**的配置 JSON（只有损坏备份 <c>*.bad-*</c> 不算）。</summary>
    public bool HasConfigFile { get; init; }

    /// <summary>一句话现状（「配置文件：config.json」/「还没有配置文件」…）。</summary>
    public string Detail { get; init; } = "";

    /// <summary>功能 Id。</summary>
    public string ToolId => Tool.Id;

    /// <summary>功能展示名。</summary>
    public string ToolName => Tool.Name;

    /// <summary>右侧大小文案。</summary>
    public string SizeText => SizeBytes > 0 ? FileSize.Format(SizeBytes) : "";
}

/// <summary>
/// 配置重建 / 查看（「主页设置 → 重建配置（恢复默认）」的引擎，纯逻辑、不碰界面，自检可直接调用）。
///
/// <para><b>为什么“重建”而不是“删了下次再说”</b>：配置是自动保存的，工具/设置页拿着内存里的配置对象；
/// 光删文件，下一次自动保存又把老值写回去。所以 <see cref="Reset"/> 会同时
/// ① 把内存里已加载的配置对象原地改回默认值 ② 删掉配置目录里的 JSON ③ 立刻写回默认 JSON。</para>
///
/// <para><b>扩展性</b>：列表按「已发现工具 + 外壳」逐个生成，<b>新工具不用登记</b> ——
/// 只要配置还放在 <c>Config\&lt;功能Id&gt;\</c>（框架默认就这样），重建功能自动包含它。</para>
/// </summary>
public static class ConfigResetter
{
    /// <summary>
    /// 列出这些功能的配置现状（不会创建任何文件 / 目录）。
    /// 外壳不是 <see cref="Tools.ITool"/>，调用方自己把 <c>new ToolRef("shell", "主页 / 外壳设置")</c> 放进列表。
    /// </summary>
    public static IReadOnlyList<ConfigTarget> List(IEnumerable<ToolRef>? tools)
    {
        var result = new List<ConfigTarget>();
        foreach (var tool in tools ?? Enumerable.Empty<ToolRef>())
        {
            if (string.IsNullOrWhiteSpace(tool.Id)) continue;
            var dir = AppPaths.ToolConfigDir(tool.Id);

            var names = new List<string>();
            long size = 0;
            var hasConfig = false;
            var hasBrokenBackup = false;

            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var path in Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(path);
                        if (name.Length == 0) continue;
                        names.Add(name);
                        try { size += new FileInfo(path).Length; } catch { /* 单个文件失败忽略 */ }

                        if (name.Contains(".bad-", StringComparison.OrdinalIgnoreCase)) hasBrokenBackup = true;
                        else if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) hasConfig = true;
                    }
                }
            }
            catch { /* 目录不可读忽略 */ }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);

            var detail = hasConfig
                ? "配置文件：" + string.Join("、", names.Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                                                  && !n.Contains(".bad-", StringComparison.OrdinalIgnoreCase)))
                : hasBrokenBackup
                    ? "只有损坏备份：" + string.Join("、", names)
                    : "还没有配置文件（打开功能时自动生成默认值）";

            result.Add(new ConfigTarget
            {
                Tool = tool,
                Directory = dir,
                Files = names,
                SizeBytes = size,
                HasConfigFile = hasConfig,
                Detail = detail,
            });
        }
        return result;
    }

    /// <summary>
    /// 重建一个功能的配置（= 恢复默认设置）：内存里的配置对象原地改回默认值 + 删配置文件 + 写回默认 JSON。
    /// 没加载过的功能只删文件，下次打开自动生成默认值。
    /// </summary>
    public static bool Reset(string toolId) => ToolConfigStore.ResetTool(toolId);

    /// <summary>批量重建（界面「重建选中配置」用）。返回成功 / 失败个数。</summary>
    public static (int Reset, int Failed) ResetMany(IEnumerable<string>? toolIds)
    {
        var reset = 0;
        var failed = 0;
        foreach (var toolId in (toolIds ?? Enumerable.Empty<string>())
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Reset(toolId.Trim())) reset++;
                else failed++;
            }
            catch { failed++; }
        }
        return (reset, failed);
    }
}
