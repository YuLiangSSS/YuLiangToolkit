using System.Runtime.InteropServices;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace YuLiangToolkit.Tools.ChatRoom.Media;

// ============================================================================
// WAV → MP3 转码（系统自带 WinRT MediaTranscoder，零 NuGet）。
//
// 为什么这么做：语音消息最终要发给对方、还要受「单次流量上限」约束，PCM16 的 WAV 太大
// （48 kHz 单声道约 96 KB/s，2 秒就 190 KB），MP3 64 kbps 只要约 8 KB/s。
// 项目里不能为了这一个转码再引一个第三方编码器（AGENTS.md 硬性规则 2），而 Windows
// 自带的 MediaTranscoder 就有 MP3 编码器（系统组件，不需要额外部署）。
//
// 线程模型：
//   · 全是异步 WinRT 调用（StorageFile / StorageFolder / MediaTranscoder），内部会走
//     WinRT 线程池；本方法自身不碰 UI，可直接在后台线程 await。
//   · 调用方（VoiceRecorder.StopAsync）保证同一时刻只有一个转码在跑，这里不做并发保护。
//
// 已知边界：
//   · Windows N / KN 版默认不含「媒体功能包」，MP3 编码器会缺失（COM 组件未注册 /
//     CodecNotFound）；这时返回中文原因并提示安装媒体功能包，不抛异常。
//   · 先试「16 kHz 单声道 64 kbps」小体积档（语音足够，文件最小）；系统不接受
//     （InvalidProfile / CodecNotFound）时退回 MediaEncodingProfile.CreateMp3(Medium) 默认档。
//   · 目标文件是「先删再建（ReplaceExisting）」：转码失败 / 取消后不会留下半个文件；
//     失败时本方法自己会删，调用方再删一次也安全（幂等）。
//   · 只做 WAV→MP3；转码结果用「ID3 或 0xFFEB 帧同步」做一次合法性检查，避免把
//     0 字节 / 被截断的文件当成成功交出去。
// ============================================================================

/// <summary>
/// 用 Windows 自带的 <see cref="MediaTranscoder"/> 把 WAV 录音转成 MP3（零第三方库）。
///
/// <para><b>为什么用系统转码器</b>：不需要随程序分发编码器、不新增 NuGet 依赖，
/// 而且 Windows 10 2004+ 都带 MP3 编码器（N / KN 版需用户装媒体功能包）。</para>
///
/// <para><b>线程模型</b>：所有等待都是 WinRT 异步操作的 <c>AsTask(ct)</c>，不阻塞调用线程；
/// 可在 UI 线程 await，也可在后台线程 await（<c>ConfigureAwait(false)</c>）。</para>
///
/// <para><b>已知边界</b>：无媒体功能包的 N / KN 版会失败并返回中文原因；转码是「整文件」
/// 行为，2 秒语音一般几十毫秒，长录音会相应变慢；取消时抛 <see cref="OperationCanceledException"/>
/// （唯一会抛的异常，其它异常统一转成 <c>(false, 中文原因)</c>）。</para>
/// </summary>
public static class Mp3Encoder
{
    /// <summary>小体积档：16 kHz 单声道 64 kbps（语音消息够用，2 秒约 16 KB）。</summary>
    private const int CompactSampleRate = 16_000;

    /// <summary>小体积档码率（bps）。</summary>
    private const int CompactBitrate = 64_000;

    /// <summary>
    /// 把 WAV 文件转成 MP3。成功时 <c>Ok = true</c> 且目标文件存在、非空、带合法 MP3 头。
    /// </summary>
    /// <param name="wavPath">输入 WAV（通常是 <see cref="WavWriter"/> 刚收尾的文件）。</param>
    /// <param name="mp3Path">目标 MP3（会覆盖同名的旧文件）。目录不存在会自动创建。</param>
    /// <param name="ct">取消令牌（松手后用户又取消发送 / 关闭窗口时用）。</param>
    /// <returns>
    /// <c>(true, "")</c> 成功；<c>(false, 中文原因)</c> 失败（不做 MP3、没有编码器、文件被占用等）。
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> 被取消（唯一的异常出口）。</exception>
    public static async Task<(bool Ok, string Error)> TranscodeWavToMp3Async(
        string wavPath, string mp3Path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            return (false, "找不到要转码的 WAV 录音文件");
        if (string.IsNullOrWhiteSpace(mp3Path))
            return (false, "没有指定 MP3 输出路径");

        // 目标目录先建好：StorageFolder.GetFolderFromPathAsync 对不存在的目录会直接失败，
        // 而临时目录可能被清理工具删掉，所以这里每次兜一次（很便宜）。
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(mp3Path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            return (false, "创建 MP3 输出目录失败：" + ex.Message);
        }

        // 两档依次试：先小体积档，不行再退回系统默认档（不同系统对 MP3 档位的支持不一样）。
        (string Label, Func<MediaEncodingProfile> Build)[] attempts =
        [
            ("16 kHz 单声道 64 kbps", BuildCompactProfile),
            ("系统默认档", BuildDefaultProfile),
        ];

        var errors = new List<string>(attempts.Length);
        foreach (var attempt in attempts)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, error) = await TryTranscodeOnceAsync(wavPath, mp3Path, attempt.Build, ct).ConfigureAwait(false);
            if (ok) return (true, "");

