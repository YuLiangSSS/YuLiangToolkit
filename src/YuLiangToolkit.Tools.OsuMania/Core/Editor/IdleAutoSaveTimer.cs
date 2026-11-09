using System;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 「用户多久没动过了」的计时器（自动保存用）。
///
/// <para><b>为什么单独抽出来</b>：这段逻辑全是边界条件（操作要不要清零、播放时算不算、
/// 阈值到了之后什么时候再触发下一次），塞在 ViewModel 里就只能靠手点验证。
/// 抽成纯类后自检可以直接喂时间，把每种情形断言一遍。</para>
///
/// <para><b>语义</b>：</para>
/// <list type="bullet">
///   <item><see cref="NoteActivity"/> 把空闲时间清零（任何交互都该调它，包括鼠标移动）；</item>
///   <item><see cref="Advance"/> 推时间；空闲达到阈值时返回 true（调用方去保存），并重新计时；</item>
///   <item>播放中（<c>suppressed</c>）不触发保存：写盘可能卡一下，正在听节奏时很难受；
///         暂停后重新开始计空闲，这段时间不算「无操作」；</item>
///   <item>关掉自动保存（<see cref="Enabled"/> = false）时什么都不触发。</item>
/// </list>
/// </summary>
public sealed class IdleAutoSaveTimer
{
    /// <summary>空闲阈值（毫秒）。</summary>
    public double ThresholdMs { get; set; } = 10_000;

    /// <summary>关掉时永不触发。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>当前累计的空闲时间（毫秒）。调试 / 自检用。</summary>
    public double IdleMs { get; private set; }

    /// <summary>本次累计里是否刚播过（用来判断「暂停后要不要重新计时」）。</summary>
    private bool _pausedByPlayback;

    /// <summary>发生过一次交互：空闲计时归零。</summary>
    public void NoteActivity()
    {
        IdleMs = 0;
        _pausedByPlayback = false;
    }

    /// <summary>
    /// 推进一步。
    /// </summary>
    /// <param name="elapsedMs">这次推进了多少毫秒（调用方的定时器间隔）。</param>
    /// <param name="suppressed">是不是「不该自动保存」的时段（播放中）。</param>
    /// <returns>true = 该保存了（空闲已超过阈值，内部已重新计时）。</returns>
    public bool Advance(double elapsedMs, bool suppressed)
    {
        if (!Enabled || elapsedMs <= 0)
        {
            return false;
        }

        if (suppressed)
        {
            // 播放中：不计空闲（这段不算用户“没操作”），并记住暂停后要重新开始计
            _pausedByPlayback = true;
            IdleMs = 0;
            return false;
        }

        if (_pausedByPlayback)
        {
            // 刚从播放切到暂停：重新开始计空闲
            _pausedByPlayback = false;
            IdleMs = 0;
            return false;
        }

        IdleMs += elapsedMs;
        if (IdleMs < Math.Max(0, ThresholdMs))
        {
            return false;
        }

        IdleMs = 0;
        return true;
    }
}
