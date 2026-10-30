namespace YuLiangToolkit.Tools.PdfReader.Text.Extraction;

/// <summary>
/// 手写的字符编码表 / 字形名表（不引第三方库，也不依赖系统的代码页 Provider）。
///
/// <para>三处用到：</para>
/// <list type="bullet">
///   <item>简单字体（Type1 / TrueType / Type3）的字节 → Unicode（WinAnsiEncoding / Latin1 兜底）；</item>
///   <item>字体 <c>/Differences</c> 里的字形名（<c>/A</c>、<c>/eacute</c>、<c>/uni4E2D</c>…）→ Unicode；</item>
///   <item>书签 <c>/Title</c> 字符串（UTF-16BE BOM 或 PDFDocEncoding）→ 可读文本。</item>
/// </list>
/// </summary>
internal static class PdfEncodings
{
    /// <summary>WinAnsiEncoding 的 0x80–0x9F 区（其余字节与 Latin1 相同）。</summary>
    private static readonly char[] WinAnsiHigh =
    {
        '\u20AC', '\uFFFD', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\uFFFD', '\u017D', '\uFFFD',
        '\uFFFD', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\uFFFD', '\u017E', '\u0178',
    };

    /// <summary>PDFDocEncoding 里 0x18–0x1F 的特殊字符。</summary>
    private static readonly char[] PdfDocLowSpecials =
    {
        '\u02D8', '\u02C7', '\u02C6', '\u02D9', '\u02DD', '\u02DB', '\u02DA', '\u02DC',
    };

    /// <summary>PDFDocEncoding 里 0x80–0x9F 的特殊字符（0xA0 是欧元符号）。</summary>
    private static readonly char[] PdfDocHighSpecials =
    {
        '\u2022', '\u2020', '\u2021', '\u2026', '\u2014', '\u2013', '\u0192', '\u2044',
        '\u2039', '\u203A', '\u2212', '\u2030', '\u201E', '\u201C', '\u201D', '\u2018',
        '\u2019', '\u201A', '\u2122', '\uFB01', '\uFB02', '\u0141', '\u0152', '\u0160',
        '\u0178', '\u017D', '\u0131', '\u0142', '\u0153', '\u0161', '\u017E', '\uFFFD',
    };

    private static readonly Dictionary<string, int> GlyphNames = BuildGlyphNames();

    /// <summary>WinAnsiEncoding 字节 → Unicode；未定义的码位返回 <c>'\0'</c>（调用方跳过）。</summary>
    public static char WinAnsiToChar(int code)
    {
        if (code is >= 0x20 and <= 0x7E) return (char)code;
        if (code is >= 0x80 and <= 0x9F) return WinAnsiHigh[code - 0x80];
        if (code is >= 0xA0 and <= 0xFF) return (char)code;
        return '\0';
    }

    /// <summary>把单个简单字体字节按 WinAnsi + Latin1 兜底转成字符。</summary>
    public static string DecodeWinAnsiByte(byte code)
    {
        var ch = WinAnsiToChar(code);
        return ch == '\0' ? "" : ch.ToString();
    }

