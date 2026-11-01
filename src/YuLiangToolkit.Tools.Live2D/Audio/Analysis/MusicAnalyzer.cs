using YuLiangToolkit.Tools.Live2D.Audio;

namespace YuLiangToolkit.Tools.Live2D.Audio.Analysis;

/// <summary>
/// 音乐跟唱 / 律动的音频分析器（纯算法，无第三方包、不碰音频设备）。
///
/// <para><b>输入</b>：48kHz 立体声 interleaved float（WASAPI 回环采集的块），内部降采样到 16kHz，
/// 1024 点 Hann 窗、hop 256（≈62.5 帧/秒）做频域特征。</para>
///
/// <para><b>处理链</b>：
/// 1) 特征：RMS、低频 60~200Hz、人声带 300~3400Hz 能量、谱平坦度、谱质心、YIN 基频 / 清晰度、
///    谐波比例、共振峰峰数、立体声中心声道占比、音节调制、颤音深度；
/// 2) 节拍：谱通量 onset → 自相关估 BPM（60~180）→ 相位 PLL 校正；
/// 3) 人声/纯音乐分类：多特征加权融合 + 最近 20~30 秒运行分位数自适应 + 时序滞后（1.5s / 3s）；
/// 4) 唇形：人声带包络（attack 15ms / release 90ms，按最近 10 秒 p90 归一化）+ 谱质心映射嘴型。</para>
///
/// <para><b>静音快速路径</b>：块 RMS 低于噪声门限时跳过 FFT / YIN / 共振峰等重计算，只推进节拍相位、
/// 衰减 MouthOpen / 能量、刷新时间戳，避免没人放音乐时空烧 CPU。</para>
///
/// <para><b>线程</b>：由采集线程单线程调用 <see cref="Analyze"/>；<see cref="Latest"/> 是整体替换的只读快照。</para>
/// </summary>
public sealed class MusicAnalyzer
{
    // ---------- 采样 / 窗口参数 ----------

    /// <summary>内部处理采样率（降采样目标）。</summary>
    private const int AnalysisSampleRate = 16000;

    /// <summary>FFT 点数 / 窗长（16k 下 64ms）。</summary>
    private const int WindowSize = 1024;

    /// <summary>帧移（16ms，≈62.5 帧/秒）。</summary>
    private const int HopSize = 256;

    /// <summary>FFT 幅度谱频点数（N/2+1）。</summary>
    private const int BinCount = (WindowSize / 2) + 1;

    /// <summary>每个频点对应的 Hz。</summary>
    private const double BinHz = (double)AnalysisSampleRate / WindowSize;

    /// <summary>静音门限（块 RMS，约 -56 dBFS）：低于它走快速路径。</summary>
    private const double NoiseGateRms = 0.0015;

    /// <summary>有声音后判定「还在播」的挂起时间（秒）。</summary>
    private const double PlayingHoldSeconds = 0.5;

    /// <summary>每帧时长（秒）。</summary>
    private const double HopSeconds = (double)HopSize / AnalysisSampleRate;

    // ---------- 频带 ----------

    private const int BassStartBin = 4;     // 62.5 Hz
    private const int BassEndBin = 13;      // 203 Hz
    private const int VoiceStartBin = 19;   // ~297 Hz
    private const int VoiceEndBin = 218;    // ~3406 Hz
    private const int OnsetStartBin = 13;   // 203 Hz
    private const int OnsetEndBin = 256;    // 4 kHz

    // ---------- 真实混音人声判定（中心声道带限自相关 / 谐波梳） ----------

    /// <summary>中心声道人声带（带限自相关 / 谐波梳用）：~200~3500Hz。</summary>
    private const int VocalBandStartBin = 13;   // ~203 Hz
    private const int VocalBandEndBin = 224;    // 3500 Hz

    /// <summary>人声齿音 / 气声高带：4kHz 起。</summary>
    private const int HighBandStartBin = 256;   // 4000 Hz

    /// <summary>自相关找基频的 lag 范围（16k 采样下 80~1000Hz）。</summary>
    private const int PeriodicityMinLag = 16;   // 1000 Hz
    private const int PeriodicityMaxLag = 200;  // 80 Hz

    /// <summary>周期显著度超过这个值就认为「mid 里有人声」：打开音节/颤音门控（真实混音不依赖 YIN）。</summary>
    private const double PeriodicityVoicingThreshold = 0.30;

    // ---------- YIN ----------

    private const int PitchMinLag = 16;     // 1000 Hz
    private const int PitchMaxLag = 178;    // ~90 Hz

    // ---------- 分类权重（经验初值；自检里用「类人声 / 纯乐器 / 粉噪」合成信号校准） ----------

    private const double WeightClarity = 0.15;
    private const double WeightHarmonic = 0.10;
    private const double WeightTonalness = 0.10;
    private const double WeightFormant = 0.10;
    private const double WeightCenter = 0.15;
    private const double WeightSyllable = 0.20;
    private const double WeightVibrato = 0.20;

    // 真实混音版基础权重（ComputeVocalRawScoreEx 内联使用：中心 / 共振峰 / 中心包络动态 / 频带互补）

    /// <summary>分类高 / 低阈值（含灵敏度放大后的分数）。</summary>
    private const double VocalHighThreshold = 0.58;
    private const double VocalLowThreshold = 0.50;

    /// <summary>判为「带歌词」需要持续多久（秒）。</summary>
    private const double VocalHoldSeconds = 1.2;

    /// <summary>判为「纯音乐」需要持续多久（秒）。</summary>
    private const double InstrumentalHoldSeconds = 2.0;

    /// <summary>数据不足（少于这个秒数）时类型为 Unknown。</summary>
    private const double MinDecisionSeconds = 1.0;

    // ---------- 无节拍回退（钢琴曲 / 纯人声 / 环境音乐也要能轻轻晃） ----------

    /// <summary>估不出可靠 BPM 时的舒缓摆动频率（Hz）。</summary>
    private const double FallbackSwayHz = 0.5;

    /// <summary>无节拍回退时 BeatStrength 的保底值（再按响度略微放大）。</summary>
    private const double FallbackBeatStrength = 0.35;

    /// <summary>节拍速率换挡的平滑系数（每 hop）；避免「回退相位 ↔ 真实节拍」切换时跳变。</summary>
    private const double BeatRateSmoothing = 0.05;

    /// <summary>自适应归一化的历史窗口（分数样本数，30 秒 @62.5fps）。</summary>
    private const int ScoreHistorySize = 1875;

    /// <summary>包络归一化历史长度（10 秒）。</summary>
    private const int EnvelopeHistorySize = 625;

    /// <summary>onset 历史长度（6 秒）。</summary>
    private const int OnsetHistorySize = 375;

    /// <summary>基频历史长度（4 秒，YIN 每 2 帧算一次 → 31.25Hz）。</summary>
    private const int PitchHistorySize = 128;

    // ---------- 输入重采样状态（任意采样率 → 16k，线性插值） ----------

    private readonly double _resampleStep;

    /// <summary>下一个输出采样在「全局输入帧坐标」里的位置（跨 Analyze 调用连续）。</summary>
    private double _nextOutputPosition;

    /// <summary>已处理的输入帧总数（全局，用于把重采样位置换算成当前块的帧坐标）。</summary>
    private long _inputFrameIndex;
    private double _previousMono;
    private double _previousSide;
    private bool _hasPreviousFrame;

    // 16k 环形缓冲（最近 1024 点窗口用）
    private readonly double[] _ringMono;
    private readonly double[] _ringSide;
    private int _ringWrite;
    private int _ringCount;
    private int _samplesSinceHop;

    // ---------- FFT / 频谱缓冲（全部预分配复用） ----------

    private readonly double[] _hannWindow;
    private readonly double[] _fftReal;
    private readonly double[] _fftImag;
    private readonly double[] _magnitudeMid;
    private readonly double[] _magnitudeSide;
    private readonly double[] _spectrumSmooth;
    private readonly double[] _yinSamples;

    // ---------- 特征状态 ----------

    private double _rms;
    private double _bassEnergy;
    private double _voiceEnergy;
    private double _voiceEnergySide;
    private double _flatness;
    private double _centroidHz = 1800;
    private double _harmonicRatio;
    private double _formantScore;
    private double _centerVoiceRatio = 0.5;
    private double _clarity;
    private double _pitchHz;
    private bool _pitchValid;
    private double _pitchSmoothed;
    private double _syllabicModulation;
    private double _vibratoDepth;

    // ---------- 节拍状态 ----------

    private readonly double[] _onsetHistory;
    private readonly double[] _pitchHistory;
    private readonly double[] _onsetTemp;
    private int _onsetWrite;
    private int _onsetCount;
    private int _pitchWrite;
    private int _pitchCount;
    private int _hopsSincePitch;
    private int _hopsSinceBpm;
    private double _onsetEnvelope;
    private double _onsetSlowAverage = 0.02;
    private double _beatStrength;
    private double _bpm;
    private double _beatPhase;
    private double _onsetPeakCorrelation;

