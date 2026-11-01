using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Live2D.Audio;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Mocap;
using YuLiangToolkit.Tools.Live2D.Model;
using YuLiangToolkit.Tools.Live2D.Rendering;

namespace YuLiangToolkit.Tools.Live2D.ViewModels;

/// <summary>
/// 「Live2D 桌宠」工具页的 ViewModel：模型加载 / 参数调试 / 动作表情 / 物理 / 桌宠开关。
/// 加载完成前是「正在加载模型…」的占位状态；模型运行时由 <see cref="Live2DSession"/> 持有。
/// </summary>
public sealed class Live2DViewModel : ToolViewModelBase
{
    private Live2DSession _session = Live2DSession.Current;

    /// <summary>
    /// 当前激活的表情（官方语义：同一时间只有一个表情；切换时旧的淡出、新表情淡入）。
    /// 表情卡是单选：点新的会切换，点当前激活的会取消回默认。
    /// </summary>
    private ExpressionRowViewModel? _activeExpression;
    private bool _isLoading;
    private bool _isLoaded;
    private double _loadProgress;
    private string _loadProgressText = "0%";
    private string _loadErrorText = "";
    private string _modelName = "（未加载）";
    private string _currentModelSourceText = "内置模型（DeepSeek 大肥鱼）";
    private string _previewStatsText = "—";
    private string _selectedDrawableText = "（点击预览里的模型可命中网格 / 部件名）";
    private int _selectedDrawableIndex = -1;
    private string _parameterSearchText = "";
    private string _parameterTotalText = "0 个参数";
    private string _partTotalText = "0 个部件";
    private string _motionSummaryText = "0 个动作";
    private string _expressionSummaryText = "0 个表情";
    private string _currentMotionName = "（无）";
    private string _motionTimeText = "0.0 / 0.0 s";
    private double _motionProgress;
    private bool _isMotionPlaying;
    private string _lastScreenshotText = "（还没有截图）";
    private string _eyeTargetText = "眼控：—";
    private OptionItem<string>? _selectedIdleMotion;

    // ===== 摄像头面部动捕 =====
    private OptionItem<string>? _selectedMocapCamera;
    private bool _isCameraEnumerating;
    private string _cameraListSummary = "还没枚举摄像头（打开页面后自动枚举）";
    private string _mocapStatusText = "动捕未启用（打开开关后才会激活摄像头）";
    private string _mocapErrorText = "";
    private bool _isMocapRunning;
    private DispatcherTimer? _mocapApplyTimer;

    // ===== 身体晃动测试 =====
    private OptionItem<string>? _selectedSwayMode;
    private string _swayValuesText = "（模型加载后这里显示参数实时值）";
    private string _swayDemoRemainingText = "没在演示（点「摇一摇」演示 3 秒）";

    // ===== 音乐跟唱 · 律动 =====
    private OptionItem<string>? _selectedMusicMode;
    private string _musicStatusText = "未启用（打开开关后会采集系统正在播放的声音）";
    private string _musicConfidenceText = "置信度 —";
    private string _musicMediaText = "媒体：—";
    private string _musicErrorText = "";
    private string _musicPitchText = "音高 —";
    private string _musicBpmText = "BPM —";
    private double _musicRms;
    private double _musicVocalScore;
    private double _musicConfidence;
    private double _musicBeatStrength;
    private double _musicCenterRatio;
    private double _musicSyllabic;

    public Live2DViewModel() : base(Live2DTool.ToolId)
    {
        Cfg = LoadConfig<Live2DConfig>();
        Cfg.PropertyChanged += OnConfigChanged;
        _selectedSwayMode = SwayModeOptions.FirstOrDefault(option =>
                                string.Equals(option.Value, Cfg.BodySwayMode, StringComparison.Ordinal))
                            ?? SwayModeOptions[0];
        _selectedMusicMode = MusicModeOptions.FirstOrDefault(option =>
                                string.Equals(option.Value, Cfg.MusicReactiveMode, StringComparison.Ordinal))
                            ?? MusicModeOptions[0];

        ReloadModelCommand = new RelayCommand(_ => Session.ReloadModel());
        ChooseModelFolderCommand = new RelayCommand(_ => ChooseModelFolder());
        UseBuiltInModelCommand = new RelayCommand(_ => UseBuiltInModel());
        OpenModelFolderCommand = new RelayCommand(_ => OpenModelFolder());
        ResetParametersCommand = new RelayCommand(_ => ResetAllParameters());
        ResetPartsCommand = new RelayCommand(_ => ResetAllParts());
        PlayMotionCommand = new RelayCommand(parameter => PlayMotion(parameter as MotionRowViewModel));
        StopMotionCommand = new RelayCommand(_ => StopMotion());
        ToggleExpressionCommand = new RelayCommand(parameter => ToggleExpression(parameter as ExpressionRowViewModel));
        ClearExpressionsCommand = new RelayCommand(_ => ClearAllExpressions());
        TogglePetCommand = new RelayCommand(_ => Session.TogglePet());
        CenterPetCommand = new RelayCommand(_ => Session.CenterPet());
        ResetPetPositionCommand = new RelayCommand(_ => Session.ResetPetPosition());
        TakeScreenshotCommand = new RelayCommand(_ => TakeScreenshot());
        OpenScreenshotFolderCommand = new RelayCommand(_ => OpenScreenshotFolder());
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        ClearSelectedDrawableCommand = new RelayCommand(_ => SetPickedDrawable(-1, "", ""));
        RefreshCamerasCommand = new AsyncRelayCommand(_ => EnsureCamerasLoadedAsync(force: true));
        ResetMocapCalibrationCommand = new RelayCommand(_ => Session.ResetMocapCalibration());
        CalibrateMocapNeutralCommand = new RelayCommand(_ => Session.CalibrateMocapNeutralPosition());
        ShakeSwayCommand = new RelayCommand(_ => ShakeSway());
        StopSwayCommand = new RelayCommand(_ => StopSway());
        ApplyFacePresetCommand = new RelayCommand(parameter => ApplyFacePreset(parameter as string ?? "tongue"));

        Session.PetVisibilityChanged += OnPetVisibilityChanged;
        Session.StateChanged += OnSessionStateChanged;
        Session.MocapStateChanged += OnMocapStateChanged;
    }

    // ===================== 基础设施 =====================

    /// <summary>模型运行时所在的会话（页面与桌宠共用）。</summary>
    public Live2DSession Session
    {
        get => _session;
        internal set => _session = value;
    }

    /// <summary>工具配置（自动保存）。</summary>
    public Live2DConfig Cfg { get; }

    /// <summary>当前模型运行时（没加载为 null）。</summary>
    public ModelRuntime? Runtime => Session.Runtime;

    // ===================== 加载状态 =====================

    /// <summary>是否正在加载模型。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    /// <summary>模型是否加载完成。</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    /// <summary>加载进度 0~1。</summary>
    public double LoadProgress
    {
        get => _loadProgress;
        private set => Set(ref _loadProgress, Math.Clamp(value, 0, 1));
    }

    /// <summary>加载进度文本。</summary>
    public string LoadProgressText
    {
        get => _loadProgressText;
        private set => Set(ref _loadProgressText, value);
    }

    /// <summary>加载失败原因（空 = 没有错误）。</summary>
    public string LoadErrorText
    {
        get => _loadErrorText;
        private set
        {
            if (!Set(ref _loadErrorText, value)) return;
            OnPropertyChanged(nameof(HasLoadError));
        }
    }

