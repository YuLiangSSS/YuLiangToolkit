using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.MapDownloader.Core;

/// <summary>
/// 界面下拉框里的选项（值 ↔ 中文标签分离：配置文件里存的是干净的值，见 AGENTS.md 2.4）。
///
/// <para>值全部用 <b>字符串</b>：空串 = 不限，其余就是 osu! API 认的那个值。
/// 这样配置类里不用为"没选"再造一个枚举，老配置文件加字段也不会错位。</para>
/// </summary>
public static class OsuOptions
{
    /// <summary>模式。<c>m=</c> 参数：0=osu! 1=taiko 2=catch 3=mania。</summary>
    public static readonly IReadOnlyList<OptionItem<string>> Modes = OptionItem.From(
        ("", "不限"), ("0", "osu!"), ("1", "Taiko"), ("2", "Catch"), ("3", "Mania"));

    /// <summary>状态。<c>s=</c> 参数（API 认小写）。</summary>
    public static readonly IReadOnlyList<OptionItem<string>> Statuses = OptionItem.From(
        ("", "不限"), ("ranked", "Ranked"), ("loved", "Loved"), ("qualified", "Qualified"),
        ("pending", "Pending"), ("wip", "WIP"), ("graveyard", "Graveyard"));

    /// <summary>排序。<c>sort=</c> 参数（空 = 相关性，API 默认）。</summary>
    public static readonly IReadOnlyList<OptionItem<string>> Sorts = OptionItem.From(
        ("", "相关性"), ("title_desc", "标题"), ("artist_desc", "艺术家"), ("difficulty_desc", "难度"),
        ("ranked_desc", "Ranked 时间"), ("rating_desc", "评分"), ("plays_desc", "游玩次数"),
        ("favourites_desc", "收藏数"));

    /// <summary>Key 数（mania 专用）。<c>"10+"</c> 表示 10 键以上（本地筛选）。</summary>
    public static readonly IReadOnlyList<OptionItem<string>> KeyCounts = OptionItem.From(
        ("", "不限"), ("4", "4K"), ("5", "5K"), ("6", "6K"), ("7", "7K"), ("8", "8K"),
        ("9", "9K"), ("10", "10K"), ("10+", "10K+"));

    /// <summary>把配置里存的模式值转成 <c>m=</c> 要的整数（空/非法 → null）。</summary>
    public static int? ToModeValue(string? mode)
        => int.TryParse(mode, out var value) ? value : null;

    /// <summary>mania 才关心 Key 数（界面据此启用/禁用那个下拉框）。</summary>
    public static bool KeyCountApplies(string? mode) => ToModeValue(mode) == 3;

    /// <summary>取选项（找不到就给第一个，也就是"不限"）。</summary>
    public static OptionItem<string> Pick(IReadOnlyList<OptionItem<string>> options, string? value)
        => OptionItem<string>.Find(options, value ?? "") ?? options[0];
}
