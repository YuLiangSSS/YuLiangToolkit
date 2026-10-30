namespace YuLiangToolkit.Tools.PdfReader.Text.Extraction;

/// <summary>
/// 页内容流（/Contents）的文字提取器：解析 PDF 内容流里的文本操作符，把文字拼成可读的一段文本。
///
/// <para>实现的操作符：<c>BT / ET</c>、<c>Tf</c>、<c>Tj</c>、<c>TJ</c>、<c>'</c>、<c>"</c>、
/// <c>Td</c>、<c>TD</c>、<c>Tm</c>、<c>T*</c>；图形 / 定位操作符一律忽略。
/// 换行与空格是启发式的：<c>T*</c> 和 y 方向位移算换行，x 方向大位移算空格，TJ 里
/// 小于 -100 的调整量算空格（和 PDF 里「字距调整」的习惯一致）。</para>
///
/// <para>内联图片（<c>BI ... ID ... EI</c>）会整段跳过，避免把图片二进制当操作符解析。
/// 字体解析失败只影响那一段文字（按 Latin1 兜底），不会丢掉整页。</para>
/// </summary>
internal sealed class PdfContentTextExtractor
{
    /// <summary>一页所有内容流拼起来的大小上限（超过截断，防止异常文件把内存吃满）。</summary>
    private const int MaxPageContentBytes = 32 * 1024 * 1024;

    private readonly PdfDocument _document;
    private readonly PdfFontRegistry _fonts;

    public PdfContentTextExtractor(PdfDocument document)
    {
        _document = document;
        _fonts = new PdfFontRegistry(document);
    }

    /// <summary>提取一页的文字（失败/没有内容流返回空串；取消时抛 <see cref="OperationCanceledException"/>）。</summary>
    public string ExtractPageText(PdfPage page, CancellationToken cancellationToken)
    {
        var content = GetPageContent(page);
        if (content.Length == 0) return "";

        var state = new PageTextState(_document, _fonts, page.Resources, cancellationToken);
        state.Run(content);
        return state.Text;
    }

