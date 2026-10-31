namespace YuLiangToolkit.Core.Mvvm;

/// <summary>
/// 可拖拽分隔条（<see cref="PaneSplitter"/>）的全局外观设置。
///
/// <para><b>提示线透明度</b>：分隔条平时是**看不见**的（只有鼠标移到缝隙上才亮起蓝线）。
/// 但看不见的缝隙不好找，所以可以留一条「提示线」——本设置控制鼠标**不在上面时**这条线的透明度：
/// <c>0</c> = 完全看不见；<c>0.18</c> = 默认的淡淡一条；<c>1</c> = 一直显示一条实心蓝线。
/// 值由外壳（主页设置 → 界面布局）读写 <c>Config\shell\config.json</c>，改一下所有工具页立即生效。</para>
///
/// <para>为什么放在 Core：分隔条控件本身在 Core，而设置值在外壳配置里；
/// 外壳启动 / 改滑块 / 重建配置后调用 <see cref="SetRestOpacity"/> 推给所有已存在的分隔条
/// （每个 <see cref="PaneSplitter"/> 在加载时订阅 <see cref="RestOpacityChanged"/>，卸载时退订）。</para>
/// </summary>
public static class PaneSplitterSettings
{
    /// <summary>默认提示线透明度（0.18 ≈ 以前的淡蓝细线，够当提示又不抢眼）。</summary>
    public const double DefaultRestOpacity = 0.18;

    private static double _restOpacity = DefaultRestOpacity;

    /// <summary>鼠标不在分隔条上时，提示线的透明度（0~1）。</summary>
    public static double RestOpacity => _restOpacity;

    /// <summary>提示线透明度变化（UI 线程；具体值见 <see cref="RestOpacity"/>）。</summary>
    public static event EventHandler? RestOpacityChanged;

    /// <summary>设置提示线透明度（自动夹到 0~1；值没变就不触发事件）。</summary>
    public static void SetRestOpacity(double value)
    {
        if (!double.IsFinite(value)) return;
        value = Math.Clamp(value, 0, 1);
        if (Math.Abs(value - _restOpacity) < 0.0005) return;

        _restOpacity = value;
        RestOpacityChanged?.Invoke(null, EventArgs.Empty);
    }
}
