namespace YuLiangToolkit.Tools.Games.Tetris;

/// <summary>说明窗口里的一条「按键 → 作用」。</summary>
/// <param name="Keys">按键文字（例如 <c>←</c> / <c>↑ 或 X</c> / <c>空格</c>）。</param>
/// <param name="Action">这一手做什么。</param>
/// <param name="Tip">补充说明（可空）。</param>
public readonly record struct TetrisHelpItem(string Keys, string Action, string Tip = "");

/// <summary>说明窗口里的一个分区。</summary>
/// <param name="Title">分区标题（例如「移动与旋转」）。</param>
/// <param name="Description">分区前面的一句话说明（可空）。</param>
/// <param name="Items">分区里的条目。</param>
public sealed record TetrisHelpSection(
    string Title,
    string Description,
    IReadOnlyList<TetrisHelpItem> Items);

/// <summary>
/// 「游玩说明」窗口的**纯数据内容**（不碰界面）：分区、按键、计分规则、小技巧。
///
/// <para>放成纯逻辑类有两个好处：① 说明窗口只负责排版，改文案不用动 XAML；
/// ② 自检能直接断言「每个能用的键都写进说明里了」，以后加键忘了补文档会红。</para>
/// </summary>
public static class TetrisHelpContent
{
    /// <summary>窗口标题。</summary>
    public const string Title = "俄罗斯方块 · 游玩说明";

    /// <summary>标题下面那行总览。</summary>
    public const string Summary =
        "10 × 20 的棋盘上，方块从顶部落下。用方向键把它摆好、塞满一整行就会消掉，"
        + "一直消下去，直到新方块顶到出生位置（游戏结束）。得分越高等级越高，方块落得也越快。";

    /// <summary>全部按键（自检拿它核对「每个键都写进说明」）。</summary>
    public static IReadOnlyList<string> AllKeys { get; } = new[]
    {
        "←", "→", "↓", "↑", "X", "Z", "空格", "C", "Esc", "P", "R", "Enter", "F1",
    };

    /// <summary>
    /// 说明窗口的栏数（左右两栏）：左栏放「怎么操作方块」，右栏放「什么时候按、怎么得分、联机」。
    /// 自检拿它核对「分区切分合理」。
    /// </summary>
    public const int LeftColumnSectionCount = 3;

    /// <summary>说明里和联机相关的小节标题（自检核对它确实写了联机）。</summary>
    public const string NetSectionTitle = "联机对战（1 对 1 + 观战）";

