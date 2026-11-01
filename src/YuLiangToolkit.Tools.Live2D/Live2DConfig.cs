using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Live2D;

/// <summary>
/// 「Live2D 桌宠」的强类型配置 → <c>Config\live2d\config.json</c>（改一下就自动存，没有保存按钮）。
///
/// <para>约定：<b>需要记住的选项都写成可读写属性</b>，默认值写在属性上；
/// 只有界面联动用的计算属性才标 <c>[JsonIgnore]</c>。桌宠窗口的位置 / 大小 / 透明度也都记在这里，
/// 所以关掉程序再打开，桌宠还停在原来的地方。</para>
/// </summary>
public sealed class Live2DConfig : ToolConfig
{
    // ---------- 桌宠窗口 ----------

    /// <summary>桌宠是否显示（点「显示桌宠」后为 true；程序退出后下次打开工具页时按这个值恢复）。</summary>
    public bool PetEnabled { get; set; }

    /// <summary>
    /// 桌宠窗口左边位置（屏幕像素；<c>null</c> = 还没摆过，首次显示时由程序放到右下角并写回具体值）。
    /// <para>⚠ 用可空 double 而不是 <see cref="double.NaN"/>：System.Text.Json 默认**不能**序列化 NaN
    /// （会让整个配置文件写成一条「序列化失败」注释，下次启动被判损坏并备份）。</para>
    /// </summary>
    public double? PetLeft { get; set; }

    /// <summary>
    /// 桌宠窗口上边位置（屏幕像素；<c>null</c> = 还没摆过，首次显示时由程序放到右下角并写回具体值）。
    /// <para>⚠ 同 <see cref="PetLeft"/>：别用 NaN，JSON 存不了。</para>
    /// </summary>
    public double? PetTop { get; set; }

    /// <summary>桌宠显示宽度（像素，高度按模型比例自动算；160 ~ 1000）。</summary>
    public double PetWidth { get; set; } = 420;

    /// <summary>桌宠不透明度（0.2 ~ 1.0）。</summary>
    public double PetOpacity { get; set; } = 1.0;

    /// <summary>桌宠是否置顶显示。</summary>
    public bool PetTopMost { get; set; } = true;

    /// <summary>透明区域是否让鼠标点穿（关掉则整个窗口都挡住鼠标）。</summary>
    public bool PetClickThrough { get; set; } = true;

    /// <summary>主程序启动时自动把桌宠放出来（不用先打开工具页）。</summary>
    public bool AutoStartPet { get; set; }

    // ---------- 模型 / 渲染 ----------

    /// <summary>自定义模型文件夹（空 = 用内置的 DeepSeek 模型）。</summary>
    public string ModelFolder { get; set; } = "";

    /// <summary>纹理最大尺寸：2048 省内存（默认），4096 原始精度（改完点「重新加载模型」生效）。</summary>
    public int TextureMaxSize { get; set; } = 2048;

    /// <summary>渲染帧率上限：15 / 30 / 60。</summary>
    public int FpsLimit { get; set; } = 30;

    /// <summary>渲染分辨率倍率（0.5 ~ 1.5；0.75 适合低配 / 高分辨率缩放场景；只影响工具页预览）。</summary>
    public double RenderScale { get; set; } = 1.0;

    // ---------- 预览 / 调试视图（只作用于工具页测试台，不影响桌宠） ----------

    /// <summary>预览左下角是否显示 FPS / 网格 / 三角形 / 耗时统计。</summary>
    public bool ShowPreviewStats { get; set; } = true;

    /// <summary>预览背景：true = 透明棋盘格（能看出透明区域），false = 纯色。</summary>
    public bool PreviewCheckerboard { get; set; } = true;

    /// <summary>调试：画出所有网格的包围盒（线框）。</summary>
    public bool ShowDrawableBounds { get; set; }

    /// <summary>调试：把所有带蒙版的网格整体染红（看蒙版范围）。</summary>
    public bool ShowMaskRanges { get; set; }

    /// <summary>调试：点击模型时高亮命中的网格。</summary>
    public bool HighlightPickedDrawable { get; set; } = true;

    /// <summary>调试：把当前选中网格的蒙版缓冲区直接可视化。</summary>
    public bool ShowMaskBuffer { get; set; }

    // ---------- 动作 / 表情 ----------

    /// <summary>动作淡入淡出时长（秒；动作文件自带 Meta 时以文件为准，这里给界面播放用）。</summary>
    public double MotionFadeSeconds { get; set; } = 0.3;

    /// <summary>表情淡入淡出时长（秒）。</summary>
    public double ExpressionFadeSeconds { get; set; } = 0.3;

    /// <summary>动作列表里「循环」的默认勾选状态（默认 false = 点播一次，播完自动回待机；每个动作行也能单独改）。</summary>
    public bool MotionLoopDefault { get; set; }

    // ---------- 表情 / 动作 / 物理 ----------

    /// <summary>启用物理演算（头发 / 尾巴 / 衣服摆动）。</summary>
    public bool PhysicsEnabled { get; set; } = true;

    /// <summary>物理强度倍率（0 = 不摆动，1 = 原始幅度；运行时的物理权重上限就是 1）。</summary>
    public double PhysicsStrength { get; set; } = 1.0;

    /// <summary>启用待机动画（呼吸 / 眨眼 / 轻微摇摆）。</summary>
    public bool IdleEnabled { get; set; } = true;

    /// <summary>待机动作名称（MotionEntry.Name；空 = 自动找 Animation 里的 IDLE）。</summary>
    public string IdleMotion { get; set; } = "";

    /// <summary>自动眨眼。</summary>
    public bool AutoBlink { get; set; } = true;

    // ---------- 眼睛跟随鼠标 ----------

    /// <summary>眼睛 / 头部是否跟随鼠标。</summary>
    public bool EyeTracking { get; set; } = true;

    /// <summary>跟随平滑度（0.05 ~ 1.0，越大越跟手、越小越柔和）。</summary>
    public double EyeTrackingSmoothing { get; set; } = 0.35;

    /// <summary>头部转动幅度倍率（0.2 ~ 1.5）。</summary>
    public double EyeTrackingHeadStrength { get; set; } = 1.0;

    /// <summary>眼球转动幅度倍率（0.2 ~ 1.5）。</summary>
    public double EyeTrackingEyeStrength { get; set; } = 1.0;

    /// <summary>身体 / 腰部跟随幅度倍率（0 ~ 1.5）。</summary>
    public double EyeTrackingBodyStrength { get; set; } = 0.5;
}
