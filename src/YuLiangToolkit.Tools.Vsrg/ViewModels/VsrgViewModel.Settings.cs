using System.Collections.ObjectModel;
using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Vsrg.Core;

namespace YuLiangToolkit.Tools.Vsrg.ViewModels;

/// <summary>
/// 「VSRG」的设置页：全屏 / 音频偏移 / 滚动速度 / 音量 / 1~10 轨键位 / 判定档 / 面条尾判。
///
/// <para>所有改动直接写在 <see cref="Cfg"/> 上（配置自动落盘，没有保存按钮）；
/// 本文件只负责把它们组织成界面好用的形态（键位行、判定档列表、提示文字）。</para>
/// </summary>
public sealed partial class VsrgViewModel
{
    /// <summary>键位列表（固定 10 行，实际用前 keyCount 行）。</summary>
    public ObservableCollection<KeyBindingRow> KeyBindings { get; } = new();

    /// <summary>判定档列表（界面直接改这一份，改完同步回配置）。</summary>
    public ObservableCollection<JudgementTierConfig> Tiers { get; } = new();

    /// <summary>滚动速度的界面文本。</summary>
    public string ScrollSpeedText => $"×{Cfg.ScrollSpeed:0.0}";

    /// <summary>note 从屏幕顶落到判定线的时间（毫秒）。</summary>
    public string ApproachMs => $"{Cfg.ApproachMs:0}ms";

    /// <summary>音频偏移的界面文本。</summary>
    public string AudioOffsetText => $"{Cfg.AudioOffsetMs:+0;-0;0} ms";

    /// <summary>音乐音量（0~1）。</summary>
    public double MusicVolume
    {
        get => Cfg.MusicVolume;
        set
        {
            if (Math.Abs(Cfg.MusicVolume - value) < 0.001) return;
            Cfg.MusicVolume = value;
            OnPropertyChanged();
            _audio.Volume = value;
        }
    }

    /// <summary>面条尾判的说明文本（当前生效的窗口）。</summary>
    public string TailWindowText
    {
        get
        {
            if (!Cfg.TailJudgementEnabled) return "面条只判头（尾不参与判定与准确率）";

            var windows = string.Join(" / ",
                Tiers.Where(t => !t.IsMiss && t.WindowMs > 0)
                    .Select(t => $"{t.Name} ±{t.WindowMs * Cfg.TailWindowMultiplier:0.#}ms"));
            return windows.Length == 0 ? "没有可用的判定档" : "尾判窗口：" + windows;
        }
    }

    /// <summary>判定档的合法性提示（不合法时界面上标红；游玩开始时也拦一下）。</summary>
    public string TierValidationText
    {
        get
        {
            var error = GameplayProfile.ValidateTiers(Tiers.Select(t => t.ToTier()).ToList());
            return error ?? "";
        }
    }

    /// <summary>有没有判定档配置错误。</summary>
    public bool HasTierError => TierValidationText.Length > 0;

    /// <summary>允许的键数选项（1K~10K，设置页用来演示 / 帮助理解）。</summary>
    public IReadOnlyList<int> SupportedKeyCounts => VsrgConfig.SupportedKeyCounts;

    /// <summary>键位冲突提示（有重复时标红）。</summary>
    public string KeyConflictText
    {
        get
        {
            var names = KeyBindings.Select(r => r.KeyName).ToList();
            return LaneKeys.HasDuplicate(names, out var duplicate, out var first)
                ? $"第 {duplicate + 1} 轨的键和第 {first + 1} 轨重复了（游玩时先出现的那一轨生效）。"
                : "";
        }
    }

    /// <summary>有没有键位冲突。</summary>
    public bool HasKeyConflict => KeyConflictText.Length > 0;

    // ---------------------------------------------------------------- 命令

    /// <summary>把键位恢复成默认（A S D F G H J K L ;）。</summary>
    public RelayCommand ResetKeysCommand { get; private set; } = null!;

    /// <summary>把判定档恢复成 Standard 四档。</summary>
    public RelayCommand ResetTiersCommand { get; private set; } = null!;

    /// <summary>加一个自定义判定档（从最宽松的档复制一份）。</summary>
    public RelayCommand AddTierCommand { get; private set; } = null!;

    /// <summary>删掉一个判定档（预设不可删）。</summary>
    public RelayCommand RemoveTierCommand { get; private set; } = null!;

