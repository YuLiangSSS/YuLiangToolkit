using System.Collections.ObjectModel;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Model;

namespace YuLiangToolkit.Tools.Live2D.ViewModels;

/// <summary>模型信息卡里的一行「名称 / 值」（参数 / 部件 / 画布这些看一眼就够的信息）。</summary>
public sealed class ModelInfoRow
{
    public ModelInfoRow(string name, string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>信息项名称。</summary>
    public string Name { get; }

    /// <summary>信息项的值。</summary>
    public string Value { get; }
}

/// <summary>
/// 参数分组（按 cdi3 的分组）：<b>折叠时一行都不生成</b>，展开时才把该组的参数行填进来。
/// 这样 509 个参数也不会一次性建出几百个 Slider，滚动 / 展开都流畅。
/// </summary>
public sealed class ParameterGroupViewModel : ObservableObject
{
    private readonly IReadOnlyList<ParameterBinding> _items;
    private bool _isExpanded;

    public ParameterGroupViewModel(string name, IReadOnlyList<ParameterBinding> items)
    {
        Name = name;
        _items = items;
        Rows = new ObservableCollection<ParameterBinding>();
    }

    /// <summary>分组名（cdi3 里的中文分组名）。</summary>
    public string Name { get; }

    /// <summary>组内参数数量。</summary>
    public int Count => _items.Count;

    /// <summary>折叠标题文本（组名 + 数量）。</summary>
    public string HeaderText => $"{Name}（{_items.Count}）";

    /// <summary>组内参数行（展开后才填充）。</summary>
    public ObservableCollection<ParameterBinding> Rows { get; }

    /// <summary>是否展开（绑定到标题复选框）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            if (value) EnsureRows();
        }
    }

    /// <summary>把组内参数行填进 <see cref="Rows"/>（重复调用无副作用）。</summary>
    public void EnsureRows()
    {
        if (Rows.Count == _items.Count) return;
        Rows.Clear();
        foreach (var item in _items) Rows.Add(item);
    }

    /// <summary>参数值被外部改掉（一键重置）后强制重绑一遍，让 Slider 显示新值。</summary>
    public void InvalidateRows()
    {
        if (Rows.Count == 0) return;
        var expanded = _isExpanded;
        Rows.Clear();
        if (expanded) EnsureRows();
    }
}

/// <summary>部件分组：和参数组一样按需生成行（147 个部件不会一次全建出来）。</summary>
public sealed class PartGroupViewModel : ObservableObject
{
    private readonly IReadOnlyList<PartBinding> _items;
    private bool _isExpanded;

    public PartGroupViewModel(string name, IReadOnlyList<PartBinding> items)
    {
        Name = name;
        _items = items;
        Rows = new ObservableCollection<PartBinding>();
    }

    /// <summary>分组名（cdi3 里的中文分组名）。</summary>
    public string Name { get; }

    /// <summary>组内部件数量。</summary>
    public int Count => _items.Count;

    /// <summary>折叠标题文本（组名 + 数量）。</summary>
    public string HeaderText => $"{Name}（{_items.Count}）";

    /// <summary>组内部件行（展开后才填充）。</summary>
    public ObservableCollection<PartBinding> Rows { get; }

    /// <summary>是否展开（绑定到标题复选框）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            if (value) EnsureRows();
        }
    }

    /// <summary>把组内部件行填进 <see cref="Rows"/>（重复调用无副作用）。</summary>
    public void EnsureRows()
    {
        if (Rows.Count == _items.Count) return;
        Rows.Clear();
        foreach (var item in _items) Rows.Add(item);
    }

    /// <summary>部件不透明度被外部改掉（全部显示）后强制重绑一遍，让 Slider 显示新值。</summary>
    public void InvalidateRows()
    {
        if (Rows.Count == 0) return;
        var expanded = _isExpanded;
        Rows.Clear();
        if (expanded) EnsureRows();
    }
}

/// <summary>动作列表里的一行：播放 / 循环勾选 / 是否正在播。</summary>
public sealed class MotionRowViewModel : ObservableObject
{
    private bool _loop;
    private bool _isCurrent;
    private double _progress;
    private string _timeText = "";