    /// <summary>
    /// 解 PDF 文本字符串（书签 <c>/Title</c> 等）：<c>FE FF</c> = UTF-16BE；<c>FF FE</c> = UTF-16LE；
    /// 否则按 PDFDocEncoding（≈ Latin1 + 少量特殊字符）解。
    /// </summary>
    public static string DecodeTextString(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            switch (b)
            {
                case >= 0x18 and <= 0x1F:
                    builder.Append(PdfDocLowSpecials[b - 0x18]);
                    break;
                case >= 0x80 and <= 0x9F:
                    builder.Append(PdfDocHighSpecials[b - 0x80]);
                    break;
                case 0xA0:
                    builder.Append('\u20AC'); // PDFDocEncoding 里 0xA0 是欧元符号
                    break;
                case 0x7F:
                    break; // 未定义，跳过
                default:
                    builder.Append((char)b);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 解 ToUnicode CMap 里 bfchar / bfrange 的目标串：一般是 UTF-16BE 字节（2 字节一个字符，
    /// 4 字节可能是代理对）；奇数长度或单字节时按 Latin1 兜底。
    /// </summary>
    public static string DecodeUnicodeHex(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Length == 1) return bytes[0] == 0 ? "" : ((char)bytes[0]).ToString();

        if (bytes.Length % 2 == 0)
        {
            var text = Encoding.BigEndianUnicode.GetString(bytes);
            // 目标串可能是 "<0041 0000>" 这种补 0 的写法，去掉尾部 NUL
            return text.TrimEnd('\0');
        }

        var builder = new StringBuilder(bytes.Length / 2 + 1);
        for (var i = 0; i + 1 < bytes.Length; i += 2)
            builder.Append((char)((bytes[i] << 8) | bytes[i + 1]));
        builder.Append((char)bytes[^1]);
        return builder.ToString();
    }

    /// <summary>
    /// 字形名 → Unicode 文本（只覆盖常见拉丁字形；查不到返回 <c>null</c>，调用方跳过，不输出乱码）。
    /// 另外支持 <c>uniXXXX</c> / <c>uXXXX</c> 这种数值写法。
    /// </summary>
    public static string? GlyphNameToUnicode(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        if (GlyphNames.TryGetValue(name, out var code)) return ((char)code).ToString();

        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7)
        {
            var builder = new StringBuilder();
            for (var i = 3; i + 4 <= name.Length; i += 4)
            {
                if (!int.TryParse(name.AsSpan(i, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
                builder.Append((char)value);
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        if (name.Length == 5 && name[0] == 'u' &&
            int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var single))
        {
            return ((char)single).ToString();
        }

        return null;
    }

    private static Dictionary<string, int> BuildGlyphNames()
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // ASCII 区
            ["space"] = 0x20, ["exclam"] = 0x21, ["quotedbl"] = 0x22, ["numbersign"] = 0x23,
            ["dollar"] = 0x24, ["percent"] = 0x25, ["ampersand"] = 0x26, ["quotesingle"] = 0x27,
            ["parenleft"] = 0x28, ["parenright"] = 0x29, ["asterisk"] = 0x2A, ["plus"] = 0x2B,
            ["comma"] = 0x2C, ["hyphen"] = 0x2D, ["period"] = 0x2E, ["slash"] = 0x2F,
            ["colon"] = 0x3A, ["semicolon"] = 0x3B, ["less"] = 0x3C, ["equal"] = 0x3D,
            ["greater"] = 0x3E, ["question"] = 0x3F, ["at"] = 0x40,
            ["bracketleft"] = 0x5B, ["backslash"] = 0x5C, ["bracketright"] = 0x5D,
            ["asciicircum"] = 0x5E, ["underscore"] = 0x5F, ["grave"] = 0x60,
            ["braceleft"] = 0x7B, ["bar"] = 0x7C, ["braceright"] = 0x7D, ["asciitilde"] = 0x7E,

            // 常用标点 / 符号（West 编码里会被 /Differences 引用的）
            ["quoteleft"] = 0x2018, ["quoteright"] = 0x2019, ["quotesinglbase"] = 0x201A,
            ["quotedblleft"] = 0x201C, ["quotedblright"] = 0x201D, ["quotedblbase"] = 0x201E,
            ["guilsinglleft"] = 0x2039, ["guilsinglright"] = 0x203A,
            ["guillemotleft"] = 0x00AB, ["guillemotright"] = 0x00BB,
            ["endash"] = 0x2013, ["emdash"] = 0x2014, ["bullet"] = 0x2022,
            ["ellipsis"] = 0x2026, ["dagger"] = 0x2020, ["daggerdbl"] = 0x2021,
            ["perthousand"] = 0x2030, ["trademark"] = 0x2122, ["copyright"] = 0x00A9,
            ["registered"] = 0x00AE, ["degree"] = 0x00B0, ["plusminus"] = 0x00B1,
            ["multiply"] = 0x00D7, ["divide"] = 0x00F7, ["minus"] = 0x2212,
            ["fraction"] = 0x2044, ["florin"] = 0x0192, ["currency"] = 0x00A4,
            ["cent"] = 0x00A2, ["sterling"] = 0x00A3, ["yen"] = 0x00A5,
            ["section"] = 0x00A7, ["paragraph"] = 0x00B6, ["germandbls"] = 0x00DF,
            ["dotlessi"] = 0x0131, ["dotlessj"] = 0x0237, ["Lslash"] = 0x0141,
            ["lslash"] = 0x0142, ["OE"] = 0x0152, ["oe"] = 0x0153,
            ["Scaron"] = 0x0160, ["scaron"] = 0x0161, ["Zcaron"] = 0x017D, ["zcaron"] = 0x017E,
            ["Ydieresis"] = 0x0178, ["fi"] = 0xFB01, ["fl"] = 0xFB02, ["ff"] = 0xFB00,
            ["ffi"] = 0xFB03, ["ffl"] = 0xFB04, ["nbspace"] = 0x00A0, ["sfthyphen"] = 0x00AD,

            // Latin-1 重音字母
            ["Agrave"] = 0x00C0, ["Aacute"] = 0x00C1, ["Acircumflex"] = 0x00C2,
            ["Atilde"] = 0x00C3, ["Adieresis"] = 0x00C4, ["Aring"] = 0x00C5,
            ["AE"] = 0x00C6, ["Ccedilla"] = 0x00C7, ["Egrave"] = 0x00C8,
            ["Eacute"] = 0x00C9, ["Ecircumflex"] = 0x00CA, ["Edieresis"] = 0x00CB,
            ["Igrave"] = 0x00CC, ["Iacute"] = 0x00CD, ["Icircumflex"] = 0x00CE,
            ["Idieresis"] = 0x00CF, ["Eth"] = 0x00D0, ["Ntilde"] = 0x00D1,
            ["Ograve"] = 0x00D2, ["Oacute"] = 0x00D3, ["Ocircumflex"] = 0x00D4,
            ["Otilde"] = 0x00D5, ["Odieresis"] = 0x00D6, ["Oslash"] = 0x00D8,
            ["Ugrave"] = 0x00D9, ["Uacute"] = 0x00DA, ["Ucircumflex"] = 0x00DB,
            ["Udieresis"] = 0x00DC, ["Yacute"] = 0x00DD, ["Thorn"] = 0x00DE,
            ["agrave"] = 0x00E0, ["aacute"] = 0x00E1, ["acircumflex"] = 0x00E2,
            ["atilde"] = 0x00E3, ["adieresis"] = 0x00E4, ["aring"] = 0x00E5,
            ["ae"] = 0x00E6, ["ccedilla"] = 0x00E7, ["egrave"] = 0x00E8,
            ["eacute"] = 0x00E9, ["ecircumflex"] = 0x00EA, ["edieresis"] = 0x00EB,
            ["igrave"] = 0x00EC, ["iacute"] = 0x00ED, ["icircumflex"] = 0x00EE,
            ["idieresis"] = 0x00EF, ["eth"] = 0x00F0, ["ntilde"] = 0x00F1,
            ["ograve"] = 0x00F2, ["oacute"] = 0x00F3, ["ocircumflex"] = 0x00F4,
            ["otilde"] = 0x00F5, ["odieresis"] = 0x00F6, ["oslash"] = 0x00F8,
            ["ugrave"] = 0x00F9, ["uacute"] = 0x00FA, ["ucircumflex"] = 0x00FB,
            ["udieresis"] = 0x00FC, ["yacute"] = 0x00FD, ["thorn"] = 0x00FE,
            ["ydieresis"] = 0x00FF,
        };

        for (var i = 0; i < 26; i++)
        {
            names[((char)('A' + i)).ToString()] = 'A' + i;
            names[((char)('a' + i)).ToString()] = 'a' + i;
        }

        for (var i = 0; i <= 9; i++)
        {
            var numberNames = new[]
            {
                "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            };
            names[numberNames[i]] = '0' + i;
        }

        return names;
    }
}
