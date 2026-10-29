using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.OsuMania;

/// <summary>
/// 「osu! Key 转换器」的参数（自动持久化到 <c>Config\osu-key-converter\config.json</c>）。
///
/// <para>字段默认值与早期版本 KeyConverter 界面控件的默认值一致：目标键数 8、间隔 Gap 10、
/// 去子弹 Clean 4、空白列 Blank 0、随机种子 114514、谱师替换不勾选。</para>
///
/// <para><b>为什么滑块相关的属性写成 <c>SetAndSave</c></b>：绑在滑块旁边的数值文本要“拖动时实时变化”，
/// 自动属性只保证最终会存盘、不发界面通知；写成 <c>SetAndSave</c>（= 基类 <c>Set</c>）才能让同一属性的
/// 其它绑定跟着刷新（见 AGENTS.md）。</para>
/// </summary>
public sealed class OsuKeyConverterConfig : ToolConfig
{
    /// <summary>工具 Id（同时是 Data / Config 下的小文件夹名，定了就别改）。</summary>
    public const string ToolId = "osu-key-converter";

    /// <summary>目标键数（早期版本 Key 滑条 2~10，默认 8）。必须是“低转高”：目标键数要大于源谱面键数。</summary>
    private int _targetKeys = 8;
    public int TargetKeys
    {
        get => _targetKeys;
        set => SetAndSave(ref _targetKeys, value);
    }

    /// <summary>转换间隔 Gap（0~20，默认 10）：数字越大，每次参与“复制列 / 插空白列”的段越短。</summary>
    private int _gap = 10;
    public int Gap
    {
        get => _gap;
        set => SetAndSave(ref _gap, value);
    }

    /// <summary>清子弹 Clean（0~16，默认 4）：清掉 1/Clean 拍内的二连音；0 = 不去重。</summary>
    private int _clean = 4;
    public int Clean
    {
        get => _clean;
        set => SetAndSave(ref _clean, value);
    }

    /// <summary>空白列 Blank（0~9，默认 0）：随机拿若干复制出来的列当空白列，用来降低高键数密度。</summary>
    private int _blank;
    public int Blank
    {
        get => _blank;
        set => SetAndSave(ref _blank, value);
    }

    /// <summary>随机种子（默认 "114514"）：固定种子 → 同一张谱每次转出来完全一样；清空则每次随机。</summary>
    public string Seed { get; set; } = "114514";

    /// <summary>是否用 <see cref="Creator"/> 替换原谱师（不勾选时早期版本会在原谱师后追加 " &amp; KeyConverter"）。</summary>
    public bool UseCreator { get; set; }

    /// <summary>新谱师名（配合 <see cref="UseCreator"/>）。</summary>
    public string Creator { get; set; } = "";

    // ---- 源键数过滤：早期版本 1k~9k 复选框；一个都不勾 = 全部转换 ----

    public bool Filter1K { get; set; }
    public bool Filter2K { get; set; }
    public bool Filter3K { get; set; }
    public bool Filter4K { get; set; }
    public bool Filter5K { get; set; }
    public bool Filter6K { get; set; }
    public bool Filter7K { get; set; }
    public bool Filter8K { get; set; }
    public bool Filter9K { get; set; }

    /// <summary>把 9 个勾选框拼成引擎要的 <c>CheckKeys</c> 列表；空列表 = 全部（早期版本语义）。</summary>
    public IReadOnlyList<int> BuildCheckKeys()
    {
        var keys = new List<int>(9);
        if (Filter1K) keys.Add(1);
        if (Filter2K) keys.Add(2);
        if (Filter3K) keys.Add(3);
        if (Filter4K) keys.Add(4);
        if (Filter5K) keys.Add(5);
        if (Filter6K) keys.Add(6);
        if (Filter7K) keys.Add(7);
        if (Filter8K) keys.Add(8);
        if (Filter9K) keys.Add(9);
        return keys;
    }
}
