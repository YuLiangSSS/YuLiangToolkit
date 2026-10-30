namespace YuLiangToolkit.Tools.PdfReader.Text.Extraction;

/// <summary>
/// ToUnicode CMap 的手写解析 / 解码器。
///
/// <para>CMap 是 PDF 里给 CID / Type0 字体用的「字符编码 → Unicode」映射表，语法是 PostScript 风格的
/// <c>begincodespacerange</c> / <c>beginbfchar</c> / <c>beginbfrange</c> 块。这里只实现提取文字需要的部分：</para>
/// <list type="bullet">
///   <item>codespace range：决定一个字符码占几个字节（Identity-H 通常是 2 字节）；</item>
///   <item>bfchar：单码映射；</item>
///   <item>bfrange：连续码区间映射（目标可以是「起始串自增」或数组）。</item>
/// </list>
/// <para>没有 ToUnicode 的 CID 字体上层会直接跳过（宁可不输出，也别输出乱码）。</para>
/// </summary>
internal sealed class PdfCMap
{
    private const int MaxMapEntries = 1_000_000;
    private const int MaxRangeSpan = 65_536;
    private const int MaxInputBytes = 16 * 1024 * 1024;

    private readonly Dictionary<int, List<(long Low, long High)>> _ranges = new();
    private readonly Dictionary<long, string> _map = new();

    private PdfCMap()
    {
    }

    /// <summary>有没有 codespace range（没有的话按调用方给的默认字节数解码）。</summary>
    public bool HasCodeSpaceRanges => _ranges.Count > 0;

    /// <summary>解析一份 CMap 数据；解析不出任何映射时返回 <c>null</c>（调用方按字体编码兜底）。</summary>
    public static PdfCMap? Parse(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxInputBytes) return null;

        var tokens = Tokenize(data);
        if (tokens.Count == 0) return null;

        var cmap = new PdfCMap();
        var index = 0;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (token.Kind == CMapTokenKind.Keyword)
            {
                index = token.Text switch
                {
                    "begincodespacerange" => cmap.ParseCodeSpaceRanges(tokens, index + 1),
                    "beginbfchar" => cmap.ParseBfChars(tokens, index + 1),
                    "beginbfrange" => cmap.ParseBfRanges(tokens, index + 1),
                    _ => index + 1,
                };
                continue;
            }

