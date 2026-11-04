using System.Buffers.Binary;

namespace YuLiangToolkit.Tools.ChatRoom.Media;

// ============================================================================
// 「按住录音」落盘用的最小 WAV 写入器（PCM16 / 单声道 / 44 字节标准头，零 NuGet）。
//
// 为什么手写：只需要「边录边写 + 结束时回填两个长度字段」这一件事，用 NAudio 之类
// 会为了 40 行代码拖进一整个音频框架，违背项目「能手写的小轮子就手写」的约定。
//
// 线程模型：
//   · Write / SampleCount / Duration 会被 **WASAPI 采集线程** 调用；
//   · Finish / Dispose 在 StopAsync / Cancel 的调用线程上执行；
//   所以全部入口都走同一把 lock（_gate），保证「写样本」和「回填头 + 关文件」不会交叉。
//
// 已知边界：
//   · RIFF 的 chunkSize / dataSize 是 32 位无符号数：单个 WAV 最多约 4 GB，
//     按 48 kHz 单声道 16 位算约 12 小时，语音消息（上限 120 秒）远远够；
//     真超了会在 Write 里抛中文 InvalidOperationException（VoiceRecorder 会记进 LastError）。
//   · 只写 PCM16 单声道，不做重采样 / 声道混合（那是采集层与通话引擎的事）。
//   · 构造后立刻落一个 0 长度的头：即使录音中途进程被杀，WAV 也至少是个合法空文件
//     （时长信息会缺，但不会像「先攒内存再写」那样连文件都没有）。
// ============================================================================

/// <summary>
/// PCM16 单声道 WAV 文件写入器：构造时先写 44 字节占位头，之后每来一段 float 样本就
/// 转成 short 追加；<see cref="Finish"/> 时回填 RIFF / data 长度并关闭文件（幂等）。
///
/// <para><b>为什么这么做</b>：语音消息要求「按住就录、松手立刻能发」，没有时间在松手后
/// 才把整段录音编码一遍；边录边写 WAV 只是磁盘顺序写，几乎零 CPU，松手后只剩一次 MP3 转码。</para>
///
/// <para><b>线程模型</b>：<see cref="Write"/> 可能在 WASAPI 采集线程上被高频调用，
/// <see cref="Finish"/> / <see cref="Dispose"/> 在调用方线程执行；内部用一把 lock 串行化，
/// 因此「采集线程正在写」时调用 Finish 是安全的（会等当前这段写完）。</para>
///
/// <para><b>已知边界</b>：RIFF 的 data 长度是 uint，单文件约 4 GB 上限（远超语音消息 120 秒）；
/// 只支持 PCM16 单声道；<see cref="Duration"/> 按「已写入样本数 / 采样率」算，
/// 与真实设备时钟可能有毫秒级偏差（对语音消息的秒数显示无影响）。</para>
/// </summary>
public sealed class WavWriter : IDisposable
{
    /// <summary>标准 PCM WAV 头长度（不含任何扩展块）。</summary>
    private const int HeaderSize = 44;

    /// <summary>一段样本最多 4 GB / 2 字节；留出 36 字节的 RIFF 头余量。</summary>
    private const long MaxDataBytes = uint.MaxValue - 36L;

    /// <summary>保护 _stream / _sampleCount / _finished 的唯一锁（采集线程与停止线程共用）。</summary>
    private readonly object _gate = new();

    /// <summary>输出文件；Finish / Dispose 之后置 null（用来判断是否已收尾）。</summary>
    private FileStream? _stream;

    /// <summary>float → short 的临时缓冲（复用，避免每次回调都分配几 KB）。</summary>
    private byte[] _bytes = Array.Empty<byte>();

    /// <summary>已写入的样本数（不是字节数）。</summary>
    private long _sampleCount;

    /// <summary>是否已经回填过头并关闭文件（幂等标记）。</summary>
    private bool _finished;

