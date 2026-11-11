using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.Games.Tetris;

/// <summary>可以自由摆放的 HUD 组件。</summary>
public enum HudPart
{
    /// <summary>暂存（Hold）。</summary>
    Hold = 0,

    /// <summary>接下来（Next）。</summary>
    Next = 1,

    /// <summary>分数 / 最高分。</summary>
    Score = 2,

    /// <summary>等级 / 消行。</summary>
    Stats = 3,

    /// <summary>垃圾行 / 连击提示。</summary>
    Status = 4,
}

/// <summary>一个 HUD 组件在棋盘容器里的相对位置（0~1 的比例，窗口缩放时跟着走）。</summary>
public sealed class HudPlacement
{
    /// <summary>组件。</summary>
    public HudPart Part { get; set; }

    /// <summary>左上角 X（0~1，相对容器宽）。</summary>
    public double X { get; set; }

    /// <summary>左上角 Y（0~1，相对容器高）。</summary>
    public double Y { get; set; }
}

/// <summary>
/// HUD 自由布局：每个组件记一个<b>相对位置</b>（0~1 比例），
/// 用户拖动后存进配置，窗口大小变化时按比例还原，不会跑偏。
///
/// <para><b>默认布局</b>（仿常见俄罗斯方块）：HOLD 在左上、NEXT 在右上、
/// 分数在左下、等级 / 消行贴在分数下面。</para>
/// </summary>
public sealed class HudLayout
{
    /// <summary>默认布局：HOLD 左上、NEXT 右上、分数左下、统计左下偏下、状态右下。</summary>
    public static HudLayout CreateDefault() => new()
    {
        // 参考常见俄罗斯方块：HOLD 贴棋盘左上、NEXT 贴右上；
        // 分数 / 等级在棋盘左侧偏下（搭在棋盘边上，不挡主视野）。
        Items = new List<HudPlacement>
        {
            new() { Part = HudPart.Hold, X = 0.02, Y = 0.03 },
            new() { Part = HudPart.Next, X = 0.79, Y = 0.03 },
            new() { Part = HudPart.Score, X = 0.02, Y = 0.72 },
            new() { Part = HudPart.Stats, X = 0.02, Y = 0.88 },
            new() { Part = HudPart.Status, X = 0.79, Y = 0.72 },
        },
    };

    /// <summary>各组件的位置。</summary>
    public List<HudPlacement> Items { get; set; } = new();

    /// <summary>取某个组件的位置（没有就返回默认）。</summary>
    public HudPlacement Get(HudPart part)
        => Items.FirstOrDefault(i => i.Part == part)
           ?? CreateDefault().Get(part);

    /// <summary>设置某个组件的位置（自动夹到 0~1）。</summary>
    public void Set(HudPart part, double x, double y)
    {
        var item = Items.FirstOrDefault(i => i.Part == part);
        if (item is null)
        {
            item = new HudPlacement { Part = part };
            Items.Add(item);
        }

        item.X = Math.Clamp(x, 0, 0.92);
        item.Y = Math.Clamp(y, 0, 0.92);
    }

    /// <summary>序列化成配置字符串。</summary>
    public string Serialize()
    {
        try { return JsonSerializer.Serialize(this, Options); }
        catch { return ""; }
    }

    /// <summary>从配置字符串还原（坏数据返回默认布局）。</summary>
    public static HudLayout Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return CreateDefault();

        try
        {
            var parsed = JsonSerializer.Deserialize<HudLayout>(json, Options);
            return parsed is { Items.Count: > 0 } ? parsed : CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
