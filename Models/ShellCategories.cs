using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App.Models;

/// <summary>
/// 外壳维护的类别元数据清单（图标 / 描述 / 排序）。
///
/// <para>类别本身是各工具在 <see cref="ITool.CategoryPath"/> 里声明出来的，这里只负责“长什么样”：
/// 不在这里登记也能用（默认 📁、无描述）；登记了就显示 Emoji、描述，并按 <see cref="ToolCategoryInfo.Order"/> 排序。
/// 以后要加新大类，在这里补一条即可，界面不用改。</para>
/// </summary>
public static class ShellCategories
{
    /// <summary>「媒体」类别名（音频 / 视频处理、播放与图片阅读）。</summary>
    public const string Media = "媒体";

    /// <summary>「文档」类别名（Office / PDF 阅读、文本编辑与转换）。</summary>
    public const string Documents = "文档";

    /// <summary>「文件」类别名（压缩包 / 压缩器、批量重命名与打开方式）。</summary>
    public const string Files = "文件";

    /// <summary>「网络」类别名（浏览器、聊天室、邮箱与下载）。</summary>
    public const string Network = "网络";

    /// <summary>「桌面」类别名（桌宠、屏幕贴纸与屏幕画笔）。</summary>
    public const string Desktop = "桌面";

    /// <summary>「系统」类别名（任务 / 应用 / 启动项 / 右键菜单 / 通知与截图）。</summary>
    public const string SystemTools = "系统";

    /// <summary>「时间」类别名（时钟、秒表、日历、倒计时与闹钟）。</summary>
    public const string Time = "时间";

    /// <summary>「数学」类别名（计算、绘图、随机数与数字转换）。</summary>
    public const string MathTools = "数学";

    /// <summary>「生活」类别名（天气、日记与表情包）。</summary>
    public const string Life = "生活";

    /// <summary>「音游工具」类别名（osu! 系列工具都归这一类）。</summary>
    public const string MusicGame = "音游工具";

    /// <summary>「棋牌」类别名（联机五子棋 / 联机象棋归这一类）。</summary>
    public const string BoardGames = "棋牌";

    /// <summary>「游戏」类别名（俄罗斯方块等单机小游戏归这一类）。</summary>
    public const string Games = "游戏";

    /// <summary>「开发」类别名（验证码联调与框架示例）。</summary>
    public const string Development = "开发";

    /// <summary>
    /// 全部已登记的类别元数据（排序就是主页「全部分类」卡片的显示顺序）。
    /// 工具只负责在 <see cref="ITool.CategoryPath"/> 里声明类别名，图标 / 描述 / 排序统一在这里维护。
    /// </summary>
    public static IReadOnlyList<ToolCategoryInfo> All { get; } = new[]
    {
        new ToolCategoryInfo
        {
            Path = new[] { Media },
            EmojiIcon = "🎬",
            Description = "音频 / 视频处理、录屏剪辑、播放器与图片阅读",
            Order = 10,
        },
        new ToolCategoryInfo
        {
            Path = new[] { Documents },
            EmojiIcon = "📖",
            Description = "Word / Excel / PPT / PDF 阅读，文本编辑与文本转换",
            Order = 20,
        },
        new ToolCategoryInfo
        {
            Path = new[] { Files },
            EmojiIcon = "🗂️",
            Description = "压缩包、压缩器、批量重命名与打开方式",
            Order = 30,
        },
        new ToolCategoryInfo
        {
            Path = new[] { Network },
            EmojiIcon = "🌐",
            Description = "网页浏览、联机聊天、邮箱收发与网页下载",
            Order = 40,
        },
        new ToolCategoryInfo
        {
            Path = new[] { Desktop },
            EmojiIcon = "🖥️",
            Description = "Live2D 桌宠、屏幕贴纸与屏幕画笔",
            Order = 50,
        },
        new ToolCategoryInfo
        {
            Path = new[] { SystemTools },
            EmojiIcon = "🛠️",
            Description = "任务管理、应用与启动项管理、右键菜单、通知与截图",
            Order = 60,
        },
        new ToolCategoryInfo
        {
            Path = new[] { Time },
            EmojiIcon = "⏰",
            Description = "全球时钟、秒表、日历、倒计时与闹钟",
            Order = 70,
        },
        new ToolCategoryInfo
        {
            Path = new[] { MathTools },
            EmojiIcon = "🧮",
            Description = "科学计算、函数绘图、随机数与数字转换",
            Order = 80,
        },
        new ToolCategoryInfo
        {
            Path = new[] { Life },
            EmojiIcon = "🌿",
            Description = "天气、日记与表情包收藏",
            Order = 90,
        },
        new ToolCategoryInfo
        {
            Path = new[] { MusicGame },
            EmojiIcon = "🎵",
            Description = "osu! 谱面转换、LN 处理、等级计算与下图",
            Order = 100,
        },
        new ToolCategoryInfo
        {
            Path = new[] { BoardGames },
            EmojiIcon = "🎲",
            Description = "联机五子棋 / 象棋 / 围棋 / 斗地主 / 军棋：开一个房间就能和朋友对弈，也能和电脑玩",
            Order = 110,
        },
        new ToolCategoryInfo
        {
            Path = new[] { Games },
            EmojiIcon = "🎮",
            Description = "单机小游戏：俄罗斯方块、贪吃蛇、VSRG 音游…（打开就能玩，不需要联网）",
            Order = 120,
        },
        new ToolCategoryInfo
        {
            Path = new[] { Development },
            EmojiIcon = "🧪",
            Description = "验证码联调与框架示例",
            Order = 130,
        },
    };
}
