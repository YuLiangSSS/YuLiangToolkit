using System;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 时间轴视口：把「毫秒」和「屏幕 Y 像素」互转，并负责缩放 / 平移的夹取。
///
/// <para><b>约定</b>：<b>时间从上往下增长</b>（跟 osu!mania 官方编辑器、以及所有下落式音游的
/// 编辑习惯一致：早的音符在上面）。所以 <c>Y = OriginY + (Time − StartTime) × PxPerMs</c>。</para>
///
/// <para><b>为什么单独抽一个类</b>：所有绘制 / 命中测试都只用这里的两个方法换算，
/// 缩放 / 跟随播放头的逻辑就集中在 <see cref="ScrollTo"/> / <see cref="ZoomAt"/> 两处，
/// 不会出现「某处忘了乘缩放」的错位，也能无头断言往返精度。</para>
/// </summary>
public sealed class ChartViewport
{
    /// <summary>最小缩放（每毫秒像素数）：1 px / 100 ms，即 100 ms 占 1 像素。</summary>
    public const double MinPxPerMs = 0.002;

    /// <summary>最大缩放：每毫秒 2 像素（1 秒铺满 2000 像素，适合对 1/16 拍）。</summary>
    public const double MaxPxPerMs = 2.0;

    /// <summary>默认缩放：每毫秒 0.15 像素（1 秒 ≈ 150 像素，一屏 6~8 秒）。</summary>
    public const double DefaultPxPerMs = 0.15;

    /// <summary>视口顶部对应的时间（毫秒）。</summary>
    public double StartTime { get; private set; }

    /// <summary>视口高度（像素）。</summary>
    public double Height { get; private set; } = 600;

    /// <summary>每毫秒多少像素。</summary>
    public double PxPerMs { get; private set; } = DefaultPxPerMs;

    /// <summary>可视时间跨度（毫秒）。</summary>
    public double VisibleMs => PxPerMs <= 0 ? 0 : Height / PxPerMs;

    /// <summary>视口底部对应的时间（毫秒）。</summary>
    public double EndTime => StartTime + VisibleMs;

    /// <summary>更新视口高度（控件尺寸变化时调）。</summary>
    public void Resize(double height)
    {
        Height = Math.Max(1, height);
    }

    /// <summary>时间 → Y（相对视口顶部）。</summary>
    public double TimeToY(double timeMs) => (timeMs - StartTime) * PxPerMs;

    /// <summary>Y → 时间。反向换算，与 <see cref="TimeToY"/> 严格互逆。</summary>
    public double YToTime(double y) => StartTime + y / PxPerMs;

    /// <summary>滚到「让 <paramref name="timeMs"/> 位于视口顶部」。可超出谱面范围（空白也能看）。</summary>
    public void ScrollTo(double timeMs)
        => StartTime = double.IsFinite(timeMs) ? timeMs : StartTime;

    /// <summary>按像素滚动（正值 = 往未来滚，即内容往上走）。</summary>
    public void ScrollByPixels(double deltaY) => StartTime += deltaY / PxPerMs;

    /// <summary>
    /// 以某个屏幕点为中心缩放（滚轮缩放的标准行为：鼠标下的时间点保持不动）。
    /// </summary>
    /// <param name="anchorY">缩放中心的 Y（视口内像素）。</param>
    /// <param name="factor">倍率（&gt;1 = 放大 = 每毫秒占更多像素）。</param>
    public void ZoomAt(double anchorY, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;

        var anchorTime = YToTime(anchorY);
        var newScale = Math.Clamp(PxPerMs * factor, MinPxPerMs, MaxPxPerMs);
        if (Math.Abs(newScale - PxPerMs) < 1e-12) return;

        PxPerMs = newScale;
        // 让 anchorTime 仍然落在 anchorY 上
        StartTime = anchorTime - anchorY / PxPerMs;
    }

    /// <summary>直接设缩放（工具栏用），锚点默认视口中心。</summary>
    public void SetScale(double pxPerMs, double? anchorY = null)
    {
        var newScale = Math.Clamp(pxPerMs, MinPxPerMs, MaxPxPerMs);
        if (Math.Abs(newScale - PxPerMs) < 1e-12) return;

        var anchor = anchorY ?? Height / 2;
        var anchorTime = YToTime(anchor);
        PxPerMs = newScale;
        StartTime = anchorTime - anchor / PxPerMs;
    }

    /// <summary>
    /// 播放跟随：让播放头固定在视口某个比例位置（默认 70%，即下方留一点前瞻）。
    /// 只在播放头跑出舒适区时才滚，避免每帧都在动。
    /// </summary>
    /// <param name="timeMs">播放头时间。</param>
    /// <param name="comfortLow">舒适区上边界（视口比例 0~1）。</param>
    /// <param name="comfortHigh">舒适区下边界。</param>
    /// <param name="targetRatio">需要滚动时把播放头放到哪。</param>
    public void FollowPlayhead(double timeMs, double comfortLow = 0.35, double comfortHigh = 0.75,
        double targetRatio = 0.6)
    {
        var visible = VisibleMs;
        if (visible <= 0) return;

        var ratio = (timeMs - StartTime) / visible;
        if (ratio >= comfortLow && ratio <= comfortHigh) return;

        StartTime = timeMs - visible * targetRatio;
    }

    /// <summary>把整张谱面（或某个时间区间）缩放到刚好放进视口。</summary>
    public void FitRange(double from, double to, double paddingRatio = 0.03)
    {
        var span = Math.Max(1, to - from);
        var padded = span * (1 + paddingRatio * 2);
        SetScale(Height / padded);
        StartTime = from - padded * paddingRatio;
    }

    /// <summary>当前缩放相对默认值的倍数（工具栏显示用）。</summary>
    public double ZoomRatio => PxPerMs / DefaultPxPerMs;
}
