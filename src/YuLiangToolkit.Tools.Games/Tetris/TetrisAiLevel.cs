namespace YuLiangToolkit.Tools.Games.Tetris;

/// <summary>
/// 俄罗斯方块电脑对手的**智慧档位（六档）**。
///
/// <para><b>为什么不复用 <c>Core.Games.AiDifficulty</c></b>：那四档是棋牌共用的一套（棋类靠「搜索深度」变强，
/// 牌类靠「会不会拆牌」），俄罗斯方块变强的办法是<b>往后多看几块</b>，档位和参数都不一样；
/// 而且别的游戏的自检写死了「四档」，硬加两档会连带改一堆不相干的地方。</para>
///
/// <para><b>档位 = 推算深度</b>：入门 / 普通 / 困难只看当前这一块，大师往后看 1 块（共 2 块），
/// 宗师 2 块（共 3 块），传说 3 块（共 4 块）。推算用的方块序列直接取「接下来几块」预览，
/// 预览只开了 1 格时深度会自动降到能算的程度（不瞎猜）。</para>
///
/// <para><b>速度不归这里管</b>：落子间隔是单独一项设置（<see cref="TetrisConfig.AiMoveIntervalMs"/>），
/// 免得「想让它慢一点」只能靠降难度。</para>
/// </summary>
public enum TetrisAiLevel
{
    /// <summary>入门：只看「别堆太高」，偶尔乱放。</summary>
    Beginner = 0,

    /// <summary>普通：会主动消行、保持平整（默认）。</summary>
    Normal = 1,

    /// <summary>困难：更怕空洞与起伏，会攒多行。</summary>
    Hard = 2,

    /// <summary>大师：往后看 1 块。</summary>
    Master = 3,

    /// <summary>宗师：往后看 2 块。</summary>
    Grandmaster = 4,

    /// <summary>传说：往后看 3 块，最难赢。</summary>
    Legend = 5,
}

/// <summary>
/// 档位的展示名、存储键与各项参数（纯逻辑，自检可直接断言）。
///
/// <para>评估沿用经典四项：<b>消行数、空洞数、最高堆叠高度、表面起伏</b>，越往上的档位
/// 越不在乎「暂时堆高一点」、越在乎「别留洞、别坑坑洼洼」。</para>
/// </summary>
public static class TetrisAiLevels
{
    /// <summary>一次落点评估的四项权重。</summary>
    /// <param name="Lines">每消一行的分数。</param>
    /// <param name="Holes">每个空洞的分数（负数）。</param>
    /// <param name="Height">每高一格的分数（负数）。</param>
    /// <param name="Bump">相邻两列高度差之和的分数（负数）。</param>
    /// <param name="Well">每深出一格的「井」的分数（负数）。</param>
    public readonly record struct Weights(double Lines, double Holes, double Height, double Bump, double Well);

    /// <summary>全部档位（界面下拉用，从弱到强）。</summary>
    public static IReadOnlyList<TetrisAiLevel> All { get; } = new[]
    {
        TetrisAiLevel.Beginner, TetrisAiLevel.Normal, TetrisAiLevel.Hard,
        TetrisAiLevel.Master, TetrisAiLevel.Grandmaster, TetrisAiLevel.Legend,
    };

    /// <summary>中文名。</summary>
    public static string DisplayName(TetrisAiLevel level) => level switch
    {
        TetrisAiLevel.Beginner => "入门",
        TetrisAiLevel.Hard => "困难",
        TetrisAiLevel.Master => "大师",
        TetrisAiLevel.Grandmaster => "宗师",
        TetrisAiLevel.Legend => "传说",
        _ => "普通",
    };

    /// <summary>一句话说明。</summary>
    public static string Description(TetrisAiLevel level) => level switch
    {
        TetrisAiLevel.Beginner => "只看「别堆太高」，会留空洞、偶尔乱放，适合练手",
        TetrisAiLevel.Hard => "更怕空洞与起伏，会攒多行一起消",
        TetrisAiLevel.Master => "往后推算 1 块，会为下一块铺路",
        TetrisAiLevel.Grandmaster => "往后推算 2 块，很少留洞",
        TetrisAiLevel.Legend => "往后推算 3 块，几乎不留洞，比较难赢",
        _ => "会主动消行、保持平整（默认）",
    };

    /// <summary>存储键（配置用；前四档与旧版兼容）。</summary>
    public static string ToKey(TetrisAiLevel level) => level switch
    {
        TetrisAiLevel.Beginner => "beginner",
        TetrisAiLevel.Hard => "hard",
        TetrisAiLevel.Master => "master",
        TetrisAiLevel.Grandmaster => "grandmaster",
        TetrisAiLevel.Legend => "legend",
        _ => "normal",
    };

