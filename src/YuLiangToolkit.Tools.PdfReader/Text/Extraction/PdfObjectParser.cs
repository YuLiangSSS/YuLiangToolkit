namespace YuLiangToolkit.Tools.PdfReader.Text.Extraction;

/// <summary>
/// PDF 词法 / 语法解析器（纯手写）：从一个字节数组的某个位置解析 PDF 对象。
///
/// <para>PDF 是「前缀 + 括号嵌套」的语法，这里用一个游标 + 递归下降就够了。故意做得比较宽容：
/// 字符串没闭合就返回已读到的部分，字典/数组不合法才抛 <see cref="PdfParseException"/>，
/// 由上层决定降级（整份 PDF 兜底全文扫描 <c>N G obj</c>）。</para>
///
/// <para>同时负责切出流对象的原始字节：优先按 <c>/Length</c> 切（切完校验后面是不是
/// <c>endstream</c>），长度是间接引用或不对时扫描 <c>endstream</c> 兜底。</para>
/// </summary>
internal sealed class PdfObjectParser
{
    private const int MaxDepth = 64;
    private const int MaxArrayItems = 1_000_000;
    private const int MaxDictionaryEntries = 200_000;

    private readonly byte[] _data;
    private readonly int _end;
    private int _pos;
    private int _depth;

    public PdfObjectParser(byte[] data, int position = 0, int? end = null)
    {
        _data = data;
        _pos = Math.Clamp(position, 0, data.Length);
        _end = Math.Clamp(end ?? data.Length, _pos, data.Length);
    }

    /// <summary>底层字节（只读约定；解析器不会改它）。</summary>
    public byte[] Data => _data;

    /// <summary>结束位置（不含）。</summary>
    public int End => _end;

    public int Position
    {
        get => _pos;
        set => _pos = Math.Clamp(value, 0, _end);
    }

    public bool AtEnd => _pos >= _end;

    /// <summary>PDF 空白字符：NUL / TAB / LF / FF / CR / 空格。</summary>
    public static bool IsWhitespace(byte value)
        => value is 0 or 9 or 10 or 12 or 13 or 32;

    /// <summary>PDF 分隔符。</summary>
    public static bool IsDelimiter(byte value)
        => value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>普通字符（名称 / 数字 / 操作符的组成字符）。</summary>
    public static bool IsRegular(byte value) => !IsWhitespace(value) && !IsDelimiter(value);

    /// <summary>跳空白与 <c>%</c> 注释（注释到行尾）。</summary>
    public void SkipWhitespaceAndComments()
    {
        while (_pos < _end)
        {
            var b = _data[_pos];
            if (IsWhitespace(b))
            {
                _pos++;
                continue;
            }

            if (b == (byte)'%')
            {
                while (_pos < _end && _data[_pos] is not (10 or 13)) _pos++;
                continue;
            }

            break;
        }
    }

    public byte? PeekByte()
    {
        SkipWhitespaceAndComments();
        return _pos < _end ? _data[_pos] : null;
    }

    /// <summary>当前位置（跳过空白后）是不是某个关键字；不消费。</summary>
    public bool MatchesKeyword(string keyword)
    {
        SkipWhitespaceAndComments();
        return MatchesKeywordAt(_pos, keyword);
    }

    /// <summary>在指定位置精确匹配关键字（调用方保证已跳过空白）；关键字后必须是分隔符 / 空白 / 结束。</summary>
    public bool MatchesKeywordAt(int position, string keyword)
    {
        if (position < 0 || position + keyword.Length > _end) return false;
        for (var i = 0; i < keyword.Length; i++)
        {
            if (_data[position + i] != (byte)keyword[i]) return false;
        }

        var after = position + keyword.Length;
        return after >= _end || IsWhitespace(_data[after]) || IsDelimiter(_data[after]);
    }

    /// <summary>跳过空白后匹配并消费一个关键字（带边界检查）。</summary>
    public bool TryReadKeyword(string keyword)
    {
        SkipWhitespaceAndComments();
        if (!MatchesKeywordAt(_pos, keyword)) return false;
        _pos += keyword.Length;
        return true;
    }

    /// <summary>读一个「普通记号」（操作符 / 名称以外的裸词）；遇到分隔符返回 false。</summary>
    public bool TryReadBareToken(out string token)
    {
        token = "";
        SkipWhitespaceAndComments();
        if (_pos >= _end) return false;

        var start = _pos;
        while (_pos < _end && IsRegular(_data[_pos])) _pos++;
        if (_pos == start) return false;

        token = System.Text.Encoding.Latin1.GetString(_data, start, _pos - start);
        return true;
    }

