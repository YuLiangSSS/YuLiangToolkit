namespace YuLiangToolkit.Tools.Live2D.Mocap;

/// <summary>
/// 人脸表情启发式分析（眨眼 / 张嘴）——不依赖任何第三方库，只用 Windows 自带的人脸框 + 简单像素统计。
///
/// <para><b>为什么只能靠启发式</b>：Windows 自带的 <c>Windows.Media.FaceAnalysis</c> 只给人脸框，
/// 不给眼睛 / 嘴角关键点；仓库规则又不许引第三方包（OpenCV / MediaPipe / ONNX）。所以：
/// <list type="bullet">
///   <item><b>眨眼</b>：取两只眼睛所在的横带，算灰度标准差 —— 睁眼时有亮眼白 + 暗瞳孔，方差大；
///         闭眼时基本是均匀眼皮，方差明显变小。用滚动分位数把「睁 / 闭」两端自动校准。</item>
///   <item><b>张嘴</b>：取嘴部横带的暗像素比例（相对两侧脸颊的肤色亮度）—— 张嘴露出暗口腔，比例上升，
///         同样按滚动分位数自动归一化。</item>
/// </list>
/// 侧脸 / 手挡脸 / 光线很差时这些启发式会不准，界面上提供了灵敏度 / 幅度滑杆和「重新校准」。</para>
/// </summary>
public sealed class FaceExpressionAnalyzer
{
    private const int CalibrationWindow = 180;      // 滚动窗口（15fps ≈ 12 秒）
    private const int MinCalibrationSamples = 24;   // 少于这个数时按「睁眼 / 闭嘴」安全值输出
    private const double EyeMinMetricSpan = 2.0;    // 灰度标准差两端差太小（用户一直不动）时不强行归一化
    private const double MouthMinMetricSpan = 0.08; // 嘴部暗度两端差太小

    private byte[] _luma = Array.Empty<byte>();
    private readonly RollingPercentile _eyeLeft = new(CalibrationWindow);
    private readonly RollingPercentile _eyeRight = new(CalibrationWindow);
    private readonly RollingPercentile _mouth = new(CalibrationWindow);

    /// <summary>人脸框（分析帧像素坐标）。</summary>
    public readonly record struct FaceBox(int X, int Y, int Width, int Height);

    /// <summary>眨眼 / 张嘴开合度（0~1）。</summary>
    public readonly record struct FaceExpressionMetrics(double EyeOpenLeft, double EyeOpenRight, double MouthOpen);

    /// <summary>清空自动校准（用户换了环境 / 光线时点「重新校准」）。</summary>
    public void ResetCalibration()
    {
        _eyeLeft.Clear();
        _eyeRight.Clear();
        _mouth.Clear();
    }

    /// <summary>分析一帧（BGRA32，宽高为分析帧尺寸；人脸框必须已经换算到分析帧坐标）。</summary>
    public FaceExpressionMetrics Analyze(ReadOnlySpan<byte> bgra, int width, int height, in FaceBox box)
    {
        if (box.Width < 24 || box.Height < 24 || width < 8 || height < 8)
            return new FaceExpressionMetrics(1, 1, 0);

        var luma = EnsureLuma(width, height);
        BgraToLuma(bgra, luma, width * height);

        // 眼睛 / 嘴的横带都按人脸框比例取；比例来自常见正面人脸结构，侧脸会变差
        var eyeBandTop = box.Y + (int)(box.Height * 0.30);
        var eyeBandBottom = box.Y + (int)(box.Height * 0.56);
        var leftEyeLeft = box.X + (int)(box.Width * 0.10);
        var leftEyeRight = box.X + (int)(box.Width * 0.44);
        var rightEyeLeft = box.X + (int)(box.Width * 0.56);
        var rightEyeRight = box.X + (int)(box.Width * 0.90);

        var eyeLeftMetric = ComputeStandardDeviation(luma, width, height, leftEyeLeft, eyeBandTop, leftEyeRight, eyeBandBottom);
        var eyeRightMetric = ComputeStandardDeviation(luma, width, height, rightEyeLeft, eyeBandTop, rightEyeRight, eyeBandBottom);

        var mouthTop = box.Y + (int)(box.Height * 0.60);
        var mouthBottom = box.Y + (int)(box.Height * 0.88);
        var mouthLeft = box.X + (int)(box.Width * 0.30);
        var mouthRight = box.X + (int)(box.Width * 0.70);
        var cheekLeft = (X: box.X + (int)(box.Width * 0.04), X2: box.X + (int)(box.Width * 0.18));
        var cheekRight = (X: box.X + (int)(box.Width * 0.82), X2: box.X + (int)(box.Width * 0.96));
        var cheekTop = box.Y + (int)(box.Height * 0.55);
        var cheekBottom = box.Y + (int)(box.Height * 0.80);

        var cheekMean = (ComputeMean(luma, width, height, cheekLeft.X, cheekTop, cheekLeft.X2, cheekBottom)
                         + ComputeMean(luma, width, height, cheekRight.X, cheekTop, cheekRight.X2, cheekBottom)) * 0.5;
        var mouthMetric = ComputeMouthDarkness(luma, width, height, mouthLeft, mouthTop, mouthRight, mouthBottom, cheekMean);

        _eyeLeft.Push(eyeLeftMetric);
        _eyeRight.Push(eyeRightMetric);
        _mouth.Push(mouthMetric);

        return new FaceExpressionMetrics(
            NormalizeOpen(_eyeLeft, eyeLeftMetric, fallback: 1.0, minSpan: EyeMinMetricSpan),
            NormalizeOpen(_eyeRight, eyeRightMetric, fallback: 1.0, minSpan: EyeMinMetricSpan),
            NormalizeOpen(_mouth, mouthMetric, fallback: 0.0, minSpan: MouthMinMetricSpan));
    }

