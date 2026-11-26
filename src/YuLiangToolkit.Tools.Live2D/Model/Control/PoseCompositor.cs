using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model.Control;

/// <summary>
/// 位姿合成的一个「层」（= 一个子系统：待机呼吸 / 鼠标眼控 / 待机动作 / 手势 / 表情 ……）。
///
/// <para><b>规则只有一条</b>：层每帧把自己的输出写进一块独立的捕获缓冲（<see cref="PoseCapture"/>），
/// 由 <see cref="PoseCompositor"/> 按 <see cref="Weight"/> 混进合成结果——
/// <c>out = out + (层输出 - out) × weight</c>。层里读参数（<c>parameter.Value</c>）拿到的是「下面各层已经合成好的值」，
/// 所以动作淡入、手势收回这类「从当前姿势过渡到目标姿势」天然成立，不需要任何「先复位再交接」的补丁。</para>
///
/// <para><b>位姿记忆</b>：层这一帧没再写参数（动作播完 / 被打断 / 开关关掉）时，合成器继续拿它最后一帧的输出
/// 按 <see cref="FadeOutSeconds"/> 淡出——任何动作收尾都是交叉淡化，不会一帧跳回默认姿势。</para>
///
/// <para><b>两种层</b>：</para>
/// <list type="bullet">
///   <item>常驻层（<see cref="AlwaysEvaluate"/>=false）：由属主每帧设 <see cref="Active"/>，只在 Active 时求值；</item>
///   <item>动作层（<see cref="AlwaysEvaluate"/>=true）：每帧都求值，「这一帧有没有在活动」以「有没有写参数」为准
///     （舞蹈 / 表演 / 手势 / 互动 / 待机小动作自己知道什么时候该停）。</item>
/// </list>
/// </summary>
internal sealed class PoseStage
{
    public PoseStage(string name, double fadeInSeconds, double fadeOutSeconds, bool alwaysEvaluate = false)
    {
        Name = name;
        FadeInSeconds = fadeInSeconds;
        FadeOutSeconds = fadeOutSeconds;
        AlwaysEvaluate = alwaysEvaluate;
    }

    public string Name { get; }

    /// <summary>从 0 淡到 1 的秒数（≤ 0 = 立刻生效）。</summary>
    public double FadeInSeconds { get; set; }

    /// <summary>从 1 淡到 0 的秒数（≤ 0 = 立刻消失）。</summary>
    public double FadeOutSeconds { get; set; }

    /// <summary>动作层：每帧都求值，活动与否看本帧有没有写参数。</summary>
    public bool AlwaysEvaluate { get; }

    /// <summary>
    /// 这一层是不是「我们自己手写的动作表」（手势 / 表演 / 舞蹈 / 待机小动作 / 鼠标互动）。
    /// 手写表里的参数是我们逐个挑过的：层求值期间对实测无效参数的写入会被丢弃并计数（<see cref="PoseCompositor.DroppedWrites"/>），
    /// 动作表体检用它保证「手写表不依赖任何无效参数」。作者的 motion 文件是原样保留的内容，不受这个开关约束。
    /// </summary>
    public bool DropsDeadWrites { get; set; }

    /// <summary>常驻层：本帧是否该求值（属主每帧设置）。</summary>
    public bool Active { get; set; }

    /// <summary>本帧是否写了参数（合成器回填）。</summary>
    public bool Wrote { get; internal set; }

    /// <summary>线性权重 0~1（合成器维护）。</summary>
    public float Weight { get; internal set; }

    /// <summary>两端速度为 0 的平滑权重（真正参与混合的值）。</summary>
    public float EasedWeight
    {
        get
        {
            var t = Weight;
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t * t * (3f - (2f * t));
        }
    }

    /// <summary>这一帧写了多少个参数（自检 / 调试）。</summary>
    public int WrittenCount { get; internal set; }

    // 位姿记忆：最后一次有输出那一帧写的参数（稀疏：下标 + 值）
    internal int[] MemoryIndices = new int[32];
    internal float[] MemoryValues = new float[32];
    internal int MemoryCount;

    /// <summary>让位姿记忆能装下 <paramref name="count"/> 个参数（只在变大时扩容，稳态 0 分配）。</summary>
    internal void EnsureMemoryCapacity(int count)
    {
        if (MemoryIndices.Length >= count) return;
        var size = MemoryIndices.Length;
        while (size < count) size *= 2;
        Array.Resize(ref MemoryIndices, size);
        Array.Resize(ref MemoryValues, size);
    }
}