    /// <summary>读一个整数（不识别引用）；带边界检查。用于 xref / 对象号等结构化位置。</summary>
    public bool TryReadInteger(out int value)
    {
        value = 0;
        SkipWhitespaceAndComments();
        var start = _pos;
        var negative = false;
        if (_pos < _end && _data[_pos] is (byte)'+' or (byte)'-')
        {
            negative = _data[_pos] == (byte)'-';
            _pos++;
        }

        var digitsStart = _pos;
        long result = 0;
        while (_pos < _end && _data[_pos] is >= (byte)'0' and <= (byte)'9')
        {
            result = result * 10 + (_data[_pos] - '0');
            if (result > int.MaxValue) result = int.MaxValue;
            _pos++;
        }

        if (_pos == digitsStart)
        {
            _pos = start;
            return false;
        }

        // 数字后必须是边界（避免把 "123abc" 当整数）
        if (_pos < _end && IsRegular(_data[_pos]))
        {
            _pos = start;
            return false;
        }

        value = negative ? (int)-result : (int)result;
        return true;
    }

    /// <summary>解析一个 PDF 对象（递归下降）。不合法时抛 <see cref="PdfParseException"/>。</summary>
    public PdfObject ParseObject()
    {
        if (++_depth > MaxDepth)
        {
            _depth--;
            throw new PdfParseException("PDF 对象嵌套太深（可能是损坏的文件）。");
        }

        try
        {
            SkipWhitespaceAndComments();
            if (_pos >= _end) throw new PdfParseException("PDF 对象意外结束。");

            var b = _data[_pos];
            return b switch
            {
                (byte)'/' => ParseName(),
                (byte)'(' => ParseLiteralString(),
                (byte)'<' => _pos + 1 < _end && _data[_pos + 1] == (byte)'<' ? ParseDictionary() : ParseHexString(),
                (byte)'[' => ParseArray(),
                (byte)'+' or (byte)'-' or (byte)'.' or (>= (byte)'0' and <= (byte)'9') => ParseNumberOrReference(),
                _ => ParseKeywordObject(),
            };
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>解析 <c>N G obj ... endobj</c>（从构造时的位置开始）；成功返回 true。</summary>
    public bool TryParseIndirectObject(out int objectNumber, out int generation, out PdfObject? value)
    {
        objectNumber = 0;
        generation = 0;
        value = null;

        var save = _pos;
        SkipWhitespaceAndComments();
        if (!TryReadInteger(out objectNumber))
        {
            _pos = save;
            return false;
        }

        if (!TryReadInteger(out generation))
        {
            _pos = save;
            return false;
        }

        if (!TryReadKeyword("obj"))
        {
            _pos = save;
            return false;
        }

        value = ParseObject();

        // 流对象：字典后面跟 stream ... endstream
        if (value is PdfDictionary dictionary && TryReadKeyword("stream"))
        {
            value = new PdfStream(dictionary) { RawData = ReadStreamRawData(dictionary) };
        }

        TryReadKeyword("endobj");
        return true;
    }

    /// <summary>解析名称对象（<c>/Name#20</c>），<c>#xx</c> 十六进制转义会解码。</summary>
    private PdfName ParseName()
    {
        _pos++; // '/'
        var builder = new StringBuilder(32);
        while (_pos < _end)
        {
            var b = _data[_pos];
            if (!IsRegular(b)) break;

            if (b == (byte)'#')
            {
                var hi = _pos + 1 < _end ? HexValue(_data[_pos + 1]) : -1;
                var lo = _pos + 2 < _end ? HexValue(_data[_pos + 2]) : -1;
                if (hi >= 0 && lo >= 0)
                {
                    builder.Append((char)((hi << 4) | lo));
                    _pos += 3;
                    continue;
                }
            }

            builder.Append((char)b);
            _pos++;
        }

        return new PdfName(builder.ToString());
    }

    /// <summary>解析字面量字符串 <c>(...)</c>：支持 <c>\n \r \t \b \f \( \) \\</c>、八进制 <c>\ddd</c>、续行。</summary>
    private PdfString ParseLiteralString()
    {
        _pos++; // '('
        var bytes = new List<byte>(32);
        var depth = 1;
        while (_pos < _end)
        {
            var b = _data[_pos++];
            if (b == (byte)'\\')
            {
                if (_pos >= _end) break;
                var escaped = _data[_pos++];
                switch (escaped)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'(': bytes.Add((byte)'('); break;
                    case (byte)')': bytes.Add((byte)')'); break;
                    case (byte)'\\': bytes.Add((byte)'\\'); break;
                    case (byte)'\r':
                        // 反斜杠 + 换行 = 续行，什么都不加
                        if (_pos < _end && _data[_pos] == (byte)'\n') _pos++;
                        break;
                    case (byte)'\n':
                        break;
                    case >= (byte)'0' and <= (byte)'7':
                    {
                        var value = escaped - '0';
                        for (var i = 0; i < 2 && _pos < _end && _data[_pos] is >= (byte)'0' and <= (byte)'7'; i++)
                        {
                            value = value * 8 + (_data[_pos++] - '0');
                        }

                        bytes.Add((byte)(value & 0xFF));
                        break;
                    }
                    default:
                        bytes.Add(escaped);
                        break;
                }

                continue;
            }

            if (b == (byte)'(')
            {
                depth++;
                bytes.Add(b);
                continue;
            }

            if (b == (byte)')')
            {
                depth--;
                if (depth == 0) break;
                bytes.Add(b);
                continue;
            }

            bytes.Add(b);
        }

        return new PdfString(bytes.ToArray());
    }

    /// <summary>解析十六进制字符串 <c>&lt;48656C6C6F&gt;</c>（奇数位补 0）。</summary>
    private PdfString ParseHexString()
    {
        _pos++; // '<'
        var bytes = new List<byte>(16);
        var high = -1;
        while (_pos < _end)
        {
            var b = _data[_pos++];
            if (b == (byte)'>') break;
            var value = HexValue(b);
            if (value < 0) continue;
            if (high < 0)
            {
                high = value;
            }
            else
            {
                bytes.Add((byte)((high << 4) | value));
                high = -1;
            }
        }

        if (high >= 0) bytes.Add((byte)(high << 4));
        return new PdfString(bytes.ToArray());
    }

    private PdfObject ParseArray()
    {
        _pos++; // '['
        var array = new PdfArray();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _end) return array; // 宽容：没闭合就返回已读部分
            if (_data[_pos] == (byte)']')
            {
                _pos++;
                return array;
            }

            if (array.Items.Count >= MaxArrayItems)
                throw new PdfParseException("PDF 数组元素过多（可能是损坏的文件）。");

            array.Items.Add(ParseObject());
        }
    }

    private PdfDictionary ParseDictionary()
    {
        _pos += 2; // '<<'
        var dictionary = new PdfDictionary();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _end) throw new PdfParseException("PDF 字典没有闭合的 >>。");

            if (_data[_pos] == (byte)'>' && _pos + 1 < _end && _data[_pos + 1] == (byte)'>')
            {
                _pos += 2;
                return dictionary;
            }

            if (_data[_pos] != (byte)'/')
                throw new PdfParseException("PDF 字典的键必须是名称。");

            var key = ParseName().Value;
            var value = ParseObject();
            dictionary.Set(key, value); // 重复键以最后一次为准

            if (dictionary.Count > MaxDictionaryEntries)
                throw new PdfParseException("PDF 字典条目过多（可能是损坏的文件）。");
        }
    }

    /// <summary>数字；整数后面跟「整数 R」时识别为间接引用。</summary>
    private PdfObject ParseNumberOrReference()
    {
        var number = ParseNumberToken();
        if (!number.IsInteger) return number;

        var save = _pos;
        if (TryReadInteger(out var generation) && generation >= 0)
        {
            if (TryReadKeyword("R"))
                return new PdfReference(number.IntValue, generation);
        }

        _pos = save;
        return number;
    }

    private PdfNumber ParseNumberToken()
    {
        var start = _pos;
        var hasDigits = false;

        if (_pos < _end && _data[_pos] is (byte)'+' or (byte)'-') _pos++;
        while (_pos < _end && _data[_pos] is >= (byte)'0' and <= (byte)'9')
        {
            hasDigits = true;
            _pos++;
        }

        if (_pos < _end && _data[_pos] == (byte)'.')
        {
            _pos++;
            while (_pos < _end && _data[_pos] is >= (byte)'0' and <= (byte)'9')
            {
                hasDigits = true;
                _pos++;
            }
        }

        if (!hasDigits)
        {
            _pos = start;
            throw new PdfParseException("PDF 数字格式不正确。");
        }

        // 指数形式极少见，但容忍一下（如 1e-3）
        if (_pos < _end && _data[_pos] is (byte)'e' or (byte)'E')
        {
            var exponentSave = _pos;
            _pos++;
            if (_pos < _end && _data[_pos] is (byte)'+' or (byte)'-') _pos++;
            var exponentDigits = _pos;
            while (_pos < _end && _data[_pos] is >= (byte)'0' and <= (byte)'9') _pos++;
            if (_pos == exponentDigits) _pos = exponentSave;
        }

        var text = System.Text.Encoding.Latin1.GetString(_data, start, _pos - start);
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            _pos = start;
            throw new PdfParseException($"无法解析 PDF 数字：{text}");
        }

        return new PdfNumber(value);
    }

    /// <summary>关键字对象：true / false / null；其他裸词视为不支持（抛异常）。</summary>
    private PdfObject ParseKeywordObject()
    {
        var save = _pos;
        if (!TryReadBareToken(out var token))
        {
            _pos = save;
            throw new PdfParseException("PDF 对象无法识别。");
        }

        return token switch
        {
            "true" => PdfBoolean.True,
            "false" => PdfBoolean.False,
            "null" => PdfNull.Instance,
            _ => throw new PdfParseException($"无法识别的 PDF 记号：{token}"),
        };
    }

    /// <summary>
    /// 读 <c>stream</c> 关键字之后的原始字节。先按 <c>/Length</c> 切并校验
    /// <c>endstream</c>，长度是间接引用 / 不对时扫描 <c>endstream</c> 兜底。
    /// </summary>
    private byte[] ReadStreamRawData(PdfDictionary dictionary)
    {
        // stream 关键字后面跟 CRLF 或 LF（有的文件只有 CR，也容忍）
        if (_pos < _end && _data[_pos] == (byte)'\r') _pos++;
        if (_pos < _end && _data[_pos] == (byte)'\n') _pos++;
        else if (_pos < _end && _data[_pos - 1] == (byte)'\r' && _data[_pos] == (byte)'\n') _pos++;

        var dataStart = _pos;

        var declaredLength = dictionary.Get("Length") is PdfNumber number && number.IsInteger
            ? number.IntValue
            : -1;
        if (declaredLength >= 0 && dataStart + declaredLength <= _end)
        {
            var afterData = dataStart + declaredLength;
            var check = afterData;
            while (check < _end && IsWhitespace(_data[check])) check++;
            if (MatchesKeywordAt(check, "endstream"))
            {
                _pos = check + "endstream".Length;
                return _data[dataStart..afterData];
            }
        }

        // 兜底：扫描 endstream
        var endstream = FindKeyword(_data, dataStart, _end, "endstream");
        var dataEnd = endstream >= 0 ? endstream : _end;
        if (dataEnd > dataStart && _data[dataEnd - 1] == (byte)'\n') dataEnd--;
        if (dataEnd > dataStart && _data[dataEnd - 1] == (byte)'\r') dataEnd--;
        if (dataEnd < dataStart) dataEnd = dataStart;

        _pos = endstream >= 0 ? endstream + "endstream".Length : _end;
        return _data[dataStart..dataEnd];
    }

    /// <summary>在 [start, end) 里找关键字（不要求边界，用于 endstream 这类流结束标记）。</summary>
    public static int FindKeyword(byte[] data, int start, int end, string keyword)
    {
        if (keyword.Length == 0) return -1;
        var last = end - keyword.Length;
        for (var i = Math.Max(0, start); i <= last; i++)
        {
            if (data[i] != (byte)keyword[0]) continue;
            var match = true;
            for (var j = 1; j < keyword.Length; j++)
            {
                if (data[i + j] != (byte)keyword[j])
                {
                    match = false;
                    break;
                }
            }

            if (match) return i;
        }

        return -1;
    }

    /// <summary>
    /// 跳过内联图片：<c>BI &lt;参数&gt; ID &lt;二进制&gt; EI</c>。调用时当前位置在 <c>BI</c> 之后。
    /// 内联图片的二进制里可能有任意字节，必须整段跳过，不能当操作符解析。
    /// </summary>
    public bool SkipInlineImage()
    {
        // 先找独立的 ID 记号
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _end) return false;
            if (TryReadBareToken(out var token))
            {
                if (token == "ID") break;
                continue;
            }

            _pos++; // 名称的 '/' 这类分隔符
        }

        // ID 后面固定跟一个空白字符，图片数据从它之后开始
        if (_pos < _end && IsWhitespace(_data[_pos])) _pos++;

        // 再找 EI：前一个字节是空白、后一个字节是空白 / 分隔符 / 文件尾
        for (var i = _pos; i + 1 < _end; i++)
        {
            if (_data[i] != (byte)'E' || _data[i + 1] != (byte)'I') continue;
            if (i > 0 && !IsWhitespace(_data[i - 1])) continue;
            var after = i + 2;
            if (after < _end && !IsWhitespace(_data[after]) && !IsDelimiter(_data[after])) continue;
            _pos = after;
            return true;
        }

        _pos = _end;
        return false;
    }

    /// <summary>从字节数组的指定位置解析一个裸对象（对象流 /ObjStm 里的对象体用这个）。</summary>
    public static PdfObject? TryParseObjectAt(byte[] data, int position, int? end, out int consumed)
    {
        consumed = 0;
        try
        {
            var parser = new PdfObjectParser(data, position, end);
            var value = parser.ParseObject();
            consumed = parser.Position - position;
            return value;
        }
        catch (PdfParseException)
        {
            return null;
        }
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
