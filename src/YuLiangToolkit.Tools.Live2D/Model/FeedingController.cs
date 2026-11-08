using System;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>吃饭动作的阶段（平常 / 嚼嚼嚼 / 伪吞咽）。</summary>
public enum FeedingPhase
{
    /// <summary>平常（没在吃）。</summary>
    Idle = 0,

    /// <summary>嚼嚼嚼（1.4 秒左右，嘴一开一合）。</summary>
    Chewing = 1,

    /// <summary>伪吞咽（0.6 秒左右：闭嘴 + 低头 + 喉咙一动，过渡回平常）。</summary>
    Swallowing = 2,
}

/// <summary>
/// 投喂表情控制（纯逻辑，不碰 Cubism）：饭团靠近时「身子凑过来 + 张嘴」，吃到后「嚼嚼嚼 → 伪吞咽」。
///
/// <para><b>输出给渲染层的量</b>（由 <see cref="ModelRuntime"/> 每帧写进模型参数）：</para>
/// <list type="bullet">
///   <item><see cref="FocusX"/> / <see cref="FocusY"/>：饭团相对桌宠中心的方向（-1~1），身体 / 头 / 眼球朝它偏；</item>
///   <item><see cref="MouthOpen"/>：张嘴程度（靠近越近张越大；咀嚼时一开一合；吞咽时闭合）；</item>
///   <item><see cref="HeadNodDegrees"/>：咀嚼时轻微点头、吞咽时低下再抬起；</item>
///   <item><see cref="BodyLeanDegrees"/>：朝饭团那一侧轻微前倾。</item>
/// </list>
///
/// <para><b>优先级</b>：动捕有脸时由 <see cref="ModelRuntime"/> 整段跳过（真实人脸优先），
/// 说话时只跳过嘴部（声音口型优先），头和身体照常。</para>
/// </summary>
public sealed class FeedingController
{
    /// <summary>默认咀嚼时长（秒）。</summary>
    public const double DefaultChewSeconds = 1.4;

    /// <summary>默认伪吞咽时长（秒）。</summary>
    public const double DefaultSwallowSeconds = 0.65;

    /// <summary>咀嚼频率（每秒几口）。</summary>
    public const double ChewFrequencyHz = 1.7;

    // 目标输入（由 SetFocus / StartEating 设置）
    private float _targetFocusX;
    private float _targetFocusY;
    private float _targetStrength;
    private float _targetMouthOpen;

    // 平滑后的输出
    private float _focusX;
    private float _focusY;
    private float _strength;
    private float _mouthOpen;

    /// <summary>当前阶段。</summary>
    public FeedingPhase Phase { get; private set; }

    /// <summary>当前阶段已经过的时间（秒）。</summary>
    public double PhaseElapsed { get; private set; }

    /// <summary>咀嚼时长（秒）。</summary>
    public double ChewSeconds { get; private set; } = DefaultChewSeconds;

    /// <summary>伪吞咽时长（秒）。</summary>
    public double SwallowSeconds { get; private set; } = DefaultSwallowSeconds;

    /// <summary>是否正在吃（咀嚼 / 吞咽中）。</summary>
    public bool IsEating => Phase != FeedingPhase.Idle;

    /// <summary>是否处于「饭团靠近、凑过去张嘴」状态。</summary>
    public bool IsFocusActive { get; private set; }

    /// <summary>饭团方向 X（-1 = 正左，1 = 正右，平滑后）。</summary>
    public float FocusX => _focusX;

    /// <summary>饭团方向 Y（-1 = 上，1 = 下，平滑后）。</summary>
    public float FocusY => _focusY;

    /// <summary>凑过去的强度（0~1，越近越大，平滑后）。</summary>
    public float Strength => _strength;

    /// <summary>张嘴程度（0~1，平滑后的最终输出）。</summary>
    public float MouthOpen => _mouthOpen;

    /// <summary>头部俯仰补偿（度；咀嚼 / 吞咽时非 0）。</summary>
    public float HeadNodDegrees { get; private set; }

    /// <summary>身体朝饭团那一侧的轻倾（度）。</summary>
    public float BodyLeanDegrees { get; private set; }

    /// <summary>
    /// 这一帧有没有要写的输出（凑近 / 吃 / 回落过程中为 true）。
    /// 没有输出时 <see cref="ModelRuntime"/> 不去碰这些参数，交回待机 / 动作 / 手动值。
    /// </summary>
    public bool HasOutput { get; private set; }

    /// <summary>饭团靠近：设置方向 / 强度 / 张嘴目标（由会话每帧从投喂层推入）。</summary>
    public void SetFocus(float normalizedX, float normalizedY, float strength, float mouthOpen)
    {
        _targetFocusX = Math.Clamp(normalizedX, -1f, 1f);
        _targetFocusY = Math.Clamp(normalizedY, -1f, 1f);
        _targetStrength = Math.Clamp(strength, 0f, 1f);
        _targetMouthOpen = Math.Clamp(mouthOpen, 0f, 1f);
        IsFocusActive = true;
    }

