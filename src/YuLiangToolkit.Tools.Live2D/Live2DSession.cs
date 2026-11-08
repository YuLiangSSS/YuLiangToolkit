using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YuLiangToolkit.Tools.Live2D.Ai;
using YuLiangToolkit.Tools.Live2D.Audio;
using YuLiangToolkit.Tools.Live2D.Play;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Mocap;
using YuLiangToolkit.Tools.Live2D.Model;
using YuLiangToolkit.Tools.Live2D.Rendering;
using YuLiangToolkit.Tools.Live2D.Speech;
using YuLiangToolkit.Tools.Live2D.ViewModels;
using YuLiangToolkit.Tools.Live2D.Views;

namespace YuLiangToolkit.Tools.Live2D;

/// <summary>
/// 页面 / 桌宠共用的一帧渲染目标：会话每帧先 <see cref="ModelRuntime.Tick"/> 一次，再依次叫它们各自出图。
///
/// <para>为什么要有这个接口：工具页和桌宠**同时可见**时都要拿到自己尺寸的画面。
/// 如果两边都往同一个 <c>renderer.Bitmap</c> 画，位图尺寸会被对方改来改去（还互相覆盖内容）。
/// 所以会话只 Tick 一次（动作速度才不会翻倍），两个目标各自用自己的 <see cref="CubismRenderer"/>
/// （共享同一份只读纹理）分别渲染。</para>
/// </summary>
public interface ILive2DFrameTarget
{
    /// <summary>这个目标现在值不值得渲染（页面切走 / 窗口最小化时返回 false，省 CPU）。</summary>
    bool IsTargetVisible { get; }

    /// <summary>用运行时当前这一帧画自己的位图（在 UI 线程、<see cref="ModelRuntime.Tick"/> 之后调用）。</summary>
    void RenderFrame(ModelRuntime runtime);
}

/// <summary>
/// 「Live2D 桌宠」的后台会话（整个进程一个）：管理模型运行时、唯一的帧循环、工具页与桌宠窗口的存活。
///
/// <para>与 KPS 的 Overlay 同一套思路：模型加载慢（5 张 4096 纹理）、桌宠窗口要在切到别的工具页之后
/// 继续显示，所以它们放在会话里；工具页只是「控制面板」，页面关掉时若桌宠还开着，会话继续跑。</para>
///
/// <para><b>帧循环</b>：会话持有一个 <see cref="DispatcherTimer"/>：每帧 = Tick 一次 → 页面目标渲染 → 桌宠目标渲染。
/// 页面和桌宠同时可见时桌宠降到约 15fps（页面仍按 <see cref="Live2DConfig.FpsLimit"/>），避免两张大图
/// 一起抢 UI 线程。页面切走 / 窗口最小化且桌宠也没开时定时器停下；两边都关掉后再过一会儿释放模型内存。</para>
///
/// <para><b>线程</b>：公开方法都应在 UI 线程调用；模型加载在后台线程（<see cref="Task.Run(Action)"/>），
/// 加载完成 / 失败都切回 UI 线程再改界面状态。开发调试截图模式（环境变量 <c>YLT_SHOT</c> /
/// <c>YLT_LIVE2D_SHOT_PET</c>）下改成 UI 线程同步加载，保证外壳 1200ms 后截图时画面已经出来。</para>
/// </summary>
public sealed class Live2DSession
{
    /// <summary>全局唯一会话。</summary>
    public static Live2DSession Current { get; } = new();

    /// <summary>两边都没人看着时，模型保留多久再释放内存（毫秒）。</summary>
    private const int IdleUnloadDelayMs = 45_000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Live2DViewModel? _viewModel;
    private ModelRuntime? _runtime;
    private CubismRenderer? _petRenderer;
    private PetWindow? _pet;
    private ILive2DFrameTarget? _pageTarget;

    private DispatcherTimer? _frameTimer;
    private DispatcherTimer? _idleUnloadTimer;
    private TimeSpan _lastFrameTime;
    private long _frameIndex;

    private Task? _loadTask;
    private CancellationTokenSource? _loadCts;
    private double _lastReportedProgress = -1;
    private bool _loading;
    private double _loadProgress;
    private string? _loadError;
    private double _lastLoadMilliseconds;

    private bool _pageActive;
    private bool _initialized;
    private bool _shutdown;
    private bool _windowCloseHooked;
    private bool _frameErrorLogged;

    private DateTime _lastPageEyeTime = DateTime.MinValue;

    // 帧耗时测量：页面 / 桌宠各自上一帧的渲染毫秒数（页面与桌宠同开太吃力时给桌宠降频）
    private double _lastPageRenderMs;
    private double _lastPetRenderMs;
    private bool _dualSlowLogged;

    // ===== 摄像头面部动捕（数据源生命周期归会话管：页面 / 桌宠可见才开，隐私 + 省 CPU） =====
    private readonly SemaphoreSlim _mocapGate = new(1, 1);
    private MocapCameraSource? _mocap;
    private MocapSettings? _mocapAppliedSettings;
    private string? _mocapStartError;
    private string? _lastLoggedMocapError;
    private int _mocapRevision;
    private bool _mocapSelfTestStarted;

    // ===== 身体晃动演示钩子（YLT_LIVE2D_SWAY_DEMO） =====
    private bool _swayDemoApplied;

    // ===== 面部测试 / 鼠标跟随演示钩子（YLT_LIVE2D_FACE_DEMO / YLT_LIVE2D_EYE_DEMO，截图验收用） =====
    private bool _faceDemoApplied;
    private bool _eyeDemoApplied;

    // ===== 音乐跟唱 · 律动（WASAPI 回环采集系统正在播放的声音；不申请麦克风、不录音、不保存） =====
    private readonly SemaphoreSlim _musicGate = new(1, 1);
    private MusicReactiveSource? _music;
    private string? _musicStartError;
    private string? _lastLoggedMusicError;
    private int _musicRevision;
    private bool _musicSelfTestStarted;

    // ===== 全身轻晃（以脚为支点渲染层倾斜）演示钩子 =====
    private bool _bodyLeanDemoApplied;

    // ===== 动作演示（手势 / 表演 / 舞蹈）与说话 / 聊天气泡（Windows 自带 TTS） =====
    private TtsService? _tts;    private SpeechBubbleWindow? _bubble;
    private CancellationTokenSource? _bubbleHideCts;
    private string? _lastLoggedSpeechError;
    private TimeSpan _idleChatterNextAt = TimeSpan.MinValue;
    private bool _speechSelfTestStarted;
    private bool _actionDemoApplied;
    private bool _bubbleShotApplied;
    private string? _bubbleShotPath;

    // ===== 桌宠鼠标互动（热区事件 → 软妹台词语音，1.5 秒节流） =====
    private bool _interactionDemoApplied;
    private TimeSpan _lastInteractionVoiceAt = TimeSpan.MinValue;
    private TimeSpan _lastInteractionVisualAt = TimeSpan.MinValue;
    private string? _lastInteractionVoiceLine;
    private string? _lastInteractionVoiceEvent;

    // ===== AI 聊天 · 好感度（DeepSeek；聊天窗 + 语音 / 气泡 / 表情 / 动作联动 + 主动说话调度） =====
    private AiChatService? _aiChat;
    private AiChatWindow? _aiChatWindow;
    private DispatcherTimer? _aiTickTimer;
    private bool _aiChatShotApplied;
    private string? _aiChatShotPath;
    private TimeSpan _aiExpressionClearAt = TimeSpan.MinValue;
    private readonly Random _aiMoodRandom = new();
    private string? _lastAiError;

    // ===== 养成：饱食度 / 金币 / 每日任务 / 投喂（饭团物理层 + 吃饭动作） =====
    private PetCareService? _care;
    private PetFoodWindow? _foodWindow;
    private TimeSpan _nextHungryReminderAt = TimeSpan.MinValue;
    private bool _hungryReminderScheduled;
    private string? _lastCareNotice;
    private TimeSpan _lastFeedTraceAt = TimeSpan.MinValue;
    private bool _feedShotApplied;
    private long _lastInteractionEventCount;
    private bool _ttsAuditionApplied;

    private Live2DSession()
    {
    }

    // ===================== 对外状态 =====================

    /// <summary>工具页 ViewModel（没创建过为 null）。</summary>
    public Live2DViewModel? ViewModel => _viewModel;

    /// <summary>当前模型运行时（没加载 / 已释放为 null）。</summary>
    public ModelRuntime? Runtime => _runtime;

    /// <summary>桌宠专用的渲染器（与页面渲染器共享纹理；没加载 / 创建失败为 null）。</summary>
    public CubismRenderer? PetRenderer => _petRenderer;

    /// <summary>模型是否加载完成且可用。</summary>
    public bool IsLoaded => _runtime?.IsLoaded == true;

    /// <summary>是否正在加载模型。</summary>
    public bool IsLoading => _loading;

    /// <summary>加载进度 0~1。</summary>
    public double LoadProgress => _loadProgress;

    /// <summary>加载失败原因（成功为 null）。</summary>
    public string? LoadError => _loadError;

    /// <summary>最近一次加载耗时（毫秒，给界面的信息卡显示）。</summary>
    public double LastLoadMilliseconds => _lastLoadMilliseconds;

    /// <summary>工具页当前是否在前台（页面打开着）。</summary>
    public bool IsPageActive => _pageActive;

    /// <summary>桌宠窗口当前是否显示着。</summary>
    public bool IsPetVisible => _pet is not null;

    /// <summary>
    /// 是不是「开发调试截图模式」（设置了 <c>YLT_SHOT</c> 或 <c>YLT_LIVE2D_SHOT_PET</c>）：
    /// 这种模式下模型在 UI 线程同步加载、纹理固定 2048，保证外壳 1200ms 后截图时画面已经出来。
    /// </summary>
    public bool IsDebugShotMode =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_SHOT"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_LIVE2D_SHOT_PET"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_LIVE2D_SHOT_CHAT"));

    /// <summary>桌宠截图钩子路径（环境变量 <c>YLT_LIVE2D_SHOT_PET</c>；没设置为空串）。</summary>
    public string PetDebugShotPath =>
        Environment.GetEnvironmentVariable("YLT_LIVE2D_SHOT_PET")?.Trim() ?? "";

    /// <summary>
    /// 画布宽高比（宽 / 高）。模型没加载时用内置模型的 3749 / 7697，保证桌宠窗口先有个合理的长宽比。
    /// </summary>
    public double CanvasAspect
    {
        get
        {
            var model = _runtime?.Model;
            try
            {
                if (model is not null && model.CanvasWidthPixels > 0 && model.CanvasHeightPixels > 0)
                    return model.CanvasWidthPixels / model.CanvasHeightPixels;
            }
            catch { /* 运行时没准备好时用默认比例 */ }
            return 3749.0 / 7697.0;
        }
    }

    /// <summary>加载状态 / 模型 / 桌宠显隐变化时触发（在 UI 线程）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>桌宠显示 / 隐藏时触发（在 UI 线程）。</summary>
    public event EventHandler? PetVisibilityChanged;

    // ===================== 动捕 / 身体晃动 对外状态 =====================

    /// <summary>当前动捕数据源（没创建为 null；UI 读 <c>Latest</c> / <c>LatestPreview</c> / <c>MeasuredFps</c>）。</summary>
    public MocapCameraSource? MocapSource => _mocap;

    /// <summary>动捕最近一次启动错误（空 = 没错误）。</summary>
    public string MocapStartError => _mocapStartError ?? "";

    /// <summary>动捕数据源是否正在采集。</summary>
    public bool IsMocapRunning => _mocap?.IsRunning == true;

    /// <summary>动捕数据源状态变化时触发（启动 / 停止 / 出错；可能在后台线程）。</summary>
    public event EventHandler? MocapStateChanged;

    /// <summary>身体晃动演示（「摇一摇」）还剩多少秒（0 = 没在演示）。</summary>
    public double SwayDemoRemainingSeconds
    {
        get
        {
            try { return _runtime?.SwayDemoRemainingSeconds ?? 0; } catch { return 0; }
        }
    }

    /// <summary>页面或桌宠现在是否有人看着（决定要不要开摄像头）。</summary>
    private bool IsPageOrPetVisible => _pageTarget is { IsTargetVisible: true } || _pet?.IsVisible == true;

    // ===================== 音乐跟唱 · 律动 对外状态 =====================

    /// <summary>当前音乐回环数据源（没启用为 null；UI 读 <c>Latest</c> / SMTC 媒体信息）。</summary>
    public MusicReactiveSource? MusicSource => _music;

    /// <summary>音乐采集最近一次启动错误（空 = 没错误）。</summary>
    public string MusicStartError => _musicStartError ?? "";

    /// <summary>音乐回环采集是否正在运行。</summary>
    public bool IsMusicRunning => _music?.IsRunning == true;

    /// <summary>音乐采集状态变化时触发（启动 / 停止 / 出错 / 换歌；可能在后台线程）。</summary>
    public event EventHandler? MusicStateChanged;

    // ===================== 说话 / 轻对话 对外状态（Windows 自带 TTS） =====================

    /// <summary>现在是否在说话（帧循环会把它同步给 <c>runtime.SpeechActive</c>）。</summary>
    public bool IsSpeaking => _tts?.IsSpeaking == true;

    /// <summary>说话时的嘴型开合（0~1，随播放进度；帧循环同步给 <c>runtime.SpeechMouthOpen</c>）。</summary>
    public double SpeechMouthOpen
    {
        get
        {
            try { return _tts?.MouthOpen ?? 0; }
            catch { return 0; }
        }
    }

    /// <summary>语音最近一次错误（空 = 没错误）。</summary>
    public string SpeechLastError => _tts?.LastError ?? "";

    /// <summary>最近一次说的话（空 = 还没说过）。</summary>
    public string LastSpokenText => _tts?.LastSpokenText ?? "";

    /// <summary>语音状态变化（开始 / 结束 / 出错；在 UI 线程触发）。</summary>
    public event EventHandler? SpeechStateChanged;

    // ===================== AI 聊天 · 好感度 对外状态（DeepSeek） =====================

    /// <summary>AI 聊天服务（没创建过为 null；聊天窗 / 测试页 AI 设置卡共用）。</summary>
    public AiChatService? AiChat => _aiChat;

    /// <summary>聊天窗当前是否显示着。</summary>
    public bool IsAiChatWindowVisible => _aiChatWindow is { IsChatVisible: true };

    /// <summary>今天累计消耗的 token（估成本用；界面显示）。</summary>
    public long AiTodayTokens => _aiChat?.TodayTokens ?? 0;

    /// <summary>AI 聊天的一句话状态（设置卡 / 聊天窗底部显示）。</summary>
    public string AiStatusSummaryText
    {
        get
        {
            var cfg = _viewModel?.Cfg;
            if (cfg is null) return "AI 聊天：未初始化";
            var online = !string.IsNullOrWhiteSpace(GetAiApiKey());
            var thinking = AiThinkingLevels.ToConfig(AiThinkingLevels.FromConfig(cfg.AiThinkingLevel));
            var value = _aiChat?.Affection.Value ?? 50;
            var mood = _aiChat?.Affection.MoodText ?? "平常";
            return $"{(online ? "DeepSeek 在线" : "离线兜底（未填 API Key）")} · {cfg.AiModel} · 思考{thinking} · " +
                   $"好感度 {value:0.0}（{mood}）";
        }
    }

    /// <summary>AI 聊天状态变化（配置 / 好感度 / 忙碌状态；在 UI 线程触发）。</summary>
    public event EventHandler? AiChatStateChanged;

    /// <summary>打开聊天窗（没建就建；桌宠可见时贴桌宠下方 / 左侧跟随）。</summary>
    public void ShowAiChatWindow()
    {
        if (_shutdown) return;
        EnsureInitialized();
        HookWindowClose();

        var vm = EnsureViewModel();
        var chat = EnsureAiChat();

        if (_aiChatWindow is { IsChatVisible: true })
        {
            UpdateAiChatWindowPosition();
            return;
        }

        try
        {
            if (_aiChatWindow is null)
            {
                var window = new AiChatWindow(chat, vm.Cfg, EnsureCare(), SpawnRiceBall,
                    onConfigChanged: OnAiChatWindowConfigChanged,
                    openSettings: OpenToolPageFromPet);
                window.Closed += OnAiChatWindowClosed;
                window.FollowToggled += OnAiChatWindowFollowToggled;
                _aiChatWindow = window;
            }

            _aiChatWindow.ShowChat();
            _aiChatWindow.ApplySavedPositionIfManual();
            UpdateAiChatWindowPosition();
            TryApplyAiChatDebugPosition();
            vm.Cfg.AiChatWindowOpen = true;
            SaveConfigNow();
            UpdateAiTickTimer();
            _viewModel?.Log("AI 聊天窗已打开（在桌宠下方 / 旁边，跟着桌宠走）");
        }
        catch (Exception ex)
        {
            _aiChatWindow = null;
            _viewModel?.Log("打开 AI 聊天窗失败：" + ex.Message);
        }

        RaiseAiChatStateChanged();
    }

    /// <summary>隐藏聊天窗（不销毁，下次打开很快）。</summary>
    public void HideAiChatWindow()
    {
        if (_aiChatWindow is not null)
        {
            try { _aiChatWindow.HideChat(); } catch { /* 忽略 */ }
        }
        if (_viewModel is not null) _viewModel.Cfg.AiChatWindowOpen = false;
        SaveConfigNow();
        UpdateAiTickTimer();
        RaiseAiChatStateChanged();
    }

    /// <summary>显示 / 隐藏聊天窗。</summary>
    public void ToggleAiChatWindow()
    {
        if (IsAiChatWindowVisible) HideAiChatWindow();
        else ShowAiChatWindow();
    }

    /// <summary>
    /// 把配置里的 AI 设置（请求参数 / 人格 / 好感度规则 / 主动聊天）推给聊天服务。
    /// 设置卡上的滑杆 / 下拉改一下就应该调它（内部会重读配置并落盘）。
    /// </summary>
    public void ApplyAiSettings()
    {
        if (_shutdown) return;
        try
        {
            EnsureAiChat().ApplySettings();
        }
        catch (Exception ex)
        {
            _viewModel?.Log("应用 AI 设置失败：" + ex.Message);
        }
        UpdateAiTickTimer();
        RaiseAiChatStateChanged();
    }

    /// <summary>设置 DeepSeek API Key（空白 = 清除；落盘走 DPAPI 密文）。</summary>
    public void SetAiApiKey(string? plainKey)
    {
        var vm = EnsureViewModel();
        var plain = (plainKey ?? "").Trim();
        vm.Cfg.AiApiKeyProtected = plain.Length == 0 ? "" : AiSecretStore.Protect(plain);
        SaveConfigNow();
        if (_aiChat is not null)
        {
            _aiChat.ApplySettings();
            // 让界面上的「在线 / 离线」立刻刷新
            _aiChat.OnPropertyChanged(nameof(AiChatService.HasUsableApiKey));
        }
        vm.Log(plain.Length == 0 ? "已清除 DeepSeek API Key（聊天用离线兜底）" : "已保存 DeepSeek API Key（本机 DPAPI 加密）");
        RaiseAiChatStateChanged();
    }

