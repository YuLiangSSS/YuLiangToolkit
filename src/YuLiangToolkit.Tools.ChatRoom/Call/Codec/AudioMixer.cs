namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// 群通话混音器：把「每个成员一路 16 kHz μ-law 解码后的 float」重采样到播放设备的混音率，
/// 按声卡「拉」的节奏混合成立体声交给扬声器。
///
/// <para><b>为什么用拉模型（<see cref="Mix"/> 由播放线程调用）</b>：每个设备的时钟都是独立的，
/// 如果自己开定时器往声卡塞数据，播放端迟早会积压或饿死（越放越飘、声音断续）。
/// 由声卡线程按它自己的节奏来要，混音器每次给「下一块正好要播的样本」，时钟永远跟声卡走。</para>
///
/// <para><b>缓冲与延迟</b>：每个成员一个「已重采样到 <see cref="MixSampleRate"/> 的环形缓冲」。
/// 网络抖动让某个成员来慢了，<see cref="Mix"/> 读不到就补静音（underrun，宁可短暂静音也不卡住别人）；
/// 来快了（时钟漂移 / 对端积压）缓冲会涨，超过 <see cref="MaxLatencyMilliseconds"/> 就把最旧的
/// 丢掉、直接裁回 <see cref="TargetLatencyMilliseconds"/>，否则延迟会越滚越大。</para>
///
/// <para><b>线程模型</b>：<see cref="AddFrame"/> 在网络读线程（多路可能并发）、
/// <see cref="Mix"/> 在扬声器线程、<see cref="RemoveMember"/> 在界面 / 会话线程。
/// 所有公开方法内部用同一把 <c>_gate</c> 锁，临界区只有「内存拷贝 / 环形读写」级别的活，
/// 不会持锁做设备操作，因此不会和采集 / 播放线程互相卡死。</para>
///
/// <para><b>边界</b>：成员第一次 <see cref="AddFrame"/> 时自动加入，不需要单独的 AddMember；
/// 单路成员 40 ms 一包、混音率 48 kHz 时，缓冲正常水位约 1920 帧（40 ms），
/// 上限 19200 帧（400 ms）。传入的 <c>mono</c> / <c>count</c> 只在本方法内使用并立刻拷贝，
/// 调用方可以复用数组。</para>
/// </summary>
public sealed class AudioMixer
{
    /// <summary>希望维持的每成员缓冲水位（毫秒）：低于它正常，高于它只是网络抖动。</summary>
    public const int TargetLatencyMilliseconds = 120;

    /// <summary>每成员缓冲上限（毫秒）：超过说明时钟漂移 / 对端积压，丢最旧的数据。</summary>
    public const int MaxLatencyMilliseconds = 400;

    private readonly object _gate = new();
    private readonly Dictionary<string, MemberBuffer> _members = new(StringComparer.Ordinal);
    private readonly int _mixSampleRate;
    private readonly int _maxFrames;
    private readonly int _targetFrames;
    private readonly int _ringCapacity;

    /// <summary>
    /// 建一个混音器。
    /// </summary>
    /// <param name="mixSampleRate">播放设备的混音采样率（48 kHz / 44.1 kHz，不是通话的 16 kHz）。</param>
    public AudioMixer(int mixSampleRate)
    {
        if (mixSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(mixSampleRate), "混音采样率必须为正数");
        _mixSampleRate = mixSampleRate;
        _maxFrames = Math.Max(1, mixSampleRate * MaxLatencyMilliseconds / 1000);
        _targetFrames = Math.Max(1, mixSampleRate * TargetLatencyMilliseconds / 1000);
        // 环容量在 Max 之外再多留 100 ms，允许「先入队再裁剪」，避免一包刚好多出几帧就越界
        _ringCapacity = _maxFrames + Math.Max(1024, mixSampleRate / 10);
    }

    /// <summary>播放设备的混音采样率（48 kHz / 44.1 kHz）。</summary>
    public int MixSampleRate => _mixSampleRate;