/// <summary>
/// 一块捕获缓冲：某个层这一帧写过的参数下标 + 值。
/// 合成期里层的所有 <c>parameter.Value</c> 写入都落到这里，不碰 Core。
/// </summary>
internal sealed class PoseCapture
{
    internal PoseCapture(int parameterCount)
    {
        Scratch = new float[parameterCount];
        Stamp = new int[parameterCount];
        Written = new int[64];
    }

    /// <summary>本层写的值（按参数下标）。</summary>
    public readonly float[] Scratch;

    /// <summary>每个参数最后一次被这块缓冲写入时的帧号（== <see cref="FrameId"/> 即本帧写过）。</summary>
    internal readonly int[] Stamp;

    /// <summary>本帧写过的参数下标（去重，按写入顺序；不够用时扩容）。</summary>
    public int[] Written;

    /// <summary>本帧写过的参数个数。</summary>
    public int WrittenCount;

    internal int FrameId;

    internal void BeginFrame()
    {
        // 帧号回绕时把旧标记清掉，避免「陈旧标记恰好等于当前帧号」的误判
        if (FrameId == int.MaxValue)
        {
            Array.Clear(Stamp);
            FrameId = 0;
        }

        FrameId++;
        WrittenCount = 0;
    }

    internal void Mark(int index)
    {
        if (Stamp[index] != FrameId)
        {
            Stamp[index] = FrameId;
            if (WrittenCount == Written.Length) Array.Resize(ref Written, Written.Length * 2);
            Written[WrittenCount++] = index;
        }
    }
}

/// <summary>
/// 位姿合成器：把「手动值 + 各层输出」合成成这一帧的最终参数，一次性写回 Cubism Core。
/// 它同时是 <see cref="CubismModel"/> 的参数转接点（<see cref="IParameterSink"/>）：
/// <list type="bullet">
///   <item><b>合成期之外</b>写参数（面板滑杆 / 预设 / 自检）= 手动值：记进 <c>_base</c> 并立即写 Core；</item>
///   <item><b>某个层求值时</b>写参数 = 这一层本帧的输出，落到该层的捕获缓冲；
///     读参数 = 本层已写就读本层输出，否则读「下面各层合成到现在的值」；</item>
///   <item><b>直通期</b>（物理求值 / 摇尾巴 / 固定参数回填）= 直接读写 Core，不进手动值。</item>
/// </list>
///
/// <para><b>每帧流程</b>：<see cref="BeginFrame"/>（out = 手动值）→ 逐层 <see cref="Run"/>（求值 + 按权重混合）
/// → <see cref="EndFrame"/>（out 写回 Core）→ 物理 → 固定参数 → <c>model.Update()</c>。</para>
///
/// <para>旧实现是「每帧开头把各子系统写过的参数复位成默认，再按固定顺序互相覆盖」，层的先后顺序
/// 就是隐式的优先级，层与层的交接全靠各自的 touched 列表和特判补丁——收回动作抽搐就是这套机制的产物。
/// 现在优先级 = 层的先后，交接 = 权重，二者都只有一处定义。</para>
/// </summary>
internal sealed class PoseCompositor : IParameterSink
{
    private readonly CubismModel _model;
    private readonly int _count;
    private readonly float[] _base;
    private readonly float[] _out;
    private readonly List<PoseStage> _stages = new(24);
    private readonly Dictionary<PoseStage, PoseCapture> _captures = new(24);
    private PoseCapture? _stageCapture;
    private PoseCapture? _temporaryCapture;
    private bool _bypass;
    private bool _composing;             // BeginFrame ~ EndFrame 之间：读到的是「下面各层合成到现在的值」
    private bool _dropDeadActive;        // 当前求值的层是手写动作表：对无效参数的写入丢弃并计数
    private bool _snapPending = true;   // 刚建好：第一帧不做淡入
    private double _deltaSeconds;
    private HashSet<int>? _deadParameters;

    public PoseCompositor(CubismModel model)
    {
        _model = model;
        _count = model.ParameterCount;
        _base = new float[_count];
        _out = new float[_count];
        foreach (var parameter in model.Parameters) _base[parameter.Index] = parameter.DefaultValue;
        model.AttachParameterSink(this);
    }

    /// <summary>全部层（按优先级从低到高）。</summary>
    public IReadOnlyList<PoseStage> Stages => _stages;

