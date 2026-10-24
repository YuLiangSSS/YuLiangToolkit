using YuLiangToolkit.Tools.OsuMania.Core;
using YuLiangToolkit.Tools.OsuMania.Core.Difficulty;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// osu!mania 星数自检：把「旧实现参考值」固化成断言，防止以后有人“顺手优化”数学 / 常量 / 循环顺序导致星数漂移。
///
/// <para><b>参考值来源</b>：旧项目 <c>HeavenUsurperToolkit-master/StarRating.cs</c>（875 行）+ <c>Utils.cs</c>（125 行）
/// 对 10 组确定性合成谱面的输出，详见 <c>docs/porting/star-rating-reference.md</c>；
/// 新旧对齐报告见 <c>docs/porting/star-rating-parity-report.md</c>（当时 10/10 差 0）。</para>
///
/// <para><b>合成谱面生成规则（确定性，不依赖外部文件）</b>：每组由固定参数 + <c>new Random(1000 + id * 37)</c> 生成：
/// 从 t=1000ms 起，每次 <c>t += rng.Next(MinGap, MaxGap+1)</c>；
/// 以 <c>ChordPermille‰</c> 概率在同一时间点放 2~3 个不同列（最多 keys 个），
/// 每个对象以 <c>LnPermille‰</c> 概率成为长条（长度 = 100 + rng.Next(0,19)*50 ms，即 100~1000ms），
/// 否则为普通键（EndTime = StartTime）；最后按 (StartTime, Column) 稳定排序。
/// 该规则与 <c>star-rating-reference.md</c> §2 完全一致（参考值就是用它算出来的）。
/// <b>注意</b>：这里用 <see cref="System.Random"/>（固定种子），在 .NET 9 运行时上确定；
/// 若将来整体升级到新的 .NET 大版本且 Random 实现变化，参考值需要按新运行时重算并更新
/// <c>docs/porting/star-rating-reference.md</c>（本自检会在数值对不上时直接报 FAIL，不会静默放过）。</para>
///
/// <para><b>接线方式</b>（Program.cs 里加这一段即可，本文件内部已给每条断言加 <c>[星数] </c> 前缀）：</para>
/// <code>
/// // ---------- osu!mania 星数（参考值 / 单调性 / 等级公式） ----------
/// try
/// {
///     StarRatingChecks.Run(
///         (name, ok, detail) => Check(name, ok, detail),
///         (name, ok) => Check(name, ok));
/// }
/// catch (Exception ex)
/// {
///     Check($"[星数] 运行过程中抛出异常（{ex.GetType().Name}）", false,
///         $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
/// }
/// </code>
/// </summary>
public static class StarRatingChecks
{
    /// <summary>数值比较容差：参考值保留 10 位小数，对齐要求 ≤ 1e-9（旧实现与新实现实测差为 0）。</summary>
    private const double Tolerance = 1e-9;

