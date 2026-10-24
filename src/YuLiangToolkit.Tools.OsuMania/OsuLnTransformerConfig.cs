using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.OsuMania;

/// <summary>
/// 「osu! LN 转换器」的参数（自动持久化到 <c>Config\osu-ln-transformer\config.json</c>）。
///
/// <para>字段默认值与旧版 LNTransformer 界面控件的默认值一致：Level 3、LN% 100、Divide 4、
/// Column 10、Gap 12、随机种子 114514、Ignore 勾选、Fix Error 勾选、Original LN 不勾选。</para>
///
/// <para><b>与旧版的一个有意的差异</b>：旧版永远把 OD 写进谱面（默认 0，会把谱面 OD 写坏）；
/// 新版默认“保留原谱 OD”，只有勾上 <see cref="ModifyOverallDifficulty"/> 才用
/// <see cref="OverallDifficulty"/> 覆盖（见 <c>LnTransformerOptions.KeepOverallDifficulty</c>）。</para>
/// </summary>
public sealed class OsuLnTransformerConfig : ToolConfig
{
    /// <summary>工具 Id（同时是 Data / Config 下的小文件夹名，定了就别改）。</summary>
    public const string ToolId = "osu-ln-transformer";

    // 注意：滑块等需要“实时显示数值”的字段用 SetAndSave（见类注释）。

    // ---- 转换参数 ----

    /// <summary>转换等级（-3~10，默认 3）。各档含义见 <see cref="LevelHint"/>。</summary>
    private int _level = 3;
    public int Level
    {
        get => _level;
        set => SetAndSave(ref _level, value);
    }

    /// <summary>转换为 LN 的占比（0~100，默认 100）。</summary>
    private double _percentage = 100;
    public double Percentage
    {
        get => _percentage;
        set => SetAndSave(ref _percentage, value);
    }

    /// <summary>LN 时长按 1/Divide 拍（1~16，默认 4）。</summary>
    private int _divide = 4;
    public int Divide
    {
        get => _divide;
        set => SetAndSave(ref _divide, value);
    }

    /// <summary>转换多少条轨道（0~10，默认 10 = 全部）。</summary>
    private int _column = 10;
    public int Column
    {
        get => _column;
        set => SetAndSave(ref _column, value);
    }

    /// <summary>每隔多少行 Note 重抽一次转换轨道（0~20，默认 12；0 = 轨道不变）。</summary>
    private int _gap = 12;
    public int Gap
    {
        get => _gap;
        set => SetAndSave(ref _gap, value);
    }

    /// <summary>随机种子（默认 "114514"）：固定种子结果可复现；清空则每次随机。</summary>
    public string Seed { get; set; } = "114514";

    // ---- 谱面属性 ----

    /// <summary>是否修改谱面 OD（**默认不勾 = 保留原谱 OD**）。</summary>
    public bool ModifyOverallDifficulty { get; set; }

    /// <summary>勾选 <see cref="ModifyOverallDifficulty"/> 时写回谱面的 OD（默认 8）。</summary>
    public double OverallDifficulty { get; set; } = 8;

    // ---- 高级选项 ----

    /// <summary>忽略已经转换过的谱面（Creator / 难度名带 LNTransformer 标记），默认勾选。</summary>
    public bool Ignore { get; set; } = true;

    /// <summary>保留原谱面里已有的长条（旧版 Original LN），默认不勾选。</summary>
    public bool OriginalLN { get; set; }

    /// <summary>用反算修复 LN 尾巴的细小误差（旧版 Fix Error），默认勾选。</summary>
    public bool FixError { get; set; } = true;

    /// <summary>新谱师名（配合 <see cref="UseCreator"/>；不勾选时追加 " &amp; LNTransformer"）。</summary>
    public string Creator { get; set; } = "";

    /// <summary>是否用 <see cref="Creator"/> 替换原谱师。</summary>
    public bool UseCreator { get; set; }

    // ---- 源键数过滤：旧版 1k~10k 复选框；一个都不勾 = 全部转换 ----

    public bool Filter1K { get; set; }
    public bool Filter2K { get; set; }
    public bool Filter3K { get; set; }
    public bool Filter4K { get; set; }
    public bool Filter5K { get; set; }
    public bool Filter6K { get; set; }
    public bool Filter7K { get; set; }
    public bool Filter8K { get; set; }
    public bool Filter9K { get; set; }
    public bool Filter10K { get; set; }

    /// <summary>把 10 个勾选框拼成引擎要的 <c>CheckKeys</c> 列表；空列表 = 全部（旧版语义）。</summary>
    public IReadOnlyList<int> BuildCheckKeys()
    {
        var keys = new List<int>(10);
        if (Filter1K) keys.Add(1);
        if (Filter2K) keys.Add(2);
        if (Filter3K) keys.Add(3);
        if (Filter4K) keys.Add(4);
        if (Filter5K) keys.Add(5);
        if (Filter6K) keys.Add(6);
        if (Filter7K) keys.Add(7);
        if (Filter8K) keys.Add(8);
        if (Filter9K) keys.Add(9);
        if (Filter10K) keys.Add(10);
        return keys;
    }

    /// <summary>
    /// 各档 Level 的含义（旧版说明整理成中文）：-3 ~ -1 是“特殊档”，
    /// 其中 -3 时 Percentage / Column / Gap 三项不生效，界面上要提示清楚。
    /// </summary>
    public const string LevelHint =
        "-3 = Hold off：把所有 LN 压成普通键（输出不含 LN）；这一档 LN% / 转换轨道 / 间隔都不生效\n" +
        "-2 = 完全随机：LN 长度在「到下一个音」之间随机取\n" +
        "-1 = 随机等分：把两音之间按 1/Divide 拍等分后随机取一份\n" +
        "0 = 常规 LN：按 1/100 节拍左右的正态分布取长度（最像手写 LN）\n" +
        "1~7 = 从轻到密的 LN：mu = Level×11，数值越大 LN 越长越密\n" +
        "8 / 9 = 更密 / 最密（σ 分别放宽到 0.9 / 1.0）\n" +
        "10 = Invert（反相）：原本没有 LN 的地方补上 LN，已有 LN 的保留策略由 Original LN 决定";
}
