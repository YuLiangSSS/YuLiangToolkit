namespace YuLiangToolkit.Tools.Live2D.Play;

/// <summary>
/// 「观察用户 / 逗你」类互动预设：台词 + 表情组合 + 程序脸（单眼瞪大 / 微眯 / 白眼 / 不满）+ 歪头 + 盯人。
/// <para>和 <see cref="InteractionScenario"/> 分开：这类动作会额外写眼睛 / 头角度 / 眼球参数，
/// 由 <c>ModelRuntime.StartObservation</c> 执行；低好感只给嫌弃 / 黑脸，高好感才凑近 + 脸红 + 爱心眼。</para>
/// </summary>
public sealed record ObservationScenario(
    string Id,
    string Kind,
    int MinAffection,
    string[] Expressions,
    string[] FacePoses,
    bool LeanForward,
    bool RandomBlackFace,
    double TiltDegrees,
    double BodyTwistDegrees,
    bool LookAtUser,
    double Seconds,
    string[] Lines);

/// <summary>观察类场景目录（数据驱动；加新观察动作只加一条）。</summary>
public static class ObservationScenarios
{
    public const string KindObserve = "observe";
    public const string KindTease = "tease";

    /// <summary>程序脸预设名（<c>ModelRuntime.ObservationFaceTargets</c> 认识这些名字）。</summary>
    public static IReadOnlyList<string> FacePresetNames { get; } = new[]
    {
        "单眼瞪大", "双眼微眯", "微眯偏睁", "单眼闭眨", "白眼", "不满", "好奇",
    };

    /// <summary>所有观察场景（同一 Kind 按 MinAffection 从高到低匹配）。</summary>
    public static IReadOnlyList<ObservationScenario> All { get; } = new[]
    {
        // ===================== 观察用户（仔细看看 / 神奇的生物） =====================
        new ObservationScenario(
            "observe.low", KindObserve, 0,
            new[] { "黑脸" }, new[] { "不满" }, LeanForward: true, RandomBlackFace: false,
            TiltDegrees: 5, BodyTwistDegrees: 2, LookAtUser: true, Seconds: 3.2,
            Lines: new[]
            {
                "这是什么脏东西黏我鞋底了…",
                "啧…别碰我。",
                "你…离我远一点。",
                "（皱眉）好奇怪的味道…",
                "{user}？不认识。",
            }),
        new ObservationScenario(
            "observe.mid", KindObserve, 30,
            Array.Empty<string>(), new[] { "单眼瞪大", "双眼微眯", "单眼闭眨" }, LeanForward: true, RandomBlackFace: false,
            TiltDegrees: 9, BodyTwistDegrees: 2.5, LookAtUser: true, Seconds: 3.6,
            Lines: new[]
            {
                "{user}真是个神奇的生物呢，让我仔细看看…",
                "哦？让我观察观察…",
                "{user}…你脸上好像沾了什么东西。",
                "唔…让我好好看看你。",
                "嗯…看起来是个有趣的生物呢。",
                "别动，我先瞧瞧…",
            }),
        new ObservationScenario(
            "observe.high", KindObserve, 85,
            new[] { "脸红", "爱心眼" }, new[] { "微眯偏睁", "单眼闭眨" }, LeanForward: true, RandomBlackFace: false,
            TiltDegrees: 11, BodyTwistDegrees: 3, LookAtUser: true, Seconds: 4.0,
            Lines: new[]
            {
                "{user}真是可爱呢，让我好好看看…",
                "越看越觉得…{user}好可爱。",
                "唔…这样盯着你看，会害羞吗？",
                "（盯着看）…真的，好可爱。",
                "让我再仔细看看…不许动哦。",
                "嗯…这张脸，怎么看都看不腻。",
            }),

        // ===================== 逗你 / 喜欢我这样看着你吗 =====================
        new ObservationScenario(
            "tease.low", KindTease, 0,
            new[] { "黑脸" }, new[] { "白眼" }, LeanForward: false, RandomBlackFace: false,
            TiltDegrees: 6, BodyTwistDegrees: 2, LookAtUser: true, Seconds: 3.0,
            Lines: new[]
            {
                "喜欢我这样看着你吗？…别自作多情。",
                "想让我这样看着你吗？…哼。",
                "看什么看…烦死了。",
                "（白眼）…离远点。",
                "丑死了，别看了。",
            }),
        new ObservationScenario(
            "tease.mid", KindTease, 30,
            Array.Empty<string>(), new[] { "白眼", "微眯偏睁" }, LeanForward: false, RandomBlackFace: true,
            TiltDegrees: 7, BodyTwistDegrees: 2.2, LookAtUser: true, Seconds: 3.2,
            Lines: new[]
            {
                "喜欢我这样看着你吗？",
                "想让我这样看着你吗？…想得美。",
                "哼哼…被我盯上了喔。",
                "再看…就把你记住喔。",
                "（眯眼）…你在紧张吗？",
                "被这样看着，会不好意思吗？",
            }),
        new ObservationScenario(
            "tease.high", KindTease, 85,
            new[] { "脸红", "爱心眼" }, new[] { "微眯偏睁" }, LeanForward: true, RandomBlackFace: false,
            TiltDegrees: 10, BodyTwistDegrees: 3, LookAtUser: true, Seconds: 4.0,
            Lines: new[]
            {
                "喜欢我这样看着你吗？…喜欢的话，就多看一会儿。",
                "想让我这样看着你吗？…那就别移开视线。",
                "唔…被{user}看着，心跳好快…",
                "（凑近）…看清楚，我是你的。",
                "这样盯着你…可以吗？",
                "再看下去…我可要当真了喔。",
            }),
    };

