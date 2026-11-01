using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Model;
using YuLiangToolkit.Tools.Live2D.Rendering;
using YuLiangToolkit.Tools.Live2D.ViewModels;

using usingRenderOptions = YuLiangToolkit.Tools.Live2D.Rendering.RenderOptions;

namespace YuLiangToolkit.Tools.Live2D.Views;

/// <summary>
/// 「Live2D 桌宠」工具页（模型测试台）。
///
/// <para><b>渲染</b>：不自己起定时器 —— 帧循环在 <see cref="Live2DSession"/> 里（整页 + 桌宠共用一次 Tick）。
/// 本页实现 <see cref="ILive2DFrameTarget"/>，每帧用 <see cref="CubismRenderer.Render"/> 把模型画到
/// <b>页面自己的</b>渲染器位图上（桌宠用另一个渲染器，互不覆盖），再把位图塞给预览 Image。</para>
///
/// <para><b>预览交互</b>：滚轮以鼠标为中心缩放、左键拖动平移、单击做网格命中测试（列在预览下方 + 日志），
/// 鼠标移动换算成画布像素坐标交给运行时做眼睛跟随；「适应窗口」一键回到自动适配。</para>
/// </summary>
public partial class Live2DView : UserControl, ILive2DFrameTarget
{
    private const double MinZoom = 0.15;
    private const double MaxZoom = 8.0;
    private const double DragThreshold = 3.0;

    private readonly usingRenderOptions _renderOptions = new() { TransparentBackground = true };
    private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();

    private Live2DViewModel? _viewModel;
    private Window? _window;

    // 预览相机：默认「适应窗口」；用户缩放 / 平移后切手动（_zoom 是相对 fit 的倍数，平移量用画布输出像素的比例存）
    private bool _manualCamera;
    private double _zoom = 1.0;
    private double _panFractionX;
    private double _panFractionY;

    // 拖动 / 点击判定
    private bool _pressing;
    private bool _dragging;
    private Point _pressPoint;
    private double _pressPanFractionX;
    private double _pressPanFractionY;

    // FPS 统计
    private int _framesSinceFps;
    private double _fps;
    private WriteableBitmap? _lastBitmap;