    /// <summary>把某个判定档往上挪一位（更严格的方向）。</summary>
    public RelayCommand MoveTierUpCommand { get; private set; } = null!;

    /// <summary>把某个判定档往下挪一位。</summary>
    public RelayCommand MoveTierDownCommand { get; private set; } = null!;

    private void InitializeSettings()
    {
        for (var i = 0; i < VsrgConfig.SupportedKeyCounts.Length; i++)
        {
            KeyBindings.Add(new KeyBindingRow(this, i, VsrgConfig.SupportedKeyCounts[i], Cfg.GetKey(i)));
        }

        SyncTiersFromConfig();

        ResetKeysCommand = new RelayCommand(_ =>
        {
            Cfg.ResetKeys();
            for (var i = 0; i < KeyBindings.Count; i++) KeyBindings[i].SetKeyName(Cfg.GetKey(i));
            NotifyKeyConflict();
        });

        ResetTiersCommand = new RelayCommand(_ =>
        {
            Cfg.ResetTiers();
            SyncTiersFromConfig();
        });

        AddTierCommand = new RelayCommand(_ => AddTier());
        RemoveTierCommand = new RelayCommand(parameter => RemoveTier(parameter as JudgementTierConfig),
            parameter => parameter is JudgementTierConfig { IsPreset: false, IsMiss: false });
        MoveTierUpCommand = new RelayCommand(parameter => MoveTier(parameter as JudgementTierConfig, -1),
            parameter => CanMoveTier(parameter as JudgementTierConfig, -1));
        MoveTierDownCommand = new RelayCommand(parameter => MoveTier(parameter as JudgementTierConfig, +1),
            parameter => CanMoveTier(parameter as JudgementTierConfig, +1));
    }

    /// <summary>
    /// 把配置里的判定档抄到界面用的列表（不改配置）。
    /// 配置里的档位是 POCO list，界面需要能单独刷新的 ObservableCollection。
    /// </summary>
    private void SyncTiersFromConfig()
    {
        Tiers.Clear();
        foreach (var tier in Cfg.Tiers)
        {
            var row = new JudgementTierConfig
            {
                Name = tier.Name,
                WindowMs = tier.WindowMs,
                AccuracyWeight = tier.AccuracyWeight,
                IsMiss = tier.IsMiss,
                IsPreset = tier.IsPreset,
            };

            row.PropertyChanged += OnTierRowChanged;
            Tiers.Add(row);
        }

        NotifyTiersChanged();
    }

