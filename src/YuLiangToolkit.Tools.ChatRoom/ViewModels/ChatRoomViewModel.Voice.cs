using System.IO;
using System.Windows.Threading;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ChatRoom.Call;
using YuLiangToolkit.Tools.ChatRoom.Media;
using YuLiangToolkit.Tools.ChatRoom.Net;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>
/// 「按住说话发语音消息」那部分界面逻辑（独立 partial，主文件保持可读）：
/// 按下 → 开麦克风录音 → 松开 → 转 MP3 → 自动发送一条语音消息 → 删临时文件。
///
/// <para><b>为什么录音在 ViewModel 里而不是网络层</b>：录音 / 转码是纯本地设备操作，
/// 跟「房间里有没有人」无关；ViewModel 只把最后那个 MP3 路径交给会话层发送。
/// 麦克风设备跟通话设置共用（<c>Cfg.CallMicrophoneId</c>），WASAPI 共享模式允许多个客户端同时采集。</para>
///
/// <para><b>边界</b>：录音最长 2 分钟（<see cref="VoiceRecorder.MaxDurationSeconds"/>，到点自动发送）；
/// 少于 1 秒当作误触直接取消；MacOS 没有「松开鼠标」事件时靠 <c>LostMouseCapture</c> 取消（视图里处理）。
/// 录音中切走工具页不会自动停（和通话一致，页面被外壳缓存）；关工具 / 断开连接时会取消。</para>
/// </summary>
public sealed partial class ChatRoomViewModel
{
    private VoiceRecorder? _voiceRecorder;
    private DispatcherTimer? _voiceTimer;
    private DateTime _voiceStartedAt;
    private bool _voiceStarting;
    private bool _voiceStopping;
    private bool _voiceCancelRequested;
    private bool _isRecordingVoice;
    private int _voiceRecordingSeconds;
    private bool _voiceAutoTestStarted;

    /// <summary>正在录音（界面上按钮变红、显示秒数）。</summary>
    public bool IsRecordingVoice
    {
        get => _isRecordingVoice;
        private set
        {
            if (!Set(ref _isRecordingVoice, value)) return;
            OnPropertyChanged(nameof(VoiceButtonText));
            OnPropertyChanged(nameof(CanRecordVoice));
            OnPropertyChanged(nameof(VoiceRecordingHint));
        }
    }

    /// <summary>已录秒数（界面上的红点 + 秒数；最长 120）。</summary>
    public int VoiceRecordingSeconds
    {
        get => _voiceRecordingSeconds;
        private set
        {
            if (!Set(ref _voiceRecordingSeconds, value)) return;
            OnPropertyChanged(nameof(VoiceButtonText));
        }
    }

    /// <summary>录音按钮的文字（未录音 = 「🎙 按住说话」，录音中 = 红点 + 秒数）。</summary>
    public string VoiceButtonText => IsRecordingVoice
        ? $"● 录音中 {VoiceRecordingSeconds}″（松开发送）"
        : "🎙 按住说话";

    /// <summary>录音中按钮下面那行提示。</summary>
    public string VoiceRecordingHint => "松开发送 · Esc 取消 · 最长 2 分钟";

    /// <summary>
    /// 能不能按「按住说话」。
    /// <para>⚠ <b>不要加 <c>!IsRecordingVoice</c></b>：按住后一旦把按钮 IsEnabled 置 false，
    /// WPF 会释放鼠标捕获 / 按钮收不到 MouseUp —— 表现是「一按住就被取消，或松手发不出去」。
    /// 「已经在录 / 正在启动」的去重由 <see cref="StartVoiceRecordingAsync"/> 自己挡。</para>
    /// </summary>
    public bool CanRecordVoice => IsOnline;

    /// <summary>
    /// 按下「按住说话」：打开麦克风开始录音。失败时给一句中文提示，不抛异常。
    /// （视图在 <c>PreviewMouseLeftButtonDown</c> 里 fire-and-forget 调用。）
    /// </summary>
    public async Task StartVoiceRecordingAsync()
    {
        if (_voiceStarting || IsRecordingVoice) return;
        if (_session is null || !IsOnline)
        {
            Notice("还没连上房间：先开房间或连主机，再发语音消息", ChatNoticeLevel.Warn);
            return;
        }

        _voiceStarting = true;
        _voiceCancelRequested = false;
        try
        {
            var recorder = new VoiceRecorder(WindowsCallMediaFactory.Shared, Cfg.CallMicrophoneId);
            var (ok, error) = await recorder.StartAsync();
            if (!ok)
            {
                recorder.Dispose();
                Notice($"麦克风打不开：{(error.Length > 0 ? error : "设备被占用或没有权限")}", ChatNoticeLevel.Error);
                Log($"✗ 语音消息录音失败：{error}");
                return;
            }

            // 启动是异步的：开麦过程中用户可能已经按了 Esc / 关工具 / 断开连接。
            // 这时候必须把刚打开的麦克风关掉，否则会留下一个「没人控制、一直录」的僵尸录音。
            if (_voiceCancelRequested)
            {
                _voiceCancelRequested = false;
                try { recorder.Cancel(); } catch { }
                try { recorder.Dispose(); } catch { }
                Notice("已取消录音");
                return;
            }

            _voiceRecorder = recorder;
            _voiceStartedAt = DateTime.Now;
            VoiceRecordingSeconds = 0;
            IsRecordingVoice = true;
            StartVoiceTimer();
            Notice("正在录音…松开发送，Esc 取消");
        }
        catch (Exception ex)
        {
            Notice($"录音启动失败：{ex.Message}", ChatNoticeLevel.Error);
            Log($"✗ 录音启动失败：{ex.Message}");
        }
        finally
        {
            _voiceStarting = false;
        }
    }

