using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.OsuMania;

/// <summary>
/// 「osu! 等级计算器」的配置（<c>Config\osu-level-calculator\config.json</c>）。
///
/// <para>等级计算本身没有参数，这里只记「上次打开的文件 / 文件夹」——下次点「选择文件…」时
/// 直接从上次的位置开始浏览，不用每次从盘符一层层点。文件列表本身**不持久化**（任务要求）。</para>
/// </summary>
public sealed class OsuLevelCalculatorConfig : ToolConfig
{
    /// <summary>工具 Id（同时是 Data / Config 下的小文件夹名，定了就别改）。</summary>
    public const string ToolId = "osu-level-calculator";

    /// <summary>上次选文件 / 拖入文件所在的文件夹（为空 = 没记录，用默认位置）。</summary>
    public string LastFolder { get; set; } = "";
}
