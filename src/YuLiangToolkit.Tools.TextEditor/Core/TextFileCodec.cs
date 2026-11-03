using System.Text;

namespace YuLiangToolkit.Tools.TextEditor.Core;

/// <summary>文本文件的换行风格（按文件里出现的换行判断）。</summary>
public enum TextNewlineStyle
{
    /// <summary>Windows 风格 \r\n（也是新建文件的默认）。</summary>
    CrLf,

    /// <summary>Unix 风格 \n。</summary>
    Lf,

    /// <summary>老 Mac 风格 \r（读取时会被规范化成 \r\n 再交给编辑器）。</summary>
    Cr,
}

/// <summary>文本文件的字符编码（保存时按它原样写回）。</summary>
public enum TextFileEncodingKind
{
    /// <summary>UTF-8 带 BOM。</summary>
    Utf8Bom,

    /// <summary>UTF-8 无 BOM（新建文件的默认）。</summary>
    Utf8,

    /// <summary>UTF-16 小端（Windows 记事本的「Unicode」）。</summary>
    Utf16Le,

    /// <summary>UTF-16 大端。</summary>
    Utf16Be,

    /// <summary>GB18030（兼容 GBK；中文 Windows 上的传统 ANSI 文件）。</summary>
    Gb18030,
}

/// <summary>读入的一个文本文件：文字 + 编码 + 换行风格。</summary>
public sealed record TextFileContent(string Text, TextFileEncodingKind Encoding, TextNewlineStyle Newline);

/// <summary>
/// 文本文件编解码（纯逻辑，自检直接断言）：
/// <list type="bullet">
/// <item>编码探测：BOM → UTF-8 严格校验 → GB18030 兜底（中文 Windows 传统文件不乱码）；</item>
/// <item>换行风格探测与规范化（保存时按原风格写回，不让用户文件被偷偷改换行）；</item>
/// <item>读写入口：超大小拒绝、读失败给错误消息（不抛异常，调用方直接展示）。</item>
/// </list>
/// 注意：.NET 默认不带 GB18030，静态构造里注册 <c>CodePagesEncodingProvider</c>。
/// </summary>
public static class TextFileCodec
{
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    static TextFileCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>新建文件（无标题标签）用的编码：UTF-8 无 BOM。</summary>
    public static TextFileEncodingKind DefaultEncoding => TextFileEncodingKind.Utf8;

    // ---------------- 读 ----------------

