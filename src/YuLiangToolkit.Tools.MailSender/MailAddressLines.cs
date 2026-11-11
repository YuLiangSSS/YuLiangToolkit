using System.Net.Mail;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.MailSender;

/// <summary>一次地址解析的结果：合法地址（已去重）+ 那些填错的原文（给界面提示用）。</summary>
public sealed record AddressParseResult(IReadOnlyList<string> Addresses, IReadOnlyList<string> Invalid)
{
    /// <summary>一条都没填。</summary>
    public bool IsEmpty => Addresses.Count == 0 && Invalid.Count == 0;

    /// <summary>填了东西但没有一个合法地址。</summary>
    public bool HasOnlyInvalid => Addresses.Count == 0 && Invalid.Count > 0;

    /// <summary>界面 / 日志里的一行摘要。</summary>
    public string Summary => Addresses.Count == 0
        ? (Invalid.Count == 0 ? "（空）" : $"（无效：{string.Join("、", Invalid)}）")
        : string.Join("、", Addresses) + (Invalid.Count > 0 ? $"（另有 {Invalid.Count} 个无效）" : "");
}

/// <summary>
/// 地址相关的纯逻辑：把界面上「一行一个 / 逗号 / 分号 / 空格」混着写的收件人拆成地址数组，
/// 校验后去重；再把发件人拼成 Resend 认识的 <c>名字 &lt;地址&gt;</c> 形式。
///
/// <para>单独成静态类是为了让自检能直接断言（不建窗口、不联网）。</para>
/// </summary>
public static class MailAddressLines
{
    // 拆地址：逗号（含中文逗号）、分号（含中文分号）、换行、制表符、空白都算分隔符。
    private static readonly Regex Separators = new(@"[,;，；\s]+", RegexOptions.Compiled);

    /// <summary>
    /// 拆分并校验一串地址（收件人 / 抄送 / 密送都用它）。
    /// 保留填写顺序去重（忽略大小写），非法项原样留在 <see cref="AddressParseResult.Invalid"/> 里。
    /// </summary>
    public static AddressParseResult Parse(string? text)
    {
        var addresses = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in Separators.Split((text ?? "").Trim()))
        {
            var candidate = part.Trim().Trim('<', '>', '"', '\'');
            if (candidate.Length == 0) continue;
            if (!IsValidEmail(candidate))
            {
                if (!invalid.Contains(candidate, StringComparer.OrdinalIgnoreCase)) invalid.Add(candidate);
                continue;
            }
            if (seen.Add(candidate)) addresses.Add(candidate);
        }

        return new AddressParseResult(addresses, invalid);
    }

    /// <summary>单个地址是否合法（用 .NET 的 MailAddress 解析，比手写正则靠谱）。</summary>
    public static bool IsValidEmail(string? email)
    {
        var text = (email ?? "").Trim();
        if (text.Length == 0 || text.Contains(',')) return false;
        try
        {
            var address = new MailAddress(text);
            return string.Equals(address.Address, text, StringComparison.OrdinalIgnoreCase)
                   && address.Address.Contains('@');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 拼发件人：有名字时写 <c>名字 &lt;地址&gt;</c>（Resend 支持这种带显示名的写法），
    /// 名字里如果混进了尖括号先去掉，避免把整串写坏。地址为空时返回空字符串。
    /// </summary>
    public static string BuildFrom(string? name, string? address)
    {
        var trimmedAddress = (address ?? "").Trim();
        if (trimmedAddress.Length == 0) return "";
        var displayName = (name ?? "").Trim().Replace("<", "").Replace(">", "").Replace("\"", "");
        return displayName.Length == 0 ? trimmedAddress : $"{displayName} <{trimmedAddress}>";
    }

    /// <summary>
    /// 从 <c>名字 &lt;地址&gt;</c> 写法里取出地址（用户把整串贴在「发件人地址」框里时兜底用）。
    /// 取不出来就返回原串（去掉首尾空白）。
    /// </summary>
    public static string ExtractAddress(string? fromField)
    {
        var text = (fromField ?? "").Trim();
        var open = text.LastIndexOf('<');
        var close = text.LastIndexOf('>');
        if (open >= 0 && close > open) return text[(open + 1)..close].Trim();
        return text;
    }

    /// <summary>发件人域名的后半段（<c>a@b.example.com</c> → <c>example.com</c> 这类只用于界面提示，不做校验）。</summary>
    public static string GetDomain(string? email)
    {
        var text = ExtractAddress(email);
        var at = text.LastIndexOf('@');
        return at >= 0 && at < text.Length - 1 ? text[(at + 1)..].ToLowerInvariant() : "";
    }
}
