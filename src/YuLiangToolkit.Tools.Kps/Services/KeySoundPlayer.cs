namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>
/// 按键音播放：默认播放内置合成「点击声」，也可以选一个自定义 WAV 文件。
///
/// <para>实现建议：WinMM <c>PlaySound</c>（<c>SND_ASYNC | SND_FILENAME</c> 异步播放，不阻塞输入回调）；
/// 内置音可以程序化生成一小段 PCM WAV 写到临时文件（音量按 <see cref="Volume"/> 缩放），
/// 或直接用 <c>System.Media.SoundPlayer</c>。不要引第三方包。</para>
///
/// <para>⚠ 这是给队友实现的契约骨架：骨架阶段 <see cref="Play"/> 什么都不做。</para>
/// </summary>
public sealed class KeySoundPlayer : IDisposable
{
    private bool _disposed;

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; }

    /// <summary>音量 0~100（内置合成音按它缩放；自定义 WAV 受系统音量控制）。</summary>
    public int Volume { get; set; } = 60;

    /// <summary>自定义 WAV 文件绝对路径；空 = 用内置合成点击声。</summary>
    public string SoundFilePath { get; set; } = "";

    /// <summary>最近一次播放失败原因。</summary>
    public string? LastError { get; private set; }

    /// <summary>播放一次（非阻塞；<see cref="Enabled"/> = false 时什么都不做）。</summary>
    public void Play()
    {
        // TODO(队友 kps-input)：实现内置合成点击声 + 自定义 WAV 播放。
    }

    /// <summary>配置变化后调用：重新加载自定义 WAV / 重新合成内置音。</summary>
    public void Reload()
    {
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
