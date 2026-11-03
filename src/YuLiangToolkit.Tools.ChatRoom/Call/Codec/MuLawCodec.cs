namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// G.711 μ-law 编解码：通话里唯一的音频格式（16 kHz 单声道，1 字节 / 采样）。
///
/// <para><b>为什么用 μ-law 而不是 PCM16</b>：同样的 16 kHz 采样，μ-law 只要一半带宽
/// （40 ms 一包 640 字节），8 位对数量化在语音上听感接近 16 位线性 —— 安静段的小信号
/// 自动获得更细的量化台阶，正好符合人耳特性。这是电话网络用了几十年的标准，
/// 编码就「夹取 → 加偏置 → 找指数 → 取尾数 → 取反」几步，零依赖、零状态。</para>
///
/// <para><b>算法</b>（标准 G.711 μ-law，注意不是 A-law）：</para>
/// <list type="number">
///   <item>输入 float 先夹到 [-1, 1]，乘 32767 变成 16 位整数，超过 32635 的按 32635 限幅
///         （留出偏置 132 的余量，防止加偏置后溢出）；</item>
///   <item>取符号位，幅度加偏置 132（0x84），从 2^14 往下找最高有效位，得到 8 段之一（指数 0~7）；</item>
///   <item>尾数取 4 位，最后按 μ-law 约定**整体按位取反**输出（所以静音 0 编码成 0xFF）；</item>
///   <item>解码是逆过程：先取反，把「尾数 + 偏置」按段左移，再减掉偏置、按符号还原。</item>
/// </list>
///
/// <para><b>精度</b>：编码再解码（往返）对满幅正弦波的 RMS 误差约 1~2%（量化噪声），
/// 这是格式本身的上限，不要拿它当无损编码用；静音 / 直流 ≈ 0 往返后仍然是 0。</para>
///
/// <para><b>线程模型</b>：纯静态无状态，任何线程可以并发调用；两个批量重载只写调用方给的
/// <c>Span</c>，不分配内存，麦克风采集线程 / 网络读线程都可以直接调，不需要锁。</para>
///
/// <para><b>边界</b>：NaN 当静音处理，±Infinity 按 ±1 夹取；批量重载写的样本数是
/// 输入与输出长度的较小值，不会越界也不会补齐。</para>
/// </summary>
public static class MuLawCodec
{
    /// <summary>通话音频采样率（16 kHz 单声道），与 <see cref="CallProtocolRules.AudioSampleRate"/> 一致。</summary>
    public const int SampleRate = CallProtocolRules.AudioSampleRate;

    /// <summary>μ-law 折线的量化偏置（标准值 132 / 0x84）。</summary>
    private const int Bias = 0x84;

    /// <summary>16 位线性样本参与编码前的限幅上限（标准值 32635，给偏置留余量）。</summary>
    private const int Clip = 32635;

    /// <summary>float（-1~1）转 16 位整数时的满量程。</summary>
    private const float FullScale = 32767f;

    /// <summary>
    /// 把 -1~1 的浮点样本编码成一个 μ-law 字节。
    /// </summary>
    public static byte Encode(float sample)
    {
        // 防御 NaN / Infinity：NaN 当静音；超范围交给夹取
        if (float.IsNaN(sample)) sample = 0f;
        var value = sample > 1f ? 1f : sample < -1f ? -1f : sample;

        var sign = value < 0f ? 0x80 : 0x00;
        var magnitude = (int)(Math.Abs(value) * FullScale);
        if (magnitude > Clip) magnitude = Clip;
        magnitude += Bias;

        // 从 2^14 往下找最高有效位，确定 8 段中的哪一段（找到就停，最小到指数 0）
        var exponent = 7;
        for (var mask = 0x4000; (magnitude & mask) == 0 && exponent > 0; exponent--, mask >>= 1)
        {
        }

        var mantissa = (magnitude >> (exponent + 3)) & 0x0F;
        // 标准 μ-law 输出整体取反；取反后高位是 1，强转 byte 只留低 8 位
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    /// <summary>
    /// 把一个 μ-law 字节解码回 -1~1 的浮点样本。
    /// </summary>
    public static float Decode(byte value)
    {
        var u = ~value & 0xFF;
        var magnitude = ((u & 0x0F) << 3) + Bias;   // 尾数还原成线性值
        magnitude <<= (u & 0x70) >> 4;              // 再乘上所在段的步长
        var linear = (u & 0x80) != 0 ? Bias - magnitude : magnitude - Bias;
        // 32768 = 2^15，保证结果落在 [-1, 1)
        return linear / 32768f;
    }

    /// <summary>
    /// 批量编码（原地写 <paramref name="destination"/>，写 min(两长度) 个）。
    /// </summary>
    public static void Encode(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        var count = Math.Min(samples.Length, destination.Length);
        for (var i = 0; i < count; i++) destination[i] = Encode(samples[i]);
    }

    /// <summary>
    /// 批量解码（原地写 <paramref name="destination"/>，写 min(两长度) 个）。
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> encoded, Span<float> destination)
    {
        var count = Math.Min(encoded.Length, destination.Length);
        for (var i = 0; i < count; i++) destination[i] = Decode(encoded[i]);
    }
}
