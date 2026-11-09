using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.RandomNumber;

/// <summary>
/// 「随机数生成器」的强类型配置 → <c>Config\random-number\config.json</c>。
///
/// <para><b>下限 / 上限 / 精确度一律存十进制文本</b>，不存 double / int：这样能写
/// <c>1000000000000000000000000000000</c> 这种超大数或 <c>1e-30</c> 这种极细步长，
/// 也不会在 JSON 往返里被浮点化、丢位。改一下自动落盘，没有保存按钮。</para>
/// </summary>
public sealed class RandomNumberConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名）。</summary>
    [JsonIgnore]
    public static string ToolId => "random-number";

    private string _lower = "0";

    /// <summary>下限（含），十进制文本。</summary>
    public string Lower
    {
        get => _lower;
        set => Set(ref _lower, value ?? "");
    }

    private string _upper = "100";

    /// <summary>上限（含），十进制文本。</summary>
    public string Upper
    {
        get => _upper;
        set => Set(ref _upper, value ?? "");
    }

    private string _step = "1";

    /// <summary>精确度（步长），十进制文本，必须 &gt; 0。</summary>
    public string Step
    {
        get => _step;
        set => Set(ref _step, value ?? "");
    }

    private int _count = 1;

    /// <summary>一次生成多少个（1 ~ 10000）。</summary>
    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    private bool _allowDuplicates = true;

    /// <summary>是否允许重复（关掉后改成不重复抽样）。</summary>
    public bool AllowDuplicates
    {
        get => _allowDuplicates;
        set => Set(ref _allowDuplicates, value);
    }

    private bool _sortResults;

    /// <summary>生成后按数值从小到大排序。</summary>
    public bool SortResults
    {
        get => _sortResults;
        set => Set(ref _sortResults, value);
    }

    private string _separator = "换行";

    /// <summary>复制 / 结果区用的分隔符（换行 / 逗号 / 空格 / 制表）。</summary>
    public string Separator
    {
        get => _separator;
        set => Set(ref _separator, value ?? "");
    }

    /// <summary>夹到合法范围的生成条数。</summary>
    [JsonIgnore]
    public int SafeCount => Core.ExactRandom.ClampCount(Count);

    /// <summary>夹到合法值的分隔符。</summary>
    [JsonIgnore]
    public string SafeSeparator => Core.ExactRandom.NormalizeSeparator(Separator);
}
