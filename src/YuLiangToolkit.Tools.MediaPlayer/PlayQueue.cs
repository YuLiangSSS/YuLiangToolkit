namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>播放顺序（跟网易云那三个一样）。值按字符串存进 <c>config.json</c>。</summary>
public enum PlayOrderMode
{
    /// <summary>列表循环：播完最后一首接第一首。</summary>
    LoopAll,

    /// <summary>单曲循环：一首播完从头再来。</summary>
    RepeatOne,

    /// <summary>随机播放：一轮内不重复，一轮放完才重洗。</summary>
    Shuffle,
}

/// <summary>
/// 「下一首是谁」的算术（**纯逻辑**，注入 <see cref="Random"/> 后可确定性复现，自检直接断言）。
///
/// <para>为什么单独抽出来：三种播放方式 + 「播完自动下一首」和「用户点下一首」的语义**不一样** ——</para>
/// <list type="bullet">
///   <item><b>播完自动</b>：单曲循环必须留在原地重放；列表循环从头接尾；随机按洗好的顺序走。</item>
///   <item><b>用户点下一首 / 右键快进</b>：哪怕在单曲循环，也应该真的换到下一首（否则按钮像坏了）；
///       随机模式则沿着当前这一轮的洗牌顺序走。</item>
/// </list>
///
/// <para>随机模式维护一份「洗好序的下标表」，一轮走完才重洗，并保证重洗后的第一首不是刚刚那首
/// （否则会连着两遍同一首，听着像卡住了）。列表增删时调 <see cref="Reset"/>。</para>
///
/// <para>内部不变量：<c>_shufflePos</c> = <c>_shuffleOrder</c> 里「当前这首」的下标；
/// 当前这首不在表里（列表换过 / 用户跳着点了）就重洗一份新的，再把指针停在它上面。</para>
/// </summary>
public sealed class PlayQueue
{
    private readonly Random _rng;
    private List<int> _shuffleOrder = new();
    private int _shufflePos = -1;
    private int _current = -1;

    public PlayQueue(Random? random = null)
    {
        _rng = random ?? new Random();
    }

    /// <summary>播放顺序（改它不会打断当前这首）。</summary>
    public PlayOrderMode Mode { get; set; } = PlayOrderMode.LoopAll;

    /// <summary>当前这首歌在列表里的下标（-1 = 还没开始）。</summary>
    public int Current => _current;

    /// <summary>当前这一轮随机里还剩几首没放（非随机模式下是 0，仅供排查/自检用）。</summary>
    public int ShuffleRemaining => Mode == PlayOrderMode.Shuffle && _shufflePos >= 0
        ? Math.Max(0, _shuffleOrder.Count - 1 - _shufflePos)
        : 0;

    /// <summary>列表内容变了（增删 / 覆盖）：把洗牌顺序作废，下次重新洗。</summary>
    public void Reset()
    {
        _shuffleOrder = new List<int>();
        _shufflePos = -1;
    }

    /// <summary>直接指定当前是第几首（用户点列表 / 开始播放时用）。</summary>
    public int SetCurrent(int index)
    {
        _current = index;
        if (Mode == PlayOrderMode.Shuffle)
        {
            // 记不清这一轮的进度了：下一次要「下一首」时按当前位置重新洗一轮（可预测、不会跳丢）
            _shufflePos = -1;
        }
        return _current;
    }

    /// <summary>
    /// 下一首的下标。<paramref name="autoAdvance"/> = true 表示「这首自己播完了」（单曲循环会留在原地）。
    /// 列表为空返回 -1。
    /// </summary>
    public int Next(int count, bool autoAdvance)
    {
        if (count <= 0) return _current = -1;
        if (_current < 0 || _current >= count) return _current = FirstIndex(count);

        if (autoAdvance && Mode == PlayOrderMode.RepeatOne) return _current;

        if (Mode == PlayOrderMode.Shuffle)
        {
            EnsureShuffle(count);
            if (_shufflePos + 1 >= _shuffleOrder.Count)
            {
                _shuffleOrder = BuildShuffleOrder(count, avoidFirst: _current);
                _shufflePos = 0;
            }
            else
            {
                _shufflePos++;
            }
            return _current = _shuffleOrder[_shufflePos];
        }

        return _current = (_current + 1) % count;
    }

    /// <summary>上一首的下标（用户点上一首 / 右键快退）。列表为空返回 -1。</summary>
    public int Previous(int count)
    {
        if (count <= 0) return _current = -1;
        if (_current < 0 || _current >= count) return _current = FirstIndex(count);

        if (Mode == PlayOrderMode.Shuffle)
        {
            EnsureShuffle(count);
            if (_shufflePos > 0) _shufflePos--;
            return _current = _shuffleOrder[_shufflePos];
        }

        return _current = (_current - 1 + count) % count;
    }

    /// <summary>第一次播放时从哪开始：随机模式挑一首随机的，其它从第一首开始。</summary>
    private int FirstIndex(int count)
    {
        if (Mode != PlayOrderMode.Shuffle) return 0;
        _shuffleOrder = BuildShuffleOrder(count, avoidFirst: -1);
        _shufflePos = 0;
        return _shuffleOrder[0];
    }

    /// <summary>保证洗牌表里含当前这首，并把指针停在它上面。</summary>
    private void EnsureShuffle(int count)
    {
        if (_shuffleOrder.Count == count && _shufflePos >= 0 && _shufflePos < count
            && _shuffleOrder[_shufflePos] == _current)
        {
            return;
        }

        _shuffleOrder = BuildShuffleOrder(count, avoidFirst: -1);
        var pos = _shuffleOrder.IndexOf(_current);
        _shufflePos = pos >= 0 ? pos : 0;
    }

    /// <summary>洗一轮（Fisher-Yates）；<paramref name="avoidFirst"/> 用来避免刚放完的那首又排第一。</summary>
    private List<int> BuildShuffleOrder(int count, int avoidFirst)
    {
        var order = new List<int>(count);
        for (var i = 0; i < count; i++) order.Add(i);

        for (var i = count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        if (count > 1 && avoidFirst >= 0 && order[0] == avoidFirst)
        {
            var swap = 1 + _rng.Next(count - 1);
            (order[0], order[swap]) = (order[swap], order[0]);
        }

        return order;
    }

    // ---------- 展示文案（一个按钮轮换三种模式，靠它显示当前状态） ----------

    /// <summary>模式按钮上的图标。</summary>
    public static string ModeGlyph(PlayOrderMode mode) => mode switch
    {
        PlayOrderMode.RepeatOne => "🔂",
        PlayOrderMode.Shuffle => "🔀",
        _ => "🔁",
    };

    /// <summary>模式的中文名。</summary>
    public static string ModeText(PlayOrderMode mode) => mode switch
    {
        PlayOrderMode.RepeatOne => "单曲循环",
        PlayOrderMode.Shuffle => "随机播放",
        _ => "列表循环",
    };

    /// <summary>按钮的悬停提示。</summary>
    public static string ModeTooltip(PlayOrderMode mode)
        => $"{ModeText(mode)}（点一下切换：列表循环 → 单曲循环 → 随机播放）";

    /// <summary>点一下轮换到下一个模式。</summary>
    public static PlayOrderMode Cycle(PlayOrderMode mode) => mode switch
    {
        PlayOrderMode.LoopAll => PlayOrderMode.RepeatOne,
        PlayOrderMode.RepeatOne => PlayOrderMode.Shuffle,
        _ => PlayOrderMode.LoopAll,
    };
}
