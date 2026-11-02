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

    /// <summary>
    /// 鼠标跟随「灵敏度」横向倍率（0.3 ~ 3.0）：越大表示鼠标离中心更近就能让头 / 眼转到更大角度。
    /// 1.0 = 原来手感；调大更跟手。
    /// </summary>
    public double EyeTrackingHorizontalGain { get; set; } = 1.0;

    /// <summary>鼠标跟随「抬头」方向灵敏度倍率（0.3 ~ 3.0）。</summary>
    public double EyeTrackingUpGain { get; set; } = 1.0;

    /// <summary>鼠标跟随「低头」方向灵敏度倍率（0.3 ~ 3.0）：觉得向下看跟不到就调大这个。</summary>
    public double EyeTrackingDownGain { get; set; } = 1.0;

    /// <summary>鼠标跟随的纵向中性偏移（-0.5 ~ 0.5）：向下 / 向上基准整体挪一点，用来校正模型左右不对称。</summary>
    public double EyeTrackingVerticalOffset { get; set; }

    // ---------- 摄像头面部动捕 ----------

    /// <summary>是否启用摄像头面部动捕（默认关：涉及隐私，需要用户自己打开）。</summary>
    public bool MocapEnabled { get; set; }

    /// <summary>使用的摄像头设备 Id（空 = 自动选第一个；设备 Id 在不同机器上会变，所以同时存名字）。</summary>
    public string MocapCameraId { get; set; } = "";

    /// <summary>摄像头显示名（只用于界面上认设备 / 设备 Id 失效时按名字再找一次）。</summary>
    public string MocapCameraName { get; set; } = "";

    /// <summary>前置摄像头镜像（画面左右翻转；想「自己的右手 = 模型右手」就保持开）。</summary>
    public bool MocapMirror { get; set; } = true;

    /// <summary>动捕是否驱动头部转动。</summary>
    public bool MocapHeadEnabled { get; set; } = true;

    /// <summary>动捕是否驱动眨眼。</summary>
    public bool MocapBlinkEnabled { get; set; } = true;

    /// <summary>动捕是否驱动张嘴。</summary>
    public bool MocapMouthEnabled { get; set; } = true;

    /// <summary>头部转动幅度倍率（0 ~ 1.5）。</summary>
    public double MocapHeadStrength { get; set; } = 1.0;

    /// <summary>眼球跟随幅度倍率（0 ~ 1.5）。</summary>
    public double MocapEyeStrength { get; set; } = 1.0;

    /// <summary>张嘴开合幅度倍率（0 ~ 1.5）。</summary>
    public double MocapMouthStrength { get; set; } = 1.0;

    /// <summary>跟随平滑（0 ~ 0.9，越大越稳、延迟越高）。</summary>
    public double MocapSmoothing { get; set; } = 0.45;

    /// <summary>动捕帧率：10 / 15 / 24（越高越顺，CPU 越高）。</summary>
    public int MocapFps { get; set; } = 15;

    /// <summary>测试页动捕卡片是否显示摄像头预览小窗（关掉更省 CPU）。</summary>
    public bool MocapShowPreview { get; set; } = true;

    /// <summary>校准：中性表情时脸中心在画面里的横向位置（0~1，默认画面正中）。</summary>
    public double MocapNeutralFaceX { get; set; } = 0.5;

    /// <summary>校准：中性表情时脸中心在画面里的纵向位置（0~1，默认画面正中）。</summary>
    public double MocapNeutralFaceY { get; set; } = 0.5;

    // ---------- 身体晃动测试 ----------

    /// <summary>是否开启身体晃动测试（用一组身体 / 腰部 / 物理输入参数做正弦晃动）。</summary>
    public bool BodySwayEnabled { get; set; }

    /// <summary>晃动模式：左右摇 / 前后摇 / 扭腰 / 椭圆 / 自然晃（多频叠加）。</summary>
    public string BodySwayMode { get; set; } = "左右摇";

    /// <summary>晃动幅度（0 ~ 30 度）。</summary>
    public double BodySwayAmplitude { get; set; } = 12;

    /// <summary>晃动速度（0.1 ~ 2 Hz）。</summary>
    public double BodySwaySpeed { get; set; } = 0.5;

    /// <summary>头部是否反向补偿 / 跟随（0 = 完全跟着身体晃，1 = 头尽量保持朝前）。</summary>
    public double BodySwayHeadFollow { get; set; } = 0.35;

    /// <summary>是否连腰部 / 腿部参数一起晃（关掉只看身体主轴 + 物理输入）。</summary>
    public bool BodySwayIncludeHips { get; set; } = true;

    // ---------- 音乐跟唱 / 律动（系统音频回环） ----------

    /// <summary>是否开启音乐跟唱 / 律动（默认关；打开后采集系统正在播放的声音，不申请麦克风）。</summary>
    public bool MusicReactiveEnabled { get; set; }

    /// <summary>识别模式：自动识别 / 强制跟唱 / 强制纯音乐。</summary>
    public string MusicReactiveMode { get; set; } = "自动识别";

    /// <summary>歌词 / 纯音乐识别灵敏度（0.3 ~ 3.0；越大越容易判成「带歌词」）。</summary>
    public double MusicReactiveSensitivity { get; set; } = 1.0;

    /// <summary>跟唱嘴型幅度倍率（0 ~ 1.5）。</summary>
    public double MusicLipSyncStrength { get; set; } = 1.0;

    /// <summary>身体摇晃幅度倍率（0 ~ 1.5）。</summary>
    public double MusicSwayStrength { get; set; } = 1.0;

    /// <summary>音乐播放时是否轻微摇晃身体（纯音乐也晃；关掉则只做嘴型 / 点头）。</summary>
    public bool MusicSwayEnabled { get; set; } = true;

    /// <summary>音乐播放时是否轻微点头（跟节拍；被动捕 / 眼睛跟随时不抢头部参数）。</summary>
    public bool MusicHeadBobEnabled { get; set; } = true;

    // ---------- 全身轻晃（以脚为支点整个身体左右轻倾） ----------

    /// <summary>
    /// 是否开启「全身轻晃」：日常也以脚为支点把整个身体左右轻倾（参考 petbox 的 0.6° / 4 秒），
    /// 有音乐时自动加快一点。这是渲染层旋转，不写模型参数，所以头 / 腿会一起倾斜。
    /// </summary>
    public bool BodyLeanEnabled { get; set; } = true;

    // ---------- 语音说话 / 聊天气泡（Windows 自带 TTS） ----------

    /// <summary>语音 id（空 = 自动选第一个中文女声；可在「说话」卡里换）。</summary>
    public string SpeechVoiceId { get; set; } = "";

    /// <summary>语速倍率（0.6 ~ 1.6；参考 SSML prosody rate）。</summary>
    public double SpeechRate { get; set; } = 0.95;

    /// <summary>音调偏移百分比（-20 ~ 40，稍微抬高更像软妹音）。</summary>
    public double SpeechPitchPercent { get; set; } = 0;

    /// <summary>说话音量（0 ~ 1）。</summary>
    public double SpeechVolume { get; set; } = 0.9;

    /// <summary>说话时在桌宠头上显示聊天气泡。</summary>
    public bool SpeechBubbleEnabled { get; set; } = true;

    /// <summary>说话时自动配一个小动作（歪头 / 点头 / 手势，更生动）。</summary>
    public bool SpeechAutoGesture { get; set; } = true;

    /// <summary>空闲时随机自言自语（默认关；打开后每隔一段时间说一句软妹台词）。</summary>
    public bool SpeechIdleChatterEnabled { get; set; }

    // ---------- 待机随机小动作（更生动） ----------

    /// <summary>待机时每隔一段时间随机做个小动作（歪头 / 张望 / 小手势 / 表情）。</summary>
    public bool IdleRandomGestureEnabled { get; set; } = true;

    /// <summary>待机随机小动作间隔（秒，8 ~ 300）。</summary>
    public double IdleRandomGestureIntervalSeconds { get; set; } = 22;

    // ---------- 鼠标互动玩法（参考 FULASKA/-live2d-） ----------

    /// <summary>是否开启桌宠鼠标互动玩法（摸头 / 捏脸 / 挠痒 / 护住 / 手部手势 / 额头表情循环）。</summary>
    public bool PetInteractionEnabled { get; set; } = true;

    /// <summary>互动时是否说话（软妹语音 + 气泡；台词按互动类型随机）。</summary>
    public bool PetInteractionVoiceEnabled { get; set; } = true;

    /// <summary>拖动桌宠时是否带「拖拽物理」（头发 / 裙子会甩）。</summary>
    public bool PetDragPhysicsEnabled { get; set; } = true;

    /// <summary>待机随机小动作（换脚 / 东张西望 / 伸懒腰 / 挥挥手 / 戴铁盆 / 爱心眼等）间隔（秒，8 ~ 30）。</summary>
    public double PetIdleActionIntervalSeconds { get; set; } = 12;

    /// <summary>待机时是否随机换表情（爱心眼 / 脸红 / 屑屑眼等，轻轻增加生动度）。</summary>
    public bool PetIdleExpressionEnabled { get; set; } = true;

    /// <summary>是否启用全局快捷键（Ctrl+Alt+L 切换鼠标穿透、Ctrl+Alt+0 退出程序）。</summary>
    public bool PetHotkeysEnabled { get; set; } = true;
}