    public Live2DView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ===================== 生命周期 =====================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not Live2DViewModel viewModel) return;
        _viewModel = viewModel;

        var session = viewModel.Session;
        session.Activate(viewModel);
        session.AttachPageTarget(this);

        if (_window is not null) _window.StateChanged -= OnWindowStateChanged;
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += OnWindowStateChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }

        if (_viewModel is not null)
        {
            _viewModel.Session.DetachPageTarget(this);
            _viewModel.Session.Deactivate(_viewModel);
        }
        _viewModel = null;
        _lastBitmap = null;
        PreviewImage.Source = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => _viewModel?.Session.RefreshFrameLoop();

    // ===================== ILive2DFrameTarget =====================

    /// <summary>页面在可视树里、窗口没最小化、预览区有尺寸时才值得渲染。</summary>
    public bool IsTargetVisible
    {
        get
        {
            if (!IsVisible || _viewModel is null) return false;
            var window = _window ?? Window.GetWindow(this);
            if (window is null || window.WindowState == WindowState.Minimized) return false;
            return PreviewHost.ActualWidth > 4 && PreviewHost.ActualHeight > 4;
        }
    }

    /// <summary>会话每帧调用：渲染本页预览（位图尺寸变了就重绑 Image.Source）。</summary>
    public void RenderFrame(ModelRuntime runtime)
    {
        var viewModel = _viewModel;
        if (viewModel is null || !IsTargetVisible) return;

        var model = runtime.Model;
        var renderScale = Math.Clamp(viewModel.RenderScale, 0.5, 1.5);
        var hostWidth = Math.Max(1.0, PreviewHost.ActualWidth);
        var hostHeight = Math.Max(1.0, PreviewHost.ActualHeight);
        var canvasWidth = Math.Max(1.0, model.CanvasWidthPixels);
        var canvasHeight = Math.Max(1.0, model.CanvasHeightPixels);

        var options = _renderOptions;
        options.TransparentBackground = true;

        if (_manualCamera)
        {
            // 手动画布（滚轮缩放 / 拖动平移）：内容铺满甚至超出预览区，只能按整块预览尺寸出图
            var targetWidth = Math.Max(2, (int)Math.Round(hostWidth * renderScale));
            var targetHeight = Math.Max(2, (int)Math.Round(hostHeight * renderScale));
            var (scale, offsetX, offsetY) = ComputeManualTransform(model, targetWidth, targetHeight);
            options.Width = targetWidth;
            options.Height = targetHeight;
            options.FitCanvas = false;
            options.Scale = scale;
            options.OffsetX = offsetX;
            options.OffsetY = offsetY;
            SetPreviewImageLayout(hostWidth, hostHeight);
        }
        else
        {
            // 适应窗口：只渲染模型在预览区里实际占的那一块（不渲染上下 / 左右的空白），再等比放大显示。
            // 例如预览 855×430、模型竖长只占 210×430 时，以前每帧光栅化 36 万像素，现在只要 9 万（省 ~75%）。
            var fit = Math.Min(hostWidth / canvasWidth, hostHeight / canvasHeight);
            var contentWidth = Math.Max(1.0, canvasWidth * fit);
            var contentHeight = Math.Max(1.0, canvasHeight * fit);
            options.Width = Math.Max(2, (int)Math.Ceiling(contentWidth * renderScale));
            options.Height = Math.Max(2, (int)Math.Ceiling(contentHeight * renderScale));
            options.FitCanvas = true;
            SetPreviewImageLayout(contentWidth, contentHeight);
        }

        // 调试叠加（只影响测试页预览；桌宠始终是干净画面）
        options.ShowDrawableBounds = viewModel.ShowDrawableBounds;
        options.ShowMaskRanges = viewModel.ShowMaskRanges;
        options.HighlightDrawableIndex = viewModel.HighlightDrawableIndex;
        options.ShowMaskBufferForDrawableIndex = viewModel.MaskBufferDrawableIndex;

        runtime.Renderer.Render(model, options);

        // 开发调试：YLT_LIVE2D_PREVIEW_DEBUG=1 时把预览布局 / 位图尺寸写到临时文件（排查拉伸 / 模糊用）
        DumpPreviewDebugFrame(runtime);

        var bitmap = runtime.Renderer.Bitmap;
        if (!ReferenceEquals(_lastBitmap, bitmap))
        {
            _lastBitmap = bitmap;
            PreviewImage.Source = bitmap;
        }

        // FPS / 耗时统计（每 0.5 秒刷新一次界面文本；FPS 按真实出图频率算）
        _framesSinceFps++;
        var elapsed = _fpsWatch.Elapsed.TotalSeconds;
        if (elapsed < 0.5) return;
        _fps = _framesSinceFps / elapsed;
        _framesSinceFps = 0;
        _fpsWatch.Restart();
        viewModel.UpdatePreviewStats(_fps, runtime.Renderer.LastStats, runtime.LastTickMilliseconds);
    }

    /// <summary>设置预览 Image 的显示尺寸（适应窗口时 = 模型实际占的那块；手动时 = 整块预览区）。</summary>
    private void SetPreviewImageLayout(double width, double height)
    {
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0) return;
        // ⚠ 初始 Width/Height 是 NaN：`Math.Abs(NaN - width) > 0.5` 恒为 false，会永远不赋值 → Image 被
        // 布局撑满整个预览区、位图被 Stretch=Fill 横向拉宽（历史 bug：模型看起来「胖 + 糊」）。显式判 NaN。
        if (double.IsNaN(PreviewImage.Width) || Math.Abs(PreviewImage.Width - width) > 0.5) PreviewImage.Width = width;
        if (double.IsNaN(PreviewImage.Height) || Math.Abs(PreviewImage.Height - height) > 0.5) PreviewImage.Height = height;
    }

    private int _previewDebugFrames;

    /// <summary>开发调试：把预览布局关键尺寸写进 %TEMP%\live2d-preview-debug.log（只写前 5 帧）。</summary>
    private void DumpPreviewDebugFrame(ModelRuntime runtime)
    {
        if (_previewDebugFrames >= 5) return;
        var enabled = Environment.GetEnvironmentVariable("YLT_LIVE2D_PREVIEW_DEBUG");
        if (string.IsNullOrWhiteSpace(enabled)) return;
        if (_previewDebugFrames == 0)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "live2d-preview-debug.log"), ""); } catch { }
        }

        _previewDebugFrames++;
        try
        {
            var bitmap = runtime.Renderer.Bitmap;
            var line = $"manual={_manualCamera} host={PreviewHost.ActualWidth:0.#}x{PreviewHost.ActualHeight:0.#} " +
                       $"dpi={VisualTreeHelper.GetDpi(this).DpiScaleX:0.##} " +
                       $"image={PreviewImage.ActualWidth:0.#}x{PreviewImage.ActualHeight:0.#} " +
                       $"imageWH={PreviewImage.Width:0.#}x{PreviewImage.Height:0.#} " +
                       $"bitmap={bitmap?.PixelWidth}x{bitmap?.PixelHeight} " +
                       $"renderScale={_viewModel?.RenderScale:0.##}";
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "live2d-preview-debug.log"), line + Environment.NewLine);
        }
        catch { /* 调试输出失败不影响渲染 */ }
    }

    // ===================== 相机 / 坐标 =====================

    /// <summary>手动模式：画布像素 → 渲染目标像素的缩放 + 偏移（渲染目标 = 整块预览 × 渲染倍率）。</summary>
    private (double Scale, double OffsetX, double OffsetY) ComputeManualTransform(
        CubismModel model, int targetWidth, int targetHeight)
    {
        var canvasWidth = Math.Max(1.0, model.CanvasWidthPixels);
        var canvasHeight = Math.Max(1.0, model.CanvasHeightPixels);
        var fit = Math.Min(targetWidth / canvasWidth, targetHeight / canvasHeight);
        var scale = fit * _zoom;
        return (scale,
            (targetWidth - canvasWidth * scale) / 2 + _panFractionX * targetWidth,
            (targetHeight - canvasHeight * scale) / 2 + _panFractionY * targetHeight);
    }

    /// <summary>当前鼠标位置对应的画布像素坐标（模型没加载时返回 false）。</summary>
    private bool TryGetCanvasPoint(Point positionInPreview, out float canvasX, out float canvasY)
    {
        canvasX = canvasY = 0;
        var runtime = _viewModel?.Runtime;
        if (runtime?.IsLoaded != true) return false;

        var model = runtime.Model;
        var hostWidth = Math.Max(1.0, PreviewHost.ActualWidth);
        var hostHeight = Math.Max(1.0, PreviewHost.ActualHeight);
        var canvasWidth = Math.Max(1.0, model.CanvasWidthPixels);
        var canvasHeight = Math.Max(1.0, model.CanvasHeightPixels);

        if (_manualCamera)
        {
            var renderScale = Math.Clamp(_viewModel!.RenderScale, 0.5, 1.5);
            var targetWidth = Math.Max(2, (int)Math.Round(hostWidth * renderScale));
            var targetHeight = Math.Max(2, (int)Math.Round(hostHeight * renderScale));
            var (scale, offsetX, offsetY) = ComputeManualTransform(model, targetWidth, targetHeight);
            if (scale <= 0.0001) return false;

            var px = positionInPreview.X / hostWidth * targetWidth;
            var py = positionInPreview.Y / hostHeight * targetHeight;
            canvasX = (float)((px - offsetX) / scale);
            canvasY = (float)((py - offsetY) / scale);
            return true;
        }

        var fit = Math.Min(hostWidth / canvasWidth, hostHeight / canvasHeight);
        if (fit <= 0.0001) return false;
        var originX = (hostWidth - canvasWidth * fit) / 2;
        var originY = (hostHeight - canvasHeight * fit) / 2;
        canvasX = (float)((positionInPreview.X - originX) / fit);
        canvasY = (float)((positionInPreview.Y - originY) / fit);
        return true;
    }

    // ===================== 鼠标交互 =====================

    /// <summary>适应窗口：清掉手动的缩放 / 平移。</summary>
    private void OnResetViewClick(object sender, RoutedEventArgs e)
    {
        _manualCamera = false;
        _zoom = 1.0;
        _panFractionX = 0;
        _panFractionY = 0;
        if (_viewModel is not null) _viewModel.StatusText = "预览已恢复「适应窗口」";
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 尺寸变化后不用做事：每帧都按当前尺寸重新算适配（手动模式下平移按比例保留）
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;   // 别让外层 ScrollViewer 跟着滚
        var runtime = _viewModel?.Runtime;
        if (runtime?.IsLoaded != true) return;

        var position = e.GetPosition(PreviewHost);
        // 缩放前鼠标下的画布点（缩放后要保持不动）
        if (!TryGetCanvasPoint(position, out var canvasX, out var canvasY)) return;

        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001) return;

        _manualCamera = true;
        _zoom = newZoom;

        // 进入手动模式后渲染目标 = 整块预览 × 渲染倍率；由「canvas 点仍在鼠标下」反推比例平移量
        var model = runtime.Model;
        var hostWidth = Math.Max(1.0, PreviewHost.ActualWidth);
        var hostHeight = Math.Max(1.0, PreviewHost.ActualHeight);
        var renderScale = Math.Clamp(_viewModel!.RenderScale, 0.5, 1.5);
        var targetWidth = Math.Max(2, (int)Math.Round(hostWidth * renderScale));
        var targetHeight = Math.Max(2, (int)Math.Round(hostHeight * renderScale));
        var (newScale, _, _) = ComputeManualTransform(model, targetWidth, targetHeight);

        var px = position.X / hostWidth * targetWidth;
        var py = position.Y / hostHeight * targetHeight;
        var desiredOffsetX = px - canvasX * newScale;
        var desiredOffsetY = py - canvasY * newScale;

        var canvasWidth = Math.Max(1.0, model.CanvasWidthPixels);
        var canvasHeight = Math.Max(1.0, model.CanvasHeightPixels);
        _panFractionX = (desiredOffsetX - (targetWidth - canvasWidth * newScale) / 2) / targetWidth;
        _panFractionY = (desiredOffsetY - (targetHeight - canvasHeight * newScale) / 2) / targetHeight;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _pressing = true;
        _dragging = false;
        _pressPoint = e.GetPosition(PreviewHost);
        _pressPanFractionX = _panFractionX;
        _pressPanFractionY = _panFractionY;
        PreviewHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(PreviewHost);

        if (_pressing)
        {
            var dx = position.X - _pressPoint.X;
            var dy = position.Y - _pressPoint.Y;
            if (!_dragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold)) _dragging = true;
            if (_dragging)
            {
                _manualCamera = true;
                var width = Math.Max(1.0, PreviewHost.ActualWidth);
                var height = Math.Max(1.0, PreviewHost.ActualHeight);
                _panFractionX = _pressPanFractionX + dx / width;
                _panFractionY = _pressPanFractionY + dy / height;
                e.Handled = true;
            }
            return;
        }

        // 眼睛跟随：鼠标位置换算成画布像素交给运行时
        if (!TryGetCanvasPoint(position, out var canvasX, out var canvasY)) return;
        _viewModel!.Session.SetPageEyeTarget(canvasX, canvasY);
        _viewModel.SetEyeTargetDisplay(canvasX, canvasY);
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressing) return;
        _pressing = false;
        PreviewHost.ReleaseMouseCapture();
        e.Handled = true;

        // 没有拖动 = 单击：做一次网格命中测试
        if (_dragging)
        {
            _dragging = false;
            return;
        }
        TryPickDrawable(e.GetPosition(PreviewHost));
    }

    private void OnPreviewMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_pressing) return;
        _pressing = false;
        _dragging = false;
        PreviewHost.ReleaseMouseCapture();

        _viewModel?.Session.ClearPageEyeTarget();
        _viewModel?.ClearEyeTargetDisplay();
    }

    /// <summary>
    /// 点击命中测试：把鼠标位置换成画布坐标，在所有「可见 + 不透明」的网格里找包围盒命中的那个
    /// （多个包含时取面积最小的 = 视觉上最靠前的细节）。
    /// </summary>
    private void TryPickDrawable(Point positionInPreview)
    {
        var runtime = _viewModel?.Runtime;
        if (runtime?.IsLoaded != true) return;
        if (!TryGetCanvasPoint(positionInPreview, out var canvasX, out var canvasY)) return;

        CubismDrawable? hit = null;
        var hitArea = float.MaxValue;
        float hitMinX = 0, hitMinY = 0, hitMaxX = 0, hitMaxY = 0;

        try
        {
            foreach (var drawable in runtime.Model.Drawables)
            {
                if (!drawable.IsVisible || drawable.Opacity < 0.02f) continue;
                if (!drawable.TryGetCanvasBounds(out var minX, out var minY, out var maxX, out var maxY)) continue;
                if (canvasX < minX || canvasX > maxX || canvasY < minY || canvasY > maxY) continue;

                var area = (maxX - minX) * (maxY - minY);
                if (area >= hitArea) continue;
                hit = drawable;
                hitArea = area;
                hitMinX = minX;
                hitMinY = minY;
                hitMaxX = maxX;
                hitMaxY = maxY;
            }
        }
        catch (Exception ex)
        {
            _viewModel?.Log("点击命中测试失败：" + ex.Message);
            return;
        }

        if (hit is null)
        {
            _viewModel?.SetPickedDrawable(-1, "", "");
            _viewModel!.StatusText = "点击位置没有命中网格（空白 / 透明区域）";
            return;
        }

        var detail = $"包围盒 ({hitMinX:0},{hitMinY:0}) ~ ({hitMaxX:0},{hitMaxY:0})，" +
                     $"绘制顺序 {hit.RenderOrder}，{(hit.IsAdditive ? "加算" : hit.IsMultiplicative ? "乘算" : "普通")}混合";
        _viewModel?.SetPickedDrawable(hit.Index, hit.Id, detail);
        if (_viewModel is not null) _viewModel.StatusText = "命中网格：" + hit.Id;
    }
}
