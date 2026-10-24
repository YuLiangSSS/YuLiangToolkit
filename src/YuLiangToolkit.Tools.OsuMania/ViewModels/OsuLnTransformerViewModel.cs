using YuLiangToolkit.Tools.OsuMania.Core;

namespace YuLiangToolkit.Tools.OsuMania.ViewModels;

/// <summary>
/// 「osu! LN 转换器」的 ViewModel：参数收集 + 调 <see cref="OsuFileBatch.RunLnTransformer"/> +
/// 进度 / 日志 / 取消（通用部分都在 <see cref="OsuTransformViewModelBase"/>）。
/// </summary>
public sealed class OsuLnTransformerViewModel : OsuTransformViewModelBase
{
    public OsuLnTransformerViewModel() : base(OsuLnTransformerConfig.ToolId)
    {
        Cfg = LoadConfig<OsuLnTransformerConfig>();
        ActionName = "转换";

        HelpText =
            """
            把普通音符批量变成长条（LN）。各 Level 档位的含义：

            · -3 Hold off：先把所有 LN 压成普通键（只保留普通键）。这一档下 LN% / 转换轨道 / 间隔都不起作用。
            · -2 完全随机：LN 长度在「当前音 ~ 下一个音」之间完全随机取。
            · -1 随机等分：把两音之间按 1/Divide 拍等分，随机取其中一份。
            · 0 常规 LN：按 1/100 拍左右的正态分布取长度，最接近手写 LN。
            · 1~7 从轻到密：mu = Level×11，数值越大 LN 越长、越密。
            · 8 / 9 更密 / 最密：σ 分别放宽到 0.9 / 1.0。
            · 10 Invert 反相：原本没 LN 的地方补 LN（已有 LN 是否保留由「保留原 LN」决定）。

            其它参数：
            · LN%：转换概率 0~100，100 = 全部符合条件的普通键都变 LN。
            · Divide：LN 时长按 1/Div 拍计算（1~16）。
            · 转换轨道：只在这些轨道上转 LN（0~10，超过谱面键数按全部算）。
            · 间隔（Gap）：每隔多少行音符重抽一次转换轨道（0 = 轨道不变）。
            · 保留原 LN：勾选后原谱面里的长条原样保留，不再参与转换。
            · 修复尾判误差：把 LN 尾巴对齐到 1/Divide 拍的整数格上，减少游戏里尾判的细小误差。
            · 忽略已转换：谱师 / 难度名里带 LNTransformer 标记的谱面直接跳过，避免重复转换。
            · 修改 OD：默认不勾 = 保留原谱 OD；勾选后才用右边填的数值覆盖（默认会把 OD 写成输入框的值 0，容易写坏谱面）。
            · 源键数过滤：勾了哪些就只转哪些键数；一个都不勾 = 全部转换。

            输出位置：新 .osu 写在「原谱面所在目录」，文件名由 艺术家 - 标题 (谱师) [难度名] 拼成，
            难度名会加 [LN-Lv3-C10] 前缀，所以新文件名与原来不同，不会覆盖原文件。
            """;

        Log($"配置文件：{Cfg.FilePath}");
        Log("用法：拖入 .osu 或文件夹 → 调参数 → 点「开始转换」；新谱面写在原谱面所在目录（不覆盖原文件）");
        if (IsFirstRun) Log("首次运行：已生成默认参数（Config\\osu-ln-transformer\\config.json）");

        if (OsuDemoData.Enabled) SeedDemoFiles();
    }

    /// <summary>界面绑定 / 自动保存的参数。</summary>
    public OsuLnTransformerConfig Cfg { get; }

    /// <summary>Level 滑块的 ToolTip（各档含义整理成中文，-3 档的无效参数也写明）。</summary>
    public string LevelTooltip => OsuLnTransformerConfig.LevelHint;

    protected override string? ValidateOptions()
    {
        if (Cfg.Level is < -3 or > 10)
        {
            return "Level 需在 -3 ~ 10 之间。";
        }

        if (!double.IsFinite(Cfg.OverallDifficulty) || Cfg.OverallDifficulty is < 0 or > 10)
        {
            return "OverallDifficulty 需在 0~10 之间。";
        }

        return null;
    }

    protected override OsuBatchResult RunBatch(IReadOnlyList<string> paths, IProgress<OsuBatchProgress> progress,
        CancellationToken cancellationToken)
    {
        var options = new LnTransformerOptions
        {
            SeedText = Cfg.Seed,
            Level = Math.Clamp(Cfg.Level, -3, 10),
            Percentage = Math.Clamp(Cfg.Percentage, 0, 100),
            Divide = Math.Clamp(Cfg.Divide, 1, 16),
            Column = Math.Clamp(Cfg.Column, 0, 10),
            Gap = Math.Clamp(Cfg.Gap, 0, 20),
            OverallDifficulty = Math.Clamp(Cfg.OverallDifficulty, 0, 10),
            // 勾「修改 OD」才覆盖；默认保留原谱 OD（早期版本 bug 的修正，见配置类注释）。
            KeepOverallDifficulty = !Cfg.ModifyOverallDifficulty,
            Creator = Cfg.Creator,
            CreatorIsChecked = Cfg.UseCreator,
            Ignore = Cfg.Ignore,
            OriginalLN = Cfg.OriginalLN,
            FixError = Cfg.FixError,
            CheckKeys = Cfg.BuildCheckKeys(),
        };

        // 内部是串行逐个 TryLoad → Apply → Save，并汇报进度；单个文件失败不会中断整批。
        return OsuFileBatch.RunLnTransformer(paths, options, progress, cancellationToken);
    }

    protected override void OnBatchCompleted(OsuBatchResult result)
    {
        if (result.ConvertedCount > 0)
        {
            Log("提示：新谱面已写在源文件所在目录，难度名带 [LN-Lv…] 前缀；勾了「忽略已转换」时再次转换会跳过它们。");
        }
    }
}
