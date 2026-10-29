using YuLiangToolkit.Core.Effects;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.App.ViewModels;

/// <summary>
/// 设置页里的一条「光标特效」勾选项：把 <see cref="CursorEffectDefinition"/> 的只读元数据
/// 和可双向绑定的 <see cref="IsSelected"/> 包在一起（设置页用 ItemsControl + CheckBox 直接绑）。
///
/// <para>勾选变化通过构造时传入的回调通知 <see cref="MainViewModel"/> 写配置、刷新渲染层，
/// 所以以后加新特效**不用改设置页 XAML**（清单里加一条就自动出现）。</para>
/// </summary>
public sealed class ShellEffectOption : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    /// <summary>用一条特效定义 + 初始勾选状态 + 变化回调构造。</summary>
    public ShellEffectOption(CursorEffectDefinition definition, bool isSelected, Action changed)
    {
        Definition = definition;
        _isSelected = isSelected;
        _changed = changed;
    }

    /// <summary>原始定义（Id / 显示名 / 说明 / 触发方式）。</summary>
    public CursorEffectDefinition Definition { get; }

    /// <summary>稳定 Id（存配置 / 渲染层用）。</summary>
    public string Id => Definition.Id;

    /// <summary>设置页显示名。</summary>
    public string Name => Definition.Name;

    /// <summary>设置页一句话说明。</summary>
    public string Description => Definition.Description;

    /// <summary>是不是自动效果（设置页分组 / 样式用）。</summary>
    public bool IsAuto => Definition.Trigger == CursorEffectTrigger.Auto;

    /// <summary>是否勾选；变化时通知外壳写配置并刷新渲染层。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            _changed();
        }
    }
}