    /// <summary>
    /// 收到某个成员的一段单声道 PCM。内部会重采样到 <see cref="MixSampleRate"/> 并存入他的缓冲。
    /// </summary>
    /// <param name="memberId">发送方成员 Id（空字符串也当一个人，不同会话不会混）。</param>
    /// <param name="mono">单声道 float 样本（通常是 μ-law 解码后的 16 kHz）。</param>
    /// <param name="count">有效样本数（可以小于数组长度）。</param>
    /// <param name="sampleRate"><paramref name="mono"/> 的采样率（通话协议固定 16 kHz）。</param>
    public void AddFrame(string memberId, float[]? mono, int count, int sampleRate)
    {
        if (mono is null || count <= 0) return;
        if (count > mono.Length) count = mono.Length;
        if (count <= 0) return;
        if (sampleRate <= 0) sampleRate = CallProtocolRules.AudioSampleRate;
        memberId ??= "";

        lock (_gate)
        {
            if (!_members.TryGetValue(memberId, out var member))
            {
                member = new MemberBuffer(_ringCapacity, sampleRate, _mixSampleRate);
                _members[memberId] = member;
            }
            else if (member.SourceRate != sampleRate)
            {
                // 设备重开（采样率变了）：重建重采样器，清掉旧速率下攒的样本
                member.SourceRate = sampleRate;
                member.Resampler = new PcmResampler(sampleRate, _mixSampleRate);
                member.Head = 0;
                member.Count = 0;
            }

            // 输出容量：理论需要 count * mix / source 个，多给几个给插值的边界
            var needed = (int)((long)count * _mixSampleRate / sampleRate) + 4;
            if (member.ConvertBuffer.Length < needed)
                member.ConvertBuffer = new float[needed + 256];

            var converted = member.Resampler.Process(mono.AsSpan(0, count), member.ConvertBuffer.AsSpan(0, needed));
            if (converted <= 0) return;

            AppendCore(member, member.ConvertBuffer, converted);
        }
    }

    /// <summary>把一个人的声音加入 / 清空（人离开通话时调用；再 <see cref="AddFrame"/> 会自动重新加入）。</summary>
    public void RemoveMember(string memberId)
    {
        memberId ??= "";
        lock (_gate) _members.Remove(memberId);
    }

    /// <summary>清空所有人（挂断 / 重新开始时调用）。</summary>
    public void Clear()
    {
        lock (_gate) _members.Clear();
    }