    private void OnTierRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        PushTiersToConfig();
        NotifyTiersChanged();
    }

    /// <summary>把界面列表写回配置（顺序 / 名字 / 窗口 / 权重全同步）。</summary>
    private void PushTiersToConfig()
    {
        var snapshot = Tiers.Select(t => new JudgementTierConfig
        {
            Name = t.Name,
            WindowMs = t.WindowMs,
            AccuracyWeight = t.AccuracyWeight,
            IsMiss = t.IsMiss,
            IsPreset = t.IsPreset,
        }).ToList();

        Cfg.Tiers = snapshot;
        Cfg.NotifyTierChanged();
    }

    private void AddTier()
    {
        // 从最宽松的非 Miss 档复制一份，名字加「+」，窗口取中间值 —— 加完立刻能改
        var source = Tiers.LastOrDefault(t => !t.IsMiss) ?? Tiers.LastOrDefault();
        var tier = new JudgementTierConfig
        {
            Name = source is null ? "新判定" : source.Name + "+",
            WindowMs = source is null ? 100 : Math.Min(1000, Math.Round(source.WindowMs * 1.5, 1)),
            AccuracyWeight = source is null ? 50 : Math.Max(0, source.AccuracyWeight - 10),
            IsMiss = false,
            IsPreset = false,
        };

        tier.PropertyChanged += OnTierRowChanged;

        // 插到 Miss 档前面（Miss 永远最后）
        var missIndex = Tiers.IndexOf(Tiers.FirstOrDefault(t => t.IsMiss)!);
        var insertAt = missIndex < 0 ? Tiers.Count : missIndex;
        Tiers.Insert(insertAt, tier);

        PushTiersToConfig();
        NotifyTiersChanged();
    }

    private void RemoveTier(JudgementTierConfig? tier)
    {
        if (tier is null) return;
        if (tier.IsPreset || tier.IsMiss) return;   // 预设 / Miss 不许删（Miss 是兜底）
        if (!Tiers.Remove(tier)) return;

        tier.PropertyChanged -= OnTierRowChanged;
        PushTiersToConfig();
        NotifyTiersChanged();
    }

    private bool CanMoveTier(JudgementTierConfig? tier, int delta)
    {
        if (tier is null || tier.IsMiss) return false;

        var index = Tiers.IndexOf(tier);
        if (index < 0) return false;

        // Miss 档固定排在最后：不许把任何档挪到它后面
        var missIndex = Tiers.IndexOf(Tiers.FirstOrDefault(t => t.IsMiss)!);
        var target = index + delta;
        if (target < 0 || target >= Tiers.Count) return false;
        if (missIndex >= 0 && target > missIndex) return false;

        return true;
    }

    private void MoveTier(JudgementTierConfig? tier, int delta)
    {
        if (tier is null || !CanMoveTier(tier, delta)) return;

        var index = Tiers.IndexOf(tier);
        Tiers.Move(index, index + delta);

        PushTiersToConfig();
        NotifyTiersChanged();
    }

    /// <summary>判定档列表变了：刷新所有依赖它的文字 / 校验状态。</summary>
    private void NotifyTiersChanged()
    {
        OnPropertyChanged(nameof(TailWindowText));
        OnPropertyChanged(nameof(TierValidationText));
        OnPropertyChanged(nameof(HasTierError));
        OnPropertyChanged(nameof(Tiers));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>键位变了：刷新冲突提示。</summary>
    internal void NotifyKeyConflict()
    {
        OnPropertyChanged(nameof(KeyConflictText));
        OnPropertyChanged(nameof(HasKeyConflict));
    }

    /// <summary>把某一轨改成新键（来自改键控件的捕获）。</summary>
    internal void ApplyKeyBinding(int column, string keyName)
    {
        Cfg.SetKey(column, keyName);
        if (column >= 0 && column < KeyBindings.Count) KeyBindings[column].SetKeyName(keyName);
        NotifyKeyConflict();
    }
}

/// <summary>
/// 设置页里的一行键位（第 N 轨 + 当前按键 + 是否正在捕获）。
///
/// <para>捕获逻辑在界面层（<c>KeyBindRow</c> 控件里处理键盘事件），这里只存值并回写配置。</para>
/// </summary>
public sealed class KeyBindingRow : ObservableObject
{
    private readonly VsrgViewModel _owner;
    private string _keyName = "";
    private bool _isCapturing;

    /// <summary>构造一行。</summary>
    public KeyBindingRow(VsrgViewModel owner, int column, int laneNumber, string keyName)
    {
        _owner = owner;
        Column = column;
        LaneNumber = laneNumber;
        _keyName = keyName;
    }

    /// <summary>轨道下标（0 基）。</summary>
    public int Column { get; }

    /// <summary>轨道的显示编号（1 基，界面上写「第 1 轨」）。</summary>
    public int LaneNumber { get; }

    /// <summary>当前按键显示名（<c>A</c> / <c>分号</c> / <c>Space</c>）。</summary>
    public string KeyName => _keyName;

    /// <summary>是不是正在等用户按键。</summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (!Set(ref _isCapturing, value)) return;
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    /// <summary>界面上的显示文本（捕获时显示「按一个键…」）。</summary>
    public string DisplayText => IsCapturing ? "按一个键…" : (KeyName.Length == 0 ? "未设置" : KeyName);

    /// <summary>改按键（由 ViewModel 调；不触发配置写入循环）。</summary>
    public void SetKeyName(string keyName)
    {
        _keyName = keyName ?? "";
        OnPropertyChanged(nameof(KeyName));
        OnPropertyChanged(nameof(DisplayText));
    }

    /// <summary>开始捕获（界面控件用）。</summary>
    public void BeginCapture() => IsCapturing = true;

    /// <summary>取消捕获。</summary>
    public void CancelCapture() => IsCapturing = false;

    /// <summary>捕获到一个键：写进配置并结束捕获。</summary>
    public void CommitCapture(string keyName)
    {
        IsCapturing = false;
        _owner.ApplyKeyBinding(Column, keyName);
    }
}