    /// <summary>饭团被扔远 / 收起：目标归零（会平滑回落，不会突然僵住）。</summary>
    public void ClearFocus()
    {
        IsFocusActive = false;
        _targetFocusX = 0f;
        _targetFocusY = 0f;
        _targetStrength = 0f;
        _targetMouthOpen = 0f;
    }

    /// <summary>
    /// 吃到饭团：切到「嚼嚼嚼」阶段（<paramref name="chewSeconds"/> 秒）→「伪吞咽」→ 回平常。
    /// 吃饭期间会清掉凑近状态（嘴里的东西不会再让身体追着跑）。
    /// </summary>
    public void StartEating(double chewSeconds = DefaultChewSeconds, double swallowSeconds = DefaultSwallowSeconds)
    {
        ClearFocus();
        ChewSeconds = Math.Clamp(chewSeconds, 0.3, 6);
        SwallowSeconds = Math.Clamp(swallowSeconds, 0.2, 3);
        Phase = FeedingPhase.Chewing;
        PhaseElapsed = 0;
        HasOutput = true;
    }

    /// <summary>立刻停止（关投喂 / 重置参数时用）。</summary>
    public void Reset()
    {
        ClearFocus();
        Phase = FeedingPhase.Idle;
        PhaseElapsed = 0;
        _focusX = _focusY = _strength = _mouthOpen = 0f;
        HeadNodDegrees = 0f;
        BodyLeanDegrees = 0f;
        HasOutput = false;
    }

    /// <summary>每帧推进：相位计时、平滑凑近、计算嘴 / 头 / 身体输出。</summary>
    public void Update(double deltaSeconds)
    {
        var dt = Math.Clamp(deltaSeconds, 0, 0.25);

        // 1) 相位推进（咀嚼 → 吞咽 → 平常）
        if (Phase == FeedingPhase.Chewing)
        {
            PhaseElapsed += dt;
            if (PhaseElapsed >= ChewSeconds)
            {
                Phase = FeedingPhase.Swallowing;
                PhaseElapsed = 0;
            }
        }
        else if (Phase == FeedingPhase.Swallowing)
        {
            PhaseElapsed += dt;
            if (PhaseElapsed >= SwallowSeconds)
            {
                Phase = FeedingPhase.Idle;
                PhaseElapsed = 0;
            }
        }

        // 2) 凑近量平滑（指数逼近，约 0.2 秒到位）
        var k = (float)Math.Clamp(dt * 7.0, 0, 1);
        _focusX += (_targetFocusX - _focusX) * k;
        _focusY += (_targetFocusY - _focusY) * k;
        _strength += (_targetStrength - _strength) * k;

        // 3) 张嘴：吃饭 >> 凑近张嘴，平滑过去
        float mouthTarget;
        float nodTarget;
        if (Phase == FeedingPhase.Chewing)
        {
            var chewCycle = Math.Sin(PhaseElapsed * Math.PI * 2.0 * ChewFrequencyHz);
            mouthTarget = 0.28f + (float)(Math.Abs(chewCycle) * 0.42);
            nodTarget = (float)(chewCycle * 2.2);
        }
        else if (Phase == FeedingPhase.Swallowing)
        {
            var t = SwallowSeconds <= 0 ? 1 : Math.Clamp(PhaseElapsed / SwallowSeconds, 0, 1);
            mouthTarget = 0.06f + (float)((1 - t) * 0.12);
            // 低头 → 抬起（像把嘴里的东西咽下去），sin(πt) 在两端为 0
            nodTarget = (float)(-Math.Sin(t * Math.PI) * 7.0);
        }
        else
        {
            mouthTarget = _targetMouthOpen;
            nodTarget = 0;
        }

        var mouthK = (float)Math.Clamp(dt * 10.0, 0, 1);
        _mouthOpen += (mouthTarget - _mouthOpen) * mouthK;

        // 4) 头 / 身体 / 输出判定
        HeadNodDegrees = nodTarget;
        BodyLeanDegrees = Phase == FeedingPhase.Idle || Phase == FeedingPhase.Chewing ? _focusX * _strength * 4.5f : 0f;

        var idleAndCalm = Phase == FeedingPhase.Idle && !IsFocusActive &&
                          Math.Abs(_focusX) < 0.01f && Math.Abs(_focusY) < 0.01f &&
                          _strength < 0.01f && _mouthOpen < 0.01f;
        HasOutput = !idleAndCalm;
        if (idleAndCalm && Math.Abs(HeadNodDegrees) < 0.01f)
        {
            // 彻底回到平常：把残值清干净，下一帧起不再写参数
            _focusX = _focusY = _strength = _mouthOpen = 0f;
            HasOutput = false;
        }
    }

    /// <summary>按阶段给一句话（会话也能自己挑；这里给对话框 / 调试用）。</summary>
    public static string PhaseText(FeedingPhase phase) => phase switch
    {
        FeedingPhase.Chewing => "嚼嚼嚼…",
        FeedingPhase.Swallowing => "咕嘟…咽下去了",
        _ => "平常",
    };
}
