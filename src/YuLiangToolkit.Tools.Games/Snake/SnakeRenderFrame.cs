namespace YuLiangToolkit.Tools.Games.Snake;

/// <summary>
/// 一帧的渲染数据（发给界面 / 联机广播）。<b>纯数据</b>，不引用对局对象，
/// 这样客户端拿到就能直接画。
/// </summary>
public sealed class SnakeRenderFrame
{
    /// <summary>空帧。</summary>
    public static SnakeRenderFrame Empty { get; } = new();

    /// <summary>棋盘宽。</summary>
    public int Width { get; set; } = 40;

    /// <summary>棋盘高。</summary>
    public int Height { get; set; } = 30;

    /// <summary>果子坐标。</summary>
    public List<int[]> Fruits { get; set; } = new();

    /// <summary>死亡残影。</summary>
    public List<int[]> DeathMarks { get; set; } = new();

    /// <summary>出生点爆炸区（保留字段：旧版本用过，现在光爆走 <see cref="Bursts"/>）。</summary>
    public List<int[]> BlastZones { get; set; } = new();

    /// <summary>正在闪烁的出生光爆格子（渲染用，不参与碰撞）。</summary>
    public List<int[]> Bursts { get; set; } = new();

    /// <summary>每条蛇。</summary>
    public List<SnakeFrameSnake> Snakes { get; set; } = new();

    /// <summary>排行榜行。</summary>
    public List<SnakePlayerRow> Rows { get; set; } = new();

    /// <summary>结果文字（结束时有）。</summary>
    public string ResultText { get; set; } = "";

    /// <summary>倒计时剩余。</summary>
    public int Countdown { get; set; }

    /// <summary>从对局拍一帧。</summary>
    public static SnakeRenderFrame FromGame(SnakeGame game)
    {
        var frame = new SnakeRenderFrame
        {
            Width = game.Width,
            Height = game.Height,
            Fruits = game.Fruits.Select(p => new[] { p.X, p.Y }).ToList(),
            DeathMarks = game.DeathMarks.Select(p => new[] { p.X, p.Y }).ToList(),
            Countdown = game.CountdownSecondsLeft,
            Bursts = game.ActiveBursts().Select(p => new[] { p.X, p.Y }).ToList(),
            ResultText = game.IsOver ? game.ResultText : "",
        };

        // 爆炸区
        for (var y = 0; y < game.Height; y++)
        {
            for (var x = 0; x < game.Width; x++)
            {
                if (game.IsSpawnBlocked(new SnakePoint(x, y))) frame.BlastZones.Add(new[] { x, y });
            }
        }

        foreach (var player in game.Players)
        {
            frame.Snakes.Add(new SnakeFrameSnake
            {
                Id = player.Id,
                Name = player.Name,
                Body = player.Snake.Body.Select(p => new[] { p.X, p.Y }).ToList(),
                IsAlive = player.Snake.IsAlive,
                IsGhost = player.Snake.IsGhost,
                GhostSteps = player.Snake.GhostSteps,
                Lives = player.Lives,
                Fruits = player.Stats.Fruits,
                RespawnIn = player.RespawnIn,
                IsAi = player.IsAi,
            });
        }

        var ranking = game.ComputeRanking();
        foreach (var (player, score) in ranking)
        {
            var status = !player.Snake.IsAlive
                ? player.Lives > 0 ? $"等待复活（{player.RespawnIn}）" : "阵亡"
                : player.Snake.IsGhost ? "虚化保护" : "存活";
            frame.Rows.Add(new SnakePlayerRow(player.Id, player.Name, player.Stats.Fruits, score, status, false, player.IsAi));
        }

        return frame;
    }
}

/// <summary>一帧里的一条蛇。</summary>
public sealed class SnakeFrameSnake
{
    /// <summary>玩家 id。</summary>
    public string Id { get; set; } = "";

    /// <summary>名字。</summary>
    public string Name { get; set; } = "";

    /// <summary>蛇身（[x,y] 数组，索引 0 = 头）。</summary>
    public List<int[]> Body { get; set; } = new();

    /// <summary>活着吗。</summary>
    public bool IsAlive { get; set; }

    /// <summary>虚化中。</summary>
    public bool IsGhost { get; set; }

    /// <summary>剩余虚化步数。</summary>
    public int GhostSteps { get; set; }

    /// <summary>剩余命。</summary>
    public int Lives { get; set; }

    /// <summary>吃果数。</summary>
    public int Fruits { get; set; }

    /// <summary>重生倒计时。</summary>
    public int RespawnIn { get; set; }

    /// <summary>是不是 AI。</summary>
    public bool IsAi { get; set; }
}

/// <summary>界面上的一个玩家行（排行榜 / 状态用）。</summary>
public sealed record SnakePlayerRow(
    string Id, string Name, int Fruits, double Score, string StatusText, bool IsMe, bool IsAi)
{
    /// <summary>显示名（含「你」「AI」标记）。</summary>
    public string DisplayName => Name + (IsMe ? "（你）" : IsAi ? "（AI）" : "");

    /// <summary>名次 / 统计一行。</summary>
    public string DetailText => $"吃 {Fruits} 果 · {Score:0.0} 分";

    /// <summary>是不是这条蛇死了在等复活。</summary>
    public string StatusColor => StatusText == "存活" ? "SuccessBrush" : "DangerBrush";
}