            errors.Add($"{attempt.Label}失败：{error}");
            TryDelete(mp3Path);   // 半成品清掉，下一档 ReplaceExisting 更干净
        }

        // 两档都失败：把原因拼起来，并给出 N 版最常见的解决办法（不改变具体原因）。
        var message = "MP3 转码失败：" + string.Join("；", errors) +
                      "（如果系统是 Windows N / KN 版，请安装「媒体功能包」后重试）";
        return (false, message);
    }

    // ===================== 单档尝试 =====================

    /// <summary>用指定档位转码一次；任何失败都转成 (false, 中文原因)，取消则原样抛出。</summary>
    private static async Task<(bool Ok, string Error)> TryTranscodeOnceAsync(
        string wavPath, string mp3Path, Func<MediaEncodingProfile> buildProfile, CancellationToken ct)
    {
        try
        {
            var profile = buildProfile();

            var source = await StorageFile.GetFileFromPathAsync(wavPath).AsTask(ct).ConfigureAwait(false);
            var folderPath = Path.GetDirectoryName(Path.GetFullPath(mp3Path))!;
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath).AsTask(ct).ConfigureAwait(false);
            var target = await folder
                .CreateFileAsync(Path.GetFileName(mp3Path), CreationCollisionOption.ReplaceExisting)
                .AsTask(ct)
                .ConfigureAwait(false);

            var transcoder = new MediaTranscoder();
            var prepared = await transcoder
                .PrepareFileTranscodeAsync(source, target, profile)
                .AsTask(ct)
                .ConfigureAwait(false);

            if (!prepared.CanTranscode)
            {
                return (false, prepared.FailureReason switch
                {
                    TranscodeFailureReason.CodecNotFound => "系统没有装这个档位的 MP3 编码器",
                    TranscodeFailureReason.InvalidProfile => "系统不接受这个编码档位",
                    _ => "系统拒绝转码（缺少媒体功能包或文件损坏）",
                });
            }

            // 真正的转码（耗时大头）：同一个 ct，取消时能尽快收手。
            await prepared.TranscodeAsync().AsTask(ct).ConfigureAwait(false);

            if (!File.Exists(mp3Path)) return (false, "转码结束但没有生成 MP3 文件");
            var length = new FileInfo(mp3Path).Length;
            if (length <= 0) return (false, "生成的 MP3 文件是空的");

            var (isMp3, why) = await LooksLikeMp3Async(mp3Path, ct).ConfigureAwait(false);
            return isMp3 ? (true, "") : (false, why);
        }
        catch (OperationCanceledException)
        {
            throw;   // 取消是契约里唯一允许抛出的异常，交给 VoiceRecorder 清理临时文件
        }
        catch (Exception ex)
        {
            return (false, Describe(ex));
        }
    }

    /// <summary>小体积档：在 CreateMp3(Low) 基础上把音频参数换成 16 kHz / 单声道 / 64 kbps。</summary>
    private static MediaEncodingProfile BuildCompactProfile()
    {
        var profile = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.Low);
        // 只覆盖音频流参数（容器仍是 MP3）。系统不接受时整档失败，会退回默认档。
        profile.Audio = AudioEncodingProperties.CreateMp3(CompactSampleRate, 1, CompactBitrate);
        return profile;
    }

    /// <summary>默认档：MediaEncodingProfile.CreateMp3(Medium)，兼容性最好。</summary>
    private static MediaEncodingProfile BuildDefaultProfile()
        => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.Medium);

    // ===================== 校验 / 清理 =====================

    /// <summary>
    /// 检查文件头是不是 MP3：'ID3'（带标签）或 0xFF 帧同步（&amp; 0xE0 == 0xE0）。
    /// <para>只读前 3 字节，够用且不占内存；比「文件 &gt; 0 字节」可靠得多。</para>
    /// </summary>
    private static async Task<(bool Ok, string Error)> LooksLikeMp3Async(string path, CancellationToken ct)
    {
        try
        {
            var head = new byte[3];
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            var read = await stream.ReadAsync(head.AsMemory(0, head.Length), ct).ConfigureAwait(false);
            if (read < 2) return (false, "生成的 MP3 文件不完整");
            if (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3') return (true, "");
            if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return (true, "");
            return (false, "生成的文件不是合法的 MP3（可能缺少媒体功能包）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, "校验 MP3 文件失败：" + ex.Message);
        }
    }

    /// <summary>把异常转成给人看的中文原因（N 版缺编码器时 Windows 抛的是 COM / 文件找不到）。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        FileNotFoundException => "系统缺少 MP3 编码器组件（Windows N / KN 版需安装「媒体功能包」）",
        TypeLoadException => "系统没有可用的媒体转码组件（Windows N / KN 版需安装「媒体功能包」）",
        COMException com => $"系统转码组件调用失败（hr=0x{com.HResult:X8}）：{com.Message}",
        UnauthorizedAccessException => "没有权限写 MP3 文件（目标目录可能被占用或只读）",
        IOException io => "读写录制文件失败：" + io.Message,
        _ => "转码失败：" + ex.Message,
    };

    /// <summary>
    /// 尽力删除文件（转码失败 / 取消时清残留）：WinRT 可能还短暂持有句柄，
    /// 所以最多重试 3 次、每次隔 20 ms；最终失败也不抛（调用方还有一次清理机会）。
    /// </summary>
    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }
}
