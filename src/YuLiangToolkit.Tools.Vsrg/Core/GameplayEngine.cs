using System.Globalization;

namespace YuLiangToolkit.Tools.Vsrg.Core;

/// <summary>一个判定单元（面条算两个：头 + 尾）。</summary>
public enum NoteUnit
{
    /// <summary>头判（普通键只有这一下）。</summary>
    Head = 0,

    /// <summary>尾判（只有面条且开启尾部判定时才有）。</summary>
    Tail = 1,
}

/// <summary>一次判定的记录（用于判定弹字、结算统计）。</summary>
/// <param name="NoteIndex">是第几个 note（谱面里的下标）。</param>
/// <param name="Unit">头还是尾。</param>
/// <param name="TierIndex">命中的档位序号（Miss 档也在里面）。</param>
/// <param name="TierName">档位名。</param>
/// <param name="DeltaMs">时间差（毫秒，正 = 按早了）。</param>
/// <param name="Miss">是不是 Miss。</param>
public readonly record struct JudgementEvent(
    int NoteIndex,
    NoteUnit Unit,
    int TierIndex,
    string TierName,
    double DeltaMs,
    bool Miss)
{
    /// <summary>弹字用文本（<c>Marvelous</c> / <c>Miss</c>）。</summary>
    public string Display => TierName;
}

/// <summary>
/// **游玩引擎**：把「谱面 + 判定档 + 按键事件」变成判定结果 / 准确率 / combo。
///
/// <para><b>纯逻辑</b>：不碰音频、不碰界面、不碰线程。时间由调用方每帧用
/// <see cref="AdvanceTo"/> 推进（游玩时的值来自音频时钟 + 用户偏移），按键用
/// <see cref="Press"/> / <see cref="Release"/>。因此整条判定链路都能无头自检。</para>
///
/// <para><b>判定口径</b>：</para>
/// <list type="bullet">
///   <item>按下时在本轨找**最早一个还没判过的头**，|按键时间 − note 时间| ≤ 该档窗口即命中，
///         按档位顺序取第一个满足的（越靠前越严格）；超过最大窗口的 note 由 <see cref="AdvanceTo"/>
///         判 Miss；</item>
///   <item>面条：开启尾部判定时**头尾各算一个判定单元**（都进准确率分母）；头部漏了，尾部一并算 Miss
///         （没按下去自然谈不上保持）；</item>
///   <item>尾部：松开时 |松开时间 − 尾巴时间| ≤ 档位窗口 × 尾部乘数 → 命中该档；
///         早于「尾巴时间 − 尾窗」松开 = 尾判 Miss；一直按到「尾巴时间 + 尾窗」之后 = 尾判最好档
///         （和 osu! 一样「按到底算满」）；</item>
///   <item>关闭尾部判定时：面条只判头，尾部既不判定也不进准确率分母。</item>
///   <item>准确率 = Σ命中档权重 /（已判单元数 × 最高权重）× 100 —— 分母用**已判单元**，
///         所以中途退出也能显示一个诚实的数（没打完就低不了）。</item>
/// </list>
///
/// <para><b>性能</b>：每条轨道维护一个「还没判过的头」的游标 + 正在保持的面条，
/// 每帧只推进游标，不遍历全谱（10 万 note 的谱面每帧 O(轨道数)）。</para>
/// </summary>
public sealed class GameplayEngine
{
    private readonly Chart _chart;
    private readonly GameplayProfile _profile;
    private readonly NoteState[] _states;
    private readonly LaneState[] _lanes;
    private readonly int[] _tierCounts;
    private readonly int _missTierIndex;
    private readonly double _tailWindowScale;

    private int _combo;
    private int _maxCombo;
    private int _judgedUnits;
    private double _weightSum;
    private double _nowMs;
    private JudgementEvent? _lastEvent;

    /// <summary>构造一份游玩会话。</summary>
    /// <param name="chart">谱面。</param>
    /// <param name="profile">判定档快照（游玩途中改设置不影响这一局）。</param>
    public GameplayEngine(Chart chart, GameplayProfile profile)
    {
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        _missTierIndex = Math.Max(0, profile.MissTierIndex);
        _tailWindowScale = profile.TailJudgementEnabled ? profile.TailWindowMultiplier : 0;
        _tierCounts = new int[profile.Tiers.Count];
        _states = new NoteState[chart.Notes.Count];

        var lanes = new LaneState[Math.Max(1, chart.KeyCount)];
        for (var i = 0; i < lanes.Length; i++) lanes[i] = new LaneState();

        for (var i = 0; i < chart.Notes.Count; i++)
        {
            var note = chart.Notes[i];
            _states[i] = new NoteState(note);
            lanes[note.Column].Order.Add(i);
        }

        _lanes = lanes;

        // 结束时间：最后一个 note（含面条尾巴）+ 最大窗口（多给一点余量，让最后一下也能判出来）
        EndMs = chart.Notes.Count == 0 ? 0 : chart.LastNoteMs + profile.MaxWindowMs + 1;
    }