    /// <summary>
    /// 松开「按住说话」：停止录音 → 转 MP3 → 自动发送。<paramref name="send"/> = false 时只取消不发送。
    /// </summary>
    public async Task StopVoiceRecordingAsync(bool send = true)
    {
        if (!IsRecordingVoice || _voiceStopping) return;
        _voiceStopping = true;

        var recorder = _voiceRecorder;
        _voiceRecorder = null;
        StopVoiceTimer();
        IsRecordingVoice = false;
        VoiceRecordingSeconds = 0;

        var tempMp3 = "";
        try
        {
            if (recorder is null) return;
            if (!send)
            {
                recorder.Cancel();
                recorder.Dispose();
                Notice("已取消录音");
                return;
            }

            var result = await recorder.StopAsync();
            recorder.Dispose();
            tempMp3 = result.Mp3Path ?? "";

            if (!result.Ok)
            {
                Notice($"语音消息转码失败：{(result.Error.Length > 0 ? result.Error : "MP3 编码器不可用")}", ChatNoticeLevel.Error);
                Log($"✗ 语音消息转码失败：{result.Error}");
                return;
            }
            if (result.DurationSeconds < 1 || tempMp3.Length == 0 || !File.Exists(tempMp3))
            {
                Notice("说话时间太短（至少 1 秒），没有发送", ChatNoticeLevel.Warn);
                return;
            }
            if (_session is null || !IsOnline)
            {
                Notice("连接已断开，语音消息没有发出", ChatNoticeLevel.Warn);
                return;
            }

            await _session.SendVoiceMessageAsync(tempMp3, result.DurationSeconds);
            Notice($"已发送语音消息（{result.DurationSeconds} 秒）");
            Log($"✓ 已发送语音消息（{result.DurationSeconds} 秒，{FileSize.Format(new FileInfo(tempMp3).Length)}）");
        }
        catch (Exception ex)
        {
            Notice($"发送语音消息失败：{ex.Message}", ChatNoticeLevel.Error);
            Log($"✗ 发送语音消息失败：{ex.Message}");
        }
        finally
        {
            DeleteTempVoiceFile(tempMp3);
            _voiceStopping = false;
            OnPropertyChanged(nameof(CanRecordVoice));
        }
    }

    /// <summary>取消录音（Esc / 失去鼠标捕获 / 工具关闭 / 断开连接时调）。</summary>
    public void CancelVoiceRecording()
    {
        // 麦克风还在打开的异步过程中：只记下「取消」，等 StartVoiceRecordingAsync 完成后自己关掉
        if (_voiceStarting)
        {
            _voiceCancelRequested = true;
            return;
        }

        var recorder = _voiceRecorder;
        _voiceRecorder = null;
        StopVoiceTimer();
        if (recorder is not null)
        {
            try { recorder.Cancel(); } catch { }
            try { recorder.Dispose(); } catch { }
        }
        if (IsRecordingVoice)
        {
            IsRecordingVoice = false;
            VoiceRecordingSeconds = 0;
            Notice("已取消录音");
        }
        OnPropertyChanged(nameof(CanRecordVoice));
    }

    private void StartVoiceTimer()
    {
        _voiceTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _voiceTimer.Tick -= OnVoiceTimerTick;
        _voiceTimer.Tick += OnVoiceTimerTick;
        _voiceTimer.Start();
    }

    private void StopVoiceTimer()
    {
        if (_voiceTimer is null) return;
        _voiceTimer.Tick -= OnVoiceTimerTick;
        _voiceTimer.Stop();
    }

    private void OnVoiceTimerTick(object? sender, EventArgs e)
    {
        if (!IsRecordingVoice) { StopVoiceTimer(); return; }
        var seconds = (int)Math.Floor((DateTime.Now - _voiceStartedAt).TotalSeconds);
        if (seconds != VoiceRecordingSeconds) VoiceRecordingSeconds = Math.Max(0, seconds);

        // 到上限自动停（松手也会走同一个 Stop；_voiceStopping 防重入）
        if (seconds >= VoiceRecorder.MaxDurationSeconds)
        {
            Notice($"录音到 {VoiceRecorder.MaxDurationSeconds} 秒上限，自动发送", ChatNoticeLevel.Info);
            _ = StopVoiceRecordingAsync(true);
        }
    }

    /// <summary>删掉录音临时 MP3（发送成功 / 失败 / 取消都清，避免临时目录越积越多）。</summary>
    private static void DeleteTempVoiceFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------------------------------------------------------- 真机自检钩子

    /// <summary>
    /// 调试 / 截图钩子：设了 <c>YLT_CHAT_VOICE_AUTOTEST=1</c> 时，连上房间后自动录 1.5 秒并发一条语音，
    /// 用来在真机上验证「录音 → MP3 → 发送 → 气泡」整条链路（不需要真的用鼠标按住按钮）。
    /// 正常启动不设这个变量就完全不会跑。
    /// </summary>
    internal void MaybeRunVoiceAutoTest()
    {
        if (_voiceAutoTestStarted) return;
        if (Environment.GetEnvironmentVariable("YLT_CHAT_VOICE_AUTOTEST") is null) return;
        if (!IsOnline || _session is null || !CanRecordVoice) return;
        _voiceAutoTestStarted = true;
        _ = RunVoiceAutoTestAsync();
    }

    private async Task RunVoiceAutoTestAsync()
    {
        try
        {
            await Task.Delay(1200);
            await StartVoiceRecordingAsync();
            await Task.Delay(1500);
            await StopVoiceRecordingAsync(true);
        }
        catch (Exception ex)
        {
            Log($"✗ 语音消息自检钩子失败：{ex.Message}");
        }
    }
}
