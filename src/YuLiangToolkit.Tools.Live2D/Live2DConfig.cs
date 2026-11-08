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

    // ==================== AI 聊天（DeepSeek API） ====================
    //
    // 说明：API Key 落盘前用 Windows DPAPI（当前用户）加密，`AiApiKeyProtected` 里存的是 `dpapi:...` 密文；
    // 换 Windows 用户 / 换机器解不开（界面显示空，重新填一次即可）。聊天记录 / 好感度数值存在
    // Data\live2d\ai\chat-state.json（用户数据，不进配置导出）。

    /// <summary>DeepSeek API Key 的 DPAPI 密文（明文只在内存里；空 = 没填，走离线关键词兜底）。</summary>
    public string AiApiKeyProtected { get; set; } = "";

    /// <summary>接口根地址（默认官方 <c>https://api.deepseek.com</c>；兼容代理时改这里）。</summary>
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>模型名（默认 <c>deepseek-flash</c>；也可填 <c>deepseek-v4-pro</c>）。</summary>
    public string AiModel { get; set; } = "deepseek-flash";

    /// <summary>思考强度：关闭 / 低 / 中 / 高（关闭最省 token，桌宠聊天默认关闭）。</summary>
    public string AiThinkingLevel { get; set; } = "关闭";

    /// <summary>单次回复最大 token：40 ~ 1000，默认 160（越小越省；聊天不需要长回复）。</summary>
    public int AiMaxTokens { get; set; } = 160;

    /// <summary>带多少轮历史对话（0 ~ 20，默认 6；只算用户 + 桌宠消息，越大越费 token）。</summary>
    public int AiHistoryTurns { get; set; } = 6;

    /// <summary>人格预设 Id：gentle（温柔女友）/ tsundere（傲娇）/ yandere（病娇）/ genki（元气妹妹）。</summary>
    public string AiPersonaId { get; set; } = "gentle";

    /// <summary>自定义人格补充（可选，最多取前 200 字；会拼在人格预设后面）。</summary>
    public string AiPersonaCustom { get; set; } = "";

    /// <summary>用户称呼预设：用户 / 老公 / 主人。</summary>
    public string AiAddressPreset { get; set; } = "主人";

    /// <summary>自定义用户称呼（非空时优先于预设，最多 12 字）。</summary>
    public string AiAddressCustom { get; set; } = "";

    /// <summary>桌宠自称预设：我 / 本小姐 / 本姑娘 / 本鱼。</summary>
    public string AiSelfPreset { get; set; } = "我";

    /// <summary>自定义桌宠自称（非空时优先于预设，最多 12 字）。</summary>
    public string AiSelfCustom { get; set; } = "";

    /// <summary>人格属性（逗号分隔，可多选：容易害羞 / 爱吃醋 / 黏人 / 爱撒娇 / 傲娇口癖 / 容易生气 / 爱夸奖）。</summary>
    public string AiAttributes { get; set; } = "";

    /// <summary>自由「自我设定 / 背景 / 人格约束」（可选，最多取前 300 字）。</summary>
    public string AiCustomSetting { get; set; } = "";

    /// <summary>AI 回复是否朗读（TTS 语音 + 气泡；关掉只显示文字）。</summary>
    public bool AiSpeakReply { get; set; } = true;

    /// <summary>是否让 AI 回复联动表情 / 动作（害羞脸红、生气黑脸、比心挥手；关掉只说话）。</summary>
    public bool AiMoodEffects { get; set; } = true;

    /// <summary>聊天窗当前是否打开（关闭后记住；显示桌宠时按 <see cref="AiChatAutoOpen"/> 决定要不要自动打开）。</summary>
    public bool AiChatWindowOpen { get; set; }

    /// <summary>显示桌宠时自动打开聊天窗（默认开；不想每次都弹就关掉）。</summary>
    public bool AiChatAutoOpen { get; set; } = true;

    /// <summary>是否允许桌宠主动搭话（按 <see cref="AiProactiveIntervalMinutes"/> 随机间隔）。</summary>
    public bool AiProactiveEnabled { get; set; }

    /// <summary>主动搭话间隔（分钟，3 ~ 360，默认 20）。</summary>
    public double AiProactiveIntervalMinutes { get; set; } = 20;

    /// <summary>用户很久没回时，桌宠是否主动撒娇 / 求陪（按 <see cref="AiIdleSilenceMinutes"/> 判定）。</summary>
    public bool AiIdleReminderEnabled { get; set; } = true;

    /// <summary>「很久没回」的门限（分钟，1 ~ 720，默认 10）。</summary>
    public double AiIdleSilenceMinutes { get; set; } = 10;

    // ---------- 好感度（自然模式 / 兜底 / 涨掉速度） ----------

    /// <summary>小聊到一定程度会主动理你（自然模式）：聊得多涨、长时间不聊掉；关掉 = 纯手动拖数值。</summary>
    public bool AiAffectionNatural { get; set; } = true;

    /// <summary>自然模式下是否启用好感度兜底（最低不掉破设定值）。</summary>
    public bool AiAffectionFloorEnabled { get; set; } = true;

    /// <summary>兜底数值（0 ~ 100，默认 20）。</summary>
    public double AiAffectionFloor { get; set; } = 20;

    /// <summary>每聊一轮涨多少（0.2 ~ 3，默认 1.0；每天最多涨 15）。</summary>
    public double AiAffectionGainPerChat { get; set; } = 1.0;

    /// <summary>每「一个不聊周期」掉多少（0 ~ 20，默认 3）。</summary>
    public double AiAffectionDecayPerDay { get; set; } = 3.0;

    /// <summary>多久不聊算一个掉值周期（小时，1 ~ 240，默认 24 = 一天）。</summary>
    public double AiAffectionDecayAfterHours { get; set; } = 24;

    /// <summary>是否让模型给出的好感度变化（-3 ~ +3）参与自然模式涨跌。</summary>
    public bool AiAffectionModelDelta { get; set; } = true;
}
