using System.Runtime.InteropServices;
using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>桌宠鼠标互动热区（画布像素坐标系，y 向下）。</summary>
public enum InteractionRegion
{
    None = 0,
    Head = 1,
    Forehead = 2,
    FaceLeft = 3,
    FaceRight = 4,
    Chest = 5,
    Waist = 6,
    Skirt = 7,
    Legs = 8,
    Feet = 9,
    HandLeft = 10,
    HandRight = 11,
    Tail = 12,
}

/// <summary>指针互动的语义动作（点一下 / 抚摸一下）。</summary>
public enum InteractionGesture
{
    Tap,
    Pet,
}

/// <summary>
/// 桌宠鼠标互动控制器（参考 FULASKA/-live2d- 的 zones.js / input.js / reactions.js / face-pinch.js / tickle.js）。
///
/// <para><b>热区</b>：加载后按「部件名 → drawable」缓存一份索引表（只扫一次，含 CubismCore 的
/// drawable→parent part 表 + cdi3 中文部件名）；每帧第一次 <see cref="HitTest"/> 时按当前蒙皮顶点
/// 重算各部件包围盒（脸/手/胸/腰/裙/腿/脚/尾巴），随后本帧的命中判定都是纯几何运算，单次目标 &lt;0.1ms。
/// 优先级：额头 9 &gt; 左右脸 8 &gt; 手 7 &gt; 头 6 &gt; 胸 5 &gt; 腰 4 &gt; 裙摆 3 &gt; 腿 2 &gt; 脚 1 &gt; 尾巴 0。</para>
///
/// <para><b>反应</b>：摸头 / 额头三态表情 / 捏脸 / 挠痒 / 护住 / 手部竖食指切换；互动期间停掉待机小动作，
/// 结束后 3 秒冷却。写出的参数全部记到 touched 列表，运行时下一帧开头复位（停手即回常态，不卡住）。</para>
/// </summary>
internal sealed class InteractionController
{
    // ---------- 区域元数据 ----------

    private static readonly InteractionRegion[] PriorityOrder =
    {
        InteractionRegion.Forehead,
        InteractionRegion.FaceLeft,
        InteractionRegion.FaceRight,
        InteractionRegion.HandLeft,
        InteractionRegion.HandRight,
        InteractionRegion.Head,
        InteractionRegion.Chest,
        InteractionRegion.Waist,
        InteractionRegion.Skirt,
        InteractionRegion.Legs,
        InteractionRegion.Feet,
        InteractionRegion.Tail,
    };

    private static readonly InteractionRegion[] AllRegions =
    {
        InteractionRegion.Head, InteractionRegion.Forehead, InteractionRegion.FaceLeft,
        InteractionRegion.FaceRight, InteractionRegion.Chest, InteractionRegion.Waist,
        InteractionRegion.Skirt, InteractionRegion.Legs, InteractionRegion.Feet,
        InteractionRegion.HandLeft, InteractionRegion.HandRight, InteractionRegion.Tail,
    };

    private static readonly string[] RegionDisplayNames =
    {
        "无", "头", "额头", "左脸", "右脸", "胸", "腰", "裙摆", "腿", "脚", "左手", "右手", "尾巴",
    };

    private enum PartGroup
    {
        Head,
        Face,
        Hand,
        Chest,
        Waist,
        Skirt,
        Legs,
        Tail,
        Count,
    }

    private static readonly string[] HeadKeywords =
    {
        "头", "脸", "脸底", "五官", "脖子", "耳朵", "发饰", "女仆发饰", "前发", "后发", "侧发",
        "下侧发", "后脑勺", "头发", "呆毛", "眉毛", "眼眶", "眼球", "眼白", "嘴", "鼻子",
    };

    private static readonly string[] ChestKeywords = { "胸衣", "胸", "领子", "肩膀花边", "花边" };
    private static readonly string[] SkirtKeywords = { "短裙", "长裙", "短裙后", "长裙后", "长裙蝴蝶结", "身后蝴蝶结" };
    private static readonly string[] TailKeywords = { "尾巴", "狗尾草" };

    private struct Bounds
    {
        public float MinX;
        public float MinY;
        public float MaxX;
        public float MaxY;

        public bool IsValid => MaxX > MinX && MaxY > MinY;

        public bool Contains(float x, float y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

        public float CenterX => (MinX + MaxX) * 0.5f;
        public float CenterY => (MinY + MaxY) * 0.5f;
    }

    private enum ReactionKind
    {
        None,
        PatHead,
        Pinch,
        Tickle,
        Protect,
        Shy,
    }

    private const double PatHeadDuration = 0.8;
    private const double TickleDuration = 1.2;
    private const double ProtectDuration = 1.5;
    private const double ShyDuration = 1.0;
    private const double ReactionCooldownSeconds = 3.0;
    private const double ExpressionFadeSeconds = 0.25;
    private const double PinchMaxPullRatio = 0.07;   // 拖动画布宽度的 7% 算捏满
    private const double TapMaxMoveRatio = 0.02;     // 手抖范围内算点按
    private const double DragMinMoveRatio = 0.08;    // 超过画布宽度 8% 算拖动（和窗口拖动区分）
    private const double PetStepRatio = 0.03;        // 抚摸每滑过画布宽度 3% 算一次
    private const double TapMaxSeconds = 0.42;

    // ---------- 依赖 ----------

    private readonly CubismModel _model;
    private readonly CubismDrawable[] _drawables;
    private int[][] _groupIndices = Array.Empty<int[]>();
    private readonly float _canvasWidth;
    private readonly float _canvasHeight;

    private readonly Func<string, ExpressionEntry?> _findExpression;
    private readonly Action<ExpressionEntry, double> _applyExpression;
    private readonly Action<double> _clearExpressions;
    private readonly Func<string, bool> _playGesture;
    private readonly Action _stopGesture;
    private readonly Func<string?> _activeGesture;
    private readonly Action<string, string> _raiseInteraction;
    private readonly Action _onInteractionStart;

    private readonly Dictionary<string, CubismParameter?> _parameters = new(64, StringComparer.Ordinal);

    // ---------- 几何缓存 ----------

    private readonly Bounds[] _groupBounds = new Bounds[(int)PartGroup.Count];
    private readonly float[] _regionCenterX = new float[13];
    private readonly float[] _regionCenterY = new float[13];
    private readonly bool[] _regionCenterValid = new bool[13];

    private bool _geometryDirty = true;
    private bool _geometryReady;

    private Bounds _headRect;
    private Bounds _foreheadRect;
    private bool _hasFaceEllipse;
    private float _faceCx;
    private float _faceCy;
    private float _faceRx;
    private float _faceRy;
    private Bounds _chestRect;
    private Bounds _waistRect;
    private Bounds _skirtRect;
    private Bounds _legsRect;
    private Bounds _feetRect;
    private Bounds _tailRect;
    private Bounds _handLeftRect;
    private Bounds _handRightRect;

