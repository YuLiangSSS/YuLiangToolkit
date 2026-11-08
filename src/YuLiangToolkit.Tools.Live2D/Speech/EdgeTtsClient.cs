using System.IO;
using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.Live2D.Speech;

/// <summary>
/// 微软 Edge 在线神经网络 TTS（非官方接口，即 Python <c>edge-tts</c> 的纯 C# 移植）：
/// 文本 → SSML → WebSocket 流式合成 → 返回 MP3 字节。零第三方包，仅用 BCL。
///
/// <para><b>DRM</b>：当前服务端要求 v2 版 <c>Sec-MS-GEC</c> 令牌，格式为
/// <c>16 位 CS64 十六进制-64 位自定义 SHA-256 十六进制</c>。计算逻辑按 Python
/// <c>edge_tts/drm.py</c> 逐位移植（非标准 SHA-256：含 FIPS 调整常量、自定义初始状态和
/// CS64 混合摘要），不能用 <see cref="System.Security.Cryptography.SHA256"/> 代替。</para>
///
/// <para><b>音频格式</b>：固定 <c>audio-24khz-48kbitrate-mono-mp3</c>（24kHz / 48kbps / 单声道）。
/// 长文本按 UTF-8 字节切块，每块单独建一次 WebSocket，音频按顺序拼接后一起返回。</para>
///
/// <para><b>线程</b>：<see cref="SynthesizeMp3Async"/> 是纯异步、可重入的；同一个实例可以并发调用，
/// 但 <see cref="LastError"/> 是实例级字段，并发时只反映最近一次失败。</para>
///
/// <para><b>隐私</b>：文本会发送到微软在线服务；音频只在内存里返回，不写磁盘。</para>
/// </summary>
public sealed class EdgeTtsClient
{
    // ==================================================================
    // 常量（对应 edge_tts/constants.py；版本号变了要一起改）
    // ==================================================================

    /// <summary>微软 Edge 的受信任客户端令牌（公开常量，非密钥）。</summary>
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";

    /// <summary>Edge 浏览器版本（决定 Sec-MS-GEC 版本与 User-Agent）。</summary>
    private const string ChromiumFullVersion = "143.0.3650.75";

    /// <summary>v2 令牌版本参数。</summary>
    private const string SecMsGecVersion = "2-" + ChromiumFullVersion;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";

    private const string WssBaseUrl =
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
        "?TrustedClientToken=" + TrustedClientToken;

    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Python datetime 的 Unix 纪元 ticks（0001-01-01 到 1970-01-01 的 100ns 数）。</summary>
    private const long UnixEpochTicks = 621355968000000000L;

    // ==================================================================
    // 公开 API
    // ==================================================================

    /// <summary>
    /// 内置的一组中文女声（ShortName 已用在线 voices 列表核对，服务端可接受）。
    /// 想换音色时把 <see cref="ShortName"/> 传给 <see cref="SynthesizeMp3Async"/> 即可。
    /// </summary>
    public static IReadOnlyList<(string ShortName, string DisplayName)> Voices { get; } =
        new (string ShortName, string DisplayName)[]
        {
            ("zh-CN-XiaoyiNeural", "晓伊 · 少女音（默认软妹）"),
            ("zh-CN-XiaoxiaoNeural", "晓晓 · 温柔女声"),
            ("zh-CN-liaoning-XiaobeiNeural", "晓北 · 东北甜妹"),
            ("zh-CN-shaanxi-XiaoniNeural", "晓妮 · 陕西女声"),
            ("zh-TW-HsiaoChenNeural", "晓臻 · 台湾女声"),
            ("zh-TW-HsiaoYuNeural", "晓雨 · 台湾甜妹"),
            ("zh-HK-HiuMaanNeural", "晓曼 · 粤语女声"),
        };

    /// <summary>默认在线音色（晓伊 · 少女音）。</summary>
    public const string DefaultShortName = "zh-CN-XiaoyiNeural";

    /// <summary>最近一次失败原因（成功时清空；不会抛异常）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 最近一次合成是否因为「情感风格不被该音色支持」而回退成了无风格版本。
    /// <para>Edge 的 <c>mstts:express-as</c> 对部分音色 / 风格组合会直接失败；失败时自动去掉风格重试一次，
    /// 让语音不至于完全没声音（只是没有演技）。诊断 / 试听清单会显示这个标记。</para>
    /// </summary>
    public bool LastStyleFallback { get; private set; }

    /// <summary>取内置音色的中文显示名；不认识的 ShortName 原样返回。</summary>
    public static string GetDisplayName(string? shortName)
    {
        if (string.IsNullOrWhiteSpace(shortName))
        {
            return "";
        }

        foreach ((string shortNameValue, string displayName) in Voices)
        {
            if (string.Equals(shortNameValue, shortName, StringComparison.OrdinalIgnoreCase))
            {
                return displayName;
            }
        }
        return shortName;
    }

    /// <summary>
    /// 生成当前时刻的 v2 <c>Sec-MS-GEC</c> 令牌（<c>16hex-64hex</c>）。
    /// 时间按 5 分钟对齐；供自测 / 诊断用。
    /// </summary>
    public static string GenerateSecMsGec() => GenerateSecMsGec(DateTimeOffset.UtcNow);

    /// <summary>生成指定时刻的 v2 令牌；<paramref name="now"/> 为空时用当前 UTC 时间。</summary>
    public static string GenerateSecMsGec(DateTimeOffset? now)
        => GenerateSecMsGec(ToUnixSeconds(now ?? DateTimeOffset.UtcNow));