    /// <summary>
    /// 读一个文本文件。成功返回 <see cref="TextFileContent"/>；失败（不存在 / 超大 / IO 错误）
    /// 返回 <c>null</c> 并给出用户可读的错误消息。
    /// </summary>
    public static TextFileContent? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > TextEditorConfig.MaxFileBytes)
            {
                error = $"文件超过 {TextEditorConfig.MaxFileBytes / 1024 / 1024} MB，暂不支持在编辑器里打开";
                return null;
            }

            var (kind, text) = Decode(bytes);
            var newline = DetectNewline(text);
            return new TextFileContent(NormalizeForEditor(text, newline), kind, newline);
        }
        catch (Exception ex)
        {
            error = $"打开失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>编码探测：BOM 优先 → UTF-8 严格校验 → GB18030 兜底（总能解出文字）。</summary>
    public static TextFileEncodingKind DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return TextFileEncodingKind.Utf8Bom;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return TextFileEncodingKind.Utf16Le;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return TextFileEncodingKind.Utf16Be;

        // 没有 BOM：先按 UTF-8 严格解一遍，字节流不是合法 UTF-8 就当 GB18030。
        try
        {
            Utf8Strict.GetString(bytes.ToArray());
            return TextFileEncodingKind.Utf8;
        }
        catch (DecoderFallbackException)
        {
            return TextFileEncodingKind.Gb18030;
        }
    }

    /// <summary>按探测结果解码（不含去 BOM 的处理——BOM 会留在开头，交给 <see cref="StripBom"/>）。</summary>
    public static (TextFileEncodingKind Kind, string Text) Decode(byte[] bytes)
    {
        var kind = DetectEncoding(bytes);
        return (kind, DecodeAs(bytes, kind));
    }

    /// <summary>按指定编码解码（自检用；正常流程走 <see cref="Decode"/>）。</summary>
    public static string DecodeAs(byte[] bytes, TextFileEncodingKind kind)
    {
        // 只有真的带 BOM 时才剥掉，免得把正文开头的第一个字符当成 BOM 切了。
        var body = StripBom(bytes, kind);
        return kind switch
        {
            TextFileEncodingKind.Utf8Bom or TextFileEncodingKind.Utf8 => Utf8NoBom.GetString(body),
            TextFileEncodingKind.Utf16Le => Encoding.Unicode.GetString(body),
            TextFileEncodingKind.Utf16Be => Encoding.BigEndianUnicode.GetString(body),
            TextFileEncodingKind.Gb18030 => GetGb18030().GetString(body),
            _ => Utf8NoBom.GetString(body),
        };
    }

    /// <summary>把开头真正的 BOM 剥掉（没有就原样返回）。</summary>
    private static byte[] StripBom(byte[] bytes, TextFileEncodingKind kind)
    {
        return kind switch
        {
            TextFileEncodingKind.Utf8Bom when bytes.Length >= 3
                                               && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                => bytes[3..],
            TextFileEncodingKind.Utf16Le when bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                => bytes[2..],
            TextFileEncodingKind.Utf16Be when bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
                => bytes[2..],
            _ => bytes,
        };
    }

    // ---------------- 写 ----------------

    /// <summary>
    /// 写文本文件：按 <paramref name="kind"/> 编码（带 BOM 的编码会自动写 BOM），
    /// 换行统一成 <paramref name="newline"/> 风格；先写临时文件再替换（写一半断电不留半个文件）。
    /// </summary>
    public static bool TryWrite(string path, string text, TextFileEncodingKind kind, TextNewlineStyle newline, out string? error)
    {
        error = null;
        try
        {
            var body = EncodeAs(NormalizeNewline(text, newline), kind);
            var tmp = path + ".tmp-write";
            File.WriteAllBytes(tmp, body);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = $"保存失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>按指定编码编码正文（BOM 由这里负责带上，与解码时的去 BOM 对应）。</summary>
    public static byte[] EncodeAs(string text, TextFileEncodingKind kind) => kind switch
    {
        TextFileEncodingKind.Utf8Bom => Utf8WithBom.GetPreamble().Concat(Utf8NoBom.GetBytes(text)).ToArray(),
        TextFileEncodingKind.Utf8 => Utf8NoBom.GetBytes(text),
        TextFileEncodingKind.Utf16Le => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(),
        TextFileEncodingKind.Utf16Be => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray(),
        TextFileEncodingKind.Gb18030 => GetGb18030().GetBytes(text),
        _ => Utf8NoBom.GetBytes(text),
    };

    // ---------------- 换行 ----------------

    /// <summary>换行风格探测：谁先出现算谁的；没有换行 / 空文件按 Windows 风格。</summary>
    public static TextNewlineStyle DetectNewline(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') return i + 1 < text.Length && text[i + 1] == '\n' ? TextNewlineStyle.CrLf : TextNewlineStyle.Cr;
            if (c == '\n') return TextNewlineStyle.Lf;
        }
        return TextNewlineStyle.CrLf;
    }

    /// <summary>把文字里所有换行统一成目标风格（保存前用；编辑器里输入的 \r\n 也一并归一）。</summary>
    public static string NormalizeNewline(string text, TextNewlineStyle style)
    {
        var target = style switch
        {
            TextNewlineStyle.Lf => "\n",
            TextNewlineStyle.Cr => "\r",
            _ => "\r\n",
        };
        // 先把两种已知风格替换掉，剩下的孤立 \r 再归一（顺序不能反，否则 \r\n 被拆成两个换行）。
        return text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\n", target);
    }

    /// <summary>编辑器内部换行统一成 \r\n（AvalonEdit 的 TextDocument 以 \r\n 为规范形态）。</summary>
    public static string NormalizeForEditor(string text, TextNewlineStyle style)
        => style == TextNewlineStyle.CrLf ? text : NormalizeNewline(text, TextNewlineStyle.CrLf);

    // ---------------- 展示 ----------------

    /// <summary>状态栏里显示的编码名。</summary>
    public static string FriendlyEncodingName(TextFileEncodingKind kind) => kind switch
    {
        TextFileEncodingKind.Utf8Bom => "UTF-8 BOM",
        TextFileEncodingKind.Utf8 => "UTF-8",
        TextFileEncodingKind.Utf16Le => "UTF-16 LE",
        TextFileEncodingKind.Utf16Be => "UTF-16 BE",
        TextFileEncodingKind.Gb18030 => "GB18030",
        _ => "UTF-8",
    };

    private static Encoding GetGb18030() => Encoding.GetEncoding("GB18030");
}