    /// <summary>登记一层（调用顺序 = 优先级，先登记的先合成、会被后登记的盖住）。</summary>
    public PoseStage AddStage(string name, double fadeInSeconds, double fadeOutSeconds, bool alwaysEvaluate = false)
    {
        var stage = new PoseStage(name, fadeInSeconds, fadeOutSeconds, alwaysEvaluate);
        _stages.Add(stage);
        _captures[stage] = new PoseCapture(_count);
        return stage;
    }

    /// <summary>本帧的时间步长（权重推进用）。</summary>
    public void SetDeltaSeconds(double deltaSeconds) => _deltaSeconds = deltaSeconds;

    /// <summary>开始合成：out = 手动值（面板里用户设的值，没设过就是模型默认值）。</summary>
    public void BeginFrame()
    {
        Array.Copy(_base, _out, _count);
        _composing = true;
    }

    /// <summary>结束合成：把 out 一次性写回 Core；「第一帧直接到位」标记只对这一帧有效。</summary>
    public void EndFrame()
    {
        for (var i = 0; i < _count; i++) _model.WriteCoreParameter(i, _out[i]);
        _composing = false;
        _snapPending = false;
    }

    /// <summary>
    /// 求值一层并混合。层求值期间的参数写入都落到它自己的捕获缓冲；
    /// 这帧有输出 → 权重往上走并混合；没输出 → 拿最后一帧的输出（位姿记忆）按权重淡出。
    /// </summary>
    public void Run(PoseStage stage, Action evaluate)
    {
        // 不活动的常驻层只在还有权重（正在淡出）时需要进来——但不再求值，只拿位姿记忆淡出
        var evaluateNow = stage.AlwaysEvaluate || stage.Active;
        if (!evaluateNow && stage.Weight <= 0) return;

        var capture = _captures[stage];
        capture.BeginFrame();
        if (evaluateNow)
        {
            _stageCapture = capture;
            _dropDeadActive = _deadParameters is not null && stage.DropsDeadWrites;
            try
            {
                evaluate();
            }
            finally
            {
                _stageCapture = null;
                _dropDeadActive = false;
            }
        }

        var wrote = capture.WrittenCount > 0;
        stage.Wrote = wrote;
        stage.WrittenCount = capture.WrittenCount;
        if (_snapPending)
        {
            // 一键重置 / 刚加载后的那一帧：有输出的常驻层直接到位，其余立即清空，不做淡入
            stage.Weight = wrote ? 1 : 0;
            stage.MemoryCount = 0;
            if (!wrote) return;
            for (var i = 0; i < capture.WrittenCount; i++) _out[capture.Written[i]] = capture.Scratch[capture.Written[i]];
            stage.EnsureMemoryCapacity(capture.WrittenCount);
            stage.MemoryCount = capture.WrittenCount;
            for (var i = 0; i < capture.WrittenCount; i++)
            {
                stage.MemoryIndices[i] = capture.Written[i];
                stage.MemoryValues[i] = capture.Scratch[capture.Written[i]];
            }

            return;
        }

        if (wrote)
        {
            stage.EnsureMemoryCapacity(capture.WrittenCount);
            stage.MemoryCount = capture.WrittenCount;
            var weight = Advance(stage.Weight, up: true, stage.FadeInSeconds);
            stage.Weight = weight;
            var eased = stage.EasedWeight;
            for (var i = 0; i < capture.WrittenCount; i++)
            {
                var index = capture.Written[i];
                var value = capture.Scratch[index];
                stage.MemoryIndices[i] = index;
                stage.MemoryValues[i] = value;
                _out[index] += (value - _out[index]) * eased;
            }

            return;
        }

        stage.Weight = Advance(stage.Weight, up: false, stage.FadeOutSeconds);
        var fade = stage.EasedWeight;
        if (fade <= 0)
        {
            stage.MemoryCount = 0;
            return;
        }

        for (var i = 0; i < stage.MemoryCount; i++)
        {
            var index = stage.MemoryIndices[i];
            _out[index] += (stage.MemoryValues[i] - _out[index]) * fade;
        }
    }

    /// <summary>
    /// 用一块临时捕获缓冲求值（一个层内部要分别算几段、再自己按权重拼起来时用，例如手势的新旧两段交叉淡化）。
    /// 期间读参数：本缓冲写过就读本缓冲，否则读「当前层的缓冲写过就读它、再不然读下面各层合成到现在的值」，
    /// 所以后算的那一段是从「前一段已经混好的姿势」开始淡入的。
    /// 缓冲由调用方持有并复用（<c>new PoseCapture(参数个数)</c> 一次），稳态 0 分配。
    /// </summary>
    public void CaptureTemporary(PoseCapture capture, Action evaluate)
    {
        capture.BeginFrame();
        _temporaryCapture = capture;
        try
        {
            evaluate();
        }
        finally
        {
            _temporaryCapture = null;
        }
    }