    /// <summary>
    /// 生成指定 Unix 时间戳（秒，可带小数）的 v2 令牌。
    /// 这是与 Python <c>generate_sec_ms_gec_v2(unix_timestamp)</c> 逐字符对拍用的重载。
    /// </summary>
    public static string GenerateSecMsGec(double unixTimestamp)
    {
        // 对应 Python：
        //   ticks = float(unix_timestamp) + WIN_EPOCH
        //   ticks -= ticks % 300
        //   ticks *= S_TO_NS / 100
        //   str_to_hash = f"{ticks:.0f}" + TRUSTED_CLIENT_TOKEN
        // 关键点：全程用 double 复现 Python float 的舍入；结果数量级 ~1.3e17，
        // 此量级下 double 的 ulp 为 16，必然已是整数，转 long 不丢精度。
        double ticks = unixTimestamp + 11644473600.0;
        ticks -= ticks % 300.0;
        ticks *= 1e9 / 100.0;
        long tickValue = (long)ticks;

        string salt = tickValue.ToString(CultureInfo.InvariantCulture) + TrustedClientToken;
        byte[] data = Encoding.ASCII.GetBytes(salt);
        (string cs64, string sha256) = ComputeTokenHash(data);
        return cs64 + "-" + sha256;
    }

    /// <summary>
    /// 静态便捷入口（内部使用 <see cref="Shared"/>），WPF 里可以直接
    /// <c>EdgeTtsClient.SynthesizeMp3Async(...)</c>，不必先 new。
    /// 最后一个参数仅用于和同名实例方法共存，正常调用无需传。
    /// </summary>
    public static Task<byte[]?> SynthesizeMp3Async(
        string text,
        string voice = DefaultShortName,
        string rate = "+0%",
        string pitch = "+15Hz",
        CancellationToken ct = default,
        bool useSharedInstance = true,
        string? style = null,
        double styleDegree = 1.0,
        int breakAfterExclaimMs = 0,
        bool punchFirstExclaim = false,
        int breakAtEllipsisMs = 0)
    {
        _ = useSharedInstance;
        return Shared.SynthesizeMp3Async(text, voice, rate, pitch, ct, style, styleDegree,
            breakAfterExclaimMs, punchFirstExclaim, breakAtEllipsisMs);
    }

    /// <summary>静态入口使用的共享实例；走静态调用时 <see cref="LastError"/> 要从这里读。</summary>
    public static EdgeTtsClient Shared { get; } = new();

    /// <summary>
    /// 合成 MP3。失败（参数非法 / 连接失败 / 15 秒连接超时 / 非 101 / 无音频）返回
    /// <c>null</c> 并写 <see cref="LastError"/>，<b>不抛异常</b>。
    /// </summary>
    /// <param name="text">要读的文本（可以含换行；控制字符会被替换成空格）。</param>
    /// <param name="voice">ShortName，例如 <c>zh-CN-XiaoyiNeural</c>；也接受完整 SSML 语音名。</param>
    /// <param name="rate">语速，格式如 <c>+0%</c> / <c>-10%</c>。</param>
    /// <param name="pitch">音调，格式如 <c>+15Hz</c> / <c>-5Hz</c>。</param>
    /// <param name="ct">取消令牌；取消时返回 <c>null</c>。</param>
    /// <param name="style">
    /// 可选的 <c>mstts:express-as</c> 情感风格名（gentle / cheerful / embarrassed / angry / sad / affectionate …）；
    /// 空 = 老行为（无风格）。该音色不支持时会自动回退无风格重试，见 <see cref="LastStyleFallback"/>。
    /// </param>
    /// <param name="styleDegree">风格强度（0.5 ~ 2.0；>1 更夸张）。</param>
    /// <param name="breakAfterExclaimMs">在第一个「！」后插 SSML 停顿的毫秒数（0 = 不加）。</param>
    /// <returns>MP3 字节；失败为 <c>null</c>。</returns>
    public async Task<byte[]?> SynthesizeMp3Async(
        string text,
        string voice = DefaultShortName,
        string rate = "+0%",
        string pitch = "+15Hz",
        CancellationToken ct = default,
        string? style = null,
        double styleDegree = 1.0,
        int breakAfterExclaimMs = 0,
        bool punchFirstExclaim = false,
        int breakAtEllipsisMs = 0)
    {
        LastStyleFallback = false;
        var normalizedStyle = NormalizeStyle(style);
        if (normalizedStyle.Length == 0)
        {
            return await SynthesizeCoreAsync(text, voice, rate, pitch, "", 1.0,
                breakAfterExclaimMs, punchFirstExclaim, breakAtEllipsisMs, ct).ConfigureAwait(false);
        }

        var audio = await SynthesizeCoreAsync(
            text, voice, rate, pitch, normalizedStyle,
            Math.Clamp(styleDegree, 0.5, 2.0), Math.Clamp(breakAfterExclaimMs, 0, 2000),
            punchFirstExclaim, Math.Clamp(breakAtEllipsisMs, 0, 600), ct).ConfigureAwait(false);
        if (audio is not null) return audio;

        // 这个音色 / 风格组合不被支持：去掉风格重试一次，别让整句没声音
        // （停顿 / 重音这些纯 prosody 效果保留 —— Edge 接口本身是支持 <break> 和嵌套 prosody 的）
        var styleError = LastError;
        LastStyleFallback = true;
        var fallback = await SynthesizeCoreAsync(text, voice, rate, pitch, "", 1.0,
            breakAfterExclaimMs, punchFirstExclaim, breakAtEllipsisMs, ct).ConfigureAwait(false);
        if (fallback is not null) LastError = styleError;
        return fallback;
    }