    private byte[] GetPageContent(PdfPage page)
    {
        try
        {
            var contents = _document.Resolve(page.Dictionary.Get("Contents"));
            switch (contents)
            {
                case PdfStream single:
                    return LimitLength(_document.GetStreamData(single) ?? Array.Empty<byte>());
                case PdfArray array:
                {
                    using var output = new MemoryStream();
                    foreach (var item in array.Items)
                    {
                        if (_document.Resolve(item) is not PdfStream stream) continue;
                        var data = _document.GetStreamData(stream);
                        if (data is null || data.Length == 0) continue;

                        var remaining = MaxPageContentBytes - (int)output.Length;
                        if (remaining <= 0) break;
                        output.Write(data, 0, Math.Min(data.Length, remaining));
                        output.WriteByte((byte)'\n'); // 多条内容流之间补一个换行，避免文字粘连
                    }

                    return output.ToArray();
                }
                default:
                    return Array.Empty<byte>();
            }
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }

    private static byte[] LimitLength(byte[] data)
        => data.Length <= MaxPageContentBytes ? data : data[..MaxPageContentBytes];

    /// <summary>单页内容流的解析状态机（一次提取用一个实例，不跨页复用）。</summary>
    private sealed class PageTextState
    {
        private const int MaxOperandCount = 64;
        private const int MaxOperatorCount = 10_000_000;
        private const double NewlineThreshold = 0.5;
        private const double DefaultFontSize = 12.0;
        private const double TjSpaceThreshold = -100.0;

        private readonly PdfDocument _document;
        private readonly PdfFontRegistry _fonts;
        private readonly PdfDictionary? _resources;
        private readonly CancellationToken _cancellationToken;
        private readonly StringBuilder _builder = new();
        private readonly List<PdfFont?> _fontStack = new();

        private PdfFont? _currentFont;
        private double _fontSize = DefaultFontSize;
        private bool _inText;
        private bool _hasPosition;
        private double _lastX;
        private double _lastY;
        private bool _pendingSpace;

        public PageTextState(PdfDocument document, PdfFontRegistry fonts, PdfDictionary? resources,
            CancellationToken cancellationToken)
        {
            _document = document;
            _fonts = fonts;
            _resources = resources;
            _cancellationToken = cancellationToken;
        }

        public string Text
        {
            get
            {
                var text = _builder.ToString();
                return text.TrimEnd();
            }
        }

        public void Run(byte[] content)
        {
            var parser = new PdfObjectParser(content);
            var operands = new List<PdfObject>(8);
            var operatorCount = 0;

            while (!parser.AtEnd)
            {
                if ((++operatorCount & 0x3FF) == 0) _cancellationToken.ThrowIfCancellationRequested();
                if (operatorCount > MaxOperatorCount) break;

                var next = parser.PeekByte();
                if (next is null) break;

                if (IsObjectStart(next.Value))
                {
                    try
                    {
                        operands.Add(parser.ParseObject());
                    }
                    catch (PdfParseException)
                    {
                        break; // 内容流坏了：保留已提取的文字
                    }

                    if (operands.Count > MaxOperandCount) operands.Clear();
                    continue;
                }

                if (!parser.TryReadBareToken(out var token))
                {
                    // 孤立的 ']' / '>' 之类：跳一个字节，别死循环
                    parser.Position += 1;
                    continue;
                }

                try
                {
                    if (token == "BI")
                    {
                        parser.SkipInlineImage();
                    }
                    else
                    {
                        Execute(token, operands);
                    }
                }
                catch (PdfParseException)
                {
                    // 单个操作符坏了不影响后面的文字
                }

                operands.Clear();
            }
        }

        private static bool IsObjectStart(byte value)
            => value is (byte)'/' or (byte)'(' or (byte)'[' or (byte)'<'
                or (byte)'+' or (byte)'-' or (byte)'.'
                or >= (byte)'0' and <= (byte)'9';

        private void Execute(string token, List<PdfObject> operands)
        {
            switch (token)
            {
                case "BT":
                    _inText = true;
                    break;
                case "ET":
                    _inText = false;
                    break;
                case "Tf":
                    ApplyFont(operands);
                    break;
                case "Tj":
                    ShowLastString(operands);
                    break;
                case "'":
                    AppendNewline();
                    ShowLastString(operands);
                    break;
                case "\"":
                    AppendNewline();
                    ShowLastString(operands);
                    break;
                case "TJ":
                    ShowArray(LastOperand(operands) as PdfArray);
                    break;
                case "Td":
                case "TD":
                    ApplyTextPosition(operands);
                    break;
                case "Tm":
                    ApplyTextMatrix(operands);
                    break;
                case "T*":
                    AppendNewline();
                    break;
                case "q":
                    if (_fontStack.Count < MaxOperandCount) _fontStack.Add(_currentFont);
                    break;
                case "Q":
                    if (_fontStack.Count > 0)
                    {
                        _currentFont = _fontStack[^1];
                        _fontStack.RemoveAt(_fontStack.Count - 1);
                    }

                    break;
            }
        }

        private static PdfObject? LastOperand(List<PdfObject> operands)
            => operands.Count > 0 ? operands[^1] : null;

        private void ApplyFont(List<PdfObject> operands)
        {
            if (operands.Count < 2) return;
            if (operands[^1] is PdfNumber size && size.Value > 0) _fontSize = size.Value;
            if (operands[^2] is not PdfName fontName) return;

            _currentFont = _fonts.Resolve(_resources, fontName) ?? _fonts.ResolveFirst(_resources);
        }

        private void ShowLastString(List<PdfObject> operands)
        {
            if (LastOperand(operands) is PdfString text) ShowString(text.Bytes);
        }

        private void ShowArray(PdfArray? array)
        {
            if (array is null) return;

            foreach (var item in array.Items)
            {
                switch (_document.Resolve(item))
                {
                    case PdfString text:
                        ShowString(text.Bytes);
                        break;
                    case PdfNumber number when number.Value < TjSpaceThreshold:
                        // TJ 里的大负调整量相当于一个空格（中文字符之间会在拼接时自动去掉）
                        RequestSpace();
                        break;
                }
            }
        }

        private void ShowString(byte[] bytes)
        {
            if (bytes.Length == 0) return;

            var font = _currentFont ?? _fonts.ResolveFirst(_resources);
            var text = font?.Decode(bytes) ?? DecodeWithoutFont(bytes);
            AppendText(text);
        }

        /// <summary>没有可用字体时的兜底：按 WinAnsi / Latin1 出可读 ASCII，至少不丢页。</summary>
        private static string DecodeWithoutFont(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length);
            foreach (var code in bytes) builder.Append(PdfEncodings.DecodeWinAnsiByte(code));
            return builder.ToString();
        }

        private void ApplyTextPosition(List<PdfObject> operands)
        {
            if (operands.Count < 2) return;
            if (operands[^2] is not PdfNumber tx || operands[^1] is not PdfNumber ty) return;

            if (_builder.Length > 0)
            {
                if (Math.Abs(ty.Value) > NewlineThreshold)
                {
                    AppendNewline();
                }
                else if (IsSignificantHorizontalMove(tx.Value))
                {
                    // 中文字体常用 Td 逐字排版（每个字一个 Td），这种一两个字符宽的位移不能当空格
                    RequestSpace();
                }
            }
        }

        /// <summary>
        /// 水平位移算不算「词间空格」：和当前字号比较（Tf 的 size 与 Td 的 tx 在同一个文本空间里，
        /// 比值不受 Tm 缩放影响）。逐字排版大约 0.5~1.0 个字号，词间距才会明显更大。
        /// </summary>
        private bool IsSignificantHorizontalMove(double tx)
        {
            var size = _fontSize > 0 ? _fontSize : DefaultFontSize;
            return Math.Abs(tx) > size * 1.5;
        }

        private void ApplyTextMatrix(List<PdfObject> operands)
        {
            if (operands.Count < 6) return;
            if (operands[^2] is not PdfNumber x || operands[^1] is not PdfNumber y) return;

            if (_inText && _hasPosition && _builder.Length > 0)
            {
                if (Math.Abs(y.Value - _lastY) > NewlineThreshold)
                {
                    AppendNewline();
                }
                else if (IsSignificantHorizontalMove(x.Value - _lastX))
                {
                    RequestSpace();
                }
            }

            _lastX = x.Value;
            _lastY = y.Value;
            _hasPosition = true;
        }

        private void AppendText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '\n' or '\r':
                        AppendNewline();
                        continue;
                    case '\t' or ' ' or '\u00A0':
                        AppendSpace();
                        continue;
                }