    // ---------- 交互状态 ----------

    private int[] _touchedIndices = new int[64];
    private int _touchedCount;

    private bool _enabled = true;
    private bool _dragPhysicsEnabled = true;
    private bool _blocked;
    private bool _mocapFace;
    private bool _speechActive;

    private double _age;
    private double _cooldownRemaining;

    private bool _pressed;
    private InteractionRegion _activeRegion;
    private float _lastX;
    private float _lastY;
    private double _downAge;
    private float _moved;
    private float _stepDistance;
    private int _petSteps;
    private bool _dragging;

    private float _petSwayX;
    private float _petSwayY;
    private float _petSwayTargetX;
    private float _petSwayTargetY;
    private bool _petSwayArmed;

    private float _dragX;
    private float _dragY;
    private float _dragTargetX;
    private float _dragTargetY;

    private ReactionKind _reaction;
    private double _reactionElapsed;
    private double _reactionDuration;
    private bool _reactionExpressionActive;
    private string? _faceExpressionName;
    private int _faceCycleStep;
    private int _faceCycleGlassesToggle;

    // 捏脸（按住脸拖动）
    private bool _pinchActive;
    private string _pinchSide = "L";
    private float _pinchAnchorX;
    private float _pinchAnchorY;
    private float _pinchTarget;
    private float _pinchMagnitude;
    private float _pinchHeadX;
    private float _pinchHeadZ;
    private float _pinchWigglePhase;

    public InteractionController(
        CubismModel model,
        PartBinding[] partBindings,
        Func<string, ExpressionEntry?> findExpression,
        Action<ExpressionEntry, double> applyExpression,
        Action<double> clearExpressions,
        Func<string, bool> playGesture,
        Action stopGesture,
        Func<string?> activeGesture,
        Action<string, string> raiseInteraction,
        Action onInteractionStart)
    {
        _model = model;
        _drawables = model.Drawables;
        _canvasWidth = Math.Max(1f, model.CanvasWidthPixels);
        _canvasHeight = Math.Max(1f, model.CanvasHeightPixels);
        _findExpression = findExpression;
        _applyExpression = applyExpression;
        _clearExpressions = clearExpressions;
        _playGesture = playGesture;
        _stopGesture = stopGesture;
        _activeGesture = activeGesture;
        _raiseInteraction = raiseInteraction;
        _onInteractionStart = onInteractionStart;

        BuildGroupIndices(partBindings);
        CacheParameters();
    }

    // ---------- 对外状态 ----------