    private async Task<byte[]?> SynthesizeCoreAsync(
        string text,
        string voice,
        string rate,
        string pitch,
        string style,
        double styleDegree,
        int breakAfterExclaimMs,
        bool punchFirstExclaim,
        int breakAtEllipsisMs,
        CancellationToken ct)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            LastError = "文本为空";
            return null;
        }

        if (!TryBuildVoiceName(voice, out string fullVoiceName))
        {
            LastError = "voice 参数不合法：" + voice;
            return null;
        }

        if (!RatePattern.IsMatch(rate))
        {
            LastError = "rate 参数不合法（应形如 +0%）：" + rate;
            return null;
        }

        if (!PitchPattern.IsMatch(pitch))
        {
            LastError = "pitch 参数不合法（应形如 +15Hz）：" + pitch;
            return null;
        }

        try
        {
            List<string> chunks = BuildTextChunks(text);
            if (chunks.Count == 0)
            {
                LastError = "文本里没有可合成的内容";
                return null;
            }

            using var output = new MemoryStream();
            foreach (string chunk in chunks)
            {
                byte[]? audio = await SynthesizeChunkAsync(
                        chunk, fullVoiceName, rate, pitch, style, styleDegree,
                        breakAfterExclaimMs, punchFirstExclaim, breakAtEllipsisMs, ct)
                    .ConfigureAwait(false);
                if (audio is null)
                {
                    // SynthesizeChunkAsync 已经把原因写进 LastError。
                    return null;
                }
                output.Write(audio, 0, audio.Length);
            }

            if (output.Length == 0)
            {
                LastError = "服务端没有返回音频";
                return null;
            }

            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            LastError = "语音合成已取消或超时";
            return null;
        }
        catch (Exception ex)
        {
            LastError = "语音合成失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>风格名只允许纯字母（避免把任意字符串塞进 SSML）；不合法 / 空 = 不用风格。</summary>
    private static string NormalizeStyle(string? style)
    {
        var value = (style ?? "").Trim();
        if (value.Length == 0 || value.Length > 32) return "";
        foreach (var c in value)
        {
            if (!char.IsAsciiLetter(c)) return "";
        }
        return value.ToLowerInvariant();
    }

    // ==================================================================
    // v2 令牌：SHA-256 变体 + CS64 摘要（逐位移植 edge_tts/drm.py）
    // ==================================================================

    private static readonly uint[] Sha256Initial =
    {
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
        0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    };

    private static readonly uint[] Sha256State =
    {
        0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A,
        0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2,
    };

    private static readonly uint[] Sha256Schedule =
    {
        0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA,
        0xA622EE8C, 0xD1DD9491, 0xC0DD25A0, 0xF9E70AB6,
        0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33,
        0xD258B060, 0x72766564,
    };

    private static readonly uint[] Sha256RoundConstants =
    {
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5,
        0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3,
        0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC,
        0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7,
        0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13,
        0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3,
        0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5,
        0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208,
        0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    };

    /// <summary>FIPS 调整常量（第 14 轮的 K 常量被替换）。</summary>
    private const uint Sha256Adjustment = 0xC9DC06A7;

    /// <summary>σ0 调整常量。</summary>
    private const uint Sha256Sigma0Adjustment = 0x059C0B80;

    /// <summary>σ1 调整常量。</summary>
    private const uint Sha256Sigma1Adjustment = 0x000B92C0;

    private static readonly uint[] Sha256BlockConstants = CreateBlockConstants();

    private static uint[] CreateBlockConstants()
    {
        var constants = (uint[])Sha256RoundConstants.Clone();
        constants[14] = Sha256Adjustment;
        return constants;
    }

    private static readonly (uint Coefficient, uint Offset)[] Cs64Coefficients =
    {
        (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D),
        (0x3BEB65BC, 0x29514038), (0xC89C5933, 0xB328CBDF),
        (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C),
        (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB),
        (0x06064CF3, 0xFA51A3DA), (0xDA79FA60, 0x207CC464),
        (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167),
    };

    private const uint Cs64Factor = 0x82893933;
    private const uint Cs64Adjustment0 = 0xF2F90B1A;
    private const uint Cs64Adjustment1 = 0x0B1A0000;

    /// <summary>计算 v2 令牌的两段摘要：CS64（16 hex）与自定义 SHA-256（64 hex）。</summary>
    private static (string Cs64, string Sha256) ComputeTokenHash(byte[] data)
    {
        byte[] digest = Sha256Digest(data);
        (uint cs64Word0, uint cs64Word1) = Cs64Digest(data, digest);

        // Python: struct.pack("<II", w0, w1).hex().upper()，即两个 word 各自小端展开。
        var cs64Bytes = new byte[8];
        WriteLittleEndianUInt32(cs64Bytes, 0, cs64Word0);
        WriteLittleEndianUInt32(cs64Bytes, 4, cs64Word1);

        return (Convert.ToHexString(cs64Bytes), Convert.ToHexString(digest));
    }

    /// <summary>32 位循环右移。</summary>
    private static uint RotateRight(uint value, int bits) => (value >> bits) | (value << (32 - bits));

    /// <summary>32 位循环左移 16 位（Python 的 <c>_rol16</c>）。</summary>
    private static uint RotateLeft16(uint value) => (value << 16) | (value >> 16);

    /// <summary>SHA-256 小 σ0（Python <c>_sigma0</c>，结果按 32 位截断）。</summary>
    private static uint Sigma0(uint value)
        => (value >> 7) ^ (value << 25) ^ (value >> 18) ^ (value << 14) ^ (value >> 3);

    /// <summary>SHA-256 小 σ1（Python <c>_sigma1</c>，结果按 32 位截断）。</summary>
    private static uint Sigma1(uint value)
        => (value >> 17) ^ (value << 15) ^ (value >> 19) ^ (value << 13) ^ (value >> 10);

    /// <summary>
    /// SHA-256 压缩函数（从 <paramref name="firstRound"/> 轮开始跑完 64 轮）。
    /// 当从第 14 轮开始时使用被替换过的 K 常量表，对应 Python <c>_sha256_rounds</c>。
    /// </summary>
    private static uint[] Sha256Rounds(uint[] state, uint[] words, int firstRound)
    {
        unchecked
        {
            uint a = state[0], b = state[1], c = state[2], d = state[3];
            uint e = state[4], f = state[5], g = state[6], h = state[7];
            uint[] constants = firstRound == 14 ? Sha256BlockConstants : Sha256RoundConstants;

            for (int index = firstRound; index < 64; index++)
            {
                uint s1 = RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = h + s1 + ch + constants[index] + words[index];

                uint s0 = RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = s0 + maj;

                h = g;
                g = f;
                f = e;
                e = d + t1;
                d = c;
                c = b;
                b = a;
                a = t1 + t2;
            }

            return new[] { a, b, c, d, e, f, g, h };
        }
    }

    /// <summary>
    /// 消息扩展（对应 Python <c>_expand_sha256_schedule</c>）。
    /// Python 故意保留旋转的高位、最后统一 mask；C# 的 uint 每步截断，模 2^32 等价。
    /// </summary>
    private static void ExpandSha256Schedule(uint[] words, int firstWord)
    {
        unchecked
        {
            for (int index = firstWord; index < 64; index++)
            {
                uint x = words[index - 2];
                uint y = words[index - 15];
                uint sigma1 = (x >> 17) ^ (x << 15) ^ (x >> 19) ^ (x << 13) ^ (x >> 10);
                uint sigma0 = (y >> 7) ^ (y << 25) ^ (y >> 18) ^ (y << 14) ^ (y >> 3);
                words[index] = sigma1 + words[index - 7] + sigma0 + words[index - 16];
            }
        }
    }

    /// <summary>自定义 SHA-256 初始状态（对应 Python <c>_sha256_init</c>，只用前 7 字节）。</summary>
    private static uint[] Sha256Init(ReadOnlySpan<byte> firstSeven)
    {
        unchecked
        {
            var words = new uint[64];
            words[14] = (uint)((firstSeven[0] << 16) | (firstSeven[1] << 8) | firstSeven[2]);
            words[15] = (uint)((firstSeven[3] << 24) | (firstSeven[4] << 16)
                               | (firstSeven[5] << 8) | firstSeven[6]);

            for (int index = 16; index < 30; index++)
            {
                uint value = Sha256Schedule[index - 16];

                if (index - 2 >= 14)
                {
                    value += index == 16
                        ? Sigma1(words[index - 2]) ^ Sha256Sigma1Adjustment
                        : Sigma1(words[index - 2]);
                }

                if (index - 7 >= 14)
                {
                    value += words[index - 7];
                }

                if (index - 15 >= 14)
                {
                    value += index == 29
                        ? Sigma0(words[index - 15]) ^ Sha256Sigma0Adjustment
                        : Sigma0(words[index - 15]);
                }

                words[index] = value;
            }

            words[30] = Sigma1(words[28]) + words[23] + Sigma0(words[15]) + words[14]
                        + Sha256Adjustment - Sha256RoundConstants[14];

            ExpandSha256Schedule(words, 31);

            uint[] working = Sha256Rounds(Sha256State, words, 14);
            var result = new uint[8];
            for (int index = 0; index < 8; index++)
            {
                result[index] = Sha256Initial[index] + working[index];
            }
            return result;
        }
    }

    /// <summary>自定义 SHA-256 摘要（对应 Python <c>_sha256_digest</c>）。</summary>
    private static byte[] Sha256Digest(byte[] data)
    {
        unchecked
        {
            if (data.Length < 7)
            {
                throw new ArgumentException("令牌盐至少需要 7 字节", nameof(data));
            }

            // Python 的 padding 把“虚拟的第一块（64 字节）”算进了总长度：
            //   length = 57 + len(data)；padded = data[7:] + 0x80 + zeros + be64(length*8)
            int length = 57 + data.Length;
            int zeroCount = ((55 - length) % 64 + 64) % 64;
            int tailLength = data.Length - 7;
            var padded = new byte[tailLength + 1 + zeroCount + 8];
            Buffer.BlockCopy(data, 7, padded, 0, tailLength);
            padded[tailLength] = 0x80;

            ulong bitLength = (ulong)length * 8UL;
            for (int index = 0; index < 8; index++)
            {
                padded[padded.Length - 1 - index] = (byte)(bitLength & 0xFF);
                bitLength >>= 8;
            }

            uint[] state = Sha256Init(data.AsSpan(0, 7));
            var words = new uint[64];
            for (int offset = 0; offset < padded.Length; offset += 64)
            {
                for (int index = 0; index < 16; index++)
                {
                    int position = offset + index * 4;
                    words[index] = (uint)((padded[position] << 24) | (padded[position + 1] << 16)
                                          | (padded[position + 2] << 8) | padded[position + 3]);
                }

                ExpandSha256Schedule(words, 16);
                uint[] working = Sha256Rounds(state, words, 0);
                for (int index = 0; index < 8; index++)
                {
                    state[index] += working[index];
                }
            }

            var digest = new byte[32];
            for (int index = 0; index < 8; index++)
            {
                WriteBigEndianUInt32(digest, index * 4, state[index]);
            }
            return digest;
        }
    }

    /// <summary>CS64 摘要（对应 Python <c>_cs64_digest</c>）。</summary>
    private static (uint First, uint Second) Cs64Digest(byte[] data, byte[] digest)
    {
        unchecked
        {
            uint keyA = (uint)(digest[0] | (digest[1] << 8) | (digest[2] << 16) | (digest[3] << 24)) | 1u;
            uint keyB = (uint)(digest[4] | (digest[5] << 8) | (digest[6] << 16) | (digest[7] << 24)) | 1u;

            uint factorA = keyA * Cs64Factor;
            uint factorB = keyB * Cs64Factor;
            uint[] factors = { factorA, factorB };

            // data 前面补一个 0 字节，再按小端 32 位读取（不足补零、字数补成偶数）。
            int prefixedLength = data.Length + 1;
            int wordCount = (prefixedLength + 3) / 4;
            if ((wordCount & 1) != 0)
            {
                wordCount++;
            }

            int coefficientCount = Cs64Coefficients.Length;
            var terms = new uint[coefficientCount + wordCount];
            var offsets = new uint[coefficientCount + wordCount];

            for (int index = 0; index < coefficientCount; index++)
            {
                terms[index] = Cs64Coefficients[index].Coefficient * factors[index % 2];
                offsets[index] = Cs64Coefficients[index].Offset;
            }

            for (int index = 0; index < wordCount; index++)
            {
                uint word = ReadPrefixedLittleEndianWord(data, index);
                uint term = word * (index % 2 == 0 ? keyA : keyB);
                uint offset = RotateLeft16(word) * (index % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu);

                if (index == 0)
                {
                    term += Cs64Adjustment0 * factorA;
                    offset += Cs64Adjustment1;
                }

                terms[coefficientCount + index] = term;
                offsets[coefficientCount + index] = offset;
            }

            uint swapAcc = 0;
            uint swapLast = 0;
            uint state = 0;
            uint acc = 0;

            for (int index = 0; index < terms.Length; index += 2)
            {
                uint x = terms[index];
                uint y = terms[index + 1];

                uint t = RotateLeft16(offsets[index] + x);
                uint q = t * 0x76F50F34u;
                swapAcc += RotateLeft16(q) * 0xC3EFEA97u + q;

                t = RotateLeft16(offsets[index + 1] + y);
                q = t * 0x1881CE3Cu;
                swapLast = RotateLeft16(q) * 0x35BD1EC9u + q;
                swapAcc += swapLast;

                state = state * keyA + x;
                state = RotateLeft16(state) * 0xCF98B111u;
                state = RotateLeft16(state) * 0x87085B9Fu;
                state = RotateLeft16(state) * 0x12CEB96Du;
                state = RotateLeft16(state) * 0x257E1D83u;
                acc += state;

                t = state * keyB + y;
                t = RotateLeft16(t) * 0xA27416F5u;
                t = RotateLeft16(t) * 0xD38396FFu;
                state = RotateLeft16(RotateLeft16(t) * 0x7C932B89u) * 0xBFA49F69u;
                acc += state;
            }

            return (swapLast ^ state, swapAcc ^ acc);
        }
    }

    /// <summary>读取 <c>b"\x00" + data</c> 的第 index 个 32 位小端 word（越界补 0）。</summary>
    private static uint ReadPrefixedLittleEndianWord(byte[] data, int wordIndex)
    {
        uint value = 0;
        for (int byteIndex = 0; byteIndex < 4; byteIndex++)
        {
            int sourceIndex = wordIndex * 4 + byteIndex - 1; // -1：前面补的那个 0 字节
            if (sourceIndex >= 0 && sourceIndex < data.Length)
            {
                value |= (uint)data[sourceIndex] << (byteIndex * 8);
            }
        }
        return value;
    }

    private static void WriteLittleEndianUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
        destination[offset + 2] = (byte)(value >> 16);
        destination[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteBigEndianUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)(value >> 16);
        destination[offset + 2] = (byte)(value >> 8);
        destination[offset + 3] = (byte)value;
    }

    // ==================================================================
    // WebSocket 合成
    // ==================================================================

    private async Task<byte[]?> SynthesizeChunkAsync(
        string escapedText,
        string voice,
        string rate,
        string pitch,
        string style,
        double styleDegree,
        int breakAfterExclaimMs,
        bool punchFirstExclaim,
        int breakAtEllipsisMs,
        CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Pragma", "no-cache");
            socket.Options.SetRequestHeader("Cache-Control", "no-cache");
            socket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
            socket.Options.SetRequestHeader("User-Agent", UserAgent);
            socket.Options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br, zstd");
            socket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
            socket.Options.SetRequestHeader("Cookie", "muid=" + CreateMuId() + ";");

            // Python aiohttp 用 compress=15 请求 permessage-deflate；.NET 用这个开关对齐。
            socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        }
        catch (Exception ex)
        {
            LastError = "设置 WebSocket 请求头失败：" + ex.Message;
            return null;
        }

        string url = WssBaseUrl
                     + "&ConnectionId=" + Guid.NewGuid().ToString("N")
                     + "&Sec-MS-GEC=" + GenerateSecMsGec()
                     + "&Sec-MS-GEC-Version=" + SecMsGecVersion;

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new Uri(url), connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastError = "连接超时（15 秒）";
            return null;
        }
        catch (OperationCanceledException)
        {
            LastError = "语音合成已取消";
            return null;
        }
        catch (Exception ex)
        {
            // 非 101 时 ClientWebSocket 会抛 WebSocketException，这里统一转成 null。
            LastError = "WebSocket 连接失败：" + ex.Message;
            return null;
        }

        if (socket.State != WebSocketState.Open)
        {
            LastError = "WebSocket 未进入 Open 状态：" + socket.State;
            return null;
        }

        if (!await TrySendTextAsync(socket, BuildConfigMessage(), ct).ConfigureAwait(false))
        {
            return null;
        }

        if (!await TrySendTextAsync(socket, BuildSsmlMessage(escapedText, voice, rate, pitch, style, styleDegree,
                    breakAfterExclaimMs, punchFirstExclaim, breakAtEllipsisMs), ct)
                .ConfigureAwait(false))
        {
            return null;
        }

        using var audio = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                (WebSocketMessageType type, byte[] payload) = await ReceiveMessageAsync(socket, buffer, ct)
                    .ConfigureAwait(false);

                if (type == WebSocketMessageType.Close)
                {
                    break;
                }

                if (type == WebSocketMessageType.Text)
                {
                    if (!TryParseTextFrame(payload, out string path, out _))
                    {
                        LastError = "服务端文本帧缺少 Path 头";
                        return null;
                    }

                    if (path == "turn.end")
                    {
                        break;
                    }

                    if (path is "audio.metadata" or "response" or "turn.start")
                    {
                        // 字幕 / 会话信息，这里只取音频。
                        continue;
                    }

                    LastError = "服务端返回未知 Path：" + path;
                    return null;
                }

                if (type == WebSocketMessageType.Binary)
                {
                    if (!TryParseBinaryFrame(payload, out byte[] audioData, out string error))
                    {
                        LastError = error;
                        return null;
                    }

                    if (audioData.Length > 0)
                    {
                        audio.Write(audioData, 0, audioData.Length);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            LastError = ct.IsCancellationRequested ? "语音合成已取消" : "等待服务端音频超时";
            return null;
        }
        catch (Exception ex)
        {
            LastError = "读取服务端音频失败：" + ex.Message;
            return null;
        }
        finally
        {
            // 尽量优雅关闭；失败也无所谓，Dispose 会兜底。
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // 忽略关闭异常。
            }
        }

        if (audio.Length == 0)
        {
            LastError = "服务端返回的音频为空";
            return null;
        }

        return audio.ToArray();
    }

    private async Task<bool> TrySendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);
            await socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    sendCts.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = ct.IsCancellationRequested ? "语音合成已取消" : "发送请求超时";
            return false;
        }
        catch (Exception ex)
        {
            LastError = "发送请求失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>收完整一条 WebSocket 消息（自动拼接分片）。</summary>
    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync(
        ClientWebSocket socket,
        byte[] buffer,
        CancellationToken ct)
    {
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        receiveCts.CancelAfter(ReceiveTimeout);
        using var message = new MemoryStream();
        WebSocketMessageType type;

        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    receiveCts.Token)
                .ConfigureAwait(false);

            type = result.MessageType;
            if (type == WebSocketMessageType.Close)
            {
                return (type, Array.Empty<byte>());
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }

            // 同一帧还没收完，空闲计时重新开始。
            receiveCts.CancelAfter(ReceiveTimeout);
        }

        return (type, message.ToArray());
    }

    private static string BuildConfigMessage()
    {
        return "X-Timestamp:" + DateToString() + "\r\n"
               + "Content-Type:application/json; charset=utf-8\r\n"
               + "Path:speech.config\r\n\r\n"
               + "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{"
               + "\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"false\""
               + "},"
               + "\"outputFormat\":\"" + OutputFormat + "\""
               + "}}}}\r\n";
    }

    /// <summary>
    /// 拼 SSML（public 便于自检直接断言结构）：
    /// 可选 <c>mstts:express-as style=... styledegree=...</c> + 可选「第一个感叹号后停顿」。
    /// </summary>
    public static string BuildSsmlMessage(
        string escapedText,
        string voice,
        string rate,
        string pitch,
        string? style = null,
        double styleDegree = 1.0,
        int breakAfterExclaimMs = 0,
        bool punchFirstExclaim = false,
        int breakAtEllipsisMs = 0)
    {
        var normalizedStyle = NormalizeStyle(style);
        var text = escapedText ?? "";

        // 1) 「呀！……（小声）」：第一段用更快 / 更高 / 更响的 prosody，后面停顿再放慢放轻。
        if (punchFirstExclaim && TrySplitAtFirstExclaim(text, out var head, out var tail))
        {
            var punch = "<prosody rate='+18%' pitch='+15Hz' volume='+15%'>" + head + "</prosody>"
                        + "<break time='130ms'/>"
                        + "<prosody rate='-8%' pitch='-2Hz' volume='-10%'>" + tail + "</prosody>";
            text = punch;
        }
        else if (breakAfterExclaimMs > 0)
        {
            text = InsertExclaimBreak(text, Math.Clamp(breakAfterExclaimMs, 0, 2000));
        }

        // 2) 省略号处加停顿（前 3 个，避免一句话被切得太碎）
        if (breakAtEllipsisMs > 0)
        {
            text = InsertEllipsisBreaks(text, Math.Clamp(breakAtEllipsisMs, 0, 600));
        }

        var styleNamespace = normalizedStyle.Length == 0 ? "" : " xmlns:mstts='https://www.w3.org/2001/mstts'";
        var prosody = "<prosody pitch='" + pitch + "' rate='" + rate + "' volume='+0%'>" + text + "</prosody>";
        // Azure 文档的层级：<voice><mstts:express-as ...><prosody ...>text</prosody></mstts:express-as></voice>
        var body = normalizedStyle.Length == 0
            ? prosody
            : "<mstts:express-as style='" + normalizedStyle + "' styledegree='" +
              Math.Clamp(styleDegree, 0.5, 2.0).ToString("0.##", CultureInfo.InvariantCulture) + "'>" +
              prosody + "</mstts:express-as>";

        string ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis'" + styleNamespace + " xml:lang='en-US'>"
            + "<voice name='" + voice + "'>"
            + body
            + "</voice></speak>";

        // 注意：X-Timestamp 后面的 "Z" 是微软 Edge 的“已知 bug”，Python 也这么发。
        return "X-RequestId:" + Guid.NewGuid().ToString("N") + "\r\n"
               + "Content-Type:application/ssml+xml\r\n"
               + "X-Timestamp:" + DateToString() + "Z\r\n"
               + "Path:ssml\r\n\r\n"
               + ssml;
    }

    /// <summary>在第一个感叹号后插 SSML 停顿（没有感叹号就不动）。</summary>
    private static string InsertExclaimBreak(string escapedText, int milliseconds)
    {
        var index = FirstExclaimIndex(escapedText);
        if (index < 0) return escapedText;
        var insertAt = index + 1;
        return escapedText[..insertAt] + "<break time='" + milliseconds.ToString(CultureInfo.InvariantCulture) + "ms'/>" +
               escapedText[insertAt..];
    }

    /// <summary>把第一个感叹号前的短句切出来（含感叹号）；没有感叹号或前面没字返回 false。</summary>
    private static bool TrySplitAtFirstExclaim(string escapedText, out string head, out string tail)
    {
        head = "";
        tail = escapedText;
        var index = FirstExclaimIndex(escapedText);
        if (index <= 0) return false;
        head = escapedText[..(index + 1)];
        tail = escapedText[(index + 1)..];
        return true;
    }

    private static int FirstExclaimIndex(string escapedText)
    {
        var fullWidth = escapedText.IndexOf('！');
        var halfWidth = escapedText.IndexOf('!');
        if (fullWidth < 0) return halfWidth;
        if (halfWidth < 0) return fullWidth;
        return Math.Min(fullWidth, halfWidth);
    }

    /// <summary>在省略号（… / ...）后插停顿，最多前 3 个。</summary>
    private static string InsertEllipsisBreaks(string escapedText, int milliseconds)
    {
        var breakTag = "<break time='" + milliseconds.ToString(CultureInfo.InvariantCulture) + "ms'/>";
        var builder = new StringBuilder(escapedText.Length + breakTag.Length * 3);
        var inserted = 0;
        for (var i = 0; i < escapedText.Length; i++)
        {
            var c = escapedText[i];
            builder.Append(c);
            if (inserted >= 3) continue;
            // 全角「…」或三个点里的第三个点
            var isEllipsis = c == '…' ||
                             (c == '.' && i >= 2 && escapedText[i - 1] == '.' && escapedText[i - 2] == '.');
            if (!isEllipsis) continue;
            // 全角省略号常常连写两个「……」，只在最后一个后面加一次停顿
            if (c == '…' && i + 1 < escapedText.Length && escapedText[i + 1] == '…') continue;
            builder.Append(breakTag);
            inserted++;
        }
        return builder.ToString();
    }

    /// <summary>
    /// 对应 Python <c>date_to_string()</c>：
    /// <c>Sun Nov 01 2026 15:06:54 GMT+0000 (Coordinated Universal Time)</c>（UTC）。
    /// </summary>
    private static string DateToString()
    {
        return DateTime.UtcNow.ToString(
            "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 对应 Python <c>DRM.generate_muid()</c>：<c>secrets.token_hex(16).upper()</c>，
    /// 即 32 位大写十六进制，作为 <c>Cookie: muid=...;</c> 的值。
    /// </summary>
    private static string CreateMuId() => Guid.NewGuid().ToString("N").ToUpperInvariant();

    // ==================================================================
    // 帧解析
    // ==================================================================

    private static bool TryParseTextFrame(byte[] data, out string path, out byte[] body)
    {
        int separator = IndexOfDoubleCrlf(data);
        if (separator < 0)
        {
            path = "";
            body = Array.Empty<byte>();
            return false;
        }

        path = GetHeaderValue(data.AsSpan(0, separator), "Path") ?? "";
        body = data.AsSpan(separator + 4).ToArray();
        return path.Length > 0;
    }

    /// <summary>
    /// 解析二进制帧。帧结构（已用真实抓包确认）：
    /// <c>[2 字节大端 headerLength][headerLength 字节的 header 文本][音频数据]</c>。
    /// 注意 headerLength <b>不含</b>前 2 字节，所以音频从 <c>2 + headerLength</c> 开始。
    /// </summary>
    private static bool TryParseBinaryFrame(byte[] data, out byte[] audio, out string error)
    {
        audio = Array.Empty<byte>();
        error = "";

        if (data.Length < 2)
        {
            error = "二进制帧太短，缺少 header 长度";
            return false;
        }

        int headerLength = (data[0] << 8) | data[1];
        if (headerLength < 0 || headerLength + 2 > data.Length)
        {
            error = "二进制帧 header 长度越界";
            return false;
        }

        ReadOnlySpan<byte> headers = data.AsSpan(2, headerLength);
        string? path = GetHeaderValue(headers, "Path");
        if (path != "audio")
        {
            error = "二进制帧 Path 不是 audio：" + (path ?? "(缺失)");
            return false;
        }

        string? contentType = GetHeaderValue(headers, "Content-Type");
        if (contentType is not null && contentType != "audio/mpeg")
        {
            error = "二进制帧 Content-Type 异常：" + contentType;
            return false;
        }

        int audioOffset = 2 + headerLength;
        int audioLength = data.Length - audioOffset;
        if (audioLength == 0)
        {
            // 流结束标记：没有 Content-Type、没有数据，正常忽略。
            return contentType is null;
        }

        if (contentType is null)
        {
            error = "音频帧缺少 Content-Type";
            return false;
        }

        audio = data.AsSpan(audioOffset, audioLength).ToArray();
        return true;
    }

    /// <summary>在字节数组里找 <c>\r\n\r\n</c>。</summary>
    private static int IndexOfDoubleCrlf(ReadOnlySpan<byte> data)
    {
        for (int index = 0; index + 3 < data.Length; index++)
        {
            if (data[index] == 13 && data[index + 1] == 10
                && data[index + 2] == 13 && data[index + 3] == 10)
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>从 header 文本（CRLF 分隔的 key:value）里取指定键的值。</summary>
    private static string? GetHeaderValue(ReadOnlySpan<byte> headers, string name)
    {
        int lineStart = 0;
        while (lineStart < headers.Length)
        {
            int relativeEnd = headers[lineStart..].IndexOf((byte)'\r');
            int lineEnd = relativeEnd < 0 ? headers.Length : lineStart + relativeEnd;

            ReadOnlySpan<byte> line = headers.Slice(lineStart, lineEnd - lineStart);
            int colon = line.IndexOf((byte)':');
            if (colon > 0)
            {
                string key = Encoding.ASCII.GetString(line.Slice(0, colon));
                if (string.Equals(key, name, StringComparison.Ordinal))
                {
                    return Encoding.ASCII.GetString(line.Slice(colon + 1)).Trim();
                }
            }

            lineStart = lineEnd;
            while (lineStart < headers.Length && (headers[lineStart] == 13 || headers[lineStart] == 10))
            {
                lineStart++;
            }
        }
        return null;
    }

    // ==================================================================
    // 文本处理（对应 edge_tts/communicate.py）
    // ==================================================================

    private static readonly Regex ShortVoicePattern =
        new(@"^([a-z]{2,})-([A-Z]{2,})-(.+Neural)$", RegexOptions.CultureInvariant);

    private static readonly Regex FullVoicePattern =
        new(@"^Microsoft Server Speech Text to Speech Voice \(.+,.+\)$", RegexOptions.CultureInvariant);

    private static readonly Regex RatePattern = new(@"^[+-]\d+%$", RegexOptions.CultureInvariant);
    private static readonly Regex PitchPattern = new(@"^[+-]\d+Hz$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 把 ShortName（如 <c>zh-CN-XiaoyiNeural</c>）转成 Edge 实际发送的完整语音名：
    /// <c>Microsoft Server Speech Text to Speech Voice (zh-CN, XiaoyiNeural)</c>。
    /// 也接受已经完整的形式。
    /// </summary>
    private static bool TryBuildVoiceName(string? voice, out string fullName)
    {
        fullName = "";
        if (string.IsNullOrWhiteSpace(voice))
        {
            return false;
        }

        Match match = ShortVoicePattern.Match(voice);
        if (match.Success)
        {
            string lang = match.Groups[1].Value;
            string region = match.Groups[2].Value;
            string name = match.Groups[3].Value;

            int dash = name.IndexOf('-');
            if (dash >= 0)
            {
                region = region + "-" + name[..dash];
                name = name[(dash + 1)..];
            }

            fullName = "Microsoft Server Speech Text to Speech Voice (" + lang + "-" + region + ", " + name + ")";
            return true;
        }

        if (FullVoicePattern.IsMatch(voice))
        {
            fullName = voice;
            return true;
        }

        return false;
    }

    /// <summary>对应 Python <c>remove_incompatible_characters</c>：把不支持的控制字符换成空格。</summary>
    private static string RemoveIncompatibleCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            int code = c;
            if ((code >= 0 && code <= 8) || (code >= 11 && code <= 12) || (code >= 14 && code <= 31))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    /// <summary>XML 转义（与 Python <c>xml.sax.saxutils.escape</c> 默认一致：&amp; &lt; &gt;）。</summary>
    private static string EscapeXml(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 对应 Python <c>split_text_by_byte_length(escaped, 4096)</c>：
    /// 先按换行 / 空格找自然断点，再保证不在 UTF-8 多字节字符和 XML 实体中间切断。
    /// </summary>
    private static List<string> BuildTextChunks(string rawText)
    {
        const int maxBytes = 4096;
        string escaped = EscapeXml(RemoveIncompatibleCharacters(rawText));
        byte[] bytes = Encoding.UTF8.GetBytes(escaped);
        var chunks = new List<string>();

        int start = 0;
        while (bytes.Length - start > maxBytes)
        {
            ReadOnlySpan<byte> remaining = bytes.AsSpan(start);
            ReadOnlySpan<byte> head = remaining[..maxBytes];

            int splitAt = head.LastIndexOf((byte)'\n');
            if (splitAt < 0)
            {
                splitAt = head.LastIndexOf((byte)' ');
            }
            if (splitAt < 0)
            {
                splitAt = SafeUtf8SplitLength(remaining);
            }

            splitAt = AdjustSplitForXmlEntity(remaining, splitAt);
            if (splitAt < 0)
            {
                // 和 Python 一样：极端情况下无法安全切分，就把整段作为一块。
                break;
            }

            string chunk = Encoding.UTF8.GetString(remaining[..splitAt]).Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            start += splitAt > 0 ? splitAt : 1;
        }

        string tail = Encoding.UTF8.GetString(bytes.AsSpan(start)).Trim();
        if (tail.Length > 0)
        {
            chunks.Add(tail);
        }

        return chunks;
    }

    /// <summary>从给定字节段里找最后一个“完整 UTF-8 字符”之后的边界。</summary>
    private static int SafeUtf8SplitLength(ReadOnlySpan<byte> segment)
    {
        int index = 0;
        int lastComplete = 0;
        while (index < segment.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf8(segment[index..], out _, out int consumed);
            if (status != OperationStatus.Done)
            {
                break;
            }
            index += consumed;
            lastComplete = index;
        }
        return lastComplete;
    }

    /// <summary>对应 Python <c>_adjust_split_point_for_xml_entity</c>：别把 <c>&amp;amp;</c> 切两半。</summary>
    private static int AdjustSplitForXmlEntity(ReadOnlySpan<byte> text, int splitAt)
    {
        while (splitAt > 0 && text[..splitAt].IndexOf((byte)'&') >= 0)
        {
            int ampersand = text[..splitAt].LastIndexOf((byte)'&');
            if (text[ampersand..splitAt].IndexOf((byte)';') >= 0)
            {
                break;
            }
            splitAt = ampersand;
        }
        return splitAt;
    }

    private static double ToUnixSeconds(DateTimeOffset time)
    {
        return (time.UtcDateTime.Ticks - UnixEpochTicks) / (double)TimeSpan.TicksPerSecond;
    }
}