    private byte[] EnsureLuma(int width, int height)
    {
        var needed = width * height;
        if (_luma.Length < needed) _luma = new byte[needed];
        return _luma;
    }

    private static void BgraToLuma(ReadOnlySpan<byte> bgra, byte[] luma, int pixelCount)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var p = i * 4;
            // 0.114B + 0.587G + 0.299R（定点）
            luma[i] = (byte)((bgra[p] * 29 + bgra[p + 1] * 150 + bgra[p + 2] * 77) >> 8);
        }
    }

    /// <summary>区域灰度标准差（睁眼时亮眼白 + 暗瞳孔 → 方差大；闭眼 → 方差小）。</summary>
    private static double ComputeStandardDeviation(byte[] luma, int width, int height, int x0, int y0, int x1, int y1)
    {
        ClampRect(width, height, ref x0, ref y0, ref x1, ref y1);
        if (x1 - x0 < 4 || y1 - y0 < 4) return 0;

        long sum = 0;
        long sumSq = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * width;
            for (var x = x0; x < x1; x++)
            {
                var value = luma[row + x];
                sum += value;
                sumSq += value * value;
                count++;
            }
        }

        if (count == 0) return 0;
        var mean = sum / (double)count;
        var variance = sumSq / (double)count - mean * mean;
        return variance <= 0 ? 0 : Math.Sqrt(variance);
    }

    /// <summary>区域平均灰度。</summary>
    private static double ComputeMean(byte[] luma, int width, int height, int x0, int y0, int x1, int y1)
    {
        ClampRect(width, height, ref x0, ref y0, ref x1, ref y1);
        if (x1 - x0 <= 0 || y1 - y0 <= 0) return 0;

        long sum = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * width;
            for (var x = x0; x < x1; x++)
            {
                sum += luma[row + x];
                count++;
            }
        }

        return count == 0 ? 0 : sum / (double)count;
    }

    /// <summary>嘴部相对脸颊的暗度（0~1：张嘴露口腔 / 嘴唇阴影时上升）。</summary>
    private static double ComputeMouthDarkness(byte[] luma, int width, int height,
        int x0, int y0, int x1, int y1, double cheekMean)
    {
        ClampRect(width, height, ref x0, ref y0, ref x1, ref y1);
        if (x1 - x0 < 4 || y1 - y0 < 4) return 0;

        var threshold = Math.Max(18.0, cheekMean * 0.72);
        var dark = 0;
        var count = 0;
        long sum = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * width;
            for (var x = x0; x < x1; x++)
            {
                var value = luma[row + x];
                sum += value;
                if (value < threshold) dark++;
                count++;
            }
        }

        if (count == 0) return 0;
        var darkRatio = dark / (double)count;
        var mean = sum / (double)count;
        var contrast = cheekMean <= 1 ? 0 : Math.Clamp((cheekMean - mean) / (cheekMean * 0.6), 0, 1);
        return darkRatio * 0.65 + contrast * 0.35;
    }

    private static void ClampRect(int width, int height, ref int x0, ref int y0, ref int x1, ref int y1)
    {
        x0 = Math.Clamp(x0, 0, width - 1);
        x1 = Math.Clamp(x1, 0, width);
        y0 = Math.Clamp(y0, 0, height - 1);
        y1 = Math.Clamp(y1, 0, height);
        if (x1 <= x0) x1 = Math.Min(width, x0 + 1);
        if (y1 <= y0) y1 = Math.Min(height, y0 + 1);
    }

    /// <summary>把指标按滚动分位数归一化成 0~1（校准样本不足 / 用户一直不动时用安全默认值）。</summary>
    private static double NormalizeOpen(RollingPercentile calibrator, double value, double fallback, double minSpan)
    {
        if (calibrator.Count < MinCalibrationSamples) return fallback;

        var low = calibrator.LowPercentile;
        var high = calibrator.HighPercentile;
        var span = high - low;
        if (span < minSpan) return fallback;   // 用户一直不动：直接用安全值（眼睛=睁、嘴=闭）
        var normalized = Math.Clamp((value - low) / span, 0, 1);
        // 两端之外做一点饱和平滑，避免 0/1 边缘抖动
        return normalized;
    }

    /// <summary>滚动窗口 + 分位数（10% / 90%）。每帧 Push，每 8 帧算一次分位数，开销可忽略。</summary>
    private sealed class RollingPercentile
    {
        private readonly double[] _values;
        private int _index;
        private int _count;
        private int _sinceSort;

        public RollingPercentile(int capacity) => _values = new double[capacity];

        public int Count => _count;

        public double LowPercentile { get; private set; }

        public double HighPercentile { get; private set; } = 1;

        public void Clear()
        {
            _index = 0;
            _count = 0;
            _sinceSort = 0;
            LowPercentile = 0;
            HighPercentile = 1;
        }

        public void Push(double value)
        {
            _values[_index] = value;
            _index = (_index + 1) % _values.Length;
            if (_count < _values.Length) _count++;

            if (++_sinceSort < 8 && _count > MinCalibrationSamples) return;
            _sinceSort = 0;
            Recompute();
        }

        private void Recompute()
        {
            if (_count == 0) return;
            var ordered = _values.AsSpan(0, _count).ToArray();
            Array.Sort(ordered);
            LowPercentile = ordered[(int)Math.Clamp(Math.Floor(ordered.Length * 0.10), 0, ordered.Length - 1)];
            HighPercentile = ordered[(int)Math.Clamp(Math.Floor(ordered.Length * 0.90), 0, ordered.Length - 1)];
        }
    }
}
