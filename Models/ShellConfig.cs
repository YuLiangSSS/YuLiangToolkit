using YuLiangToolkit.App.Themes;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Effects;

namespace YuLiangToolkit.App.Models;

/// <summary>主页功能卡片的排序方式（存在 <c>Config\shell\config.json</c>，重启后保持）。</summary>
public enum ToolSortMode
{
    /// <summary>默认顺序：按 <see cref="YuLiangToolkit.Core.Tools.ITool.Order"/>，即作者排的优先级。</summary>
    Default,

    /// <summary>最近使用：刚打开过的功能排最前面（从未用过的排最后）。</summary>
    RecentlyUsed,

    /// <summary>名称 A→Z（中文按当前系统区域的拼音顺序）。</summary>
    NameAsc,

    /// <summary>名称 Z→A。</summary>
    NameDesc,

    /// <summary>按分类归堆，同分类内再按默认优先级。</summary>
    Category,
}

/// <summary>点主窗口右上角 × 时的行为（存在 <c>Config\shell\config.json</c>，重启后保持）。</summary>
public enum ShellCloseAction
{
    /// <summary>关闭应用（默认，和以前一样）。</summary>
    Exit,

    /// <summary>最小化到任务栏右侧的托盘：窗口藏起来，应用继续在后台运行。</summary>
    MinimizeToTray,
}

/// <summary>
/// 程序外壳（主页 / 导航）自己的配置 → <c>Config\shell\config.json</c>。
///
/// <para>外壳也算一个“功能”，所以同样用 <see cref="ToolConfig"/>：改一下就自动落盘。
/// 这里存主页排序偏好，以及每个功能上次被打开的时间（“最近使用”排序的依据，
/// 也顺便让你在卡片上看到“3 天前用过”）。</para>
/// </summary>
public sealed class ShellConfig : ToolConfig
{
    /// <summary>主页排序方式。</summary>
    public ToolSortMode SortMode { get; set; } = ToolSortMode.Default;

    /// <summary>功能 Id → 上次打开时间（本地时间）。</summary>
    public Dictionary<string, DateTime> LastUsed { get; set; } = new();

    /// <summary>
    /// 是否绘制鼠标粒子效果（**老版本的开关，v0.26.0 起只用于配置迁移**，界面不再直接改它）。
    /// 新配置改用 <see cref="AutoEffects"/> / <see cref="ClickEffects"/> 两个 Id 列表：
    /// 老配置里没有这两个字段时按它迁移（开着 = 粒子拖尾 + 粒子爆炸，关着 = 两个都空），
    /// 保证老用户升级后的行为与以前一致。
    /// </summary>
    public bool ShowParticles { get; set; } = true;

    /// <summary>
    /// 自动效果 Id 列表（<c>null</c> = 老配置没有这个字段，加载时按 <see cref="ShowParticles"/> 迁移；
    /// 显式空数组 = 用户就是不要自动效果）。可选值见 <see cref="CursorEffects"/>：
    /// <c>particle-trail</c> 粒子拖尾、<c>cursor-glow</c> 鼠标散光。
    /// </summary>
    public List<string>? AutoEffects { get; set; }

    /// <summary>
    /// 点击效果 Id 列表（可多选；<c>null</c> = 老配置没有这个字段，按 <see cref="ShowParticles"/> 迁移）。
    /// 可选值见 <see cref="CursorEffects"/>：<c>particle-burst</c> 粒子爆炸、
    /// <c>dispersion</c> 散光扩散、<c>glow-flash</c> 光晕闪烁、<c>invert-circle</c> 反色圆环。
    /// 与自动效果互不影响：自动效果全关、点击效果照样能触发。
    /// </summary>
    public List<string>? ClickEffects { get; set; }

    // ---------- 光标特效可调参数（v0.37.0，主页设置里的滑块） ----------
    //
    // 默认值 = v0.36.x 的固定外观；老配置没有这些字段时读出来就是默认值、行为不变。
    // 取值范围 / 步进集中在 Core/Effects/CursorEffectTuning.cs：设置页按它生成滑块，
    // 渲染层（ParticleOverlay.Tuning）再按它夹取，避免两处各写一套数字。

    /// <summary>粒子拖尾：每基准帧生成数量（个/帧，默认 1）。</summary>
    public double TrailParticleCount { get; set; } = 1.0;