    /// <summary>
    /// 登记实测无效的参数（改了它们画面一点不变，例如作者没做、只留了个开关的「翅膀」）。
    /// 之后各层对它们的写入一律丢弃——动作表里就算写了，也不会让一个「其实什么都没动」的动作占着调度。
    /// 面板手动拖它们仍然写值（只是没效果），UI 负责打「无效」标记。
    /// </summary>
    public void IgnoreLayerWritesTo(HashSet<int>? deadParameterIndices) => _deadParameters = deadParameterIndices;

    /// <summary>把一块临时缓冲的输出按权重混进当前层（只在层求值期间调用）。</summary>
    public void BlendIntoCurrent(PoseCapture source, float weight)
    {
        if (weight <= 0) return;
        var parameters = _model.Parameters;
        for (var i = 0; i < source.WrittenCount; i++)
        {
            var index = source.Written[i];
            var target = parameters[index];
            target.Value += (source.Scratch[index] - target.Value) * weight;
        }
    }

    /// <summary>
    /// 直通求值：期间读写参数都直接走 Core（物理求值 / 摇尾巴 / 固定参数回填）。
    /// 这些写入不该被记成「手动值」，否则关掉物理后输出会冻在最后一次的结果上。
    /// </summary>
    public void RunBypass(Action action)
    {
        var previous = _bypass;
        _bypass = true;
        try
        {
            action();
        }
        finally
        {
            _bypass = previous;
        }
    }

    /// <summary>
    /// 一键重置 / 刚加载：下一帧各层不做淡入淡出——有输出的常驻层直接到位，动作层立即清空
    /// （之后新一帧里活动起来的层照常淡入）。
    /// </summary>
    public void SnapAll()
    {
        foreach (var stage in _stages)
        {
            stage.MemoryCount = 0;
            stage.Wrote = false;
        }

        _snapPending = true;
    }

    /// <summary>某个参数的手动值（面板里设的值；没设过 = 默认值）。</summary>
    public float GetManualValue(int index) => _base[index];


    private float Advance(float weight, bool up, double seconds)
    {
        if (up)
        {
            if (seconds <= 0 || _deltaSeconds <= 0) return 1;
            var next = weight + (float)(_deltaSeconds / seconds);
            return next >= 1 ? 1 : next;
        }

        if (seconds <= 0 || _deltaSeconds <= 0) return 0;
        var value = weight - (float)(_deltaSeconds / seconds);
        return value <= 0 ? 0 : value;
    }

    // ---------- IParameterSink ----------

    public float ReadParameter(int index)
    {
        if (_bypass || !_composing) return _model.ReadCoreParameter(index);
        if (_temporaryCapture is { } temporary && temporary.Stamp[index] == temporary.FrameId)
            return temporary.Scratch[index];
        if (_stageCapture is { } stage && stage.Stamp[index] == stage.FrameId) return stage.Scratch[index];
        return _out[index];
    }

    public void WriteParameter(int index, float value)
    {
        if (_bypass)
        {
            // 直通期：直接写 Core，不记手动值
            _model.WriteCoreParameter(index, value);
            return;
        }

        var capture = _temporaryCapture ?? _stageCapture;
        if (capture is null)
        {
            // 合成期之外 = 手动值
            _base[index] = value;
            _model.WriteCoreParameter(index, value);
            return;
        }

        if (_deadParameters is not null && _dropDeadActive && _deadParameters.Contains(index))
        {
            DroppedWrites++;    // 手写动作表写了实测无效的参数：丢弃（累计计数给自检 / 调试看）
            _droppedByParameter[index] = _droppedByParameter.GetValueOrDefault(index) + 1;
            return;
        }

        capture.Mark(index);
        capture.Scratch[index] = value;
    }

    private readonly Dictionary<int, long> _droppedByParameter = new();

    /// <summary>手写动作表对无效参数的写入累计被丢弃的次数（作者的 motion 文件不计入）。</summary>
    public long DroppedWrites { get; private set; }

    /// <summary>按参数统计的丢弃次数（诊断用：动作表体检失败时直接看哪些参数还在被写）。</summary>
    public IReadOnlyDictionary<int, long> DroppedByParameter => _droppedByParameter;
}