    /// <summary>
    /// 创建一个 WAV 文件并写入占位头。目录不存在会自动创建。
    /// </summary>
    /// <param name="path">输出路径（通常是临时目录里的 voice-{guid}.wav）。</param>
    /// <param name="sampleRate">设备混音采样率（一般 48000 / 44100），必须 &gt; 0。</param>
    /// <exception cref="ArgumentException">路径为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">采样率 &lt;= 0。</exception>
    /// <exception cref="IOException">文件创建失败（中文原因；VoiceRecorder 会兜住并清理）。</exception>
    public WavWriter(string path, int sampleRate)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("WAV 输出路径不能为空", nameof(path));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "采样率必须大于 0");

        SampleRate = sampleRate;

        try
        {
            // 临时目录可能还没建（VoiceRecorder 会建，但这里再兜一次，方便单独使用 / 自检）。
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // FileShare.Read：转码前调用方会先 Finish/关闭；这里给读共享只是防御（例如杀毒软件扫描）。
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, FileOptions.None);

            Span<byte> header = stackalloc byte[HeaderSize];
            BuildHeader(header, sampleRate, dataBytes: 0);
            _stream.Write(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // 统一成 IOException + 中文消息：VoiceRecorder 只按「失败原因」展示，不区分异常类型。
            throw new IOException("创建 WAV 录音文件失败：" + ex.Message, ex);
        }
    }

    /// <summary>采样率（构造时传入，之后不变）。</summary>
    public int SampleRate { get; }

    /// <summary>已写入的样本数（单声道，一个 float 算一个样本）。</summary>
    public long SampleCount
    {
        get
        {
            lock (_gate) return _sampleCount;
        }
    }

    /// <summary>已写入的音频时长（= 样本数 / 采样率；空文件是 <see cref="TimeSpan.Zero"/>）。</summary>
    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return _sampleCount <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)_sampleCount / SampleRate);
            }
        }
    }

    /// <summary>
    /// 追加一段单声道样本（-1~1 的 float：越界会夹取，NaN / Infinity 按 0 处理，再四舍五入成 PCM16）。
    /// </summary>
    /// <param name="samples">设备混音率下的单声道样本；空 span 直接返回。</param>
    /// <exception cref="ObjectDisposedException">已经 Finish / Dispose 之后又写。</exception>
    /// <exception cref="InvalidOperationException">录音超过 WAV 的 4 GB 上限（实际到不了）。</exception>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;

        lock (_gate)
        {
            var stream = _stream;
            if (stream is null || _finished)
                throw new ObjectDisposedException(nameof(WavWriter), "WAV 文件已经收尾，不能再写入");

            // RIFF 的数据长度是 32 位无符号数：超了就别写坏文件，抛中文原因让上层停录。
            var nextSamples = _sampleCount + samples.Length;
            if (nextSamples > MaxDataBytes / 2)
                throw new InvalidOperationException("录音文件超过 WAV 格式上限（约 4 GB），请缩短录音时长");

            EnsureByteBuffer(samples.Length * 2);
            var buffer = _bytes;
            for (var i = 0; i < samples.Length; i++)
            {
                var value = samples[i];
                if (!float.IsFinite(value)) value = 0f;      // NaN / ±Infinity 一律当静音
                value = Math.Clamp(value, -1f, 1f);          // 先夹取再缩放，不会把 -1 变成 -32768 之外的怪值
                var scaled = (int)Math.Round(value * 32767.0, MidpointRounding.AwayFromZero);
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), (short)scaled);
            }

            stream.Write(buffer.AsSpan(0, samples.Length * 2));
            _sampleCount = nextSamples;
        }
    }

    /// <summary>
    /// 回填 RIFF / data 长度并关闭文件（幂等：重复调用只有第一次生效）。
    /// <para>没调用就 Dispose 时也会走到这里；即使没有任何样本，也会产出一个 44 字节的合法空 WAV。</para>
    /// </summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;

            var stream = _stream;
            _stream = null;
            if (stream is null) return;

            try
            {
                stream.Flush();
                var dataBytes = (uint)(_sampleCount * 2);   // Write 里已保证不超 uint 范围
                Span<byte> header = stackalloc byte[HeaderSize];
                BuildHeader(header, SampleRate, dataBytes);
                stream.Seek(0, SeekOrigin.Begin);
                stream.Write(header);
                stream.Flush();
            }
            finally
            {
                // 长度回填失败（磁盘满 / 文件被删）也不要把句柄泄漏出去；异常继续上抛给调用方。
                try { stream.Dispose(); } catch { /* 关闭失败不影响已经写进去的内容 */ }
            }
        }
    }

    /// <summary>释放并尽量回填头（= <see cref="Finish"/>；重复调用安全）。</summary>
    public void Dispose() => Finish();

    // ===================== 内部 =====================

    /// <summary>把 44 字节标准 PCM 头写进 <paramref name="header"/>。</summary>
    /// <param name="header">至少 44 字节。</param>
    /// <param name="sampleRate">采样率。</param>
    /// <param name="dataBytes">已经写了多少字节的样本数据（0 = 占位头）。</param>
    private static void BuildHeader(Span<byte> header, int sampleRate, uint dataBytes)
    {
        // RIFF 头：'RIFF' + (36 + dataSize) + 'WAVE'
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 36u + dataBytes);
        "WAVE"u8.CopyTo(header[8..]);

        // fmt 块：16 字节，PCM（1），单声道，采样率，字节率，块对齐，位深
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);                       // AudioFormat = PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 1);                       // 单声道
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * 2));  // 字节率 = 采样率 × 块对齐
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 2);                       // 块对齐 = 声道数 × 位深/8
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);                      // 位深

        // data 块头
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataBytes);
    }

    /// <summary>保证 float→short 的临时缓冲至少能放 <paramref name="bytes"/> 个字节。</summary>
    private void EnsureByteBuffer(int bytes)
    {
        if (_bytes.Length >= bytes) return;
        _bytes = new byte[Math.Max(bytes, Math.Max(2048, _bytes.Length * 2))];
    }
}