    /// <summary>当前节拍相位推进速率（Hz）：可靠 BPM 时跟拍频，否则回退到 0.5Hz 舒缓摆动；换挡带平滑。</summary>
    private double _beatRateHz = FallbackSwayHz;

    /// <summary>最近一次 BPM 估计是否可靠（无鼓点 / 纯人声时为 false，走回退摆动）。</summary>
    private bool _beatReliable;

    /// <summary>发布给运行时的 BeatStrength（可靠节拍时用 onset 强度，否则用 0.35 起的响度保底）。</summary>
    private double _displayBeatStrength = FallbackBeatStrength;

    /// <summary>上一个 hop 的时域电平（log RMS），做 onset 差分用。</summary>
    private double _previousHopDb = -13.8;

    /// <summary>基频轨迹稳定度（0~1）：稳定音高才可能是人声/旋律，和弦与打击会跳变。</summary>
    private double _pitchStability;

    /// <summary>「像人声」的持续度（0~1）：音高稳定 + 清晰度够，用来给音节/颤音/谐波特征做门控。</summary>
    private double _voicedStrength;

    /// <summary>处于「播放中」的累计墙钟秒数（稀疏点击时有效分析时间很短，但内容判断仍需要够长时间）。</summary>
    private double _observedPlayingSeconds;

    // ---------- 真实混音人声特征（mid 人声带） ----------

    /// <summary>中心声道人声带的周期显著度（带限自相关峰值相对均值，0~1）。</summary>
    private double _periodicitySalience;

    /// <summary>中心声道人声带的谐波梳对比度（0~1；人声/单音乐器高，噪声/打击低）。</summary>
    private double _harmonicSalience;

    /// <summary>人声带能量占 mid 总能量的比例（调试用）。</summary>
    private double _voiceBandEnergyRatio;

    /// <summary>自相关峰对应的 lag（16k 采样），用于调试 / 自检。</summary>
    private int _periodicityLag;

    /// <summary>mid 4~8kHz 能量占 mid 200~8000Hz 能量的比例（人声齿音 / 气声会抬高它）。</summary>
    private double _midHighBandRatio;

    /// <summary>mid 4~8kHz 谱平坦度（人声齿音 / 辅音噪声偏平坦，纯乐器高次谐波偏尖锐）。</summary>
    private double _midHighBandFlatness;

    // ---------- 音节 / 颤音调制 ----------

    /// <summary>人声带包络的慢包络（~0.4s），用来去掉整体响度 / 音符衰减，只看快速起伏。</summary>
    private double _voiceSlowEnvelope;

    /// <summary>最近 32 个「人声包络 / 慢包络」（≈0.5s），做 2~6Hz 调制检测。</summary>
    private readonly double[] _modulationEnvelope;

    private readonly double[] _modulationTemp;
    private int _modulationWrite;
    private int _modulationCount;

    /// <summary>基频稳定度 / 颤音检测用的临时缓冲（比 _modulationTemp 大，能装下 2 秒基频历史）。</summary>
    private readonly double[] _pitchWindowTemp;

    // ---------- 自适应归一化 / 唇形 ----------

    private readonly double[] _scoreHistory;
    private readonly double[] _scoreSortTemp;
    private readonly double[] _envelopeHistory;
    private readonly double[] _envelopeSortTemp;
    private int _scoreWrite;
    private int _scoreCount;
    private int _envelopeWrite;
    private int _envelopeCount;
    private int _hopsSinceQuantile;
    private double _scoreLowQuantile;
    private double _scoreHighQuantile;
    private double _envelopeP90;
    private double _mouthOpen;
    private double _mouthForm;

    // ---------- 分类状态 ----------

    private double _vocalRawScore;
    private double _vocalFinalScore;
    private double _vocalSmoothedScore;
    private double _vocalRunSeconds;
    private double _instrumentalRunSeconds;
    private double _activeAnalyzedSeconds;
    private MusicContentKind _kind = MusicContentKind.Unknown;
    private double _confidence;

    // ---------- 播放状态 ----------

    private long _lastSoundTimestampMs = long.MinValue / 2;
    private long _lastTimestampMs;
    private MusicAnalysisSnapshot? _latest;

    /// <summary>
    /// 创建分析器。<paramref name="sampleRate"/> 传采集设备的实际混音采样率（48000 或 44100 都行；
    /// 内部用线性插值重采样到 16k，不假定 48k）。
    /// </summary>
    public MusicAnalyzer(int sampleRate)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率太低（至少 8k）");

        _resampleStep = sampleRate / (double)AnalysisSampleRate;

        _ringMono = new double[WindowSize * 2];
        _ringSide = new double[WindowSize * 2];

        _hannWindow = new double[WindowSize];
        FastFft.FillHannWindow(_hannWindow);
        _fftReal = new double[WindowSize];
        _fftImag = new double[WindowSize];
        _magnitudeMid = new double[BinCount];
        _magnitudeSide = new double[BinCount];
        _spectrumSmooth = new double[BinCount];
        _yinSamples = new double[WindowSize];

        _onsetHistory = new double[OnsetHistorySize];
        _pitchHistory = new double[PitchHistorySize];
        _onsetTemp = new double[OnsetHistorySize];