    /// <summary>
    /// 混出 <paramref name="frames"/> 帧立体声（interleaved，左 = 右），写进
    /// <paramref name="stereoOutput"/>（至少 <c>frames * 2</c> 个元素）。
    /// </summary>
    /// <returns>实际写入的帧数（恒等于 <paramref name="frames"/>；没人说话时是静音）。</returns>
    public int Mix(int frames, Span<float> stereoOutput)
    {
        if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames), "帧数不能为负");
        if (stereoOutput.Length < frames * 2)
            throw new ArgumentException("输出缓冲至少要能放 frames * 2 个样本", nameof(stereoOutput));

        lock (_gate)
        {
            if (_members.Count == 0)
            {
                // 没人：照样要把整段写静音（声卡会播这一块，缓冲里可能是上一次的垃圾）
                stereoOutput[..(frames * 2)].Clear();
                return frames;
            }

            // 逐帧从每个成员读一个样本累加。所有缓冲都已统一到 MixSampleRate，
            // 所以「一帧 = 每个成员一个样本」；没数据的成员自然贡献 0（不推进它的读指针）。
            var values = _members.Values;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                foreach (var member in values)
                {
                    if (member.Count == 0) continue;
                    sum += member.Ring[member.Head];
                    member.Head = (member.Head + 1) % member.Ring.Length;
                    member.Count--;
                }

                // 多人同时说话会叠加超界：硬限幅到 [-1, 1]，宁可削波也不要爆音
                if (sum > 1f) sum = 1f;
                else if (sum < -1f) sum = -1f;

                var outputIndex = frame * 2;
                stereoOutput[outputIndex] = sum;
                stereoOutput[outputIndex + 1] = sum;
            }
        }

        return frames;
    }

    /// <summary>某个成员当前攒了多少帧（还没被 <see cref="Mix"/> 取走的）。</summary>
    public int BufferedFrames(string memberId)
    {
        memberId ??= "";
        lock (_gate)
        {
            return _members.TryGetValue(memberId, out var member) ? member.Count : 0;
        }
    }

    /// <summary>当前有缓冲的成员 Id 快照（统计 / 调试用）。</summary>
    public IReadOnlyList<string> Members
    {
        get
        {
            lock (_gate) return _members.Keys.ToArray();
        }
    }

    // ---------------------------------------------------------------- 环形缓冲内部实现

    /// <summary>把重采样后的数据追加进成员缓冲，并按延迟上限裁掉最旧的。</summary>
    private void AppendCore(MemberBuffer member, float[] source, int count)
    {
        var capacity = member.Ring.Length;
        if (count >= capacity)
        {
            // 一包就比整个环还长（异常大的包）：只保留最后 capacity 个样本
            member.Head = 0;
            member.Count = 0;
            AppendSamples(member, source.AsSpan(count - capacity, capacity));
        }
        else
        {
            var overflow = member.Count + count - capacity;
            if (overflow > 0) DropOldestCore(member, overflow);
            AppendSamples(member, source.AsSpan(0, count));
        }

        // 时钟漂移 / 对端积压：超过上限就丢最旧的，直接裁回目标水位，避免延迟越滚越大
        if (member.Count > _maxFrames)
            DropOldestCore(member, member.Count - _targetFrames);
    }

    /// <summary>把样本写进环的尾部（可能分两段写）。调用方保证总长度不超过容量。</summary>
    private static void AppendSamples(MemberBuffer member, ReadOnlySpan<float> samples)
    {
        var ring = member.Ring;
        var capacity = ring.Length;
        var write = (member.Head + member.Count) % capacity;
        while (!samples.IsEmpty)
        {
            var chunk = Math.Min(samples.Length, capacity - write);
            samples[..chunk].CopyTo(ring.AsSpan(write, chunk));
            samples = samples[chunk..];
            write = (write + chunk) % capacity;
            member.Count += chunk;
        }
    }

    /// <summary>从头部丢掉 <paramref name="count"/> 帧最旧的数据。</summary>
    private static void DropOldestCore(MemberBuffer member, int count)
    {
        if (count <= 0) return;
        if (count >= member.Count)
        {
            member.Head = 0;
            member.Count = 0;
            return;
        }

        member.Head = (member.Head + count) % member.Ring.Length;
        member.Count -= count;
    }

    /// <summary>一个成员的缓冲（只由 <see cref="AudioMixer"/> 在 <c>_gate</c> 里访问）。</summary>
    private sealed class MemberBuffer
    {
        public MemberBuffer(int capacity, int sourceRate, int mixSampleRate)
        {
            Ring = new float[capacity];
            SourceRate = sourceRate;
            Resampler = new PcmResampler(sourceRate, mixSampleRate);
            ConvertBuffer = Array.Empty<float>();
        }

        /// <summary>已重采样到混音率的环形缓冲。</summary>
        public float[] Ring { get; }

        /// <summary>读指针（最旧一帧的下标）。</summary>
        public int Head { get; set; }

        /// <summary>缓冲里的有效帧数。</summary>
        public int Count { get; set; }

        /// <summary>最近一次 <see cref="AddFrame"/> 用的源采样率。</summary>
        public int SourceRate { get; set; }

        /// <summary>这个成员专属的重采样器（保留跨包相位）。</summary>
        public PcmResampler Resampler { get; set; }

        /// <summary>重采样临时缓冲（复用以减少 GC）。</summary>
        public float[] ConvertBuffer { get; set; }
    }
}
