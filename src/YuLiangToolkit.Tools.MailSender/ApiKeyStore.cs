using System.Security.Cryptography;
using System.Text;

namespace YuLiangToolkit.Tools.MailSender;

/// <summary>
/// 邮件推送 API Key 的落盘保护：Windows DPAPI（<see cref="DataProtectionScope.CurrentUser"/>）。
///
/// <para>为什么需要它：推送服务的 API Key 等价于「以你的域名随便发信」的凭证，
/// 明文写进 <c>Config\mail-sender\config.json</c> 的话，截图、备份、同步盘都可能泄露。
/// 用 DPAPI 按当前 Windows 用户加密后，配置文件里只有一串 <c>dpapi:...</c> 密文；
/// 同一台电脑的同一个用户能解开，换用户 / 换电脑解不开（这时界面显示空，重新填一次即可）。</para>
///
/// <para>和「邮箱代收」的 <c>SecureStore</c> 是同一套做法（各工具各留一份，避免工具之间互相引用）。</para>
/// </summary>
public static class ApiKeyStore
{
    private const string Prefix = "dpapi:";

    /// <summary>是否是可识别的 DPAPI 密文。</summary>
    public static bool IsProtected(string? value)
        => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密：空字符串原样返回；失败返回空字符串（宁可让用户重填，也不悄悄写明文）。</summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>解密：空 / 非密文原样返回；解不开（换了用户 / 机器）返回空字符串。</summary>
    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!IsProtected(value)) return value;
        try
        {
            var protectedBytes = Convert.FromBase64String(value[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>界面 / 日志里显示的 Key 摘要：只留前 4 位与后 4 位（<c>re_C…HoHo6</c>）。</summary>
    public static string Mask(string? apiKey)
    {
        var text = (apiKey ?? "").Trim();
        if (text.Length == 0) return "（未填写）";
        if (text.Length <= 10) return text[..Math.Min(4, text.Length)] + "…";
        return text[..4] + "…" + text[^4..];
    }
}
