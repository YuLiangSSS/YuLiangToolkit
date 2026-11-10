using System.Collections.ObjectModel;
using System.Text.Json;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.BoardGames.Ludo;

/// <summary>座位（颜色）状态。</summary>
public sealed record LudoSeat(LudoColor Color, string MemberId, string Name)
{
    /// <summary>有没有人坐。</summary>
    public bool IsOccupied => MemberId.Length > 0;

    /// <summary>显示文字。</summary>
    public string DisplayText => $"{LudoColors.DisplayName(Color)}：{(IsOccupied ? Name : "空位")}";
}

/// <summary>
/// 飞行棋房间（<b>自己选颜色坐</b>）：最多 4 个颜色，谁进来谁点颜色坐下、也可以退出让别人坐。
///
/// <para>跟其它棋牌不同：这里没有「房主安排上场」，而是<b>每个人自己点颜色</b>。
/// 所以房间状态就是「四个颜色各坐着谁」，靠房主广播同步。</para>
/// </summary>
public sealed class LudoRoom
{
    private readonly Dictionary<LudoColor, (string MemberId, string Name)> _seats = new();
    private readonly object _gate = new();

    /// <summary>四个座位（按颜色顺序）。</summary>
    public IReadOnlyList<LudoSeat> Seats
    {
        get
        {
            lock (_gate)
            {
                return LudoColors.All
                    .Select(c => _seats.TryGetValue(c, out var who)
                        ? new LudoSeat(c, who.MemberId, who.Name)
                        : new LudoSeat(c, "", ""))
                    .ToList();
            }
        }
    }

    /// <summary>某个颜色坐着谁。</summary>
    public LudoSeat SeatOf(LudoColor color)
    {
        lock (_gate)
        {
            return _seats.TryGetValue(color, out var who)
                ? new LudoSeat(color, who.MemberId, who.Name)
                : new LudoSeat(color, "", "");
        }
    }

    /// <summary>某个人坐在哪个颜色（没坐返回 null）。</summary>
    public LudoColor? ColorOf(string memberId)
    {
        lock (_gate)
        {
            foreach (var (color, who) in _seats)
            {
                if (who.MemberId == memberId) return color;
            }
            return null;
        }
    }

    /// <summary>坐下（如果这个颜色已经有人，先拒绝）。</summary>
    /// <returns>成功返回空字符串，失败返回原因。</returns>
    public string Sit(LudoColor color, string memberId, string name)
    {
        if (memberId.Length == 0) return "没有成员 id";

        lock (_gate)
        {
            // 已经坐在别处 → 先退出原来的位置
            if (ColorOf(memberId) is { } current && current != color) _seats.Remove(current);

            if (_seats.TryGetValue(color, out var existing) && existing.MemberId != memberId)
                return $"{LudoColors.DisplayName(color)}位已经有人了";

            _seats[color] = (memberId, name);
            return "";
        }
    }

    /// <summary>退出自己的位置（空出来给别人）。</summary>
    public void Leave(string memberId)
    {
        lock (_gate)
        {
            if (ColorOf(memberId) is { } color) _seats.Remove(color);
        }
    }

    /// <summary>清空所有座位。</summary>
    public void Clear()
    {
        lock (_gate) _seats.Clear();
    }

    /// <summary>有几个颜色坐了人。</summary>
    public int OccupiedCount
    {
        get { lock (_gate) return _seats.Count; }
    }

    /// <summary>导出成「能发出去」的座位表。</summary>
    public string[] Export()
    {
        lock (_gate)
        {
            return LudoColors.All
                .Select(c => _seats.TryGetValue(c, out var who) ? $"{LudoColors.ToKey(c)}={who.MemberId}|{who.Name}" : "")
                .ToArray();
        }
    }

    /// <summary>从座位表恢复（客户端用）。</summary>
    public void Import(IReadOnlyList<string>? entries)
    {
        if (entries is null) return;

        lock (_gate)
        {
            _seats.Clear();
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                var parts = entry.Split('=', 2);
                if (parts.Length != 2) continue;

                var color = LudoColors.Parse(parts[0]);
                var who = parts[1].Split('|', 2);
                var memberId = who[0];
                var name = who.Length > 1 ? who[1] : memberId;
                if (memberId.Length == 0) continue;

                _seats[color] = (memberId, name);
            }
        }
    }
}

/// <summary>飞行棋房间状态（协议用）。</summary>
public sealed class LudoRoomState
{
    /// <summary>座位表（每项 <c>red=id|name</c>，空位为空串）。</summary>
    public string[] Seats { get; set; } = Array.Empty<string>();

    /// <summary>现在轮到谁（颜色键）。</summary>
    public string Turn { get; set; } = "red";

    /// <summary>状态序号。</summary>
    public int Seq { get; set; }
}

/// <summary>规则配置（房主可改，协议同步）。</summary>
public sealed class LudoRulesState
{
    public bool NeedSixToTakeOff { get; set; } = true;
    public bool FiveAlsoTakesOff { get; set; }
    public bool ExtraRollOnSix { get; set; } = true;
    public bool ExactToFinish { get; set; } = true;
    public bool CaptureOnLand { get; set; } = true;
    public bool CaptureOnFlightLine { get; set; } = true;

    /// <summary>从规则复制。</summary>
    public static LudoRulesState From(LudoRules rules) => new()
    {
        NeedSixToTakeOff = rules.NeedSixToTakeOff,
        FiveAlsoTakesOff = rules.FiveAlsoTakesOff,
        ExtraRollOnSix = rules.ExtraRollOnSix,
        ExactToFinish = rules.ExactToFinish,
        CaptureOnLand = rules.CaptureOnLand,
        CaptureOnFlightLine = rules.CaptureOnFlightLine,
    };

    /// <summary>写回规则。</summary>
    public void ApplyTo(LudoRules rules)
    {
        rules.NeedSixToTakeOff = NeedSixToTakeOff;
        rules.FiveAlsoTakesOff = FiveAlsoTakesOff;
        rules.ExtraRollOnSix = ExtraRollOnSix;
        rules.ExactToFinish = ExactToFinish;
        rules.CaptureOnLand = CaptureOnLand;
        rules.CaptureOnFlightLine = CaptureOnFlightLine;
    }
}