            index++;
        }

        return cmap._map.Count == 0 && cmap._ranges.Count == 0 ? null : cmap;
    }

    /// <summary>查单个字符码的映射（<paramref name="codeLength"/> = 码占几个字节）。</summary>
    public bool TryGetMapping(int codeLength, int code, out string text)
    {
        return _map.TryGetValue(Key(codeLength, code), out text!);
    }

    /// <summary>
    /// 解码一段字符串字节：按 codespace 决定每码字节数，只输出有映射的字符。
    /// 没有 codespace 时按 <paramref name="fallbackCodeLength"/>（Type0/Identity-H 为 2，简单字体为 1）。
    /// 查不到映射的码直接跳过（绝不输出替换乱码）。
    /// </summary>
    public string Decode(byte[] bytes, int fallbackCodeLength)
    {
        if (bytes.Length == 0) return "";

        StringBuilder? builder = null;
        var fallback = Math.Clamp(fallbackCodeLength, 1, 4);
        var pos = 0;

        while (pos < bytes.Length)
        {
            var matchedLength = 0;
            long matchedCode = 0;

            if (HasCodeSpaceRanges)
            {
                for (var length = 1; length <= 4 && pos + length <= bytes.Length; length++)
                {
                    if (!_ranges.ContainsKey(length)) continue;
                    var code = ReadCode(bytes, pos, length);
                    if (!Contains(length, code)) continue;
                    matchedLength = length;
                    matchedCode = code;
                    break;
                }
            }

            if (matchedLength > 0)
            {
                if (_map.TryGetValue(Key(matchedLength, matchedCode), out var text))
                {
                    builder ??= new StringBuilder(bytes.Length);
                    builder.Append(text);
                }

                pos += matchedLength;
                continue;
            }

            if (!HasCodeSpaceRanges)
            {
                var length = Math.Min(fallback, bytes.Length - pos);
                var code = ReadCode(bytes, pos, length);
                if (_map.TryGetValue(Key(length, code), out var text))
                {
                    builder ??= new StringBuilder(bytes.Length);
                    builder.Append(text);
                }

                pos += length;
                continue;
            }

            // 落在 codespace 之外：保守地跳一个字节，继续找后面的字符
            pos++;
        }

        return builder?.ToString() ?? "";
    }

    private int ParseCodeSpaceRanges(List<CMapToken> tokens, int index)
    {
        while (index < tokens.Count && !IsKeyword(tokens[index], "endcodespacerange"))
        {
            if (tokens[index].Kind != CMapTokenKind.HexString || index + 1 >= tokens.Count)
            {
                index++;
                continue;
            }

            var low = tokens[index].Bytes!;
            var high = tokens[index + 1].Kind == CMapTokenKind.HexString ? tokens[index + 1].Bytes! : null;
            if (high is not null && low.Length == high.Length && low.Length is >= 1 and <= 4)
            {
                var lowCode = ReadCode(low, 0, low.Length);
                var highCode = ReadCode(high, 0, high.Length);
                if (highCode >= lowCode)
                {
                    if (!_ranges.TryGetValue(low.Length, out var list))
                    {
                        list = new List<(long, long)>();
                        _ranges[low.Length] = list;
                    }

                    list.Add((lowCode, highCode));
                }
            }

            index += 2;
        }

        return index + 1;
    }

    private int ParseBfChars(List<CMapToken> tokens, int index)
    {
        while (index < tokens.Count && !IsKeyword(tokens[index], "endbfchar"))
        {
            if (tokens[index].Kind != CMapTokenKind.HexString)
            {
                index++;
                continue;
            }

            if (index + 1 >= tokens.Count || _map.Count >= MaxMapEntries) break;

            var source = tokens[index].Bytes!;
            var target = tokens[index + 1];
            if (source.Length is >= 1 and <= 4)
            {
                var text = DecodeTarget(target);
                if (!string.IsNullOrEmpty(text) && text != "\uFFFD")
                    _map[Key(source.Length, ReadCode(source, 0, source.Length))] = text;
            }

            index += 2;
        }

        return index + 1;
    }

    private int ParseBfRanges(List<CMapToken> tokens, int index)
    {
        while (index < tokens.Count && !IsKeyword(tokens[index], "endbfrange"))
        {
            if (tokens[index].Kind != CMapTokenKind.HexString || index + 2 >= tokens.Count)
            {
                index++;
                continue;
            }

            var lowBytes = tokens[index].Bytes!;
            if (tokens[index + 1].Kind != CMapTokenKind.HexString || lowBytes.Length is < 1 or > 4)
            {
                index++;
                continue;
            }

            var highBytes = tokens[index + 1].Bytes!;
            if (highBytes.Length != lowBytes.Length) 
            {
                index += 2;
                continue;
            }

            var lowCode = ReadCode(lowBytes, 0, lowBytes.Length);
            var highCode = ReadCode(highBytes, 0, highBytes.Length);
            if (highCode < lowCode || highCode - lowCode + 1 > MaxRangeSpan)
            {
                index += 2;
                continue;
            }

            var target = tokens[index + 2];
            if (target.Kind == CMapTokenKind.ArrayStart)
            {
                index = ParseBfRangeArray(tokens, index + 3, lowCode, highCode, lowBytes.Length);
                continue;
            }

            if (target.Kind == CMapTokenKind.HexString)
            {
                AddIncrementedRange(lowCode, highCode, lowBytes.Length, target.Bytes!);
                index += 3;
                continue;
            }

            index += 3;
        }

        return index + 1;
    }

    /// <summary>数组形式的 bfrange：<c>&lt;lo&gt; &lt;hi&gt; [&lt;d0&gt; &lt;d1&gt; ...]</c>。</summary>
    private int ParseBfRangeArray(List<CMapToken> tokens, int index, long lowCode, long highCode, int codeLength)
    {
        var offset = 0;
        while (index < tokens.Count && tokens[index].Kind != CMapTokenKind.ArrayEnd)
        {
            if (tokens[index].Kind == CMapTokenKind.HexString && lowCode + offset <= highCode)
            {
                var text = PdfEncodings.DecodeUnicodeHex(tokens[index].Bytes!);
                if (!string.IsNullOrEmpty(text) && text != "\uFFFD")
                    _map[Key(codeLength, lowCode + offset)] = text;
                offset++;
            }

            index++;
        }

        return index + 1;
    }

    /// <summary>目标串按起始值逐码自增（UTF-16BE 大端整数 +1）。</summary>
    private int AddIncrementedRange(long lowCode, long highCode, int codeLength, byte[] startTarget)
    {
        if (startTarget.Length == 0) return 0;
        if (highCode < lowCode) return 0;

        var target = (byte[])startTarget.Clone();
        for (var code = lowCode; code <= highCode; code++)
        {
            if (_map.Count >= MaxMapEntries) break;

            var text = PdfEncodings.DecodeUnicodeHex(target);
            if (!string.IsNullOrEmpty(text) && text != "\uFFFD")
                _map[Key(codeLength, code)] = text;

            IncrementBigEndian(target);
        }

        return 0;
    }

    private static int SkipToRangeEnd(List<CMapToken> tokens, int index)
    {
        while (index < tokens.Count && !IsKeyword(tokens[index], "endbfrange")) index++;
        return index;
    }

    private bool Contains(int codeLength, long code)
    {
        if (!_ranges.TryGetValue(codeLength, out var list)) return false;
        foreach (var (low, high) in list)
        {
            if (code >= low && code <= high) return true;
        }

        return false;
    }

    private static string DecodeTarget(CMapToken token)
        => token.Kind switch
        {
            CMapTokenKind.HexString => PdfEncodings.DecodeUnicodeHex(token.Bytes!),
            CMapTokenKind.Name => PdfEncodings.GlyphNameToUnicode(token.Text) ?? "",
            _ => "",
        };

    private static void IncrementBigEndian(byte[] bytes)
    {
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            if (bytes[i] != 0xFF)
            {
                bytes[i]++;
                return;
            }

            bytes[i] = 0;
        }
    }

    private static long ReadCode(byte[] bytes, int position, int length)
    {
        long value = 0;
        for (var i = 0; i < length; i++) value = (value << 8) | bytes[position + i];
        return value;
    }

    private static long Key(int codeLength, long code) => ((long)codeLength << 32) | (uint)code;

    private static bool IsKeyword(CMapToken token, string keyword)
        => token.Kind == CMapTokenKind.Keyword && token.Text == keyword;

    // ------------------------------------------------------------------ 词法分析

    private enum CMapTokenKind
    {
        Keyword,
        Number,
        Name,
        HexString,
        ArrayStart,
        ArrayEnd,
    }

    private readonly struct CMapToken
    {
        public CMapToken(CMapTokenKind kind, string text, byte[]? bytes)
        {
            Kind = kind;
            Text = text;
            Bytes = bytes;
        }

        public CMapTokenKind Kind { get; }

        public string Text { get; }

        public byte[]? Bytes { get; }
    }

    private static List<CMapToken> Tokenize(byte[] data)
    {
        var tokens = new List<CMapToken>(256);
        var pos = 0;
        while (pos < data.Length)
        {
            var b = data[pos];
            if (PdfObjectParser.IsWhitespace(b))
            {
                pos++;
                continue;
            }

            if (b == (byte)'%')
            {
                while (pos < data.Length && data[pos] is not (10 or 13)) pos++;
                continue;
            }

            switch (b)
            {
                case (byte)'<':
                {
                    pos++;
                    var bytes = new List<byte>(8);
                    var high = -1;
                    while (pos < data.Length && data[pos] != (byte)'>')
                    {
                        var value = HexValue(data[pos]);
                        pos++;
                        if (value < 0) continue;
                        if (high < 0) high = value;
                        else
                        {
                            bytes.Add((byte)((high << 4) | value));
                            high = -1;
                        }
                    }

                    if (high >= 0) bytes.Add((byte)(high << 4));
                    if (pos < data.Length) pos++; // '>'
                    tokens.Add(new CMapToken(CMapTokenKind.HexString, "", bytes.ToArray()));
                    break;
                }

                case (byte)'[':
                    pos++;
                    tokens.Add(new CMapToken(CMapTokenKind.ArrayStart, "[", null));
                    break;

                case (byte)']':
                    pos++;
                    tokens.Add(new CMapToken(CMapTokenKind.ArrayEnd, "]", null));
                    break;

                case (byte)'(':
                    // CMap 里字面量字符串基本不出现，跳过（避免把里面的字符当记号）
                    pos = SkipLiteralString(data, pos);
                    break;

                case (byte)'/':
                {
                    pos++;
                    var start = pos;
                    while (pos < data.Length && PdfObjectParser.IsRegular(data[pos])) pos++;
                    tokens.Add(new CMapToken(CMapTokenKind.Name,
                        Encoding.Latin1.GetString(data, start, pos - start), null));
                    break;
                }

                default:
                {
                    if (b is (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9')
                    {
                        var start = pos;
                        pos++;
                        while (pos < data.Length &&
                               data[pos] is >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'-' or (byte)'+')
                            pos++;
                        tokens.Add(new CMapToken(CMapTokenKind.Number,
                            Encoding.Latin1.GetString(data, start, pos - start), null));
                        break;
                    }

                    var tokenStart = pos;
                    while (pos < data.Length && PdfObjectParser.IsRegular(data[pos])) pos++;
                    if (pos == tokenStart) pos++;
                    tokens.Add(new CMapToken(CMapTokenKind.Keyword,
                        Encoding.Latin1.GetString(data, tokenStart, pos - tokenStart), null));
                    break;
                }
            }
        }

        return tokens;
    }

    private static int SkipLiteralString(byte[] data, int position)
    {
        var depth = 0;
        while (position < data.Length)
        {
            var b = data[position++];
            if (b == (byte)'\\')
            {
                if (position < data.Length) position++;
                continue;
            }

            if (b == (byte)'(')
            {
                depth++;
                continue;
            }

            if (b == (byte)')')
            {
                depth--;
                if (depth == 0) break;
            }
        }

        return position;
    }

    private static int HexValue(byte value)
        => value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - '0',
            >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
            _ => -1,
        };
}