    public MotionRowViewModel(MotionEntry motion, bool defaultLoop)
    {
        Motion = motion;
        _loop = defaultLoop;
    }
    /// <summary>对应的动作资源。</summary>
    public MotionEntry Motion { get; }

    /// <summary>动作名。</summary>
    public string Name => Motion.Name;

    /// <summary>时长文本。</summary>
    public string DurationText => $"{Motion.Duration:0.0}s";

    /// <summary>是否是待机候选（界面打个小标）。</summary>
    public bool IsIdleCandidate => Motion.IsIdleCandidate;

    /// <summary>循环播放勾选（每个动作行单独记）。</summary>
    public bool Loop
    {
        get => _loop;
        set => Set(ref _loop, value);
    }

    /// <summary>是不是当前正在播放的动作。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    /// <summary>当前动作进度（0~1，只有正在播的行有意义）。</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, Math.Clamp(value, 0, 1));
    }

    /// <summary>当前动作时间文本。</summary>
    public string TimeText
    {
        get => _timeText;
        set => Set(ref _timeText, value);
    }
}

/// <summary>表情列表里的一行：点一下施加，再点取消（可多个叠加）。</summary>
public sealed class ExpressionRowViewModel : ObservableObject
{
    private bool _isApplied;

    public ExpressionRowViewModel(ExpressionEntry expression)
    {
        Expression = expression;
    }

    /// <summary>对应的表情资源。</summary>
    public ExpressionEntry Expression { get; }

    /// <summary>表情名。</summary>
    public string Name => Expression.Name;

    /// <summary>是否已施加（界面高亮 + 文案）。</summary>
    public bool IsApplied
    {
        get => _isApplied;
        set => Set(ref _isApplied, value);
    }
}

/// <summary>
/// 「😛 面部测试」卡里的一行：直接代理一个 <see cref="CubismParameter"/>。
/// <para>滑杆双向写参数值（CubismParameter 自己会夹到 min/max）；当前值文本也绑同一个
/// <see cref="Value"/>，所以拖动时两边同步。外部（预设 / 动捕 / 眼控 / 表情）改了参数后，
/// 页面 100ms 定时器调 <see cref="RefreshFromModel"/> 把显示刷新到真实值。</para>
/// </summary>
public sealed class FaceParameterRowViewModel : ObservableObject
{
    private readonly CubismParameter _parameter;
    private double _lastNotifiedValue;

    public FaceParameterRowViewModel(CubismParameter parameter, string? displayName)
    {
        _parameter = parameter;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? parameter.Id : displayName!;
        _lastNotifiedValue = parameter.Value;
    }

    /// <summary>参数 Id（模型里的原始名，例如 <c>TongueOut</c>）。</summary>
    public string Id => _parameter.Id;

    /// <summary>界面显示名（cdi3 中文名；没有就用 Id）。</summary>
    public string DisplayName { get; }

    /// <summary>参数最小值（滑杆用）。</summary>
    public double Minimum => _parameter.Minimum;

    /// <summary>参数最大值（滑杆用）。</summary>
    public double Maximum => _parameter.Maximum;

    /// <summary>参数默认值（重置面部用）。</summary>
    public double DefaultValue => _parameter.DefaultValue;

    /// <summary>当前值（双向绑定滑杆；写入即生效，越界由 CubismParameter 夹取）。</summary>
    public double Value
    {
        get => _parameter.Value;
        set
        {
            if (Math.Abs(_parameter.Value - value) < 0.0001) return;
            _parameter.Value = (float)value;
            _lastNotifiedValue = _parameter.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>外部直接改了参数值后刷新界面（值没明显变化就不发通知，避免 100ms 刷一次绑定）。</summary>
    public void RefreshFromModel()
    {
        var current = _parameter.Value;
        if (Math.Abs(current - _lastNotifiedValue) < 0.0005) return;
        _lastNotifiedValue = current;
        OnPropertyChanged(nameof(Value));
    }

    /// <summary>直接写参数值（预设按钮用；写后立刻刷新显示）。</summary>
    public void SetValue(float value)
    {
        _parameter.Value = value;
        _lastNotifiedValue = _parameter.Value;
        OnPropertyChanged(nameof(Value));
    }
}
