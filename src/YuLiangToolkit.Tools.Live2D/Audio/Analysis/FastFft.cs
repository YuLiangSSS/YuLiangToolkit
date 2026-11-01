namespace YuLiangToolkit.Tools.Live2D.Audio.Analysis;

/// <summary>
/// 手写的 radix-2 实数 / 复数 FFT（不引第三方包）。
///
/// <para><b>用途</b>：<see cref="MusicAnalyzer"/> 每帧把 1024 点 Hann 窗音频变换到频域算谱特征；
/// 自检工程用 <see cref="NaiveDft"/> 对拍验证正确性。</para>
///
/// <para><b>实现</b>：经典迭代 Cooley–Tukey（位反转 + 蝶形），双精度；长度必须是 2 的幂。
/// 所有方法都不分配内存（缓冲区由调用方传入并复用）。</para>
/// </summary>
public static class FastFft
{
    /// <summary>长度是不是 2 的幂（0 不算）。</summary>
    public static bool IsPowerOfTwo(int length) => length > 0 && (length & (length - 1)) == 0;

    /// <summary>
    /// 就地复数 FFT（正变换，<c>X[k] = Σ x[n]·e^(-2πikn/N)</c>）。
    /// <paramref name="real"/> / <paramref name="imag"/> 长度相同且为 2 的幂；imag 传全 0 即实数输入。
    /// </summary>
    public static void Transform(Span<double> real, Span<double> imag)
    {
        var n = real.Length;
        if (imag.Length != n) throw new ArgumentException("real / imag 长度必须一致", nameof(imag));
        if (n <= 1) return;
        if (!IsPowerOfTwo(n)) throw new ArgumentException("FFT 长度必须是 2 的幂", nameof(real));

        // 位反转置换（迭代写法，不分配置换表）
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i >= j) continue;
            (real[i], real[j]) = (real[j], real[i]);
            (imag[i], imag[j]) = (imag[j], imag[i]);
        }

        // 蝶形运算（每一级用旋转因子的递推，避免每只蝶形都算 sin/cos）
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2.0 * Math.PI / length;
            var baseReal = Math.Cos(angle);
            var baseImag = Math.Sin(angle);
            var half = length >> 1;

            for (var start = 0; start < n; start += length)
            {
                double wReal = 1.0;
                double wImag = 0.0;
                for (var k = 0; k < half; k++)
                {
                    var evenIndex = start + k;
                    var oddIndex = evenIndex + half;

                    var oddReal = real[oddIndex];
                    var oddImag = imag[oddIndex];

                    var vReal = (oddReal * wReal) - (oddImag * wImag);
                    var vImag = (oddReal * wImag) + (oddImag * wReal);

                    var uReal = real[evenIndex];
                    var uImag = imag[evenIndex];

                    real[evenIndex] = uReal + vReal;
                    imag[evenIndex] = uImag + vImag;
                    real[oddIndex] = uReal - vReal;
                    imag[oddIndex] = uImag - vImag;

                    // 旋转因子递推：(cos + i·sin) * (baseReal + i·baseImag)
                    var nextReal = (wReal * baseReal) - (wImag * baseImag);
                    wImag = (wReal * baseImag) + (wImag * baseReal);
                    wReal = nextReal;
                }
            }
        }
    }

    /// <summary>
    /// 就地复数逆 FFT（<c>x[n] = (1/N)·Σ X[k]·e^(+2πikn/N)</c>，用共轭正变换实现）。
    /// 用途：把带限功率谱变换回自相关（MusicAnalyzer 的「人声周期显著度」）。
    /// </summary>
    public static void InverseTransform(Span<double> real, Span<double> imag)
    {
        var n = real.Length;
        if (imag.Length != n) throw new ArgumentException("real / imag 长度必须一致", nameof(imag));
        if (n <= 1) return;

        for (var i = 0; i < n; i++) imag[i] = -imag[i];
        Transform(real, imag);
        var scale = 1.0 / n;
        for (var i = 0; i < n; i++)
        {
            real[i] *= scale;
            imag[i] *= -scale;
        }
    }

    /// <summary>由 FFT 的实部 / 虚部算幅度谱（取前 N/2+1 个频点）。</summary>
    public static void MagnitudeSpectrum(ReadOnlySpan<double> real, ReadOnlySpan<double> imag, Span<double> magnitude)
    {
        var n = real.Length;
        var bins = (n / 2) + 1;
        if (magnitude.Length < bins) throw new ArgumentException("magnitude 长度不足（需要 N/2+1）", nameof(magnitude));
        for (var i = 0; i < bins; i++)
        {
            var re = real[i];
            var im = imag[i];
            magnitude[i] = Math.Sqrt((re * re) + (im * im));
        }
    }

    /// <summary>朴素 DFT（O(N²)，只给自检对拍用）。</summary>
    public static void NaiveDft(ReadOnlySpan<double> input, Span<double> outReal, Span<double> outImag)
    {
        var n = input.Length;
        if (outReal.Length < n || outImag.Length < n) throw new ArgumentException("输出缓冲区长度不足");

        for (var k = 0; k < n; k++)
        {
            double sumReal = 0.0;
            double sumImag = 0.0;
            for (var t = 0; t < n; t++)
            {
                var angle = -2.0 * Math.PI * k * t / n;
                sumReal += input[t] * Math.Cos(angle);
                sumImag += input[t] * Math.Sin(angle);
            }

            outReal[k] = sumReal;
            outImag[k] = sumImag;
        }
    }

    /// <summary>填充周期 Hann 窗：<c>w[i] = 0.5·(1 - cos(2πi/N))</c>（标准 FFT 分析窗）。</summary>
    public static void FillHannWindow(Span<double> window)
    {
        var n = window.Length;
        if (n == 0) return;
        for (var i = 0; i < n; i++) window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / n));
    }

    /// <summary>加窗并拷贝到目标缓冲（长度取小值）。</summary>
    public static void ApplyWindow(ReadOnlySpan<double> samples, ReadOnlySpan<double> window, Span<double> destination)
    {
        var count = Math.Min(Math.Min(samples.Length, window.Length), destination.Length);
        for (var i = 0; i < count; i++) destination[i] = samples[i] * window[i];
        for (var i = count; i < destination.Length; i++) destination[i] = 0.0;
    }
}
