using YuLiangToolkit.Core.Effects;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.App.ViewModels;

/// <summary>
/// 设置页里的一条**特效参数滑块**（v0.37.0）：把「显示名 / 说明 / 范围 / 步进 / 单位 / 当前值」
/// 和一条写配置 + 刷新渲染层的回调包在一起（设置页用 ItemsControl + Slider 直接绑）。
///
/// <para>取值范围 / 步进来自 <see cref="CursorEffectRange"/>（源头上是
/// <see cref="CursorEffectTuning"/> 里的静态字段），所以设置页 XAML 和配置校验不会再各写一套数字。</para>
///
/// <para>滑块挂在对应特效项（<see cref="ShellEffectOption.Sliders"/>）下面：设置页只在勾选了那个特效时
/// 才显示它的参数滑块，没勾选时整块不占地方。</para>
/// </summary>
public sealed class ShellSliderOption : ObservableObject
{
    private readonly Action<double> _changed;
    private readonly int _decimals;
    private double _value;

    /// <summary>用一条参数定义 + 初始值 + 变化回调构造（<paramref name="range"/> 提供范围 / 步进）。</summary>
    public ShellSliderOption(
        string id,
        string effectId,
        string label,
        string description,
        CursorEffectRange range,
        double value,
        string unit,
        Action<double> changed)
    {
        Id = id;
        EffectId = effectId;
        Label = label;
        Description = description;
        Minimum = range.Minimum;
        Maximum = range.Maximum;
        Step = range.Step;
        Unit = unit;
        _decimals = Step >= 1 ? 0 : Step >= 0.1 ? 1 : 2;
        _changed = changed;
        _value = Snap(value);
    }

    /// <summary>稳定 Id（诊断 / 以后存「每个参数上次的值」时用）。</summary>
    public string Id { get; }

    /// <summary>这一条参数属于哪个特效（勾选项的 Id；没勾选对应特效时整行置灰）。</summary>
    public string EffectId { get; }

    /// <summary>设置页显示名（例如「粒子爆炸数量」）。</summary>
    public string Label { get; }

    /// <summary>设置页一句话说明（同时作为滑块 ToolTip）。</summary>
    public string Description { get; }

    /// <summary>数值单位（拼在 <see cref="ValueText"/> 后面，例如 " DIP" / " 个/帧" / " ×"）。</summary>
    public string Unit { get; }

    /// <summary>滑块最小值。</summary>
    public double Minimum { get; }

    /// <summary>滑块最大值。</summary>
    public double Maximum { get; }

    /// <summary>滑块步进（键盘一次移动的幅度；吸附刻度也用它）。</summary>
    public double Step { get; }

    /// <summary>PageUp / PageDown / 点轨道一次移动的幅度（10 档）。</summary>
    public double LargeStep => Step * 10;

    /// <summary>数值文案（按步进精度格式化，带单位），例如「1.5 个/帧」「150 DIP」「0.05 ×」。</summary>
    public string ValueText => _value.ToString("F" + _decimals) + Unit;

    /// <summary>
    /// 当前值（双向绑滑块）。赋进来的值会按 <see cref="Step"/> 吸附、夹进范围并通知回调
    /// （写配置 + 通知渲染层）；值没变时不重复通知。
    /// </summary>
    public double Value
    {
        get => _value;
        set
        {
            var snapped = Snap(value);
            if (!Set(ref _value, snapped)) return;
            OnPropertyChanged(nameof(ValueText));
            _changed(snapped);
        }
    }

    /// <summary>对应特效勾选了没有（没勾时整行置灰；由 <see cref="MainViewModel"/> 维护）。</summary>
    /// <summary>按步进吸附到最近的刻度并夹进范围（手改配置 / 程序赋值也走这里，保证值永远合法）。</summary>
    private double Snap(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = Minimum;
        var steps = Math.Round((value - Minimum) / Step, MidpointRounding.AwayFromZero);
        var snapped = Math.Round(Minimum + steps * Step, _decimals);
        return Math.Clamp(snapped, Minimum, Maximum);
    }
}