    /// <summary>跑完整组断言（签名与其它 Checks 一致：带 detail 的 check + 不带 detail 的 plain）。</summary>
    public static void Run(Action<string, bool, string> checkWithDetail, Action<string, bool> plainCheck)
    {
        // 所有条目的名字统一带 [星数] 前缀（Program.cs 接线时不用再加，避免重复）
        void check(string name, bool ok, string detail = "") => checkWithDetail("[星数] " + name, ok, detail);
        void plain(string name, bool ok) => plainCheck("[星数] " + name, ok);

        // ============================ 1. 10 组参考值（旧实现基准，10 位小数） ============================
        var cases = BuildCases();
        var generated = new Dictionary<int, List<NoteSpec>>();

        int refIndex = 0;
        foreach (var c in cases)
        {
            var notes = generated.TryGetValue(c.Id, out var made)
                ? made
                : c.ReuseFromId > 0 ? generated[c.ReuseFromId] : Generate(c);
            generated[c.Id] = notes;

            double sr = StarRatingCalculator.CalculateStarRating(ToManiaNotes(notes), c.Od, c.Keys, c.Rate);

            string detail = $"实际={sr:F10} 参考={c.Expected:F10} 差={Math.Abs(sr - c.Expected):E2}";
            check($"参考值 #{c.Id:D2}（{c.Desc}）", Math.Abs(sr - c.Expected) <= Tolerance, detail);
            if (refIndex++ == 0)
                plain("参考值 #01 是有限值（不是 NaN/∞）", double.IsFinite(sr));
        }

        // 参考值 #02/#03/#05/#07/#08 的“含 LN / 高密度 / 7K / 10K”已由上面逐组覆盖；
        // 这里再补一条 10 键可算的显式断言（防止将来有人把键数上限改坏）。
        var tenKey = generated[8];
        double tenKeySr = StarRatingCalculator.CalculateStarRating(ToManiaNotes(tenKey), 8.0, 10, 1.0);
        check("10 键谱面可算且 SR 有限且 > 0", double.IsFinite(tenKeySr) && tenKeySr > 0, $"SR={tenKeySr:F10}");

        // ============================ 2. 边界 / 退化输入 ============================
        double emptySr = double.NaN;
        bool emptyThrew = false;
        try
        {
            emptySr = StarRatingCalculator.CalculateStarRating(new List<ManiaNote>(), 8.0, 4, 1.0);
        }
        catch
        {
            emptyThrew = true;
        }
        check("空谱面返回 0 且不抛异常（旧实现在 OsuFileV14 构造器里同样兜底为 0）",
            !emptyThrew && emptySr == 0, emptyThrew ? "抛异常了" : $"SR={emptySr}");

        double oneNoteSr = StarRatingCalculator.CalculateStarRating(
            new List<ManiaNote> { new(0, 1000) }, 8.0, 4, 1.0);
        check("单 note：可算、有限（退化输入返回 0，不抛；与旧实现一致）",
            double.IsFinite(oneNoteSr) && oneNoteSr >= 0, $"SR={oneNoteSr}");

        // 2K（旧版 KeyX 表没有 1K/2K，新实现 ColumnX 已修；星数算法本身支持 2 键）
        var twoKeyNotes = Generate(new CaseSpec(101, 2, 8.0, 1.0, 120, 100, 0, 80, 300, "2K 120 notes", 1.7929102511));
        double twoKeySr = StarRatingCalculator.CalculateStarRating(ToManiaNotes(twoKeyNotes), 8.0, 2, 1.0);
        check("2K 谱面参考值（参考表边界用例：1.7929102511）", Math.Abs(twoKeySr - 1.7929102511) <= Tolerance,
            $"实际={twoKeySr:F10}");

        // 零长度 LN（type=128 但 end == start）：算法只看 head/tail，等同普通键，不应崩
        var zeroLnNotes = Enumerable.Range(0, 50)
            .Select(i => new ManiaNote(i % 4, 1000 + i * 200, 1000 + i * 200, isLongNote: true))
            .ToList();
        double zeroLnSr = StarRatingCalculator.CalculateStarRating(zeroLnNotes, 8.0, 4, 1.0);
        check("零长度 LN（type=128 但 end==start）参考值 0.4858933604",
            Math.Abs(zeroLnSr - 0.4858933604) <= Tolerance, $"实际={zeroLnSr:F10}");

        // 确定性：同输入连算两次结果一致（算法内部不应留 static 可变状态）
        double again = StarRatingCalculator.CalculateStarRating(ToManiaNotes(generated[2]), 8.0, 4, 1.0);
        double first = StarRatingCalculator.CalculateStarRating(ToManiaNotes(generated[2]), 8.0, 4, 1.0);
        check("同输入两次计算结果完全相同（无隐藏状态）", again == first, $"{first:F10} / {again:F10}");

        // ============================ 3. 单调性 / 合理性 ============================
        var case02 = generated[2];
        double od0 = StarRatingCalculator.CalculateStarRating(ToManiaNotes(case02), 0.0, 4, 1.0);
        double od95 = StarRatingCalculator.CalculateStarRating(ToManiaNotes(case02), 9.5, 4, 1.0);
        check("同谱 OD 9.5 的 SR > OD 0 的 SR", od95 > od0, $"OD0={od0:F6} OD9.5={od95:F6}");

        double rate10 = StarRatingCalculator.CalculateStarRating(ToManiaNotes(case02), 8.0, 4, 1.0);
        double rate15 = StarRatingCalculator.CalculateStarRating(ToManiaNotes(case02), 8.0, 4, 1.5);
        check("同谱 rate 1.5 的 SR > rate 1.0 的 SR", rate15 > rate10, $"r1.0={rate10:F6} r1.5={rate15:F6}");

        var case07 = generated[7];
        double od0Seven = StarRatingCalculator.CalculateStarRating(ToManiaNotes(case07), 0.0, 7, 1.0);
        double od95Seven = StarRatingCalculator.CalculateStarRating(ToManiaNotes(case07), 9.5, 7, 1.0);
        check("7K 同谱 OD 9.5 的 SR > OD 0 的 SR", od95Seven > od0Seven, $"OD0={od0Seven:F6} OD9.5={od95Seven:F6}");

        // ============================ 4. LevelCalculator：SR → 等级公式 ============================
        check("LevelCalculator 下界常量与旧版一致（2.76257856739498）",
            LevelCalculator.LowerBound == 2.76257856739498, $"实际={LevelCalculator.LowerBound:R}");
        check("LevelCalculator 上界常量与旧版一致（10.5541834716376）",
            LevelCalculator.UpperBound == 10.5541834716376, $"实际={LevelCalculator.UpperBound:R}");

        double fitting5 = 6.9615 * Math.Exp(0.1374 * 5.0);
        check("区间内（SR=5）走拟合公式 6.9615*e^(0.1374*x)",
            Math.Abs(LevelCalculator.CalculateLevel(5.0) - fitting5) <= 1e-12,
            $"实际={LevelCalculator.CalculateLevel(5.0):F12} 手算={fitting5:F12}");

        double fittingLower = 6.9615 * Math.Exp(0.1374 * LevelCalculator.LowerBound);
        check("恰在下界处也走拟合公式",
            Math.Abs(LevelCalculator.CalculateLevel(LevelCalculator.LowerBound) - fittingLower) <= 1e-12,
            $"实际={LevelCalculator.CalculateLevel(LevelCalculator.LowerBound):F12}");

        double fittingUpper = 6.9615 * Math.Exp(0.1374 * LevelCalculator.UpperBound);
        check("恰在上界处也走拟合公式",
            Math.Abs(LevelCalculator.CalculateLevel(LevelCalculator.UpperBound) - fittingUpper) <= 1e-12,
            $"实际={LevelCalculator.CalculateLevel(LevelCalculator.UpperBound):F12}");

        check("下界以下（SR=1.0）走 3.6198*x",
            Math.Abs(LevelCalculator.CalculateLevel(1.0) - 3.6198) <= 1e-12,
            $"实际={LevelCalculator.CalculateLevel(1.0):F12} 期望=3.619800000000");

        check("下界略下方（SR=2.75）走 3.6198*x",
            Math.Abs(LevelCalculator.CalculateLevel(2.75) - 3.6198 * 2.75) <= 1e-12,
            $"实际={LevelCalculator.CalculateLevel(2.75):F12}");

        check("上界以上（SR=11.0）走 2.791*x+0.5436",
            Math.Abs(LevelCalculator.CalculateLevel(11.0) - (2.791 * 11.0 + 0.5436)) <= 1e-12,
            $"实际={LevelCalculator.CalculateLevel(11.0):F12}");

        check("上界略上方（SR=10.6）走 2.791*x+0.5436",
            Math.Abs(LevelCalculator.CalculateLevel(10.6) - (2.791 * 10.6 + 0.5436)) <= 1e-12,
            $"实际={LevelCalculator.CalculateLevel(10.6):F12}");

        check("SR=0 返回 NaN（旧版 CalculateLevel 的行为）", double.IsNaN(LevelCalculator.CalculateLevel(0)), "");
        check("负 SR 返回 NaN", double.IsNaN(LevelCalculator.CalculateLevel(-1.0)), "");
        check("SR >= 12.3456789 返回 NaN（上限外）",
            double.IsNaN(LevelCalculator.CalculateLevel(12.3456789)) && double.IsNaN(LevelCalculator.CalculateLevel(13.0)), "");
        check("NaN 输入仍返回 NaN", double.IsNaN(LevelCalculator.CalculateLevel(double.NaN)), "");

        var bmWithStar = new OsuManiaBeatmap { StarRating = 5.0 };
        check("OsuManiaBeatmap 重载 = 用 Beatmap.StarRating 走同一公式",
            LevelCalculator.CalculateLevel(bmWithStar) == LevelCalculator.CalculateLevel(5.0), "");

        check("FormatLevel(NaN) 显示 -", LevelCalculator.FormatLevel(double.NaN) == "-", LevelCalculator.FormatLevel(double.NaN));
        check("FormatLevel(4.9352511737) 显示 4.94", LevelCalculator.FormatLevel(4.9352511737) == "4.94",
            LevelCalculator.FormatLevel(4.9352511737));
        check("FormatLevel(3.0) 显示 3（去掉多余的 .00）", LevelCalculator.FormatLevel(3.0) == "3",
            LevelCalculator.FormatLevel(3.0));

        // ============================ 5. 参考值与等级公式联动（旧版 LevelCalculator 的实际用法） ============================
        // 旧版 LevelCalculator 的等级 = 按星数拟合公式算出来的；拿参考表里的 4.9352511737 验证一遍链路
        double levelOfCase02 = LevelCalculator.CalculateLevel(4.9352511737);
        double expectedLevelOfCase02 = 6.9615 * Math.Exp(0.1374 * 4.9352511737);
        check("参考值 #02 的星数 → 等级与拟合公式一致",
            Math.Abs(levelOfCase02 - expectedLevelOfCase02) <= 1e-12,
            $"level={levelOfCase02:F12}");
    }

