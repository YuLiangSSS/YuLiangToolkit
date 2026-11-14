using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Vsrg.Core;

namespace YuLiangToolkit.Tools.Vsrg;

/// <summary>
/// 「VSRG」的强类型配置 → <c>Config\vsrg\config.json</c>（改一下最多 400ms 自动落盘，没有保存按钮）。
///
/// <para>只存**该记住的东西**：游玩开关（全屏 / 偏移 / 滚动速度）、1~10 轨键位、判定档与面条尾判。
/// 谱面集本身是用户数据（<c>Data\vsrg\Mapset\</c>），不在这里；成绩**不记录**（按需求不做历史最佳）。</para>
///
/// <para>键位存的是**显示名**（<c>A</c> / <c>分号</c> / <c>Space</c>），和 Core 的
/// <see cref="YuLiangToolkit.Core.Input.KeyComboNames"/> 进出双向一致：配置文件人眼可读、可手改。</para>
/// </summary>
public sealed class VsrgConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名，自检会核对它与 ITool.Id 一致）。</summary>
    [JsonIgnore]
    public static string ToolId => "vsrg";

    /// <summary>滚动速度下限（越低 note 落得越慢）。</summary>
    public const double MinScrollSpeed = 1.0;

    /// <summary>滚动速度上限。</summary>
    public const double MaxScrollSpeed = 20.0;

    /// <summary>滚动速度默认值。</summary>
    public const double DefaultScrollSpeed = 7.0;

    /// <summary>速度换算基准：<c>速度 1.0</c> 时 note 用 900ms 从屏幕顶落到判定线。</summary>
    public const double ApproachBaseMs = 900.0;

    /// <summary>音频偏移下限（毫秒）。</summary>
    public const int MinAudioOffsetMs = -500;

    /// <summary>音频偏移上限（毫秒）。</summary>
    public const int MaxAudioOffsetMs = 500;

    /// <summary>面条尾部判定宽度乘数的下限。</summary>
    public const double MinTailMultiplier = 0.1;

    /// <summary>面条尾部判定宽度乘数的上限。</summary>
    public const double MaxTailMultiplier = 10.0;

    /// <summary>支持的键数（1K ~ 10K）。</summary>
    public static readonly int[] SupportedKeyCounts = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

    /// <summary>1~10 轨的默认键位（显示名，见 <see cref="KeyBinding"/>）。</summary>
    public static readonly string[] DefaultKeys =
    {
        "A", "S", "D", "F", "G", "H", "J", "K", "L", "分号",
    };

    // ---------------------------------------------------------------- 游玩设置

    private bool _playFullscreen;

    /// <summary>点「开始游玩」时是否立刻进入全屏（游玩结束自动退出全屏并进结算）。</summary>
    public bool PlayFullscreen
    {
        get => _playFullscreen;
        set => Set(ref _playFullscreen, value);
    }

    private int _audioOffsetMs;

    /// <summary>
    /// 音频偏移（毫秒，−500 ~ 500）。正数 = 音乐听起来偏晚时用（判定时间整体往后挪）。
    /// 游玩时叠加在播放位置上，见 <see cref="Audio.AudioClockEngine"/> 的说明。
    /// </summary>
    public int AudioOffsetMs
    {
        get => _audioOffsetMs;
        set => Set(ref _audioOffsetMs, Math.Clamp(value, MinAudioOffsetMs, MaxAudioOffsetMs));
    }

    private double _scrollSpeed = DefaultScrollSpeed;

    /// <summary>音符滚动速度（1.0 ~ 20.0）；越高 note 落得越快。</summary>
    public double ScrollSpeed
    {
        get => _scrollSpeed;
        set => Set(ref _scrollSpeed, Math.Clamp(double.IsFinite(value) ? value : DefaultScrollSpeed,
            MinScrollSpeed, MaxScrollSpeed));
    }

    private double _musicVolume = 0.85;

    /// <summary>游玩时音乐音量（0~1）。</summary>
    public double MusicVolume
    {
        get => _musicVolume;
        set => Set(ref _musicVolume, Math.Clamp(double.IsFinite(value) ? value : 0.85, 0, 1));
    }

    private bool _pauseOnLostFocus = true;

    /// <summary>切走程序窗口时自动暂停（默认开，免得回来时漏完一整首）。</summary>
    public bool PauseOnLostFocus
    {
        get => _pauseOnLostFocus;
        set => Set(ref _pauseOnLostFocus, value);
    }

    private bool _quitToResult;

    /// <summary>
    /// 游玩中「退出当前歌曲」后去哪：<c>true</c> = 进结算（按已打的部分算成绩），
    /// <c>false</c> = 直接回选歌页（默认，退出就当没打这一局）。
    /// </summary>
    public bool QuitToResult
    {
        get => _quitToResult;
        set => Set(ref _quitToResult, value);
    }

    // ---------------------------------------------------------------- 键位（1~10 轨）

    private string _keys1 = DefaultKeys[0];
    private string _keys2 = DefaultKeys[1];
    private string _keys3 = DefaultKeys[2];
    private string _keys4 = DefaultKeys[3];
    private string _keys5 = DefaultKeys[4];
    private string _keys6 = DefaultKeys[5];
    private string _keys7 = DefaultKeys[6];
    private string _keys8 = DefaultKeys[7];
    private string _keys9 = DefaultKeys[8];
    private string _keys10 = DefaultKeys[9];

    /// <summary>第 1 轨按键。</summary>
    public string Key1 { get => _keys1; set => Set(ref _keys1, value ?? ""); }

    /// <summary>第 2 轨按键。</summary>
    public string Key2 { get => _keys2; set => Set(ref _keys2, value ?? ""); }

    /// <summary>第 3 轨按键。</summary>
    public string Key3 { get => _keys3; set => Set(ref _keys3, value ?? ""); }

    /// <summary>第 4 轨按键。</summary>
    public string Key4 { get => _keys4; set => Set(ref _keys4, value ?? ""); }

    /// <summary>第 5 轨按键。</summary>
    public string Key5 { get => _keys5; set => Set(ref _keys5, value ?? ""); }

    /// <summary>第 6 轨按键。</summary>
    public string Key6 { get => _keys6; set => Set(ref _keys6, value ?? ""); }

    /// <summary>第 7 轨按键。</summary>
    public string Key7 { get => _keys7; set => Set(ref _keys7, value ?? ""); }

    /// <summary>第 8 轨按键。</summary>
    public string Key8 { get => _keys8; set => Set(ref _keys8, value ?? ""); }

    /// <summary>第 9 轨按键。</summary>
    public string Key9 { get => _keys9; set => Set(ref _keys9, value ?? ""); }

    /// <summary>第 10 轨按键。</summary>
    public string Key10 { get => _keys10; set => Set(ref _keys10, value ?? ""); }

    /// <summary>取某一轨的按键显示名（轨道号 0 基；越界给空串）。</summary>
    public string GetKey(int column) => column switch
    {
        0 => Key1,
        1 => Key2,
        2 => Key3,
        3 => Key4,
        4 => Key5,
        5 => Key6,
        6 => Key7,
        7 => Key8,
        8 => Key9,
        9 => Key10,
        _ => "",
    };

    /// <summary>按显示名设置某一轨的按键（轨道号 0 基；越界忽略）。</summary>
    public void SetKey(int column, string keyName)
    {
        switch (column)
        {
            case 0: Key1 = keyName; break;
            case 1: Key2 = keyName; break;
            case 2: Key3 = keyName; break;
            case 3: Key4 = keyName; break;
            case 4: Key5 = keyName; break;
            case 5: Key6 = keyName; break;
            case 6: Key7 = keyName; break;
            case 7: Key8 = keyName; break;
            case 8: Key9 = keyName; break;
            case 9: Key10 = keyName; break;
        }
    }

    /// <summary>把 1~10 轨键位恢复成默认（A S D F G H J K L ;）。</summary>
    public void ResetKeys()
    {
        Key1 = DefaultKeys[0];
        Key2 = DefaultKeys[1];
        Key3 = DefaultKeys[2];
        Key4 = DefaultKeys[3];
        Key5 = DefaultKeys[4];
        Key6 = DefaultKeys[5];
        Key7 = DefaultKeys[6];
        Key8 = DefaultKeys[7];
        Key9 = DefaultKeys[8];
        Key10 = DefaultKeys[9];
    }

    // ---------------------------------------------------------------- 判定

    private bool _tailJudgementEnabled = true;

    /// <summary>是否开启面条（LN）尾部判定。</summary>
    public bool TailJudgementEnabled
    {
        get => _tailJudgementEnabled;
        set => Set(ref _tailJudgementEnabled, value);
    }

    private double _tailWindowMultiplier = 1.5;

    /// <summary>
    /// 面条尾部判定宽度乘数（0.1 ~ 10）：尾判窗口 = 该档窗口 × 它。
    /// 尾部手感普遍比头难，所以默认放宽到 1.5 倍（普通 35ms 的档 → 尾判 52.5ms）。
    /// </summary>
    public double TailWindowMultiplier
    {
        get => _tailWindowMultiplier;
        set => Set(ref _tailWindowMultiplier, Math.Clamp(
            double.IsFinite(value) ? value : 1.5, MinTailMultiplier, MaxTailMultiplier));
    }

    private List<JudgementTierConfig> _tiers = CreateStandardTiers();

    /// <summary>
    /// 判定档（顺序 = 匹配顺序，从严格到宽松）。默认是 Standard 四档：
    /// Marvelous / Good / Bad / Miss。
    /// </summary>
    public List<JudgementTierConfig> Tiers
    {
        get => _tiers;
        set => Set(ref _tiers, value is { Count: > 0 } ? value : CreateStandardTiers());
    }

    /// <summary>Standard 预设四档（新建配置用的默认值）。</summary>
    public static List<JudgementTierConfig> CreateStandardTiers()
        => GameplayProfile.StandardTiers().Select(JudgementTierConfig.FromTier).ToList();

    /// <summary>把判定档恢复成 Standard 预设。</summary>
    public void ResetTiers() => Tiers = CreateStandardTiers();

    /// <summary>重新抬一次通知（改完判定档列表后让界面刷新）。</summary>
    public void NotifyTierChanged() => OnPropertyChanged(nameof(Tiers));

    /// <summary>按当前配置算「note 从屏幕顶到判定线」的时间（毫秒）。</summary>
    [JsonIgnore]
    public double ApproachMs => ApproachBaseMs / Math.Clamp(ScrollSpeed, MinScrollSpeed, MaxScrollSpeed);

    /// <summary>把当前配置拍成一份游玩用的判定快照。</summary>
    public GameplayProfile BuildProfile()
        => new(Tiers.Select(t => t.ToTier()).ToList(), TailJudgementEnabled, TailWindowMultiplier);
}