    /// <summary>谱面。</summary>
    public Chart Chart => _chart;

    /// <summary>判定档快照。</summary>
    public GameplayProfile Profile => _profile;

    /// <summary>这一局什么时候算打完（毫秒，谱面时间）。</summary>
    public double EndMs { get; }

    /// <summary>当前时间（毫秒，谱面时间；由 <see cref="AdvanceTo"/> 推进）。</summary>
    public double NowMs => _nowMs;

    /// <summary>当前 combo。</summary>
    public int Combo => _combo;

    /// <summary>最大 combo。</summary>
    public int MaxCombo => _maxCombo;

    /// <summary>已判定的单元数。</summary>
    public int JudgedUnits => _judgedUnits;

    /// <summary>总判定单元数（面条开尾判时一首歌 = 普通键 + 面条 × 2）。</summary>
    public int TotalUnits => _chart.Notes.Count + (TailJudgementEnabled ? _chart.LongNoteCount : 0);

    /// <summary>这首歌里面条的尾判是否参与（判定档设置里那个开关）。</summary>
    public bool TailJudgementEnabled => _profile.TailJudgementEnabled;

    /// <summary>是否全部判定单元都判完了。</summary>
    public bool AllJudged => _judgedUnits >= TotalUnits;

    /// <summary>是否打完了（时间过完 + 都判完了）。</summary>
    public bool IsFinished => _nowMs >= EndMs && AllJudged;

    /// <summary>准确率（0~100；还没判定任何单元时为 100 —— 和 osu! 一致，避免开局显示 0%）。</summary>
    public double Accuracy
    {
        get
        {
            if (_judgedUnits == 0) return 100;
            return _weightSum / (_judgedUnits * _profile.MaxWeight) * 100;
        }
    }

    /// <summary>最近一次判定（界面弹字用；没有时为 null）。</summary>
    public JudgementEvent? LastEvent => _lastEvent;

    /// <summary>每个档位命中了几次（下标与判定档列表一致）。</summary>
    public IReadOnlyList<int> TierCounts => _tierCounts;

    /// <summary>按名字取命中次数（结算列表用；找不到返回 0）。</summary>
    public int CountOf(string tierName)
    {
        for (var i = 0; i < _profile.Tiers.Count; i++)
        {
            if (string.Equals(_profile.Tiers[i].Name, tierName, StringComparison.Ordinal)) return _tierCounts[i];
        }
        return 0;
    }

    /// <summary>Miss 次数。</summary>
    public int MissCount => _missTierIndex < _tierCounts.Length ? _tierCounts[_missTierIndex] : 0;

    /// <summary>轨道数。</summary>
    public int LaneCount => _lanes.Length;

    /// <summary>某条轨道当前是否按着（渲染按键高亮用）。</summary>
    public bool IsLaneHeld(int lane) => lane >= 0 && lane < _lanes.Length && _lanes[lane].Held;

    /// <summary>某个 note 的头是否已经判过（渲染：判过的普通键不再画）。</summary>
    public bool IsHeadResolved(int noteIndex)
        => noteIndex >= 0 && noteIndex < _states.Length && _states[noteIndex].HeadResolved;

    /// <summary>某个面条是否正被按着（渲染：身体从当前位置画到尾巴）。</summary>
    public bool IsHoldingLongNote(int noteIndex)
        => noteIndex >= 0 && noteIndex < _states.Length && _states[noteIndex].TailPending;