    /// <summary>取回明文 API Key（解不开 / 没填返回空串）。</summary>
    public string GetAiApiKey()
    {
        try
        {
            return AiSecretStore.Unprotect(_viewModel?.Cfg.AiApiKeyProtected);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>手动设置好感度（用户自己拖，不走自然模式规则）。</summary>
    public void SetAiAffection(double value)
    {
        var chat = EnsureAiChat();
        chat.SetAffection(value);
        SaveConfigNow();
        RaiseAiChatStateChanged();
    }

    /// <summary>好感度重置为 50（默认初始值）。</summary>
    public void ResetAiAffection()
    {
        SetAiAffection(50);
        _viewModel?.Log("好感度已重置为 50");
    }

    /// <summary>清空聊天记录（好感度不动）。</summary>
    public void ClearAiConversation()
    {
        try
        {
            EnsureAiChat().ClearConversation();
            _viewModel?.Log("AI 聊天记录已清空");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("清空 AI 聊天记录失败：" + ex.Message);
        }
        RaiseAiChatStateChanged();
    }

    /// <summary>测试 API 连通性（设置卡「测试连接」按钮）。</summary>
    public async Task<AiConnectionTestResult> TestAiConnectionAsync()
    {
        try
        {
            var result = await EnsureAiChat().TestConnectionAsync();
            _viewModel?.Log("AI 连接测试：" + result.Message);
            return result;
        }
        catch (Exception ex)
        {
            var message = "测试连接失败：" + ex.Message;
            _viewModel?.Log(message);
            return new AiConnectionTestResult { Ok = false, Message = message };
        }
    }

    // ===================== 养成：饱食度 · 金币 · 每日任务 · 投喂 =====================

    /// <summary>养成服务（饱食度 / 金币 / 饭团库存 / 每日任务；第一次访问懒创建）。</summary>
    public PetCareService Care => EnsureCare();

    /// <summary>投喂层现在有没有饭团。</summary>
    public bool IsFoodLayerVisible => _foodWindow?.HasBalls == true;

    /// <summary>
    /// 把配置里的养成设置推给服务（自然模式 / 锁定 / 掉值速度 / 任务开关 / 饥饿提醒）。
    /// 设置卡改滑杆 / 勾选框后调它。
    /// </summary>
    public void ApplyCareSettings()
    {
        if (_shutdown) return;
        try
        {
            EnsureCare().ApplySettings();
        }
        catch (Exception ex)
        {
            _viewModel?.Log("应用养成设置失败：" + ex.Message);
        }
        try { _aiChatWindow?.RefreshCareHud(); } catch { /* 聊天窗没开：忽略 */ }
        UpdateAiTickTimer();
    }

    /// <summary>花金币买一个饭团（只买不扔；库存 +1）。金币不足 / 锁定 / 总开关关闭返回 false。</summary>
    public bool BuyRiceBall(RiceBallKind kind)
    {
        if (_shutdown) return false;
        var vm = EnsureViewModel();
        if (!vm.Cfg.PetCareEnabled)
        {
            ShowCareToast("养成系统已关闭（到「饥饿 · 金币 · 每日任务」卡里打开）", CareNoticeKind.Warning);
            return false;
        }

        var care = EnsureCare();
        if (!care.TryBuy(kind, out var error))
        {
            ShowCareToast(error, CareNoticeKind.Warning);
            return false;
        }

        _viewModel?.Log($"买了一个{RiceBallCatalog.Get(kind).Name}（剩 {care.Coins} 金币，库存 {care.GetInventory(kind)}）");
        RaiseAiChatStateChanged();
        return true;
    }

    /// <summary>
    /// 弄一个饭团丢到屏幕上（有库存就用库存，没有就自动买一个）。
    /// 桌宠没显示会自动显示；调用后由投喂层负责物理 / 拖拽 / 进嘴判定。
    /// </summary>
    public bool SpawnRiceBall(RiceBallKind kind)
    {
        if (_shutdown) return false;
        var vm = EnsureViewModel();
        if (!vm.Cfg.PetCareEnabled)
        {
            ShowCareToast("养成系统已关闭（到「饥饿 · 金币 · 每日任务」卡里打开）", CareNoticeKind.Warning);
            return false;
        }

        EnsureInitialized();
        HookWindowClose();
        var care = EnsureCare();

        // 先出库；没库存就买一个再出库
        string error;
        if (care.GetInventory(kind) > 0)
        {
            if (!care.TryConsumeInventory(kind, out error))
            {
                ShowCareToast(error, CareNoticeKind.Warning);
                return false;
            }
        }
        else
        {
            if (!care.TryBuy(kind, out error))
            {
                ShowCareToast(error, CareNoticeKind.Warning);
                return false;
            }
            if (!care.TryConsumeInventory(kind, out error))
            {
                ShowCareToast(error, CareNoticeKind.Warning);
                return false;
            }
        }

        try
        {
            if (!IsPetVisible) ShowPet();
            var window = EnsureFoodWindow();
            window.SpawnRiceBall(kind, ComputeFoodSpawnPoint());
            vm.Log($"丢出一个{RiceBallCatalog.Get(kind).Name}：拖住它甩给大肥鱼，掉进嘴里才算吃掉");
            RaiseAiChatStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            _viewModel?.Log("丢饭团失败：" + ex.Message);
            ShowCareToast("丢饭团失败：" + ex.Message, CareNoticeKind.Warning);
            return false;
        }
    }

    /// <summary>清掉投喂层上的所有饭团（隐藏桌宠 / 退出时用）。</summary>
    public void ClearFoodLayer()
    {
        try { _foodWindow?.DisposeBalls(); } catch { /* 忽略 */ }
        try { _runtime?.ClearFeedFocus(); } catch { /* 忽略 */ }
    }

    // ---------- 养成：内部实现 ----------

    /// <summary>懒创建养成服务（配置回调现读）。</summary>
    private PetCareService EnsureCare()
    {
        if (_care is not null) return _care;

        var vm = EnsureViewModel();
        var service = new PetCareService(new PetCareServiceOptions
        {
            Storage = vm.Storage,
            Settings = BuildCareSettings,
            Log = message => _viewModel?.Log(message),
            Notice = OnCareNotice,
        });
        service.StateChanged += OnCareStateChanged;
        _care = service;
        try { service.ApplySettings(); }
        catch (Exception ex) { _viewModel?.Log("养成服务初始化失败：" + ex.Message); }
        UpdateAiTickTimer();
        return service;
    }

    /// <summary>当前配置 → 养成设置（数值夹到合理区间）。</summary>
    private PetCareSettings BuildCareSettings()
    {
        var cfg = _viewModel?.Cfg;
        return new PetCareSettings
        {
            Enabled = cfg?.PetCareEnabled ?? true,
            FullnessNatural = cfg?.PetFullnessNatural ?? true,
            FullnessLocked = cfg?.PetFullnessLocked ?? false,
            FullnessDecayPerHour = Math.Clamp(cfg?.PetFullnessDecayPerHour ?? 4, 0, 20),
            FullnessInitial = 70,
            CoinsNatural = cfg?.PetCoinsNatural ?? true,
            CoinsLocked = cfg?.PetCoinsLocked ?? false,
            CoinsInitial = 30,
            DailyTasksEnabled = cfg?.PetDailyTasksEnabled ?? true,
            HungryReminderEnabled = cfg?.PetHungryReminderEnabled ?? true,
            HungryThreshold = Math.Clamp(cfg?.PetHungryThreshold ?? 25, 0, 100),
            HungryReminderMinutes = Math.Clamp(cfg?.PetHungryReminderMinutes ?? 8, 1, 240),
        };
    }

    private void OnCareStateChanged(object? sender, EventArgs e)
    {
        // 金币 / 饱食度变化时聊天窗的养成 HUD 也要刷新：统一转发一次 AI 状态变化
        RaiseAiChatStateChanged();
    }

    /// <summary>养成提示（任务完成 / 金币不足）：写日志 + 聊天窗灰色系统提示。</summary>
    private void OnCareNotice(CareNoticeEventArgs e)
    {
        if (e is null || string.IsNullOrWhiteSpace(e.Text)) return;
        if (string.Equals(e.Text, _lastCareNotice, StringComparison.Ordinal)) return;
        _lastCareNotice = e.Text;
        _viewModel?.Log("养成：" + e.Text);
        try
        {
            if (e.Kind is CareNoticeKind.Reward or CareNoticeKind.Warning)
                EnsureAiChat().AppendNotice(e.Text);
        }
        catch { /* 聊天服务没准备好：只记日志 */ }
        RaiseAiChatStateChanged();
    }

    /// <summary>没有聊天气泡时也要能看到提示：桌面右下角没有 toast 系统，这里用状态栏 + 日志即可（保留占位）。</summary>
    private void ShowCareToast(string message, CareNoticeKind kind)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _viewModel?.Log("养成：" + message);
        _viewModel?.SetStatus(message);
    }

    /// <summary>投喂层窗口（第一次丢饭团时创建；坐标 / 嘴部点都是屏幕 DIP）。</summary>
    private PetFoodWindow EnsureFoodWindow()
    {
        if (_foodWindow is not null) return _foodWindow;

        var window = new PetFoodWindow(new PetFoodWindowOptions
        {
            GetMouthScreenPoint = GetPetMouthScreenPoint,
            GetPetScreenRect = () => _pet?.GetScreenRect(),
            OnEaten = OnRiceBallEaten,
            OnFeedingFocus = OnFeedingFocus,
            Log = message =>
            {
                _viewModel?.Log("投喂层：" + message);
                if (Environment.GetEnvironmentVariable("YLT_LIVE2D_FEED_TRACE") is "1" or "on" or "true")
                    WriteFeedTrace("投喂层：" + message);
            },
        });
        window.Closed += (_, _) => _foodWindow = null;
        _foodWindow = window;
        return window;
    }

    /// <summary>嘴部中心的屏幕坐标（桌宠不可见 / 模型没加载 / 拿不到脸 = null）。</summary>
    private Point? GetPetMouthScreenPoint()
    {
        var pet = _pet;
        var runtime = _runtime;
        if (pet is not { IsVisible: true } || runtime is not { IsLoaded: true })
        {
            TraceFeed($"嘴部不可用：pet={pet is { IsVisible: true }}，runtimeLoaded={runtime is { IsLoaded: true }}");
            return null;
        }
        if (!runtime.TryGetMouthCanvasCenter(out var canvasX, out var canvasY))
        {
            TraceFeed("嘴部不可用：TryGetMouthCanvasCenter=false（脸部件包围盒没拿到？）");
            return null;
        }
        if (!pet.TryCanvasToScreen(canvasX, canvasY, out var point))
        {
            TraceFeed("嘴部不可用：PetWindow.TryCanvasToScreen=false（模型/窗口尺寸没准备好）");
            return null;
        }

        // 调试钩子：YLT_LIVE2D_FEED_TRACE=1 时每秒打一行嘴部坐标（投喂定位排错用）
        if (Environment.GetEnvironmentVariable("YLT_LIVE2D_FEED_TRACE") is "1" or "on" or "true")
        {
            var now = _clock.Elapsed;
            if (_lastFeedTraceAt == TimeSpan.MinValue || now - _lastFeedTraceAt > TimeSpan.FromSeconds(1))
            {
                _lastFeedTraceAt = now;
                var petRect = pet.GetScreenRect();
                var traceLine =
                    $"投喂定位：嘴部画布 ({canvasX:0},{canvasY:0}) → 屏幕 ({point.X:0},{point.Y:0})；" +
                    $"桌宠窗口 {petRect?.Left:0},{petRect?.Top:0} {petRect?.Width:0}×{petRect?.Height:0}" +
                    $"；嘴半径（画布）{runtime.MouthCanvasRadius:0}";
                _viewModel?.Log(traceLine);
                WriteFeedTrace(traceLine);
            }
        }

        return point;
    }

    /// <summary>调试钩子：把一行投喂定位信息写进 Data\live2d\feed-trace.log（只在 YLT_LIVE2D_FEED_TRACE 打开时写）。</summary>
    private void TraceFeed(string line)
    {
        if (Environment.GetEnvironmentVariable("YLT_LIVE2D_FEED_TRACE") is not ("1" or "on" or "true")) return;
        var now = _clock.Elapsed;
        if (_lastFeedTraceAt != TimeSpan.MinValue && now - _lastFeedTraceAt <= TimeSpan.FromSeconds(1)) return;
        _lastFeedTraceAt = now;
        WriteFeedTrace(line);
    }

    private void WriteFeedTrace(string line)
    {
        try
        {
            var path = _viewModel?.Storage.PathOf("feed-trace.log");
            if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { /* 调试钩子失败不影响投喂 */ }
    }

    /// <summary>饭团默认出现位置：桌宠左侧、贴地放（静止好抓；拖动甩出去后才有重力弹跳）。</summary>
    private Point ComputeFoodSpawnPoint()
    {
        var area = SystemParameters.WorkArea;
        var pet = _pet;
        double x;
        if (pet is { IsVisible: true } && pet.ActualWidth > 2)
        {
            x = pet.Left - 150;
        }
        else
        {
            x = area.Left + (area.Width * 0.5);
        }

        x = Math.Clamp(x, area.Left + 60, area.Right - 60);
        var y = Math.Clamp(area.Bottom - 40, area.Top + 60, area.Bottom - 10);
        return new Point(x, y);
    }

    /// <summary>投喂层每帧推来的「饭团靠近」状态：转给模型做凑近 / 张嘴。</summary>
    private void OnFeedingFocus(float normalizedX, float normalizedY, float strength, float mouthOpen)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;
        if (strength <= 0.01f && mouthOpen <= 0.01f)
        {
            runtime.ClearFeedFocus();
        }
        else
        {
            runtime.SetFeedFocus(normalizedX, normalizedY, strength, mouthOpen);
        }
    }

    /// <summary>饭团真的掉进嘴里了：饱食度 / 好感度 / 每日任务 + 嚼嚼嚼 → 吞咽 → 说一句。</summary>
    private void OnRiceBallEaten(RiceBallKind kind, Point screenPoint)
    {
        try
        {
            var care = EnsureCare();
            var result = care.ApplyEat(kind, DateTimeOffset.Now);
            _viewModel?.Log(
                $"大肥鱼吃掉了{RiceBallCatalog.Get(kind).Name}：饱食度 {result.FullnessAfter:0}（{FeedingLines.TierText(result.Tier)}），" +
                $"好感度 +{result.AffectionGain:0.#}" +
                (result.CoinsRewarded > 0 ? $"、任务奖励 +{result.CoinsRewarded} 金币" : ""));

            if (result.AffectionGain > 0)
            {
                try { EnsureAiChat().AddAffection(result.AffectionGain); }
                catch (Exception ex) { _viewModel?.Log("投喂加好感度失败：" + ex.Message); }
            }

            var runtime = _runtime;
            if (runtime is { IsLoaded: true })
            {
                runtime.ClearFeedFocus();
                runtime.StartEating(1.5, 0.65);
            }

            // 好感度 / 饱食度变化完记得让界面刷新（Care 自己也会发 StateChanged）
            RaiseAiChatStateChanged();
            _ = AnnounceAfterEatingAsync(result);
        }
        catch (Exception ex)
        {
            _viewModel?.Log("处理投喂失败：" + ex.Message);
        }
    }

    /// <summary>等嚼完 + 咽下去，再按「吃饱 / 没吃饱 / 很饿」说一句（语音 + 气泡 + 聊天记录 + 表情）。</summary>
    private async Task AnnounceAfterEatingAsync(FeedResult result)
    {
        try
        {
            await Task.Delay(2200);   // 1.5s 咀嚼 + 0.65s 吞咽，稍微多等一点
            var address = BuildAiPersonaSettings().ResolvedAddress;
            var line = FeedingLines.PickAfterEat(result.Tier, address);
            var puff = result.Tier switch
            {
                FullnessTier.Full => 0f,
                FullnessTier.Normal => 0.15f,
                FullnessTier.Hungry => 0.3f,
                _ => 0.4f,
            };
            ApplyCareExpression(FeedingLines.GetExpressionKeyword(result.Tier, afterEat: true), 7, puff);
            try { EnsureAiChat().AppendLocalPetMessage(line); } catch { /* 聊天服务没准备好就只说话 */ }
            SpeakText(line, result.Tier switch
            {
                FullnessTier.Full => SpeechToneCatalog.ForFeedSatisfied(),
                FullnessTier.Starving => SpeechToneCatalog.ForFeedStarving(),
                _ => SpeechToneCatalog.ForFeedStillHungry(),
            }, allowAutoGesture: false);
            _viewModel?.Log("投喂台词：" + line);
        }
        catch (Exception ex)
        {
            _viewModel?.Log("投喂台词播放失败：" + ex.Message);
        }
    }

    /// <summary>饿的时候主动说话（由 2 秒调度检查；带难受表情 + 鼓嘴）。</summary>
    private void UpdateHungryReminder()
    {
        var vm = _viewModel;
        var care = _care;
        if (vm is null || care is null) return;
        var cfg = vm.Cfg;
        if (!cfg.PetCareEnabled || !cfg.PetHungryReminderEnabled) return;
        if (!IsPageOrPetVisible || IsSpeaking) return;

        var threshold = Math.Clamp(cfg.PetHungryThreshold, 0, 100);
        if (care.Fullness > threshold)
        {
            _hungryReminderScheduled = false;
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(cfg.PetHungryReminderMinutes, 1, 240));
        if (!_hungryReminderScheduled)
        {
            // 第一次只排期：避免刚启动 / 刚变饿就连环喊
            _hungryReminderScheduled = true;
            _nextHungryReminderAt = _clock.Elapsed + interval;
            return;
        }
        if (_clock.Elapsed < _nextHungryReminderAt) return;
        _nextHungryReminderAt = _clock.Elapsed + interval;

        var address = BuildAiPersonaSettings().ResolvedAddress;
        var line = FeedingLines.PickHungryReminder(address);
        var tier = care.Tier;
        ApplyCareExpression(tier == FullnessTier.Starving ? "流冷汗" : "", 5, 0.35f);
        try { EnsureAiChat().AppendLocalPetMessage(line); } catch { /* 忽略 */ }
        SpeakText(line, SpeechToneCatalog.ForHungryReminder(), allowAutoGesture: false);
        vm.Log($"饿了主动说话（饱食 {care.Fullness:0} / {care.TierText}）：{line}");
        RaiseAiChatStateChanged();
    }

    /// <summary>按表情文件名关键词换表情（找不到就跳过），并设置临时收回时间 + 鼓嘴。</summary>
    private void ApplyCareExpression(string keyword, double seconds, float puff)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;

        try { runtime.MoodPuffAmount = Math.Clamp(puff, 0, 1); } catch { /* 忽略 */ }
        if (string.IsNullOrWhiteSpace(keyword)) return;

        var expression = runtime.Expressions.FirstOrDefault(entry =>
            entry.Name.Contains(keyword, StringComparison.Ordinal));
        if (expression is null) return;
        ApplyExpression(expression, 0.35);
        _aiExpressionClearAt = _clock.Elapsed + TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30));
    }

    /// <summary>程序退出：停投喂层、落盘养成状态。</summary>
    private void DisposeCareBlocking()
    {
        ClearFoodLayer();
        try
        {
            if (_foodWindow is not null)
            {
                _foodWindow.ClearAll();
                _foodWindow.Close();
            }
        }
        catch { /* 退出流程忽略 */ }
        _foodWindow = null;

        try
        {
            if (_care is not null)
            {
                _care.StateChanged -= OnCareStateChanged;
                _care.Dispose();
            }
        }
        catch { /* 退出流程忽略 */ }
        _care = null;
    }

    // ---------- 截图冒烟：YLT_LIVE2D_SHOT_FEED=<png>（桌宠 + 聊天窗 + 投喂层拼成一张图） ----------

    private void TryBeginFeedShot()
    {
        if (_feedShotApplied) return;
        var path = Environment.GetEnvironmentVariable("YLT_LIVE2D_SHOT_FEED")?.Trim();
        if (string.IsNullOrEmpty(path)) return;
        if (_viewModel is null) return;
        _feedShotApplied = true;
        _ = RunFeedShotAsync(path);
    }

    private async Task RunFeedShotAsync(string path)
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;
        try
        {
            ShowPet();
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 20_000 && !IsLoaded) await Task.Delay(100);
            await Task.Delay(600);
            if (!IsLoaded) { viewModel.Log("投喂截图：模型没加载出来"); return; }

            // 截图用的人造数值（只影响这次临时数据目录）
            var care = EnsureCare();
            care.SetFullness(62);
            care.SetCoins(46);

            ShowAiChatWindow();
            if (_aiChatWindow is null) { viewModel.Log("投喂截图：聊天窗没打开"); return; }
            var pet = _pet;
            if (pet is null) { viewModel.Log("投喂截图：桌宠没打开"); return; }
            await Task.Delay(600);
            UpdateAiChatWindowPosition();

            // 摆两个饭团：一个贴地、一个停在空中（都放在聊天窗左边，截图不挡聊天内容）
            var area = SystemParameters.WorkArea;
            var food = EnsureFoodWindow();
            var chatRight = _aiChatWindow.Left;
            var ballX = Math.Clamp(chatRight - 90, area.Left + 60, area.Right - 60);
            var floorPoint = new Point(ballX, area.Bottom - 50);
            food.SpawnRiceBall(RiceBallKind.Triangle, floorPoint);
            var airPoint = new Point(
                Math.Clamp(ballX - 80, area.Left + 60, area.Right - 60),
                Math.Clamp(area.Bottom - 50, area.Top + 60, area.Bottom - 10));
            food.SpawnRiceBall(RiceBallKind.Sphere, airPoint);

            // 让桌宠朝贴地的那个饭团凑过去张嘴（截图更生动）
            if (pet.GetScreenRect() is { } petRect && _runtime is { IsLoaded: true })
            {
                var nx = (floorPoint.X - (petRect.Left + (petRect.Width * 0.5))) / (petRect.Width * 0.5);
                var ny = (floorPoint.Y - (petRect.Top + (petRect.Height * 0.5))) / (petRect.Height * 0.5);
                _runtime.SetFeedFocus((float)Math.Clamp(nx, -1, 1), (float)Math.Clamp(ny, -1, 1), 0.9f, 0.55f);
            }

            await Task.Delay(700);
            UpdateAiChatWindowPosition();

            var chatRoot = _aiChatWindow.Content as FrameworkElement;
            var foodRoot = food.Content as FrameworkElement;
            if (chatRoot is null || chatRoot.ActualWidth < 2 || chatRoot.ActualHeight < 2)
            {
                viewModel.Log("投喂截图：聊天窗还没布局完");
                return;
            }

            var petBitmap = pet.RenderWindowBitmap();
            var petScreen = pet.GetScreenRect() ?? new Rect(pet.Left, pet.Top, pet.ActualWidth, pet.ActualHeight);
            var chatRect = new Rect(_aiChatWindow.Left, _aiChatWindow.Top, chatRoot.ActualWidth, chatRoot.ActualHeight);
            var union = Rect.Union(new Rect(petScreen.Left, petScreen.Top, petScreen.Width, petScreen.Height), chatRect);
            // 两个饭团在聊天窗左边：把它们的区域也算进拼图，别裁掉
            union = Rect.Union(union, new Rect(floorPoint.X - 70, floorPoint.Y - 70, 140, 140));
            union = Rect.Union(union, new Rect(airPoint.X - 70, airPoint.Y - 70, 140, 140));
            union.Inflate(24, 24);
            union.Intersect(new Rect(area.Left, area.Top, area.Width, area.Height));
            if (union.Width < 2 || union.Height < 2) { viewModel.Log("投喂截图：拼图区域太小"); return; }

            var width = (int)Math.Ceiling(union.Width);
            var height = (int)Math.Ceiling(union.Height);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24)), null, new Rect(0, 0, width, height));
                // 桌宠
                dc.DrawImage(petBitmap, new Rect(
                    petScreen.Left - union.Left, petScreen.Top - union.Top, petScreen.Width, petScreen.Height));
                // 聊天窗
                dc.DrawRectangle(new VisualBrush(chatRoot), null, new Rect(
                    chatRect.Left - union.Left, chatRect.Top - union.Top, chatRect.Width, chatRect.Height));
                // 投喂层（透明底，只有饭团 / 米粒）
                if (foodRoot is not null && foodRoot.ActualWidth > 2)
                {
                    dc.DrawRectangle(new VisualBrush(foodRoot), null, new Rect(
                        food.Left - union.Left, food.Top - union.Top, foodRoot.ActualWidth, foodRoot.ActualHeight));
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
            viewModel.Log($"投喂截图已保存：{path}（{width}×{height}）");
        }
        catch (Exception ex)
        {
            viewModel.Log("投喂截图失败：" + ex.Message);
        }
        finally
        {
            await Task.Delay(200);
            Application.Current?.Shutdown();
        }
    }

    // ---------- 语音试听对比：YLT_LIVE2D_TTS_AUDITION=<folder|1> ----------
    //
    // 用法：设 YLT_LIVE2D_TTS_AUDITION=1（或给一个文件夹）后启动程序（配合 YLT_SHOT_TOOL=live2d 打开本页），
    // 会把「4 个音色 × 6 条台词」的语气化对比 MP3 合成到 Data\live2d\voice-auditions（或指定目录），
    // 并写一个 index.md（Markdown 表格）+ audition.m3u 播放列表。
    // 加 YLT_LIVE2D_TTS_AUDITION_EXIT=1 时生成完自动退出（自动化验收用）。

    private void TryBeginTtsAudition()
    {
        if (_ttsAuditionApplied) return;
        var spec = Environment.GetEnvironmentVariable("YLT_LIVE2D_TTS_AUDITION")?.Trim();
        if (string.IsNullOrWhiteSpace(spec)) return;
        if (_viewModel is null) return;
        _ttsAuditionApplied = true;

        string folder;
        if (spec is "1" or "on" or "true")
        {
            folder = Path.Combine(AppContext.BaseDirectory, "Data", "live2d", "voice-auditions");
        }
        else
        {
            folder = Path.IsPathRooted(spec) ? spec : Path.Combine(AppContext.BaseDirectory, spec);
        }

        _ = RunTtsAuditionAsync(folder);
    }

    private async Task RunTtsAuditionAsync(string folder)
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        var ok = 0;
        try
        {
            Directory.CreateDirectory(folder);
            var samples = SpeechToneCatalog.BuildAuditionSamples();
            var rows = new List<string>();
            var playlist = new List<string> { "#EXTM3U" };

            // 每个音色用一个独立 TtsService（走真实合成链路：分段韵律 + WAV 拼接），
            // 保存成 WAV 试听（不播放），这样听到的就是实际对话时的效果。
            var services = new Dictionary<string, TtsService>();
            try
            {
                var index = 0;
                foreach (var sample in samples)
                {
                    if (_shutdown) return;
                    index++;
                    var file = sample.Id + ".wav";
                    var path = Path.Combine(folder, file);
                    if (!services.TryGetValue(sample.VoiceShortName, out var tts))
                    {
                        tts = new TtsService { VoiceId = "edge:" + sample.VoiceShortName };
                        services[sample.VoiceShortName] = tts;
                    }

                    var utterance = await tts.SynthesizeAsync(sample.Text, sample.Tone, CancellationToken.None)
                        .ConfigureAwait(true);
                    string note;
                    if (utterance is null || utterance.WaveBytes.Length < 512)
                    {
                        note = "合成失败：" + (tts.LastError ?? "未知原因");
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(path, utterance.WaveBytes);
                        ok++;
                        note = sample.IsBaseline ? "原始（无韵律调整）" : "分段韵律（实际对话效果）";
                        playlist.Add(path);
                    }

                    viewModel.Log($"[试听 {index}/{samples.Count}] {file}：{note}");
                    rows.Add("| " + file + " | " + sample.VoiceLabel + " | " + sample.Scene + " | " +
                             (sample.IsBaseline ? "（原始）" : "分段韵律") + " | " +
                             sample.Text + " | " + note + " |");
                }
            }
            finally
            {
                foreach (var tts in services.Values)
                {
                    try { tts.Dispose(); } catch { /* 忽略 */ }
                }
            }

            var markdown = "# 大肥鱼语音试听对比（A：Edge 韵律化 / 情感风格实测）\n\n"
                           + "> 同一批台词在不同音色 / 语气下的对比。直接双击 MP3 听；或双击 `audition.m3u` 整列表播放。\n"
                           + "> **重要实测结论**：Edge 的朗读接口不接受 `mstts:express-as` 情感风格（24 个风格样本全部自动回退），\n"
                           + "> 所以下面听到的是**纯韵律版**（语速 / 音调 / 音量 / 「呀！」重音 / 省略号停顿），也就是不换引擎时能达到的效果。\n"
                           + "> 想要真正的「演技」需要换 Azure 官方（同样音色、支持风格）或商用情感 TTS（路线 B/C）。\n\n"
                           + "| 文件 | 音色 | 场景 | 风格/强度 | 台词 | 结果 |\n| --- | --- | --- | --- | --- | --- |\n"
                           + string.Join("\n", rows) + "\n\n"
                           + "**重点对比**：`pinch`（呀！+ 停顿 + 后半句放轻）是否够「激烈」；`wait`（原始）vs `wait_affection`（慢一点、停顿多）差别是否明显；\n"
                           + "`hungry` / `tsundere` 是否自然；同一句换 `晓伊 / 晓晓 / 晓北 / 晓雨` 哪个更像你想要的软妹。\n";
            await File.WriteAllTextAsync(Path.Combine(folder, "index.md"), markdown);
            await File.WriteAllLinesAsync(Path.Combine(folder, "audition.m3u"), playlist);

            viewModel.Log($"语音试听生成完毕：{folder}（成功 {ok}/{samples.Count}）");
        }
        catch (Exception ex)
        {
            viewModel.Log("语音试听生成失败：" + ex.Message);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("YLT_LIVE2D_TTS_AUDITION_EXIT") is "1" or "on" or "true")
            {
                await Task.Delay(400);
                Application.Current?.Shutdown();
            }
        }
    }

    // ===================== 页面生命周期 =====================

    /// <summary>取（或创建）工具页 ViewModel（整个进程只建一次，页面重开复用）。</summary>
    public Live2DViewModel EnsureViewModel()
    {
        EnsureInitialized();
        if (_viewModel is null)
        {
            _viewModel = new Live2DViewModel();
            _viewModel.Session = this;
        }
        return _viewModel;
    }

    /// <summary>工具页面打开：恢复配置保存、按需加载模型 / 显示桌宠。</summary>
    public void Activate(Live2DViewModel viewModel)
    {
        if (_shutdown) return;
        EnsureInitialized();
        HookWindowClose();

        _viewModel = viewModel;
        viewModel.Session = this;
        _pageActive = true;
        try { viewModel.NotifyOpened(); } catch { /* 恢复自动保存失败不影响使用 */ }

        // AI 聊天服务：页面一打开就建（设置卡 / 好感度数值要及时显示），设置按当前配置套用
        try { EnsureAiChat(); } catch { /* 聊天服务初始化失败不影响模型 */ }
        ApplyAiSettings();

        // 养成服务：饱食度 / 金币 / 任务（页面或桌宠可见时由 2 秒调度结算）
        try { EnsureCare(); } catch { /* 养成服务初始化失败不影响模型 */ }
        ApplyCareSettings();

        EnsureModelLoaded();
        ApplyRuntimeSettings();
        UpdateFrameLoop();
        ApplyMocapSettings();
        ApplyMusicSettings();
        TryBeginSwayDemo();
        TryBeginFaceDemo();
        TryBeginEyeDemo();
        TryBeginMocapSelfTest();
        TryBeginMusicSelfTest();
        TryBeginBodyLeanDemo();
        TryBeginActionDemo();
        TryBeginSpeechSelfTest();
        TryBeginBubbleShot();
        TryBeginInteractionDemo();
        if (_idleChatterNextAt == TimeSpan.MinValue)
        {
            _idleChatterNextAt = _clock.Elapsed + TimeSpan.FromSeconds(Random.Shared.Next(60, 120));
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>工具页面关闭（切走 / 回主页）；桌宠还开着就继续跑。</summary>
    public void Deactivate(Live2DViewModel? viewModel = null)
    {
        if (viewModel is not null && !ReferenceEquals(viewModel, _viewModel)) return;
        _pageActive = false;
        _pageTarget = null;
        UpdateFrameLoop();
        ApplyMocapSettings();   // 页面关掉且桌宠也没开 → 关摄像头
        ApplyMusicSettings();   // 同理：音频回环也释放

        // 页面关了、桌宠也没开：别让模型一直盯着一个不存在的鼠标
        if (_pet is null)
        {
            try { _runtime?.ClearEyeTrackingTarget(); } catch { /* 忽略 */ }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>程序退出：关桌宠、停帧循环、释放模型、把配置落盘。</summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;

        UnhookWindowClose();
        try { _loadCts?.Cancel(); } catch { /* 忽略 */ }
        _loadCts = null;
        _loadTask = null;
        _loading = false;

        try { _frameTimer?.Stop(); } catch { /* 忽略 */ }
        try { _idleUnloadTimer?.Stop(); } catch { /* 忽略 */ }
        _frameTimer = null;
        _idleUnloadTimer = null;

        ClosePetInternal(raiseEvent: false);
        DisposeMocapBlocking();
        DisposeMusicBlocking();
        DisposeSpeechBlocking();
        DisposeAiBlocking();
        DisposeCareBlocking();
        DisposeRuntime();

        try { _viewModel?.ConfigStore.SaveNow(); } catch { /* 退出流程忽略 */ }
        try { _viewModel?.NotifyClosed(); } catch { /* 忽略 */ }
    }

    /// <summary>立刻保存配置（桌宠拖动 / 缩放 / 切换设置后调用，防抖窗口内的改动也不会丢）。</summary>
    public void SaveConfigNow()
    {
        try { _viewModel?.ConfigStore.SaveNow(); } catch { /* 忽略 */ }
    }

    // ===================== 模型加载 =====================

    /// <summary>
    /// 按需加载模型（已加载 / 正在加载时什么都不做）。
    /// <para>普通启动走后台线程（不卡界面）；开发调试截图模式（<see cref="IsDebugShotMode"/>）下
    /// 在 UI 线程同步加载，保证外壳截图时画面已经出来。</para>
    /// </summary>
    public void EnsureModelLoaded()
    {
        if (_shutdown) return;
        EnsureInitialized();
        HookWindowClose();
        if (_runtime is not null || _loadTask is not null || _loading) return;

        var vm = EnsureViewModel();
        _loading = true;
        _loadProgress = 0;
        _lastReportedProgress = -1;
        _loadError = null;
        _frameErrorLogged = false;
        vm.SetLoadStarted();
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (IsDebugShotMode)
        {
            // 截图模式：同步加载（UI 线程阻塞 1~3 秒，换来 1200ms 后截图里有最终画面）
            vm.Log("截图模式：同步加载模型…");
            DoLoad(vm, CancellationToken.None);
            return;
        }

        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        vm.Log("开始异步加载模型…");
        _loadTask = Task.Run(() => DoLoad(vm, token), token);
    }

    /// <summary>重新加载模型（换模型文件夹 / 改纹理尺寸后调用）。</summary>
    public void ReloadModel()
    {
        if (_shutdown) return;
        try { _loadCts?.Cancel(); } catch { /* 忽略 */ }
        _loadCts = null;
        _loadTask = null;
        _loading = false;
        _loadError = null;

        DisposeRuntime();
        _viewModel?.AttachRuntime(null, 0);
        _viewModel?.SetLoadStarted();
        _viewModel?.Log("重新加载模型…");
        EnsureModelLoaded();
    }

    /// <summary>真正做加载（后台线程 / 截图模式的 UI 线程都会走到这里）。</summary>
    private void DoLoad(Live2DViewModel vm, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (_shutdown) return;
            var cfg = vm.Cfg;
            var folder = string.IsNullOrWhiteSpace(cfg.ModelFolder)
                ? Live2DModelAsset.BuiltInModelDirectory
                : cfg.ModelFolder.Trim();
            var asset = Live2DModelAsset.Discover(folder);
            vm.Log($"模型资源：{asset.Name}（{asset.Directory}）");

            var textureSize = IsDebugShotMode ? 2048 : ClampTextureSize(cfg.TextureMaxSize);
            var options = new RuntimeLoadOptions
            {
                TextureMaxSize = textureSize,
                IdleMotionName = string.IsNullOrWhiteSpace(cfg.IdleMotion) ? null : cfg.IdleMotion.Trim(),
                Progress = progress => ReportLoadProgress(vm, progress),
                Log = message => vm.Log(message),
                CancellationToken = cancellationToken,
            };

            // Core 自己的日志也转进界面日志（加载失败时能看出 moc / 纹理哪一步出的问题）
            try { CubismCore.SetLogHandler(message => vm.Log("[Core] " + message)); } catch { /* 忽略 */ }
            ModelRuntime runtime;
            try
            {
                runtime = ModelRuntime.Load(asset, options);
            }
            finally
            {
                try { CubismCore.SetLogHandler(null); } catch { /* 忽略 */ }
            }

            watch.Stop();
            if (_shutdown || cancellationToken.IsCancellationRequested)
            {
                try { runtime.Dispose(); } catch { /* 忽略 */ }
                return;
            }

            vm.Ui(() => FinishLoad(vm, runtime, watch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException)
        {
            // 页面 / 程序关闭时取消：什么都不用做
        }
        catch (Exception ex)
        {
            watch.Stop();
            vm.Ui(() => FailLoad(vm, ex));
        }
    }

    /// <summary>加载进度回调（可能来自后台线程；节流后切 UI 线程更新进度条）。</summary>
    private void ReportLoadProgress(Live2DViewModel vm, double progress)
    {
        if (!double.IsFinite(progress)) return;
        progress = Math.Clamp(progress, 0, 1);
        if (progress < 1 && progress - _lastReportedProgress < 0.01) return;
        _lastReportedProgress = progress;
        vm.Ui(() =>
        {
            _loadProgress = progress;
            vm.SetLoadProgress(progress);
        });
    }

    private static int ClampTextureSize(int value) => value switch
    {
        <= 1024 => 1024,
        <= 2048 => 2048,
        _ => 4096,
    };

    /// <summary>加载成功（UI 线程）：接上运行时、建桌宠渲染器、开始帧循环、渲染第一帧。</summary>
    private void FinishLoad(Live2DViewModel vm, ModelRuntime runtime, double loadMilliseconds)
    {
        if (_shutdown)
        {
            try { runtime.Dispose(); } catch { /* 忽略 */ }
            return;
        }

        _runtime = runtime;
        _lastLoadMilliseconds = loadMilliseconds;
        _loading = false;
        _loadProgress = 1;
        _loadError = null;
        _loadTask = null;
        _loadCts = null;

        // 桌宠鼠标互动：热区点击 / 抚摸 / 捏脸 → 运行时抛 voice event → 会话挑台词说话（气泡 + 口型）
        try { runtime.InteractionHappened += OnInteractionHappened; } catch { /* 运行时还没实现互动事件 */ }
        try { runtime.InteractionTriggered += OnInteractionTriggered; } catch { /* 旧运行时没有这个事件 */ }

        // 桌宠用独立的渲染器（只共享只读纹理）：页面和桌宠同时显示时各画各的位图，互不覆盖
        _petRenderer = null;
        try
        {
            _petRenderer = new CubismRenderer(runtime.Renderer.Textures);
        }
        catch (Exception ex)
        {
            vm.Log("桌宠独立渲染器创建失败（桌宠将复用页面渲染器）：" + ex.Message);
        }

        vm.AttachRuntime(runtime, loadMilliseconds);
        ApplyRuntimeSettings();
        AssignMocapToRuntime();
        ApplyMocapSettings();
        ApplyMusicSettings();
        vm.Log($"模型加载完成：{runtime.ModelName}，用时 {loadMilliseconds:0} ms" +
               $"（动作 {runtime.Motions.Count} 个，表情 {runtime.Expressions.Count} 个）");
        vm.SetStatus("模型已就绪");

        // 截图 / 验收用的演示钩子放在「模型已就绪」之后：状态栏能显示演示提示
        TryBeginSwayDemo();
        TryBeginFaceDemo();
        TryBeginEyeDemo();
        TryBeginMocapSelfTest();
        TryBeginMusicSelfTest();
        TryBeginBodyLeanDemo();
        TryBeginActionDemo();
        TryBeginSpeechSelfTest();
        TryBeginBubbleShot();
        TryBeginInteractionDemo();

        UpdateFrameLoop();
        RenderFirstFrame();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>加载失败（UI 线程）：界面显示错误 + 日志，桌宠窗口保持透明（不会崩）。</summary>
    private void FailLoad(Live2DViewModel vm, Exception exception)
    {
        _loading = false;
        _loadProgress = 0;
        _loadTask = null;
        _loadCts = null;
        _loadError = exception.Message;
        DisposeRuntime();

        var message = "模型加载失败：" + exception.Message;
        vm.Log(message);
        try { vm.Log(exception.ToString()); } catch { /* 忽略 */ }
        vm.SetLoadFailed(exception.Message);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>释放当前模型（页面 / 桌宠都不用了、重新加载前、程序退出时）。</summary>
    private void DisposeRuntime()
    {
        var runtime = _runtime;
        _runtime = null;
        var petRenderer = _petRenderer;
        _petRenderer = null;

        try { if (runtime is not null) runtime.InteractionHappened -= OnInteractionHappened; } catch { /* 忽略 */ }
        try { if (runtime is not null) runtime.InteractionTriggered -= OnInteractionTriggered; } catch { /* 忽略 */ }
        try { petRenderer?.Dispose(); } catch { /* 忽略 */ }
        try { runtime?.Dispose(); } catch { /* 忽略 */ }

        _frameIndex = 0;
    }

    /// <summary>加载完成后立刻出一帧（截图模式要保证 Bitmap 已生成，不能等定时器）。</summary>
    private void RenderFirstFrame()
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;

        try { runtime.Tick(TimeSpan.FromMilliseconds(16)); } catch { /* 一帧失败不影响后面 */ }

        var page = _pageTarget;
        if (page is { IsTargetVisible: true })
        {
            try { page.RenderFrame(runtime); } catch { /* 渲染异常由帧循环统一处理 */ }
        }
        if (_pet is { IsVisible: true } pet)
        {
            try { pet.RenderFrame(runtime); } catch { /* 同上 */ }
        }
        try { _viewModel?.RefreshFrameState(runtime); } catch { /* 忽略 */ }
    }

    // ===================== 设置 / 帧率 =====================

    /// <summary>把配置里的运行时开关（物理 / 待机 / 眨眼 / 眼控强度 / 待机动作）推给模型运行时。</summary>
    public void ApplyRuntimeSettings()
    {
        var runtime = _runtime;
        var cfg = _viewModel?.Cfg;
        if (runtime is null || cfg is null) return;

        try
        {
            runtime.PhysicsEnabled = cfg.PhysicsEnabled;
            runtime.PhysicsStrength = Math.Clamp(cfg.PhysicsStrength, 0, 1);
            runtime.IdleEnabled = cfg.IdleEnabled;
            runtime.AutoBlink = cfg.AutoBlink;
            runtime.EyeTrackingEnabled = cfg.EyeTracking;
            runtime.EyeTrackingSmoothing = Math.Clamp(cfg.EyeTrackingSmoothing, 0.05, 1);
            runtime.EyeTrackingHeadStrength = Math.Clamp(cfg.EyeTrackingHeadStrength, 0.2, 1.5);
            runtime.EyeTrackingEyeStrength = Math.Clamp(cfg.EyeTrackingEyeStrength, 0.2, 1.5);
            runtime.EyeTrackingBodyStrength = Math.Clamp(cfg.EyeTrackingBodyStrength, 0, 1.5);

            // 鼠标跟随灵敏度：横向 / 抬头 / 低头 / 纵向偏移（桌宠全局鼠标也共用这套）
            runtime.EyeTrackingHorizontalGain = Math.Clamp(cfg.EyeTrackingHorizontalGain, 0.3, 3);
            runtime.EyeTrackingUpGain = Math.Clamp(cfg.EyeTrackingUpGain, 0.3, 3);
            runtime.EyeTrackingDownGain = Math.Clamp(cfg.EyeTrackingDownGain, 0.3, 3);
            runtime.EyeTrackingVerticalOffset = Math.Clamp(cfg.EyeTrackingVerticalOffset, -0.5, 0.5);

            runtime.IdleMotion = FindIdleMotion(runtime, cfg.IdleMotion);

            // 摄像头面部动捕：驱动开关与强度（数据源本身由 ApplyMocapSettings 启停 / 重建）
            runtime.MocapEnabled = cfg.MocapEnabled;
            runtime.MocapHeadEnabled = cfg.MocapHeadEnabled;
            runtime.MocapBlinkEnabled = cfg.MocapBlinkEnabled;
            runtime.MocapMouthEnabled = cfg.MocapMouthEnabled;
            runtime.MocapHeadStrength = Math.Clamp(cfg.MocapHeadStrength, 0, 1.5);
            runtime.MocapEyeStrength = Math.Clamp(cfg.MocapEyeStrength, 0, 1.5);
            runtime.MocapMouthStrength = Math.Clamp(cfg.MocapMouthStrength, 0, 1.5);

            // 身体晃动测试（参数由运行时 BodySwayController 每帧写；「摇一摇」是临时演示）
            runtime.SwayEnabled = cfg.BodySwayEnabled;
            runtime.SwayMode = cfg.BodySwayMode;
            runtime.SwayAmplitudeDegrees = Math.Clamp(cfg.BodySwayAmplitude, 0, 30);
            runtime.SwaySpeedHz = Math.Clamp(cfg.BodySwaySpeed, 0.1, 2);
            runtime.SwayHeadFollow = Math.Clamp(cfg.BodySwayHeadFollow, 0, 1);
            runtime.SwayIncludeHips = cfg.BodySwayIncludeHips;

            // 音乐跟唱 / 律动（数据源本身由 ApplyMusicSettings 启停；这里只推驱动参数）
            runtime.MusicEnabled = cfg.MusicReactiveEnabled;
            runtime.MusicMode = MapMusicMode(cfg.MusicReactiveMode);
            runtime.MusicLipSyncStrength = Math.Clamp(cfg.MusicLipSyncStrength, 0, 1.5);
            runtime.MusicSwayStrength = Math.Clamp(cfg.MusicSwayStrength, 0, 1.5);
            runtime.MusicSwayEnabled = cfg.MusicSwayEnabled;
            runtime.MusicHeadBobEnabled = cfg.MusicHeadBobEnabled;

            // 全身轻晃：以脚为支点的整身轻倾（运行时算角度，UI 渲染层按它旋转；不是扭腰）
            runtime.BodyLeanEnabled = cfg.BodyLeanEnabled;

            // 待机灵动：随机小动作（手势 / 表演由运行时自己挑）
            runtime.IdleRandomGestureEnabled = cfg.IdleRandomGestureEnabled;
            runtime.IdleRandomGestureIntervalSeconds = Math.Clamp(cfg.IdleRandomGestureIntervalSeconds, 8, 60);

            // 桌宠鼠标互动玩法 / 待机灵动（热区是运行时模型空间；UI 只负责把开关推下去）
            runtime.InteractionEnabled = cfg.PetInteractionEnabled;
            runtime.InteractionVoiceEnabled = cfg.PetInteractionVoiceEnabled;
            runtime.PetIdleActionIntervalSeconds = Math.Clamp(cfg.PetIdleActionIntervalSeconds, 8, 30);
            runtime.PetIdleExpressionEnabled = cfg.PetIdleExpressionEnabled;
            runtime.DragPhysicsEnabled = cfg.PetDragPhysicsEnabled;
        }
        catch (Exception ex)
        {
            _viewModel?.Log("应用运行时设置失败：" + ex.Message);
        }

        ApplyFrameRate();
    }

    /// <summary>按配置里的待机动作名找 MotionEntry（空 / 找不到返回 null = 让运行时自己挑）。</summary>
    private static MotionEntry? FindIdleMotion(ModelRuntime runtime, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var motion in runtime.Motions)
        {
            if (string.Equals(motion.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return motion;
        }
        return null;
    }

    /// <summary>帧率上限改了之后重设定时器间隔。</summary>
    public void ApplyFrameRate()
    {
        if (_frameTimer is null) return;
        var fps = Math.Clamp(_viewModel?.Cfg.FpsLimit ?? 30, 15, 60);
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        if (Math.Abs((_frameTimer.Interval - interval).TotalMilliseconds) > 0.5) _frameTimer.Interval = interval;
    }

    // ===================== 帧循环 =====================

    /// <summary>页面 / 桌宠可见性或加载状态变化后调它：决定帧循环开还是关。</summary>
    public void RefreshFrameLoop()
    {
        UpdateFrameLoop();
        ApplyMocapSettings();   // 窗口最小化 / 还原会影响「页面可见」→ 摄像头跟着开 / 关
        ApplyMusicSettings();   // 音乐回环同理
    }

    /// <summary>页面目标挂上来（工具页 Loaded）。</summary>
    public void AttachPageTarget(ILive2DFrameTarget target)
    {
        if (_shutdown) return;
        EnsureInitialized();
        HookWindowClose();
        _pageTarget = target;
        UpdateFrameLoop();
        ApplyMocapSettings();
        ApplyMusicSettings();
        TryBeginMocapSelfTest();
        TryBeginMusicSelfTest();
        TryBeginBodyLeanDemo();
        TryBeginActionDemo();
        TryBeginSpeechSelfTest();
        TryBeginBubbleShot();
        TryBeginInteractionDemo();
        TryBeginAiChatShot();
        TryBeginFeedShot();
        TryBeginTtsAudition();
        if (IsLoaded)
        {
            try { _runtime!.Tick(TimeSpan.FromMilliseconds(16)); } catch { /* 忽略 */ }
            try { target.RenderFrame(_runtime!); } catch { /* 忽略 */ }
        }
    }

    /// <summary>页面目标摘下来（工具页 Unloaded）。</summary>
    public void DetachPageTarget(ILive2DFrameTarget target)
    {
        if (ReferenceEquals(_pageTarget, target)) _pageTarget = null;
        UpdateFrameLoop();
        ApplyMocapSettings();
        ApplyMusicSettings();
    }

    private void UpdateFrameLoop()
    {
        if (_shutdown) return;

        var pageVisible = _pageTarget is { IsTargetVisible: true };
        var petVisible = _pet?.IsVisible == true;
        var need = _runtime?.IsLoaded == true && (pageVisible || petVisible);

        if (need)
        {
            if (_frameTimer is null)
            {
                _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(_viewModel?.Cfg.FpsLimit ?? 30, 15, 60)),
                };
                _frameTimer.Tick += OnFrameTick;
            }
            if (!_frameTimer.IsEnabled)
            {
                _lastFrameTime = _clock.Elapsed;
                _frameTimer.Start();
            }
        }
        else
        {
            _frameTimer?.Stop();
        }

        ScheduleIdleUnload(pageTargetAttached: _pageTarget is not null, petVisible: _pet is not null);
        UpdateAiTickTimer();
    }

    /// <summary>页面 / 桌宠都没了：过一会儿释放模型内存（避免在别的工具页呆着白占 ~100MB）。</summary>
    private void ScheduleIdleUnload(bool pageTargetAttached, bool petVisible)
    {
        if (_runtime is null)
        {
            _idleUnloadTimer?.Stop();
            return;
        }

        if (pageTargetAttached || petVisible)
        {
            _idleUnloadTimer?.Stop();
            return;
        }

        if (_idleUnloadTimer is null)
        {
            _idleUnloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleUnloadDelayMs) };
            _idleUnloadTimer.Tick += OnIdleUnloadTick;
        }
        _idleUnloadTimer.Stop();
        _idleUnloadTimer.Start();
    }

    private void OnIdleUnloadTick(object? sender, EventArgs e)
    {
        _idleUnloadTimer?.Stop();
        if (_pageTarget is not null || _pet is not null) return;
        _viewModel?.Log("页面与桌宠都已关闭，释放模型内存（再次打开会重新加载）");
        _viewModel?.SetStatus("模型未加载：点「重新加载模型」或重新打开桌宠");
        DisposeRuntime();
        _viewModel?.AttachRuntime(null, 0);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>每帧：Tick 一次 → 页面渲染 → 桌宠渲染（两边都可见时桌宠降到约 15fps）。</summary>
    private void OnFrameTick(object? sender, EventArgs e)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded)
        {
            _frameTimer?.Stop();
            return;
        }

        var now = _clock.Elapsed;
        var delta = now - _lastFrameTime;
        _lastFrameTime = now;
        if (delta <= TimeSpan.Zero) return;
        // 卡顿 / 系统休眠后别把动作一次快进太多
        if (delta > TimeSpan.FromMilliseconds(200)) delta = TimeSpan.FromMilliseconds(100);

        try
        {
            runtime.Tick(delta);
        }
        catch (Exception ex)
        {
            HandleFrameException(ex);
            return;
        }

        // 说话期间每帧同步状态给运行时（嘴型由运行时驱动 ParamMouthOpenY；动捕有脸时嘴型优先动捕）
        SyncSpeechToRuntime(runtime);

        // 聊天气泡跟随桌宠（拖动 / 缩放 / 多屏时每帧重算位置）
        UpdateBubblePosition();

        // AI 聊天窗贴在桌宠下方 / 旁边，同样每帧跟随
        UpdateAiChatWindowPosition();

        // AI 临时表情 / 鼓嘴到期收回
        UpdateAiMoodEffectsExpiry();

        // 空闲自言自语：每约 2 秒检查一次（内部还有 60~120 秒的随机间隔）
        if (_frameIndex % 60 == 0) TryIdleChatter();

        _frameIndex++;

        var page = _pageTarget;
        var pageVisible = page is { IsTargetVisible: true };
        if (pageVisible)
        {
            var pageWatch = Stopwatch.StartNew();
            try { page!.RenderFrame(runtime); }
            catch (Exception ex) { HandleFrameException(ex); return; }
            _lastPageRenderMs = pageWatch.Elapsed.TotalMilliseconds;
        }

        if (_pet is { IsVisible: true } pet)
        {
            var fpsLimit = Math.Clamp(_viewModel?.Cfg.FpsLimit ?? 30, 15, 60);
            int petSkip;
            if (!pageVisible)
            {
                petSkip = 1;   // 只有桌宠：每帧都画（窗口小）
            }
            else
            {
                // 页面 + 桌宠同时可见：两张大图各渲染一次。实测明显吃力时把桌宠降到约 10fps（页面不受影响），
                // 并在日志 / 状态栏说明一次，免得用户以为掉帧是 bug。
                var heavy = _lastPageRenderMs + _lastPetRenderMs > 25;
                if (heavy && !_dualSlowLogged)
                {
                    _dualSlowLogged = true;
                    var message = $"页面与桌宠同时显示，每帧渲染约 {_lastPageRenderMs + _lastPetRenderMs:0} ms，" +
                                  "桌宠画面自动降到约 10fps（测试页仍按设定帧率）";
                    _viewModel?.Log(message);
                    _viewModel?.SetStatus(message);
                }
                petSkip = heavy
                    ? Math.Max(3, (int)Math.Round(fpsLimit / 10.0))
                    : Math.Max(2, (int)Math.Round(fpsLimit / 15.0));
            }

            if (_frameIndex % petSkip == 0)
            {
                var petWatch = Stopwatch.StartNew();
                try { pet.RenderFrame(runtime); }
                catch (Exception ex) { HandleFrameException(ex); return; }
                _lastPetRenderMs = petWatch.Elapsed.TotalMilliseconds;
            }
        }
        else
        {
            _lastPetRenderMs = 0;
        }

        // 界面上的动作进度 / 参数状态不用每帧刷，每 6 帧刷一次就够（避免无谓的绑定开销）
        if (_frameIndex % 6 == 0)
        {
            try { _viewModel?.RefreshFrameState(runtime); } catch { /* 忽略 */ }
        }
    }

    /// <summary>帧循环里出错：只记一次并停下，避免每帧刷日志把程序拖死。</summary>
    private void HandleFrameException(Exception exception)
    {
        if (_frameErrorLogged) return;
        _frameErrorLogged = true;
        try { _frameTimer?.Stop(); } catch { /* 忽略 */ }
        _viewModel?.Log("渲染帧出错，已暂停渲染：" + exception.Message);
        _viewModel?.SetStatus("渲染出错：" + exception.Message);
    }

    // ===================== 动作 / 表情 / 参数控制 =====================

    /// <summary>播放动作（loop = null 时用动作自带的循环建议；fadeSeconds = null 时用配置里的淡入淡出时长）。</summary>
    public void PlayMotion(MotionEntry motion, bool? loop = null, double? fadeSeconds = null)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;
        var fade = Math.Clamp(fadeSeconds ?? _viewModel?.Cfg.MotionFadeSeconds ?? 0.3, 0, 2);
        try
        {
            runtime.PlayMotion(motion, loop, fade);
            _viewModel?.Log($"播放动作：{motion.Name}（{motion.Duration:0.0}s，{(loop ?? motion.Loop ? "循环" : "单次")}，淡入 {fade:0.00}s）");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("播放动作失败：" + ex.Message);
        }
    }

    /// <summary>停止当前动作。</summary>
    public void StopMotion()
    {
        try
        {
            _runtime?.StopMotion();
            _viewModel?.Log("已停止动作");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("停止动作失败：" + ex.Message);
        }
    }

    /// <summary>施加表情（多个表情可叠加；淡入时长取配置）。</summary>
    public void ApplyExpression(ExpressionEntry expression, double fadeSeconds)
    {
        try { _runtime?.ApplyExpression(expression, Math.Max(0, fadeSeconds)); }
        catch (Exception ex) { _viewModel?.Log("施加表情失败：" + ex.Message); }
    }

    /// <summary>清除全部表情（淡出时长取配置）。</summary>
    public void ClearExpressions(double fadeSeconds)
    {
        try { _runtime?.ClearExpressions(Math.Max(0, fadeSeconds)); }
        catch (Exception ex) { _viewModel?.Log("清除表情失败：" + ex.Message); }
    }

    /// <summary>只移除一个表情（多选叠加时取消其中一项，其它表情继续）。</summary>
    public void RemoveExpression(ExpressionEntry expression, double fadeSeconds)
    {
        try { _runtime?.RemoveExpression(expression, Math.Max(0, fadeSeconds)); }
        catch (Exception ex) { _viewModel?.Log("取消表情失败：" + ex.Message); }
    }

    /// <summary>当前生效（含淡出中）的表情个数。</summary>
    public int ActiveExpressionCount => _runtime?.ActiveExpressionCount ?? 0;

    /// <summary>把所有参数 / 部件恢复默认（同时停掉动作与表情）。</summary>
    public void ResetAll()
    {
        try
        {
            _runtime?.ResetParameters();
            _viewModel?.Log("已重置全部参数与部件");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("重置失败：" + ex.Message);
        }
    }

    // ===================== 眼睛跟随 =====================

    /// <summary>页面预览里的鼠标位置（画布像素坐标）：页面活着时由页面优先控制眼神。</summary>
    public void SetPageEyeTarget(float canvasX, float canvasY)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;
        _lastPageEyeTime = DateTime.UtcNow;
        try { runtime.SetEyeTrackingTarget(canvasX, canvasY); } catch { /* 忽略 */ }
    }

    /// <summary>鼠标离开预览区：交回给桌宠（桌宠没开就是回正）。</summary>
    public void ClearPageEyeTarget()
    {
        _lastPageEyeTime = DateTime.MinValue;
        try { _runtime?.ClearEyeTrackingTarget(); } catch { /* 忽略 */ }
    }

    /// <summary>页面最近是否还在控制眼神（桌宠据此决定要不要抢）。</summary>
    public bool PageOwnsEyeTracking => (DateTime.UtcNow - _lastPageEyeTime).TotalMilliseconds < 400;

    /// <summary>桌宠用全局鼠标位置控制眼神（画布像素坐标）。</summary>
    public void SetPetEyeTarget(float canvasX, float canvasY)
    {
        if (PageOwnsEyeTracking) return;
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;
        try { runtime.SetEyeTrackingTarget(canvasX, canvasY); } catch { /* 忽略 */ }
    }

    /// <summary>桌宠不再控制眼神（眼控关掉 / 鼠标取不到 / 桌宠关闭）：回正，别留旧目标。</summary>
    public void ClearPetEyeTarget()
    {
        if (PageOwnsEyeTracking) return;   // 页面正在控制时别抢
        try { _runtime?.ClearEyeTrackingTarget(); } catch { /* 忽略 */ }
    }

    // ===================== 摄像头面部动捕 =====================

    /// <summary>
    /// 按配置让动捕数据源对齐目标状态：该开就开、该关就关、设置变了就整源重建。
    /// <para>可快速连续调用：内部按 revision + 信号量串行，只有最后一次请求真正落地（避免连点开关时
    /// 反复开关摄像头）。启动 / 停止 / 重建都在 UI 线程上下文继续执行，失败只记 <see cref="MocapStartError"/>
    /// 和日志，不会让程序崩。</para>
    /// </summary>
    public void ApplyMocapSettings()
    {
        if (_shutdown) return;
        _ = ReconcileMocapAsync();
    }

    private async Task ReconcileMocapAsync()
    {
        var revision = Interlocked.Increment(ref _mocapRevision);
        try { await _mocapGate.WaitAsync(); }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (_shutdown || revision != Volatile.Read(ref _mocapRevision)) return;

            var cfg = _viewModel?.Cfg;
            if (cfg is null)
            {
                await StopMocapAsync();
                AssignMocapToRuntime();
                RaiseMocapStateChanged();
                return;
            }

            // 截图 / 桌宠截图模式绝不开摄像头（隐私：截图流程里不弹摄像头权限、不采集画面）
            var wantRunning = cfg.MocapEnabled && IsPageOrPetVisible && !IsDebugShotMode;
            var settings = MocapSettings.FromConfig(cfg);

            // 设置变了（摄像头 / 镜像 / 帧率 / 平滑 / 预览 / 中性位校准）：旧源释放，按新设置重建
            if (_mocap is not null && !MocapSettingsEqual(_mocapAppliedSettings, settings))
            {
                await DisposeMocapAsync();
            }

            if (!wantRunning)
            {
                await StopMocapAsync();
                RaiseMocapStateChanged();
                return;
            }

            if (_mocap is null)
            {
                _mocap = new MocapCameraSource(settings);
                _mocapAppliedSettings = settings;
                _mocap.StateChanged += OnMocapStateChanged;
                _mocapStartError = null;
                _lastLoggedMocapError = null;
                AssignMocapToRuntime();
                _viewModel?.Log($"动捕：准备打开摄像头（{DescribeMocapCamera(settings)}）");
            }

            if (revision != Volatile.Read(ref _mocapRevision))
            {
                RaiseMocapStateChanged();
                return;   // 排队期间配置又变了：交给最后一次
            }

            if (!_mocap.IsRunning)
            {
                var watch = Stopwatch.StartNew();
                var started = await _mocap.StartAsync();
                watch.Stop();
                if (revision != Volatile.Read(ref _mocapRevision))
                {
                    RaiseMocapStateChanged();
                    return;
                }

                if (started)
                {
                    _mocapStartError = null;
                    var frame = _mocap.FrameSize;
                    _viewModel?.Log($"动捕已启动：{DescribeMocapCamera(settings)}（{settings.Fps} fps 上限，" +
                                    $"预览{(settings.EnablePreview ? "开" : "关")}，画面 {frame.Width}×{frame.Height}，" +
                                    $"启动耗时 {watch.ElapsedMilliseconds} ms）");
                }
                else
                {
                    _mocapStartError = _mocap.LastError ?? "启动失败（没有可用摄像头 / 权限被拒 / 被其它程序占用）";
                    _viewModel?.Log("动捕启动失败：" + _mocapStartError);
                }
            }

            AssignMocapToRuntime();
            RaiseMocapStateChanged();
        }
        catch (Exception ex)
        {
            _mocapStartError = ex.Message;
            _viewModel?.Log("动捕异常：" + ex.Message);
            RaiseMocapStateChanged();
        }
        finally
        {
            try { _mocapGate.Release(); } catch { /* 已释放 */ }
        }
    }

    /// <summary>把数据源交给模型运行时（运行时只依赖 IMocapProvider 接口读结果）。</summary>
    private void AssignMocapToRuntime()
    {
        var runtime = _runtime;
        if (runtime is null) return;
        try { runtime.Mocap = _mocap; } catch { /* 模型还没加载完 */ }
    }

    /// <summary>停止采集（窗口关掉 / 动捕关掉时；数据源保留，下次可快速重启）。</summary>
    private async Task StopMocapAsync()
    {
        var source = _mocap;
        if (source is null) return;
        if (!source.IsRunning) { AssignMocapToRuntime(); return; }

        try { await source.StopAsync(); }
        catch (Exception ex) { _viewModel?.Log("动捕停止失败：" + ex.Message); }
        AssignMocapToRuntime();
    }

    /// <summary>释放当前数据源（换摄像头 / 换设置时；之后按需重建）。</summary>
    private async Task DisposeMocapAsync()
    {
        var source = _mocap;
        _mocap = null;
        _mocapAppliedSettings = null;
        if (source is null) return;

        try { source.StateChanged -= OnMocapStateChanged; } catch { /* 忽略 */ }
        try { await source.StopAsync(); } catch { /* 忽略 */ }
        try { source.Dispose(); } catch { /* 忽略 */ }
        AssignMocapToRuntime();
    }

    /// <summary>程序退出 / 主窗口关闭时的同步释放（不能再 await）。</summary>
    private void DisposeMocapBlocking()
    {
        var source = _mocap;
        _mocap = null;
        _mocapAppliedSettings = null;
        if (source is null) return;

        try { source.StateChanged -= OnMocapStateChanged; } catch { /* 忽略 */ }
        try { source.Dispose(); } catch { /* 忽略 */ }   // Dispose 内部会同步 Stop
        AssignMocapToRuntime();
    }

    private void OnMocapStateChanged(object? sender, EventArgs e)
    {
        var error = _mocap?.LastError;
        if (!string.IsNullOrEmpty(error) && !string.Equals(error, _lastLoggedMocapError, StringComparison.Ordinal))
        {
            _lastLoggedMocapError = error;
            _viewModel?.Log("动捕状态：" + error);
        }
        _viewModel?.Ui(RaiseMocapStateChanged);
    }

    private void RaiseMocapStateChanged()
    {
        try { MocapStateChanged?.Invoke(this, EventArgs.Empty); } catch { /* 订阅方异常不影响采集 */ }
    }

    private static bool MocapSettingsEqual(MocapSettings? left, MocapSettings? right)
    {
        if (left is null || right is null) return ReferenceEquals(left, right);
        return string.Equals(left.CameraId, right.CameraId, StringComparison.Ordinal)
               && string.Equals(left.CameraName, right.CameraName, StringComparison.Ordinal)
               && left.Mirror == right.Mirror
               && left.Fps == right.Fps
               && left.EnablePreview == right.EnablePreview
               && Math.Abs(left.Smoothing - right.Smoothing) < 0.0001
               && Math.Abs(left.NeutralFaceX - right.NeutralFaceX) < 0.0001
               && Math.Abs(left.NeutralFaceY - right.NeutralFaceY) < 0.0001;
    }

    private static string DescribeMocapCamera(MocapSettings settings)
        => string.IsNullOrWhiteSpace(settings.CameraName)
            ? (string.IsNullOrWhiteSpace(settings.CameraId) ? "自动选择（第一个摄像头）" : settings.CameraId)
            : settings.CameraName;

    /// <summary>重新校准表情（眨眼 / 张嘴的自动基准清零；换环境 / 光线后调）。</summary>
    public void ResetMocapCalibration()
    {
        try
        {
            _mocap?.ResetCalibration();
        }
        catch (Exception ex)
        {
            _viewModel?.Log("重新校准表情失败：" + ex.Message);
            return;
        }

        _viewModel?.Log("动捕：已重新校准表情（眨眼 / 张嘴的自动基准清零）");
        _viewModel?.SetStatus("动捕：表情基准已重新校准");
    }

    /// <summary>
    /// 校准中性位：把当前「有脸」时的脸中心位置写成新的 0 度参考
    /// （换座位 / 换摄像头后「正前方」不对时用）。
    /// </summary>
    public void CalibrateMocapNeutralPosition()
    {
        var cfg = _viewModel?.Cfg;
        if (cfg is null) return;

        var latest = _mocap?.Latest;
        if (latest is not { HasFace: true })
        {
            _viewModel?.Log("校准中性位失败：动捕还没检测到人脸（先把脸放到画面中央、保持正面朝前）");
            _viewModel?.SetStatus("校准中性位：还没检测到人脸");
            return;
        }

        cfg.MocapNeutralFaceX = Math.Clamp(latest.FaceX, 0.05, 0.95);
        cfg.MocapNeutralFaceY = Math.Clamp(latest.FaceY, 0.05, 0.95);
        _viewModel?.Log($"动捕：中性位已校准到画面 ({cfg.MocapNeutralFaceX:0.000}, {cfg.MocapNeutralFaceY:0.000})" +
                        "，数据源会按新基准重建");
        _viewModel?.SetStatus("动捕：中性位已校准");
        ApplyMocapSettings();   // 中性位变了 → 重建数据源
    }

    // ===================== 身体晃动测试 =====================

    /// <summary>「摇一摇」：让运行时按当前模式 / 幅度 / 速度临时晃 N 秒（默认 3 秒）。</summary>
    public void StartSwayDemo(double seconds = 3.0)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;

        try
        {
            runtime.StartSwayDemo(Math.Clamp(seconds, 0.5, 60));
            _viewModel?.Log($"身体晃动：摇一摇 {seconds:0.0} 秒（{_viewModel?.Cfg.BodySwayMode ?? "左右摇"}）");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("摇一摇失败：" + ex.Message);
        }
    }

    /// <summary>停止「摇一摇」演示（不动「开启身体晃动」配置）。</summary>
    public void StopSwayDemo()
    {
        try { _runtime?.StopSwayDemo(); } catch { /* 忽略 */ }
    }

    /// <summary>开发调试（<c>YLT_LIVE2D_SWAY_DEMO=1</c>）：打开页面自动左右摇 15° / 0.5Hz 并演示 10 秒。</summary>
    private void TryBeginSwayDemo()
    {
        if (_swayDemoApplied) return;
        if (Environment.GetEnvironmentVariable("YLT_LIVE2D_SWAY_DEMO") is not ("1" or "on" or "true")) return;
        if (_viewModel is null || _runtime?.IsLoaded != true) return;

        _swayDemoApplied = true;
        var cfg = _viewModel.Cfg;
        cfg.BodySwayEnabled = true;
        cfg.BodySwayMode = "左右摇";
        cfg.BodySwayAmplitude = 15;
        cfg.BodySwaySpeed = 0.5;
        cfg.BodySwayHeadFollow = 0.35;
        ApplyRuntimeSettings();
        try { _runtime.StartSwayDemo(10); } catch { /* 忽略 */ }
        _viewModel.RefreshAllBindings();
        _viewModel.SetStatus("截图模式：身体晃动演示 10 秒（左右摇 15° / 0.5Hz）");
        _viewModel.Log("截图模式（YLT_LIVE2D_SWAY_DEMO）：已启用身体晃动演示 10 秒（左右摇 15° / 0.5Hz）");
    }

    // ===================== 开发调试：面部测试 / 鼠标跟随演示 =====================

    /// <summary>
    /// 开发调试（<c>YLT_LIVE2D_FACE_DEMO</c>）：打开页面自动应用一个「面部测试」预设，
    /// 方便截图里直接看到吐舌头等效果。值可以是预设名（tongue/open/pucker/smile/wink/rolleyes/reset）
    /// 或中文（吐舌头/张嘴/撅嘴/微笑/眨眼/翻白眼/重置面部），<c>1</c> = 吐舌头。
    /// 同时关掉眼睛跟随 / 自动眨眼 / 动捕，避免它们每帧把这些参数覆盖掉。
    /// </summary>
    private void TryBeginFaceDemo()
    {
        if (_faceDemoApplied) return;
        var raw = Environment.GetEnvironmentVariable("YLT_LIVE2D_FACE_DEMO")?.Trim();
        if (string.IsNullOrEmpty(raw)) return;
        if (_viewModel is null || _runtime?.IsLoaded != true) return;

        _faceDemoApplied = true;
        var preset = raw.ToLowerInvariant() switch
        {
            "1" or "on" or "true" => "tongue",
            "吐舌头" => "tongue",
            "张嘴" => "open",
            "撅嘴" => "pucker",
            "微笑" => "smile",
            "眨眼" => "wink",
            "翻白眼" => "rolleyes",
            "重置面部" => "reset",
            _ => raw.ToLowerInvariant(),
        };

        var cfg = _viewModel.Cfg;
        cfg.EyeTracking = false;    // 手动面部值不被眼睛跟随每帧覆盖
        cfg.AutoBlink = false;      // 不被自动眨眼覆盖
        cfg.MocapEnabled = false;   // 动捕也先关（截图验收不碰摄像头）
        ApplyRuntimeSettings();
        ApplyMocapSettings();
        _viewModel.RefreshAllBindings();
        _viewModel.ApplyFacePreset(preset);
        _viewModel.RefreshAllBindings();
        _viewModel.SetStatus("截图模式：已应用面部预设 " + Live2DViewModel.FacePresetDisplayName(preset));
        _viewModel.Log("截图模式（YLT_LIVE2D_FACE_DEMO）：已关闭眼控/自动眨眼/动捕，并应用面部预设 " + preset);
        _ = ReportFaceDemoResultAsync(preset);
    }

    /// <summary>面部演示：0.8 秒后把「实际参数值」写 Data\live2d\face-demo.log（验收证据，便于判断哪些值被运行时覆盖）。</summary>
    private async Task ReportFaceDemoResultAsync(string preset)
    {
        await Task.Delay(800);

        var viewModel = _viewModel;
        if (viewModel is null) return;
        viewModel.Ui(() =>
        {
            try
            {
                var text =
                    $"面部预设：{preset}（{Live2DViewModel.FacePresetDisplayName(preset)}）" + Environment.NewLine +
                    $"关闭的接管：EyeTracking={viewModel.Cfg.EyeTracking}，AutoBlink={viewModel.Cfg.AutoBlink}，" +
                    $"MocapEnabled={viewModel.Cfg.MocapEnabled}" + Environment.NewLine + Environment.NewLine +
                    "800ms 后从模型读回的实际参数值：" + Environment.NewLine +
                    viewModel.BuildFaceValuesSnapshot();
                viewModel.Storage.WriteText("face-demo.log", text);
                viewModel.Log("截图模式：面部预设的实际参数值已写入 Data\\live2d\\face-demo.log");
            }
            catch (Exception ex)
            {
                viewModel.Log("面部演示证据写入失败：" + ex.Message);
            }
        });
    }

    /// <summary>
    /// 开发调试（<c>YLT_LIVE2D_EYE_DEMO=down|up|left|right</c>）：把眼睛跟随目标设到画布外对应方向，
    /// 0.8 秒后把 ParamEyeBallX/Y、ParamAngleX/Y 写进日志，用来验证「低头灵敏度」等滑杆链路。
    /// </summary>
    private void TryBeginEyeDemo()
    {
        if (_eyeDemoApplied) return;
        var raw = Environment.GetEnvironmentVariable("YLT_LIVE2D_EYE_DEMO")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw)) return;
        if (_viewModel is null || _runtime?.IsLoaded != true) return;

        _eyeDemoApplied = true;
        try
        {
            var model = _runtime.Model;
            var canvasWidth = (float)Math.Max(1.0, model.CanvasWidthPixels);
            var canvasHeight = (float)Math.Max(1.0, model.CanvasHeightPixels);
            var (canvasX, canvasY) = raw switch
            {
                "up" => (canvasWidth * 0.5f, -canvasHeight * 0.6f),
                "left" => (-canvasWidth * 0.6f, canvasHeight * 0.5f),
                "right" => (canvasWidth * 1.6f, canvasHeight * 0.5f),
                _ => (canvasWidth * 0.5f, canvasHeight * 1.6f),   // down（默认）
            };

            var cfg = _viewModel.Cfg;
            cfg.EyeTracking = true;
            ApplyRuntimeSettings();
            _viewModel.RefreshAllBindings();
            _runtime.SetEyeTrackingTarget(canvasX, canvasY);
            _viewModel.Log($"截图模式（YLT_LIVE2D_EYE_DEMO）：眼睛跟随目标设为画布 ({canvasX:0},{canvasY:0})（{raw}）");
            _ = ReportEyeDemoResultAsync(raw);
        }
        catch (Exception ex)
        {
            _viewModel.Log("眼控演示失败：" + ex.Message);
        }
    }

    private async Task ReportEyeDemoResultAsync(string direction)
    {
        await Task.Delay(800);

        var viewModel = _viewModel;
        if (viewModel is null) return;
        viewModel.Ui(() =>
        {
            try
            {
                var model = _runtime?.Model;
                if (model is null) return;
                double Value(string id) => model.FindParameter(id)?.Value ?? 0;
                var text =
                    $"眼睛跟随目标方向：{direction}" + Environment.NewLine +
                    $"ParamEyeBallX={Value("ParamEyeBallX"):0.000}  ParamEyeBallY={Value("ParamEyeBallY"):0.000}" +
                    Environment.NewLine +
                    $"ParamAngleX={Value("ParamAngleX"):0.000}  ParamAngleY={Value("ParamAngleY"):0.000}" +
                    Environment.NewLine +
                    $"滑杆：横向 {viewModel.EyeTrackingHorizontalGain:0.00} / 抬头 {viewModel.EyeTrackingUpGain:0.00} / " +
                    $"低头 {viewModel.EyeTrackingDownGain:0.00} / 偏移 {viewModel.EyeTrackingVerticalOffset:0.00}" +
                    Environment.NewLine + "（目标在画布外对应方向；低头时 ParamEyeBallY 应为负、ParamAngleY 为负附近）";
                viewModel.Storage.WriteText("eye-demo.log", text);
                viewModel.Log(
                    $"眼睛跟随验证（目标：{direction}）→ ParamEyeBallX={Value("ParamEyeBallX"):0.00} " +
                    $"ParamEyeBallY={Value("ParamEyeBallY"):0.00} ParamAngleX={Value("ParamAngleX"):0.00} " +
                    $"ParamAngleY={Value("ParamAngleY"):0.00}；滑杆：横向 {viewModel.EyeTrackingHorizontalGain:0.00} / " +
                    $"抬头 {viewModel.EyeTrackingUpGain:0.00} / 低头 {viewModel.EyeTrackingDownGain:0.00} / " +
                    $"偏移 {viewModel.EyeTrackingVerticalOffset:0.00}（已写 Data\\live2d\\eye-demo.log）");
            }
            catch
            {
                // 模型正在切换：忽略
            }
        });
    }

    // ===================== 音乐跟唱 · 律动（WASAPI 回环采集） =====================

    /// <summary>
    /// 按配置让音乐回环数据源对齐目标状态：该开就开、该关就关。
    /// <para>灵敏度 / 模式 / 强度变更只更新属性，<b>不重启采集</b>；只有开关关闭才 StopAsync + Dispose。</para>
    /// <para>可快速连续调用（revision + 信号量串行，只让最后一次请求落地）。</para>
    /// </summary>
    public void ApplyMusicSettings()
    {
        if (_shutdown) return;
        _ = ReconcileMusicAsync();
    }

    private async Task ReconcileMusicAsync()
    {
        var revision = Interlocked.Increment(ref _musicRevision);
        try { await _musicGate.WaitAsync(); }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (_shutdown || revision != Volatile.Read(ref _musicRevision)) return;

            var cfg = _viewModel?.Cfg;
            if (cfg is null)
            {
                await DisposeMusicAsync();
                RaiseMusicStateChanged();
                return;
            }

            // 截图 / 桌宠截图模式绝不启动采集（保持截图流程干净；也不会去碰系统声音）
            var wantRunning = cfg.MusicReactiveEnabled && IsPageOrPetVisible && !IsDebugShotMode;

            if (!wantRunning)
            {
                await DisposeMusicAsync();
                RaiseMusicStateChanged();
                return;
            }

            if (_music is null)
            {
                _music = new MusicReactiveSource();
                _music.Sensitivity = Math.Clamp(cfg.MusicReactiveSensitivity, 0.3, 3);
                _music.StateChanged += OnMusicStateChanged;
                _musicStartError = null;
                _lastLoggedMusicError = null;

                var watch = Stopwatch.StartNew();
                var started = await _music.StartAsync();
                watch.Stop();
                if (revision != Volatile.Read(ref _musicRevision)) { RaiseMusicStateChanged(); return; }

                if (started)
                {
                    _musicStartError = null;
                    AssignMusicToRuntime();
                    var media = DescribeMusicMedia(_music);
                    _viewModel?.Log($"音乐跟唱：音频回环采集已启动（灵敏度 {_music.Sensitivity:0.00}，" +
                                    $"启动耗时 {watch.ElapsedMilliseconds} ms，{(media.Length == 0 ? "SMTC 暂无媒体信息" : media)}）");
                }
                else
                {
                    _musicStartError = _music.LastError ?? "启动失败（没有播放设备 / 被其它程序独占 / 系统不支持回环）";
                    _viewModel?.Log("音乐跟唱启动失败：" + _musicStartError);
                    await DisposeMusicAsync();
                }
            }
            else
            {
                // 已经在跑：配置变更只更新属性（不重启采集）
                _music.Sensitivity = Math.Clamp(cfg.MusicReactiveSensitivity, 0.3, 3);
                AssignMusicToRuntime();
            }

            RaiseMusicStateChanged();
        }
        catch (Exception ex)
        {
            _musicStartError = ex.Message;
            _viewModel?.Log("音乐跟唱异常：" + ex.Message);
            RaiseMusicStateChanged();
        }
        finally
        {
            try { _musicGate.Release(); } catch { /* 已释放 */ }
        }
    }

    /// <summary>把数据源交给模型运行时（运行时只依赖 IMusicReactiveProvider 读快照）。</summary>
    private void AssignMusicToRuntime()
    {
        var runtime = _runtime;
        if (runtime is null) return;
        try { runtime.Music = _music; } catch { /* 模型还没加载完 */ }
    }

    /// <summary>关闭采集并释放（页面 + 桌宠都不可见 / 开关关掉时）。</summary>
    private async Task DisposeMusicAsync()
    {
        var source = _music;
        _music = null;
        if (source is null) { AssignMusicToRuntime(); return; }

        try { source.StateChanged -= OnMusicStateChanged; } catch { /* 忽略 */ }
        try { await source.StopAsync(); } catch { /* 忽略 */ }
        try { source.Dispose(); } catch { /* 忽略 */ }
        AssignMusicToRuntime();
    }

    /// <summary>程序退出 / 主窗口关闭时的同步释放（不能再 await）。</summary>
    private void DisposeMusicBlocking()
    {
        var source = _music;
        _music = null;
        if (source is null) { AssignMusicToRuntime(); return; }

        try { source.StateChanged -= OnMusicStateChanged; } catch { /* 忽略 */ }
        try { source.Dispose(); } catch { /* 忽略 */ }
        AssignMusicToRuntime();
    }

    private void OnMusicStateChanged(object? sender, EventArgs e)
    {
        var error = _music?.LastError;
        if (!string.IsNullOrEmpty(error) && !string.Equals(error, _lastLoggedMusicError, StringComparison.Ordinal))
        {
            _lastLoggedMusicError = error;
            _viewModel?.Log("音乐跟唱状态：" + error);
        }
        _viewModel?.Ui(RaiseMusicStateChanged);
    }

    private void RaiseMusicStateChanged()
    {
        try { MusicStateChanged?.Invoke(this, EventArgs.Empty); } catch { /* 订阅方异常不影响采集 */ }
    }

    /// <summary>配置里的识别模式字符串 → 运行时 enum。</summary>
    private static MusicVocalMode MapMusicMode(string? mode) => mode switch
    {
        "强制跟唱" => MusicVocalMode.ForceVocal,
        "强制纯音乐" => MusicVocalMode.ForceInstrumental,
        _ => MusicVocalMode.Auto,
    };

    /// <summary>媒体信息一句话（歌名 - 歌手 · 播放器应用；都没有返回空串）。</summary>
    private static string DescribeMusicMedia(MusicReactiveSource? source)
    {
        if (source is null) return "";
        var title = source.MediaTitle?.Trim() ?? "";
        var artist = source.MediaArtist?.Trim() ?? "";
        var app = source.MediaApp?.Trim() ?? "";
        if (title.Length == 0 && artist.Length == 0 && app.Length == 0) return "";

        var text = title.Length > 0 ? title : "（未知歌曲）";
        if (artist.Length > 0) text += " - " + artist;
        if (app.Length > 0) text += " · " + app;
        return text;
    }

    // ===================== 开发调试：全身轻晃（以脚为支点）演示 =====================

    /// <summary>
    /// 开发调试（<c>YLT_LIVE2D_LEAN_DEMO</c>）：截图 / 验收用。
    /// <list type="bullet">
    /// <item>值是数字（例如 <c>0.6</c> / <c>-0.6</c>）：只把预览 / 桌宠的倾斜角临时钉住
    /// （<b>仅 UI 渲染层</b>，不改运行时数值、不写配置），方便截 +0.6° / -0.6° 两张相位对比图；</item>
    /// <item>同时把运行时真实 <c>BodyLeanDegrees</c> 采样 1 秒写 <c>Data\live2d\bodylean-demo.log</c>
    /// （证明运行时角度确实在变、UI 跟随的是同一条值）；</item>
    /// <item>带 <c>YLT_LIVE2D_LEAN_DEMO_EXIT=1</c> 时写完日志自动退出（自动验收用）。</item>
    /// </list>
    /// </summary>
    private void TryBeginBodyLeanDemo()
    {
        if (_bodyLeanDemoApplied) return;
        var raw = Environment.GetEnvironmentVariable("YLT_LIVE2D_LEAN_DEMO")?.Trim();
        if (string.IsNullOrEmpty(raw)) return;
        if (_viewModel is null || _runtime?.IsLoaded != true) return;
        _bodyLeanDemoApplied = true;

        double? overrideDegrees = null;
        // "1"/"on"/"true" = 只记录运行时角度（不覆盖 UI 角度）；其它能解析成数字的才固定 UI 倾斜角
        var logOnly = raw is "1" or "on" or "true";
        if (!logOnly && double.TryParse(raw, out var parsed))
        {
            overrideDegrees = Math.Clamp(parsed, -30, 30);
            _viewModel.LeanOverrideDegrees = overrideDegrees;
            _viewModel.SetStatus($"截图模式：倾斜角固定 {overrideDegrees.Value:+0.00;-0.00;0.00}°（仅 UI 演示）");
        }
        else if (!logOnly)
        {
            return;
        }

        _ = ReportBodyLeanAsync(overrideDegrees);
    }

    /// <summary>
    /// 采样运行时 BodyLeanDegrees 写日志（UI 线程每 100ms 读一次，共 2.5 秒）。
    /// 用 UI 线程读是为了和帧循环（Tick）同一个线程、看到的是最新值，不用管跨线程可见性。
    /// </summary>
    private async Task ReportBodyLeanAsync(double? overrideDegrees)
    {
        var viewModel = _viewModel;
        var runtime = _runtime;
        if (viewModel is null || runtime is null) return;

        var exitAfter = Environment.GetEnvironmentVariable("YLT_LIVE2D_LEAN_DEMO_EXIT") is "1" or "on" or "true";
        try
        {
            var lines = new List<string>();
            double min = double.MaxValue, max = double.MinValue;
            double lastTickMs = 0;            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(100);

                double value;
                try { value = runtime.BodyLeanDegrees; }
                catch { continue; }
                if (!double.IsFinite(value)) continue;

                min = Math.Min(min, value);
                max = Math.Max(max, value);
                try { lastTickMs = runtime.LastTickMilliseconds; } catch { /* 忽略 */ }
                lines.Add($"{i * 100,6} ms    {value:+0.000;-0.000;0.000}°");
            }

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("全身轻晃（以脚为支点）演示 / 验收记录");
            builder.AppendLine($"UI 倾斜角覆盖：{(overrideDegrees is { } degrees ? degrees.ToString("+0.00;-0.00;0.00") + "°（仅渲染层，不改运行时 / 不写配置）" : "无（跟随 runtime.BodyLeanDegrees）")}");
            builder.AppendLine($"BodyLeanEnabled={viewModel.Cfg.BodyLeanEnabled}（配置；true = 以脚为轴整身轻倾）");
            builder.AppendLine($"页面在前台={viewModel.Session.IsPageActive}；最近一帧 Tick={lastTickMs:0.000} ms");
            builder.AppendLine(min <= max
                ? $"2.5 秒内 BodyLeanDegrees 范围：{min:+0.000;-0.000;0.000}° ~ {max:+0.000;-0.000;0.000}°"
                : "2.5 秒内没有读到 BodyLeanDegrees");
            builder.AppendLine();
            builder.AppendLine("时刻       BodyLeanDegrees");
            foreach (var line in lines) builder.AppendLine(line);

            try { viewModel.Storage.WriteText("bodylean-demo.log", builder.ToString()); }
            catch (Exception ex) { viewModel.Log("轻晃验证日志写入失败：" + ex.Message); }

            viewModel.Log("轻晃验证：运行时长倾角已写 Data\\live2d\\bodylean-demo.log" +
                          (overrideDegrees is { } fixedDegrees
                              ? $"；UI 倾斜角临时固定 {fixedDegrees:+0.00;-0.00;0.00}°（仅渲染层）"
                              : ""));
        }
        catch (Exception ex)
        {
            viewModel.Log("轻晃验证失败：" + ex.Message);
        }

        if (exitAfter)
        {
            await Task.Delay(200);
            Application.Current?.Shutdown();
        }
    }

    // ===================== 开发调试：音乐链路自测 =====================

    /// <summary>
    /// 开发调试（<c>YLT_LIVE2D_MUSIC_SELFTEST=1</c>）：页面打开后自动启用音乐模块并采样 8 秒，
    /// 把「采集是否启动 / 采样次数 / IsPlaying 占比 / Kind 分布 / 平均 RMS·VocalScore·Confidence·BPM /
    /// 音高有效率 / SMTC 歌名歌手应用 / LastError」写 <c>Data\live2d\music-selftest.log</c>。
    /// <para>只写数字和媒体信息，<b>不保存任何音频</b>；<c>YLT_LIVE2D_MUSIC_SELFTEST_EXIT=1</c> 写完后退出。</para>
    /// </summary>
    private void TryBeginMusicSelfTest()
    {
        if (_musicSelfTestStarted) return;
        if (Environment.GetEnvironmentVariable("YLT_LIVE2D_MUSIC_SELFTEST") is not ("1" or "on" or "true")) return;
        if (_viewModel is null || !IsPageOrPetVisible) return;
        _musicSelfTestStarted = true;
        _ = RunMusicSelfTestAsync();
    }

    private async Task RunMusicSelfTestAsync()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        var lines = new List<string>();
        void Line(string text)
        {
            lines.Add(text);
            viewModel.Log("[音乐自测] " + text);
        }

        var exitAfter = Environment.GetEnvironmentVariable("YLT_LIVE2D_MUSIC_SELFTEST_EXIT") is "1" or "on" or "true";
        var cfg = viewModel.Cfg;
        var originalEnabled = cfg.MusicReactiveEnabled;

        try
        {
            cfg.MusicReactiveEnabled = true;
            viewModel.RefreshAllBindings();
            ApplyRuntimeSettings();
            ApplyMusicSettings();

            // 等采集启动（最多 12 秒；没有播放设备 / 被独占时 LastError 会有原因）
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 12_000 && !IsMusicRunning && string.IsNullOrEmpty(MusicStartError))
            {
                await Task.Delay(100);
            }

            var source = _music;
            if (source is null || !source.IsRunning)
            {
                Line($"FAIL 音频回环采集未启动：{(string.IsNullOrEmpty(MusicStartError) ? "超时（12 秒）" : MusicStartError)}");
                return;
            }

            var mediaAtStart = DescribeMusicMedia(source);
            Line($"OK 音频回环采集已启动：{(mediaAtStart.Length == 0 ? "SMTC 暂无媒体信息" : mediaAtStart)}" +
                 (string.IsNullOrEmpty(source.LastError) ? "；LastError=无" : "；LastError=" + source.LastError));

            // 采样 8 秒：后台线程 + Thread.Sleep 计时稳定；只读 Latest 快照，不接触任何音频数据
            var sample = await Task.Run(() =>
            {
                var total = 0;
                var latestCount = 0;
                var playingCount = 0;
                var pitchValidCount = 0;
                var silentKinds = 0;
                var instrumentalKinds = 0;
                var vocalKinds = 0;
                var unknownKinds = 0;
                double rmsSum = 0, vocalSum = 0, confidenceSum = 0, bpmSum = 0, pitchSum = 0;
                var bpmCount = 0;
                var watched = Stopwatch.StartNew();
                while (watched.ElapsedMilliseconds < 8_000)
                {
                    Thread.Sleep(250);
                    total++;

                    var latest = source.Latest;
                    if (latest is null) continue;
                    latestCount++;
                    if (latest.IsPlaying) playingCount++;
                    switch (latest.Kind)
                    {
                        case MusicContentKind.Silent: silentKinds++; break;
                        case MusicContentKind.Instrumental: instrumentalKinds++; break;
                        case MusicContentKind.VocalSong: vocalKinds++; break;
                        default: unknownKinds++; break;
                    }

                    rmsSum += latest.Rms;
                    vocalSum += latest.VocalScore;
                    confidenceSum += latest.Confidence;
                    if (latest.Bpm > 0) { bpmSum += latest.Bpm; bpmCount++; }
                    if (latest.PitchValid) { pitchValidCount++; pitchSum += latest.PitchHz; }
                }

                return (total, latestCount, playingCount, pitchValidCount, silentKinds, instrumentalKinds,
                    vocalKinds, unknownKinds, rmsSum, vocalSum, confidenceSum, bpmSum, bpmCount, pitchSum);
            });

            var totalDenominator = Math.Max(1, sample.total);
            var latestDenominator = Math.Max(1, sample.latestCount);

            Line($"OK 8 秒采样 {sample.total} 次：Latest 非 null {sample.latestCount}/{sample.total} 次，" +
                 $"IsPlaying 占比 {sample.playingCount * 100.0 / totalDenominator:0.0}%");
            Line($"OK 内容类型分布：静音 {sample.silentKinds} / 纯音乐 {sample.instrumentalKinds} / " +
                 $"带歌词 {sample.vocalKinds} / 未识别 {sample.unknownKinds}（机器没放音乐时全静音属正常）");
            Line($"OK 平均数值：RMS {sample.rmsSum / latestDenominator:0.000}，" +
                 $"VocalScore {sample.vocalSum / latestDenominator:0.000}，" +
                 $"Confidence {sample.confidenceSum / latestDenominator:0.000}，" +
                 $"BPM {(sample.bpmCount > 0 ? (sample.bpmSum / sample.bpmCount).ToString("0.0") : "—")}");
            Line($"OK 音高（YIN）：有效 {sample.pitchValidCount}/{sample.latestCount} 次" +
                 (sample.pitchValidCount > 0 ? $"，平均 {sample.pitchSum / sample.pitchValidCount:0.0} Hz" : "（静音时没有音高属正常）"));
            var mediaAtEnd = DescribeMusicMedia(source);
            Line($"OK SMTC 媒体信息：{(mediaAtEnd.Length == 0 ? "（无，播放器未上报）" : mediaAtEnd)}");
            Line("OK 隐私：仅 Windows 回环采集系统正在播放的声音；未申请麦克风、未录音、本自测未保存任何音频");
        }
        catch (Exception ex)
        {
            Line("EXCEPTION " + ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            // 恢复用户原配置（不留下自动开启的采集）
            cfg.MusicReactiveEnabled = originalEnabled;
            viewModel.RefreshAllBindings();
            ApplyRuntimeSettings();
            ApplyMusicSettings();

            var report = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            try { viewModel.Storage.WriteText("music-selftest.log", report); }
            catch (Exception ex) { viewModel.Log("音乐自测日志写入失败：" + ex.Message); }

            if (exitAfter)
            {
                await Task.Delay(400);
                Application.Current?.Shutdown();
            }
        }
    }

    // ===================== 动作演示（手势 / 表演 / 舞蹈） =====================

    /// <summary>播放一个手势（按钮 / 说话自动小动作 / 桌宠菜单 / 调试钩子共用）。</summary>
    public void PlayGesture(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded)
        {
            _viewModel?.Log("模型还没加载，无法播放手势：" + id);
            return;
        }

        try
        {
            runtime.PlayGesture(id.Trim());
            _viewModel?.Log("播放手势：" + id.Trim());
        }
        catch (Exception ex)
        {
            _viewModel?.Log("播放手势失败：" + ex.Message);
        }
    }

    /// <summary>播放一个表演。</summary>
    public void PlayPerformance(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded)
        {
            _viewModel?.Log("模型还没加载，无法播放表演：" + id);
            return;
        }

        try
        {
            runtime.PlayPerformance(id.Trim());
            _viewModel?.Log("播放表演：" + id.Trim());
        }
        catch (Exception ex)
        {
            _viewModel?.Log("播放表演失败：" + ex.Message);
        }
    }

    /// <summary>开始跳舞。</summary>
    public void StartDance(string mode, double speed)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;

        try
        {
            runtime.StartDance(mode, Math.Clamp(speed, 0.5, 2.0));
            _viewModel?.Log($"开始跳舞：{mode}（速度 {speed:0.00}x）");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("开始跳舞失败：" + ex.Message);
        }
    }

    /// <summary>停止跳舞。</summary>
    public void StopDance()
    {
        try
        {
            _runtime?.StopDance();
            _viewModel?.Log("停止跳舞");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("停止跳舞失败：" + ex.Message);
        }
    }

    /// <summary>停止所有演示（手势 / 表演 / 舞蹈 / 摇晃演示），把参数交还手动控制。</summary>
    public void StopAllDemos()
    {
        try
        {
            _runtime?.StopAllDemos();
            _viewModel?.Log("停止所有演示（手势 / 表演 / 舞蹈 / 摇晃）");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("停止所有演示失败：" + ex.Message);
        }
    }

    /// <summary>随机播一个手势或表演（「🎲 随机动作」/ 桌宠菜单）。</summary>
    public void RandomAction()
    {
        if (Random.Shared.Next(2) == 0) PlayRandomGesture();
        else PlayRandomPerformance();
    }

    /// <summary>随机换一个姿势（手势）。</summary>
    public void PlayRandomGesture()
    {
        // 已经有手势在播时不要硬切：硬切会让手「闪一下掉下去再抬起来」（用户反馈的卡顿）。
        if (_runtime?.ActiveGesture is not null) return;
        var gestures = _viewModel?.GestureActions;
        var id = gestures is { Count: > 0 }
            ? gestures[Random.Shared.Next(gestures.Count)].Id
            : "比心";
        PlayGesture(id);
        _viewModel?.SetStatus("随机手势：" + id);
    }

    /// <summary>随机表演。</summary>
    public void PlayRandomPerformance()
    {
        var performances = _viewModel?.Performances;
        var id = performances is { Count: > 0 }
            ? performances[Random.Shared.Next(performances.Count)].Id
            : "俏皮比心";
        PlayPerformance(id);
        _viewModel?.SetStatus("随机表演：" + id);
    }

    /// <summary>随机跳一段（seconds 秒后自动停；桌宠菜单「跳个舞」用 3 秒）。</summary>
    public void PlayRandomDance(double seconds)
    {
        var modes = _viewModel?.DanceModeOptions;
        var mode = modes is { Count: > 0 }
            ? modes[Random.Shared.Next(modes.Count)].Value
            : "轻快摇摆";
        var speed = Math.Clamp(_viewModel?.DanceSpeed ?? 1.0, 0.5, 2.0);
        StartDance(mode, speed);
        _viewModel?.SetStatus($"跳个舞：{mode}（{seconds:0.#} 秒）");

        var duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 10));
        _ = Task.Delay(duration).ContinueWith(_ =>
        {
            try
            {
                _viewModel?.Ui(() =>
                {
                    try { _runtime?.StopDance(); } catch { /* 忽略 */ }
                });
            }
            catch { /* 忽略 */ }
        }, TaskScheduler.Default);
    }

    // ===================== 说话 / 轻对话 / 聊天气泡（Windows 自带 TTS） =====================

    /// <summary>
    /// 说一句话：先合成，**真正开始播放的那一刻**才显示气泡 + 配小动作，保证声音 / 口型 / 气泡同时出现。
    /// 说话期间帧循环同步 <c>runtime.SpeechMouthOpen</c>。
    /// </summary>
    /// <param name="text">要说的文本（超过 120 字截断）。</param>
    /// <param name="tone">
    /// 语气覆盖（情感风格 / 语速 / 音调 / 音量 / 感叹后停顿）；null = 保持全局设置。
    /// 互动 / 投喂 / AI 回复都会带自己的语气，见 <see cref="SpeechToneCatalog"/>。
    /// </param>
    /// <param name="allowAutoGesture">
    /// 说话时能不能顺手随机配个小动作。互动 / 投喂 / 饥饿台词传 false —— 否则会和互动自己摆的手势
    /// 抢手臂，出现「手闪一下掉下去又抬起来」的卡顿（用户反馈的 bug）。
    /// </param>
    public void SpeakText(string text, SpeechTone? tone = null, bool allowAutoGesture = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.Length > 120) text = text[..120];

        _viewModel?.Log("说话：" + text + (tone?.HasStyle == true ? $"［语气 {tone.Style}］" : ""));
        _ = SpeakAsyncCore(text, null, tone, allowAutoGesture);
    }

    /// <summary>停止说话（气泡稍后隐藏）。</summary>
    public void StopSpeaking()
    {
        try
        {
            _tts?.Stop();
        }
        catch (Exception ex)
        {
            _viewModel?.Log("停止说话失败：" + ex.Message);
        }

        _viewModel?.Log("停止说话");
        HideBubbleSoon();
        RaiseSpeechStateChanged();
    }

    /// <summary>随机说一句（桌宠菜单「说句话」/ 空闲自言自语）。</summary>
    public void SpeakRandomLine()
    {
        var line = Live2DViewModel.PickRandomSpeechLine();
        if (!string.IsNullOrWhiteSpace(line)) SpeakText(line, SpeechToneCatalog.ForIdle());
    }

    /// <summary>把语音相关配置推给 <see cref="TtsService"/>（还没创建时只记着，第一次说话时应用）。</summary>
    public void ApplySpeechSettings()
    {
        var cfg = _viewModel?.Cfg;
        var tts = _tts;
        if (cfg is null || tts is null) return;

        try
        {
            tts.VoiceId = cfg.SpeechVoiceId ?? "";
            tts.Rate = Math.Clamp(cfg.SpeechRate, 0.6, 1.6);
            tts.PitchPercent = Math.Clamp(cfg.SpeechPitchPercent, -20, 40);
            tts.Volume = Math.Clamp(cfg.SpeechVolume, 0, 1);
        }
        catch (Exception ex)
        {
            _viewModel?.Log("应用语音设置失败：" + ex.Message);
        }
    }

    private TtsService? EnsureSpeech()
    {
        if (_tts is not null) return _tts;

        try
        {
            _tts = new TtsService();
            _tts.StateChanged += OnTtsStateChanged;
            ApplySpeechSettings();
        }
        catch (Exception ex)
        {
            _viewModel?.Log("语音初始化失败：" + ex.Message);
            _tts = null;
        }

        return _tts;
    }

    private async Task SpeakAsyncCore(string text, string? gestureId = null, SpeechTone? tone = null,
        bool allowAutoGesture = true)
    {
        var tts = EnsureSpeech();
        if (tts is null)
        {
            RaiseSpeechStateChanged();
            return;
        }

        ApplySpeechSettings();
        try
        {
            // 关键：onPlaybackStarted 在「音频真正开始播放」的一瞬间回调，
            // 气泡与小动作在这里才出现 → 声音 / 口型 / 气泡三者同步。
            var ok = await tts.SpeakAsync(text, tone, () =>
            {
                ShowBubble(text);
                // AI 回复自带动作标签时优先摆那个手势；否则按配置随机配一个小动作
                if (!string.IsNullOrWhiteSpace(gestureId)) PlayGesture(gestureId);
                else if (allowAutoGesture && _viewModel?.Cfg.SpeechAutoGesture == true) PlayRandomGesture();
                RaiseSpeechStateChanged();
            });
            if (!ok)
            {
                var error = tts.LastError;
                if (!string.IsNullOrWhiteSpace(error) && !string.Equals(error, _lastLoggedSpeechError, StringComparison.Ordinal))
                {
                    _lastLoggedSpeechError = error;
                    _viewModel?.Log("语音播放失败：" + error);
                }
            }
        }
        catch (Exception ex)
        {
            _viewModel?.Log("语音播放异常：" + ex.Message);
        }
        finally
        {
            // AI 回复自带的手势：说完话播「退出动作」回正，不要在结束位置突然定住 / 闪回。
            if (!string.IsNullOrWhiteSpace(gestureId))
            {
                try
                {
                    var runtime = _runtime;
                    if (runtime is not null && string.Equals(runtime.ActiveGesture, gestureId, StringComparison.Ordinal))
                        runtime.StopGesture();
                }
                catch { /* 手势退出失败不影响说话 */ }
            }

            RaiseSpeechStateChanged();
            HideBubbleSoon();
        }
    }

    private void OnTtsStateChanged(object? sender, EventArgs e)
    {
        var error = _tts?.LastError;
        if (!string.IsNullOrWhiteSpace(error) && !string.Equals(error, _lastLoggedSpeechError, StringComparison.Ordinal))
        {
            _lastLoggedSpeechError = error;
            _viewModel?.Log("语音状态：" + error);
        }
        try { _viewModel?.Ui(RaiseSpeechStateChanged); } catch { /* 忽略 */ }
    }

    private void RaiseSpeechStateChanged()
    {
        try { SpeechStateChanged?.Invoke(this, EventArgs.Empty); } catch { /* 订阅方异常不影响说话 */ }
    }

    /// <summary>显示聊天气泡（只有桌宠可见时才显示；位置每帧跟随）。</summary>
    private void ShowBubble(string text)
    {
        var viewModel = _viewModel;
        if (viewModel?.Cfg.SpeechBubbleEnabled != true) return;
        if (_pet is not { IsVisible: true }) return;

        try
        {
            var bubble = EnsureBubble();
            if (bubble is null) return;
            bubble.ShowBubble(text);
            UpdateBubblePosition();
        }
        catch (Exception ex)
        {
            viewModel?.Log("聊天气泡显示失败：" + ex.Message);
        }
    }

    private SpeechBubbleWindow? EnsureBubble()
    {
        if (_bubble is { IsLoaded: true }) return _bubble;

        try
        {
            _bubble = new SpeechBubbleWindow();
            _bubble.Closed += (_, _) => _bubble = null;
        }
        catch (Exception ex)
        {
            _viewModel?.Log("聊天气泡窗口创建失败：" + ex.Message);
            _bubble = null;
        }

        return _bubble;
    }

    private void HideBubble()
    {
        try { _bubble?.HideBubble(); } catch { /* 忽略 */ }
    }

    /// <summary>说话结束后约 1.5 秒隐藏气泡（中途又开始说就取消隐藏）。</summary>
    private async void HideBubbleSoon()
    {
        try
        {
            _bubbleHideCts?.Cancel();
        }
        catch { /* 忽略 */ }

        var cts = new CancellationTokenSource();
        _bubbleHideCts = cts;
        try
        {
            await Task.Delay(1500, cts.Token);
            if (!IsSpeaking) HideBubble();
        }
        catch (TaskCanceledException)
        {
            // 又开始说话了：保留气泡
        }
        catch (Exception)
        {
            // 忽略
        }
        finally
        {
            if (ReferenceEquals(_bubbleHideCts, cts)) _bubbleHideCts = null;
            cts.Dispose();
        }
    }

    /// <summary>每帧更新气泡位置（跟随桌宠拖动 / 缩放 / 多屏）。</summary>
    private void UpdateBubblePosition()
    {
        var bubble = _bubble;
        if (bubble is null || !bubble.IsBubbleVisible) return;

        var pet = _pet;
        if (pet is not { IsVisible: true })
        {
            HideBubble();
            return;
        }

        try
        {
            bubble.PlaceAbove(new Rect(pet.Left, pet.Top, pet.ActualWidth, pet.ActualHeight));
        }
        catch
        {
            // 窗口正在切换：忽略
        }
    }

    /// <summary>把说话状态同步给运行时（嘴型由运行时驱动 ParamMouthOpenY；音频线程只写字段）。</summary>
    private void SyncSpeechToRuntime(ModelRuntime runtime)
    {
        try
        {
            runtime.SpeechActive = IsSpeaking;
            runtime.SpeechMouthOpen = (float)SpeechMouthOpen;
        }
        catch
        {
            // 运行时还没实现语音字段：忽略
        }
    }

    /// <summary>空闲随机自言自语：页面 / 桌宠可见、没在说话、没在放音乐时，约 60~120 秒说一句。</summary>
    private void TryIdleChatter()
    {
        var viewModel = _viewModel;
        if (viewModel?.Cfg.SpeechIdleChatterEnabled != true) return;
        if (!IsPageOrPetVisible || IsSpeaking) return;
        if (_music?.IsRunning == true) return;
        // 开了 AI 主动搭话 / 聊天窗开着：交给 AI 那边开口，避免两条链路抢着说话
        if (viewModel.Cfg.AiProactiveEnabled || IsAiChatWindowVisible) return;
        if (_clock.Elapsed < _idleChatterNextAt) return;

        _idleChatterNextAt = _clock.Elapsed + TimeSpan.FromSeconds(Random.Shared.Next(60, 120));
        SpeakRandomLine();
    }

    private void DisposeSpeechBlocking()
    {
        try { _bubbleHideCts?.Cancel(); } catch { /* 忽略 */ }
        _bubbleHideCts = null;

        try
        {
            _bubble?.HideBubble();
            _bubble?.Close();
        }
        catch { /* 忽略 */ }
        _bubble = null;

        try
        {
            if (_tts is not null)
            {
                _tts.StateChanged -= OnTtsStateChanged;
                _tts.Dispose();
            }
        }
        catch { /* 退出流程忽略 */ }
        _tts = null;
    }

    // ===================== AI 聊天 · 好感度（DeepSeek） =====================
    //
    // 数据流：
    //   聊天窗输入 → AiChatService.SendUserMessageAsync → DeepSeek（思考默认关、160 token、只带最近几轮）
    //   → 解析 JSON（r/e/a/d）→ 好感度引擎涨跌 → ReplyReady
    //   → 会话：换表情 / 鼓嘴 + 动作手势 + TTS（声音 / 口型 / 头顶气泡同时出现）+ 聊天窗消息
    //
    // 主动说话：AiChatService.TickSchedule 由这里 2 秒一次的 DispatcherTimer 驱动（页面 / 桌宠 / 聊天窗有人看着时）；
    // 久未回复撒娇 / 定时搭话都由服务内部节流（最小间隔、连续提醒次数上限、不打断正在回复的请求）。

    /// <summary>懒创建 AI 聊天服务（配置回调都是「现读」，改设置立刻生效）。</summary>
    private AiChatService EnsureAiChat()
    {
        if (_aiChat is not null) return _aiChat;

        var vm = EnsureViewModel();
        var service = new AiChatService(new AiChatServiceOptions
        {
            Storage = vm.Storage,
            RequestOptions = BuildAiRequestOptions,
            Persona = BuildAiPersonaSettings,
            Affection = BuildAffectionSettings,
            HistoryTurns = () => Math.Clamp(vm.Cfg.AiHistoryTurns, 0, 20),
            Proactive = BuildAiProactiveSettings,
            OfflineFallback = PickAiOfflineReply,
            Log = message => _viewModel?.Log(message),
        });

        service.StateChanged += OnAiChatStateChanged;
        service.ReplyReady += OnAiReplyReady;
        _aiChat = service;

        try { service.ApplySettings(); }
        catch (Exception ex) { _viewModel?.Log("AI 聊天服务初始化失败：" + ex.Message); }

        return service;
    }

    /// <summary>当前配置 → 请求参数（Key 解密；地址 / 模型空值回默认；token 预算夹取）。</summary>
    private AiRequestOptions BuildAiRequestOptions()
    {
        var cfg = _viewModel?.Cfg;
        var baseUrl = cfg?.AiBaseUrl?.Trim();
        var model = cfg?.AiModel?.Trim();
        return new AiRequestOptions
        {
            ApiKey = GetAiApiKey(),
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? AiRequestOptions.DefaultBaseUrl : baseUrl,
            Model = string.IsNullOrWhiteSpace(model) ? AiRequestOptions.DefaultModel : model,
            Thinking = AiThinkingLevels.FromConfig(cfg?.AiThinkingLevel),
            MaxTokens = Math.Clamp(cfg?.AiMaxTokens ?? 160, 40, 1000),
            JsonOutput = true,
        };
    }

    /// <summary>当前配置 → 人格 / 称呼 / 属性 / 自我设定（自定义称呼、自称优先）。</summary>
    private AiPersonaSettings BuildAiPersonaSettings()
    {
        var cfg = _viewModel?.Cfg;
        return new AiPersonaSettings
        {
            PersonaId = cfg?.AiPersonaId ?? "gentle",
            CustomPersona = cfg?.AiPersonaCustom ?? "",
            AddressPreset = string.IsNullOrWhiteSpace(cfg?.AiAddressPreset) ? "主人" : cfg.AiAddressPreset,
            AddressCustom = cfg?.AiAddressCustom ?? "",
            SelfPreset = string.IsNullOrWhiteSpace(cfg?.AiSelfPreset) ? "我" : cfg.AiSelfPreset,
            SelfCustom = cfg?.AiSelfCustom ?? "",
            Attributes = cfg?.AiAttributes ?? "",
            CustomSetting = cfg?.AiCustomSetting ?? "",
            PetName = "大肥鱼",
        };
    }

    /// <summary>当前配置 → 好感度自然模式规则（数值夹到合理区间）。</summary>
    private AffectionSettings BuildAffectionSettings()
    {
        var cfg = _viewModel?.Cfg;
        return new AffectionSettings
        {
            NaturalMode = cfg?.AiAffectionNatural ?? true,
            FloorEnabled = cfg?.AiAffectionFloorEnabled ?? true,
            Floor = Math.Clamp(cfg?.AiAffectionFloor ?? 20, 0, 100),
            GainPerChat = Math.Clamp(cfg?.AiAffectionGainPerChat ?? 1.0, 0, 5),
            DecayPerDay = Math.Clamp(cfg?.AiAffectionDecayPerDay ?? 3.0, 0, 20),
            DecayAfterHours = Math.Clamp(cfg?.AiAffectionDecayAfterHours ?? 24, 1, 240),
            UseModelDelta = cfg?.AiAffectionModelDelta ?? true,
            Locked = cfg?.AiAffectionLocked ?? false,
        };
    }

    /// <summary>当前配置 → 主动聊天规则（间隔 / 久未回复门限夹取）。</summary>
    private AiProactiveSettings BuildAiProactiveSettings()
    {
        var cfg = _viewModel?.Cfg;
        return new AiProactiveSettings
        {
            Enabled = cfg?.AiProactiveEnabled ?? false,
            IntervalMinutes = Math.Clamp(cfg?.AiProactiveIntervalMinutes ?? 20, 3, 360),
            IdleReminderEnabled = cfg?.AiIdleReminderEnabled ?? true,
            IdleSilenceMinutes = Math.Clamp(cfg?.AiIdleSilenceMinutes ?? 10, 1, 720),
        };
    }

    private void OnAiChatStateChanged(object? sender, EventArgs e) => RaiseAiChatStateChanged();

    private void RaiseAiChatStateChanged()
    {
        try { AiChatStateChanged?.Invoke(this, EventArgs.Empty); } catch { /* 订阅方异常不影响聊天 */ }
    }

    /// <summary>聊天窗里改了配置（主动聊天 / 自然好感度 / 语音）：立刻套用并刷新测试页绑定。</summary>
    private void OnAiChatWindowConfigChanged()
    {
        ApplyAiSettings();
        SaveConfigNow();
        try { _viewModel?.RefreshAllBindings(); } catch { /* 页面没开时忽略 */ }
    }

    private void OnAiChatWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not AiChatWindow window || !ReferenceEquals(window, _aiChatWindow)) return;
        window.FollowToggled -= OnAiChatWindowFollowToggled;
        _aiChatWindow = null;
        if (_viewModel is not null) _viewModel.Cfg.AiChatWindowOpen = false;
        SaveConfigNow();
        UpdateAiTickTimer();
        RaiseAiChatStateChanged();
    }

    /// <summary>聊天窗拖动 / 点 📌：保存「跟随 / 自由摆放」状态；重新开启跟随时立刻贴回桌宠旁边。</summary>
    private void OnAiChatWindowFollowToggled(object? sender, EventArgs e)
    {
        if (sender is not AiChatWindow window) return;
        var vm = _viewModel;
        if (vm is not null)
        {
            vm.Cfg.AiChatWindowFollow = window.FollowPet;
            if (window.FollowPet)
            {
                vm.Cfg.AiChatWindowLeft = -99999;
                vm.Cfg.AiChatWindowTop = -99999;
            }
        }

        if (window.FollowPet) UpdateAiChatWindowPosition();
        SaveConfigNow();
    }

    /// <summary>让主窗口到前面（聊天窗 ⚙ / 桌宠菜单「设置」用；工具工程引用不到外壳的 MainViewModel，只能激活窗口）。</summary>
    private void OpenToolPageFromPet()
    {
        try
        {
            if (Application.Current?.MainWindow is { } mainWindow)
            {
                if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                _viewModel?.Log("已把主窗口叫到前面：侧边栏「桌面 → Live2D 桌宠」里的「AI 聊天」卡可以改设置");
            }
        }
        catch (Exception ex)
        {
            _viewModel?.Log("打开设置失败：" + ex.Message);
        }
    }

    /// <summary>每帧把聊天窗贴到桌宠下方 / 左侧（跟随拖动 / 缩放 / 多屏）；桌宠不在时不重定位，保持原位。</summary>
    private void UpdateAiChatWindowPosition()
    {
        var window = _aiChatWindow;
        if (window is null || !window.IsChatVisible) return;
        var pet = _pet;
        if (pet is not { IsVisible: true }) return;
        try { window.PlaceBelow(pet); } catch { /* 窗口正在切换：忽略 */ }
    }

    /// <summary>
    /// 调试钩子：<c>YLT_LIVE2D_CHAT_POS=left,top</c> 把聊天窗摆成「用户手动位置」，
    /// 用于验收「手动拖动后不会再被每帧拽回桌宠旁边」。
    /// </summary>
    private void TryApplyAiChatDebugPosition()
    {
        var window = _aiChatWindow;
        if (window is null) return;
        var raw = Environment.GetEnvironmentVariable("YLT_LIVE2D_CHAT_POS")?.Trim();
        if (string.IsNullOrEmpty(raw)) return;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var left) ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var top))
        {
            _viewModel?.Log("YLT_LIVE2D_CHAT_POS 格式应为 left,top：" + raw);
            return;
        }

        window.PlaceManually(left, top);
        _viewModel?.Log($"调试：聊天窗已摆到手动位置 ({left:0},{top:0})，不再跟随桌宠");
    }

    /// <summary>按「页面 / 桌宠 / 聊天窗是否有人看着」启停 2 秒一次的调度（AI 掉值/主动说话 + 饱食度结算）。</summary>
    private void UpdateAiTickTimer()
    {
        if (_shutdown) return;

        var need = (_aiChat is not null || _care is not null) && (IsPageOrPetVisible || IsAiChatWindowVisible);
        if (!need)
        {
            _aiTickTimer?.Stop();
            return;
        }

        if (_aiTickTimer is null)
        {
            _aiTickTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _aiTickTimer.Tick += OnAiTick;
        }
        if (!_aiTickTimer.IsEnabled) _aiTickTimer.Start();
    }

    private void OnAiTick(object? sender, EventArgs e)
    {
        // 养成：摸头每日任务（用模型互动计数，关掉「互动语音」也能完成）
        try
        {
            var runtime = _runtime;
            if (runtime is { IsLoaded: true })
            {
                var count = runtime.InteractionEventCount;
                var diff = count - _lastInteractionEventCount;
                _lastInteractionEventCount = count;
                if (diff > 0 && string.Equals(runtime.LastInteractionVoiceEvent, "pat_head", StringComparison.OrdinalIgnoreCase))
                {
                    // 一次调度里连续摸了好几下：按次数补记（最多 8 次，防异常值）
                    for (var i = 0; i < Math.Min(diff, 8); i++)
                        _care?.ReportDailyEvent(DailyTaskKind.PetHead, DateTimeOffset.Now);
                }
            }
        }
        catch (Exception ex)
        {
            _viewModel?.Log("每日任务（摸头）记录失败：" + ex.Message);
        }

        // 养成：饱食度按时间掉值 / 跨天重置每日任务 / 饿了主动说话
        try
        {
            _care?.Tick(DateTimeOffset.Now);
            UpdateHungryReminder();
        }
        catch (Exception ex)
        {
            if (_lastAiError != ex.Message)
            {
                _lastAiError = ex.Message;
                _viewModel?.Log("养成结算出错：" + ex.Message);
            }
        }

        var chat = _aiChat;
        if (chat is null) return;
        try
        {
            chat.TickSchedule(DateTimeOffset.Now, IsPageOrPetVisible || IsAiChatWindowVisible);
        }
        catch (Exception ex)
        {
            if (_lastAiError == ex.Message) return;
            _lastAiError = ex.Message;
            _viewModel?.Log("AI 主动说话调度出错：" + ex.Message);
        }
    }

    /// <summary>拿到 AI 回复：换表情 / 鼓嘴 + 手势 + 说话（声音 / 口型 / 气泡同步）。</summary>
    private void OnAiReplyReady(object? sender, AiReplyEventArgs e)
    {
        var reply = e.Reply;
        if (reply is null || string.IsNullOrWhiteSpace(reply.Text)) return;

        var kindText = e.Proactive switch
        {
            AiProactiveKind.IdleReminder => "（久未回复撒娇）",
            AiProactiveKind.Chatter => "（主动搭话）",
            _ => "",
        };
        _viewModel?.Log($"AI 回复{kindText}：{reply.Text}" +
                        (string.IsNullOrWhiteSpace(reply.Emotion) ? "" : $"［情绪 {reply.Emotion}］") +
                        (string.IsNullOrWhiteSpace(reply.Action) ? "" : $"［动作 {reply.Action}］"));

        var cfg = _viewModel?.Cfg;
        var effects = cfg?.AiMoodEffects == true;
        var gestureId = effects ? PickAiGesture(reply.Action) : "";

        if (effects) ApplyAiMoodEffects(reply, e.Proactive is not null);

        // 每日任务：聊一次（主动搭话不算「用户陪着聊」，只算用户发起的回复）
        if (e.Proactive is null)
        {
            try { _care?.ReportDailyEvent(DailyTaskKind.Chat, DateTimeOffset.Now); }
            catch (Exception ex) { _viewModel?.Log("每日任务（聊天）记录失败：" + ex.Message); }
        }

        if (cfg?.AiSpeakReply == true)
        {
            // 复用既有 TTS 链路：合成完成、真正开始播放时才弹气泡 + 摆动作（声音 / 口型 / 气泡同一时刻）
            _ = SpeakAsyncCore(reply.Text, gestureId, SpeechToneCatalog.ForAiEmotion(reply.Emotion));
        }
        else
        {
            // 不朗读也要有文字气泡：显示一段时间后自动淡出
            ShowBubble(reply.Text);
            HideBubbleSoon();
        }

        RaiseAiChatStateChanged();
    }

    /// <summary>情绪标签 → 表情 / 鼓嘴表现（好感度高容易脸红冒爱心眼，低 / 吃醋容易黑脸鼓嘴）。</summary>
    private void ApplyAiMoodEffects(AiReply reply, bool proactive)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;

        var affection = _aiChat?.Affection;
        var mood = affection?.Mood ?? AiMood.Normal;
        var emotion = NormalizeAiEmotion(reply.Emotion, mood);

        var puff = 0f;
        if (emotion is "angry" or "jealous" or "cold" || mood == AiMood.Angry) puff = 0.65f;
        else if (emotion == "sad") puff = 0.3f;
        else if (mood == AiMood.Sulky) puff = 0.35f;
        try { runtime.MoodPuffAmount = puff; } catch { /* 老模型没有鼓嘴参数：忽略 */ }

        var expression = PickAiExpression(emotion, mood, proactive);
        if (expression is not null)
        {
            ApplyExpression(expression, 0.35);
            _aiExpressionClearAt = _clock.Elapsed + TimeSpan.FromSeconds(emotion == "love" ? 7 : 5);
        }
        else if (puff > 0)
        {
            _aiExpressionClearAt = _clock.Elapsed + TimeSpan.FromSeconds(5);
        }
        else
        {
            ClearExpressions(0.6);
            _aiExpressionClearAt = TimeSpan.MinValue;
        }
    }

    /// <summary>帧循环里到期把 AI 临时表情 / 鼓嘴收回（别一直红着脸）。</summary>
    private void UpdateAiMoodEffectsExpiry()
    {
        if (_aiExpressionClearAt == TimeSpan.MinValue || _clock.Elapsed < _aiExpressionClearAt) return;
        _aiExpressionClearAt = TimeSpan.MinValue;
        try
        {
            if (_runtime is not null) _runtime.MoodPuffAmount = 0f;
            _runtime?.ClearExpressions(0.9);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>情绪标签归一化（模型可能给中文 / 大写 / 空；空 = 按好感度档位推断）。</summary>
    private static string NormalizeAiEmotion(string? emotion, AiMood mood)
    {
        var tag = (emotion ?? "").Trim().ToLowerInvariant();
        if (tag.Length > 0)
        {
            if (tag.Contains("love") || tag.Contains("heart") || tag.Contains("爱")) return "love";
            if (tag.Contains("shy") || tag.Contains("害羞") || tag.Contains("blush")) return "shy";
            if (tag.Contains("jealous") || tag.Contains("醋") || tag.Contains("吃醋")) return "jealous";
            if (tag.Contains("angry") || tag.Contains("生气") || tag.Contains("怒")) return "angry";
            if (tag.Contains("cold") || tag.Contains("黑脸") || tag.Contains("冷")) return "cold";
            if (tag.Contains("sad") || tag.Contains("难过") || tag.Contains("哭")) return "sad";
            if (tag.Contains("surprised") || tag.Contains("惊讶") || tag.Contains("惊")) return "surprised";
            if (tag.Contains("happy") || tag.Contains("开心") || tag.Contains("笑")) return "happy";
            if (tag.Contains("neutral") || tag.Contains("normal") || tag.Contains("平常")) return "neutral";
        }

        return mood switch
        {
            AiMood.Love => "love",
            AiMood.Happy => "happy",
            AiMood.Sulky => "sulky",
            AiMood.Angry => "angry",
            _ => "neutral",
        };
    }

    /// <summary>情绪 → 表情文件（按名字包含匹配；模型没有的表情自动跳过）。</summary>
    private ExpressionEntry? PickAiExpression(string emotion, AiMood mood, bool proactive)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return null;

        var affection = _aiChat?.Affection;
        var candidates = new List<string>();
        switch (emotion)
        {
            case "love":
                candidates.Add("爱心眼");
                candidates.Add("脸红");
                break;
            case "shy":
                candidates.Add("脸红");
                candidates.Add("＞＜");
                break;
            case "happy":
                if (affection?.IsBlushLikely(_aiMoodRandom.NextDouble()) == true) candidates.Add("脸红");
                break;
            case "jealous":
            case "cold":
                candidates.Add("黑脸");
                candidates.Add("屑屑眼");
                break;
            case "angry":
            case "sulky":
                candidates.Add("黑脸");
                candidates.Add("＞＜");
                break;
            case "sad":
                candidates.Add("流泪");
                break;
            case "surprised":
                candidates.Add("圈圈眼");
                break;
        }

        // 好感度高的时候随机脸红 / 爱心眼（自然一点，不是每次都说）
        if (candidates.Count == 0 && affection is not null && affection.Value >= 70 &&
            affection.IsBlushLikely(_aiMoodRandom.NextDouble()))
        {
            candidates.Add(affection.Value >= 85 ? "爱心眼" : "脸红");
        }

        // 主动搭话时表情收敛一点：只在心动档位冒爱心眼
        if (proactive && candidates.Count > 0 && !candidates[0].Contains("爱心眼") &&
            affection?.Value < 80)
        {
            candidates.Clear();
        }

        foreach (var name in candidates)
        {
            var expression = runtime.Expressions.FirstOrDefault(entry =>
                entry.Name.Contains(name, StringComparison.Ordinal));
            if (expression is not null) return expression;
        }
        return null;
    }

    /// <summary>动作标签 → 手势（支持英文别名；none / 空 = 不摆动作，交给自动小动作）。</summary>
    private static string PickAiGesture(string? action)
    {
        var tag = (action ?? "").Trim().ToLowerInvariant();
        return tag switch
        {
            "wave" or "挥手" => "举手打招呼",
            "heart" or "比心" => "比心",
            "akimbo" or "叉腰" => "叉腰",
            "head_hold" or "抱头" => "抱头",
            "point" or "指" => "竖起食指",
            "grass" or "狗尾草" => "拿起狗尾草",
            _ => "",
        };
    }

    // ---------- 离线兜底（没填 API Key / 请求失败时也有得聊） ----------

    private static readonly (string[] Keys, string[] Replies)[] AiOfflineReplyRules =
    {
        (new[] { "你好", "嗨", "hi", "hello", "在吗", "在不在" },
            new[] { "在的在的～叫我做什么呀？", "嗨嗨～我一直在这里喔。" }),
        (new[] { "累", "困", "疲惫", "加班", "辛苦" },
            new[] { "辛苦啦～先休息一下，我陪着你。", "累了就靠一会儿吧，我不吵你。" }),
        (new[] { "开心", "高兴", "好耶", "哈哈" },
            new[] { "嘿嘿，你开心我就开心～", "好耶！今天一定是顺利的一天！" }),
        (new[] { "难过", "伤心", "不开心", "郁闷", "烦" },
            new[] { "抱抱…不开心的事先放一放。", "别难过嘛，我把我最喜欢的星星分你一颗。" }),
        (new[] { "晚安", "睡了", "睡觉" },
            new[] { "晚安，做个好梦～梦里也要有我喔。", "去睡吧，我帮你把今天收好尾巴～" }),
        (new[] { "喜欢你", "爱你", "么么", "亲亲" },
            new[] { "唔…我也最喜欢你啦！…才没有害羞呢。", "诶嘿嘿，这种话要小声一点说啦。" }),
        (new[] { "摸头", "摸摸" },
            new[] { "诶嘿…摸头会让我变笨的啦…再摸一下也不是不行。", "唔…好舒服，再多摸一会儿也可以喔。" }),
        (new[] { "工作", "上班", "学习", "写代码" },
            new[] { "工作再忙也要记得站起来活动一下喔。", "加油！写完这一段我们就休息一下好不好？" }),
    };

    private static readonly string[] AiOfflineFallbackReplies =
    {
        "唔…这样呀，那你想听听我的想法吗？",
        "诶嘿嘿，我好像没太听懂，但我会认真听的～",
        "和你聊天真开心，再多说一点嘛。",
        "嗯嗯，我在呢，继续说嘛～",
        "这个嘛…我可能需要一点点提示～",
    };

    /// <summary>离线兜底回复（关键词匹配；给没填 API Key / 网络失败时用）。</summary>
    private static string PickAiOfflineReply(string userText)
    {
        var text = userText ?? "";
        foreach (var rule in AiOfflineReplyRules)
        {
            foreach (var key in rule.Keys)
            {
                if (text.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return rule.Replies[_sharedRandom.Next(rule.Replies.Length)];
            }
        }
        return AiOfflineFallbackReplies[_sharedRandom.Next(AiOfflineFallbackReplies.Length)];
    }

    private static readonly Random _sharedRandom = new();

    // ---------- 截图冒烟：YLT_LIVE2D_SHOT_CHAT=<png> ----------

    private void TryBeginAiChatShot()
    {
        if (_aiChatShotApplied) return;
        var path = Environment.GetEnvironmentVariable("YLT_LIVE2D_SHOT_CHAT")?.Trim();
        if (string.IsNullOrEmpty(path)) return;
        if (_viewModel is null) return;
        _aiChatShotApplied = true;
        _aiChatShotPath = path;
        _ = RunAiChatShotAsync(path);
    }

    private async Task RunAiChatShotAsync(string path)
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;
        try
        {
            // 先放出桌宠（截图模式同步加载模型），再把聊天窗按「桌宠下方 / 左侧」贴上去，
            // 最后把两个窗口拼成一张桌面视角的图，验证定位 + 试管控件 + 气泡版式。
            ShowPet();
            var waitModel = Stopwatch.StartNew();
            while (waitModel.ElapsedMilliseconds < 15_000 && !IsLoaded) await Task.Delay(100);
            await Task.Delay(500);

            ShowAiChatWindow();
            if (_aiChatWindow is null)
            {
                viewModel.Log("聊天窗截图：聊天窗没打开");
                return;
            }
            _aiChatWindow.LoadDemoMessages();
            await Task.Delay(800);
            UpdateAiChatWindowPosition();

            var pet = _pet;
            var chatRoot = _aiChatWindow.Content as FrameworkElement;
            if (pet is null || chatRoot is null || chatRoot.ActualWidth < 2 || chatRoot.ActualHeight < 2 || !IsLoaded)
            {
                viewModel.Log("聊天窗截图：桌宠 / 聊天窗还没准备好");
                return;
            }

            var petBitmap = pet.RenderWindowBitmap();
            var chatWidth = (int)Math.Ceiling(chatRoot.ActualWidth);
            var chatHeight = (int)Math.Ceiling(chatRoot.ActualHeight);
            var chatVisual = new DrawingVisual();
            using (var dc = chatVisual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24)), null, new Rect(0, 0, chatWidth, chatHeight));
                dc.DrawRectangle(new VisualBrush(chatRoot), null, new Rect(0, 0, chatWidth, chatHeight));
            }
            var chatBitmap = new RenderTargetBitmap(chatWidth, chatHeight, 96, 96, PixelFormats.Pbgra32);
            chatBitmap.Render(chatVisual);

            // 按窗口在屏幕上的相对位置拼图（聊天窗一般在桌宠左侧或下方）
            var minLeft = Math.Min(pet.Left, _aiChatWindow.Left);
            var minTop = Math.Min(pet.Top, _aiChatWindow.Top);
            var maxRight = Math.Max(pet.Left + pet.ActualWidth, _aiChatWindow.Left + chatWidth);
            var maxBottom = Math.Max(pet.Top + pet.ActualHeight, _aiChatWindow.Top + chatHeight);
            var width = (int)Math.Ceiling(Math.Max(1, maxRight - minLeft));
            var height = (int)Math.Ceiling(Math.Max(1, maxBottom - minTop));

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24)), null, new Rect(0, 0, width, height));
                dc.DrawImage(petBitmap, new Rect(pet.Left - minLeft, pet.Top - minTop,
                    pet.ActualWidth, pet.ActualHeight));
                dc.DrawRectangle(new VisualBrush(chatRoot), null, new Rect(
                    _aiChatWindow.Left - minLeft, _aiChatWindow.Top - minTop, chatWidth, chatHeight));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
            viewModel.Log($"聊天窗截图已保存：{path}（{width}×{height}，桌宠 {(int)pet.ActualWidth}×{(int)pet.ActualHeight}，聊天窗 {chatWidth}×{chatHeight}）");
        }
        catch (Exception ex)
        {
            viewModel.Log("聊天窗截图失败：" + ex.Message);
        }
        finally
        {
            await Task.Delay(200);
            Application.Current?.Shutdown();
        }
    }

    /// <summary>程序退出：停 AI 调度、关聊天窗、落盘聊天记录 / 好感度。</summary>
    private void DisposeAiBlocking()
    {
        try { _aiTickTimer?.Stop(); } catch { /* 忽略 */ }
        _aiTickTimer = null;

        try
        {
            if (_aiChatWindow is not null)
            {
                _aiChatWindow.Closed -= OnAiChatWindowClosed;
                _aiChatWindow.HideChat();
                _aiChatWindow.Close();
            }
        }
        catch { /* 退出流程忽略 */ }
        _aiChatWindow = null;

        try
        {
            if (_aiChat is not null)
            {
                _aiChat.StateChanged -= OnAiChatStateChanged;
                _aiChat.ReplyReady -= OnAiReplyReady;
                _aiChat.Dispose();
            }
        }
        catch { /* 退出流程忽略 */ }
        _aiChat = null;
    }

    // ===================== 桌宠鼠标互动（热区事件 → 软妹台词 + 语音/气泡/口型） =====================

    /// <summary>互动热区 → 台词（参考 fulaska 的 phrases.js 语气：短、轻、偶尔嘴硬；同一句尽量不连发）。</summary>
    private static readonly Dictionary<string, string[]> InteractionVoiceLines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pat_head"] = new[]
        {
            "嘿嘿…好舒服呀～", "再摸摸头嘛…", "头发要乱啦～", "摸头会变笨的啦…",
            "唔…好舒服，再多摸一会儿也可以喔。",
        },
        ["face_cycle"] = new[]
        {
            "看我～", "这样好看吗？", "换了个表情，怎么样？", "嘿嘿，有没有被我电到？",
            "这个表情…喜欢吗？",
        },
        ["pinch"] = new[]
        {
            "呀！别、别捏啦……", "呀！脸要变形了啦…", "呜…别捏啦～", "轻一点嘛…",
            "要漏气了要漏气了！", "再捏我就生气了喔…大概。",
        },
        ["tickle"] = new[]
        {
            "哈哈…痒、别挠了！", "别挠啦～", "好痒呀…", "别、别挠那里！", "嘿嘿…不行不行。",
        },
        ["protect"] = new[]
        {
            "呀！不、不给看～", "这里不行哦。", "别一直戳这里…", "衣服要皱了啦。",
            "再碰就咬你了喔！",
        },
        ["hand"] = new[]
        {
            "看我的手指～", "要牵手吗？", "手在这里哦。", "别捏手指啦。", "嗯？怎么了？",
        },
        ["skirt"] = new[] { "裙子！裙子！", "风、风要吹起来了——", "快放下来啦。" },
        ["legs"] = new[] { "痒…别摸腿。", "腿腿不是玩具哦。", "再摸就踢你了。" },
        ["feet"] = new[] { "鞋子会掉的！", "脚、脚底不行…", "凉凉的。" },
        ["waist"] = new[] { "腰很怕痒的。", "别戳腰呀。", "嗯？要量腰围吗？" },
        ["chest"] = new[] { "衣服要皱了啦。", "别一直戳这里…", "这里不行哦。" },
        ["tail"] = new[] { "尾巴的开关在这里哦。", "别、别拽尾巴…", "尾巴不是把手哦。" },
    };

    /// <summary>互动 voice event 的中文名（日志 / 截图验收用）。</summary>
    private static string DescribeInteractionEvent(string voiceEvent) => voiceEvent switch
    {
        "pat_head" => "摸头",
        "face_cycle" => "点脸换表情",
        "pinch" => "捏脸",
        "tickle" => "挠痒",
        "protect" => "护住",
        "hand" => "点手",
        "skirt" => "裙子",
        "legs" => "腿",
        "feet" => "脚",
        "waist" => "腰",
        "chest" => "胸口",
        "tail" => "尾巴",
        _ => voiceEvent,
    };

    /// <summary>运行时热区互动发生（可能在后台线程）→ 切 UI 线程挑台词说话。</summary>
    private void OnInteractionHappened(object? sender, string voiceEvent)
    {
        try
        {
            var viewModel = _viewModel;
            if (viewModel is null) return;
            viewModel.Ui(() => HandleInteractionVoice(voiceEvent));
        }
        catch
        {
            // 互动事件处理失败不影响桌宠
        }
    }

    /// <summary>互动触发（不受互动语音开关影响）：按好感度套用「表情 + 前倾 + 扭动」预设。</summary>
    private void OnInteractionTriggered(object? sender, string voiceEvent)
    {
        try
        {
            var viewModel = _viewModel;
            if (viewModel is null) return;
            viewModel.Ui(() => ApplyInteractionScenarioVisuals(voiceEvent));
        }
        catch
        {
            // 互动反馈失败不影响桌宠
        }
    }

    /// <summary>
    /// 好感度驱动的互动动作：摸头（高好感 → 爱心眼 + 脸红 + 前倾）、摸裙子（脸红 + 扭扭捏捏）…
    /// 数据在 <see cref="InteractionScenarios"/>；低好感只走默认反应，不会爱心眼 / 前倾 / 扭动。
    /// </summary>
    private void ApplyInteractionScenarioVisuals(string? voiceEvent)
    {
        var viewModel = _viewModel;
        var runtime = _runtime;
        if (viewModel is null || runtime is null || runtime.IsLoaded != true) return;
        if (string.IsNullOrWhiteSpace(voiceEvent)) return;

        // 0.9 秒节流：抚摸拖动时别每帧重新套表情 / 重新开始扭
        var now = _clock.Elapsed;
        if (_lastInteractionVisualAt != TimeSpan.MinValue &&
            now - _lastInteractionVisualAt < TimeSpan.FromMilliseconds(900))
        {
            return;
        }

        var affection = _aiChat?.Affection.Value ?? 50.0;
        var scenario = InteractionScenarios.TryMatch(voiceEvent, affection);
        if (scenario is null) return;

        _lastInteractionVisualAt = now;
        foreach (var name in scenario.Expressions)
        {
            var expression = runtime.Expressions.FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (expression is not null) runtime.ApplyExpression(expression, 0.28);
        }

        if (scenario.LeanForward)
        {
            var lean = runtime.Expressions.FirstOrDefault(e =>
                string.Equals(e.Name, "前倾", StringComparison.OrdinalIgnoreCase));
            if (lean is not null) runtime.ApplyExpression(lean, 0.6);
        }

        if (scenario.WiggleSeconds > 0) runtime.StartShyWiggle(scenario.WiggleSeconds);

        viewModel.Log(
            $"互动预设[{scenario.Id}]：好感 {affection:0} · 表情 {string.Join("+", scenario.Expressions)}" +
            (scenario.LeanForward ? " · 前倾" : "") +
            (scenario.WiggleSeconds > 0 ? $" · 扭动 {scenario.WiggleSeconds:0.0}s" : ""));
    }

    private void HandleInteractionVoice(string? voiceEvent)
    {
        var viewModel = _viewModel;
        if (viewModel is null || string.IsNullOrWhiteSpace(voiceEvent)) return;

        _lastInteractionVoiceEvent = voiceEvent;

        if (viewModel.Cfg.PetInteractionVoiceEnabled != true) return;

        // 1.5 秒节流：连续点 / 抚摸时别把语音播成复读机
        var now = _clock.Elapsed;
        if (_lastInteractionVoiceAt != TimeSpan.MinValue &&
            now - _lastInteractionVoiceAt < TimeSpan.FromMilliseconds(1500))
        {
            return;
        }

        var affection = _aiChat?.Affection.Value ?? 50.0;
        var scenario = InteractionScenarios.TryMatch(voiceEvent, affection);
        var line = scenario is not null
            ? InteractionScenarios.PickLine(scenario, _lastInteractionVoiceLine, Random.Shared)
            : PickInteractionVoiceLine(voiceEvent);
        if (string.IsNullOrEmpty(line)) return;

        _lastInteractionVoiceAt = now;
        _lastInteractionVoiceLine = line;
        var name = DescribeInteractionEvent(voiceEvent!);
        viewModel.Log($"桌宠互动语音（{name}，好感 {affection:0}，{(scenario is null ? "默认" : scenario.Id)}）：{line}");
        if (scenario is not null)
        {
            try { EnsureAiChat().AppendLocalPetMessage(line); } catch { /* 聊天服务没准备好就只说话 */ }
        }
        // 互动台词不再顺手随机配手势：否则会和互动自己摆的手势抢手臂（手闪一下的 bug）
        SpeakText(line, SpeechToneCatalog.ForInteraction(voiceEvent), allowAutoGesture: false);
    }

    private string PickInteractionVoiceLine(string voiceEvent)
    {
        if (!InteractionVoiceLines.TryGetValue(voiceEvent, out var lines) || lines.Length == 0)
        {
            return "";
        }
        if (lines.Length == 1) return lines[0];

        for (var i = 0; i < 6; i++)
        {
            var line = lines[Random.Shared.Next(lines.Length)];
            if (!string.Equals(line, _lastInteractionVoiceLine, StringComparison.Ordinal)) return line;
        }
        return lines[0];
    }

    /// <summary>
    /// 调试钩子（截图验收）：<c>YLT_LIVE2D_INTERACTION_DEMO=&lt;region 或 voice event&gt;</c>。
    /// 打开页面 / 桌宠后取区域中心模拟一次「按下 → 抚摸拖动 → 松开」，日志打印区域名 / 画布坐标 /
    /// 语音事件 / 是否说话；若同时给 <c>YLT_LIVE2D_INTERACTION_SHOT=&lt;png&gt;</c> 则把桌宠窗口离屏
    /// 渲染成 PNG 后退出（<b>只截桌宠窗口位图，不抓桌面</b>）。
    /// </summary>
    private void TryBeginInteractionDemo()
    {
        if (_interactionDemoApplied) return;
        var raw = Environment.GetEnvironmentVariable("YLT_LIVE2D_INTERACTION_DEMO")?.Trim();
        if (string.IsNullOrEmpty(raw)) return;
        if (_viewModel is null || _runtime?.IsLoaded != true) return;
        _interactionDemoApplied = true;
        _ = RunInteractionDemoAsync(raw);
    }

    private async Task RunInteractionDemoAsync(string raw)
    {
        var viewModel = _viewModel;
        var runtime = _runtime;
        if (viewModel is null || runtime is null) return;

        var shotPath = Environment.GetEnvironmentVariable("YLT_LIVE2D_INTERACTION_SHOT")?.Trim() ?? "";
        var exitAfter = Environment.GetEnvironmentVariable("YLT_LIVE2D_INTERACTION_DEMO_EXIT") is "1" or "on" or "true"
                        || shotPath.Length > 0;

        // 先把桌宠放出来（截图 / 互动都需要桌宠窗口在渲染）
        try
        {
            if (!IsPetVisible) ShowPet();
        }
        catch (Exception ex)
        {
            viewModel.Log("互动演示：打开桌宠失败：" + ex.Message);
        }
        await Task.Delay(350);

        var region = ResolveInteractionRegion(raw, runtime);
        var regionName = raw;
        var canvasX = 0f;
        var canvasY = 0f;
        var voiceEvent = "";
        var pointerDown = false;

        // 额头 / 手 / 胸是「点按」型反应；头 / 脸 / 腰 / 腿 / 脚 / 尾巴是「抚摸 / 捏」型（需要累计移动步长）
        var tapStyle = region is InteractionRegion.Forehead or InteractionRegion.HandLeft
            or InteractionRegion.HandRight or InteractionRegion.Chest;

        var canvasWidth = 1000f;
        try { canvasWidth = Math.Max(100f, runtime.Model.CanvasWidthPixels); } catch { /* 忽略 */ }

        try
        {
            regionName = runtime.GetRegionDisplayName(region);
            if (runtime.GetRegionCenter(region, out var cx, out var cy))
            {
                canvasX = cx;
                canvasY = cy;
            }

            pointerDown = runtime.InteractionPointerDown(canvasX, canvasY);

            if (tapStyle)
            {
                // 点按：按下后快速抬起（运行时判定 tap 需要 <0.42s 且位移很小）
                await Task.Delay(90);
            }
            else
            {
                // 抚摸 / 捏脸：分几段拖动累积步长（抚摸每 3% 画布宽算一步；捏脸需要拖到 ~7% 才明显）
                var step = region is InteractionRegion.FaceLeft or InteractionRegion.FaceRight
                    ? Math.Max(40f, canvasWidth * 0.045f)
                    : Math.Max(40f, canvasWidth * 0.035f);
                for (var i = 0; i < 4; i++)
                {
                    await Task.Delay(80);
                    var dx = step * (i + 1) * 0.7f;   // 往一个方向拉开，捏脸 / 顺毛都更明显
                    try { runtime.InteractionPointerMove(canvasX + dx, canvasY + 4f); } catch { /* 忽略 */ }
                }
                await Task.Delay(200);
            }

            try { voiceEvent = runtime.LastInteractionVoiceEvent ?? ""; } catch { /* 忽略 */ }
        }
        catch (Exception ex)
        {
            viewModel.Log("互动演示按下失败：" + ex.Message);
        }

        if (tapStyle)
        {
            // 点按：抬起 → 触发反应（换表情 / 护住 / 竖食指）→ 等表情淡入后再截图
            await Task.Delay(60);
            try { runtime.InteractionPointerUp(canvasX, canvasY); } catch { /* 忽略 */ }
            await Task.Delay(550);
            try { voiceEvent = runtime.LastInteractionVoiceEvent ?? ""; } catch { /* 忽略 */ }
            if (shotPath.Length > 0) await CaptureInteractionShotAsync(shotPath, viewModel);
        }
        else
        {
            // 抚摸 / 捏脸：反应还在按住期间 → 先截图（捏脸变形最明显），再松开
            try { voiceEvent = runtime.LastInteractionVoiceEvent ?? ""; } catch { /* 忽略 */ }
            if (shotPath.Length > 0) await CaptureInteractionShotAsync(shotPath, viewModel);
            await Task.Delay(150);
            try { runtime.InteractionPointerUp(canvasX, canvasY); } catch { /* 忽略 */ }
            await Task.Delay(350);
            if (string.IsNullOrEmpty(voiceEvent))
            {
                try { voiceEvent = runtime.LastInteractionVoiceEvent ?? ""; } catch { /* 忽略 */ }
            }
        }

        await Task.Delay(150);
        viewModel.Log($"截图模式（YLT_LIVE2D_INTERACTION_DEMO）：输入={raw}，区域={regionName}，" +
                      $"画布坐标=({canvasX:0},{canvasY:0})，按下命中={pointerDown}，临时点击型={tapStyle}，" +
                      $"语音事件={(voiceEvent.Length > 0 ? voiceEvent : "（无）")}，" +
                      $"是否在说话={IsSpeaking}，语音开关={viewModel.Cfg.PetInteractionVoiceEnabled}");

        try
        {
            var interactionName = "";
            try { interactionName = runtime.LastInteractionName ?? ""; } catch { /* 忽略 */ }
            var report =
                "桌宠鼠标互动演示 / 验收记录" + Environment.NewLine +
                $"输入={raw}；区域={regionName}；画布坐标=({canvasX:0},{canvasY:0})；按下命中={pointerDown}；" +
                $"路径={(tapStyle ? "点按（tap）" : "抚摸 / 捏（pet）")}" + Environment.NewLine +
                $"LastInteractionName={(interactionName.Length > 0 ? interactionName : "（空）")}" + Environment.NewLine +
                $"LastInteractionVoiceEvent={(voiceEvent.Length > 0 ? voiceEvent : "（空）")}" + Environment.NewLine +
                $"会话挑选的台词={_lastInteractionVoiceLine ?? "（没有说话 / 语音开关关闭）"}" + Environment.NewLine +
                $"是否在说话={IsSpeaking}；语音开关={viewModel.Cfg.PetInteractionVoiceEnabled}" + Environment.NewLine +
                "说明：调试钩子模拟「按下 → 抚摸移动 / 点按 → 抬起」，只读取模型状态与媒体信息，不保存任何音频。";
            viewModel.Storage.WriteText("interaction-demo.log", report);
        }
        catch (Exception ex)
        {
            viewModel.Log("互动演示日志写入失败：" + ex.Message);
        }

        if (exitAfter)
        {
            await Task.Delay(250);
            Application.Current?.Shutdown();
        }
    }

    /// <summary>互动演示截图：只把桌宠窗口离屏渲染成 PNG（不抓桌面 / 不包含其他窗口）。</summary>
    private async Task CaptureInteractionShotAsync(string path, Live2DViewModel viewModel)
    {
        try
        {
            await Task.Delay(120);
            if (_pet is { IsVisible: true } pet)
            {
                var bitmap = pet.RenderWindowBitmap();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                await using var stream = File.Create(path);
                encoder.Save(stream);
                viewModel.Log($"互动演示截图已保存：{path}（{bitmap.PixelWidth}×{bitmap.PixelHeight}，仅桌宠窗口离屏位图）");
            }
            else
            {
                viewModel.Log("互动演示截图失败：桌宠窗口没打开");
            }
        }
        catch (Exception ex)
        {
            viewModel.Log("互动演示截图失败：" + ex.Message);
        }
    }

    /// <summary>把调试输入（region 英文 / 中文 / voice event）映射成 <see cref="InteractionRegion"/>。</summary>
    private static InteractionRegion ResolveInteractionRegion(string raw, ModelRuntime runtime)
    {
        try
        {
            if (Enum.TryParse<InteractionRegion>(raw, ignoreCase: true, out var parsed) && parsed != InteractionRegion.None)
            {
                return parsed;
            }
        }
        catch
        {
            // 运行时还没实现枚举：走下面的字符串映射
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "pat_head" or "摸头" => InteractionRegion.Head,
            "face_cycle" or "爱心眼" or "换表情" => InteractionRegion.Forehead,
            "pinch" or "捏脸" => InteractionRegion.FaceLeft,
            "tickle" or "挠痒" or "摸腰" => InteractionRegion.Waist,
            "protect" or "护住" or "摸胸" => InteractionRegion.Chest,
            "hand" or "点手" => InteractionRegion.HandRight,
            "skirt" or "裙子" => InteractionRegion.Skirt,
            "legs" or "腿" => InteractionRegion.Legs,
            "feet" or "脚" => InteractionRegion.Feet,
            "tail" or "尾巴" => InteractionRegion.Tail,
            _ => InteractionRegion.Head,
        };
    }

    // ===================== 开发调试：动作 / 语音 / 气泡截图 =====================

    /// <summary>调试钩子：<c>YLT_LIVE2D_GESTURE_DEMO</c> / <c>PERFORMANCE_DEMO</c> / <c>DANCE_DEMO</c> 自动播一个动作（截图验收用）。</summary>
    private void TryBeginActionDemo()
    {
        if (_actionDemoApplied) return;
        var runtime = _runtime;
        if (runtime?.IsLoaded != true) return;

        var gesture = Environment.GetEnvironmentVariable("YLT_LIVE2D_GESTURE_DEMO")?.Trim();
        var performance = Environment.GetEnvironmentVariable("YLT_LIVE2D_PERFORMANCE_DEMO")?.Trim();
        var dance = Environment.GetEnvironmentVariable("YLT_LIVE2D_DANCE_DEMO")?.Trim();
        if (string.IsNullOrEmpty(gesture) && string.IsNullOrEmpty(performance) && string.IsNullOrEmpty(dance)) return;

        _actionDemoApplied = true;
        try
        {
            if (!string.IsNullOrEmpty(gesture))
            {
                runtime.PlayGesture(gesture);
                _viewModel?.Log("截图模式：播放手势 " + gesture);
                _viewModel?.SetStatus("截图模式：手势 " + gesture);
            }
            if (!string.IsNullOrEmpty(performance))
            {
                runtime.PlayPerformance(performance);
                _viewModel?.Log("截图模式：播放表演 " + performance);
                _viewModel?.SetStatus("截图模式：表演 " + performance);
            }
            if (!string.IsNullOrEmpty(dance))
            {
                runtime.StartDance(dance, Math.Clamp(_viewModel?.DanceSpeed ?? 1.0, 0.5, 2.0));
                _viewModel?.Log("截图模式：开始跳舞 " + dance);
                _viewModel?.SetStatus("截图模式：跳舞 " + dance);
            }
        }
        catch (Exception ex)
        {
            _viewModel?.Log("截图模式动作演示失败：" + ex.Message);
        }
    }

    /// <summary>调试钩子：<c>YLT_LIVE2D_SPEECH_SELFTEST=1</c>（+ <c>_EXIT=1</c>）合成并播放固定台词，
    /// 采样 IsSpeaking / MouthOpen 写 <c>Data\live2d\speech-selftest.log</c>（只写数字，不保存音频）。</summary>
    private void TryBeginSpeechSelfTest()
    {
        if (_speechSelfTestStarted) return;
        if (Environment.GetEnvironmentVariable("YLT_LIVE2D_SPEECH_SELFTEST") is not ("1" or "on" or "true")) return;
        if (_viewModel is null || !IsPageOrPetVisible) return;
        _speechSelfTestStarted = true;
        _ = RunSpeechSelfTestAsync();
    }

    private async Task RunSpeechSelfTestAsync()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        var lines = new List<string>();
        void Line(string text)
        {
            lines.Add(text);
            viewModel.Log("[语音自测] " + text);
        }

        var exitAfter = Environment.GetEnvironmentVariable("YLT_LIVE2D_SPEECH_SELFTEST_EXIT") is "1" or "on" or "true";
        var tts = EnsureSpeech();
        if (tts is null)
        {
            Line("FAIL 语音初始化失败：" + viewModel.SpeechVoiceError);
        }
        else
        {
            ApplySpeechSettings();
            var onlineCount = TtsService.GetVoices().Count(v => v.Id.StartsWith("edge:", StringComparison.Ordinal));
            var localCount = TtsService.GetVoices().Count(v => v.Id.StartsWith("win:", StringComparison.Ordinal));
            Line($"OK 语音服务已初始化：VoiceId={(string.IsNullOrWhiteSpace(tts.VoiceId) ? "（自动：晓伊 · 少女音）" : tts.VoiceId)}，" +
                 $"Rate={tts.Rate:0.00} Pitch={tts.PitchPercent:0} Volume={tts.Volume:0.00}；可用语音 在线 {onlineCount} + 本地 {localCount}");

            var callbackAtMs = -1L;
            var playWatch = Stopwatch.StartNew();
            var spoken = await tts.SpeakAsync("你好呀，我是大肥鱼，今天也要开心喔。",
                () => callbackAtMs = playWatch.ElapsedMilliseconds);
            if (!spoken)
            {
                Line("FAIL 说话失败：" + (tts.LastError ?? "未知原因"));
            }
            else
            {
                Line($"OK 引擎={tts.LastEngine}；播放开始回调={callbackAtMs}ms（生产路径在同一回调里显示气泡 + 开始口型，三者同步）");
                Line("OK SpeakAsync 返回成功，开始采样 6 秒（只写数字，不保存音频）");
                var speakingSamples = 0;
                var total = 0;
                var maxMouth = 0.0;
                var sumMouth = 0.0;
                var mouthSamples = 0;
                var firstSpeakAt = -1.0;
                var lastSpeakAt = -1.0;
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 6000)
                {
                    await Task.Delay(100);
                    total++;
                    var speaking = IsSpeaking;
                    var mouth = SpeechMouthOpen;
                    if (speaking)
                    {
                        speakingSamples++;
                        if (firstSpeakAt < 0) firstSpeakAt = watch.Elapsed.TotalSeconds;
                        lastSpeakAt = watch.Elapsed.TotalSeconds;
                    }
                    if (mouth > 0.0001)
                    {
                        mouthSamples++;
                        sumMouth += mouth;
                        if (mouth > maxMouth) maxMouth = mouth;
                    }
                    if (!speaking && watch.ElapsedMilliseconds > 1200 && firstSpeakAt >= 0) break;
                }

                Line($"OK 采样 {total} 次：IsSpeaking 占比 {speakingSamples * 100.0 / Math.Max(1, total):0.0}% " +
                     $"(首次 {firstSpeakAt:0.0}s，最后 {lastSpeakAt:0.0}s)");
                Line($"OK 嘴型：非零比例 {mouthSamples * 100.0 / Math.Max(1, total):0.0}%，" +
                     $"平均 {(mouthSamples > 0 ? sumMouth / mouthSamples : 0):0.000}，最大 {maxMouth:0.000}");
                Line($"OK 说话文本={tts.LastSpokenText}");
                Line($"OK LastError={(string.IsNullOrWhiteSpace(tts.LastError) ? "无" : tts.LastError)}");
                Line("OK 隐私：默认微软 Edge 在线神经网络语音（只发送要说的文本，不上传桌面/音频、不录音、不保存音频）；" +
                     "离线可在语音列表选「本地」语音");
            }
        }

        var report = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        try { viewModel.Storage.WriteText("speech-selftest.log", report); }
        catch (Exception ex) { viewModel.Log("语音自测日志写入失败：" + ex.Message); }
        RaiseSpeechStateChanged();

        if (exitAfter)
        {
            await Task.Delay(400);
            Application.Current?.Shutdown();
        }
    }

    /// <summary>调试钩子：<c>YLT_LIVE2D_BUBBLE_SHOT=&lt;png&gt;</c> 截「桌宠 + 头顶气泡」合成图
    /// （离屏合成两张位图，不抓桌面，避免截到用户其它窗口）。</summary>
    private void TryBeginBubbleShot()
    {
        if (_bubbleShotApplied) return;
        var path = Environment.GetEnvironmentVariable("YLT_LIVE2D_BUBBLE_SHOT")?.Trim();
        if (string.IsNullOrEmpty(path)) return;
        if (_viewModel is null || _runtime?.IsLoaded != true) return;

        _bubbleShotApplied = true;
        _bubbleShotPath = path;
        _ = RunBubbleShotAsync(path);
    }

    private async Task RunBubbleShotAsync(string path)
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        try
        {
            ShowPet();
            await Task.Delay(500);
            SpeakText("你好呀～今天也要开心喔！");

            // 在线语音要先合成（约 1~3 秒），等「真正开始播放」后再截，保证气泡已经显示出来
            var waitSpeech = Stopwatch.StartNew();
            while (waitSpeech.ElapsedMilliseconds < 12_000 && !IsSpeaking)
            {
                await Task.Delay(100);
            }
            await Task.Delay(900);

            var pet = _pet;
            var bubble = _bubble;
            if (pet is null) { viewModel.Log("气泡截图：桌宠没打开"); return; }

            var petBitmap = pet.RenderWindowBitmap();
            var bubbleBitmap = bubble?.RenderBubbleBitmap();

            var bubbleWidth = bubbleBitmap?.PixelWidth ?? 0;
            var bubbleHeight = bubbleBitmap?.PixelHeight ?? 0;
            var width = Math.Max(petBitmap.PixelWidth, bubbleWidth);
            var height = petBitmap.PixelHeight + bubbleHeight + 8;

            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1F, 0x24)),
                    null, new Rect(0, 0, width, height));
                if (bubbleBitmap is not null)
                {
                    dc.DrawImage(bubbleBitmap,
                        new Rect((width - bubbleWidth) / 2.0, 0, bubbleWidth, bubbleHeight));
                }
                dc.DrawImage(petBitmap,
                    new Rect((width - petBitmap.PixelWidth) / 2.0, bubbleHeight + 8, petBitmap.PixelWidth, petBitmap.PixelHeight));
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            await using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            viewModel.Log($"气泡截图已保存：{path}（气泡 {bubbleWidth}×{bubbleHeight}，桌宠 {petBitmap.PixelWidth}×{petBitmap.PixelHeight}）");
        }
        catch (Exception ex)
        {
            viewModel.Log("气泡截图失败：" + ex.Message);
        }
        finally
        {
            await Task.Delay(200);
            Application.Current?.Shutdown();
        }
    }

    // ===================== 开发调试：动捕链路自测 =====================

    /// <summary>
    /// 开发调试（<c>YLT_LIVE2D_MOCAP_SELFTEST=1</c>）：页面打开后自动挑内置摄像头开动捕、跑 6 秒，
    /// 把「枚举 / 启动 / FPS / 帧尺寸 / 处理耗时 / 有脸次数 / 预览次数 / 停止」逐项写成
    /// <c>Data\live2d\mocap-selftest.log</c>（只写数字和设备名，<b>不写摄像头画面</b>）。
    /// 加 <c>YLT_LIVE2D_MOCAP_SELFTEST_EXIT=1</c> 时写完后退出程序（自动验收用）。
    /// </summary>
    private void TryBeginMocapSelfTest()
    {
        if (_mocapSelfTestStarted) return;
        if (Environment.GetEnvironmentVariable("YLT_LIVE2D_MOCAP_SELFTEST") is not ("1" or "on" or "true")) return;
        if (_viewModel is null || !IsPageOrPetVisible) return;
        _mocapSelfTestStarted = true;
        _ = RunMocapSelfTestAsync();
    }

    private async Task RunMocapSelfTestAsync()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        var lines = new List<string>();
        void Line(string text)
        {
            lines.Add(text);
            viewModel.Log("[动捕自测] " + text);
        }

        var exitAfter = Environment.GetEnvironmentVariable("YLT_LIVE2D_MOCAP_SELFTEST_EXIT") is "1" or "on" or "true";
        var cfg = viewModel.Cfg;
        var originalEnabled = cfg.MocapEnabled;

        try
        {
            // ① 枚举摄像头
            var cameras = await MocapCameraSource.EnumerateCamerasAsync();
            Line(cameras.Count > 0
                ? $"OK 枚举摄像头：{cameras.Count} 个（{string.Join("、", cameras.Select(camera => camera.Name))}）"
                : "FAIL 枚举摄像头：0 个（检查设备管理器 / Windows 隐私设置是否允许桌面应用访问摄像头）");
            if (cameras.Count == 0) return;

            // ② 挑摄像头：优先内置 "HD User Facing"，尽量避开 DroidCam / 虚拟摄像头
            var chosen = Live2DViewModel.PickPreferredCamera(cameras) ?? cameras[0];
            cfg.MocapCameraId = chosen.Id;
            cfg.MocapCameraName = chosen.Name;
            cfg.MocapEnabled = true;
            cfg.MocapShowPreview = true;
            viewModel.RefreshAllBindings();
            _ = viewModel.EnsureCamerasLoadedAsync(force: true);
            ApplyRuntimeSettings();
            ApplyMocapSettings();

            // ③ 等启动（最多 12 秒；失败时 MocapStartError 有原因）
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 12_000 && !IsMocapRunning && string.IsNullOrEmpty(MocapStartError))
            {
                await Task.Delay(100);
            }

            var source = _mocap;
            if (source is null || !source.IsRunning)
            {
                Line($"FAIL StartAsync：未运行（{MocapStartError}{(string.IsNullOrEmpty(MocapStartError) ? "超时" : "")}）");
                return;
            }

            Line($"OK StartAsync：{chosen.Name}；LastError={(source.LastError ?? "无")}");

            // ④ 采样 6 秒（只统计数字，不保存画面）。
            // 放在后台线程上用 Thread.Sleep 计时（不用 Task.Delay：UI/线程池忙时定时器续体会被拖延，
            // 之前实测 6 秒只跑出 7 次采样；独占线程的 Sleep 不受影响，能稳定 200ms 一次）。
            var sample = await Task.Run(() =>
            {
                var total = 0;
                var latestCount = 0;
                var faceCount = 0;
                var previewCount = 0;
                long previewB = 0, previewG = 0, previewR = 0;
                var previewColorSamples = 0;
                double maxFpsLocal = 0, maxProcessLocal = 0, maxDetectLocal = 0;
                var frameSizeLocal = (0, 0);
                var sampled = Stopwatch.StartNew();
                while (sampled.ElapsedMilliseconds < 6_000)
                {
                    Thread.Sleep(200);
                    total++;

                    var latest = source.Latest;
                    if (latest is not null) latestCount++;
                    if (latest?.HasFace == true) faceCount++;
                    var preview = source.LatestPreview;
                    if (preview is not null)
                    {
                        previewCount++;
                        // 颜色自检：稀疏采样算 RGB 均值（只算数字，不保存画面）。
                        // 修复后三通道应接近（灰调画面）；若把 YUV 当 BGRA 读会 G 明显偏高、B/R 偏低（历史「绿油油」bug）。
                        for (var i = 0; i + 3 < preview.Bgra.Length; i += 4 * 37)
                        {
                            previewB += preview.Bgra[i];
                            previewG += preview.Bgra[i + 1];
                            previewR += preview.Bgra[i + 2];
                            previewColorSamples++;
                        }
                    }

                    maxFpsLocal = Math.Max(maxFpsLocal, source.MeasuredFps);
                    var size = source.FrameSize;
                    if (size.Item1 > 0) frameSizeLocal = size;
                    maxProcessLocal = Math.Max(maxProcessLocal, source.LastProcessMilliseconds);
                    maxDetectLocal = Math.Max(maxDetectLocal, source.LastDetectMilliseconds);
                }

                return (total, latestCount, faceCount, previewCount,
                    maxFpsLocal, maxProcessLocal, maxDetectLocal, frameSizeLocal,
                    previewB, previewG, previewR, previewColorSamples);
            });

            var (total, latestCount, faceCount, previewCount, maxFps, maxProcessMs, maxDetectMs, frameSize,
                previewB, previewG, previewR, previewColorSamples) = sample;

            Line($"OK 6 秒采样 {total} 次：MeasuredFps 最大 {maxFps:0.0}；FrameSize {frameSize.Item1}×{frameSize.Item2}；" +
                 $"单帧处理耗时最大 {maxProcessMs:0.0} ms（其中人脸检测 {maxDetectMs:0.0} ms）");
            Line((latestCount > 0 ? "OK" : "FAIL") + $" Latest 非 null：{latestCount}/{total} 次");
            Line($"OK 人脸采样：{faceCount}/{total} 次（房间没人时 0 也正常，这条只验证检测管线在跑）");
            Line((previewCount > 0 ? "OK" : "FAIL") +
                 $" LatestPreview 非 null：{previewCount}/{total} 次（已启用预览，应该有）");
            if (previewColorSamples > 0)
            {
                var meanB = previewB / (double)previewColorSamples;
                var meanG = previewG / (double)previewColorSamples;
                var meanR = previewR / (double)previewColorSamples;
                // 灰调画面三通道应接近；早先「把 YUV 当 BGRA 读」的 bug 会让 G 远高于 B/R（画面发绿）。
                var colorBalanced = Math.Abs(meanG - (meanB + meanR) / 2) < 45;
                Line($"{(colorBalanced ? "OK" : "WARN")} 预览颜色：平均 B={meanB:0.0} G={meanG:0.0} R={meanR:0.0}（三通道接近 = 颜色正常；G 明显偏高 = 发绿错色）");
            }

            // ⑤ 停止
            var stopWatch = Stopwatch.StartNew();
            await source.StopAsync();
            stopWatch.Stop();
            Line((!source.IsRunning ? "OK" : "FAIL") +
                 $" StopAsync：耗时 {stopWatch.ElapsedMilliseconds} ms，IsRunning={source.IsRunning}");
        }
        catch (Exception ex)
        {
            Line("EXCEPTION " + ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            // 恢复用户原来的配置（隐私：自测完不留下开着的摄像头）
            cfg.MocapEnabled = originalEnabled;
            ApplyRuntimeSettings();
            ApplyMocapSettings();
            viewModel.RefreshAllBindings();

            var report = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            try { viewModel.Storage.WriteText("mocap-selftest.log", report); }
            catch (Exception ex) { viewModel.Log("动捕自测日志写入失败：" + ex.Message); }

            if (exitAfter)
            {
                await Task.Delay(400);
                Application.Current?.Shutdown();
            }
        }
    }

    // ===================== 桌宠窗口 =====================

    /// <summary>显示桌宠（模型没加载就开始加载；加载完成前窗口是透明的）。</summary>
    public void ShowPet()
    {
        if (_shutdown) return;
        EnsureInitialized();
        HookWindowClose();

        var vm = EnsureViewModel();
        vm.Cfg.PetEnabled = true;
        EnsureModelLoaded();

        if (_pet is not null)
        {
            try { _pet.RefreshFromConfig(); } catch { /* 忽略 */ }
            UpdateFrameLoop();
            return;
        }

        try
        {
            var pet = new PetWindow(this);
            pet.Closed += OnPetClosed;
            _pet = pet;
            pet.Show();
            UpdateFrameLoop();
            ApplyMocapSettings();   // 桌宠也算「有人看着」→ 动捕可以开始采集
            ApplyMusicSettings();   // 音乐回环同理
            StateChanged?.Invoke(this, EventArgs.Empty);
            PetVisibilityChanged?.Invoke(this, EventArgs.Empty);

            // 桌宠出来时按配置自动带出 AI 聊天窗（默认开；不想每次弹就在设置卡里关掉）
            if (vm.Cfg.AiChatAutoOpen) ShowAiChatWindow();
        }
        catch (Exception ex)
        {
            _pet = null;
            vm.Log("桌宠窗口打开失败：" + ex.Message);
        }
    }

    /// <summary>隐藏桌宠（页面也关着时帧循环随之停下，摄像头 / 音频回环也随之关闭）。</summary>
    public void HidePet()
    {
        if (_viewModel is not null) _viewModel.Cfg.PetEnabled = false;
        // 聊天窗是「贴桌宠」的：桌宠隐藏时一起收起来，避免一个无主窗口飘在桌面上
        if (IsAiChatWindowVisible) HideAiChatWindow();
        ClearFoodLayer();
        ClosePetInternal(raiseEvent: true);
        UpdateFrameLoop();
        ApplyMocapSettings();
        ApplyMusicSettings();
    }

    /// <summary>显示 / 隐藏桌宠。</summary>
    public void TogglePet()
    {
        if (_pet is not null) HidePet();
        else ShowPet();
    }

    /// <summary>桌宠居中到主屏工作区。</summary>
    public void CenterPet()
    {
        try
        {
            _pet?.CenterOnScreen();
            _viewModel?.SetStatus("桌宠已居中");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("桌宠居中失败：" + ex.Message);
        }
    }

    /// <summary>桌宠回到默认位置（主屏右下角）并恢复默认大小。</summary>
    public void ResetPetPosition()
    {
        try
        {
            _pet?.ResetPositionToDefault();
            _viewModel?.SetStatus("桌宠位置已重置");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("重置桌宠位置失败：" + ex.Message);
        }
    }

    /// <summary>配置变化后让桌宠窗口重新套用外观（宽高 / 不透明度 / 置顶 / 点击穿透）。</summary>
    public void ApplyPetConfig()
    {
        try { _pet?.RefreshFromConfig(); } catch { /* 忽略 */ }
    }

    private void ClosePetInternal(bool raiseEvent)
    {
        var pet = _pet;
        if (pet is null) return;
        _pet = null;
        try { pet.Closed -= OnPetClosed; } catch { /* 忽略 */ }
        try { pet.CloseForShutdown(); } catch { /* 忽略 */ }
        try { pet.Close(); } catch { /* 忽略 */ }
        UpdateFrameLoop();
        ApplyMocapSettings();
        if (!raiseEvent) return;
        StateChanged?.Invoke(this, EventArgs.Empty);
        PetVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPetClosed(object? sender, EventArgs e)
    {
        if (sender is not PetWindow pet || !ReferenceEquals(pet, _pet)) return;
        _pet = null;
        if (_viewModel is not null) _viewModel.Cfg.PetEnabled = false;
        SaveConfigNow();
        if (IsAiChatWindowVisible) HideAiChatWindow();
        ClearFoodLayer();
        UpdateFrameLoop();
        ApplyMocapSettings();   // 桌宠关了、页面也没开 → 关闭摄像头
        ApplyMusicSettings();   // 音频回环同理

        // 桌宠关了、页面也没开：眼神回正
        if (!_pageActive)
        {
            try { _runtime?.ClearEyeTrackingTarget(); } catch { /* 忽略 */ }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        PetVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===================== 生命周期 / 退出 =====================

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (Application.Current is { } app) app.Exit += OnApplicationExit;
        }
        catch { /* 无 Application（理论上不会发生）忽略 */ }
        HookWindowClose();
    }

    /// <summary>挂主窗口关闭事件：主窗口一关就释放桌宠，否则最后一个窗口永远不关、进程退不掉。</summary>
    private void HookWindowClose()
    {
        if (_windowCloseHooked) return;
        if (Application.Current?.MainWindow is not { } window) return;
        _windowCloseHooked = true;
        try { window.Closed += OnMainWindowClosed; } catch { /* 忽略 */ }
    }

    private void UnhookWindowClose()
    {
        if (!_windowCloseHooked) return;
        _windowCloseHooked = false;
        try
        {
            if (Application.Current?.MainWindow is { } window) window.Closed -= OnMainWindowClosed;
        }
        catch { /* 忽略 */ }
    }

    private void OnApplicationExit(object? sender, ExitEventArgs e) => Shutdown();

    private void OnMainWindowClosed(object? sender, EventArgs e) => Shutdown();

    // ===================== 日志快捷方式 =====================

    /// <summary>往工具页日志里追加一行（可能在后台线程调用）。</summary>
    public void Log(string message) => _viewModel?.Log(message);
}