    /// <summary>按 Kind + 好感度挑门槛最高的一条；没到门槛返回 null。</summary>
    public static ObservationScenario? TryMatch(string? kind, double affection)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var key = kind.Trim();
        ObservationScenario? best = null;
        foreach (var scenario in All)
        {
            if (!string.Equals(scenario.Kind, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (affection + 0.001 < scenario.MinAffection) continue;
            if (best is null || scenario.MinAffection > best.MinAffection) best = scenario;
        }
        return best;
    }

    /// <summary>挑一句台词（尽量不重复上一句）。</summary>
    public static string PickLine(ObservationScenario scenario, string? lastLine, Random random)
    {
        var lines = scenario.Lines;
        if (lines.Length == 0) return "";
        if (lines.Length == 1) return lines[0];
        for (var i = 0; i < 6; i++)
        {
            var line = lines[random.Next(lines.Length)];
            if (!string.Equals(line, lastLine, StringComparison.Ordinal)) return line;
        }
        return lines[0];
    }
}

/// <summary>
/// 「眼睛盯着用户」的方向算法：用户一般正对屏幕中央，所以按「桌宠在屏幕上的位置 → 屏幕中央」算眼球偏移。
/// 桌宠在屏幕右边 → 眼球往左看（X 为负）；在左下 → 往左上（X 正 / Y 正，Live2D 里 +Y 朝上）。
/// </summary>
public static class UserGaze
{
    /// <summary>
    /// 返回 -1~1 的眼球方向（X 正 = 右，Y 正 = 上；0 = 正前方）。
    /// 桌宠脸锚点取窗口高度的 32%（和模型头/眼位置近似），目标点取工作区的中央偏上。
    /// </summary>
    public static (float X, float Y) Compute(
        double petLeft, double petTop, double petWidth, double petHeight,
        double workLeft, double workTop, double workWidth, double workHeight)
    {
        if (workWidth <= 1 || workHeight <= 1 || petWidth <= 1 || petHeight <= 1) return (0f, 0f);

        var faceX = petLeft + (petWidth * 0.5);
        var faceY = petTop + (petHeight * 0.32);
        var targetX = workLeft + (workWidth * 0.5);
        var targetY = workTop + (workHeight * 0.45);

        var x = (targetX - faceX) / Math.Max(1.0, workWidth * 0.5);
        // Live2D 的 ParamEyeBallY 正方向朝上，屏幕坐标 Y 朝下 → 要取反
        var y = (faceY - targetY) / Math.Max(1.0, workHeight * 0.5);
        return ((float)Math.Clamp(x, -1.0, 1.0), (float)Math.Clamp(y, -1.0, 1.0));
    }
}