    // =====================================================================================
    //  确定性合成谱面生成（与 docs/porting/star-rating-reference.md §2 的规则逐行一致）
    // =====================================================================================

    /// <summary>参考表（<c>docs/porting/star-rating-reference.md</c> §3）的 10 组参数与参考值。</summary>
    private static List<CaseSpec> BuildCases() => new()
    {
        new(1, 4, 8.0, 1.0, 200, 0, 0, 120, 380, "4K 稀疏纯 note", 1.0478683262),
        new(2, 4, 8.0, 1.0, 600, 150, 250, 70, 220, "4K 中等密度含 LN", 4.9352511737),
        new(3, 4, 8.0, 1.0, 1200, 300, 150, 45, 150, "4K 高密度和弦", 8.1031144462),
        new(4, 4, 0.0, 1.0, 200, 0, 0, 120, 380, "4K OD0 稀疏纯 note", 0.9600663396),
        new(5, 4, 9.5, 1.0, 600, 150, 250, 70, 220, "4K OD9.5 中等含 LN", 5.1732675209),
        new(6, 7, 0.0, 1.0, 150, 50, 100, 120, 380, "7K OD0 稀疏", 1.8638253317),
        new(7, 7, 8.0, 1.0, 700, 150, 300, 60, 200, "7K 中等含 LN", 6.2438279397),
        new(8, 10, 8.0, 1.0, 900, 200, 200, 55, 180, "10K 高密度", 5.7213766701),
        new(9, 4, 8.0, 1.5, 0, 0, 0, 0, 0, "同 #02 相同对象，仅 rate=1.5", 6.4424754489, ReuseFromId: 2),
        new(10, 7, 9.5, 1.5, 0, 0, 0, 0, 0, "同 #07 相同对象，仅 rate=1.5", 7.7591794804, ReuseFromId: 7),
    };