    /// <summary>粒子拖尾：粒子大小倍率（默认 1.0 = 原来的 5px）。</summary>
    public double TrailParticleSize { get; set; } = 1.0;

    /// <summary>鼠标散光：光晕半径 DIP（默认 46）。</summary>
    public double CursorGlowRadius { get; set; } = 46.0;

    /// <summary>粒子爆炸：一次炸开多少颗（默认 24；右键按同比例更多）。</summary>
    public double BurstParticleCount { get; set; } = 24.0;

    /// <summary>粒子爆炸：扩散范围倍率（默认 1.0 = 原来的速度 / 射程）。</summary>
    public double BurstRange { get; set; } = 1.0;

    /// <summary>散光扩散：外圈最大半径 DIP（默认 98）。</summary>
    public double DispersionRadius { get; set; } = 98.0;

    /// <summary>光晕闪烁：光团最大半径 DIP（默认 64）。</summary>
    public double GlowFlashRadius { get; set; } = 64.0;

    /// <summary>反色圆环：最大半径 DIP（默认 150，约等于 v0.36.x 的自动半径）。</summary>
    public double InvertCircleRadius { get; set; } = 150.0;

    /// <summary>
    /// 反色圆环：两个圆叠到同一片区域时，重叠处是否**再反一次变回正色**（奇偶叠加，v0.37.2，默认开）。
    /// 关掉 = 后触发的圆直接盖住先前的（重叠处仍是反色，和 v0.37.1 之前一样）。
    /// </summary>
    public bool InvertCircleOverlapCancels { get; set; } = true;

    /// <summary>
    /// 界面主题（「主页设置 → 界面风格」，默认 <see cref="UiTheme.Default"/>）。
    /// 选 <see cref="UiTheme.LiquidGlass"/> 切换成第二套 UI（液态玻璃）。
    /// 旧配置文件没有这个字段时读出来就是默认值 —— 老用户升级后外观不变。
    /// </summary>
    public UiTheme UiTheme { get; set; } = UiTheme.Default;

    /// <summary>
    /// 主页「快速筛选」标签墙是否显示（「主页设置 → 主页显示」，默认 <c>true</c> = 和以前一样显示）。
    /// 标签多的时候整块墙比较占地方，不需要的人可以在设置里关掉；
    /// 关掉后搜索框仍然能按标签搜到功能。旧配置文件没有这个字段时按默认值 true 处理（行为不变）。
    /// </summary>
    public bool ShowHomeTags { get; set; } = true;

    // ---------- 粒子颜色（v0.22.0，主页设置「粒子颜色」卡片） ----------

    /// <summary>是否启用自定义粒子颜色（默认关 = 原来的淡紫白，行为完全不变）。</summary>
    public bool ParticleCustomColors { get; set; }

    /// <summary>颜色 1 是否启用（默认开，但总开关关着时不生效）。</summary>
    public bool ParticleColor1Enabled { get; set; } = true;

    /// <summary>颜色 2 是否启用；只启用其中一种时粒子是单色，两种都启用时在区间内随机取色。</summary>
    public bool ParticleColor2Enabled { get; set; } = true;

    /// <summary>颜色 1（<c>#RRGGBB</c>）。</summary>
    public string ParticleColor1 { get; set; } = "#F2EEFF";

    /// <summary>颜色 2（<c>#RRGGBB</c>）。</summary>
    public string ParticleColor2 { get; set; } = "#8FB4FF";

    /// <summary>
    /// 点窗口右上角 × 时的行为（主页设置里的选项，默认 <see cref="ShellCloseAction.Exit"/> = 关闭应用）。
    /// 选 <see cref="ShellCloseAction.MinimizeToTray"/> 后 × 只把窗口藏到托盘，应用继续在后台运行。
    /// </summary>
    public ShellCloseAction CloseAction { get; set; } = ShellCloseAction.Exit;

    /// <summary>取某个功能上次使用时间（没有记录返回 null）。</summary>
    public DateTime? GetLastUsed(string toolId)
        => LastUsed.TryGetValue(toolId, out var time) ? time : null;

    /// <summary>记录某个功能刚刚被使用（字典改动会被后台自动保存捡到）。</summary>
    public void MarkUsed(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return;
        LastUsed[toolId] = DateTime.Now;
    }
}