    /// <summary>是否开启鼠标互动（关闭后 HitTest 返回 None、所有交互 no-op）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) StopImmediate();
        }
    }

    /// <summary>拖动物理开关（关闭时 ApplyDragImpulse 不生效）。</summary>
    public bool DragPhysicsEnabled
    {
        get => _dragPhysicsEnabled;
        set => _dragPhysicsEnabled = value;
    }

    /// <summary>所有区域（不含 None）。</summary>
    public IReadOnlyList<InteractionRegion> RegionIds => AllRegions;

    /// <summary>当前按下的热区（没有按下 = None；用于 UI 悬停高亮 / 调试显示）。</summary>
    public InteractionRegion ActiveRegion => _pressed || _reaction != ReactionKind.None ? _activeRegion : InteractionRegion.None;

    /// <summary>是否有互动正在进行（按下 / 反应中 / 捏脸中）。</summary>
    public bool IsInteracting => _pressed || _reaction != ReactionKind.None;

    /// <summary>互动结束后的冷却（秒）；运行时的待机小动作 / 随机手势要等它归零。</summary>
    public double CooldownRemaining => _cooldownRemaining;

    /// <summary>本帧写过的参数个数（运行时下一帧开头复位）。</summary>
    public int TouchedCount => _touchedCount;

    public int GetTouchedIndex(int index) => _touchedIndices[index];

    /// <summary>额头三态表情当前是否处于「非常态」（待机小动作以此判断别清掉用户表情）。</summary>
    public bool HasFaceExpression => _faceExpressionName is not null;

    /// <summary>区域中文名。</summary>
    public static string GetRegionDisplayName(InteractionRegion region)
    {
        var index = (int)region;
        return index >= 0 && index < RegionDisplayNames.Length ? RegionDisplayNames[index] : "无";
    }

    // ---------- 几何 / 命中 ----------

    /// <summary>让本帧的顶点几何缓存失效（运行时每次 model.Update 后调用一次）。</summary>
    public void InvalidateGeometry()
    {
        _geometryDirty = true;
    }

    /// <summary>画布像素坐标命中哪个热区；关闭互动 / 拿不到几何时返回 None。</summary>
    public InteractionRegion HitTest(float canvasX, float canvasY)
    {
        if (!_enabled) return InteractionRegion.None;
        EnsureGeometry();
        if (!_geometryReady) return InteractionRegion.None;

        foreach (var region in PriorityOrder)
        {
            if (Contains(region, canvasX, canvasY)) return region;
        }

        return InteractionRegion.None;
    }

    /// <summary>
    /// 嘴部在画布上的大致中心 + 半径：取脸部包围盒下半段（脸椭圆中心再往下 45%），
    /// 给投喂的「饭团掉进嘴里」判定用（近似值，不依赖嘴部 drawable）。
    /// </summary>
    public bool TryGetMouthCanvasCenter(out float canvasX, out float canvasY)
    {
        canvasX = 0;
        canvasY = 0;
        EnsureGeometry();   // 第一次访问时按当前蒙皮算一遍部件包围盒（不点模型也能拿到脸）
        if (!_geometryReady || !_hasFaceEllipse) return false;
        canvasX = _faceCx;
        canvasY = _faceCy + (_faceRy * 0.45f);
        return !float.IsNaN(canvasX) && !float.IsNaN(canvasY);
    }

    /// <summary>嘴部判定半径（画布像素；按脸宽估算，夹在 18 ~ 80）。</summary>
    public float MouthCanvasRadius
    {
        get
        {
            EnsureGeometry();
            return _geometryReady && _hasFaceEllipse ? Math.Clamp(_faceRx * 0.55f, 18f, 80f) : 30f;
        }
    }

    /// <summary>某个热区的代表点（UI 调试钩子 / 自检模拟点击用）。拿不到返回 false。</summary>
    public bool GetRegionCenter(InteractionRegion region, out float canvasX, out float canvasY)
    {
        canvasX = 0;
        canvasY = 0;
        if (region == InteractionRegion.None) return false;
        EnsureGeometry();
        if (!_geometryReady) return false;
        var index = (int)region;
        if (index < 0 || index >= _regionCenterValid.Length || !_regionCenterValid[index]) return false;
        canvasX = _regionCenterX[index];
        canvasY = _regionCenterY[index];
        return true;
    }

    private bool Contains(InteractionRegion region, float x, float y)
    {
        switch (region)
        {
            case InteractionRegion.Forehead: return _foreheadRect.Contains(x, y);
            case InteractionRegion.FaceLeft: return ContainsHalfEllipse(x, y, left: true);
            case InteractionRegion.FaceRight: return ContainsHalfEllipse(x, y, left: false);
            case InteractionRegion.HandLeft: return _handLeftRect.Contains(x, y);
            case InteractionRegion.HandRight: return _handRightRect.Contains(x, y);
            case InteractionRegion.Head: return _headRect.Contains(x, y);
            case InteractionRegion.Chest: return _chestRect.Contains(x, y);
            case InteractionRegion.Waist: return _waistRect.Contains(x, y);
            case InteractionRegion.Skirt: return _skirtRect.Contains(x, y);
            case InteractionRegion.Legs: return _legsRect.Contains(x, y);
            case InteractionRegion.Feet: return _feetRect.Contains(x, y);
            case InteractionRegion.Tail: return _tailRect.Contains(x, y);
            default: return false;
        }
    }

    private bool ContainsHalfEllipse(float x, float y, bool left)
    {
        if (!_hasFaceEllipse) return false;
        var dx = (x - _faceCx) / _faceRx;
        var dy = (y - _faceCy) / _faceRy;
        if ((dx * dx) + (dy * dy) > 1f) return false;
        return left ? x <= _faceCx : x >= _faceCx;
    }

    // ---------- 指针状态机 ----------

    /// <summary>按下：命中热区才接受（返回 true 时 UI 应捕获指针、走互动而不是拖窗口）。</summary>
    public bool PointerDown(float canvasX, float canvasY)
    {
        if (!CanInteract()) return false;
        var region = HitTest(canvasX, canvasY);
        if (region == InteractionRegion.None) return false;
        StartPress(region, canvasX, canvasY);
        return true;
    }

    /// <summary>显式按指定区域按下（调试钩子 / 自检用；不经过命中测试）。</summary>
    public void Press(InteractionRegion region, float canvasX, float canvasY)
    {
        if (!CanInteract() || region == InteractionRegion.None) return;
        StartPress(region, canvasX, canvasY);
    }

    /// <summary>拖动 / 抚摸推进（返回是否有互动在进行）。</summary>
    public bool PointerMove(float canvasX, float canvasY)
    {
        if (!_pressed) return false;
        if (!_enabled)
        {
            PointerUp();
            return false;
        }

        var dx = canvasX - _lastX;
        var dy = canvasY - _lastY;
        _lastX = canvasX;
        _lastY = canvasY;
        var distance = MathF.Sqrt((dx * dx) + (dy * dy));
        _moved += distance;
        _stepDistance += distance;

        var region = _activeRegion;
        if (region is InteractionRegion.FaceLeft or InteractionRegion.FaceRight)
        {
            UpdatePinchAnchor(dx, dy);
        }
        else if (region is InteractionRegion.Head or InteractionRegion.Forehead)
        {
            _petSwayTargetX = Math.Clamp(_petSwayTargetX + (dx / (_canvasWidth * 0.5f) * 0.5f), -1f, 1f);
            _petSwayTargetY = Math.Clamp(_petSwayTargetY + (dy / (_canvasHeight * 0.5f) * 0.5f), -1f, 1f);
            _petSwayArmed = true;
        }

        // 抚摸判定：每滑过画布宽度 3% 记一步；第一步触发分区反应
        if (!_pinchActive && _stepDistance >= _canvasWidth * PetStepRatio)
        {
            _stepDistance = 0;
            _petSteps++;
            if (_petSteps == 1) HandlePet(region);
        }

        if (_moved > _canvasWidth * DragMinMoveRatio && _petSteps == 0) _dragging = true;
        return true;
    }

    /// <summary>抬起：内部判定 tap / pet / drag 并触发对应反应。</summary>
    public void PointerUp()
    {
        if (!_pressed) return;
        _pressed = false;
        var region = _activeRegion;
        var duration = _age - _downAge;

        if (_pinchActive)
        {
            ReleasePinch();
        }
        else if (!_dragging && _petSteps == 0 && _moved <= _canvasWidth * TapMaxMoveRatio && duration <= TapMaxSeconds)
        {
            HandleTap(region);
        }

        _petSwayArmed = false;
        _activeRegion = InteractionRegion.None;
        if (_reaction == ReactionKind.None && _cooldownRemaining <= 0) _cooldownRemaining = ReactionCooldownSeconds;
    }

    /// <summary>抬起（带坐标；先按最后位置推进一次再抬起）。</summary>
    public void PointerUp(float canvasX, float canvasY)
    {
        if (_pressed) PointerMove(canvasX, canvasY);
        PointerUp();
    }

    /// <summary>显式触发一次语义互动（调试钩子 / 菜单用）。</summary>
    public bool TriggerInteraction(InteractionRegion region, InteractionGesture gesture)
    {
        if (!CanInteract() || region == InteractionRegion.None) return false;
        _onInteractionStart();
        if (gesture == InteractionGesture.Pet) HandlePet(region);
        else HandleTap(region);
        return true;
    }

    // ---------- 拖动物理 ----------

    /// <summary>拖动桌宠时喂位移（像素），给物理输入一个冲量，头发 / 裙子会甩起来；约 0.6s 衰减到 0。</summary>
    public void ApplyDragImpulse(float dxPixels, float dyPixels)
    {
        if (!_enabled || !_dragPhysicsEnabled) return;
        var scale = 30f / _canvasWidth;
        _dragTargetX = Math.Clamp(_dragTargetX + (dxPixels * scale), -30f, 30f);
        _dragTargetY = Math.Clamp(_dragTargetY + (dyPixels * scale), -30f, 30f);
    }

    // ---------- 额头三态表情 ----------

    /// <summary>额头点击：常态 → 爱心眼 → 单片眼镜 / 黑框眼镜交替 → 常态。返回当前状态中文名。</summary>
    public string CycleFaceExpression()
    {
        if (!_enabled) return _faceExpressionName is null ? "常态" : _faceExpressionName;
        _faceCycleStep = (_faceCycleStep + 1) % 3;
        string? next;
        string display;
        switch (_faceCycleStep)
        {
            case 0:
                next = null;
                display = "常态";
                break;
            case 1:
                next = "爱心眼";
                display = "爱心眼";
                break;
            default:
                next = (_faceCycleGlassesToggle++ % 2 == 0) ? "单片眼镜" : "黑框眼镜";
                display = next;
                break;
        }

        if (next is null)
        {
            _faceExpressionName = null;
            _clearExpressions(0.3);
        }
        else
        {
            // 三态循环同一时间只显示一个表情：先把上一个表情淡出，再挂新的
            _faceExpressionName = next;
            _clearExpressions(0.15);
            ApplyExpressionByName(next, ExpressionFadeSeconds);
        }

        _raiseInteraction("face_cycle", "额头表情");
        return display;
    }

    // ---------- 每帧更新 ----------

    /// <summary>
    /// 推进一帧：拖动物理衰减、捏脸跟随、抚摸摆头、定时反应包络。
    /// <paramref name="blocked"/> = 用户动作 / 表演 / 舞蹈进行中（此时不接受新的互动）；
    /// <paramref name="mocapFace"/> / <paramref name="speechActive"/> 用于参数优先级让位。
    /// </summary>
    public void Update(double deltaSeconds, bool blocked, bool mocapFace, bool speechActive)
    {
        _touchedCount = 0;
        _blocked = blocked;
        _mocapFace = mocapFace;
        _speechActive = speechActive;
        _age += deltaSeconds;

        if (_cooldownRemaining > 0)
        {
            _cooldownRemaining -= deltaSeconds;
            if (_cooldownRemaining < 0) _cooldownRemaining = 0;
        }

        if (!_enabled)
        {
            if (IsInteracting || _reaction != ReactionKind.None) StopImmediate();
            return;
        }

        if (blocked && IsInteracting)
        {
            // 用户突然播了动作 / 表演 / 舞蹈：让位，互动收掉（不卡在描一半）
            StopImmediate();
        }

        UpdateDragPhysics(deltaSeconds);
        UpdatePetSway(deltaSeconds);
        UpdateReaction(deltaSeconds);
    }

    /// <summary>立即停止所有互动状态（不触发语音事件，不停用户演示）。</summary>
    public void StopImmediate()
    {
        if (_reaction == ReactionKind.Protect) _stopGesture();
        _pressed = false;
        _dragging = false;
        _pinchActive = false;
        _pinchMagnitude = 0;
        _petSteps = 0;
        _moved = 0;
        _activeRegion = InteractionRegion.None;
        _petSwayArmed = false;
        _petSwayX = 0;
        _petSwayY = 0;
        _petSwayTargetX = 0;
        _petSwayTargetY = 0;
        _dragX = 0;
        _dragY = 0;
        _dragTargetX = 0;
        _dragTargetY = 0;
        _cooldownRemaining = 0;
        _reaction = ReactionKind.None;
        _reactionElapsed = 0;
        ClearReactionExpression();
    }

    /// <summary>运行时下一帧开头复位「上一帧互动写过的参数」。</summary>
    public void ResetTouched(CubismParameter[] parameters)
    {
        for (var i = 0; i < _touchedCount; i++)
        {
            var parameter = parameters[_touchedIndices[i]];
            parameter.Value = parameter.DefaultValue;
        }

        _touchedCount = 0;
    }

    // ---------- 内部：按下 / 反应派发 ----------

    private bool CanInteract() => _enabled && !_blocked;

    private void StartPress(InteractionRegion region, float canvasX, float canvasY)
    {
        // 换目标：上一段反应直接收掉，避免「黑脸 + 捏脸」叠在一起
        if (_reaction != ReactionKind.None) FinishReaction(immediate: true);

        _onInteractionStart();
        _pressed = true;
        _activeRegion = region;
        _lastX = canvasX;
        _lastY = canvasY;
        _downAge = _age;
        _moved = 0;
        _stepDistance = 0;
        _petSteps = 0;
        _dragging = false;
        _petSwayTargetX = 0;
        _petSwayTargetY = 0;
        _petSwayArmed = region is InteractionRegion.Head or InteractionRegion.Forehead;
        _cooldownRemaining = 0;

        if (region is InteractionRegion.FaceLeft or InteractionRegion.FaceRight) StartPinch(region);
    }

    private void HandleTap(InteractionRegion region)
    {
        switch (region)
        {
            case InteractionRegion.Head:
            case InteractionRegion.FaceLeft:
            case InteractionRegion.FaceRight:
                StartPatHead();
                break;
            case InteractionRegion.Forehead:
                CycleFaceExpression();
                break;
            case InteractionRegion.Chest:
            case InteractionRegion.Skirt:
                StartProtect();
                break;
            case InteractionRegion.Waist:
            case InteractionRegion.Legs:
            case InteractionRegion.Feet:
                StartTickle();
                break;
            case InteractionRegion.HandLeft:
            case InteractionRegion.HandRight:
                ToggleHand();
                break;
            case InteractionRegion.Tail:
                StartShy("摸尾巴", "tickle");
                break;
        }
    }

    private void HandlePet(InteractionRegion region)
    {
        switch (region)
        {
            case InteractionRegion.Head:
            case InteractionRegion.Forehead:
            case InteractionRegion.FaceLeft:
            case InteractionRegion.FaceRight:
                StartPatHead();
                break;
            case InteractionRegion.Chest:
            case InteractionRegion.Skirt:
                StartProtect();
                break;
            case InteractionRegion.Waist:
            case InteractionRegion.Legs:
            case InteractionRegion.Feet:
                StartTickle();
                break;
            case InteractionRegion.HandLeft:
            case InteractionRegion.HandRight:
                StartShy("摸手", "hand");
                break;
            case InteractionRegion.Tail:
                StartShy("摸尾巴", "tickle");
                break;
        }
    }

    private void StartPatHead()
    {
        StartReaction(ReactionKind.PatHead, PatHeadDuration);
        _raiseInteraction("pat_head", "摸头");
    }

    private void StartTickle()
    {
        StartReaction(ReactionKind.Tickle, TickleDuration);
        ApplyReactionExpression("＞＜");
        _raiseInteraction("tickle", "挠痒");
    }

    private void StartProtect()
    {
        StartReaction(ReactionKind.Protect, ProtectDuration);
        _playGesture("抱胸致意");
        // 表情交给会话按好感度选（高好感脸红+爱心眼、中好感脸红+＞＜、低好感黑脸），
        // 这里不再固定黑脸 / 屑屑眼，避免高好感时黑脸和爱心眼叠在一起。
        _raiseInteraction("protect", "护住");
    }

    private void StartShy(string displayName, string voiceEvent)
    {
        StartReaction(ReactionKind.Shy, ShyDuration);
        ApplyReactionExpression("＞＜", "脸红");
        _raiseInteraction(voiceEvent, displayName);
    }

    private void ToggleHand()
    {
        var active = _activeGesture();
        if (string.Equals(active, "竖起食指", StringComparison.Ordinal))
        {
            _stopGesture();
            _raiseInteraction("hand", "放下手");
        }
        else
        {
            _playGesture("竖起食指");
            _raiseInteraction("hand", "竖起食指");
        }
    }

    private void StartReaction(ReactionKind kind, double duration)
    {
        if (_reaction != ReactionKind.None) FinishReaction(immediate: true);
        _reaction = kind;
        _reactionElapsed = 0;
        _reactionDuration = duration;
        _cooldownRemaining = 0;
    }

    private void FinishReaction(bool immediate)
    {
        var kind = _reaction;
        _reaction = ReactionKind.None;
        _reactionElapsed = 0;
        if (kind == ReactionKind.Protect) _stopGesture();
        if (kind is ReactionKind.Tickle or ReactionKind.Protect or ReactionKind.Shy or ReactionKind.Pinch)
        {
            ClearReactionExpression();
        }

        _pinchActive = false;
        _pinchMagnitude = 0;
        if (!immediate) _cooldownRemaining = ReactionCooldownSeconds;
    }

    // ---------- 内部：捏脸 ----------

    private void StartPinch(InteractionRegion region)
    {
        _reaction = ReactionKind.Pinch;
        _reactionElapsed = 0;
        _reactionDuration = 0;
        _pinchActive = true;
        _pinchSide = region == InteractionRegion.FaceLeft ? "L" : "R";
        _pinchAnchorX = 0;
        _pinchAnchorY = 0;
        _pinchTarget = 0;
        _pinchMagnitude = 0;
        _pinchHeadX = 0;
        _pinchHeadZ = 0;
        _pinchWigglePhase = 0;
        ApplyReactionExpression("＞＜");
        _raiseInteraction("pinch", "捏脸");
    }

    private void UpdatePinchAnchor(float dx, float dy)
    {
        var limit = _canvasWidth * (float)(PinchMaxPullRatio * 1.6);
        _pinchAnchorX = Math.Clamp(_pinchAnchorX + dx, -limit, limit);
        _pinchAnchorY = Math.Clamp(_pinchAnchorY + dy, -limit, limit);
        var pulled = MathF.Sqrt((_pinchAnchorX * _pinchAnchorX) + (_pinchAnchorY * _pinchAnchorY));
        var ratio = Math.Clamp(pulled / (_canvasWidth * (float)PinchMaxPullRatio), 0f, 1f);
        _pinchTarget = MathF.Pow(ratio, 1.15f);
    }

    private void ReleasePinch()
    {
        _pinchActive = false;
        _pinchTarget = 0;
    }

    // ---------- 内部：每帧写参数 ----------

    private void UpdateDragPhysics(double deltaSeconds)
    {
        if (_dragTargetX == 0 && _dragTargetY == 0 && MathF.Abs(_dragX) < 0.01f && MathF.Abs(_dragY) < 0.01f) return;
        if (!_dragPhysicsEnabled)
        {
            _dragTargetX = 0;
            _dragTargetY = 0;
            _dragX = 0;
            _dragY = 0;
            return;
        }

        var follow = 1.0 - Math.Exp(-deltaSeconds / 0.05);
        _dragX += (float)((_dragTargetX - _dragX) * follow);
        _dragY += (float)((_dragTargetY - _dragY) * follow);
        var decay = (float)Math.Exp(-deltaSeconds / 0.18);
        _dragTargetX *= decay;
        _dragTargetY *= decay;
        if (MathF.Abs(_dragTargetX) < 0.02f) _dragTargetX = 0;
        if (MathF.Abs(_dragTargetY) < 0.02f) _dragTargetY = 0;

        Write("PositionX", _dragX);
        Write("PositionY", _dragY);
        Write("AngleX", _dragX * 0.45f);
        Write("AngleZ", -_dragY * 0.35f);
    }

    private void UpdatePetSway(double deltaSeconds)
    {
        var armed = _petSwayArmed && _pressed;
        var follow = 1.0 - Math.Exp(-deltaSeconds * (armed ? 14.0 : 6.0));
        _petSwayX += (float)((_petSwayTargetX - _petSwayX) * follow);
        _petSwayY += (float)((_petSwayTargetY - _petSwayY) * follow);
        if (!armed && MathF.Abs(_petSwayX) < 0.003f && MathF.Abs(_petSwayY) < 0.003f)
        {
            _petSwayX = 0;
            _petSwayY = 0;
            _petSwayTargetX = 0;
            _petSwayTargetY = 0;
            return;
        }

        if (!armed && _petSwayX == 0 && _petSwayY == 0) return;
        AddFace("ParamAngleX", _petSwayX * 16f);
        AddFace("ParamAngleZ", _petSwayY * 14f);
        AddFace("ParamAngleY", -_petSwayY * 6f);
        Write("AngleX", _petSwayX * 2.5f);
        Write("AngleZ", _petSwayY * 2.0f);
    }

    private void UpdateReaction(double deltaSeconds)
    {
        switch (_reaction)
        {
            case ReactionKind.None:
                return;
            case ReactionKind.Pinch:
                UpdatePinch(deltaSeconds);
                return;
            default:
                _reactionElapsed += deltaSeconds;
                if (_reactionElapsed >= _reactionDuration)
                {
                    FinishReaction(immediate: false);
                    return;
                }

                var t = (float)_reactionElapsed;
                var progress = t / (float)_reactionDuration;
                switch (_reaction)
                {
                    case ReactionKind.PatHead:
                        WritePatHead(t);
                        break;
                    case ReactionKind.Tickle:
                        WriteTickle(t);
                        break;
                    case ReactionKind.Protect:
                        WriteProtect(t);
                        break;
                    case ReactionKind.Shy:
                        WriteShy(t, progress);
                        break;
                }

                return;
        }
    }

    private void UpdatePinch(double deltaSeconds)
    {
        var follow = 1.0 - Math.Exp(-deltaSeconds * 10.0);
        _pinchMagnitude += (float)((_pinchTarget - _pinchMagnitude) * follow);
        if (!_pinchActive && _pinchMagnitude < 0.004f)
        {
            _pinchMagnitude = 0;
            FinishReaction(immediate: false);
            return;
        }

        var magnitude = Math.Clamp(_pinchMagnitude, 0f, 1f);
        if (magnitude <= 0) return;
        _pinchWigglePhase += (float)(deltaSeconds * Math.PI * 2.0 * 3.6);

        var leftSide = _pinchSide == "L";
        // 左右映射和外号相反（实测同参考项目）：左脸热区捏 → 鼓画面右半边（CheeckPuffR）
        Write("CheeckPuff", magnitude);
        Write("CheeckPuff2", magnitude * 0.6f);
        Write("CheeckPuffL", leftSide ? 0f : magnitude);
        Write("CheeckPuffR", leftSide ? magnitude : 0f);
        var wiggle = MathF.Sin(_pinchWigglePhase) * magnitude * 0.32f;
        Write("CheeksWiggleX", wiggle);
        Write("CheeksWiggleXR", -wiggle);
        Write("CheeksWiggleXL", wiggle * 0.6f);
        Write("Mouthpucker", magnitude * 0.35f);

        var headTargetX = Math.Clamp(_pinchAnchorX / (_canvasWidth * (float)PinchMaxPullRatio), -1f, 1f) * 12f;
        var headTargetZ = Math.Clamp(_pinchAnchorY / (_canvasWidth * (float)PinchMaxPullRatio), -1f, 1f) * 8f;
        var headFollow = 1.0 - Math.Exp(-deltaSeconds * 8.0);
        _pinchHeadX += (float)((headTargetX - _pinchHeadX) * headFollow);
        _pinchHeadZ += (float)((headTargetZ - _pinchHeadZ) * headFollow);
        AddFace("ParamAngleX", _pinchHeadX);
        AddFace("ParamAngleZ", _pinchHeadZ);
        AddFace("ParamMouthForm", (-0.4f * magnitude) + (headTargetZ * 0.01f));

        var bodyWiggle = MathF.Sin(_pinchWigglePhase * 0.7f) * magnitude * 3.0f;
        Write("AngleZ", bodyWiggle);
        Write("ShoulderY", bodyWiggle * 0.4f);
    }

    private void WritePatHead(float t)
    {
        var duration = (float)PatHeadDuration;
        var envelope = Envelope(t, duration, 0.12f, 0.22f);
        if (envelope <= 0) return;
        var echo = MathF.Sin(t * MathF.PI * 2f * 2.2f) * envelope;
        AddFace("ParamAngleZ", (4.5f * envelope) + (2.5f * echo));
        AddFace("ParamAngleY", 2.2f * envelope);
        AddFace("EyeSmile", 0.7f * envelope);
        AddFace("ParamMouthForm", 0.25f * envelope);
        Write("AngleZ", 1.5f * envelope);
        Write("ShoulderY", -1.2f * envelope);
        Write("PositionX", echo * 0.8f);
    }

    private void WriteTickle(float t)
    {
        var duration = (float)TickleDuration;
        var envelope = Envelope(t, duration, 0.12f, 0.22f);
        if (envelope <= 0) return;
        var phase = t * MathF.PI * 2f * 2.4f;
        var wave = MathF.Sin(phase);
        Write("AngleX", 3.5f * envelope * wave);
        Write("AngleZ", 4.5f * envelope * MathF.Sin(phase + 0.6f));
        Write("PositionX", 2.2f * envelope * wave);
        Write("PositionY", 1.4f * envelope * MathF.Cos(phase));
        Write("ShoulderX", 2.0f * envelope * wave);
        Write("ShoulderY", 1.4f * envelope * MathF.Sin(phase + 1.0f));
        Write("LArm", 0.28f * envelope * MathF.Sin(phase + 0.4f));
        Write("RArm", -0.28f * envelope * MathF.Sin(phase + 0.4f));
        AddFace("ParamAngleZ", 3.0f * envelope * MathF.Sin(phase + 0.3f));
        AddFace("ParamAngleY", 1.5f * envelope * MathF.Sin(phase * 0.5f));
        for (var i = 1; i <= 3; i++)
        {
            var ear = MathF.Sin((phase * 1.3f) + (i * 0.3f)) * 0.4f * envelope * (1f - (i * 0.18f));
            Write("LEar" + i, ear);
            Write("REar" + i, ear);
        }
    }

    private void WriteProtect(float t)
    {
        var duration = (float)ProtectDuration;
        var envelope = Envelope(t, duration, 0.22f, 0.3f);
        if (envelope <= 0) return;
        Write("LeanInput", 12f * envelope);
        AddFace("ParamAngleX", 10f * envelope);
        AddFace("ParamAngleY", 4f * envelope);
        Write("HipAngleZ", 2f * envelope);
        Write("AngleZ", -1.5f * envelope);
    }

    private void WriteShy(float t, float progress)
    {
        var duration = (float)ShyDuration;
        var envelope = Envelope(t, duration, 0.16f, 0.22f);
        if (envelope <= 0) return;
        AddFace("ParamAngleZ", -3.5f * envelope);
        AddFace("ParamAngleY", 1.2f * envelope);
        AddFace("EyeSmile", 0.45f * envelope);
        Write("AngleZ", -1.2f * envelope);
        Write("PositionX", MathF.Sin(progress * MathF.PI) * 0.6f * envelope);
    }

    private static float Envelope(float t, float duration, float fadeIn, float fadeOut)
    {
        var inK = Math.Clamp(t / MathF.Max(0.001f, fadeIn), 0f, 1f);
        var outK = Math.Clamp((duration - t) / MathF.Max(0.001f, fadeOut), 0f, 1f);
        return DemoMath.SmoothStep(MathF.Min(inK, outK));
    }

    // ---------- 内部：写参数 / 表情 ----------

    private void Write(string id, float value)
    {
        var parameter = FindParameter(id);
        if (parameter is null) return;
        parameter.Value = Math.Clamp(value, parameter.Minimum, parameter.Maximum);
        MarkTouched(parameter);
    }

    /// <summary>面部 / 头部参数：动捕有脸时让位（互动优先级低于动捕）；语音说话时嘴部参数也让位。</summary>
    private void WriteFace(string id, float value)
    {
        if (_mocapFace && IsHeadOrEyeParameter(id)) return;
        if (_speechActive && IsMouthParameter(id)) return;
        Write(id, value);
    }

    /// <summary>加性写（在眼控 / 音乐写完之后再叠一层，例如抚摸摆头）。</summary>
    private void AddFace(string id, float delta)
    {
        if (delta == 0) return;
        if (_mocapFace && IsHeadOrEyeParameter(id)) return;
        if (_speechActive && IsMouthParameter(id)) return;
        var parameter = FindParameter(id);
        if (parameter is null) return;
        parameter.Value = Math.Clamp(parameter.Value + delta, parameter.Minimum, parameter.Maximum);
        MarkTouched(parameter);
    }

    private static bool IsHeadOrEyeParameter(string id)
        => id.StartsWith("ParamAngle", StringComparison.Ordinal) ||
           id.StartsWith("Angle", StringComparison.Ordinal) ||
           id.StartsWith("ParamEye", StringComparison.Ordinal) ||
           id is "EyeSmile" or "LEyeSquint" or "REyeSquint";

    private static bool IsMouthParameter(string id)
        => id.StartsWith("ParamMouth", StringComparison.Ordinal) ||
           id is "JawOpen" or "Mouthpucker" or "MouthSmile_L" or "MouthSmile_R";

    private void ApplyReactionExpression(params string[] names)
    {
        ClearReactionExpression();
        foreach (var name in names)
        {
            var entry = _findExpression(name);
            if (entry is null) continue;
            _applyExpression(entry, ExpressionFadeSeconds);
            _reactionExpressionActive = true;
        }
    }

    private void ClearReactionExpression()
    {
        if (!_reactionExpressionActive) return;
        _reactionExpressionActive = false;
        _clearExpressions(ExpressionFadeSeconds);
        // 额头三态表情是长期状态，临时反应结束后重新挂回来
        if (_faceExpressionName is not null) ApplyExpressionByName(_faceExpressionName, ExpressionFadeSeconds);
    }

    private bool ApplyExpressionByName(string name, double fadeSeconds)
    {
        var entry = _findExpression(name);
        if (entry is null) return false;
        _applyExpression(entry, fadeSeconds);
        return true;
    }

    private void MarkTouched(CubismParameter parameter)
    {
        if (_touchedCount >= _touchedIndices.Length)
        {
            Array.Resize(ref _touchedIndices, _touchedIndices.Length * 2);
        }

        _touchedIndices[_touchedCount++] = parameter.Index;
    }

    private CubismParameter? FindParameter(string id)
        => _parameters.TryGetValue(id, out var parameter) ? parameter : null;

    // ---------- 内部：构建索引 / 缓存 ----------

    private void BuildGroupIndices(PartBinding[] partBindings)
    {
        var partCount = _model.PartCount;
        var parentPart = ReadParentPartIndices();
        var partNames = new string[partCount];
        for (var i = 0; i < partCount; i++)
        {
            var display = i < partBindings.Length ? partBindings[i].DisplayName : "";
            partNames[i] = string.IsNullOrWhiteSpace(display) ? _model.Parts[i].Id : display;
        }

        var lists = new List<int>[(int)PartGroup.Count];
        for (var i = 0; i < lists.Length; i++) lists[i] = new List<int>(64);

        for (var drawableIndex = 0; drawableIndex < _drawables.Length; drawableIndex++)
        {
            var parent = parentPart[drawableIndex];
            if (parent < 0 || parent >= partCount) continue;
            var name = partNames[parent];
            for (var groupIndex = 0; groupIndex < lists.Length; groupIndex++)
            {
                if (MatchGroup((PartGroup)groupIndex, name)) lists[groupIndex].Add(drawableIndex);
            }
        }

        var result = new int[lists.Length][];
        for (var i = 0; i < lists.Length; i++) result[i] = lists[i].ToArray();
        _groupIndices = result;
    }

    /// <summary>读 CubismCore 的 drawable → parent part 下标表（moc 静态数据，加载时读一次）。</summary>
    private int[] ReadParentPartIndices()
    {
        var result = new int[_drawables.Length];
        for (var i = 0; i < result.Length; i++) result[i] = -1;
        try
        {
            var table = CubismCore.csmGetDrawableParentPartIndices(_model.Handle);
            if (table == IntPtr.Zero) return result;
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = Marshal.ReadInt32(table, i * sizeof(int));
            }
        }
        catch (Exception)
        {
            // 老版本 Core 没有这个函数时退化为「无热区」，不影响模型其它功能
        }

        return result;
    }

    private static bool MatchGroup(PartGroup group, string name)
    {
        switch (group)
        {
            case PartGroup.Head:
                foreach (var keyword in HeadKeywords)
                {
                    if (name.Contains(keyword, StringComparison.Ordinal)) return true;
                }

                return false;
            case PartGroup.Face:
                return name is "脸" or "脸底";
            case PartGroup.Hand:
                return name == "手" || name.StartsWith("手指", StringComparison.Ordinal);
            case PartGroup.Chest:
                foreach (var keyword in ChestKeywords)
                {
                    if (name.Contains(keyword, StringComparison.Ordinal)) return true;
                }

                return false;
            case PartGroup.Waist:
                return name.Contains("腰部衣服", StringComparison.Ordinal);
            case PartGroup.Skirt:
                foreach (var keyword in SkirtKeywords)
                {
                    if (name.Contains(keyword, StringComparison.Ordinal)) return true;
                }

                return false;
            case PartGroup.Legs:
                return name is "腿" or "左腿" or "右腿";
            case PartGroup.Tail:
                foreach (var keyword in TailKeywords)
                {
                    if (name.Contains(keyword, StringComparison.Ordinal)) return true;
                }

                return false;
            default:
                return false;
        }
    }

    private void EnsureGeometry()
    {
        if (!_geometryDirty && _geometryReady) return;
        _geometryDirty = false;
        _geometryReady = false;
        for (var i = 0; i < _regionCenterValid.Length; i++) _regionCenterValid[i] = false;

        ComputeGroupBounds();
        var head = _groupBounds[(int)PartGroup.Head];
        var face = _groupBounds[(int)PartGroup.Face];
        var hand = _groupBounds[(int)PartGroup.Hand];
        var chest = _groupBounds[(int)PartGroup.Chest];
        var waist = _groupBounds[(int)PartGroup.Waist];
        var skirt = _groupBounds[(int)PartGroup.Skirt];
        var legs = _groupBounds[(int)PartGroup.Legs];
        var tail = _groupBounds[(int)PartGroup.Tail];

        if (!head.IsValid || !face.IsValid || !legs.IsValid) return;

        // 身体总高度：头 / 胸 / 腰 / 裙 / 腿里最高的那个（不含尾巴），用于切「头段」与「脚段」
        var bodyTop = head.MinY;
        if (chest.IsValid) bodyTop = Math.Min(bodyTop, chest.MinY);
        if (waist.IsValid) bodyTop = Math.Min(bodyTop, waist.MinY);
        if (skirt.IsValid) bodyTop = Math.Min(bodyTop, skirt.MinY);
        bodyTop = Math.Min(bodyTop, legs.MinY);

        var neckY = head.MaxY;
        var chestBottom = chest.IsValid ? Math.Max(chest.MaxY, neckY + 1f) : neckY + 1f;
        var waistBottom = skirt.IsValid ? skirt.MinY + ((skirt.MaxY - skirt.MinY) * 0.45f) : chestBottom + 1f;
        var skirtBottom = skirt.IsValid ? skirt.MaxY : waistBottom + 1f;
        var feetTop = legs.MaxY - ((legs.MaxY - bodyTop) * 0.105f);
        feetTop = Math.Clamp(feetTop, skirtBottom + 1f, legs.MaxY - 1f);

        var headWidth = head.MaxX - head.MinX;
        _headRect = new Bounds { MinX = head.MinX, MinY = bodyTop, MaxX = head.MaxX, MaxY = neckY };

        // 额头：脸包围盒上 3%~44%（到眼睛线为止，不抢眼睛）
        var faceWidth = face.MaxX - face.MinX;
        var faceHeight = face.MaxY - face.MinY;
        _foreheadRect = new Bounds
        {
            MinX = face.MinX + (faceWidth * 0.1f),
            MaxX = face.MaxX - (faceWidth * 0.1f),
            MinY = face.MinY + (faceHeight * 0.03f),
            MaxY = face.MinY + (faceHeight * 0.44f),
        };

        // 左右脸：眼睛以下的下半椭圆，按中线一分为二
        var faceTop = face.MinY + (faceHeight * 0.46f);
        _faceCx = face.CenterX;
        _faceCy = (faceTop + face.MaxY) * 0.5f;
        _faceRx = faceWidth * 0.45f;
        _faceRy = (face.MaxY - faceTop) * 0.51f;
        _hasFaceEllipse = _faceRx > 1f && _faceRy > 1f;

        _chestRect = MakeBand(chest, head, neckY, chestBottom);
        _waistRect = MakeBand(waist, chest, chestBottom, waistBottom);
        _skirtRect = MakeBand(skirt, waist, waistBottom, skirtBottom);
        _legsRect = MakeBand(legs, skirt, skirtBottom, feetTop);
        _feetRect = MakeBand(legs, skirt, feetTop, legs.MaxY);
        _tailRect = tail.IsValid ? tail : default;
        if (!tail.IsValid) _tailRect = new Bounds { MinX = 0, MinY = 0, MaxX = 0, MaxY = 0 };

        BuildHandBounds(hand);
        BuildCenters();

        _geometryReady = true;
    }

    private static Bounds MakeBand(Bounds body, Bounds fallbackX, float top, float bottom)
    {
        var source = body.IsValid ? body : fallbackX;
        if (!source.IsValid) return default;
        return new Bounds { MinX = source.MinX, MinY = top, MaxX = source.MaxX, MaxY = bottom };
    }

    private void BuildHandBounds(Bounds hand)
    {
        _handLeftRect = default;
        _handRightRect = default;
        if (!hand.IsValid) return;

        var indices = _groupIndices[(int)PartGroup.Hand];
        var midX = hand.CenterX;
        var left = new Bounds { MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };
        var right = left;
        foreach (var drawableIndex in indices)
        {
            var drawable = _drawables[drawableIndex];
            if (drawable.Opacity <= 0.05f) continue;
            if (!drawable.TryGetCanvasBounds(out var minX, out var minY, out var maxX, out var maxY)) continue;
            var centerX = (minX + maxX) * 0.5f;
            if (centerX <= midX)
            {
                left.MinX = Math.Min(left.MinX, minX);
                left.MinY = Math.Min(left.MinY, minY);
                left.MaxX = Math.Max(left.MaxX, maxX);
                left.MaxY = Math.Max(left.MaxY, maxY);
            }
            else
            {
                right.MinX = Math.Min(right.MinX, minX);
                right.MinY = Math.Min(right.MinY, minY);
                right.MaxX = Math.Max(right.MaxX, maxX);
                right.MaxY = Math.Max(right.MaxY, maxY);
            }
        }

        if (!left.IsValid && right.IsValid)
        {
            // 只剩一只手（抬手动作）：按中线切成两半兜底
            left = new Bounds { MinX = hand.MinX, MinY = right.MinY, MaxX = midX, MaxY = right.MaxY };
        }
        else if (!right.IsValid && left.IsValid)
        {
            right = new Bounds { MinX = midX, MinY = left.MinY, MaxX = hand.MaxX, MaxY = left.MaxY };
        }
        else if (!left.IsValid && !right.IsValid)
        {
            left = new Bounds { MinX = hand.MinX, MinY = hand.MinY, MaxX = midX, MaxY = hand.MaxY };
            right = new Bounds { MinX = midX, MinY = hand.MinY, MaxX = hand.MaxX, MaxY = hand.MaxY };
        }

        _handLeftRect = left;
        _handRightRect = right;
    }

    private void BuildCenters()
    {
        SetCenter(InteractionRegion.Forehead, _foreheadRect.CenterX, _foreheadRect.CenterY);
        if (_hasFaceEllipse)
        {
            SetCenter(InteractionRegion.FaceLeft, _faceCx - (_faceRx * 0.45f), _faceCy + (_faceRy * 0.15f));
            SetCenter(InteractionRegion.FaceRight, _faceCx + (_faceRx * 0.45f), _faceCy + (_faceRy * 0.15f));
        }

        SetCenter(InteractionRegion.HandLeft, _handLeftRect.CenterX, _handLeftRect.CenterY);
        SetCenter(InteractionRegion.HandRight, _handRightRect.CenterX, _handRightRect.CenterY);

        // 头：靠头顶 / 侧面取点，避开额头 / 脸椭圆，保证「头中心」命中的是头
        var headX = _headRect.MinX + ((_headRect.MaxX - _headRect.MinX) * 0.5f);
        var headY = _headRect.MinY + ((_headRect.MaxY - _headRect.MinY) * 0.06f);
        SetCenter(InteractionRegion.Head, headX, headY);

        SetCenter(InteractionRegion.Chest, _chestRect.CenterX, _chestRect.CenterY);
        SetCenter(InteractionRegion.Waist, _waistRect.CenterX, _waistRect.CenterY);
        SetCenter(InteractionRegion.Skirt, _skirtRect.CenterX, _skirtRect.CenterY);
        SetCenter(InteractionRegion.Legs, _legsRect.CenterX, _legsRect.CenterY);
        SetCenter(InteractionRegion.Feet, _feetRect.CenterX, _feetRect.CenterY);

        if (_tailRect.IsValid)
        {
            // 尾巴的一部分在身体后面，取最外侧（右缘）保证不被身体各段抢走
            SetCenter(InteractionRegion.Tail, _tailRect.MaxX - ((_tailRect.MaxX - _tailRect.MinX) * 0.02f), _tailRect.CenterY);
        }
    }

    private void SetCenter(InteractionRegion region, float x, float y)
    {
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) return;
        var index = (int)region;
        _regionCenterX[index] = Math.Clamp(x, 0f, _canvasWidth);
        _regionCenterY[index] = Math.Clamp(y, 0f, _canvasHeight);
        _regionCenterValid[index] = true;
    }

    private void ComputeGroupBounds()
    {
        for (var groupIndex = 0; groupIndex < _groupBounds.Length; groupIndex++)
        {
            var bounds = new Bounds { MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };
            foreach (var drawableIndex in _groupIndices[groupIndex])
            {
                var drawable = _drawables[drawableIndex];
                if (drawable.Opacity <= 0.05f) continue;
                if (!drawable.TryGetCanvasBounds(out var minX, out var minY, out var maxX, out var maxY)) continue;
                if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY)) continue;
                bounds.MinX = Math.Min(bounds.MinX, minX);
                bounds.MinY = Math.Min(bounds.MinY, minY);
                bounds.MaxX = Math.Max(bounds.MaxX, maxX);
                bounds.MaxY = Math.Max(bounds.MaxY, maxY);
            }

            _groupBounds[groupIndex] = bounds;
        }
    }

    private void CacheParameters()
    {
        var ids = new[]
        {
            "ParamAngleX", "ParamAngleY", "ParamAngleZ", "AngleX", "AngleY", "AngleZ",
            "PositionX", "PositionY", "PositionZ", "LeanInput", "HipAngleZ",
            "ParamEyeBallX", "ParamEyeBallY", "ParamEyeLOpen", "ParamEyeROpen",
            "EyeSmile", "LEyeSquint", "REyeSquint",
            "ParamMouthOpenY", "ParamMouthForm", "JawOpen", "Mouthpucker",
            "MouthSmile_L", "MouthSmile_R",
            "CheeckPuff", "CheeckPuff2", "CheeckPuffL", "CheeckPuffR",
            "CheeksWiggleX", "CheeksWiggleXR", "CheeksWiggleXL",
            "ShoulderX", "ShoulderY", "ShoulderZ", "LArm", "RArm",
            "LEar1", "LEar2", "LEar3", "REar1", "REar2", "REar3",
        };
        foreach (var id in ids)
        {
            _parameters[id] = _model.FindParameter(id);
        }
    }
}