    /// <summary>键 → 档位（认不出按普通）。</summary>
    public static TetrisAiLevel Parse(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "beginner" => TetrisAiLevel.Beginner,
        "hard" => TetrisAiLevel.Hard,
        "master" => TetrisAiLevel.Master,
        "grandmaster" or "gm" => TetrisAiLevel.Grandmaster,
        "legend" or "insane" => TetrisAiLevel.Legend,
        _ => TetrisAiLevel.Normal,
    };

    /// <summary>最多往后推算几块（含当前这一块）。</summary>
    public static int LookaheadPieces(TetrisAiLevel level) => level switch
    {
        TetrisAiLevel.Master => 2,
        TetrisAiLevel.Grandmaster => 3,
        TetrisAiLevel.Legend => 4,
        _ => 1,
    };

    /// <summary>走「次优解」的概率（0~1）：入门犯傻多，困难偶尔，大师以上不犯。</summary>
    public static double BlunderChance(TetrisAiLevel level) => level switch
    {
        TetrisAiLevel.Beginner => 0.35,
        TetrisAiLevel.Normal => 0.12,
        TetrisAiLevel.Hard => 0.04,
        _ => 0,
    };

    /// <summary>
    /// 评估权重（<paramref name="garbageFocused"/> = 垃圾行互攻：更看重一次消多行，好给对手塞垃圾）。
    /// <para><b>为什么必须按比例给</b>：这套分是「消行收益 vs 地形惩罚」的比值游戏。
    /// 空洞分一旦和消行分同量级，AI 就会宁可把方块堆到十几层，也不肯为了填一个洞而「破坏形状」，
    /// 最后一直不消行、直接顶死（实测过：空洞 -11 / 消行 +8 时，六档全都 30 来块就死、0 消行）。</para>
    /// <para>这里按经典 DELLacherry 权重取比值（消行 0.76 / 空洞 -0.36 / 高度 -0.51 / 起伏 -0.18），
    /// 再统一乘 10，并按档位把「消行」往上抬 —— 越强的档位越主动攒多行。</para>
    /// </summary>
    /// <summary>
    /// 评估权重（<paramref name="garbageFocused"/> = 垃圾行互攻：更看重一次消多行，好给对手塞垃圾）。
    ///
    /// <para><b>三条调参教训</b>（都在自检里踩过，别再退回去）：</para>
    /// <list type="number">
    ///   <item>空洞必须<b>最狠</b>：它是唯一「一旦造出来就几乎补不回来」的损伤。
    ///     空洞权重和消行收益同量级时，AI 会为了凑一次消行而满盘造洞，之后就再也消不掉了。</item>
    ///   <item>堆叠高度只能<b>很轻</b>：给重了 AI 不敢往上堆，反而宁可去塞那些填不满的深坑，越塞越死。</item>
    ///   <item>起伏要够重：它是「铺平、不挖坑」的直接信号，比高度有用得多。</item>
    /// </list>
    /// </summary>
    public static Weights WeightsOf(TetrisAiLevel level, bool garbageFocused) => level switch
    {
        // 入门：几乎不在乎消行、也不怕堆高（会自己作死，符合「练手」定位）
        TetrisAiLevel.Beginner => new Weights(0.76, -2.0, -0.3, -0.6, -0.3),
        TetrisAiLevel.Normal => new Weights(9.0, -2.0, -0.3, -0.6, -0.3),
        TetrisAiLevel.Hard => new Weights(garbageFocused ? 13.0 : 11.0, -2.4, -0.3, -0.7, -0.4),
        TetrisAiLevel.Master => new Weights(garbageFocused ? 16.0 : 14.0, -2.6, -0.3, -0.7, -0.5),
        TetrisAiLevel.Grandmaster => new Weights(garbageFocused ? 18.0 : 16.0, -3.0, -0.3, -0.8, -0.6),
        TetrisAiLevel.Legend => new Weights(garbageFocused ? 20.0 : 18.0, -3.4, -0.3, -0.9, -0.7),
        _ => new Weights(9.0, -2.0, -0.3, -0.6, -0.3),
    };

    /// <summary>根节点（当前这一手）最多往下算几个候选（其余只按当前分数排）。</summary>
    public static int RootBeam(TetrisAiLevel level) => level switch
    {
        TetrisAiLevel.Beginner => 6,
        TetrisAiLevel.Normal => 8,
        TetrisAiLevel.Hard => 10,
        TetrisAiLevel.Master => 10,
        TetrisAiLevel.Grandmaster => 12,
        _ => 12,
    };

    /// <summary>深层每层保留几个候选（越小越快、越省 CPU）。</summary>
    public static int InnerBeam(TetrisAiLevel level) => level switch
    {
        TetrisAiLevel.Master => 4,
        TetrisAiLevel.Grandmaster => 4,
        TetrisAiLevel.Legend => 3,
        _ => 4,
    };
}
