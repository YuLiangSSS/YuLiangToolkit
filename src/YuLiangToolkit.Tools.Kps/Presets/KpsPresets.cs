using YuLiangToolkit.Tools.Kps.Models;

namespace YuLiangToolkit.Tools.Kps.Presets;

/// <summary>
/// 内置键位预设：每套预设是一份独立的「按行排列」的键位布局，切换预设不会丢掉之前那套的键位。
///
/// <para><b>布局模型是「按行排列」的简化模型</b>：<see cref="KpsKeyRow.Keys"/> 一行内从左到右，
/// 多行从上到下；<b>不是</b>自由 Canvas。DmNote 2.1.0 的 4 / 5 / 6 / 8 键模式实际是 8 / 10 / 12 / 12
/// 个槽位（含 LShift / RShift / 1 / 2 等侧键），本工具用简化的行布局近似：主键一行，侧键单独一行，
/// 不为了还原它把配置模型改复杂。</para>
///
/// <para>按键用 Windows 虚拟键码（<c>VK_*</c>，显示名见 <c>KeyNames</c>）；用户也可以把当前布局
/// 「另存为」自定义预设（存在 <c>config.json</c> 的 <c>customPresets</c> 里，见 <c>KpsConfig</c>）。</para>
/// </summary>
public static class KpsPresets
{
    /// <summary>默认预设 Id（首次运行 / 配置里写的预设不存在时用它）：<c>key-4</c>。</summary>
    public const string DefaultId = "key-4";

    // 常用的虚拟键码（避免到处写魔法数字；字母 / 数字键直接用 ASCII 码）
    private const int VkSpace = 0x20;
    private const int VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;
    private const int VkLShift = 0xA0, VkRShift = 0xA1;
    private const int VkSemicolon = 0xBA; // 分号 ;（OEM_1）

    /// <summary>所有内置预设（顺序 = 界面上下拉框顺序；Id 稳定，别随便改）。</summary>
    public static IReadOnlyList<KpsPreset> BuiltIn { get; } = Build();

    /// <summary>默认预设（4K，DJMAX / osu!mania 4K 常见手位 D F J K）。</summary>
    public static KpsPreset Default
        => BuiltIn.First(p => string.Equals(p.Id, DefaultId, StringComparison.OrdinalIgnoreCase));

    /// <summary>按 Id 找内置预设（大小写不敏感）；找不到返回 null。</summary>
    public static KpsPreset? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return BuiltIn.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>深拷贝一套行（配置默认值 / 切预设用，防止两个预设共享同一批对象）。</summary>
    public static List<KpsKeyRow> CloneRows(IEnumerable<KpsKeyRow> rows)
        => rows.Select(row => row.Clone()).ToList();

    /// <summary>
    /// 创建「一个键」的便捷方法：<paramref name="label"/> 为空时界面上用 <c>KeyNames.Display</c> 的键名；
    /// <paramref name="widthUnits"/> 是宽度倍数（1 = 标准，空格这类长键可以给 2）。
    /// </summary>
    public static KpsKey Key(int virtualKey, string label = "", int widthUnits = 1)
        => new() { VirtualKey = virtualKey, Label = label, WidthUnits = widthUnits };

    private static IReadOnlyList<KpsPreset> Build()
    {
        return new List<KpsPreset>
        {
            // ---------- 主预设：4K / 5K / 6K / 8K（键盘中段对称手位） ----------

            // 4K（默认）：D F J K —— 左手食指 / 中指、右手食指 / 中指，双手间距适中、好按，
            // 是 DJMAX 4B 与 osu!mania 4K 最常见的默认手位。
            Preset("key-4", "4K（D F J K）", Row(
                Key(0x44, "D"),
                Key(0x46, "F"),
                Key(0x4A, "J"),
                Key(0x4B, "K"))),

            // 5K：D F [空格] J K —— 中间用空格当中键（宽 2 格），左右各两键，双手对称。
            Preset("key-5", "5K（D F 空格 J K）", Row(
                Key(0x44, "D"),
                Key(0x46, "F"),
                Key(VkSpace, "空格", 2),
                Key(0x4A, "J"),
                Key(0x4B, "K"))),

            // 6K：S D F J K L —— 左右手各三键，和 osu!mania 6K 的常见手位一致。
            Preset("key-6", "6K（S D F J K L）", Row(
                Key(0x53, "S"),
                Key(0x44, "D"),
                Key(0x46, "F"),
                Key(0x4A, "J"),
                Key(0x4B, "K"),
                Key(0x4C, "L"))),

            // 8K：A S D F J K L ; —— 左手四键、右手四键，分号是 osu!mania 8K 的右手小指位。
            Preset("key-8", "8K（A S D F J K L ;）", Row(
                Key(0x41, "A"),
                Key(0x53, "S"),
                Key(0x44, "D"),
                Key(0x46, "F"),
                Key(0x4A, "J"),
                Key(0x4B, "K"),
                Key(0x4C, "L"),
                Key(VkSemicolon, ";"))),

            // 方向键：上单独一行，下排「左 ↓ 右」——和键盘上的实际排列一致；
            // 适合玩以方向键为主的音游 / 休闲游戏时看按下。
            Preset("arrows", "方向键（↑ ↓ ← →）",
                Row(Key(VkUp, "↑")),
                Row(Key(VkLeft, "←"), Key(VkDown, "↓"), Key(VkRight, "→"))),

            // ---------- 附加预设：带侧键（近似 DmNote 8 / 12 槽位的简化版） ----------

            // 4K + 侧键：DmNote 的 4 键模式实际有 8 个槽位（主键 + LShift / RShift 等侧键）。
            // 行布局里把侧键放到主键上、下各一行（宽 2 格），中间仍是 D F J K。
            Preset("key-4-side", "4K + 侧键（D F J K · LShift / RShift）",
                Row(Key(VkLShift, "LShift", 2)),
                Row(
                    Key(0x44, "D"),
                    Key(0x46, "F"),
                    Key(0x4A, "J"),
                    Key(0x4B, "K")),
                Row(Key(VkRShift, "RShift", 2))),

            // 8K + 侧键：主键 A S D F J K L ;，上、下各一行侧键（简化行布局）。
            Preset("key-8-side", "8K + 侧键（A S D F J K L ; · LShift / RShift）",
                Row(Key(VkLShift, "LShift", 2)),
                Row(
                    Key(0x41, "A"),
                    Key(0x53, "S"),
                    Key(0x44, "D"),
                    Key(0x46, "F"),
                    Key(0x4A, "J"),
                    Key(0x4B, "K"),
                    Key(0x4C, "L"),
                    Key(VkSemicolon, ";")),
                Row(Key(VkRShift, "RShift", 2))),
        };
    }

    private static KpsPreset Preset(string id, string name, params KpsKeyRow[] rows)
        => new() { Id = id, Name = name, BuiltIn = true, Rows = rows.ToList() };

    private static KpsKeyRow Row(params KpsKey[] keys) => new() { Keys = keys.ToList() };
}