    /// <summary>说明分区。</summary>
    public static IReadOnlyList<TetrisHelpSection> Sections { get; } = new[]
    {
        new TetrisHelpSection("移动与旋转", "键盘在页面上任何位置都能用（焦点在侧边栏时先点一下棋盘）。", new[]
        {
            new TetrisHelpItem("← / →", "左右移动一格", "贴着墙就移不动了，这是正常的"),
            new TetrisHelpItem("↓", "软降：往下挪一格，每格 +1 分", "按住会连续下滑；已经到底时会立刻锁定"),
            new TetrisHelpItem("↑ 或 X", "顺时针旋转 90°", "两个键效果一样，挑顺手的一个"),
            new TetrisHelpItem("Z", "逆时针旋转 90°", "和上一条相反方向；贴墙、贴地时会自动「踢」开一格（SRS 墙踢）"),
        }),

        new TetrisHelpSection("快速落底", "", new[]
        {
            new TetrisHelpItem("空格", "硬降：直接落到底并立刻锁定", "按落下的格子数加分（每格 2 分）；遮罩提示「准备好了吗」时按它是开始游戏"),
            new TetrisHelpItem("落点预览", "棋盘上那块半透明的影子", "就是硬降之后方块会停的位置，先在设置里把「显示幽灵方块」打开"),
        }),

        new TetrisHelpSection("暂存与预览", "", new[]
        {
            new TetrisHelpItem("C", "暂存（Hold）：把手上这块存起来，换出下一块", "一个方块落地之前只能换一次；暂存区里已经有东西时是「互换」"),
            new TetrisHelpItem("右侧「接下来」", "显示后面 3 块是什么", "用 C 换出来的是第 1 块，提前规划能少留坑"),
        }),

        new TetrisHelpSection("暂停、重开与开始", "", new[]
        {
            new TetrisHelpItem("Esc 或 P", "暂停 / 继续", "暂停时棋盘会盖上遮罩，方块不会继续掉"),
            new TetrisHelpItem("R", "重新开始一局", "当前分数不保留（历史最高分会记住）"),
            new TetrisHelpItem("Enter", "开始 / 继续", "遮罩提示「准备好了吗」时用它开局；游戏结束后再按一次是重开"),
            new TetrisHelpItem("F1 或「游玩说明」按钮", "打开这个说明窗口", "读说明时会自动暂停，关掉窗口后点「继续」接着玩"),
        }),

        new TetrisHelpSection("鼠标也能玩", "不想用键盘就点页面底部那排按钮。", new[]
        {
            new TetrisHelpItem("← → ↓ 软降", "左移 / 右移 / 下落一格", ""),
            new TetrisHelpItem("↻ 顺时针 / ↺ 逆时针", "旋转当前方块", ""),
            new TetrisHelpItem("硬降 / 暂存", "直接落底 / 暂存当前方块", ""),
        }),

        new TetrisHelpSection("联机对战（1 对 1 + 观战）", "页面顶部有三个选项：单人 / 联机 · 垃圾行互攻 / 联机 · 各自计分。", new[]
        {
            new TetrisHelpItem("开房间", "你当房主，等好友连进来", "填个玩家名（对方看到的名字），端口留 0 让系统自动挑；开好后把「把地址复制发给好友」里的地址发给对方"),
            new TetrisHelpItem("加入房间", "填对方发来的地址连过去", "形如 192.168.1.5:5310；同一局域网直接用内网地址就行"),
            new TetrisHelpItem("只观战", "对战位满了也能进去看", "房间最多 8 人观战；观战者能看两边棋盘与分数，但不能操作（按键无效）"),
            new TetrisHelpItem("⚔ 垃圾行互攻", "自己消行就给对手塞垃圾行", "一次消 2/3/4 行分别送 1/2/4 行；谁先被顶死谁输。塞来的垃圾行每行会留一个缺口"),
            new TetrisHelpItem("🏁 各自计分", "双方互不干扰，比谁分高", "两边出块序列完全一样（同一个随机种子），所以比的是操作；谁先顶死谁输，都顶死时比分数"),
            new TetrisHelpItem("对手区", "右侧显示对手分数 / 行数 / 等级", "垃圾行互攻时还会显示对方即将被塞多少行，以及对手棋盘的缩略图"),
            new TetrisHelpItem("掉线", "对局中有人掉线 = 判负", "不是作废本局：对方掉线你赢，你掉线就判你负"),
            new TetrisHelpItem("离开房间", "点「离开房间」/「关闭房间」", "房主关掉房间时对方会立刻看到提示"),
        }),
    };

    /// <summary>计分规则（窗口里单独一块，用列表显示）。</summary>
    public static IReadOnlyList<string> ScoreRules { get; } = new[]
    {
        "一次消 1 行 = 100 分 × 当前等级",
        "一次消 2 行 = 300 分 × 当前等级",
        "一次消 3 行 = 500 分 × 当前等级",
        "一次消 4 行 = 800 分 × 当前等级（TETRIS，最划算）",
        "软降每格 +1 分，硬降每格 +2 分",
        "每消 10 行升 1 级；等级越高落得越快（800ms 起，每级快 12%，最快 60ms）",
        "最高分在游戏结束时刷新，存在 Config\\tetris\\config.json",
    };

    /// <summary>小技巧（窗口最下面一条条的提示）。</summary>
    public static IReadOnlyList<string> Tips { get; } = new[]
    {
        "尽量别留「一格宽的深井」：竖着的 I 长条只能落一次，别的方块下去就是坑。",
        "优先消掉最底下那行，别让方块一路堆到顶。",
        "一次消 4 行（TETRIS）分数是消 1 行的 8 倍，宁可先摆平也别急着消单行。",
        "手里这块不合适时按 C 先存起来，等合适的时机再换回来。",
        "到了高等级方块落得很快，多用「幽灵方块」预判落点，别再一格一格试。",
    };
}