    /// <summary>是否有加载错误（控制错误提示条的显示）。</summary>
    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadErrorText);

    /// <summary>加载状态的一句话（顶部状态条 / 信息卡共用）。</summary>
    public string ModelStatusText
    {
        get
        {
            if (IsLoading) return $"正在加载模型… {LoadProgressText}";
            if (HasLoadError) return "模型加载失败：" + LoadErrorText;
            if (IsLoaded) return $"模型已加载：{ModelName}";
            return "模型未加载";
        }
    }

    /// <summary>模型名。</summary>
    public string ModelName
    {
        get => _modelName;
        private set => Set(ref _modelName, value);
    }

    /// <summary>当前模型来源的一句话（内置 / 自定义文件夹）。</summary>
    public string CurrentModelSourceText
    {
        get => _currentModelSourceText;
        private set => Set(ref _currentModelSourceText, value);
    }

    /// <summary>内置模型所在目录（exe 旁边 Live2D\Models\DeepSeek）。</summary>
    public string BuiltInModelPathText => Live2DModelAsset.BuiltInModelDirectory;

    /// <summary>模型信息卡里的信息行。</summary>
    public ObservableCollection<ModelInfoRow> InfoRows { get; } = new();

    // ===================== 参数 / 部件 =====================

    /// <summary>参数搜索框内容（按参数 Id / 中文名过滤，命中的分组自动展开）。</summary>
    public string ParameterSearchText
    {
        get => _parameterSearchText;
        set
        {
            if (!Set(ref _parameterSearchText, value ?? "")) return;
            BuildParameterGroups();
        }
    }

    /// <summary>参数分组列表（cdi3 分组，折叠时按需生成行）。</summary>
    public ObservableCollection<ParameterGroupViewModel> ParameterGroups { get; } = new();

    /// <summary>参数统计文本。</summary>
    public string ParameterTotalText
    {
        get => _parameterTotalText;
        private set => Set(ref _parameterTotalText, value);
    }

    /// <summary>部件分组列表。</summary>
    public ObservableCollection<PartGroupViewModel> PartGroups { get; } = new();

    /// <summary>部件统计文本。</summary>
    public string PartTotalText
    {
        get => _partTotalText;
        private set => Set(ref _partTotalText, value);
    }

    // ===================== 动作 / 表情 =====================

    /// <summary>动作列表。</summary>
    public ObservableCollection<MotionRowViewModel> Motions { get; } = new();

    /// <summary>动作统计文本。</summary>
    public string MotionSummaryText
    {
        get => _motionSummaryText;
        private set => Set(ref _motionSummaryText, value);
    }

    /// <summary>当前动作名。</summary>
    public string CurrentMotionName
    {
        get => _currentMotionName;
        private set => Set(ref _currentMotionName, value);
    }

    /// <summary>当前动作时间文本（已播 / 总时长）。</summary>
    public string MotionTimeText
    {
        get => _motionTimeText;
        private set => Set(ref _motionTimeText, value);
    }

    /// <summary>当前动作进度 0~1。</summary>
    public double MotionProgress
    {
        get => _motionProgress;
        private set => Set(ref _motionProgress, Math.Clamp(value, 0, 1));
    }

    /// <summary>当前是否有动作在播。</summary>
    public bool IsMotionPlaying
    {
        get => _isMotionPlaying;
        private set => Set(ref _isMotionPlaying, value);
    }

    /// <summary>待机动作下拉选项（第一项 = 自动）。</summary>
    public ObservableCollection<OptionItem<string>> IdleMotionOptions { get; } = new();

    /// <summary>当前选中的待机动作。</summary>
    public OptionItem<string>? SelectedIdleMotion
    {
        get => _selectedIdleMotion;
        set
        {
            if (!Set(ref _selectedIdleMotion, value) || value is null) return;
            Cfg.IdleMotion = value.Value;
            Session.ApplyRuntimeSettings();
            Log(value.Value.Length == 0 ? "待机动作：自动选择" : $"待机动作：{value.Value}");
        }
    }

    /// <summary>表情列表。</summary>
    public ObservableCollection<ExpressionRowViewModel> Expressions { get; } = new();

    /// <summary>表情统计文本。</summary>
    public string ExpressionSummaryText
    {
        get => _expressionSummaryText;
        private set => Set(ref _expressionSummaryText, value);
    }

    // ===================== 调试 / 预览 =====================

    /// <summary>预览左下角的统计文本（FPS / 网格 / 三角形 / 耗时）。</summary>
    public string PreviewStatsText
    {
        get => _previewStatsText;
        private set => Set(ref _previewStatsText, value);
    }

    /// <summary>点击预览后命中的网格说明（给预览下方一行显示 + 日志）。</summary>
    public string SelectedDrawableText
    {
        get => _selectedDrawableText;
        private set => Set(ref _selectedDrawableText, value);
    }

    /// <summary>当前选中的网格下标（-1 = 没选；渲染器用它做高亮 / 蒙版缓冲可视化）。</summary>
    public int SelectedDrawableIndex
    {
        get => _selectedDrawableIndex;
        private set
        {
            if (!Set(ref _selectedDrawableIndex, value)) return;
            OnPropertyChanged(nameof(HighlightDrawableIndex));
            OnPropertyChanged(nameof(MaskBufferDrawableIndex));
            OnPropertyChanged(nameof(HasSelectedDrawable));
        }
    }

    /// <summary>要传给 <see cref="RenderOptions.HighlightDrawableIndex"/> 的值（-1 = 不高亮）。</summary>
    public int HighlightDrawableIndex => Cfg.HighlightPickedDrawable ? SelectedDrawableIndex : -1;

    /// <summary>要传给 <see cref="RenderOptions.ShowMaskBufferForDrawableIndex"/> 的值（-1 = 不显示）。</summary>
    public int MaskBufferDrawableIndex => Cfg.ShowMaskBuffer ? SelectedDrawableIndex : -1;

    /// <summary>是否已经点中一个网格（控制「清除高亮」按钮的显隐）。</summary>
    public bool HasSelectedDrawable => SelectedDrawableIndex >= 0;

    /// <summary>眼睛跟随的当前目标文本（预览左下角显示，验证眼控换算是准的）。</summary>
    public string EyeTargetText
    {
        get => _eyeTargetText;
        private set => Set(ref _eyeTargetText, value);
    }

    /// <summary>预览背景是否棋盘格（绑定 + 渲染前的代码都读它，保证两边同步）。</summary>
    public bool PreviewCheckerboard
    {
        get => Cfg.PreviewCheckerboard;
        set
        {
            if (Cfg.PreviewCheckerboard == value) return;
            Cfg.PreviewCheckerboard = value;
            OnPropertyChanged();
        }
    }

    /// <summary>预览左下角是否显示统计。</summary>
    public bool ShowPreviewStats
    {
        get => Cfg.ShowPreviewStats;
        set
        {
            if (Cfg.ShowPreviewStats == value) return;
            Cfg.ShowPreviewStats = value;
            OnPropertyChanged();
        }
    }

    /// <summary>调试：画网格包围盒。</summary>
    public bool ShowDrawableBounds
    {
        get => Cfg.ShowDrawableBounds;
        set
        {
            if (Cfg.ShowDrawableBounds == value) return;
            Cfg.ShowDrawableBounds = value;
            OnPropertyChanged();
        }
    }

    /// <summary>调试：蒙版范围染红。</summary>
    public bool ShowMaskRanges
    {
        get => Cfg.ShowMaskRanges;
        set
        {
            if (Cfg.ShowMaskRanges == value) return;
            Cfg.ShowMaskRanges = value;
            OnPropertyChanged();
        }
    }

    /// <summary>调试：高亮点击命中的网格。</summary>
    public bool HighlightPickedDrawable
    {
        get => Cfg.HighlightPickedDrawable;
        set
        {
            if (Cfg.HighlightPickedDrawable == value) return;
            Cfg.HighlightPickedDrawable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HighlightDrawableIndex));
        }
    }

    /// <summary>调试：蒙版缓冲可视化。</summary>
    public bool ShowMaskBuffer
    {
        get => Cfg.ShowMaskBuffer;
        set
        {
            if (Cfg.ShowMaskBuffer == value) return;
            Cfg.ShowMaskBuffer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskBufferDrawableIndex));
        }
    }

    /// <summary>渲染分辨率倍率（只影响测试页预览）。</summary>
    public double RenderScale
    {
        get => Math.Clamp(Cfg.RenderScale, 0.5, 1.5);
        set
        {
            var clamped = Math.Clamp(value, 0.5, 1.5);
            if (Math.Abs(Cfg.RenderScale - clamped) < 0.0001) return;
            Cfg.RenderScale = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>动作淡入淡出时长（秒）。</summary>
    public double MotionFadeSeconds
    {
        get => Math.Clamp(Cfg.MotionFadeSeconds, 0, 2);
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (Math.Abs(Cfg.MotionFadeSeconds - clamped) < 0.0001) return;
            Cfg.MotionFadeSeconds = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>表情淡入淡出时长（秒）。</summary>
    public double ExpressionFadeSeconds
    {
        get => Math.Clamp(Cfg.ExpressionFadeSeconds, 0, 2);
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (Math.Abs(Cfg.ExpressionFadeSeconds - clamped) < 0.0001) return;
            Cfg.ExpressionFadeSeconds = clamped;
            OnPropertyChanged();
        }
    }

    // ===================== 运行时设置（改一下就推给 ModelRuntime） =====================

    /// <summary>启用物理。</summary>
    public bool PhysicsEnabled
    {
        get => Cfg.PhysicsEnabled;
        set
        {
            if (Cfg.PhysicsEnabled == value) return;
            Cfg.PhysicsEnabled = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>物理强度倍率（0 ~ 1，1 = 原始幅度）。</summary>
    public double PhysicsStrength
    {
        get => Cfg.PhysicsStrength;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(Cfg.PhysicsStrength - clamped) < 0.0001) return;
            Cfg.PhysicsStrength = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>启用待机动画。</summary>
    public bool IdleEnabled
    {
        get => Cfg.IdleEnabled;
        set
        {
            if (Cfg.IdleEnabled == value) return;
            Cfg.IdleEnabled = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>自动眨眼。</summary>
    public bool AutoBlink
    {
        get => Cfg.AutoBlink;
        set
        {
            if (Cfg.AutoBlink == value) return;
            Cfg.AutoBlink = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>眼睛 / 头部是否跟随鼠标。</summary>
    public bool EyeTracking
    {
        get => Cfg.EyeTracking;
        set
        {
            if (Cfg.EyeTracking == value) return;
            Cfg.EyeTracking = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>跟随平滑度。</summary>
    public double EyeTrackingSmoothing
    {
        get => Cfg.EyeTrackingSmoothing;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 1);
            if (Math.Abs(Cfg.EyeTrackingSmoothing - clamped) < 0.0001) return;
            Cfg.EyeTrackingSmoothing = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>头部转动幅度倍率。</summary>
    public double EyeTrackingHeadStrength
    {
        get => Cfg.EyeTrackingHeadStrength;
        set
        {
            var clamped = Math.Clamp(value, 0.2, 1.5);
            if (Math.Abs(Cfg.EyeTrackingHeadStrength - clamped) < 0.0001) return;
            Cfg.EyeTrackingHeadStrength = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>眼球转动幅度倍率。</summary>
    public double EyeTrackingEyeStrength
    {
        get => Cfg.EyeTrackingEyeStrength;
        set
        {
            var clamped = Math.Clamp(value, 0.2, 1.5);
            if (Math.Abs(Cfg.EyeTrackingEyeStrength - clamped) < 0.0001) return;
            Cfg.EyeTrackingEyeStrength = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>身体 / 腰部跟随幅度倍率。</summary>
    public double EyeTrackingBodyStrength
    {
        get => Cfg.EyeTrackingBodyStrength;
        set
        {
            var clamped = Math.Clamp(value, 0, 1.5);
            if (Math.Abs(Cfg.EyeTrackingBodyStrength - clamped) < 0.0001) return;
            Cfg.EyeTrackingBodyStrength = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>鼠标跟随：横向灵敏度（0.3 ~ 3；越大左右看越灵敏）。</summary>
    public double EyeTrackingHorizontalGain
    {
        get => Math.Clamp(Cfg.EyeTrackingHorizontalGain, 0.3, 3);
        set
        {
            var clamped = Math.Clamp(value, 0.3, 3);
            if (Math.Abs(Cfg.EyeTrackingHorizontalGain - clamped) < 0.0001) return;
            Cfg.EyeTrackingHorizontalGain = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>鼠标跟随：抬头灵敏度（0.3 ~ 3；鼠标在画布上方时用）。</summary>
    public double EyeTrackingUpGain
    {
        get => Math.Clamp(Cfg.EyeTrackingUpGain, 0.3, 3);
        set
        {
            var clamped = Math.Clamp(value, 0.3, 3);
            if (Math.Abs(Cfg.EyeTrackingUpGain - clamped) < 0.0001) return;
            Cfg.EyeTrackingUpGain = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>鼠标跟随：低头灵敏度（0.3 ~ 3；鼠标在画布下方时用，向下看跟不到就调大这个）。</summary>
    public double EyeTrackingDownGain
    {
        get => Math.Clamp(Cfg.EyeTrackingDownGain, 0.3, 3);
        set
        {
            var clamped = Math.Clamp(value, 0.3, 3);
            if (Math.Abs(Cfg.EyeTrackingDownGain - clamped) < 0.0001) return;
            Cfg.EyeTrackingDownGain = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>鼠标跟随：纵向偏移（-0.5 ~ 0.5；整体偏上 / 偏下一点点）。</summary>
    public double EyeTrackingVerticalOffset
    {
        get => Math.Clamp(Cfg.EyeTrackingVerticalOffset, -0.5, 0.5);
        set
        {
            var clamped = Math.Clamp(value, -0.5, 0.5);
            if (Math.Abs(Cfg.EyeTrackingVerticalOffset - clamped) < 0.0001) return;
            Cfg.EyeTrackingVerticalOffset = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>帧率上限（15 / 30 / 60）。</summary>
    public int FpsLimit
    {
        get => Math.Clamp(Cfg.FpsLimit, 15, 60);
        set
        {
            var normalized = value switch { <= 15 => 15, <= 30 => 30, _ => 60 };
            if (Cfg.FpsLimit == normalized) return;
            Cfg.FpsLimit = normalized;
            OnPropertyChanged();
            Session.ApplyFrameRate();
        }
    }

    /// <summary>纹理最大尺寸（重新加载模型后生效）。</summary>
    public int TextureMaxSize
    {
        get => Cfg.TextureMaxSize;
        set
        {
            if (Cfg.TextureMaxSize == value) return;
            Cfg.TextureMaxSize = value;
            OnPropertyChanged();
            StatusText = "纹理尺寸改完点「重新加载模型」生效";
        }
    }

    /// <summary>帧率选项。</summary>
    public IReadOnlyList<OptionItem<int>> FpsOptions { get; } = OptionItem.From(
        (15, "15 fps（省电）"),
        (30, "30 fps（推荐）"),
        (60, "60 fps（丝滑）"));

    /// <summary>纹理尺寸选项。</summary>
    public IReadOnlyList<OptionItem<int>> TextureSizeOptions { get; } = OptionItem.From(
        (1024, "1024（低配）"),
        (2048, "2048（推荐）"),
        (4096, "4096（原始精度）"));

    /// <summary>预览渲染倍率选项。</summary>
    public IReadOnlyList<OptionItem<double>> RenderScaleOptions { get; } = OptionItem.From(
        (0.5, "0.5×（最省）"),
        (0.75, "0.75×"),
        (1.0, "1.0×（推荐）"),
        (1.25, "1.25×"),
        (1.5, "1.5×（最清晰）"));

    // ===================== 桌宠 =====================

    /// <summary>桌宠窗口是否显示着。</summary>
    public bool IsPetVisible => Session.IsPetVisible;

    /// <summary>桌宠开关按钮的文案。</summary>
    public string PetButtonText => IsPetVisible ? "隐藏桌宠" : "显示桌宠";

    /// <summary>桌宠状态文本（显示中 / 未显示）。</summary>
    public string PetStateText => IsPetVisible ? "显示中" : "未显示";

    /// <summary>桌宠宽度（160 ~ 1000；改一下立刻套到桌宠窗口）。</summary>
    public double PetWidth
    {
        get => Math.Clamp(Cfg.PetWidth, 160, 1000);
        set
        {
            var clamped = Math.Clamp(value, 160, 1000);
            if (Math.Abs(Cfg.PetWidth - clamped) < 0.1) return;
            Cfg.PetWidth = clamped;
            OnPropertyChanged();
            Session.ApplyPetConfig();
        }
    }

    /// <summary>桌宠不透明度（0.2 ~ 1）。</summary>
    public double PetOpacity
    {
        get => Math.Clamp(Cfg.PetOpacity, 0.2, 1);
        set
        {
            var clamped = Math.Clamp(value, 0.2, 1);
            if (Math.Abs(Cfg.PetOpacity - clamped) < 0.001) return;
            Cfg.PetOpacity = clamped;
            OnPropertyChanged();
            Session.ApplyPetConfig();
        }
    }

    // ===================== 摄像头面部动捕 =====================

    /// <summary>摄像头设备选项（值是设备 Id，标签是设备名）。</summary>
    public ObservableCollection<OptionItem<string>> MocapCameraOptions { get; } = new();

    /// <summary>当前选中的摄像头。</summary>
    public OptionItem<string>? SelectedMocapCamera
    {
        get => _selectedMocapCamera;
        set
        {
            if (!Set(ref _selectedMocapCamera, value) || value is null) return;
            Cfg.MocapCameraId = value.Value;
            Cfg.MocapCameraName = value.Label;
            OnPropertyChanged();
            Log("动捕摄像头：" + value.Label);
            Session.ApplyMocapSettings();
        }
    }

    /// <summary>动捕帧率选项。</summary>
    public IReadOnlyList<OptionItem<int>> MocapFpsOptions { get; } = OptionItem.From(
        (10, "10 fps（省 CPU）"),
        (15, "15 fps（推荐）"),
        (24, "24 fps（更顺）"));

    /// <summary>是否正在枚举摄像头。</summary>
    public bool IsCameraEnumerating
    {
        get => _isCameraEnumerating;
        private set => Set(ref _isCameraEnumerating, value);
    }

    /// <summary>摄像头列表摘要（枚举结果 / 失败原因）。</summary>
    public string CameraListSummary
    {
        get => _cameraListSummary;
        private set => Set(ref _cameraListSummary, value);
    }

    /// <summary>动捕状态行（实测 FPS / 有脸 / 运行时状态都会拼进来）。</summary>
    public string MocapStatusText
    {
        get => _mocapStatusText;
        private set => Set(ref _mocapStatusText, value);
    }

    /// <summary>动捕错误文本（空 = 没错误）。</summary>
    public string MocapErrorText
    {
        get => _mocapErrorText;
        private set
        {
            if (!Set(ref _mocapErrorText, value)) return;
            OnPropertyChanged(nameof(HasMocapError));
        }
    }

    /// <summary>是否有动捕错误（控制错误提示行显隐）。</summary>
    public bool HasMocapError => !string.IsNullOrWhiteSpace(MocapErrorText);

    /// <summary>摄像头是否正在采集。</summary>
    public bool IsMocapRunning
    {
        get => _isMocapRunning;
        private set
        {
            if (Set(ref _isMocapRunning, value)) OnPropertyChanged(nameof(MocapToggleText));
        }
    }

    /// <summary>动捕开关旁边的一行小状态。</summary>
    public string MocapToggleText => Cfg.MocapEnabled
        ? (IsMocapRunning ? "动捕运行中" : "动捕已开启（等待摄像头启动）")
        : "动捕未启用";

    /// <summary>启用摄像头动捕（打开后才请求摄像头权限）。</summary>
    public bool MocapEnabled
    {
        get => Cfg.MocapEnabled;
        set
        {
            if (Cfg.MocapEnabled == value) return;
            Cfg.MocapEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MocapToggleText));
            Session.ApplyRuntimeSettings();
            Session.ApplyMocapSettings();
            StatusText = value ? "正在启动摄像头动捕…" : "已关闭摄像头动捕";
            Log(value ? "动捕：已开启（会请求摄像头权限）" : "动捕：已关闭（摄像头已释放）");
        }
    }

    /// <summary>动捕画面是否镜像（前置摄像头默认开）。</summary>
    public bool MocapMirror
    {
        get => Cfg.MocapMirror;
        set
        {
            if (Cfg.MocapMirror == value) return;
            Cfg.MocapMirror = value;
            OnPropertyChanged();
            Session.ApplyMocapSettings();   // 镜像属于采集参数 → 数据源重建
        }
    }

    /// <summary>动捕是否驱动头部转动。</summary>
    public bool MocapHeadEnabled
    {
        get => Cfg.MocapHeadEnabled;
        set
        {
            if (Cfg.MocapHeadEnabled == value) return;
            Cfg.MocapHeadEnabled = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>动捕是否驱动眨眼。</summary>
    public bool MocapBlinkEnabled
    {
        get => Cfg.MocapBlinkEnabled;
        set
        {
            if (Cfg.MocapBlinkEnabled == value) return;
            Cfg.MocapBlinkEnabled = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>动捕是否驱动张嘴。</summary>
    public bool MocapMouthEnabled
    {
        get => Cfg.MocapMouthEnabled;
        set
        {
            if (Cfg.MocapMouthEnabled == value) return;
            Cfg.MocapMouthEnabled = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>头部转动幅度倍率。</summary>
    public double MocapHeadStrength
    {
        get => Math.Clamp(Cfg.MocapHeadStrength, 0, 1.5);
        set
        {
            var clamped = Math.Clamp(value, 0, 1.5);
            if (Math.Abs(Cfg.MocapHeadStrength - clamped) < 0.001) return;
            Cfg.MocapHeadStrength = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>眼球跟随幅度倍率。</summary>
    public double MocapEyeStrength
    {
        get => Math.Clamp(Cfg.MocapEyeStrength, 0, 1.5);
        set
        {
            var clamped = Math.Clamp(value, 0, 1.5);
            if (Math.Abs(Cfg.MocapEyeStrength - clamped) < 0.001) return;
            Cfg.MocapEyeStrength = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>张嘴开合幅度倍率。</summary>
    public double MocapMouthStrength
    {
        get => Math.Clamp(Cfg.MocapMouthStrength, 0, 1.5);
        set
        {
            var clamped = Math.Clamp(value, 0, 1.5);
            if (Math.Abs(Cfg.MocapMouthStrength - clamped) < 0.001) return;
            Cfg.MocapMouthStrength = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>跟随平滑（0 ~ 0.9；拖动时防抖 0.5 秒再重建数据源）。</summary>
    public double MocapSmoothing
    {
        get => Math.Clamp(Cfg.MocapSmoothing, 0, 0.9);
        set
        {
            var clamped = Math.Clamp(value, 0, 0.9);
            if (Math.Abs(Cfg.MocapSmoothing - clamped) < 0.001) return;
            Cfg.MocapSmoothing = clamped;
            OnPropertyChanged();
            ScheduleMocapApply();
        }
    }

    /// <summary>动捕目标帧率（10 / 15 / 24）。</summary>
    public int MocapFps
    {
        get => Math.Clamp(Cfg.MocapFps, 10, 24);
        set
        {
            var clamped = Math.Clamp(value, 10, 24);
            if (Cfg.MocapFps == clamped) return;
            Cfg.MocapFps = clamped;
            OnPropertyChanged();
            Session.ApplyMocapSettings();
        }
    }

    /// <summary>是否生成摄像头预览帧（测试页小窗；关掉更省 CPU）。</summary>
    public bool MocapShowPreview
    {
        get => Cfg.MocapShowPreview;
        set
        {
            if (Cfg.MocapShowPreview == value) return;
            Cfg.MocapShowPreview = value;
            OnPropertyChanged();
            Log(value ? "动捕：已开启摄像头预览" : "动捕：已关闭摄像头预览（省 CPU）");
            Session.ApplyMocapSettings();
        }
    }

    /// <summary>页面 100ms 定时器调用：刷新动捕状态行 / 错误提示（不碰摄像头本身）。</summary>
    public void UpdateMocapStatus(double fps, bool running, bool hasFace, string? error)
    {
        IsMocapRunning = running;
        var errorText = error ?? "";
        if (!string.Equals(MocapErrorText, errorText, StringComparison.Ordinal)) MocapErrorText = errorText;

        if (!Cfg.MocapEnabled)
        {
            MocapStatusText = "动捕未启用（打开上面的开关后才会请求摄像头权限）";
        }
        else if (!running)
        {
            MocapStatusText = HasMocapError
                ? "摄像头未运行：" + MocapErrorText
                : "正在启动摄像头…（首次使用 Windows 会弹权限提示）";
        }
        else
        {
            var runtimeStatus = "";
            try { runtimeStatus = Runtime?.MocapStatusText ?? ""; } catch { /* 运行时没准备好 */ }
            var fpsText = fps > 0 ? $"{fps:0.0} fps" : "等待首帧";
            var faceText = hasFace ? "检测到人脸" : "未检测到人脸";
            MocapStatusText = $"运行中 · {fpsText} · {faceText}" +
                              (runtimeStatus.Length > 0 ? " · " + runtimeStatus : "");
        }

        OnPropertyChanged(nameof(MocapToggleText));
    }

    /// <summary>枚举摄像头（页面打开 / 点「刷新设备」时调用；只枚举、不打开摄像头）。</summary>
    public async Task EnsureCamerasLoadedAsync(bool force = false)
    {
        if (IsCameraEnumerating) return;
        if (!force && MocapCameraOptions.Count > 0) return;

        IsCameraEnumerating = true;
        try
        {
            var cameras = await MocapCameraSource.EnumerateCamerasAsync();
            MocapCameraOptions.Clear();
            foreach (var camera in cameras)
            {
                MocapCameraOptions.Add(new OptionItem<string>(camera.Id, camera.Name));
            }

            var preferred = cameras.FirstOrDefault(camera =>
                                string.Equals(camera.Id, Cfg.MocapCameraId, StringComparison.Ordinal))
                            ?? cameras.FirstOrDefault(camera =>
                                string.Equals(camera.Name, Cfg.MocapCameraName, StringComparison.OrdinalIgnoreCase))
                            ?? PickPreferredCamera(cameras);
            _selectedMocapCamera = preferred is null
                ? null
                : MocapCameraOptions.FirstOrDefault(option => option.Value == preferred.Id);
            OnPropertyChanged(nameof(SelectedMocapCamera));

            CameraListSummary = cameras.Count == 0
                ? "没有找到摄像头：检查设备管理器 / Windows「隐私和安全性 → 摄像头」是否允许桌面应用访问"
                : $"找到 {cameras.Count} 个摄像头：{string.Join("、", cameras.Select(camera => camera.Name))}";
            Log(CameraListSummary);
        }
        catch (Exception ex)
        {
            CameraListSummary = "枚举摄像头失败：" + ex.Message;
            Log(CameraListSummary);
        }
        finally
        {
            IsCameraEnumerating = false;
        }
    }

    /// <summary>
    /// 挑默认摄像头：优先内置摄像头（名字里带 "HD User Facing" 这类），
    /// 尽量避开 DroidCam / OBS / ManyCam 这些手机或虚拟摄像头（通常不是用户想要的）。
    /// </summary>
    public static MocapCameraInfo? PickPreferredCamera(IReadOnlyList<MocapCameraInfo> cameras)
    {
        if (cameras.Count == 0) return null;

        var preferred = cameras.FirstOrDefault(camera =>
            camera.Name.Contains("HD User Facing", StringComparison.OrdinalIgnoreCase));
        preferred ??= cameras.FirstOrDefault(camera =>
            camera.Name.Contains("Integrated", StringComparison.OrdinalIgnoreCase)
            || camera.Name.Contains("内置", StringComparison.Ordinal)
            || camera.Name.Contains("Face", StringComparison.OrdinalIgnoreCase));
        preferred ??= cameras.FirstOrDefault(camera => !IsVirtualCameraName(camera.Name));
        return preferred ?? cameras[0];
    }

    private static bool IsVirtualCameraName(string name)
        => name.Contains("DroidCam", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
           || name.Contains("OBS", StringComparison.OrdinalIgnoreCase)
           || name.Contains("ManyCam", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Iriun", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Phone", StringComparison.OrdinalIgnoreCase)
           || name.Contains("手机", StringComparison.Ordinal);

    /// <summary>平滑滑杆拖动时防抖（0.5 秒）再让会话重建数据源，避免每动一下就重启摄像头。</summary>
    private void ScheduleMocapApply()
    {
        if (_mocapApplyTimer is null)
        {
            _mocapApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _mocapApplyTimer.Tick += OnMocapApplyTimerTick;
        }

        _mocapApplyTimer.Stop();
        _mocapApplyTimer.Start();
    }

    private void OnMocapApplyTimerTick(object? sender, EventArgs e)
    {
        _mocapApplyTimer?.Stop();
        Session.ApplyMocapSettings();
    }

    private void OnMocapStateChanged(object? sender, EventArgs e) => Ui(() =>
    {
        OnPropertyChanged(nameof(IsMocapRunning));
        OnPropertyChanged(nameof(MocapToggleText));
    });

    // ===================== 身体晃动测试 =====================

    /// <summary>晃动模式选项（配置里存中文名字符串，运行时按它选正弦组合）。</summary>
    public IReadOnlyList<OptionItem<string>> SwayModeOptions { get; } = OptionItem.From(
        ("左右摇", "左右摇（侧面看最明显）"),
        ("前后摇", "前后摇（前后点头感）"),
        ("扭腰", "扭腰（腰胯一起扭）"),
        ("椭圆", "椭圆（X/Z 相位差 90°）"),
        ("自然晃", "自然晃（多频叠加）"));

    /// <summary>当前晃动模式。</summary>
    public OptionItem<string>? SelectedSwayMode
    {
        get => _selectedSwayMode;
        set
        {
            if (!Set(ref _selectedSwayMode, value) || value is null) return;
            Cfg.BodySwayMode = value.Value;
            Session.ApplyRuntimeSettings();
            Log("身体晃动模式：" + value.Label);
        }
    }

    /// <summary>是否开启身体晃动测试（关掉后模型回静止）。</summary>
    public bool BodySwayEnabled
    {
        get => Cfg.BodySwayEnabled;
        set
        {
            if (Cfg.BodySwayEnabled == value) return;
            Cfg.BodySwayEnabled = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
            StatusText = value ? "身体晃动已开启：模型会持续按设定模式摆动" : "身体晃动已关闭";
        }
    }

    /// <summary>晃动幅度（度）。</summary>
    public double BodySwayAmplitude
    {
        get => Math.Clamp(Cfg.BodySwayAmplitude, 0, 30);
        set
        {
            var clamped = Math.Clamp(value, 0, 30);
            if (Math.Abs(Cfg.BodySwayAmplitude - clamped) < 0.001) return;
            Cfg.BodySwayAmplitude = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>晃动速度（Hz）。</summary>
    public double BodySwaySpeed
    {
        get => Math.Clamp(Cfg.BodySwaySpeed, 0.1, 2);
        set
        {
            var clamped = Math.Clamp(value, 0.1, 2);
            if (Math.Abs(Cfg.BodySwaySpeed - clamped) < 0.001) return;
            Cfg.BodySwaySpeed = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>头部补偿（0 = 完全跟着晃，1 = 尽量保持朝前）。</summary>
    public double BodySwayHeadFollow
    {
        get => Math.Clamp(Cfg.BodySwayHeadFollow, 0, 1);
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(Cfg.BodySwayHeadFollow - clamped) < 0.001) return;
            Cfg.BodySwayHeadFollow = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>是否连腰部 / 腿部参数一起晃。</summary>
    public bool BodySwayIncludeHips
    {
        get => Cfg.BodySwayIncludeHips;
        set
        {
            if (Cfg.BodySwayIncludeHips == value) return;
            Cfg.BodySwayIncludeHips = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>关键参数实时值文本（100ms 刷新；看「身体晃 → AngleX → FrontHairX1」链条）。</summary>
    public string SwayValuesText
    {
        get => _swayValuesText;
        private set => Set(ref _swayValuesText, value);
    }

    /// <summary>「摇一摇」剩余时间文本。</summary>
    public string SwayDemoRemainingText
    {
        get => _swayDemoRemainingText;
        private set => Set(ref _swayDemoRemainingText, value);
    }

    /// <summary>页面 100ms 定时器调用：刷新晃动的实时值文本与「摇一摇」剩余时间。</summary>
    public void UpdateSwayDisplay(string valuesText, double demoRemainingSeconds)
    {
        if (!string.Equals(SwayValuesText, valuesText, StringComparison.Ordinal)) SwayValuesText = valuesText;
        SwayDemoRemainingText = demoRemainingSeconds > 0.05
            ? $"摇一摇演示中… 剩余 {demoRemainingSeconds:0.0} 秒"
            : "没在演示（点「摇一摇」演示 3 秒）";
    }

    /// <summary>摇一摇：打开晃动并让运行时演示 3 秒（不改幅度 / 速度，方便反复试）。</summary>
    public void ShakeSway()
    {
        if (!Cfg.BodySwayEnabled)
        {
            Cfg.BodySwayEnabled = true;
            OnPropertyChanged(nameof(BodySwayEnabled));
            Session.ApplyRuntimeSettings();
        }

        Session.StartSwayDemo(3.0);
        StatusText = "摇一摇：3 秒演示中";
    }

    /// <summary>停：停掉演示并关闭身体晃动。</summary>
    public void StopSway()
    {
        Session.StopSwayDemo();
        if (!Cfg.BodySwayEnabled) return;
        Cfg.BodySwayEnabled = false;
        OnPropertyChanged(nameof(BodySwayEnabled));
        Session.ApplyRuntimeSettings();
        StatusText = "已停止身体晃动";
    }

    // ===================== 面部测试（眼睛 / 嘴 / 舌头） =====================

    /// <summary>眼睛组参数 Id（顺序即界面顺序）。</summary>
    private static readonly string[] FaceEyeIds =
    {
        "ParamEyeBallX", "ParamEyeBallY", "ParamEyeLOpen", "ParamEyeROpen", "EyeSmile",
    };

    /// <summary>嘴组参数 Id。</summary>
    private static readonly string[] FaceMouthIds =
    {
        "ParamMouthOpenY", "ParamMouthForm", "JawOpen", "Mouthpucker", "MouthFunnel",
        "MouthPuckerWiden", "MouthPressLipOpen", "MouthRollUpper", "MouthRollLower",
        "MouthSmile_L", "MouthSmile_R", "PoutOffsetXL", "PoutOffsetXR",
    };

    /// <summary>舌头组参数 Id（重点：吐舌头）。</summary>
    private static readonly string[] FaceTongueIds =
    {
        "TongueOut", "ParamTongueUpDown", "ParamTongueWidth",
    };

    private readonly List<FaceParameterRowViewModel> _faceRows = new();
    private string _faceSummaryText = "模型加载后显示可调参数";

    /// <summary>眼睛组参数行。</summary>
    public ObservableCollection<FaceParameterRowViewModel> FaceEyeParameters { get; } = new();

    /// <summary>嘴组参数行。</summary>
    public ObservableCollection<FaceParameterRowViewModel> FaceMouthParameters { get; } = new();

    /// <summary>舌头组参数行。</summary>
    public ObservableCollection<FaceParameterRowViewModel> FaceTongueParameters { get; } = new();

    /// <summary>面部参数统计文本（眼睛 / 嘴 / 舌头各几个）。</summary>
    public string FaceSummaryText
    {
        get => _faceSummaryText;
        private set => Set(ref _faceSummaryText, value);
    }

    private void BuildFaceParameters(ModelRuntime runtime)
    {
        ClearFaceParameters();
        try
        {
            var model = runtime.Model;
            var display = runtime.Display;

            void Add(ObservableCollection<FaceParameterRowViewModel> target, string id)
            {
                var parameter = model.FindParameter(id);
                if (parameter is null) return;

                string? name = null;
                if (display is not null && display.ParameterNames.TryGetValue(id, out var foundName))
                {
                    name = foundName;
                }

                var row = new FaceParameterRowViewModel(parameter, name);
                target.Add(row);
                _faceRows.Add(row);
            }

            foreach (var id in FaceEyeIds) Add(FaceEyeParameters, id);
            foreach (var id in FaceMouthIds) Add(FaceMouthParameters, id);
            foreach (var id in FaceTongueIds) Add(FaceTongueParameters, id);

            FaceSummaryText = $"{_faceRows.Count} 个可调参数（眼睛 {FaceEyeParameters.Count} / " +
                              $"嘴 {FaceMouthParameters.Count} / 舌头 {FaceTongueParameters.Count}）";
        }
        catch (Exception ex)
        {
            FaceSummaryText = "面部参数读取失败：" + ex.Message;
        }
    }

    private void ClearFaceParameters()
    {
        FaceEyeParameters.Clear();
        FaceMouthParameters.Clear();
        FaceTongueParameters.Clear();
        _faceRows.Clear();
        FaceSummaryText = "模型加载后显示可调参数";
    }

    /// <summary>
    /// 页面 100ms 定时器调用：把滑杆 / 当前值刷新成模型真实值
    /// （动捕 / 眼睛跟随 / 表情可能在每帧写这些参数，界面上要能看到实际值）。
    /// </summary>
    public void RefreshFaceValues()
    {
        foreach (var row in _faceRows) row.RefreshFromModel();
    }

    /// <summary>应用「面部测试」预设（按钮命令 / 截图调试钩子共用）。</summary>
    /// <param name="preset">tongue / open / pucker / smile / wink / rolleyes / reset。</param>
    public void ApplyFacePreset(string preset)
    {
        if (Runtime?.IsLoaded != true)
        {
            StatusText = "模型还没加载，无法应用面部预设";
            return;
        }

        var applied = new List<string>();
        void Set(string id, double value) => SetFaceValue(id, value, applied);

        switch (preset)
        {
            case "open":
                Set("ParamMouthOpenY", 1);
                Set("JawOpen", 1);
                break;

            case "pucker":
                Set("Mouthpucker", 1);
                Set("ParamMouthForm", 0.3);
                break;

            case "smile":
                Set("MouthSmile_L", 1);
                Set("MouthSmile_R", 1);
                Set("EyeSmile", 1);
                Set("ParamMouthForm", 1);
                break;

            case "wink":
                Set("ParamEyeLOpen", 0.05);
                Set("ParamEyeROpen", 1);
                break;

            case "rolleyes":
                Set("ParamEyeBallY", 1);
                Set("ParamEyeLOpen", 0.5);
                Set("ParamEyeROpen", 0.5);
                break;

            case "reset":
                foreach (var row in _faceRows) row.SetValue((float)row.DefaultValue);
                break;

            default:   // tongue（默认）
                preset = "tongue";
                Set("TongueOut", 1);
                Set("ParamMouthOpenY", 0.7);
                Set("JawOpen", 0.4);
                Set("ParamMouthForm", 0.5);
                break;
        }

        RefreshFaceValues();
        var summary = preset == "reset"
            ? "面部测试：已恢复全部面部参数默认值"
            : $"面部测试：预设「{FacePresetDisplayName(preset)}」→ " + string.Join("，", applied);
        Log(summary);
        StatusText = summary;
    }

    private void SetFaceValue(string id, double value, List<string> applied)
    {
        try
        {
            var row = _faceRows.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (row is not null)
            {
                row.SetValue((float)Math.Clamp(value, row.Minimum, row.Maximum));
                applied.Add($"{id}={row.Value:0.00}");
                return;
            }

            var parameter = Runtime?.Model.FindParameter(id);
            if (parameter is null) return;
            parameter.Value = (float)value;
            applied.Add($"{id}={parameter.Value:0.00}");
        }
        catch
        {
            // 单个参数失败不影响其它预设值
        }
    }

    /// <summary>预设中文名（日志 / 界面提示用）。</summary>
    public static string FacePresetDisplayName(string preset) => preset switch
    {
        "open" => "😲 张嘴",
        "pucker" => "😗 撅嘴",
        "smile" => "😊 微笑",
        "wink" => "😉 眨眼",
        "rolleyes" => "🙄 翻白眼",
        "reset" => "😐 重置面部",
        _ => "😛 吐舌头",
    };

    /// <summary>
    /// 给调试钩子 / 验收用：把面部测试卡里每个参数的实际值拼成多行文本
    /// （含范围与默认值，方便判断哪些手动值被运行时每帧覆盖了）。
    /// </summary>
    public string BuildFaceValuesSnapshot()
    {
        var builder = new System.Text.StringBuilder();
        void AddGroup(string title, ObservableCollection<FaceParameterRowViewModel> rows)
        {
            builder.AppendLine("[" + title + "]");
            foreach (var row in rows)
            {
                builder.AppendLine($"  {row.Id} = {row.Value:0.000}（范围 {row.Minimum:0.###}~{row.Maximum:0.###}，" +
                                   $"默认 {row.DefaultValue:0.###}）");
            }
        }

        AddGroup("眼睛", FaceEyeParameters);
        AddGroup("嘴", FaceMouthParameters);
        AddGroup("舌头", FaceTongueParameters);
        return builder.ToString();
    }

    // ===================== 音乐跟唱 · 律动 =====================

    /// <summary>识别模式选项（配置里存中文名字符串，运行时映射到 MusicVocalMode）。</summary>
    public IReadOnlyList<OptionItem<string>> MusicModeOptions { get; } = OptionItem.From(
        ("自动识别", "自动识别（推荐）"),
        ("强制跟唱", "强制跟唱（跟唱 + 摇晃）"),
        ("强制纯音乐", "强制纯音乐（只摇晃）"));

    /// <summary>当前识别模式。</summary>
    public OptionItem<string>? SelectedMusicMode
    {
        get => _selectedMusicMode;
        set
        {
            if (!Set(ref _selectedMusicMode, value) || value is null) return;
            Cfg.MusicReactiveMode = value.Value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();   // 模式只影响运行时属性，不用重启采集
            Log("音乐跟唱模式：" + value.Label);
        }
    }

    /// <summary>启用音乐跟唱 / 律动（Windows 回环采集系统正在播放的声音，不申请麦克风、不录音、不保存）。</summary>
    public bool MusicReactiveEnabled
    {
        get => Cfg.MusicReactiveEnabled;
        set
        {
            if (Cfg.MusicReactiveEnabled == value) return;
            Cfg.MusicReactiveEnabled = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
            Session.ApplyMusicSettings();   // 开关 → 启停 / 释放音频回环采集
            StatusText = value ? "正在启动音乐跟唱（采集系统正在播放的声音）…" : "已关闭音乐跟唱";
            Log(value ? "音乐跟唱：已开启（WASAPI 回环采集系统声音，不使用麦克风）" : "音乐跟唱：已关闭，音频采集已释放");
        }
    }

    /// <summary>识别灵敏度（0.3 ~ 3；越大越容易判成「带歌词」）。</summary>
    public double MusicReactiveSensitivity
    {
        get => Math.Clamp(Cfg.MusicReactiveSensitivity, 0.3, 3);
        set
        {
            var clamped = Math.Clamp(value, 0.3, 3);
            if (Math.Abs(Cfg.MusicReactiveSensitivity - clamped) < 0.001) return;
            Cfg.MusicReactiveSensitivity = clamped;
            OnPropertyChanged();
            Session.ApplyMusicSettings();   // 灵敏度直接转给分析器，不重启采集
        }
    }

    /// <summary>跟唱嘴型幅度倍率（0 ~ 1.5）。</summary>
    public double MusicLipSyncStrength
    {
        get => Math.Clamp(Cfg.MusicLipSyncStrength, 0, 1.5);
        set
        {
            var clamped = Math.Clamp(value, 0, 1.5);
            if (Math.Abs(Cfg.MusicLipSyncStrength - clamped) < 0.001) return;
            Cfg.MusicLipSyncStrength = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>身体摇晃幅度倍率（0 ~ 1.5）。</summary>
    public double MusicSwayStrength
    {
        get => Math.Clamp(Cfg.MusicSwayStrength, 0, 1.5);
        set
        {
            var clamped = Math.Clamp(value, 0, 1.5);
            if (Math.Abs(Cfg.MusicSwayStrength - clamped) < 0.001) return;
            Cfg.MusicSwayStrength = clamped;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>是否跟随节拍摇晃身体。</summary>
    public bool MusicSwayEnabled
    {
        get => Cfg.MusicSwayEnabled;
        set
        {
            if (Cfg.MusicSwayEnabled == value) return;
            Cfg.MusicSwayEnabled = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>是否轻微点头（跟着节拍轻点）。</summary>
    public bool MusicHeadBobEnabled
    {
        get => Cfg.MusicHeadBobEnabled;
        set
        {
            if (Cfg.MusicHeadBobEnabled == value) return;
            Cfg.MusicHeadBobEnabled = value;
            OnPropertyChanged();
            Session.ApplyRuntimeSettings();
        }
    }

    /// <summary>音乐卡状态行（未启用 / 启动中 / 静音 / 识别中 / 纯音乐 / 带歌词 + 置信度）。</summary>
    public string MusicStatusText
    {
        get => _musicStatusText;
        private set => Set(ref _musicStatusText, value);
    }

    /// <summary>置信度文本。</summary>
    public string MusicConfidenceText
    {
        get => _musicConfidenceText;
        private set => Set(ref _musicConfidenceText, value);
    }

    /// <summary>媒体信息行（歌名 - 歌手 · 播放器；拿不到显示 —）。</summary>
    public string MusicMediaText
    {
        get => _musicMediaText;
        private set => Set(ref _musicMediaText, value);
    }

    /// <summary>音频采集错误（空 = 没错误）。</summary>
    public string MusicErrorText
    {
        get => _musicErrorText;
        private set
        {
            if (!Set(ref _musicErrorText, value)) return;
            OnPropertyChanged(nameof(HasMusicError));
        }
    }

    /// <summary>是否有音频采集错误。</summary>
    public bool HasMusicError => !string.IsNullOrWhiteSpace(MusicErrorText);

    /// <summary>实时音高文本（YIN 基频；无效显示 —）。</summary>
    public string MusicPitchText
    {
        get => _musicPitchText;
        private set => Set(ref _musicPitchText, value);
    }

    /// <summary>实时 BPM 文本（0 = 还没估出来）。</summary>
    public string MusicBpmText
    {
        get => _musicBpmText;
        private set => Set(ref _musicBpmText, value);
    }

    /// <summary>实时 RMS（0~1，进度条用）。</summary>
    public double MusicRms
    {
        get => _musicRms;
        private set => Set(ref _musicRms, value);
    }

    /// <summary>实时人声分数（0~1，进度条用）。</summary>
    public double MusicVocalScore
    {
        get => _musicVocalScore;
        private set => Set(ref _musicVocalScore, value);
    }

    /// <summary>实时置信度（0~1，进度条用）。</summary>
    public double MusicConfidence
    {
        get => _musicConfidence;
        private set => Set(ref _musicConfidence, value);
    }

    /// <summary>实时节拍强度（0~1，进度条用）。</summary>
    public double MusicBeatStrength
    {
        get => _musicBeatStrength;
        private set => Set(ref _musicBeatStrength, value);
    }

    /// <summary>实时中心声道占比（0~1，进度条用）。</summary>
    public double MusicCenterRatio
    {
        get => _musicCenterRatio;
        private set => Set(ref _musicCenterRatio, value);
    }

    /// <summary>实时音节调制（0~1，进度条用）。</summary>
    public double MusicSyllabic
    {
        get => _musicSyllabic;
        private set => Set(ref _musicSyllabic, value);
    }

    /// <summary>
    /// 页面 100ms 定时器调用：刷新音乐卡的状态 / 媒体信息 / 实时数据。
    /// 数据来自会话持有的音频回环数据源（<c>MusicReactiveSource.Latest</c>），只读快照。
    /// </summary>
    public void UpdateMusicPanel(
        bool enabled,
        bool running,
        string? error,
        string mediaText,
        MusicContentKind kind,
        double confidence,
        double rms,
        double vocalScore,
        double beatStrength,
        double centerVoiceRatio,
        double syllabicModulation,
        bool pitchValid,
        double pitchHz,
        double bpm)
    {
        if (!string.Equals(MusicMediaText, mediaText, StringComparison.Ordinal)) MusicMediaText = mediaText;

        var errorText = error ?? "";
        if (!string.Equals(MusicErrorText, errorText, StringComparison.Ordinal)) MusicErrorText = errorText;

        // 实时数值（进度条 0~1，文本两位小数）
        MusicRms = Math.Clamp(rms, 0, 1);
        MusicVocalScore = Math.Clamp(vocalScore, 0, 1);
        MusicConfidence = Math.Clamp(confidence, 0, 1);
        MusicBeatStrength = Math.Clamp(beatStrength, 0, 1);
        MusicCenterRatio = Math.Clamp(centerVoiceRatio, 0, 1);
        MusicSyllabic = Math.Clamp(syllabicModulation, 0, 1);
        MusicPitchText = pitchValid && pitchHz > 0 ? $"{pitchHz:0.0} Hz" : "—";
        MusicBpmText = bpm > 0 ? $"{bpm:0.0}" : "—";

        MusicConfidenceText = confidence > 0.001 ? $"置信度 {confidence:0.00}" : "置信度 —";

        if (!enabled)
        {
            MusicStatusText = "未启用（打开开关后会采集系统正在播放的声音）";
            return;
        }

        if (!running)
        {
            MusicStatusText = HasMusicError
                ? "音频采集未运行：" + MusicErrorText
                : "正在启动音频回环采集…";
            return;
        }

        var kindText = kind switch
        {
            MusicContentKind.Silent => "静音 / 没在放声音（采集正常）",
            MusicContentKind.Instrumental => "纯音乐（只摇晃，不跟唱）",
            MusicContentKind.VocalSong => "带歌词（跟唱 + 摇晃）",
            _ => "识别中…",
        };
        MusicStatusText = kindText + (confidence > 0.001 ? $"，{MusicConfidenceText}" : "");
    }

    /// <summary>外部直接改 Cfg（截图演示钩子 / 校准写入）后把界面全部重绑一遍。</summary>
    public void RefreshAllBindings() => OnPropertyChanged(string.Empty);

    // ===================== 截图 / 日志 =====================

    /// <summary>最近一次截图结果文本。</summary>
    public string LastScreenshotText
    {
        get => _lastScreenshotText;
        private set => Set(ref _lastScreenshotText, value);
    }

    // ===================== 命令 =====================

    public RelayCommand ReloadModelCommand { get; }
    public RelayCommand ChooseModelFolderCommand { get; }
    public RelayCommand UseBuiltInModelCommand { get; }
    public RelayCommand OpenModelFolderCommand { get; }
    public RelayCommand ResetParametersCommand { get; }
    public RelayCommand ResetPartsCommand { get; }
    public RelayCommand PlayMotionCommand { get; }
    public RelayCommand StopMotionCommand { get; }
    public RelayCommand ToggleExpressionCommand { get; }
    public RelayCommand ClearExpressionsCommand { get; }
    public RelayCommand TogglePetCommand { get; }
    public RelayCommand CenterPetCommand { get; }
    public RelayCommand ResetPetPositionCommand { get; }
    public RelayCommand TakeScreenshotCommand { get; }
    public RelayCommand OpenScreenshotFolderCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ClearSelectedDrawableCommand { get; }
    public AsyncRelayCommand RefreshCamerasCommand { get; }
    public RelayCommand ResetMocapCalibrationCommand { get; }
    public RelayCommand CalibrateMocapNeutralCommand { get; }
    public RelayCommand ShakeSwayCommand { get; }
    public RelayCommand StopSwayCommand { get; }
    public RelayCommand ApplyFacePresetCommand { get; }

    // ===================== 加载状态回调（由 Live2DSession 调用） =====================

    /// <summary>会话开始加载模型。</summary>
    public void SetLoadStarted()
    {
        IsLoading = true;
        IsLoaded = false;
        LoadErrorText = "";
        LoadProgress = 0;
        LoadProgressText = "0%";
        ModelName = "（加载中…）";
        StatusText = "正在加载模型…";
        RaiseAll();
    }

    /// <summary>加载进度变化（会话在 UI 线程调用）。</summary>
    public void SetLoadProgress(double progress)
    {
        LoadProgress = progress;
        LoadProgressText = $"{progress * 100:0}%";
        OnPropertyChanged(nameof(ModelStatusText));
    }

    /// <summary>加载失败（会话在 UI 线程调用）。</summary>
    public void SetLoadFailed(string error)
    {
        IsLoading = false;
        IsLoaded = false;
        LoadErrorText = error;
        ModelName = "（加载失败）";
        StatusText = "模型加载失败：" + error;
        RaiseAll();
    }

    /// <summary>
    /// 接上 / 摘掉模型运行时（会话在 UI 线程调用；null = 释放后清空界面状态）。
    /// 这里一次性把信息卡 / 参数 / 部件 / 动作 / 表情全部重建。
    /// </summary>
    public void AttachRuntime(ModelRuntime? runtime, double loadMilliseconds)
    {
        if (runtime is null)
        {
            IsLoaded = false;
            IsLoading = false;
            ModelName = "（未加载）";
            InfoRows.Clear();
            ParameterGroups.Clear();
            PartGroups.Clear();
            Motions.Clear();
            Expressions.Clear();
            IdleMotionOptions.Clear();
            _activeExpression = null;
            SelectedIdleMotion = null;
            SelectedDrawableIndex = -1;
            SelectedDrawableText = "（点击预览里的模型可命中网格 / 部件名）";
            ParameterTotalText = "0 个参数";
            PartTotalText = "0 个部件";
            MotionSummaryText = "0 个动作";
            ExpressionSummaryText = "0 个表情";
            CurrentMotionName = "（无）";
            IsMotionPlaying = false;
            MotionProgress = 0;
            MotionTimeText = "0.0 / 0.0 s";
            PreviewStatsText = "—";
            ClearFaceParameters();
            RaiseAll();
            return;
        }

        IsLoading = false;
        IsLoaded = runtime.IsLoaded;
        LoadProgress = 1;
        LoadProgressText = "100%";
        LoadErrorText = "";
        ModelName = string.IsNullOrWhiteSpace(runtime.ModelName) ? "（未命名模型）" : runtime.ModelName;
        CurrentModelSourceText = string.IsNullOrWhiteSpace(Cfg.ModelFolder)
            ? "内置模型（DeepSeek 大肥鱼）"
            : "自定义模型：" + Cfg.ModelFolder;

        BuildModelInfo(runtime, loadMilliseconds);
        BuildParameterGroups();
        BuildPartGroups();
        BuildMotionRows(runtime);
        BuildExpressionRows(runtime);
        BuildIdleMotionOptions(runtime);
        BuildFaceParameters(runtime);
        RefreshFrameState(runtime);
        RaiseAll();
    }

    // ===================== 帧状态刷新（由会话 / 页面调用） =====================

    /// <summary>每帧（节流后）刷新动作进度 / 表情状态等界面状态。</summary>
    public void RefreshFrameState(ModelRuntime runtime)
    {
        try
        {
            var current = runtime.CurrentMotion;
            IsMotionPlaying = current is not null;
            CurrentMotionName = current?.Name ?? "（无）";
            var time = runtime.CurrentMotionTime;
            var total = current?.Duration ?? 0;
            MotionProgress = total > 0 ? time / total : 0;
            MotionTimeText = current is null ? "0.0 / 0.0 s" : $"{time:0.0} / {total:0.0} s";

            foreach (var row in Motions)
            {
                var isCurrent = current is not null
                                && (ReferenceEquals(row.Motion, current)
                                    || string.Equals(row.Motion.Name, current.Name, StringComparison.Ordinal));
                row.IsCurrent = isCurrent;
                if (!isCurrent) continue;
                row.Progress = MotionProgress;
                row.TimeText = MotionTimeText;
            }
        }
        catch
        {
            // 运行时还没准备好：忽略这一帧
        }
    }

    /// <summary>预览区每半秒更新一次的统计文本（FPS / 网格 / 三角形 / 耗时）。</summary>
    public void UpdatePreviewStats(double fps, RenderStats stats, double tickMilliseconds)
    {
        if (!Cfg.ShowPreviewStats)
        {
            PreviewStatsText = "";
            return;
        }

        PreviewStatsText =
            $"FPS {fps:0} · 网格 {stats.DrawablesDrawn} · 三角形 {stats.TrianglesDrawn:N0} · " +
            $"Tick {tickMilliseconds:0.0} ms · 渲染 {stats.RenderMilliseconds:0.0} ms · 蒙版 {stats.MaskPasses}";
    }

    /// <summary>预览点击命中一个网格（-1 / 空 = 清空选中）。</summary>
    public void SetPickedDrawable(int drawableIndex, string drawableId, string detailText)
    {
        SelectedDrawableIndex = drawableIndex;
        SelectedDrawableText = drawableIndex < 0
            ? "（点击预览里的模型可命中网格 / 部件名）"
            : $"网格 #{drawableIndex}「{drawableId}」{(string.IsNullOrWhiteSpace(detailText) ? "" : " · " + detailText)}";
        if (drawableIndex >= 0) Log("[命中] " + SelectedDrawableText);
    }

    /// <summary>鼠标在预览里移动时更新左下角的眼控目标文本（由页面代码后置调用）。</summary>
    public void SetEyeTargetDisplay(float canvasX, float canvasY)
        => EyeTargetText = $"眼控：({canvasX:0}, {canvasY:0}) px";

    /// <summary>鼠标离开预览：眼控目标清空。</summary>
    public void ClearEyeTargetDisplay() => EyeTargetText = "眼控：—";

    // ===================== 建模数据（加载完成时一次） =====================

    private void BuildModelInfo(ModelRuntime runtime, double loadMilliseconds)
    {
        InfoRows.Clear();
        try
        {
            var model = runtime.Model;
            var asset = runtime.Asset;
            var visibleCount = 0;
            foreach (var drawable in model.Drawables)
            {
                if (drawable.IsVisible) visibleCount++;
            }

            void Add(string name, string value) => InfoRows.Add(new ModelInfoRow(name, value));

            Add("模型名称", ModelName);
            try { Add("moc 格式", $"{CubismMoc.DescribeMocVersion(model.MocVersion)}（v{model.MocVersion}）"); }
            catch { Add("moc 格式", "读取失败"); }
            try { Add("Core 版本", CubismMoc.GetCoreVersionText()); }
            catch { Add("Core 版本", "未知"); }
            Add("画布", $"{model.CanvasWidthPixels:0} × {model.CanvasHeightPixels:0} px（ppu {model.PixelsPerUnit:0}）");
            Add("参数 / 部件", $"{model.ParameterCount} / {model.PartCount}");
            Add("网格", $"{model.DrawableCount}（当前可见 {visibleCount}）");
            Add("顶点 / 三角形", $"{model.TotalVertexCount:N0} / {model.TotalTriangleCount:N0}");
            Add("纹理", $"{asset?.TexturePaths.Count ?? model.TexturesUsed} 张（限 {TextureMaxSize}）");
            Add("动作 / 表情", $"{runtime.Motions.Count} / {runtime.Expressions.Count}");
            Add("物理", string.IsNullOrWhiteSpace(asset?.PhysicsPath) ? "无" : Path.GetFileName(asset!.PhysicsPath));
            Add("纹理尺寸上限", TextureMaxSize + " px");
            Add("加载耗时", $"{loadMilliseconds:0} ms");
            Add("模型目录", asset?.Directory ?? "（未知）");
        }
        catch (Exception ex)
        {
            InfoRows.Add(new ModelInfoRow("信息读取", "失败：" + ex.Message));
        }
    }

    private void BuildParameterGroups()
    {
        ParameterGroups.Clear();
        var runtime = Runtime;
        if (runtime is null || !runtime.IsLoaded)
        {
            ParameterTotalText = "0 个参数";
            return;
        }

        List<ParameterBinding> bindings;
        try { bindings = runtime.Parameters.ToList(); }
        catch { return; }

        var filter = ParameterSearchText.Trim();
        var matched = filter.Length == 0
            ? bindings
            : bindings.Where(binding => Matches(binding.DisplayName, filter) || Matches(binding.Id, filter)).ToList();

        // cdi3 分组的顺序 = 参数第一次出现的顺序（分组名由模型运行时从 cdi3 填好）
        var groups = matched
            .GroupBy(binding => string.IsNullOrWhiteSpace(binding.GroupName) ? "其他" : binding.GroupName)
            .ToList();
        var searching = filter.Length > 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = new ParameterGroupViewModel(groups[i].Key, groups[i].ToList());
            if (searching || i == 0) group.IsExpanded = true;
            ParameterGroups.Add(group);
        }

        ParameterTotalText = filter.Length == 0
            ? $"{bindings.Count} 个参数 · {groups.Count} 组"
            : $"搜到 {matched.Count} 个 / 共 {bindings.Count} 个参数";
    }

    private static bool Matches(string? text, string filter)
        => !string.IsNullOrEmpty(text)
           && text.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void BuildPartGroups()
    {
        PartGroups.Clear();
        var runtime = Runtime;
        if (runtime is null || !runtime.IsLoaded)
        {
            PartTotalText = "0 个部件";
            return;
        }

        List<PartBinding> bindings;
        try { bindings = runtime.Parts.ToList(); }
        catch { return; }

        var groups = bindings
            .GroupBy(binding => string.IsNullOrWhiteSpace(binding.GroupName) ? "其他" : binding.GroupName)
            .ToList();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = new PartGroupViewModel(groups[i].Key, groups[i].ToList());
            if (i == 0) group.IsExpanded = true;
            PartGroups.Add(group);
        }

        PartTotalText = $"{bindings.Count} 个部件 · {groups.Count} 组";
    }

    private void BuildMotionRows(ModelRuntime runtime)
    {
        Motions.Clear();
        try
        {
            foreach (var motion in runtime.Motions)
            {
                // 默认「单次播放」（播完自动回 IDLE 待机）；要循环就勾行尾的循环框
                Motions.Add(new MotionRowViewModel(motion, Cfg.MotionLoopDefault));
            }
        }
        catch { /* 运行时还在准备中 */ }
        MotionSummaryText = $"{Motions.Count} 个动作";
    }

    private void BuildExpressionRows(ModelRuntime runtime)
    {
        Expressions.Clear();
        _activeExpression = null;
        try
        {
            foreach (var expression in runtime.Expressions)
            {
                Expressions.Add(new ExpressionRowViewModel(expression));
            }
        }
        catch { /* 运行时还在准备中 */ }
        UpdateExpressionSummary();
    }

    private void BuildIdleMotionOptions(ModelRuntime runtime)
    {
        IdleMotionOptions.Clear();
        IdleMotionOptions.Add(new OptionItem<string>("", "自动（按动作文件建议）"));
        try
        {
            foreach (var motion in runtime.Motions)
            {
                IdleMotionOptions.Add(new OptionItem<string>(
                    motion.Name,
                    motion.IsIdleCandidate ? motion.Name + "（推荐）" : motion.Name));
            }
        }
        catch { /* 运行时还在准备中 */ }

        var wanted = Cfg.IdleMotion?.Trim() ?? "";
        _selectedIdleMotion = IdleMotionOptions.FirstOrDefault(option =>
                              string.Equals(option.Value, wanted, StringComparison.OrdinalIgnoreCase))
                          ?? IdleMotionOptions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedIdleMotion));
    }

    // ===================== 参数 / 部件操作 =====================

    private void ResetAllParameters()
    {
        Session.ResetAll();
        foreach (var group in ParameterGroups) group.InvalidateRows();
        foreach (var group in PartGroups) group.InvalidateRows();
        _activeExpression = null;
        UpdateExpressionStates();
        foreach (var row in Motions)
        {
            row.IsCurrent = false;
            row.Progress = 0;
            row.TimeText = "";
        }
        StatusText = "已重置全部参数与部件";
    }

    private void ResetAllParts()
    {
        try
        {
            Runtime?.Model.ResetPartOpacities();
            foreach (var group in PartGroups) group.InvalidateRows();
            Log("部件不透明度已全部恢复显示");
            StatusText = "部件已全部显示";
        }
        catch (Exception ex)
        {
            Log("重置部件失败：" + ex.Message);
        }
    }

    // ===================== 动作 / 表情操作 =====================

    private void PlayMotion(MotionRowViewModel? row)
    {
        if (row is null) return;
        Session.PlayMotion(row.Motion, row.Loop, MotionFadeSeconds);
        StatusText = "正在播放动作：" + row.Name;
    }

    private void StopMotion()
    {
        Session.StopMotion();
        StatusText = "已停止动作";
    }

    /// <summary>
    /// 表情卡点击：官方语义是「同一时间只有一个激活表情」，所以这里是单选切换 ——
    /// 点当前激活的 = 取消回默认；点别的 = 旧的淡出、新表情淡入（交叉淡化）。
    /// </summary>
    private void ToggleExpression(ExpressionRowViewModel? row)
    {
        if (row is null) return;
        var runtime = Runtime;
        if (runtime is null || !runtime.IsLoaded) return;

        var fade = Math.Clamp(Cfg.ExpressionFadeSeconds, 0, 2);
        if (ReferenceEquals(_activeExpression, row))
        {
            _activeExpression = null;
            Session.ClearExpressions(fade);
            UpdateExpressionStates();
            Log($"取消表情：{row.Name}");
            StatusText = $"已取消表情：{row.Name}";
            return;
        }

        ActivateSingleExpression(row.Expression, fade);
    }

    /// <summary>
    /// 切换成「只激活这一个表情」（桌宠双击随机表情也走这里，保证工具页高亮跟着变）。
    /// 先让旧表情淡出，再让新表情淡入 —— 就是官方的交叉淡化。
    /// </summary>
    public void ActivateSingleExpression(ExpressionEntry expression, double fadeSeconds)
    {
        var runtime = Runtime;
        if (runtime is null || !runtime.IsLoaded) return;

        var fade = Math.Clamp(fadeSeconds, 0, 2);
        Session.ClearExpressions(fade);
        Session.ApplyExpression(expression, fade);

        _activeExpression = Expressions.FirstOrDefault(row => ReferenceEquals(row.Expression, expression))
                            ?? Expressions.FirstOrDefault(row =>
                                string.Equals(row.Expression.FilePath, expression.FilePath, StringComparison.OrdinalIgnoreCase))
                            ?? Expressions.FirstOrDefault(row =>
                                string.Equals(row.Name, expression.Name, StringComparison.OrdinalIgnoreCase));
        UpdateExpressionStates();
        Log(_activeExpression is null ? $"施加表情：{expression.Name}" : $"切换表情：{_activeExpression.Name}");
        StatusText = $"当前表情：{_activeExpression?.Name ?? expression.Name}";
    }

    private void ClearAllExpressions()
    {
        var fade = Math.Clamp(Cfg.ExpressionFadeSeconds, 0, 2);
        _activeExpression = null;
        Session.ClearExpressions(fade);
        UpdateExpressionStates();
        Log("已清除表情（回到默认表情）");
        StatusText = "已清除表情";
    }

    /// <summary>把「当前激活的表情」同步到 UI（单选高亮 + 统计文本）。</summary>
    private void UpdateExpressionStates()
    {
        foreach (var row in Expressions) row.IsApplied = ReferenceEquals(row, _activeExpression);
        UpdateExpressionSummary();
    }

    private void UpdateExpressionSummary()
        => ExpressionSummaryText = _activeExpression is null
            ? $"{Expressions.Count} 个表情 · 未激活"
            : $"{Expressions.Count} 个表情 · 当前：{_activeExpression.Name}";

    // ===================== 模型文件夹 =====================

    private void ChooseModelFolder()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择 Live2D 模型文件夹（里面有 .model3.json / .moc3）",
                Multiselect = false,
            };
            var current = Cfg.ModelFolder;
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            {
                dialog.InitialDirectory = current;
            }
            if (dialog.ShowDialog() != true) return;

            Cfg.ModelFolder = dialog.FolderName;
            OnPropertyChanged(nameof(CurrentModelSourceText));
            Log("已选择模型文件夹：" + dialog.FolderName);
            StatusText = "正在加载新模型…";
            Session.ReloadModel();
        }
        catch (Exception ex)
        {
            Log("选择模型文件夹失败：" + ex.Message);
        }
    }

    private void UseBuiltInModel()
    {
        Cfg.ModelFolder = "";
        OnPropertyChanged(nameof(CurrentModelSourceText));
        Log("已切回内置模型（DeepSeek 大肥鱼）");
        StatusText = "正在加载内置模型…";
        Session.ReloadModel();
    }

    private void OpenModelFolder()
    {
        try
        {
            var folder = string.IsNullOrWhiteSpace(Cfg.ModelFolder)
                ? Live2DModelAsset.BuiltInModelDirectory
                : Cfg.ModelFolder;
            if (!Directory.Exists(folder))
            {
                Log("模型文件夹不存在：" + folder);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("打开模型文件夹失败：" + ex.Message);
        }
    }

    // ===================== 截图 / 日志 =====================

    private void TakeScreenshot()
    {
        try
        {
            var bitmap = Session.Runtime?.Renderer?.Bitmap;
            if (bitmap is null)
            {
                Log("还没有渲染出画面，无法截图（等模型加载完再试）");
                StatusText = "截图失败：还没有画面";
                return;
            }

            var fileName = $"live2d-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            var relative = Path.Combine("screenshots", fileName);
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            var path = Storage.WriteBytes(relative, stream.ToArray());
            if (string.IsNullOrWhiteSpace(path))
            {
                Log("截图保存失败（Data 目录不可写？）");
                StatusText = "截图保存失败";
                return;
            }

            LastScreenshotText = path;
            Log("截图已保存：" + path);
            StatusText = "截图已保存：" + path;
            OpenScreenshotFolderCommand.Execute(null);
        }
        catch (Exception ex)
        {
            Log("截图失败：" + ex.Message);
            StatusText = "截图失败：" + ex.Message;
        }
    }

    private void OpenScreenshotFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(Storage.PathOf(Path.Combine("screenshots", "live2d.png")));
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("打开截图文件夹失败：" + ex.Message);
        }
    }

    // ===================== 配置 / 会话事件 =====================

    private void OnConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 配置文件被「重建配置」原地改回默认值时会发一次空属性名：把界面和运行时设置都重新套一遍
        if (!string.IsNullOrEmpty(e.PropertyName)) return;
        _selectedSwayMode = SwayModeOptions.FirstOrDefault(option =>
                                string.Equals(option.Value, Cfg.BodySwayMode, StringComparison.Ordinal))
                            ?? _selectedSwayMode;
        Session.ApplyRuntimeSettings();
        Session.ApplyMocapSettings();
        OnPropertyChanged(string.Empty);
    }

    private void OnPetVisibilityChanged(object? sender, EventArgs e) => Ui(() =>
    {
        OnPropertyChanged(nameof(IsPetVisible));
        OnPropertyChanged(nameof(PetButtonText));
        OnPropertyChanged(nameof(PetStateText));
    });

    private void OnSessionStateChanged(object? sender, EventArgs e) => Ui(() =>
    {
        OnPropertyChanged(nameof(IsPetVisible));
        OnPropertyChanged(nameof(PetButtonText));
        OnPropertyChanged(nameof(PetStateText));
    });

    /// <summary>把所有属性都通知一遍（运行时接上 / 摘掉之后用，省得逐个列）。</summary>
    private void RaiseAll() => OnPropertyChanged(string.Empty);

    /// <summary>设置状态栏文本（会话可能在后台线程调用，内部会切回 UI 线程）。</summary>
    public void SetStatus(string text) => Ui(() => StatusText = text);
}
