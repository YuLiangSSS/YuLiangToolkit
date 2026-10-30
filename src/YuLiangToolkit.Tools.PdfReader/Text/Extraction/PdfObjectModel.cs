namespace YuLiangToolkit.Tools.PdfReader.Text.Extraction;

/// <summary>
/// PDF 对象模型的极简实现（纯手写，不引任何第三方 PDF 库）。
///
/// <para>只覆盖「提取文字层 / 书签目录」用得到的对象类型：空、布尔、数字、字符串、名称、
/// 数组、字典、间接引用、流。所有对象都是不可变或只读的，解析器内部使用。</para>
/// </summary>
internal abstract class PdfObject
{
}

/// <summary>PDF 的空对象 <c>null</c>。</summary>
internal sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();

    private PdfNull()
    {
    }
}

/// <summary>PDF 布尔对象。</summary>
internal sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    public bool Value { get; }
}

/// <summary>PDF 数字（整数 / 实数）。</summary>
internal sealed class PdfNumber : PdfObject
{
    public PdfNumber(double value) => Value = value;

    public double Value { get; }

    /// <summary>是不是能安全转成 int 的整数。</summary>
    public bool IsInteger
        => Math.Abs(Value % 1) < 1e-9 && Value >= int.MinValue && Value <= int.MaxValue;

    public int IntValue => (int)Math.Round(Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>PDF 字符串（原始字节；编码由调用方按上下文决定：字体编码 / UTF-16BE / PDFDocEncoding）。</summary>
internal sealed class PdfString : PdfObject
{
    public PdfString(byte[] bytes) => Bytes = bytes;

    public byte[] Bytes { get; }

    /// <summary>调试用：把字节按 Latin1 粗看（不是最终解码结果）。</summary>
    public override string ToString() => System.Text.Encoding.Latin1.GetString(Bytes);
}

/// <summary>PDF 名称对象（已去掉开头的 <c>/</c>，<c>#xx</c> 已解码）。</summary>
internal sealed class PdfName : PdfObject
{
    public PdfName(string value) => Value = value;

    public string Value { get; }

    public bool Is(string name) => string.Equals(Value, name, StringComparison.Ordinal);

    public override string ToString() => "/" + Value;
}

/// <summary>PDF 数组。</summary>
internal sealed class PdfArray : PdfObject
{
    public List<PdfObject> Items { get; } = new();

    public int Count => Items.Count;

    public PdfObject? this[int index]
        => index >= 0 && index < Items.Count ? Items[index] : null;
}

/// <summary>PDF 间接引用（<c>N G R</c>）。</summary>
internal sealed class PdfReference : PdfObject
{
    public PdfReference(int objectNumber, int generation)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    public int ObjectNumber { get; }

    public int Generation { get; }

    public override string ToString() => $"{ObjectNumber} {Generation} R";
}

/// <summary>PDF 字典（键是名称，<c>/</c> 已去掉）。</summary>
internal sealed class PdfDictionary : PdfObject
{
    public Dictionary<string, PdfObject> Entries { get; } = new(StringComparer.Ordinal);

    public int Count => Entries.Count;

    public PdfObject? Get(string key) => Entries.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, PdfObject value) => Entries[key] = value;

    public string? GetName(string key) => Get(key) is PdfName name ? name.Value : null;

    public int? GetInt(string key) => Get(key) is PdfNumber number ? number.IntValue : null;

    public double? GetDouble(string key) => Get(key) is PdfNumber number ? number.Value : null;

    public PdfDictionary? GetDictionary(string key) => Get(key) as PdfDictionary;
}

/// <summary>
/// PDF 流对象：字典 + 原始字节（未解码）。
/// 解码（Flate 等）交给 <see cref="PdfDocument.GetStreamData"/> 惰性完成，解出来的结果会缓存。
/// </summary>
internal sealed class PdfStream : PdfObject
{
    public PdfStream(PdfDictionary dictionary) => Dictionary = dictionary;

    public PdfDictionary Dictionary { get; }

    /// <summary>从文件里切出来的原始字节（还没过过滤器）。</summary>
    public byte[] RawData { get; internal set; } = Array.Empty<byte>();

    /// <summary>解码后的字节；<c>null</c> = 还不确定解不解得开。</summary>
    public byte[]? DecodedData { get; internal set; }

    /// <summary>是否已经尝试过解码（失败后再问就不必重复解压）。</summary>
    public bool DecodeAttempted { get; internal set; }

    public override string ToString() => "stream";
}

/// <summary>解析 PDF 结构时遇到的致命错误（消息是给用户看的中文；提取器会转成空文字层的 Note）。</summary>
internal sealed class PdfParseException : Exception
{
    public PdfParseException(string message) : base(message)
    {
    }
}
