using System.IO;
using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>
/// 按键音播放：默认播放内置合成「点击声」，也可以选一个自定义 WAV 文件。
///
/// <para><b>实现</b>：WinMM <c>PlaySound</c>（<c>SND_ASYNC</c> 异步播放，不阻塞输入回调，任意线程可调）。
/// 内置音是程序化生成的 16-bit PCM 单声道 WAV（约 35ms 的短促 click），放在内存里用
/// <c>SND_MEMORY</c> 播放；样本按 <see cref="Volume"/> / 100 缩放，所以不需要落临时文件。</para>
///
/// <para><b>自定义 WAV</b>：<see cref="SoundFilePath"/> 非空时播放该文件；文件不存在 / 播放失败会写
/// <see cref="LastError"/> 并退回内置点击声（不抛异常）。</para>
///
/// <para><b>生命周期</b>：内存 WAV 用 <see cref="GCHandle"/> 固定到 <see cref="Reload"/> / <see cref="Dispose"/>
/// （异步播放期间缓冲区必须有效）；<see cref="Dispose"/> 会先 <c>PlaySound(NULL)</c> 停止再释放。</para>
/// </summary>
public sealed class KeySoundPlayer : IDisposable
{
    // ---------------- WinMM 播放标志 ----------------

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_FILENAME = 0x00020000;

    // ---------------- 内置点击声参数 ----------------

    private const int SampleRate = 44100;
    private const int ClickDurationMs = 35;

    // ---------------- 状态 ----------------

    private readonly object _gate = new();

    private bool _disposed;

    /// <summary><see cref="SoundFilePath"/> 当前指向一个存在的文件（否则用内置音）。</summary>
    private bool _customReady;
    private string _resolvedPath = "";

    /// <summary>固定住内存 WAV（异步播放期间缓冲区必须有效；句柄本身也保住了数组引用）。</summary>
    private GCHandle _wavHandle;

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; }

    /// <summary>音量 0~100（内置合成音按它缩放；自定义 WAV 受系统音量控制）。</summary>
    public int Volume { get; set; } = 60;

    /// <summary>自定义 WAV 文件绝对路径；空 = 用内置合成点击声。</summary>
    public string SoundFilePath { get; set; } = "";

    /// <summary>最近一次播放失败原因。</summary>
    public string? LastError { get; private set; }

    /// <summary>构造时先合成一份内置点击声（配置里 <see cref="Volume"/> 变了要调 <see cref="Reload"/>）。</summary>
    public KeySoundPlayer()
    {
        RebuildBuiltIn();
    }

    /// <summary>播放一次（非阻塞；<see cref="Enabled"/> = false 时什么都不做；不抛异常）。</summary>
    public void Play()
    {
        if (_disposed || !Enabled) return;

        lock (_gate)
        {
            if (_disposed) return;

            try
            {
                if (_customReady && _resolvedPath.Length > 0)
                {
                    if (PlaySoundFile(_resolvedPath, IntPtr.Zero,
                            SND_FILENAME | SND_ASYNC | SND_NODEFAULT))
                        return;

                    // 文件损坏 / 格式不支持 / 被占用 → 退回内置音（下次 Reload 前不再重试）
                    var code = Marshal.GetLastWin32Error();
                    _customReady = false;
                    LastError = $"播放键音文件失败（Win32 错误 {code}），已退回内置点击声：{_resolvedPath}";
                }

                if (_wavHandle.IsAllocated)
                    PlaySoundMemory(_wavHandle.AddrOfPinnedObject(), IntPtr.Zero,
                        SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
            }
            catch (Exception ex)
            {
                LastError = "播放键音失败：" + ex.Message;
            }
        }
    }

    /// <summary>配置变化后调用：音量变 → 重新合成内置音；路径变 → 重新检查自定义 WAV 是否存在。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _resolvedPath = (SoundFilePath ?? "").Trim();
            if (_resolvedPath.Length == 0)
            {
                _customReady = false;
                LastError = null;
            }
            else if (File.Exists(_resolvedPath))
            {
                _customReady = true;
                LastError = null;
            }
            else
            {
                _customReady = false;
                LastError = $"键音文件不存在：{_resolvedPath}（已退回内置点击声）";
            }

            RebuildBuiltIn();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            try { PlaySoundMemory(IntPtr.Zero, IntPtr.Zero, 0); } // 停止当前播放
            catch { /* 退出流程忽略 */ }

            FreeBuiltIn();
        }
    }

    // ================================================================== 内置音

    /// <summary>重新合成内置点击声（先停掉当前播放，再换缓冲区）。调用方需持有 <see cref="_gate"/>。</summary>
    private void RebuildBuiltIn()
    {
        try { PlaySoundMemory(IntPtr.Zero, IntPtr.Zero, 0); }
        catch { /* 停止失败也继续重建 */ }

        FreeBuiltIn();

        // 句柄固定住了数组（数组引用由句柄保持，不需要再存字段）
        _wavHandle = GCHandle.Alloc(BuildClickWav(Volume), GCHandleType.Pinned);
    }

    private void FreeBuiltIn()
    {
        if (_wavHandle.IsAllocated) _wavHandle.Free();
    }

    /// <summary>
    /// 生成一小段 16-bit PCM 单声道 WAV：高频正弦 + 极短白噪声瞬态，指数衰减包络，
    /// 听感接近机械键盘 / 音游按键的「嗒」一声。振幅按 <paramref name="volume"/>（0~100）缩放。
    /// </summary>
    private static byte[] BuildClickWav(int volume)
    {
        var sampleCount = SampleRate * ClickDurationMs / 1000;
        var amplitude = Math.Clamp(volume, 0, 100) / 100.0 * 0.85;
        var pcm = new byte[sampleCount * 2];

        var noise = 0x2545F491u; // 固定种子：同样的音量生成同样的波形，方便排查
        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)SampleRate;
            var envelope = Math.Exp(-t * 95.0);

            // xorshift32 白噪声，只保留开头几毫秒作「咔」的瞬态
            noise ^= noise << 13;
            noise ^= noise >> 17;
            noise ^= noise << 5;
            var noiseSample = (noise / (double)uint.MaxValue * 2.0 - 1.0) * Math.Exp(-t * 900.0);

            var tone = Math.Sin(2 * Math.PI * 1850.0 * t) * 0.8
                       + Math.Sin(2 * Math.PI * 3700.0 * t) * 0.2;
            var sample = (tone * 0.7 + noiseSample * 0.5) * envelope * amplitude;
            var value = (short)(Math.Clamp(sample, -1.0, 1.0) * short.MaxValue);

            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return BuildWavContainer(pcm);
    }

    /// <summary>给 PCM 数据套 44 字节标准 WAV 头（RIFF / WAVE / fmt / data）。</summary>
    private static byte[] BuildWavContainer(byte[] pcm)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var byteRate = SampleRate * blockAlign;

        var wav = new byte[44 + pcm.Length];
        using var stream = new MemoryStream(wav);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);               // fmt 块大小
        writer.Write((short)1);         // PCM
        writer.Write(channels);
        writer.Write(SampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);

        return wav;
    }

    // ================================================================== Win32 声明

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySoundFile(string sound, IntPtr module, uint flags);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    private static extern bool PlaySoundMemory(IntPtr sound, IntPtr module, uint flags);
}
