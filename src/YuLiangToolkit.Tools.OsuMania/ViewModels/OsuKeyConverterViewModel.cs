using YuLiangToolkit.Tools.OsuMania.Core;

namespace YuLiangToolkit.Tools.OsuMania.ViewModels;

/// <summary>
/// 「osu! Key 转换器」的 ViewModel：参数收集 + 调 <see cref="OsuFileBatch.RunKeyConverter"/> +
/// 进度 / 日志 / 取消（通用部分都在 <see cref="OsuTransformViewModelBase"/>）。
/// </summary>
public sealed class OsuKeyConverterViewModel : OsuTransformViewModelBase
{
    public OsuKeyConverterViewModel() : base(OsuKeyConverterConfig.ToolId)
    {
        Cfg = LoadConfig<OsuKeyConverterConfig>();
        ActionName = "转换";

        HelpText =
            """
            只支持「低键数 → 高键数」：目标键数必须大于源谱面键数（源 1~9K，目标 2~10K）。

            · 目标键数：想转成几 K。转出来的新谱面文件名会带 [4To7C] 这样的前缀，方便区分。
            · 转换间隔（Gap）：0~20。数字越大，参与一次「复制列 / 插空白列」的音符段越短；0 = 整张谱一次处理。
            · 去子弹（Clean）：0~16。清掉 1/Clean 拍内的二连音（例如 4 = 清掉 1/4 拍内重复的键）；0 = 不去重。
            · 空白列（Blank）：0~9。随机拿若干复制出来的列当空白列，用来降低高键数谱面的密度，最多到「目标键数 - 源键数」。
            · 随机种子：默认 114514。固定种子 → 每个文件转出来的结果都一样、可复现；清空则每次随机。
            · 替换谱师：不勾选时旧版会在原谱师后追加 " & KeyConverter"，勾选且填了名字才替换（新谱面文件名也会跟着变，保证不覆盖原谱）。
            · 源键数过滤：勾了哪些就只转哪些源键数；一个都不勾 = 全部转换。

            输出位置：新 .osu 写在「原谱面所在目录」，文件名由 艺术家 - 标题 (谱师) [难度名] 拼成，
            因为工具会改 Difficulty / Creator，正常不会覆盖原文件（原文件仍然保留）。
            """;

        Log($"配置文件：{Cfg.FilePath}");
        Log("用法：拖入 .osu 或文件夹 → 调参数 → 点「开始转换」；新谱面写在原谱面所在目录（不覆盖原文件）");
        if (IsFirstRun) Log("首次运行：已生成默认参数（Config\\osu-key-converter\\config.json）");

        if (OsuDemoData.Enabled) SeedDemoFiles();
    }

    /// <summary>界面绑定 / 自动保存的参数。</summary>
    public OsuKeyConverterConfig Cfg { get; }

    protected override string? ValidateOptions()
    {
        if (Cfg.TargetKeys is < 2 or > 10)
        {
            return "目标键数需在 2~10 之间。";
        }
        return null;
    }

    protected override OsuBatchResult RunBatch(IReadOnlyList<string> paths, IProgress<OsuBatchProgress> progress,
        CancellationToken cancellationToken)
    {
        var options = new KeyConverterOptions
        {
            SeedText = Cfg.Seed,
            TargetKeys = Math.Clamp(Cfg.TargetKeys, 2, 10),
            Blank = Cfg.Blank,
            Clean = Cfg.Clean,
            Gap = Cfg.Gap,
            Creator = Cfg.Creator,
            CreatorIsChecked = Cfg.UseCreator,
            CheckKeys = Cfg.BuildCheckKeys(),
        };

        // 内部是串行逐个 TryLoad → Apply → Save，并汇报进度；单个文件失败不会中断整批。
        return OsuFileBatch.RunKeyConverter(paths, options, progress, cancellationToken);
    }

    protected override void OnBatchCompleted(OsuBatchResult result)
    {
        if (result.ConvertedCount > 0)
        {
            Log("提示：新谱面已写在源文件所在目录；工具会改难度名前缀与谱师，所以文件名与原来不同，不会覆盖原谱。");
        }
    }
}