        _scoreHistory = new double[ScoreHistorySize];
        _scoreSortTemp = new double[ScoreHistorySize];
        _envelopeHistory = new double[EnvelopeHistorySize];
        _envelopeSortTemp = new double[EnvelopeHistorySize];
        _modulationEnvelope = new double[32];
        _modulationTemp = new double[32];
        _pitchWindowTemp = new double[PitchHistorySize];
    }

    // ---------- 对外 API ----------

    /// <summary>识别灵敏度（0.3~3.0；越大越容易判成「带歌词」，唇形 / 分类都受它影响）。</summary>
    public double Sensitivity { get; set; } = 1.0;

    /// <summary>最近一次发布的快照（还没有数据时为 null）。</summary>
    public MusicAnalysisSnapshot? Latest => _latest;

    // ---------- 诊断属性（离线调参 / 自检用，不参与契约） ----------

    /// <summary>中心声道人声带的周期显著度（带限自相关峰值相对均值，0~1）。</summary>
    public double PeriodicitySalience => _periodicitySalience;

    /// <summary>中心声道人声带的谐波梳对比度（0~1）。</summary>
    public double HarmonicSalience => _harmonicSalience;

    /// <summary>人声带能量占 mid 总能量的比例（0~1）。</summary>
    public double VoiceBandEnergyRatio => _voiceBandEnergyRatio;

    /// <summary>最近一次人声原始分（未乘 Sensitivity / 自适应）。</summary>
    public double LastVocalRawScore => _vocalRawScore;

    /// <summary>YIN 清晰度（诊断）。</summary>
    public double ClarityDiag => _clarity;

    /// <summary>基频轨迹稳定度（诊断）。</summary>
    public double PitchStabilityDiag => _pitchStability;

    /// <summary>「像人声」门控强度（诊断）。</summary>
    public double VoicedStrengthDiag => _voicedStrength;

    /// <summary>共振峰打分（诊断）。</summary>
    public double FormantScoreDiag => _formantScore;

    /// <summary>谱平坦度（诊断）。</summary>
    public double FlatnessDiag => _flatness;

    /// <summary>谐波比例（诊断）。</summary>
    public double HarmonicRatioDiag => _harmonicRatio;

    /// <summary>颤音调制深度（诊断）。</summary>
    public double VibratoDepthDiag => _vibratoDepth;

    /// <summary>mid 4~8kHz 能量占比（诊断）。</summary>
    public double MidHighBandRatioDiag => _midHighBandRatio;

    /// <summary>mid 4~8kHz 谱平坦度（诊断）。</summary>
    public double MidHighBandFlatnessDiag => _midHighBandFlatness;

    /// <summary>带限自相关峰对应 lag（诊断）。</summary>
    public int PeriodicityLagDiag => _periodicityLag;

    /// <summary>
    /// 分析一块 48k 立体声 interleaved 音频（<paramref name="interleavedStereo"/> 长度 = frames×2）。
    /// 返回最新快照（内部可能处理多个 hop，快照总是最终状态）。
    /// </summary>
    public MusicAnalysisSnapshot Analyze(ReadOnlySpan<float> interleavedStereo, int frameCount, long timestampMs)
    {
        _lastTimestampMs = timestampMs;

        var availableFrames = interleavedStereo.Length / 2;
        if (frameCount > availableFrames) frameCount = availableFrames;
        if (frameCount <= 0) return Publish();

        // ---- 1) mid/side 抽取 + 线性插值重采样到 16k（支持 44.1k / 48k 等任意采样率）+ 原始块 RMS ----
        double sumSquares = 0.0;
        for (var i = 0; i < frameCount; i++)
        {
            var left = interleavedStereo[i * 2];
            var right = interleavedStereo[(i * 2) + 1];
            sumSquares += (left * left) + (right * right);

            var mono = (left + right) * 0.5;
            var side = (left - right) * 0.5;
            var frameIndex = _inputFrameIndex++;
            if (!_hasPreviousFrame)
            {
                _previousMono = mono;
                _previousSide = side;
                _hasPreviousFrame = true;
            }

            // 用「全局输入帧坐标」插值：_nextOutputPosition 跨块递增，所以每块之间能无缝衔接
            // （旧实现用块内坐标 i 比较，第二块起相位永远大于 i，分析器会停摆——l2d-verify 抓到的 bug）
            while (_nextOutputPosition <= frameIndex)
            {
                var t = _nextOutputPosition - frameIndex + 1.0; // 1 = 当前帧，0 = 上一帧
                if (t < 0) t = 0;
                else if (t > 1) t = 1;
                PushSample(
                    _previousMono + ((mono - _previousMono) * t),
                    _previousSide + ((side - _previousSide) * t));
                _nextOutputPosition += _resampleStep;
            }

            _previousMono = mono;
            _previousSide = side;
        }

        var blockRms = Math.Sqrt(sumSquares / (2.0 * frameCount));
        var silent = blockRms < NoiseGateRms;

        if (!silent)
        {
            _lastSoundTimestampMs = timestampMs;
        }

        // ---- 2) 有声音：正常分析每个 hop；静音：走快速路径（不跑 FFT / YIN） ----
        if (silent)
        {
            while (_samplesSinceHop >= HopSize)
            {
                _samplesSinceHop -= HopSize;
                ProcessSilentHop();
            }
        }
        else
        {
            while (_samplesSinceHop >= HopSize && _ringCount >= WindowSize)
            {
                _samplesSinceHop -= HopSize;
                ProcessActiveHop();
            }
        }

        // ---- 3) 静音挂起：超过 500ms 没声音就认为不播了 ----
        var playing = timestampMs - _lastSoundTimestampMs <= PlayingHoldSeconds * 1000.0;
        if (!playing)
        {
            _kind = MusicContentKind.Silent;
            _confidence = 0.0;
            _activeAnalyzedSeconds = 0.0;
            _observedPlayingSeconds = 0.0;
            _vocalRunSeconds = 0.0;
            _instrumentalRunSeconds = 0.0;
            _mouthOpen *= 0.85;
            _bassEnergy *= 0.85;
            _voiceEnergy *= 0.85;
            _pitchValid = false;
        }

        UpdateRmsSmoothing(blockRms);
        return Publish();
    }

    /// <summary>清空全部状态（换歌 / 重新开始识别时调）。</summary>
    public void Reset()
    {
        _nextOutputPosition = 0;
        _inputFrameIndex = 0;
        _hasPreviousFrame = false;
        _previousMono = 0;
        _previousSide = 0;
        Array.Clear(_ringMono);
        Array.Clear(_ringSide);
        _ringWrite = 0;
        _ringCount = 0;
        _samplesSinceHop = 0;

        _rms = 0;
        _bassEnergy = 0;
        _voiceEnergy = 0;
        _voiceEnergySide = 0;
        _flatness = 0;
        _centroidHz = 1800;
        _harmonicRatio = 0;
        _formantScore = 0;
        _centerVoiceRatio = 0.5;
        _clarity = 0;
        _pitchHz = 0;
        _pitchValid = false;
        _pitchSmoothed = 0;
        _syllabicModulation = 0;
        _vibratoDepth = 0;
        _periodicitySalience = 0;
        _harmonicSalience = 0;
        _voiceBandEnergyRatio = 0;
        _midHighBandRatio = 0;
        _midHighBandFlatness = 0;
        _periodicityLag = 0;
        _voiceSlowEnvelope = 0;
        Array.Clear(_modulationEnvelope);
        Array.Clear(_pitchWindowTemp);
        _modulationWrite = 0;
        _modulationCount = 0;

        Array.Clear(_onsetHistory);
        Array.Clear(_pitchHistory);
        _onsetWrite = 0;
        _onsetCount = 0;
        _pitchWrite = 0;
        _pitchCount = 0;
        _hopsSincePitch = 0;
        _hopsSinceBpm = 0;
        _onsetEnvelope = 0;
        _onsetSlowAverage = 0.02;
        _beatStrength = 0;
        _bpm = 0;
        _beatPhase = 0;
        _onsetPeakCorrelation = 0;
        _beatRateHz = FallbackSwayHz;
        _beatReliable = false;
        _displayBeatStrength = FallbackBeatStrength;
        _previousHopDb = -13.8;
        _pitchStability = 0;
        _voicedStrength = 0;

        Array.Clear(_scoreHistory);
        Array.Clear(_envelopeHistory);
        _scoreWrite = 0;
        _scoreCount = 0;
        _envelopeWrite = 0;
        _envelopeCount = 0;
        _hopsSinceQuantile = 0;
        _scoreLowQuantile = 0;
        _scoreHighQuantile = 1;
        _envelopeP90 = 0;
        _mouthOpen = 0;
        _mouthForm = 0;

        _vocalRawScore = 0;
        _vocalFinalScore = 0;
        _vocalSmoothedScore = 0;
        _vocalRunSeconds = 0;
        _instrumentalRunSeconds = 0;
        _activeAnalyzedSeconds = 0;
        _observedPlayingSeconds = 0;
        _kind = MusicContentKind.Unknown;
        _confidence = 0;
        _lastSoundTimestampMs = long.MinValue / 2;
        _lastTimestampMs = 0;
        _latest = null;
    }

    // ---------- 快照发布 ----------

    private MusicAnalysisSnapshot Publish()
    {
        var playing = _lastTimestampMs - _lastSoundTimestampMs <= PlayingHoldSeconds * 1000.0;
        var kind = playing ? _kind : MusicContentKind.Silent;
        var snapshot = new MusicAnalysisSnapshot
        {
            TimestampMs = _lastTimestampMs,
            IsPlaying = playing,
            Kind = kind,
            Confidence = playing ? _confidence : 0.0,
            VocalScore = Clamp01(_vocalRawScore * Math.Clamp(Sensitivity, 0.3, 3.0)),
            Rms = _rms,
            BassEnergy = _bassEnergy,
            VoiceEnergy = _voiceEnergy,
            MouthOpen = playing ? _mouthOpen : 0.0,
            MouthForm = _mouthForm,
            BeatPhase = _beatPhase,
            Bpm = _bpm,
            BeatStrength = playing ? _displayBeatStrength : 0.0,
            PitchValid = playing && _pitchValid && _voicedStrength > 0.5,
            PitchHz = _pitchValid ? _pitchHz : 0.0,
            CenterVoiceRatio = _centerVoiceRatio,
            SyllabicModulation = _syllabicModulation,
        };

        _latest = snapshot;
        return snapshot;
    }

    // ---------- 重采样 / 环形缓冲 ----------

    /// <summary>把重采样后的 16k 采样推进环形缓冲（每帧调用，0 分配）。</summary>
    private void PushSample(double mono, double side)
    {
        _ringMono[_ringWrite] = mono;
        _ringSide[_ringWrite] = side;
        _ringWrite = (_ringWrite + 1) % _ringMono.Length;
        if (_ringCount < _ringMono.Length) _ringCount++;
        _samplesSinceHop++;
    }

    /// <summary>把最近 <see cref="WindowSize"/> 个 16k 采样拷出来（<paramref name="side"/> = true 取 side 声道）。</summary>
    private void CopyLatestWindow(double[] destination, bool side)
    {
        var ring = side ? _ringSide : _ringMono;
        var capacity = ring.Length;
        var start = (_ringWrite - WindowSize + capacity) % capacity;
        var count = Math.Min(WindowSize, _ringCount);
        for (var i = 0; i < count; i++) destination[i] = ring[(start + i) % capacity];
        for (var i = count; i < WindowSize; i++) destination[i] = 0.0;
    }

    /// <summary>取最近 <paramref name="sampleCount"/> 个 16k 采样的 RMS（时域 onset 用，不依赖窗函数位置）。</summary>
    private double ComputeRecentRms(int sampleCount)
    {
        var capacity = _ringMono.Length;
        var count = Math.Min(sampleCount, _ringCount);
        if (count <= 0) return 0.0;
        var start = (_ringWrite - count + capacity) % capacity;
        double sum = 0.0;
        for (var i = 0; i < count; i++)
        {
            var value = _ringMono[(start + i) % capacity];
            sum += value * value;
        }

        return Math.Sqrt(sum / count);
    }

    // ---------- 静音快速路径 ----------

    private void ProcessSilentHop()
    {
        // 节拍相位继续推进（有 BPM 时），但没有 onset 校正
        AdvanceBeatPhase(0.0);
        _beatStrength *= 0.9;
        _onsetEnvelope *= 0.9;
        _mouthOpen *= 0.75;
        _bassEnergy *= 0.85;
        _voiceEnergy *= 0.85;
        _voiceEnergySide *= 0.85;
        _pitchValid = false;
        _syllabicModulation *= 0.9;
        _vibratoDepth *= 0.9;
        _periodicitySalience *= 0.9;
        _harmonicSalience *= 0.9;
        PushOnsetSample(0.0);
        _activeAnalyzedSeconds = 0.0;
        _vocalSmoothedScore *= 0.95;
        _voiceSlowEnvelope *= 0.9;
        _previousHopDb = Math.Max(-13.8, _previousHopDb - 0.5);
        _pitchStability *= 0.95;
        _voicedStrength *= 0.95;
        OnHopProcessed();
    }

    // ---------- 活跃帧处理 ----------

    private void ProcessActiveHop()
    {
        _activeAnalyzedSeconds += HopSeconds;

        // ---- 1) mid / side 两个 FFT（mid = (L+R)/2 即中心声道，用于谱特征 / 中心占比） ----
        CopyLatestWindow(_fftReal, side: false);
        ApplyHann(_fftReal);
        Array.Clear(_fftImag);
        FastFft.Transform(_fftReal, _fftImag);
        FastFft.MagnitudeSpectrum(_fftReal, _fftImag, _magnitudeMid);

        CopyLatestWindow(_fftReal, side: true);
        ApplyHann(_fftReal);
        Array.Clear(_fftImag);
        FastFft.Transform(_fftReal, _fftImag);
        FastFft.MagnitudeSpectrum(_fftReal, _fftImag, _magnitudeSide);

        // ---- 2) 能量 / 谱特征（幅度按 N/4 归一化，约等于正弦振幅） ----
        const double amplitudeScale = 4.0 / WindowSize;
        var bass = 0.0;
        var voice = 0.0;
        var voiceSide = 0.0;
        for (var bin = BassStartBin; bin <= BassEndBin; bin++)
        {
            var scaled = _magnitudeMid[bin] * amplitudeScale;
            bass += scaled * scaled;
        }

        for (var bin = VoiceStartBin; bin <= VoiceEndBin; bin++)
        {
            var scaled = _magnitudeMid[bin] * amplitudeScale;
            voice += scaled * scaled;
            var sideScaled = _magnitudeSide[bin] * amplitudeScale;
            voiceSide += sideScaled * sideScaled;
        }

        var voiceAmplitude = Math.Sqrt(voice);
        var bassAmplitude = Math.Sqrt(bass);

        // ---- 3) 谱平坦度 / 质心 / 共振峰 ----
        _flatness = ComputeSpectralFlatness(_magnitudeMid, VoiceStartBin, VoiceEndBin);
        var centroid = ComputeSpectralCentroid(_magnitudeMid, VoiceStartBin, VoiceEndBin, BinHz);
        if (centroid > 0) _centroidHz += (centroid - _centroidHz) * 0.25;

        var formantScore = ComputeFormantScore(_magnitudeMid, _spectrumSmooth, VoiceStartBin, VoiceEndBin);
        _formantScore += (formantScore - _formantScore) * 0.2;

        // ---- 4) 立体声中心占比（人声频带内 mid / (mid + side)） ----
        var center = voice / (voice + voiceSide + 1e-12);
        _centerVoiceRatio += (center - _centerVoiceRatio) * 0.25;

        // ---- 4.5) 真实混音人声特征：mid 人声带带限自相关 + 谐波梳（不依赖整段 YIN 清晰度） ----
        UpdateMidVoiceSalience();

        // ---- 5) onset（时域 hop 能量差）：比单窗谱通量更抗「瞬态落在 Hann 窗边缘」的衰减，
        //        纯点击 / 鼓点等稀疏打击也能稳定抓到，不依赖音调 ----
        var hopRms = ComputeRecentRms(HopSize);
        var hopDb = Math.Log(hopRms + 1e-6);
        var levelJump = hopDb - _previousHopDb;
        _previousHopDb = hopDb;
        var onset = levelJump > 0 ? Math.Min(1.0, levelJump * 0.35) : 0.0;
        _onsetEnvelope += (onset - _onsetEnvelope) * (onset > _onsetEnvelope ? 0.6 : 0.25);
        PushOnsetSample(_onsetEnvelope);

        // onset 相对最近平均的强度（BeatStrength 0~1）
        _onsetSlowAverage += (_onsetEnvelope - _onsetSlowAverage) * 0.02;
        var beatStrengthTarget = Clamp01((_onsetEnvelope / (_onsetSlowAverage + 1e-4) - 1.0) * 0.35);
        _beatStrength += (beatStrengthTarget - _beatStrength) * 0.4;

        // ---- 6) 节拍相位推进 + PLL 校正 ----
        AdvanceBeatPhase(_beatStrength);

        // ---- 7) YIN 基频（每 2 个 hop 算一次） ----
        if (++_hopsSincePitch >= 2)
        {
            _hopsSincePitch = 0;
            CopyLatestWindow(_yinSamples, side: false);
            var pitch = EstimatePitchYin(_yinSamples, AnalysisSampleRate, PitchMinLag, PitchMaxLag, out var clarity);
            _clarity = clarity;

            // 八度防抖：谐波丰富的信号 YIN 会在基频 / 高八度之间来回跳（110 ↔ 220），
            // 用上一帧的平滑值把明显是 ±1 八度的结果拉回同一八度，音高轨迹才稳定（否则稳定度、颤音全失效）。
            if (pitch > 0 && _pitchSmoothed > 0)
            {
                var ratio = pitch / _pitchSmoothed;
                if (ratio is > 1.7 and < 2.3) pitch *= 0.5;
                else if (ratio is > 0.43 and < 0.59) pitch *= 2.0;
            }

            _pitchValid = pitch > 0 && clarity > 0.3;
            if (_pitchValid)
            {
                _pitchHz = pitch;
                _pitchSmoothed = _pitchSmoothed <= 0 ? pitch : (_pitchSmoothed * 0.8) + (pitch * 0.2);
            }
            else
            {
                _pitchSmoothed *= 0.9;
            }

            PushPitchSample(_pitchValid ? _pitchSmoothed : 0.0);
            UpdateModulations(voiceAmplitude);
        }
        else
        {
            UpdateModulations(voiceAmplitude);
        }

        // ---- 9) 谐波比例（需要稳定音高，否则和弦 / 打击会把比例算飘） ----
        if (_voicedStrength > 0.5 && _pitchValid)
        {
            var harmonic = ComputeHarmonicRatio(_magnitudeMid, _pitchHz, VoiceStartBin, VoiceEndBin, amplitudeScale);
            _harmonicRatio += (harmonic - _harmonicRatio) * 0.25;
        }
        else
        {
            _harmonicRatio *= 0.9;
        }

        // ---- 10) 能量 / 唇形包络 ----
        var bassNorm = Clamp01(bassAmplitude * 1.5);
        var voiceNorm = Clamp01(voiceAmplitude * 1.5);
        _bassEnergy += (bassNorm - _bassEnergy) * 0.2;
        _voiceEnergy += (voiceNorm - _voiceEnergy) * 0.2;
        _voiceEnergySide = voiceSide;

        PushEnvelopeSample(voiceAmplitude);
        PushModulationEnvelope(voiceAmplitude);
        if (++_hopsSinceQuantile >= 64)
        {
            _hopsSinceQuantile = 0;
            UpdateQuantiles();
        }

        var mouthTarget = _envelopeP90 > 0.02 ? Clamp01(voiceAmplitude / _envelopeP90) : 0.0;
        var attack = 1.0 - Math.Exp(-HopSeconds / 0.015); // ~15ms 起音
        var release = 1.0 - Math.Exp(-HopSeconds / 0.090); // ~90ms 释放
        _mouthOpen += (mouthTarget - _mouthOpen) * (_mouthOpen < mouthTarget ? attack : release);
        var formTarget = Math.Clamp((_centroidHz - 1850.0) / 1550.0, -1.0, 1.0);
        _mouthForm += (formTarget - _mouthForm) * 0.15;

        // ---- 11) 人声分数 / 分类（真实混音版：中心门控的混音证据 + 噪声门控 + 清晰人声兜底） ----
        _vocalRawScore = ComputeVocalRawScoreEx(
            _centerVoiceRatio,
            _formantScore,
            _mouthOpen,
            _voiceBandEnergyRatio,
            _clarity,
            _pitchStability,
            _flatness);

        PushScoreSample(_vocalRawScore);
        var adaptive = _scoreHighQuantile - _scoreLowQuantile > 0.08
            ? Clamp01((_vocalRawScore - _scoreLowQuantile) / (_scoreHighQuantile - _scoreLowQuantile))
            : 0.5;
        var sensitivity = Math.Clamp(Sensitivity, 0.3, 3.0);
        _vocalFinalScore = Clamp01(_vocalRawScore * (0.85 + (0.15 * adaptive)) * sensitivity);
        _vocalSmoothedScore += (_vocalFinalScore - _vocalSmoothedScore) * 0.1;

        OnHopProcessed();
    }

    /// <summary>分类（每个 hop 都调一次，静音 hop 也推进计时，稀疏点击也能按墙钟时间下结论）。</summary>
    private void UpdateClassification()
    {
        var playingNow = _lastTimestampMs - _lastSoundTimestampMs <= PlayingHoldSeconds * 1000.0;
        if (!playingNow)
        {
            _kind = MusicContentKind.Silent;
            _confidence = 0.0;
            _vocalRunSeconds = 0.0;
            _instrumentalRunSeconds = 0.0;
            return;
        }

        // 稀疏内容（纯点击）有效分析时间很短，但播放墙钟时间够长也可以下结论 —— 否则永远 Unknown
        if (_activeAnalyzedSeconds < MinDecisionSeconds && _observedPlayingSeconds < MinDecisionSeconds)
        {
            _kind = MusicContentKind.Unknown;
            _confidence = 0.2;
            return;
        }

        if (_vocalSmoothedScore >= VocalHighThreshold)
        {
            _vocalRunSeconds += HopSeconds;
            _instrumentalRunSeconds = Math.Max(0, _instrumentalRunSeconds - (HopSeconds * 2));
        }
        else if (_vocalSmoothedScore <= VocalLowThreshold)
        {
            _instrumentalRunSeconds += HopSeconds;
            // 人声有乐句间隙：Vocal 计时只慢速回退（0.5×），不要求连续满 1.2 秒，短停顿不会把进度清零
            _vocalRunSeconds = Math.Max(0, _vocalRunSeconds - (HopSeconds * 0.5));
        }
        else
        {
            // 中间地带：两个计时都冻结（保持上一结论，也避免乐句过渡把进度回退掉）
        }

        if (_vocalRunSeconds >= VocalHoldSeconds)
        {
            _kind = MusicContentKind.VocalSong;
        }
        else if (_instrumentalRunSeconds >= InstrumentalHoldSeconds)
        {
            _kind = MusicContentKind.Instrumental;
        }

        _confidence = Clamp01(Math.Abs(_vocalSmoothedScore - 0.5) * 2.0);
    }

    private void AdvanceBeatPhase(double onsetStrength)
    {
        // 目标速率：可靠 BPM → 拍频；估不出（无鼓点 / 纯人声 / 环境音乐）→ 0.5Hz 舒缓摆动。
        // 速率是平滑换挡的，所以「回退 ↔ 真实节拍」切换时相位连续、不会跳变。
        var targetRate = _beatReliable && _bpm > 0 ? _bpm / 60.0 : FallbackSwayHz;
        _beatRateHz += (targetRate - _beatRateHz) * BeatRateSmoothing;
        _beatPhase += _beatRateHz * HopSeconds;
        if (_beatPhase >= 1.0) _beatPhase -= Math.Floor(_beatPhase);
        if (_beatPhase < 0) _beatPhase += 1.0;

        // 只有可靠节拍才用 onset 做 PLL 校正（否则纯人声 4Hz 音节会被错当鼓点拉相位）
        if (!_beatReliable || onsetStrength <= 0.35) return;

        // PLL：把相位往最近的拍子拉（强 onset 时多拉一点）
        var error = _beatPhase < 0.5 ? _beatPhase : _beatPhase - 1.0;
        var pull = 0.25 * Clamp01(onsetStrength);
        _beatPhase -= error * pull;
        if (_beatPhase < 0) _beatPhase += 1.0;
        if (_beatPhase >= 1.0) _beatPhase -= 1.0;
    }

    /// <summary>更新发布用的 BeatStrength：可靠节拍用 onset 起伏，否则用响度保底的舒缓值。</summary>
    private void UpdateBeatOutput()
    {
        var fallback = FallbackBeatStrength + (0.3 * Clamp01((_rms - 0.15) / 0.6));
        var target = _beatReliable ? _beatStrength : fallback;
        _displayBeatStrength += (target - _displayBeatStrength) * 0.2;
    }

    private void EstimateBpm()
    {
        var available = Math.Min(_onsetCount, OnsetHistorySize);
        if (available < 128) return;

        var start = (_onsetWrite - available + OnsetHistorySize) % OnsetHistorySize;
        for (var i = 0; i < available; i++) _onsetTemp[i] = _onsetHistory[(start + i) % OnsetHistorySize];

        var bpm = EstimateBpmFromOnsetEnvelope(_onsetTemp.AsSpan(0, available), 1.0 / HopSeconds, out var peak,
            60.0, 180.0);
        _onsetPeakCorrelation = peak;

        // 峰值相关度不够 → 认为「没有明显鼓点」，BPM 慢慢淡出到 0，运行时改走舒缓摆动回退
        var reliable = bpm > 0 && peak >= 0.3;
        _beatReliable = reliable;
        if (!reliable)
        {
            _bpm *= 0.85;
            if (_bpm < 20) _bpm = 0;
            return;
        }

        _bpm = _bpm <= 0 ? bpm : (_bpm * 0.7) + (bpm * 0.3);
    }

    private void UpdateModulations(double voiceAmplitude)
    {
        _ = voiceAmplitude; // 包络已经由 PushModulationEnvelope 记录，这里只做调制检测

        // ---- 先算基频稳定度 / 「像人声」程度（音节、颤音、谐波比例都靠它门控） ----
        // 人声 / 旋律的音高在几十毫秒内基本稳定（允许 ±6% 的颤音），和弦 / 打击的 YIN 结果会来回跳，
        // 这样钢琴和弦就不会被误判成「音节调制 / 颤音很强」。
        // ---- 用最近 ~2 秒的基频历史算「稳定度」：人声 / 旋律音高基本稳定，和弦 / 打击会来回跳 ----
        var pitchCount = Math.Min(_pitchCount, 64);
        var pitchStabilityTarget = 0.0;
        var vibratoTarget = 0.0;
        if (pitchCount >= 8)
        {
            var start = (_pitchWrite - pitchCount + PitchHistorySize) % PitchHistorySize;
            var validCount = 0;
            double mean = 0.0;
            for (var i = 0; i < pitchCount; i++)
            {
                var value = _pitchHistory[(start + i) % PitchHistorySize];
                if (value <= 0) continue;
                _pitchWindowTemp[validCount++] = value;
                mean += value;
            }

            if (validCount >= 4)
            {
                mean /= validCount;
                var stableCount = 0;
                for (var i = 0; i < validCount; i++)
                {
                    if (Math.Abs(_pitchWindowTemp[i] - mean) <= mean * 0.06) stableCount++;
                }

                pitchStabilityTarget = Clamp01(((stableCount / (double)validCount) - 0.45) / 0.45);

                // 颤音：最近最多 16 个有效基频点（≈0.5s）的 4~7Hz 相对调制深度
                var recentCount = Math.Min(validCount, 16);
                var recentStart = validCount - recentCount;
                double recentMean = 0.0;
                for (var i = recentStart; i < validCount; i++) recentMean += _pitchWindowTemp[i];
                recentMean /= recentCount;
                if (recentCount >= 6 && recentMean > 1e-6)
                {
                    for (var i = 0; i < recentCount; i++)
                    {
                        _modulationTemp[i] = (_pitchWindowTemp[recentStart + i] - recentMean) / recentMean;
                    }

                    var pitchRate = 0.5 / HopSeconds; // YIN 每 2 个 hop 算一次 → 31.25Hz
                    var amplitude = 0.0;
                    for (var frequency = 4.0; frequency <= 7.0; frequency += 1.0)
                    {
                        amplitude = Math.Max(amplitude, GoertzelAmplitude(_modulationTemp.AsSpan(0, recentCount), pitchRate, frequency));
                    }

                    // 相对调制深度（例如 ±2% 颤音 → 0.02）× 20 → 0.4
                    vibratoTarget = Clamp01(amplitude * 20.0);
                }
            }
        }

        _pitchStability += (pitchStabilityTarget - _pitchStability) * 0.25;
        // 「像人声」门控：真实混音下 YIN 整段找不到清晰基频，主要看 mid 人声带的周期显著度；
        // 合成单声道素材（YIN 清晰且稳定）也保留。钢琴和弦 / 打击两者都低，不会误开音节特征。
        var voicingTarget = _periodicitySalience >= PeriodicityVoicingThreshold
                            || (_pitchValid && _clarity > 0.3 && _pitchStability > 0.5)
            ? 1.0
            : 0.0;
        _voicedStrength += (voicingTarget - _voicedStrength) * 0.25;
        var voicing = 0.1 + (0.9 * _voicedStrength);

        // ---- 音节调制：去掉慢包络后，人声带包络在 3~7Hz 的起伏强度（Goertzel 带能量） ----
        // 用「归一化包络」而不是原始响度：钢琴 / kick 的音符衰减是慢变化，会被慢包络除掉；
        // 打击类的瞬态是宽带冲击，只在很短时间内有能量，除以 0.5s 窗口后也明显低于人声念字。
        var envelopeCount = Math.Min(_modulationCount, _modulationEnvelope.Length);
        if (envelopeCount >= 16)
        {
            var start = (_modulationWrite - envelopeCount + _modulationEnvelope.Length) % _modulationEnvelope.Length;
            for (var i = 0; i < envelopeCount; i++)
            {
                _modulationTemp[i] = _modulationEnvelope[(start + i) % _modulationEnvelope.Length] - 1.0;
            }

            var envelopeRate = 1.0 / HopSeconds; // 62.5Hz
            var amplitude = 0.0;
            for (var frequency = 3.0; frequency <= 7.0; frequency += 1.0)
            {
                amplitude = Math.Max(amplitude, GoertzelAmplitude(_modulationTemp.AsSpan(0, envelopeCount), envelopeRate, frequency));
            }

            var target = Clamp01(amplitude * 1.6) * voicing;
            _syllabicModulation += (target - _syllabicModulation) * 0.25;
        }
        else
        {
            _syllabicModulation *= 0.95;
        }

        _vibratoDepth += ((vibratoTarget * voicing) - _vibratoDepth) * 0.25;
    }

    /// <summary>记录「归一化人声包络」（人声带包络 / 慢包络），供 2~6Hz 调制检测。</summary>
    private void PushModulationEnvelope(double voiceAmplitude)
    {
        _voiceSlowEnvelope += (voiceAmplitude - _voiceSlowEnvelope) * 0.04; // ~0.4s 时间常数
        var normalized = _voiceSlowEnvelope > 1e-6 ? voiceAmplitude / _voiceSlowEnvelope : 1.0;
        _modulationEnvelope[_modulationWrite] = Math.Clamp(normalized, 0.0, 4.0);
        _modulationWrite = (_modulationWrite + 1) % _modulationEnvelope.Length;
        if (_modulationCount < _modulationEnvelope.Length) _modulationCount++;
    }

    /// <summary>
    /// 真实混音人声特征 1：中心声道 200~3500Hz 的带限自相关「周期显著度」。
    /// 做法：mid 功率谱频域带通掩码 → 逆 FFT 得自相关 → 在 80~1000Hz 对应 lag 上取峰值相对均值的显著度。
    /// 真实多声部混音里 YIN 往往找不到清晰基频，但主唱所在的 mid 声道在带内仍有周期结构。
    /// </summary>
    private void UpdateMidVoiceSalience()
    {
        Array.Clear(_fftReal);
        Array.Clear(_fftImag);
        double bandEnergy = 0.0;
        double totalEnergy = 0.0;
        double vocalTotalEnergy = 0.0;
        double highBandEnergy = 0.0;
        for (var bin = 1; bin < BinCount; bin++)
        {
            var magnitude = _magnitudeMid[bin];
            var power = magnitude * magnitude;
            totalEnergy += power;
            if (bin >= HighBandStartBin) highBandEnergy += power;
            if (bin < VocalBandStartBin)
            {
                continue;
            }

            vocalTotalEnergy += power;
            if (bin > VocalBandEndBin) continue;
            bandEnergy += power;
            _fftReal[bin] = power;
            _fftReal[WindowSize - bin] = power;
        }

        var ratio = totalEnergy > 1e-12 ? bandEnergy / totalEnergy : 0.0;
        _voiceBandEnergyRatio += (ratio - _voiceBandEnergyRatio) * 0.2;

        // 顶带特征（4~8kHz）：人声齿音 / 辅音气声会让 mid 的这一带更「噪声化」
        var highRatio = vocalTotalEnergy > 1e-12 ? highBandEnergy / vocalTotalEnergy : 0.0;
        _midHighBandRatio += (highRatio - _midHighBandRatio) * 0.2;
        _midHighBandFlatness += (ComputeSpectralFlatness(_magnitudeMid, HighBandStartBin, BinCount - 2) - _midHighBandFlatness) * 0.2;

        FastFft.InverseTransform(_fftReal, _fftImag);
        var self = _fftReal[0];
        if (self <= 1e-12)
        {
            _periodicitySalience *= 0.9;
            _harmonicSalience *= 0.9;
            return;
        }

        var peak = 0.0;
        var peakLag = 0;
        var sum = 0.0;
        var count = 0;
        for (var lag = PeriodicityMinLag; lag <= PeriodicityMaxLag; lag++)
        {
            var value = _fftReal[lag] / self;
            sum += value;
            count++;
            if (value > peak)
            {
                peak = value;
                peakLag = lag;
            }
        }

        var mean = count > 0 ? sum / count : 0.0;
        var salience = Clamp01((peak - mean) / (1.0 - mean + 1e-9));
        _periodicityLag = peakLag;
        _periodicitySalience += (salience - _periodicitySalience) * 0.25;
        _harmonicSalience += (ComputeHarmonicCombContrast(_magnitudeMid, VocalBandStartBin, VocalBandEndBin) - _harmonicSalience) * 0.2;
    }

    /// <summary>
    /// 真实混音人声特征 2：中心声道人声带的谐波梳对比度（0~1）。
    /// 在 80~500Hz 扫基频候选，比较「整数倍谐波位置」与「半整数倍之间位置」的能量，取最好的候选。
    /// 人声 / 单音乐器高，宽带噪声 / 打击低。
    /// </summary>
    public static double ComputeHarmonicCombContrast(ReadOnlySpan<double> magnitudes, int startBin, int endBin)
    {
        if (startBin < 1) startBin = 1;
        if (endBin >= magnitudes.Length) endBin = magnitudes.Length - 1;
        var best = 0.0;
        for (var f0 = 80.0; f0 <= 500.0; f0 += 5.0)
        {
            double harmonic = 0.0;
            double between = 0.0;
            var count = 0;
            for (var k = 1; k <= 12; k++)
            {
                var frequency = f0 * k;
                if (frequency >= (endBin - 1) * BinHz) break;
                var center = (int)Math.Round(frequency / BinHz);
                if (center <= startBin || center >= endBin) continue;
                var centerValue = Math.Max(magnitudes[center],
                    Math.Max(magnitudes[center - 1], magnitudes[center + 1]));

                var left = (int)Math.Round((k - 0.5) * f0 / BinHz);
                var right = (int)Math.Round((k + 0.5) * f0 / BinHz);
                var leftValue = left >= startBin && left <= endBin ? magnitudes[left] : 0.0;
                var rightValue = right >= startBin && right <= endBin ? magnitudes[right] : 0.0;
                harmonic += centerValue;
                between += 0.5 * (leftValue + rightValue);
                count++;
            }

            if (count < 3) continue;
            var contrast = (harmonic - between) / (harmonic + between + 1e-9);
            if (contrast > best) best = contrast;
        }

        return Clamp01(best);
    }

    /// <summary>
    /// 真实混音版 VocalScore（0~1）。
    /// <para>真实歌曲是多声部混音：YIN 清晰度 / HPS / 周期显著度在钢琴、弦乐等 tonal 乐器上同样很高，
    /// 单靠它们无法区分「主唱」与「乐器」。这里组合两条互补证据：</para>
    /// <list type="number">
    /// <item><b>混音证据</b>：中心占比（主唱居中、伴奏更宽）× 共振峰峰显著度 × 中心人声带包络动态（吐字起伏）
    /// × 频带互补项；再乘「中心占比门控」把侧声道很宽 / 居中性差的乐器混音压下去；</item>
    /// <item><b>清晰人声证据</b>：YIN 清晰度 × 基频稳定度（干净的主唱 / 合成人声，多声部混音里通常很低，
    /// 但能兜住清唱、类人声信号）。</item>
    /// </list>
    /// </summary>
    public static double ComputeVocalRawScoreEx(double centerVoiceRatio, double formantScore, double mouthOpen,
        double voiceBandEnergyRatio, double clarity, double pitchStability, double flatness)
    {
        var center = Clamp01(centerVoiceRatio);
        var mixScore = (0.30 * center)
                       + (0.26 * Clamp01(formantScore))
                       + (0.26 * Clamp01(mouthOpen))
                       + (0.18 * (1.0 - Clamp01(voiceBandEnergyRatio)));
        // 中心占比门控：主唱在中间（center 高）才按混音证据算；侧声道很宽（center < 0.55）直接归零
        var centerGate = Clamp01((center - 0.55) / 0.25);
        // 噪声门控：谱很平坦（宽带噪声 / 镲片铺满）不可能是主唱；真实歌曲人声带谱不平坦
        var noiseGate = Clamp01((0.55 - Clamp01(flatness)) / 0.35);
        // 清晰人声兜底：YIN 清晰且基频稳定（清唱 / 干净主唱 / 类人声合成）额外加分
        var cleanVocal = 0.25 * Clamp01(clarity) * Clamp01(pitchStability);
        return Clamp01((mixScore * centerGate * noiseGate) + cleanVocal);
    }

    /// <summary>每处理完一个 hop 调一次（有声 / 静音都算）：驱动 BPM 估计，和当前是否有声音无关。</summary>
    private void OnHopProcessed()
    {
        if (_lastTimestampMs - _lastSoundTimestampMs <= PlayingHoldSeconds * 1000.0)
        {
            _observedPlayingSeconds += HopSeconds;
        }

        // 分类每个 hop 都推进（静音 / 稀疏点击的墙钟时间也要计入）
        UpdateClassification();
        UpdateBeatOutput();

        if (++_hopsSinceBpm < 125) return;
        _hopsSinceBpm = 0;
        EstimateBpm();
    }

    private void UpdateRmsSmoothing(double blockRms)
    {
        var decibels = 20.0 * Math.Log10(blockRms + 1e-6);
        var normalized = Clamp01((decibels + 60.0) / 60.0);
        _rms += (normalized - _rms) * 0.15;
    }

    private void UpdateQuantiles()
    {
        // 分数的运行分位数（最近 30 秒）
        var scoreCount = _scoreCount;
        if (scoreCount >= 64)
        {
            Array.Copy(_scoreHistory, _scoreSortTemp, scoreCount);
            Array.Sort(_scoreSortTemp, 0, scoreCount);
            _scoreLowQuantile = _scoreSortTemp[(int)(scoreCount * 0.15)];
            _scoreHighQuantile = _scoreSortTemp[(int)(scoreCount * 0.85)];
        }

        // 包络 p90（最近 10 秒，用于 MouthOpen 归一化）
        var envelopeCount = _envelopeCount;
        if (envelopeCount >= 32)
        {
            Array.Copy(_envelopeHistory, _envelopeSortTemp, envelopeCount);
            Array.Sort(_envelopeSortTemp, 0, envelopeCount);
            _envelopeP90 = _envelopeSortTemp[(int)(envelopeCount * 0.9)];
        }
    }

    // ---------- 历史 ring ----------

    private void PushOnsetSample(double value)
    {
        _onsetHistory[_onsetWrite] = value;
        _onsetWrite = (_onsetWrite + 1) % OnsetHistorySize;
        if (_onsetCount < OnsetHistorySize) _onsetCount++;
    }

    private void PushPitchSample(double value)
    {
        _pitchHistory[_pitchWrite] = value;
        _pitchWrite = (_pitchWrite + 1) % PitchHistorySize;
        if (_pitchCount < PitchHistorySize) _pitchCount++;
    }

    private void PushScoreSample(double value)
    {
        _scoreHistory[_scoreWrite] = value;
        _scoreWrite = (_scoreWrite + 1) % ScoreHistorySize;
        if (_scoreCount < ScoreHistorySize) _scoreCount++;
    }

    private void PushEnvelopeSample(double value)
    {
        _envelopeHistory[_envelopeWrite] = value;
        _envelopeWrite = (_envelopeWrite + 1) % EnvelopeHistorySize;
        if (_envelopeCount < EnvelopeHistorySize) _envelopeCount++;
    }

    // ---------- 信号处理静态工具（自检对拍用） ----------

    /// <summary>
    /// YIN 基频估计：返回基频 Hz（检测失败返回 0），<paramref name="clarity"/> 为 1 - 非周期度（0~1）。
    /// 适用 lag 16~178（16k 采样下 ≈ 90~1000Hz）。
    /// </summary>
    public static double EstimatePitchYin(ReadOnlySpan<double> samples, int sampleRate, int minLag, int maxLag, out double clarity)
    {
        clarity = 0;
        if (minLag < 2 || maxLag <= minLag) return 0;

        var window = samples.Length - maxLag;
        if (window < 64) window = samples.Length - maxLag;
        if (window < 64) return 0;

        // 差分函数 d(τ)：从 lag=1 开始算（CMNDF 的累计均值分母要用到 1..minLag-1，
        // 只从 minLag 算会让归一化曲线整体偏高、阈值穿越点提前 —— l2d-verify 抓到的 bug）
        Span<double> difference = maxLag <= 256 ? stackalloc double[maxLag + 1] : new double[maxLag + 1];
        for (var lag = 1; lag <= maxLag; lag++)
        {
            double sum = 0.0;
            for (var i = 0; i < window; i++)
            {
                var diff = samples[i] - samples[i + lag];
                sum += diff * diff;
            }

            difference[lag] = sum;
        }

        // 累积均值归一化差分 d'(τ)：先把整条曲线算完（后面找谷底要读邻居，不能提前 break）
        Span<double> normalized = maxLag <= 256 ? stackalloc double[maxLag + 1] : new double[maxLag + 1];
        double running = 0.0;
        normalized[0] = 1.0;
        for (var lag = 1; lag <= maxLag; lag++)
        {
            running += difference[lag];
            normalized[lag] = running <= 1e-12 ? 1.0 : difference[lag] * lag / running;
        }

        // 找第一个低于阈值的 lag（进入谷区），再继续走到谷底
        const double threshold = 0.15;
        var bestLag = -1;
        var bestValue = double.MaxValue;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            if (normalized[lag] >= threshold) continue;
            bestLag = lag;
            bestValue = normalized[lag];
            break;
        }

        if (bestLag < 0)
        {
            // 没有低于阈值的点：退回全局最小（弱周期信号也能给个估计）
            for (var lag = minLag; lag <= maxLag; lag++)
            {
                if (normalized[lag] >= bestValue) continue;
                bestValue = normalized[lag];
                bestLag = lag;
            }
        }

        if (bestLag < minLag) return 0;

        // 阈值穿越点往往在谷的肩部：继续往后直到局部最小（l2d-verify 抓到的 bug —— 220Hz 被估成 242Hz）
        while (bestLag + 1 <= maxLag && normalized[bestLag + 1] < normalized[bestLag])
        {
            bestLag++;
            bestValue = normalized[bestLag];
        }

        // 八度校正：谐波丰富的信号（人声 / 钢琴）在半周期处也可能出现深谷，YIN 会锁到低一个八度的频率。
        // 如果 bestLag/2 附近有「不差于当前谷」的候选谷，说明应该取更短的滞后（更高的基频）。
        for (var octave = 0; octave < 2; octave++)
        {
            var halfLag = bestLag / 2;
            if (halfLag < minLag) break;
            var searchStart = Math.Max(minLag, halfLag - 4);
            var searchEnd = Math.Min(maxLag, halfLag + 4);
            var candidateLag = -1;
            var candidateValue = double.MaxValue;
            for (var lag = searchStart; lag <= searchEnd; lag++)
            {
                if (normalized[lag] >= candidateValue) continue;
                candidateValue = normalized[lag];
                candidateLag = lag;
            }

            // 半周期谷只要「够深」就接受：人声带颤音 / 包络时真实基频的半周期谷可能不如理想正弦那么深
            // （颤音会让一个周期内的波形漂移，CMNDF 在基频处反而变浅），但太浅的谷（>0.2）不可信。
            var candidateLimit = Math.Max(normalized[bestLag] * 2.0, 0.2);
            if (candidateLag <= 0 || candidateValue >= candidateLimit) break;
            bestLag = candidateLag;
            bestValue = candidateValue;
            // 继续往谷底走两步（候选搜索窗口里可能只到肩部）
            while (bestLag + 1 <= maxLag && normalized[bestLag + 1] < normalized[bestLag])
            {
                bestLag++;
                bestValue = normalized[bestLag];
            }
        }

        // 抛物线插值细化（在谷底用左右邻居）
        var refinedLag = (double)bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            var left = normalized[bestLag - 1];
            var center = normalized[bestLag];
            var right = normalized[bestLag + 1];
            var denominator = left - (2.0 * center) + right;
            if (Math.Abs(denominator) > 1e-12) refinedLag += 0.5 * (left - right) / denominator;
        }

        if (refinedLag <= 0) return 0;
        clarity = Clamp01(1.0 - bestValue);
        var frequency = sampleRate / refinedLag;

        // 只接受人声 / 乐器基频范围（90~1000Hz）；超出就置 Invalid（不要输出 1000 这种假值）
        return frequency < 90 || frequency >= 1000 ? 0 : frequency;
    }

    /// <summary>
    /// Goertzel 单频幅度估计：返回该频率正弦分量的振幅（例如一段纯正弦 → 振幅）。
    /// 用来做音节调制（2~6Hz）/ 颤音（4~7Hz）的带能量检测，比差分 RMS 更抗宽带瞬态。
    /// </summary>
    public static double GoertzelAmplitude(ReadOnlySpan<double> samples, double sampleRate, double frequency)
    {
        var count = samples.Length;
        if (count < 4 || sampleRate <= 0 || frequency <= 0 || frequency >= sampleRate * 0.5) return 0;

        var omega = 2.0 * Math.PI * frequency / sampleRate;
        var coefficient = 2.0 * Math.Cos(omega);
        double s1 = 0.0;
        double s2 = 0.0;
        for (var i = 0; i < count; i++)
        {
            var s0 = samples[i] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
        return Math.Sqrt(Math.Max(0.0, power)) * 2.0 / count;
    }

    /// <summary>向后兼容重载（参数顺序为 minBpm/maxBpm 在前）：自检代码两种写法都能编。</summary>
    public static double EstimateBpmFromOnsetEnvelope(ReadOnlySpan<double> onsetEnvelope, double hopRateHz,
        double minBpm, double maxBpm, out double peakCorrelation)
        => EstimateBpmFromOnsetEnvelope(onsetEnvelope, hopRateHz, out peakCorrelation, minBpm, maxBpm);

    /// <summary>谱平坦度（0~1；平坦 = 噪声，越小越有音调 / 共振峰）。</summary>
    public static double ComputeSpectralFlatness(ReadOnlySpan<double> magnitudes, int startBin, int endBin)
    {
        if (startBin < 0) startBin = 0;
        if (endBin >= magnitudes.Length) endBin = magnitudes.Length - 1;
        if (endBin <= startBin) return 1.0;

        double logSum = 0.0;
        double sum = 0.0;
        var count = 0;
        for (var bin = startBin; bin <= endBin; bin++)
        {
            var power = magnitudes[bin] * magnitudes[bin] + 1e-12;
            logSum += Math.Log(power);
            sum += power;
            count++;
        }

        if (count == 0 || sum <= 0) return 1.0;
        var geometric = Math.Exp(logSum / count);
        var arithmetic = sum / count;
        return Clamp01(geometric / arithmetic);
    }

    /// <summary>谱质心（Hz）。</summary>
    public static double ComputeSpectralCentroid(ReadOnlySpan<double> magnitudes, int startBin, int endBin, double binHz)
    {
        if (startBin < 0) startBin = 0;
        if (endBin >= magnitudes.Length) endBin = magnitudes.Length - 1;
        if (endBin <= startBin) return 0;

        double weighted = 0.0;
        double sum = 0.0;
        for (var bin = startBin; bin <= endBin; bin++)
        {
            var power = magnitudes[bin] * magnitudes[bin];
            weighted += bin * binHz * power;
            sum += power;
        }

        return sum <= 1e-12 ? 0 : weighted / sum;
    }

    /// <summary>正向谱通量（只累计变亮的部分；越大 onset 越强）。</summary>
    public static double ComputePositiveSpectralFlux(ReadOnlySpan<double> current, ReadOnlySpan<double> previous, int startBin, int endBin)
    {
        if (startBin < 0) startBin = 0;
        var max = Math.Min(Math.Min(current.Length, previous.Length), endBin + 1);
        double flux = 0.0;
        for (var bin = startBin; bin < max; bin++)
        {
            var delta = current[bin] - previous[bin];
            if (delta > 0) flux += delta;
        }

        return flux;
    }

    /// <summary>
    /// 共振峰峰数打分：人声带频谱平滑后数局部峰（1~4 个），越多越像人声 → 0~1。
    /// </summary>
    public static double ComputeFormantScore(ReadOnlySpan<double> magnitudes, Span<double> smoothBuffer, int startBin, int endBin)
    {
        if (endBin >= magnitudes.Length) endBin = magnitudes.Length - 1;
        if (endBin - startBin < 8) return 0;

        // 15 点滑动平均（≈230Hz）把谐波梳齿抹平，保留共振峰的宽峰
        const int radius = 7;
        var peak = 0.0;
        for (var bin = startBin; bin <= endBin; bin++)
        {
            double sum = 0.0;
            var count = 0;
            for (var offset = -radius; offset <= radius; offset++)
            {
                var index = bin + offset;
                if (index < startBin || index > endBin) continue;
                sum += magnitudes[index];
                count++;
            }

            var value = count == 0 ? 0 : sum / count;
            smoothBuffer[bin] = value;
            if (value > peak) peak = value;
        }

        if (peak <= 1e-9) return 0;

        var peaks = 0;
        for (var bin = startBin + 2; bin < endBin - 1; bin++)
        {
            var value = smoothBuffer[bin];
            if (value < peak * 0.08) continue;
            if (value <= smoothBuffer[bin - 1] || value < smoothBuffer[bin + 1]) continue;
            // 和 ±14 bin（≈220Hz）比要有明显突出，避免数到梳齿
            var left = smoothBuffer[Math.Max(startBin, bin - 14)];
            var right = smoothBuffer[Math.Min(endBin, bin + 14)];
            if (value > left * 1.15 && value > right * 1.15) peaks++;
        }

        return Clamp01((peaks - 1) / 3.0);
    }

    /// <summary>
    /// 人声原始融合分数（0~1，未做灵敏度 / 自适应）。
    /// 权重见 <see cref="WeightClarity"/> 等常量：YIN 清晰度、谐波比例、谱平坦度（低=人声）、
    /// 共振峰峰数、中心声道占比、音节调制、颤音深度。
    /// </summary>
    public static double ComputeVocalRawScore(double clarity, double harmonicRatio, double tonalness,
        double formantScore, double centerVoiceRatio, double syllabicModulation, double vibratoDepth)
    {
        var score = (Clamp01(clarity) * WeightClarity)
                    + (Clamp01(harmonicRatio) * WeightHarmonic)
                    + (Clamp01(tonalness) * WeightTonalness)
                    + (Clamp01(formantScore) * WeightFormant)
                    + (Clamp01(centerVoiceRatio) * WeightCenter)
                    + (Clamp01(syllabicModulation) * WeightSyllable)
                    + (Clamp01(vibratoDepth) * WeightVibrato);
        return Clamp01(score);
    }

    /// <summary>
    /// 用 onset 包络自相关估 BPM（60~180）。返回 0 = 还没估出来；<paramref name="peakCorrelation"/> 是峰值相关度。
    /// </summary>
    public static double EstimateBpmFromOnsetEnvelope(ReadOnlySpan<double> onsetEnvelope, double hopRateHz,
        out double peakCorrelation, double minBpm = 60.0, double maxBpm = 180.0)
    {
        peakCorrelation = 0;
        if (onsetEnvelope.Length < 32 || hopRateHz <= 0) return 0;

        // 去均值
        double mean = 0.0;
        for (var i = 0; i < onsetEnvelope.Length; i++) mean += onsetEnvelope[i];
        mean /= onsetEnvelope.Length;
        if (mean <= 1e-9) return 0;

        var minLag = Math.Max(2, (int)Math.Ceiling(hopRateHz * 60.0 / maxBpm));
        var maxLag = Math.Min(onsetEnvelope.Length / 2, (int)Math.Floor(hopRateHz * 60.0 / minBpm));
        if (maxLag <= minLag) return 0;

        double energy = 0.0;
        for (var i = 0; i < onsetEnvelope.Length; i++)
        {
            var centered = onsetEnvelope[i] - mean;
            energy += centered * centered;
        }

        if (energy <= 1e-12) return 0;

        var bestLag = -1;
        var bestCorrelation = double.MinValue;
        double previousCorrelation = 0;
        double bestPrevious = 0;
        double bestNext = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double correlation = 0.0;
            for (var i = lag; i < onsetEnvelope.Length; i++)
            {
                correlation += (onsetEnvelope[i] - mean) * (onsetEnvelope[i - lag] - mean);
            }

            correlation /= energy;
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestLag = lag;
            }

            if (lag == bestLag - 1) previousCorrelation = correlation;
            if (lag == bestLag + 1) bestNext = correlation;
            _ = previousCorrelation;
            _ = bestPrevious;
        }

        if (bestLag < 0 || bestCorrelation <= 0) return 0;
        peakCorrelation = Clamp01(bestCorrelation);

        // 抛物线插值细化周期
        var refinedLag = (double)bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            // 重新算一遍相邻 lag（缓存逻辑上面被简化掉了，这里直接补算）
            var left = CorrelateAt(onsetEnvelope, mean, energy, bestLag - 1);
            var center = bestCorrelation;
            var right = CorrelateAt(onsetEnvelope, mean, energy, bestLag + 1);
            var denominator = left - (2.0 * center) + right;
            if (Math.Abs(denominator) > 1e-12) refinedLag += 0.5 * (left - right) / denominator;
        }

        if (refinedLag <= 0) return 0;
        var bpm = 60.0 * hopRateHz / refinedLag;
        return bpm is < 55 or > 185 ? 0 : bpm;
    }

    private static double CorrelateAt(ReadOnlySpan<double> signal, double mean, double energy, int lag)
    {
        if (lag <= 0 || lag >= signal.Length) return 0;
        double correlation = 0.0;
        for (var i = lag; i < signal.Length; i++)
        {
            correlation += (signal[i] - mean) * (signal[i - lag] - mean);
        }

        return energy <= 1e-12 ? 0 : correlation / energy;
    }

    private static double ComputeHarmonicRatio(ReadOnlySpan<double> magnitudes, double pitchHz,
        int startBin, int endBin, double amplitudeScale)
    {
        if (pitchHz < 50 || pitchHz > 1100) return 0;
        double harmonicEnergy = 0.0;
        double totalEnergy = 0.0;
        for (var bin = startBin; bin <= endBin; bin++)
        {
            var scaled = magnitudes[bin] * amplitudeScale;
            totalEnergy += scaled * scaled;
        }

        for (var harmonic = 2; harmonic <= 8; harmonic++)
        {
            var centerBin = (int)Math.Round(harmonic * pitchHz / BinHz);
            for (var bin = centerBin - 2; bin <= centerBin + 2; bin++)
            {
                if (bin < startBin || bin > endBin) continue;
                var scaled = magnitudes[bin] * amplitudeScale;
                harmonicEnergy += scaled * scaled;
            }
        }

        return totalEnergy <= 1e-12 ? 0 : Clamp01(harmonicEnergy / totalEnergy);
    }

    private static double SumMagnitude(ReadOnlySpan<double> magnitudes, int startBin, int endBin)
    {
        if (startBin < 0) startBin = 0;
        if (endBin >= magnitudes.Length) endBin = magnitudes.Length - 1;
        double sum = 0.0;
        for (var bin = startBin; bin <= endBin; bin++) sum += magnitudes[bin];
        return sum;
    }

    private void ApplyHann(double[] buffer)
    {
        for (var i = 0; i < WindowSize; i++) buffer[i] *= _hannWindow[i];
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
