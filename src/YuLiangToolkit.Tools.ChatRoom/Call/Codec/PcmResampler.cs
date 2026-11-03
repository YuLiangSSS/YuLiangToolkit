namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// 单声道 float PCM 的线性插值重采样器（通话里两个方向都要用）：
/// 麦克风 / 扬声器的设备混音率（48 kHz、44.1 kHz 等）↔ 通话固定 16 kHz。
///
/// <para><b>为什么不用「丢样本 / 重复样本」</b>：48k→16k 恰好是 3:1 还能直接抽，但 44.1k→16k
/// 是 2.75625:1，整数抽取会周期性错位，声音发闷、有金属感。线性插值实现只有十几行，
/// 音质对语音通话够用，而且**没有额外延迟**（不需要攒一大批样本做 FFT / 多相滤波）。</para>
///
/// <para><b>跨块连续</b>：设备每 10 ms 左右回调一块数据，块边界必须接得上。内部保存小数相位
/// <c>_position</c>（下一块第一帧在块内的位置），并把「这一块最后一个样本」用于块尾插值，
/// 所以连续调用 <see cref="Process"/> 的输出与「把整段数据一次喂进来」几乎等价，不会在
/// 块边界产生咔哒声。块尾那一个样本没有「下一个样本」可用，按保持（hold）处理 ——
/// 最多差一个采样间隔，16 kHz 下 62.5 µs，听不出来。</para>
///
/// <para><b>输出数量</b>：正常给足输出缓冲时，每块输出 <c>floor(n × outputRate / inputRate)</c>
/// 或 <c>ceil</c> 个（取决于相位），多块累计的数量是对的；48k→16k 的 480 样本块恰好输出 160，
/// 16k→48k 的 640 样本块恰好输出 1920，自检可以直接断言。</para>
///
/// <para><b>线程模型</b>：有状态且**不是线程安全的**。一个 <see cref="PcmResampler"/> 只能被一个
/// 线程 / 一条设备回调链使用（麦克风采集线程一个、混音器里每个成员一个）。跨线程共享的
/// 场景由 <see cref="AudioMixer"/> 自己加锁。</para>
///
/// <para><b>边界</b>：
/// ① 输入为空直接返回 0（不改变内部状态）；
/// ② 输出缓冲比「需要写的样本数」小时，没写完的输入会留到下一次调用，不丢样本；
/// ③ 采样率必须为正数，否则构造时抛 <see cref="ArgumentOutOfRangeException"/>。</para>
/// </summary>
public sealed class PcmResampler
{
    private readonly int _inputRate;
    private readonly int _outputRate;

    /// <summary>输出一个样本需要前进的输入样本数（48k→16k = 3，16k→48k ≈ 0.3333）。</summary>
    private readonly double _step;

    /// <summary>
    /// 判定「已经到块尾」的容差。44.1k→16k 这种非整数比在多次加法后，相位可能比块尾
    /// 小 1e-8 量级；不留容差会多写 1 个样本（严格断言 44100→16000 的数量会挂）。
    /// </summary>
    private const double PositionEpsilon = 1e-6;

    /// <summary>下一个输出样本在当前输入块内的位置（[0, step)，跨块保留小数）。</summary>
    private double _position;

    /// <summary>上一次因输出缓冲不够而没消费完的输入尾部（正常调用时为空）。</summary>
    private float[] _pending = Array.Empty<float>();

    private int _pendingCount;

    /// <summary>
    /// 建一个单声道重采样器。
    /// </summary>
    /// <param name="inputRate">源采样率（如设备混音率 48000）。</param>
    /// <param name="outputRate">目标采样率（如通话的 16000）。</param>
    public PcmResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate), "输入采样率必须为正数");
        if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate), "输出采样率必须为正数");
        _inputRate = inputRate;
        _outputRate = outputRate;
        _step = (double)inputRate / outputRate;
    }

    /// <summary>源采样率。</summary>
    public int InputRate => _inputRate;

    /// <summary>目标采样率。</summary>
    public int OutputRate => _outputRate;

    /// <summary>
    /// 重采样一块数据：读 <paramref name="input"/>，写 <paramref name="output"/>，
    /// 返回实际写入的输出样本数。小数相位跨调用保留，所以块边界连续。
    /// </summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.IsEmpty) return 0;

        // 上一次没写完的尾巴接在本次输入前面，保证相位与样本都不丢
        ReadOnlySpan<float> data;
        if (_pendingCount > 0)
        {
            var combined = new float[_pendingCount + input.Length];
            _pending.AsSpan(0, _pendingCount).CopyTo(combined);
            input.CopyTo(combined.AsSpan(_pendingCount));
            data = combined;
            _pendingCount = 0;
        }
        else
        {
            data = input;
        }

        var written = 0;
        var position = _position;
        // 浮点累计误差可能让相位差一丁点变成负数：夹回 0，避免下面地板除得到 -1
        if (position < 0 && position > -1e-6) position = 0;

        // 位置落在 [0, data.Length) 就还能从这一块拿到样本；块尾最后一个样本没有「下一个」
        // 可插值，用保持（b = a）处理。
        while (written < output.Length && position < data.Length - PositionEpsilon)
        {
            var index = (int)Math.Floor(position);
            if (index < 0) index = 0;
            var fraction = position - index;
            if (fraction < 0) fraction = 0;
            var a = data[index];
            var b = index + 1 < data.Length ? data[index + 1] : a;
            output[written++] = (float)(a + (b - a) * fraction);
            position += _step;
        }

        if (written == output.Length && position < data.Length - PositionEpsilon)
        {
            // 输出空间用完、输入还有效：把还没消费的尾部留到下一次调用（正常容量给够时不会发生）
            var keepFrom = (int)Math.Floor(position);
            if (keepFrom < 0) keepFrom = 0;
            if (keepFrom > data.Length) keepFrom = data.Length;
            _pending = data[keepFrom..].ToArray();
            _pendingCount = _pending.Length;
            _position = position - keepFrom;
            if (_position < 0) _position = 0;
            return written;
        }

        _position = position - data.Length;
        if (_position < 0) _position = 0;
        return written;
    }

    /// <summary>
    /// 清空内部相位与缓存（切换设备 / 重新开始采集时调用；不调用也只是多几个样本的旧尾巴）。
    /// </summary>
    public void Reset()
    {
        _position = 0;
        _pendingCount = 0;
    }
}