    /// <summary>
    /// 把时间推进到 <paramref name="ms"/> 并结算「时间已经过了、还没判」的单元。
    /// 时间只进不退（播放时钟本身就不会倒退，这里再兜一层）。
    /// </summary>
    public void AdvanceTo(double ms)
    {
        if (!double.IsFinite(ms)) return;
        if (ms < _nowMs) return;
        _nowMs = ms;

        var miss = _profile.MissTier;

        for (var lane = 0; lane < _lanes.Length; lane++)
        {
            var state = _lanes[lane];

            // ① 头部超时（按下窗口已经过去还没按）
            while (state.Cursor < state.Order.Count)
            {
                var index = state.Order[state.Cursor];
                var note = _states[index];
                if (note.HeadResolved) { state.Cursor++; continue; }

                if (_nowMs <= note.Note.StartMs + _profile.MaxWindowMs) break;

                ResolveMissedHead(index, _nowMs - note.Note.StartMs, miss);
                state.Cursor++;
            }

            // ② 正按着的面条：一直按到「尾巴 + 最大尾窗」之后 = 按到底，算最严档
            if (state.ActiveNote >= 0)
            {
                var index = state.ActiveNote;
                var note = _states[index];

                if (_nowMs >= note.Note.EndMs + MaxTailWindowMs)
                {
                    var bestIndex = BestTierIndex();
                    note.TailResolved = true;
                    note.TailTierIndex = bestIndex;
                    note.TailPending = false;
                    state.ActiveNote = -1;
                    Record(index, NoteUnit.Tail, bestIndex, _nowMs - note.Note.EndMs, _profile.Tiers[bestIndex]);
                }
            }
        }
    }

    /// <summary>
    /// 按下某条轨道（按住不放的键盘自动重复请自行过滤：只有「真按下」才调这里）。
    /// </summary>
    /// <returns>这次按下判到了什么；没判到任何 note 返回 null。</returns>
    public JudgementEvent? Press(int lane)
    {
        if (lane < 0 || lane >= _lanes.Length) return null;
        var state = _lanes[lane];
        state.Held = true;

        while (state.Cursor < state.Order.Count)
        {
            var index = state.Order[state.Cursor];
            var note = _states[index];
            if (note.HeadResolved) { state.Cursor++; continue; }

            // 太早（还没进窗口）：这一下不判任何东西，等着
            var delta = _nowMs - note.Note.StartMs;
            if (delta < -_profile.MaxWindowMs) return null;

            // 太晚（理论上 AdvanceTo 已经判掉了，这里防御一下）
            if (delta > _profile.MaxWindowMs)
            {
                ResolveMissedHead(index, delta, _profile.MissTier);
                state.Cursor++;
                continue;
            }

            var tier = _profile.Match(delta) ?? _profile.MissTier;
            var tierIndex = Math.Max(0, _profile.IndexOf(tier));

            note.HeadResolved = true;
            note.HeadTierIndex = tierIndex;
            var result = Record(index, NoteUnit.Head, tierIndex, delta, tier);
            state.Cursor++;

            // 面条：头判命中才进入「保持中」；头都 Miss 了，尾部由 ResolveMissedHead 一起判掉
            if (TailJudgementEnabled && note.Note.IsLongNote && !note.TailResolved && !tier.IsMiss)
            {
                note.TailPending = true;
                state.ActiveNote = index;
            }

            return result;
        }

        return null;
    }

    /// <summary>松开某条轨道。</summary>
    /// <returns>这次松开判到了什么（尾判）；没判到返回 null。</returns>
    public JudgementEvent? Release(int lane)
    {
        if (lane < 0 || lane >= _lanes.Length) return null;
        var state = _lanes[lane];
        state.Held = false;

        if (!TailJudgementEnabled || state.ActiveNote < 0) return null;

        var index = state.ActiveNote;
        state.ActiveNote = -1;

        var note = _states[index];
        if (note.TailResolved) return null;

        // 松开时刻相对尾巴的偏移：负数 = 早松，正数 = 晚松
        var delta = _nowMs - note.Note.EndMs;
        JudgementTier tier;

        if (delta < -MaxTailWindowMs)
        {
            // 早于「尾巴 − 最大尾窗」就松手：尾判 Miss
            tier = _profile.MissTier;
        }
        else
        {
            // 窗口内按 |差值| 取档（超出所有窗口 → Miss）
            tier = _profile.MatchScaled(delta, _tailWindowScale) ?? _profile.MissTier;
        }

        var tierIndex = Math.Max(0, _profile.IndexOf(tier));
        note.TailResolved = true;
        note.TailTierIndex = tierIndex;
        note.TailPending = false;

        return Record(index, NoteUnit.Tail, tierIndex, delta, tier);
    }