    /// <summary>生成一组确定性 mania 对象（算法与参考表来源完全一致，详见类注释）。</summary>
    private static List<NoteSpec> Generate(CaseSpec c)
    {
        var rng = new Random(1000 + c.Id * 37);
        var objs = new List<NoteSpec>(c.NoteCount + 64);
        int t = 1000;

        while (objs.Count < c.NoteCount)
        {
            t += rng.Next(c.MinGap, c.MaxGap + 1);

            // 同一时间点上放 1~3 个不同列（和弦）
            int chord = rng.Next(1000) < c.ChordPermille ? rng.Next(2, Math.Min(3, c.Keys) + 1) : 1;
            chord = Math.Min(chord, c.Keys);

            var used = new bool[c.Keys];
            for (int i = 0; i < chord && objs.Count < c.NoteCount; i++)
            {
                int column;
                do { column = rng.Next(c.Keys); } while (used[column]);
                used[column] = true;

                if (rng.Next(1000) < c.LnPermille)
                {
                    int length = 100 + rng.Next(0, 19) * 50;   // 100~1000ms，50ms 步进
                    objs.Add(new NoteSpec(t, t + length, column));
                }
                else
                {
                    objs.Add(new NoteSpec(t, t, column));      // 普通键：EndTime == StartTime
                }
            }
        }

        return objs.OrderBy(o => o.StartTime).ThenBy(o => o.Column).ToList();
    }

    /// <summary>把生成结果转成算法输入（普通键 EndTime == StartTime，LN EndTime &gt; StartTime；与旧版语义一致）。</summary>
    private static List<ManiaNote> ToManiaNotes(List<NoteSpec> notes)
        => notes.Select(n => new ManiaNote(n.Column, n.StartTime, n.EndTime)).ToList();

    /// <summary>与旧项目 <c>ManiaHitObject</c> 对齐的最小记录（算法只读这三个字段）。</summary>
    private readonly record struct NoteSpec(int StartTime, int EndTime, int Column);

    /// <summary>一组参考用例：生成参数 + 旧实现参考值。</summary>
    private sealed record CaseSpec(
        int Id, int Keys, double Od, double Rate, int NoteCount,
        int ChordPermille, int LnPermille, int MinGap, int MaxGap,
        string Desc, double Expected, int ReuseFromId = 0);
}
