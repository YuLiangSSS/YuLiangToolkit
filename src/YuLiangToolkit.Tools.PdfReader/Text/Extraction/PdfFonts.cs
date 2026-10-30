namespace YuLiangToolkit.Tools.PdfReader.Text.Extraction;

/// <summary>
/// 字体解码：把内容流里字符串的原始字节，按字体定义变成可读文本。
///
/// <para>两类：</para>
/// <list type="bullet">
///   <item>简单字体（Type1 / TrueType / Type3）：一字节一个码，优先用字体自带的 ToUnicode，
///         其次 /Encoding 的 /Differences 字形名，最后 WinAnsi / Latin1 兜底；</item>
///   <item>Type0 / CID 字体：按 ToUnicode CMap 的 codespace 决定每码字节数再解码；
///         **没有 ToUnicode 就直接不输出**（宁可空着也不输出乱码）。</item>
/// </list>
///
/// <para>字体解析结果按字体字典对象缓存（跨页复用）；任何一个字体解析失败只影响它自己，
/// 内容流里遇到解析不了的字体时按 Latin1 兜底，不会把整页丢掉。</para>
/// </summary>
internal sealed class PdfFontRegistry
{
    private readonly PdfDocument _document;
    private readonly Dictionary<PdfDictionary, PdfFont?> _cache = new();
    private readonly Dictionary<PdfDictionary, PdfFont?> _firstFontCache = new();

    public PdfFontRegistry(PdfDocument document) => _document = document;

    /// <summary>按页资源 <c>/Font /&lt;名字&gt;</c> 取字体解码器；找不到返回 <c>null</c>。</summary>
    public PdfFont? Resolve(PdfDictionary? resources, PdfName? fontName)
    {
        if (resources is null || fontName is null) return null;

        var fontDictionary = ResolveFontDictionary(resources, fontName.Value);
        if (fontDictionary is null) return null;

        if (_cache.TryGetValue(fontDictionary, out var cached)) return cached;

        PdfFont? font = null;
        try
        {
            font = PdfFont.Create(_document, fontDictionary);
        }
        catch (Exception)
        {
            font = null;
        }

        _cache[fontDictionary] = font;
        return font;
    }

    /// <summary>页资源里第一个字体（有些内容流漏写 Tf，拿它兜底）。</summary>
    public PdfFont? ResolveFirst(PdfDictionary? resources)
    {
        if (resources is null) return null;
        if (_firstFontCache.TryGetValue(resources, out var cached)) return cached;

        PdfFont? font = null;
        try
        {
            var fonts = _document.ResolveDictionary(resources.Get("Font"));
            if (fonts is not null)
            {
                foreach (var value in fonts.Entries.Values)
                {
                    if (_document.ResolveDictionary(value) is not { } fontDictionary) continue;
                    font = Resolve(resources, new PdfName(FindFontName(fonts, fontDictionary) ?? ""));
                    if (font is not null) break;
                }
            }
        }
        catch (Exception)
        {
            font = null;
        }

        _firstFontCache[resources] = font;
        return font;
    }

    private PdfDictionary? ResolveFontDictionary(PdfDictionary resources, string name)
    {
        var fonts = _document.ResolveDictionary(resources.Get("Font"));
        return fonts is null ? null : _document.ResolveDictionary(fonts.Get(name));
    }

    private static string? FindFontName(PdfDictionary fonts, PdfDictionary target)
    {
        foreach (var (key, value) in fonts.Entries)
        {
            if (ReferenceEquals(value, target)) return key;
        }

        return null;
    }
}

/// <summary>一个已解析字体的解码器（不可变；解码本身无状态）。</summary>
internal sealed class PdfFont
{
    private readonly bool _isComposite;
    private readonly PdfCMap? _toUnicode;
    private readonly Dictionary<int, string>? _differences;

    private PdfFont(bool isComposite, PdfCMap? toUnicode, Dictionary<int, string>? differences)
    {
        _isComposite = isComposite;
        _toUnicode = toUnicode;
        _differences = differences;
    }

    /// <summary>解析字体字典；失败返回 <c>null</c>（调用方按 Latin1 兜底）。</summary>
    public static PdfFont? Create(PdfDocument document, PdfDictionary dictionary)
    {
        var subtype = dictionary.GetName("Subtype");
        var toUnicodeStream = document.Resolve(dictionary.Get("ToUnicode")) as PdfStream;
        var toUnicode = toUnicodeStream is null
            ? null
            : PdfCMap.Parse(document.GetStreamData(toUnicodeStream) ?? Array.Empty<byte>());

        if (string.Equals(subtype, "Type0", StringComparison.Ordinal))
            return new PdfFont(isComposite: true, toUnicode, differences: null);

        // 简单字体：/Encoding 是名称或字典（字典里可以带 BaseEncoding + Differences）
        Dictionary<int, string>? differences = null;
        var encoding = document.Resolve(dictionary.Get("Encoding"));
        if (encoding is PdfDictionary encodingDictionary)
        {
            differences = ParseDifferences(document, encodingDictionary.Get("Differences"));
        }

        return new PdfFont(isComposite: false, toUnicode, differences);
    }

    /// <summary>把内容流里的一段字符串字节解成文本。</summary>
    public string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        if (_isComposite)
        {
            // CID 字体没有 ToUnicode → 不输出（避免乱码）
            return _toUnicode?.Decode(bytes, fallbackCodeLength: 2) ?? "";
        }

        var builder = new StringBuilder(bytes.Length);
        foreach (var code in bytes)
        {
            if (_toUnicode is not null && _toUnicode.TryGetMapping(1, code, out var mapped) && mapped.Length > 0)
            {
                builder.Append(mapped);
                continue;
            }

            if (_differences is not null && _differences.TryGetValue(code, out var glyphText))
            {
                builder.Append(glyphText);
                continue;
            }

            var ch = PdfEncodings.WinAnsiToChar(code);
            if (ch != '\0') builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>解析 /Differences 数组：[ 65 /A /B 90 /Z ] 这类「码 + 连续字形名」列表。</summary>
    private static Dictionary<int, string>? ParseDifferences(PdfDocument document, PdfObject? value)
    {
        var array = document.ResolveArray(value);
        if (array is null) return null;

        var result = new Dictionary<int, string>();
        var currentCode = 0;
        foreach (var item in array.Items)
        {
            var resolved = document.Resolve(item);
            switch (resolved)
            {
                case PdfNumber number when number.IsInteger:
                    currentCode = number.IntValue;
                    break;
                case PdfName name:
                {
                    var text = PdfEncodings.GlyphNameToUnicode(name.Value);
                    if (!string.IsNullOrEmpty(text) && currentCode is >= 0 and <= 65_535)
                        result.TryAdd(currentCode, text);
                    if (currentCode < 65_535) currentCode++;
                    break;
                }
            }
        }

        return result.Count == 0 ? null : result;
    }
}