    /// <summary>全部松开（暂停 / 退出时用：不算判定，只清按键状态）。</summary>
    public void ReleaseAll()
    {
        foreach (var lane in _lanes) lane.Held = false;
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>某个 note 头部被判成 Miss 时的统一处理（面条的尾巴一起判掉）。</summary>
    private void ResolveMissedHead(int index, double delta, JudgementTier miss)
    {
        var note = _states[index];
        var missIndex = Math.Max(0, _profile.IndexOf(miss));

        note.HeadResolved = true;
        note.HeadTierIndex = missIndex;
        Record(index, NoteUnit.Head, missIndex, delta, miss);

        // 头都没打上，尾巴自然也谈不上「保持」：一起算 Miss
        if (TailJudgementEnabled && note.Note.IsLongNote && !note.TailResolved)
        {
            note.TailResolved = true;
            note.TailTierIndex = missIndex;
            note.TailPending = false;
            Record(index, NoteUnit.Tail, missIndex, 0, miss);
        }
    }

    /// <summary>尾巴窗口的最大值（= 最宽松档的窗口 × 尾部乘数）；超过它就再也判不到尾判了。</summary>
    private double MaxTailWindowMs => TailJudgementEnabled ? _profile.MaxWindowMs * _tailWindowScale : 0;

    /// <summary>最严格（窗口最小但大于 0）那一档的窗口 —— 面条「按到底算满」用的档。</summary>
    private double BestTailWindowMs
    {
        get
        {
            var best = _profile.Tiers
                .Where(t => !t.IsMiss && t.WindowMs > 0)
                .Select(t => t.WindowMs)
                .DefaultIfEmpty(0)
                .Min();
            return best * _tailWindowScale;
        }
    }

    /// <summary>最好档的序号（窗口最小、权重最高的那一档）。</summary>
    private int BestTierIndex()
    {
        var best = -1;
        var bestWindow = double.MaxValue;
        for (var i = 0; i < _profile.Tiers.Count; i++)
        {
            var tier = _profile.Tiers[i];
            if (tier.IsMiss || tier.WindowMs <= 0) continue;
            if (tier.WindowMs < bestWindow)
            {
                bestWindow = tier.WindowMs;
                best = i;
            }
        }
        return best >= 0 ? best : _missTierIndex;
    }

    private JudgementEvent Record(int noteIndex, NoteUnit unit, int tierIndex, double delta, JudgementTier tier)
    {
        var miss = tier.IsMiss;
        if (miss)
        {
            _combo = 0;
        }
        else
        {
            _combo++;
            if (_combo > _maxCombo) _maxCombo = _combo;
        }

        _judgedUnits++;
        _weightSum += tier.AccuracyWeight;
        if (tierIndex >= 0 && tierIndex < _tierCounts.Length) _tierCounts[tierIndex]++;

        var result = new JudgementEvent(noteIndex, unit, tierIndex, tier.Name, delta, miss);
        _lastEvent = result;
        return result;
    }

    /// <summary>一个 note 的判定状态（可变，引擎内部用）。</summary>
    private sealed class NoteState
    {
        public NoteState(ChartNote note) => Note = note;

        public ChartNote Note { get; }

        /// <summary>头部判过没有。</summary>
        public bool HeadResolved;

        /// <summary>头部命中档位。</summary>
        public int HeadTierIndex = -1;

        /// <summary>尾部判过没有。</summary>
        public bool TailResolved;

        /// <summary>尾部命中档位。</summary>
        public int TailTierIndex = -1;

        /// <summary>正被按着（渲染：身体从当前位置画到尾巴）。</summary>
        public bool TailPending;
    }

    /// <summary>一条轨道的状态。</summary>
    private sealed class LaneState
    {
        /// <summary>本轨的 note 下标（按开始时间升序）。</summary>
        public List<int> Order { get; } = new();

        /// <summary>下一个「头还没判」的 note 在 <see cref="Order"/> 里的位置。</summary>
        public int Cursor;

        /// <summary>键现在是否按着。</summary>
        public bool Held;

        /// <summary>最近一次松开的时刻（调试 / 手感分析用）。</summary>
        public double HeldSince = double.NaN;

        /// <summary>正在保持的面条（<see cref="Order"/> 里的下标对应的 note 下标；-1 = 没有）。</summary>
        public int ActiveNote = -1;
    }

    /// <summary>准确率的界面文本（<c>98.76%</c>）。</summary>
    public string AccuracyText => Accuracy.ToString("0.00", CultureInfo.InvariantCulture) + "%";
}