/// <summary>
/// 判定档的**持久化形态**（存进 JSON）。和 <see cref="JudgementTier"/> 分开是因为：
/// 运行时那一份是不可变快照（游玩途中改设置不影响这一局），配置这一份要能被界面改。
/// </summary>
public sealed class JudgementTierConfig : ObservableObject
{
    private string _name = "";
    private double _windowMs;
    private double _accuracyWeight;
    private bool _isMiss;
    private bool _isPreset;

    /// <summary>档位名。</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, (value ?? "").Trim());
    }

    /// <summary>判定窗口（±毫秒）。</summary>
    public double WindowMs
    {
        get => _windowMs;
        set => Set(ref _windowMs, Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1000));
    }

    /// <summary>准确率权重（0~100）。</summary>
    public double AccuracyWeight
    {
        get => _accuracyWeight;
        set => Set(ref _accuracyWeight, Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100));
    }

    /// <summary>是不是 Miss（兜底）档。</summary>
    public bool IsMiss
    {
        get => _isMiss;
        set => Set(ref _isMiss, value);
    }

    /// <summary>是不是内置预设档（预设不能删除）。</summary>
    public bool IsPreset
    {
        get => _isPreset;
        set => Set(ref _isPreset, value);
    }

    /// <summary>窗口的界面文本（<c>±22ms</c> / <c>兜底</c>）。</summary>
    [JsonIgnore]
    public string WindowText => IsMiss ? "兜底" : $"±{WindowMs:0.#}ms";

    /// <summary>权重的界面文本（<c>100%</c>）。</summary>
    [JsonIgnore]
    public string WeightText => $"{AccuracyWeight:0.#}%";

    /// <summary>转成运行时用的不可变档位。</summary>
    public JudgementTier ToTier() => new()
    {
        Name = Name,
        WindowMs = WindowMs,
        AccuracyWeight = AccuracyWeight,
        IsMiss = IsMiss,
        IsPreset = IsPreset,
    };

    /// <summary>从运行时档位复制一份（造默认值用）。</summary>
    public static JudgementTierConfig FromTier(JudgementTier tier) => new()
    {
        Name = tier.Name,
        WindowMs = tier.WindowMs,
        AccuracyWeight = tier.AccuracyWeight,
        IsMiss = tier.IsMiss,
        IsPreset = tier.IsPreset,
    };

    /// <summary>复制一份（界面里「加一档」时用）。</summary>
    public JudgementTierConfig Clone() => new()
    {
        Name = Name,
        WindowMs = WindowMs,
        AccuracyWeight = AccuracyWeight,
        IsMiss = false,
        IsPreset = false,
    };

    /// <summary>调试用摘要。</summary>
    public override string ToString() => $"{Name} {WindowText} {WeightText}";
}
