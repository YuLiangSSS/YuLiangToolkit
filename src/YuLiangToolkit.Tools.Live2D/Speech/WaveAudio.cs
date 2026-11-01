namespace YuLiangToolkit.Tools.Live2D.Speech;

/// <summary>解析出来的 WAV 头信息（TTS 输出通常是 16k / 单声道 / 16bit，但按标准 RIFF 块解析能兼容其他格式）。</summary>
public readonly record struct WaveAudioInfo(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int DataOffset,
    int DataLength)
{
    /// <summary>每帧（所有声道）字节数。</summary>
    public int BlockAlign => Math.Max(1, Channels * Math.Max(1, BitsPerSample / 8));

    /// <summary>时长（秒）。</summary>
    public double DurationSeconds => DataLength / (double)(BlockAlign * Math.Max(1, SampleRate));

    /// <summary>帧数。</summary>
    public int FrameCount => DataLength / Math.Max(1, BlockAlign);
}

/// <summary>
/// 手写 WAV 解析 + 唇形包络（给 TTS 用；也做成 public static 方便自检用合成 WAV 断言）。
/// <para>Windows 语音合成返回的 WAV 头不一定只有 44 字节（可能有 LIST/fact 等块），所以按 RIFF 块表遍历找 <c>fmt </c> / <c>data</c>。</para>
/// </summary>
public static class WaveAudio
{
    private const int EnvelopeWindowMs = 20;
    private const int EnvelopeHopMs = 20;

    /// <summary>解析 WAV 头（失败返回 false，并给出原因）。</summary>
    public static bool TryParse(byte[]? wav, out WaveAudioInfo info, out string? error)
    {
        info = default;
        error = null;
        if (wav is null || wav.Length < 44) { error = "数据太短，不是 WAV"; return false; }
        if (wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F'
            || wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E')
        {
            error = "不是 RIFF/WAVE 格式";
            return false;
        }

        var sampleRate = 0;
        var channels = 0;
        var bits = 0;
        var dataOffset = -1;
        var dataLength = 0;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, offset, 4);
            var chunkSize = BitConverter.ToInt32(wav, offset + 4);
            var body = offset + 8;
            if (chunkSize < 0 || body + chunkSize > wav.Length) chunkSize = Math.Max(0, wav.Length - body);
            if (chunkId == "fmt ")
            {
                if (chunkSize < 16) { error = "fmt 块太小"; return false; }
                var format = BitConverter.ToUInt16(wav, body);
                channels = BitConverter.ToUInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToUInt16(wav, body + 14);
                if (format is not (1 or 0xFFFE)) { error = $"不支持的 WAV 编码（format={format}，只支持 PCM/float）"; return false; }
            }
            else if (chunkId == "data")
            {
                dataOffset = body;
                dataLength = chunkSize;
            }

            offset = body + chunkSize + (chunkSize & 1); // RIFF 块按 2 字节对齐
        }

        if (dataOffset < 0 || dataLength <= 0) { error = "没有找到 data 块"; return false; }
        if (sampleRate <= 0 || channels <= 0 || bits <= 0) { error = "fmt 块信息不完整"; return false; }
        if (bits is not (8 or 16 or 24 or 32)) { error = $"不支持的位深 {bits}"; return false; }

        info = new WaveAudioInfo(sampleRate, channels, bits, dataOffset, dataLength);
        return true;
    }

    /// <summary>取第一声道、转成 16bit 样本（float / 24bit / 32bit PCM 都转成 -1~1 的 short 近似）。</summary>
    public static short[] ReadMonoSamples(byte[] wav, in WaveAudioInfo info)
    {
        var frames = info.FrameCount;
        var samples = new short[frames];
        var bytesPerSample = info.BitsPerSample / 8;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = info.DataOffset + frame * info.BlockAlign;
            if (offset + bytesPerSample > wav.Length) break;
            samples[frame] = info.BitsPerSample switch
            {
                8 => (short)((wav[offset] - 128) << 8),
                16 => BitConverter.ToInt16(wav, offset),
                24 => (short)((wav[offset] | (wav[offset + 1] << 8) | (wav[offset + 2] << 16)) >> 8),
                _ => BitConverter.ToInt16(wav, offset),
            };
        }
        return samples;
    }

    /// <summary>
    /// 由 16bit 单声道样本构建唇形包络：每 20ms 一个 RMS 值，按 95 分位归一化，再做快起慢落的平滑。
    /// </summary>
    public static float[] BuildEnvelope(ReadOnlySpan<short> samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0) return Array.Empty<float>();
        var window = Math.Max(1, sampleRate * EnvelopeWindowMs / 1000);
        var hop = Math.Max(1, sampleRate * EnvelopeHopMs / 1000);
        var count = Math.Max(1, (samples.Length + hop - 1) / hop);
        var rms = new float[count];
        var sorted = new float[count];
        for (var i = 0; i < count; i++)
        {
            var start = i * hop;
            var end = Math.Min(samples.Length, start + window);
            double sum = 0;
            var n = 0;
            for (var j = start; j < end; j++)
            {
                var v = samples[j] / 32768.0;
                sum += v * v;
                n++;
            }
            rms[i] = n > 0 ? (float)Math.Sqrt(sum / n) : 0f;
            sorted[i] = rms[i];
        }

        Array.Sort(sorted);
        var p95 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))];
        if (p95 < 1e-4) p95 = 1e-4f;
        var envelope = new float[count];
        var previous = 0f;
        for (var i = 0; i < count; i++)
        {
            var target = Math.Clamp(rms[i] / p95, 0f, 1f);
            var alpha = target > previous ? 0.55f : 0.30f; // 快起慢落，嘴型更自然
            previous += (target - previous) * alpha;
            envelope[i] = Math.Clamp(previous, 0f, 1f);
        }
        return envelope;
    }
}