                if (char.IsControl(ch)) continue;

                if (_pendingSpace) FlushPendingSpace(ch);
                _builder.Append(ch);
            }
        }

        /// <summary>记一个「待定空格」：真正决定加不加要等下一个字符出来（中文之间不加）。</summary>
        private void RequestSpace()
        {
            if (_builder.Length == 0) return;
            if (_builder[^1] is ' ' or '\n' or '\r' or '\t') return;
            _pendingSpace = true;
        }

        /// <summary>
        /// 冲刷待定空格：只有「两边都不是中日韩字符」时才真的插一个空格，
        /// 这样中文逐字排版不会变成「软 件 工 程」，而英文单词之间仍然保留空格。
        /// </summary>
        private void FlushPendingSpace(char nextChar)
        {
            _pendingSpace = false;
            if (_builder.Length == 0) return;

            var last = _builder[^1];
            if (last is ' ' or '\n' or '\r' or '\t') return;
            if (IsCjk(last) || IsCjk(nextChar)) return;
            _builder.Append(' ');
        }

        /// <summary>中日韩字符 / 全角标点（这些字符之间不需要空格）。</summary>
        private static bool IsCjk(char ch)
            => ch is >= '\u2E80' and <= '\u9FFF'   // 中日韩部首、假名、汉字
                or >= '\uF900' and <= '\uFAFF'     // 兼容汉字
                or >= '\uFE30' and <= '\uFE4F'     // 竖排标点
                or >= '\uFF00' and <= '\uFFEF'     // 全角字符 / 标点
                or >= '\u3000' and <= '\u303F';    // 中文标点

        private void AppendSpace()
        {
            _pendingSpace = false;
            if (_builder.Length == 0) return;
            var last = _builder[^1];
            if (last is ' ' or '\n' or '\r' or '\t') return;
            _builder.Append(' ');
        }

        private void AppendNewline()
        {
            _pendingSpace = false;
            if (_builder.Length == 0) return;

            while (_builder.Length > 0 && _builder[^1] is ' ' or '\t') _builder.Length--;

            if (_builder.Length == 0 || _builder[^1] == '\n') return;
            _builder.Append('\n');
        }
    }
}
