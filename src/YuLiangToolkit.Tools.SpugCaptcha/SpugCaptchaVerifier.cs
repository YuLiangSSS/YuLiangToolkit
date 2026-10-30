namespace YuLiangToolkit.Tools.SpugCaptcha;

/// <summary>验证码来自哪个通道。</summary>
public enum SpugChannel
{
    /// <summary>短信。</summary>
    Sms,

    /// <summary>邮箱。</summary>
    Mail,
}

/// <summary>
/// 一条「已成功发出、等待本地校对」的验证码记录。只放内存（<b>不写配置</b>），
/// 因为验证码是敏感信息，落盘没有意义。
/// </summary>
public sealed record PendingCode(SpugChannel Channel, string Code, string Recipient, DateTimeOffset SentAt)
{
    /// <summary>给界面 / 日志用的中文通道名。</summary>
    public string ChannelName => Channel == SpugChannel.Sms ? "短信" : "邮箱";
}

/// <summary>本地验证结果：成功时带上匹配到的通道名（短信 / 邮箱，可能两个都有）。</summary>
public sealed record VerifyOutcome(bool Success, string Message, IReadOnlyList<string> MatchedChannels);

/// <summary>
/// 本工具自己的验证逻辑：Spug 只有「发送验证码」接口、<b>没有「校验验证码」接口</b>，
/// 所以验证只能在本机把用户输入的验证码和刚生成并成功发出的那串做比对。
/// 纯逻辑，自检直接断言。
/// </summary>
public static class SpugCaptchaVerifier
{
    /// <summary>生成 6 位数字验证码（Spug 建议 4~6 位数字或字母）。</summary>
    public static string GenerateCode() => Random.Shared.Next(0, 1_000_000).ToString("D6");

    /// <summary>
    /// 决定本次发送用哪个验证码：<paramref name="customInput"/> 留空 → 随机生成 6 位；
    /// 填了就校验格式（4~6 位数字或字母）并原样使用。返回 false 时 <paramref name="error"/> 是可显示的原因。
    /// </summary>
    public static bool TryResolveCode(string? customInput, out string code, out string error)
    {
        var text = (customInput ?? "").Trim();
        if (text.Length == 0)
        {
            code = GenerateCode();
            error = "";
            return true;
        }

        if (!SpugApi.IsValidCode(text))
        {
            code = "";
            error = "自定义验证码格式不对：应为 4~6 位数字或字母";
            return false;
        }

        code = text;
        error = "";
        return true;
    }

    /// <summary>
    /// 把用户输入与待验证记录比对：忽略首尾空格和大小写，自动匹配短信 / 邮箱通道。
    /// 输入为空、格式不对、没发过验证码、对不上，都会返回可读的失败原因。
    /// </summary>
    public static VerifyOutcome Verify(string? input, IReadOnlyList<PendingCode> pending)
    {
        var code = (input ?? "").Trim();
        if (code.Length == 0) return new VerifyOutcome(false, "请先输入收到的验证码", Array.Empty<string>());
        if (!SpugApi.IsValidCode(code)) return new VerifyOutcome(false, "验证码应为 4~6 位数字或字母", Array.Empty<string>());
        if (pending.Count == 0)
            return new VerifyOutcome(false, "还没有成功发送过验证码，无法本地比对（先点「发送验证码」）", Array.Empty<string>());

        var matches = pending
            .Where(item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            var channels = string.Join(" / ", pending.Select(item => item.ChannelName).Distinct());
            return new VerifyOutcome(false, $"验证码不匹配（已发送通道：{channels}）", Array.Empty<string>());
        }

        var matchedChannels = matches.Select(item => item.ChannelName).Distinct().ToList();
        return new VerifyOutcome(true, $"✅ 验证成功（{string.Join(" + ", matchedChannels)}）", matchedChannels);
    }
}
