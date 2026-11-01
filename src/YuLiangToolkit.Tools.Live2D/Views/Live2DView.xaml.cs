using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YuLiangToolkit.Tools.Live2D.Audio;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Mocap;
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

    // ===== 100ms 辅助刷新：动捕预览小窗 / 动捕状态行 / 身体晃动实时值与折线图 =====
    private const int SwaySampleCount = 50;      // 5 秒 @ 10Hz（100ms 采样一次）
    private readonly List<double>[] _swaySeries =
    {
        new(), new(), new(), new(),
    };

    private DispatcherTimer? _auxTimer;
    private WriteableBitmap? _mocapBitmap;
    private long _lastMocapPreviewTimestamp = -1;

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

        // 100ms 辅助刷新（动捕预览 / 状态行 / 身体晃动实时值与折线图）
        if (_auxTimer is null)
        {
            _auxTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            _auxTimer.Tick += OnAuxTimerTick;
        }
        _auxTimer.Start();

        // 枚举摄像头（只枚举设备列表、不会打开摄像头）
        _ = viewModel.EnsureCamerasLoadedAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }

        try { _auxTimer?.Stop(); } catch { /* 忽略 */ }

        if (_viewModel is not null)
        {
            _viewModel.Session.DetachPageTarget(this);
            _viewModel.Session.Deactivate(_viewModel);
        }
        _viewModel = null;
        _lastBitmap = null;
        PreviewImage.Source = null;

        // 动捕预览也清掉（隐私：页面关掉后不留摄像头画面）
        MocapPreviewImage.Source = null;
        MocapPreviewPlaceholder.Visibility = Visibility.Visible;
        MocapFaceRect.Visibility = Visibility.Collapsed;
        _mocapBitmap = null;
        _lastMocapPreviewTimestamp = -1;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => _viewModel?.Session.RefreshFrameLoop();

    // ===================== 辅助刷新：动捕预览 / 状态行 / 身体晃动 =====================

    private void OnAuxTimerTick(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        UpdateMocapPreview();
        UpdateMocapStatusLine();
        UpdateSwayDisplay();
        _viewModel.RefreshFaceValues();   // 面部测试卡：100ms 刷新滑杆 / 当前值
        UpdateMusicPanel();               // 音乐跟唱卡：100ms 刷新状态 / 媒体信息 / 实时数据
        _viewModel.RefreshArmValues();    // 手臂 & 姿势卡：100ms 刷新手臂参数真实值
        _viewModel.RefreshActionStatus(); // 动作卡：当前手势 / 表演 / 舞蹈
        _viewModel.RefreshSpeechStatus(); // 说话卡：语音列表 / 说话状态 / 嘴型
    }

    /// <summary>「重新检测」按钮：重新枚举系统 TTS 语音（刚装了语音包时用）。</summary>
    private void OnReloadVoicesClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.ReloadVoices();
    }

    /// <summary>
    /// 音乐卡 100ms 刷新：状态行 / 媒体信息 / 实时分析数值。
    /// 数据源是会话持有的 WASAPI 回环采集（只读 Latest 快照），这里不接触任何音频数据。
    /// </summary>
    private void UpdateMusicPanel()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        var session = viewModel.Session;
        var source = session.MusicSource;
        var latest = source?.Latest;

        var error = source?.LastError;
        if (string.IsNullOrEmpty(error)) error = session.MusicStartError;

        var media = "";
        if (source is not null)
        {
            var title = source.MediaTitle?.Trim() ?? "";
            var artist = source.MediaArtist?.Trim() ?? "";
            var app = source.MediaApp?.Trim() ?? "";
            if (title.Length > 0 || artist.Length > 0 || app.Length > 0)
            {
                media = "媒体：" + (title.Length > 0 ? title : "（未知歌曲）");
                if (artist.Length > 0) media += " - " + artist;
                if (app.Length > 0) media += " · " + app;
            }
        }
        if (media.Length == 0) media = "媒体：—";

        // 整身轻倾（以脚为支点）：截图调试钩子可临时钉住角度；否则跟随运行时 BodyLeanDegrees
        double bodyLean = 0;
        if (viewModel.LeanOverrideDegrees is { } fixedLean)
        {
            bodyLean = fixedLean;
        }
        else
        {
            var runtime = viewModel.Runtime;
            if (runtime is not null)
            {
                try { bodyLean = runtime.BodyLeanDegrees; } catch { /* 运行时还没实现该属性 */ }
            }
        }

        viewModel.UpdateMusicPanel(
            viewModel.MusicReactiveEnabled,
            source?.IsRunning == true,
            string.IsNullOrEmpty(error) ? null : error,
            media,
            latest?.Kind ?? MusicContentKind.Unknown,
            latest?.Confidence ?? 0,
            latest?.Rms ?? 0,
            latest?.VocalScore ?? 0,
            latest?.BeatStrength ?? 0,
            latest?.CenterVoiceRatio ?? 0,
            latest?.SyllabicModulation ?? 0,
            latest?.PitchValid ?? false,
            latest?.PitchHz ?? 0,
            latest?.Bpm ?? 0,
            bodyLean);
    }

    /// <summary>把 <see cref="MocapCameraSource.LatestPreview"/> 每 100ms 写进小窗位图，并叠加人脸框。</summary>
    private void UpdateMocapPreview()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        // 预览开关关掉 / 动捕关掉：清空画面（隐私：不留旧摄像头帧）
        if (!viewModel.MocapShowPreview)
        {
            MocapPreviewImage.Source = null;
            _mocapBitmap = null;
            _lastMocapPreviewTimestamp = -1;
            MocapFaceRect.Visibility = Visibility.Collapsed;
            MocapPreviewPlaceholder.Visibility = Visibility.Visible;
            MocapPreviewPlaceholder.Text = "预览已关闭（左侧「显示摄像头预览」可重新打开）";
            return;
        }

        var session = viewModel.Session;
        var source = session.MocapSource;
        var frame = source?.LatestPreview;
        if (frame is null)
        {
            MocapPreviewImage.Source = null;
            _mocapBitmap = null;
            _lastMocapPreviewTimestamp = -1;
            MocapFaceRect.Visibility = Visibility.Collapsed;
            MocapPreviewPlaceholder.Visibility = Visibility.Visible;
            MocapPreviewPlaceholder.Text = viewModel.MocapEnabled
                ? "等待摄像头画面…（首次打开会在系统弹窗里请求摄像头权限）"
                : "动捕未启用\n打开上面的「启用摄像头动捕」后这里会显示预览";
            return;
        }

        if (frame.TimestampMs == _lastMocapPreviewTimestamp)
        {
            // 同一帧：画面不用重写，只更新状态行（人脸框已在上次画好）
            return;
        }
        _lastMocapPreviewTimestamp = frame.TimestampMs;

        if (_mocapBitmap is null
            || _mocapBitmap.PixelWidth != frame.Width
            || _mocapBitmap.PixelHeight != frame.Height)
        {
            _mocapBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            MocapPreviewImage.Source = _mocapBitmap;
        }
        _mocapBitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
        MocapPreviewPlaceholder.Visibility = Visibility.Collapsed;

        // 人脸框：预览图按 Uniform 缩放到小窗里，框也跟着缩放 / 居中
        if (!frame.HasFace)
        {
            MocapFaceRect.Visibility = Visibility.Collapsed;
            return;
        }

        var hostWidth = Math.Max(1.0, MocapPreviewHost.ActualWidth);
        var hostHeight = Math.Max(1.0, MocapPreviewHost.ActualHeight);
        var scale = Math.Min(hostWidth / frame.Width, hostHeight / frame.Height);
        if (scale <= 0.0001)
        {
            MocapFaceRect.Visibility = Visibility.Collapsed;
            return;
        }
        var offsetX = (hostWidth - frame.Width * scale) / 2;
        var offsetY = (hostHeight - frame.Height * scale) / 2;

        MocapFaceRect.Width = Math.Max(2, frame.FaceWidth * frame.Width * scale);
        MocapFaceRect.Height = Math.Max(2, frame.FaceHeight * frame.Height * scale);
        Canvas.SetLeft(MocapFaceRect, offsetX + frame.FaceX * frame.Width * scale);
        Canvas.SetTop(MocapFaceRect, offsetY + frame.FaceY * frame.Height * scale);
        MocapFaceRect.Visibility = Visibility.Visible;
    }

    /// <summary>刷新动捕状态行（实测 FPS / 有脸 / 错误；不碰摄像头本身）。</summary>
    private void UpdateMocapStatusLine()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        var source = viewModel.Session.MocapSource;
        var frame = source?.LatestPreview;
        var error = source?.LastError;
        if (string.IsNullOrEmpty(error)) error = viewModel.Session.MocapStartError;
        viewModel.UpdateMocapStatus(
            source?.MeasuredFps ?? 0,
            source?.IsRunning == true,
            frame?.HasFace == true,
            string.IsNullOrEmpty(error) ? null : error);
    }

    /// <summary>采样晃动关键参数（100ms 一次）：更新参数实时值文本 + 5 秒滚动折线图。</summary>
    private void UpdateSwayDisplay()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        var runtime = viewModel.Runtime;
        if (runtime?.IsLoaded != true)
        {
            viewModel.UpdateSwayDisplay("（模型加载后这里显示参数实时值）", 0);
            return;
        }

        try
        {
            var model = runtime.Model;
            double Value(string id) => model.FindParameter(id)?.Value ?? 0;

            var bodyX = Value("ParamBodyAngleX");
            var angleX = Value("AngleX");
            var headX = Value("ParamAngleX");
            var hairX = Value("FrontHairX1");

            AddSwaySample(0, bodyX);
            AddSwaySample(1, angleX);
            AddSwaySample(2, headX);
            AddSwaySample(3, hairX);
            RedrawSwayChart();

            var text =
                $"ParamBodyAngleX {bodyX,7:0.00}   AngleX {angleX,7:0.00}   ParamAngleX {headX,7:0.00}   FrontHairX1 {hairX,8:0.000}" +
                Environment.NewLine +
                $"FrontHairZ {Value("FrontHairZ"),8:0.000}   SideHairX1 {Value("SideHairX1"),8:0.000}   " +
                $"ShockR {Value("ShockR"),7:0.00}   ParamBreath {Value("ParamBreath"),7:0.00}";

            // 晃动控制器本帧实际写入了哪些参数（用它说明「谁在动」，比只看结果参数更直观）
            try
            {
                var applied = runtime.BodySway?.LastApplied;
                if (applied is { Count: > 0 })
                {
                    var summary = string.Join("  ", applied.Take(6).Select(item => $"{item.Id}={item.Value:0.00}"));
                    if (applied.Count > 6) summary += $"  …（共 {applied.Count} 个）";
                    text += Environment.NewLine + "本帧晃动写入：" + summary;
                }
            }
            catch
            {
                // 运行时没准备好就忽略这一行
            }
            viewModel.UpdateSwayDisplay(text, viewModel.Session.SwayDemoRemainingSeconds);
        }
        catch
        {
            // 模型正在切换 / 参数表还没就绪：忽略这一拍
        }
    }

    private void AddSwaySample(int index, double value)
    {
        var series = _swaySeries[index];
        series.Add(double.IsFinite(value) ? value : 0);
        while (series.Count > SwaySampleCount) series.RemoveAt(0);
    }

    /// <summary>
    /// 把 4 条参数曲线画到 <see cref="SwayChart"/> 里（右边 = 最新）。
    /// <para>身体角度是几十度、物理输出（头发）往往只有零点几度：如果统一按 ±30° 画，
    /// 头发线会贴在 0 上看不出来。所以<b>每条曲线按自身幅度自动缩放</b>（下限 1°），
    /// 这样能清楚看到「身体晃 → 中间角度 → 头发物理输出」的相位 / 联动关系。</para>
    /// </summary>
    private void RedrawSwayChart()
    {
        var width = SwayChart.ActualWidth;
        var height = SwayChart.ActualHeight;
        if (width < 4 || height < 4) return;

        var middle = height / 2.0;
        var amplitude = height * 0.44;
        var lines = new[] { SwayLineBodyX, SwayLineAngleX, SwayLineHeadX, SwayLineFrontHair };
        var step = width / (SwaySampleCount - 1);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var series = _swaySeries[lineIndex];
            var points = new PointCollection(series.Count);
            var startX = Math.Max(0.0, width - (series.Count - 1) * step);

            // 每条曲线自己的显示范围（至少 1°，避免数值很小的物理输出被压成一条直线）
            var range = 1.0;
            foreach (var value in series)
            {
                range = Math.Max(range, Math.Abs(value));
            }
            range *= 1.15;

            for (var i = 0; i < series.Count; i++)
            {
                var normalized = Math.Clamp(series[i] / range, -1, 1);
                points.Add(new Point(startX + i * step, middle - normalized * amplitude));
            }
            lines[lineIndex].Points = points;
        }
    }

    private void OnSwayChartSizeChanged(object sender, SizeChangedEventArgs e) => RedrawSwayChart();

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

        // 全身轻晃：以脚为支点旋转「模型位图」（只转 PreviewImage，棋盘背景 / 文字 / 布局都不动）；
        // 角度很小（常态 ±0.6°），点击映射仍按未旋转坐标算，体验上没有偏差。
        double leanDegrees;
        if (viewModel.LeanOverrideDegrees is { } fixedLean)
        {
            leanDegrees = fixedLean;
        }
        else
        {
            try { leanDegrees = runtime.BodyLeanDegrees; } catch { leanDegrees = 0; }
        }
        PreviewLeanTransform.Angle = Math.Clamp(leanDegrees, -30, 30);

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
