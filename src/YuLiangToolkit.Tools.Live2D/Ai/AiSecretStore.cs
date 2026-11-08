using System.Security.Cryptography;
using System.Text;

namespace YuLiangToolkit.Tools.Live2D.Ai;

/// <summary>
/// DeepSeek API Key 的落盘保护：Windows DPAPI（<see cref="DataProtectionScope.CurrentUser"/>）。
///
/// <para>为什么需要它：API Key 等价于账户余额的钥匙，明文写进 <c>Config\live2d\config.json</c>
/// 的话，截图 / 备份 / 同步盘 / 把配置发给别人都可能泄露。用 DPAPI 按当前 Windows 用户加密后，
/// 配置文件里只有一串 <c>dpapi:...</c> 密文；同一台电脑的同一个用户能解开，换用户 / 换电脑解不开
/// （这时界面显示空，重新填一次即可）。</para>
///
/// <para>兼容性：解不开或遇到历史明文（不以 <c>dpapi:</c> 开头）时按原样返回，
/// 这样手改配置 / 从旧版本升级也不会直接把 Key 弄丢。</para>
/// </summary>
public static class AiSecretStore
{
    /// <summary>密文前缀（配置里只有带这个前缀的才算 DPAPI 密文）。</summary>
    public const string Prefix = "dpapi:";

    /// <summary>是否是可识别的 DPAPI 密文。</summary>
    public static bool IsProtected(string? value)
        => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密：空字符串原样返回空串；失败返回空字符串（宁可让用户重填，也不悄悄写明文）。</summary>
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

    /// <summary>界面显示用的打码文案（不泄露 Key；空 = 没填）。</summary>
    public static string Describe(string? protectedValue)
    {
        var plain = Unprotect(protectedValue);
        if (plain.Length == 0) return "未填写 Key（离线兜底）";
        if (plain.Length <= 8) return "已填写（密文保存）";
        return $"已填写：{plain[..4]}…{plain[^4..]}（密文保存）";
    }
}
