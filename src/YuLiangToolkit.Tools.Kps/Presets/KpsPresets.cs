using YuLiangToolkit.Tools.Kps.Models;

namespace YuLiangToolkit.Tools.Kps.Presets;

/// <summary>
/// 内置键位预设（与 DmNote 的 4key / 5key / 6key / 8key 键模式对应）：
/// 每个预设是一套独立的键位布局，切换预设不会丢掉之前那套的键位。
///
/// <para>按键用 Windows 虚拟键码（<c>VK_*</c>）；用户也可以把当前布局「另存为」自定义预设
/// （存在 <c>config.json</c> 的 <c>customPresets</c> 里，见 <c>KpsConfig</c>）。</para>
/// </summary>
public static class KpsPresets
{
    /// <summary>默认预设 Id（首次运行 / 配置里写的预设不存在时用它）。</summary>
    public const string DefaultId = "key-4";

    // 常用的虚拟键码（避免到处写魔法数字）
    private const int VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkEscape = 0x1B, VkSpace = 0x20;
    private const int VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;

    /// <summary>所有内置预设（顺序 = 界面上下拉框顺序）。</summary>
    public static IReadOnlyList<KpsPreset> BuiltIn { get; } = Build();

    /// <summary>默认预设（4K，DJMAX 风格 D F J K）。</summary>
    public static KpsPreset Default => BuiltIn.First(p => p.Id == DefaultId);

    /// <summary>按 Id 找内置预设；找不到返回 null。</summary>
    public static KpsPreset? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return BuiltIn.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>深拷贝一套行（配置默认值 / 切预设用，防止两个预设共享同一批对象）。</summary>
    public static List<KpsKeyRow> CloneRows(IEnumerable<KpsKeyRow> rows)
        => rows.Select(row => row.Clone()).ToList();

    /// <summary>创建「一个键」的便捷方法：<paramref name="label"/> 为空时界面上用键名。</summary>
    public static KpsKey Key(int virtualKey, string label = "", int widthUnits = 1)
        => new() { VirtualKey = virtualKey, Label = label, WidthUnits = widthUnits };

    private static IReadOnlyList<KpsPreset> Build()
    {
        return new List<KpsPreset>
        {
            Preset("key-4", "4K（D F J K）", Row(
                Key(0x44, "D"), Key(0x46, "F"), Key(0x4A, "J"), Key(0x4B, "K"))),

            Preset("key-5", "5K（D F 空格 J K）", Row(
                Key(0x44, "D"), Key(0x46, "F"), Key(VkSpace, "空格", 2), Key(0x4A, "J"), Key(0x4B, "K"))),

            Preset("key-6", "6K（S D F J K L）", Row(
                Key(0x53, "S"), Key(0x44, "D"), Key(0x46, "F"),
                Key(0x4A, "J"), Key(0x4B, "K"), Key(0x4C, "L"))),

            Preset("key-8", "8K（A S D F J K L ;）", Row(
                Key(0x41, "A"), Key(0x53, "S"), Key(0x44, "D"), Key(0x46, "F"),
                Key(0x4A, "J"), Key(0x4B, "K"), Key(0x4C, "L"), Key(0xBA, ";"))),

            Preset("arrows", "方向键（↑ ↓ ← →）",
                Row(Key(VkUp, "↑")),
                Row(Key(VkLeft, "←"), Key(VkDown, "↓"), Key(VkRight, "→"))),
        };
    }

    private static KpsPreset Preset(string id, string name, params KpsKeyRow[] rows)
        => new() { Id = id, Name = name, BuiltIn = true, Rows = rows.ToList() };

    private static KpsKeyRow Row(params KpsKey[] keys) => new() { Keys = keys.ToList() };
}
