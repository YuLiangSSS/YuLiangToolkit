namespace YuLiangToolkit.Tools.PdfReader.Text.Extraction;

/// <summary>页树里的一页：页号（0 起）、页字典、生效的资源字典（/Resources 可继承）。</summary>
internal sealed record PdfPage(int Index, PdfDictionary Dictionary, PdfDictionary? Resources);

/// <summary>
/// 手写 PDF 文档结构解析器：文件头 / xref（传统表 + xref 流 + /Prev 链 + 混合 /XRefStm）/
/// 对象流 /ObjStm / 惰性对象缓存 / 页树 / 书签目录 / 命名目标。
///
/// <para>设计原则是「尽力而为」：结构坏了就退到全文扫描 <c>N G obj</c> 建对象表，
/// 单个对象解析失败只让那一个对象变 <c>null</c>，绝不把异常抛到提取器外面
/// （提取器最后还有一层 try/catch 兜底）。</para>
/// </summary>
internal sealed class PdfDocument
{
    /// <summary>文件大小上限：超过就不做文字提取（防止读一个巨型文件把内存吃满）。</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    private const int MaxObjects = 2_000_000;
    private const int MaxXrefSections = 64;
    private const int MaxScanObjects = 200_000;
    private const int MaxPages = 10_000;
    private const int MaxPageTreeDepth = 64;
    private const int MaxOutlineItems = 5_000;
    private const int MaxOutlineDepth = 32;
    private const int MaxCatalogScanObjects = 20_000;
    private const int MaxObjectStreams = 5_000;

    private readonly byte[] _data;
    private readonly Dictionary<int, XrefEntry> _xrefTable = new();
    private readonly Dictionary<int, PdfObject?> _objectCache = new();
    private readonly HashSet<int> _objectsInProgress = new();
    private readonly Dictionary<int, ObjectStreamInfo?> _objectStreams = new();
    private readonly HashSet<PdfStream> _streamsInProgress = new();
    private readonly List<PdfObject> _rootCandidates = new();
    private readonly Dictionary<int, int> _scanIndex = new();
    private readonly Dictionary<int, int> _pageIndexByObjectNumber = new();
    private readonly Dictionary<PdfDictionary, int> _pageIndexByDictionary = new();
    private readonly Dictionary<string, PdfObject> _namedDestinations = new(StringComparer.Ordinal);

    private PdfDictionary _trailer = new();
    private PdfDictionary? _catalog;
    private List<PdfPage>? _pages;
    private List<PdfOutlineItem>? _outline;
    private int _outlineItemCount;
    private bool _fullScanDone;
    private bool _scanIndexBuilt;
    private bool _rawTrailerRecovered;
    private bool _namedDestinationsLoaded;
    private CancellationToken _token;

    private sealed class XrefEntry
    {
        public int Type; // 0 = 空闲，1 = 文件偏移，2 = 在对象流里
        public long Offset;
        public int ObjectStream;
        public int IndexInStream;
    }

    private sealed class ObjectStreamInfo
    {
        public byte[] Data = Array.Empty<byte>();
        public int First;
        public readonly List<(int ObjectNumber, int Offset)> Objects = new();
    }

    private PdfDocument(byte[] data) => _data = data;

    /// <summary>解析入口：读文件 + 建结构；结构性失败抛 <see cref="PdfParseException"/>（消息可直接给用户看）。</summary>
    public static PdfDocument Open(string filePath, CancellationToken cancellationToken)
    {
        var data = ReadFileBytes(filePath, cancellationToken);
        var document = new PdfDocument(data);
        document.LoadStructure(cancellationToken);
        return document;
    }

    // ================================================================== 文件读取 / 结构加载

    private static byte[] ReadFileBytes(string filePath, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var length = stream.Length;
        if (length <= 0) throw new PdfParseException("PDF 文件是空的。");
        if (length > MaxFileBytes)
            throw new PdfParseException($"PDF 文件过大（{length / 1024 / 1024} MB），已跳过文字层提取。");

        var buffer = new byte[length];
        var total = 0;
        while (total < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, total, Math.Min(512 * 1024, buffer.Length - total));
            if (read <= 0) break;
            total += read;
        }

        return total == buffer.Length ? buffer : buffer[..total];
    }

    private void LoadStructure(CancellationToken cancellationToken)
    {
        _token = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

        if (!HasPdfHeader())
            throw new PdfParseException("不是有效的 PDF 文件（缺少 %PDF- 文件头）。");

        var xrefLoaded = TryLoadXref(cancellationToken);
        if (!xrefLoaded) DoFullObjectScan(overwriteExisting: true);

        if (!TryResolveRoot())
        {
            RecoverTrailerFromRawScan();
            if (!TryResolveRoot())
            {
                if (!_fullScanDone) DoFullObjectScan(overwriteExisting: !xrefLoaded);
                RecoverCatalogByObjectScan();
                TryResolveRoot();
            }
        }

        if (_catalog is null)
            throw new PdfParseException("找不到 PDF 的文档目录（Catalog），文件可能已损坏。");

        if (Resolve(_trailer.Get("Encrypt")) is not null)
            throw new PdfParseException("PDF 已加密，暂不支持文字层提取。");
    }

    private bool HasPdfHeader()
    {
        var limit = Math.Min(_data.Length, 1024);
        for (var i = 0; i + 4 <= limit; i++)
        {
            if (_data[i] == (byte)'%' && _data[i + 1] == (byte)'P' && _data[i + 2] == (byte)'D'
                && _data[i + 3] == (byte)'F' && _data[i + 4] == (byte)'-')
                return true;
        }

        return false;
    }

    // ================================================================== xref 解析

    private bool TryLoadXref(CancellationToken cancellationToken)
    {
        foreach (var offset in CollectStartXrefOffsets())
        {
            cancellationToken.ThrowIfCancellationRequested();
            _xrefTable.Clear();
            try
            {
                if (LoadXrefChain(offset, cancellationToken)) return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 这个 startxref 不行，试试文件里更早的一个；都不行就全文扫描
            }
        }

        _xrefTable.Clear();
        return false;
    }

    /// <summary>从文件末尾往前找 <c>startxref</c>，收集最多 8 个候选偏移（最近的在前）。</summary>
    private List<long> CollectStartXrefOffsets()
    {
        var result = new List<long>();
        var searchEnd = _data.Length;
        for (var attempt = 0; attempt < 8 && searchEnd > 0; attempt++)
        {
            var index = LastIndexOfKeyword("startxref", searchEnd);
            if (index < 0) break;

            var parser = new PdfObjectParser(_data, index + "startxref".Length);
            if (parser.TryReadInteger(out var offset) && offset >= 0) result.Add(offset);
            searchEnd = index;
        }

        return result;
    }

    /// <summary>顺着 startxref → /Prev 链把各版本 xref 合并进对象表（新版本优先）。</summary>
    private bool LoadXrefChain(long offset, CancellationToken cancellationToken)
    {
        var visited = new HashSet<long>();
        long? current = offset;
        var any = false;
        var guard = 0;

        while (current is { } position && guard++ < MaxXrefSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position < 0 || position >= _data.Length) break;
            if (!visited.Add(position)) break;

            if (!TryParseXrefSection(position, out var trailer, out var previous, out var hybridXrefStream))
                break;

            any = true;
            MergeTrailer(trailer);

            // 混合引用文件：传统 xref 表 + 一个 /XRefStm 指向的 xref 流（补压缩对象）
            if (hybridXrefStream is { } hybrid && hybrid >= 0 && hybrid < _data.Length && !visited.Contains(hybrid))
            {
                try
                {
                    if (TryParseXrefStream((int)hybrid, out var hybridTrailer)) MergeTrailerMissing(hybridTrailer);
                }
                catch (Exception)
                {
                    // 混合 xref 流坏了不影响主表
                }
            }

            current = previous >= 0 ? previous : null;
        }

        return any;
    }

    /// <summary>解析一节 xref：先试传统表，再试 xref 流；偏移有小偏差时在附近找。</summary>
    private bool TryParseXrefSection(long offset, out PdfDictionary? trailer, out long previousOffset,
        out long? xrefStreamOffset)
    {
        trailer = null;
        previousOffset = -1;
        xrefStreamOffset = null;

        foreach (var candidate in CandidateOffsets(offset))
        {
            if (candidate < 0 || candidate >= _data.Length) continue;

            var parser = new PdfObjectParser(_data, (int)candidate);
            if (parser.MatchesKeyword("xref"))
            {
                if (!TryParseTraditionalXref(parser, out trailer)) continue;
                previousOffset = ReadTrailerNumber(trailer, "Prev");
                xrefStreamOffset = ReadTrailerNumber(trailer, "XRefStm");
                return true;
            }

            if (TryParseXrefStream((int)candidate, out trailer))
            {
                previousOffset = ReadTrailerNumber(trailer, "Prev");
                return true;
            }
        }

        return false;
    }

    /// <summary>候选偏移：本身、±1，以及往后 64 字节内出现 <c>xref</c> 的位置。</summary>
    private IEnumerable<long> CandidateOffsets(long offset)
    {
        yield return offset;
        if (offset > 0) yield return offset - 1;
        if (offset + 1 < _data.Length) yield return offset + 1;

        var start = (int)Math.Clamp(offset + 2, 0, Math.Max(0, _data.Length - 1));
        var end = (int)Math.Min((long)_data.Length, offset + 64);
        for (var i = start; i + 4 <= end; i++)
        {
            if (_data[i] == (byte)'x' && MatchesKeywordAt(i, "xref")) yield return i;
        }
    }

    private bool TryParseTraditionalXref(PdfObjectParser parser, out PdfDictionary? trailer)
    {
        trailer = null;
        if (!parser.TryReadKeyword("xref")) return false;

        var sectionGuard = 0;
        while (true)
        {
            parser.SkipWhitespaceAndComments();
            if (parser.AtEnd) return false;

            if (parser.MatchesKeyword("trailer"))
            {
                parser.TryReadKeyword("trailer");
                try
                {
                    if (parser.ParseObject() is not PdfDictionary dictionary) return false;
                    trailer = dictionary;
                    return true;
                }
                catch (PdfParseException)
                {
                    return false;
                }
            }

            if (!parser.TryReadInteger(out var startObject)) return false;
            if (!parser.TryReadInteger(out var count)) return false;
            if (count < 0 || count > MaxObjects) return false;

            for (var i = 0; i < count; i++)
            {
                if (!parser.TryReadInteger(out var firstField)) return false;
                if (!parser.TryReadInteger(out var secondField)) return false;
                if (!parser.TryReadBareToken(out var flag)) return false;

                var objectNumber = startObject + i;
                if (flag == "n")
                {
                    AddXrefEntry(objectNumber, new XrefEntry
                    {
                        Type = 1,
                        Offset = firstField,
                    });
                }
                else if (flag == "f")
                {
                    AddXrefEntry(objectNumber, new XrefEntry { Type = 0 });
                }
                else
                {
                    return false;
                }
            }

            if (++sectionGuard > 10_000) return false;
        }
    }

    private bool TryParseXrefStream(int offset, out PdfDictionary? trailer)
    {
        trailer = null;
        var parser = new PdfObjectParser(_data, offset);
        if (!parser.TryParseIndirectObject(out _, out _, out var value)) return false;
        if (value is not PdfStream stream) return false;

        var dictionary = stream.Dictionary;
        if (!string.Equals(dictionary.GetName("Type"), "XRef", StringComparison.Ordinal)) return false;

        var data = GetStreamData(stream);
        if (data is null) return false;

        if (dictionary.Get("W") is not PdfArray widths || widths.Count < 3) return false;
        if (ResolveNumber(widths[0]) is not { } width0) return false;
        if (ResolveNumber(widths[1]) is not { } width1) return false;
        if (ResolveNumber(widths[2]) is not { } width2) return false;
        if (width0 is < 0 or > 8 || width1 is < 0 or > 8 || width2 is < 0 or > 8) return false;
        if (width0 + width1 + width2 <= 0) return false;

        var entrySize = (int)(width0 + width1 + width2);
        var segments = new List<(int Start, int Count)>();
        if (dictionary.Get("Index") is PdfArray indexArray)
        {
            for (var i = 0; i + 1 < indexArray.Count; i += 2)
            {
                if (ResolveNumber(indexArray[i]) is not { } segmentStart) continue;
                if (ResolveNumber(indexArray[i + 1]) is not { } segmentCount) continue;
                if (segmentStart < 0 || segmentCount < 0 || segmentStart > MaxObjects) continue;
                var clampedCount = (int)Math.Min(segmentCount, MaxObjects - segmentStart);
                segments.Add(((int)segmentStart, clampedCount));
            }
        }
        else
        {
            var size = dictionary.GetInt("Size") ?? (entrySize > 0 ? data.Length / entrySize : 0);
            segments.Add((0, Math.Min(size, MaxObjects)));
        }

        if (segments.Count == 0) return false;

        var position = 0;
        foreach (var (segmentStart, segmentCount) in segments)
        {
            for (var i = 0; i < segmentCount; i++)
            {
                if (position + entrySize > data.Length) break;
                var type = width0 == 0 ? 1L : ReadBigEndian(data, position, (int)width0);
                var second = ReadBigEndian(data, position + (int)width0, (int)width1);
                var third = ReadBigEndian(data, position + (int)width0 + (int)width1, (int)width2);
                position += entrySize;

                var objectNumber = segmentStart + i;
                if (type == 0)
                {
                    AddXrefEntry(objectNumber, new XrefEntry { Type = 0 });
                }
                else if (type == 1)
                {
                    if (second < 0 || second >= _data.Length) continue;
                    AddXrefEntry(objectNumber, new XrefEntry { Type = 1, Offset = second });
                }
                else if (type == 2)
                {
                    if (second <= 0 || second > MaxObjects || third < 0 || third > MaxObjects) continue;
                    AddXrefEntry(objectNumber, new XrefEntry
                    {
                        Type = 2,
                        ObjectStream = (int)second,
                        IndexInStream = (int)third,
                    });
                }
            }
        }

        trailer = dictionary;
        return true;
    }

    private static long ReadBigEndian(byte[] data, int position, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++) value = (value << 8) | data[position + i];
        return value;
    }

    private long? ResolveNumber(PdfObject? value) => Resolve(value) is PdfNumber number ? (long)number.Value : null;

    private static long ReadTrailerNumber(PdfDictionary? trailer, string key)
        => trailer?.Get(key) is PdfNumber number ? (long)number.Value : -1;

    private void AddXrefEntry(int objectNumber, XrefEntry entry)
    {
        if (objectNumber < 0 || objectNumber > MaxObjects) return;
        _xrefTable.TryAdd(objectNumber, entry); // 新版本先解析 → 先到先得
    }

    /// <summary>合并 trailer（最新版本优先：缺的键才补）。</summary>
    private void MergeTrailer(PdfDictionary? trailer)
    {
        if (trailer is null) return;
        foreach (var key in new[] { "Root", "Encrypt", "Info", "ID", "Size" })
        {
            var value = trailer.Get(key);
            if (value is null) continue;
            if (key == "Encrypt") _trailer.Set(key, value);
            else if (!_trailer.Entries.ContainsKey(key)) _trailer.Set(key, value);
        }

        AddRootCandidate(trailer.Get("Root"));
    }

    /// <summary>合并混合 xref 流里的 trailer（只补缺失键）。</summary>
    private void MergeTrailerMissing(PdfDictionary? trailer)
    {
        if (trailer is null) return;
        foreach (var key in new[] { "Root", "Encrypt", "Info", "ID", "Size" })
        {
            var value = trailer.Get(key);
            if (value is null) continue;
            if (!_trailer.Entries.ContainsKey(key)) _trailer.Set(key, value);
        }

        AddRootCandidate(trailer.Get("Root"));
    }

    // ================================================================== 目录 / 根对象恢复

    private void AddRootCandidate(PdfObject? value)
    {
        switch (value)
        {
            case PdfReference reference:
                if (!_rootCandidates.OfType<PdfReference>()
                        .Any(item => item.ObjectNumber == reference.ObjectNumber))
                    _rootCandidates.Add(reference);
                break;
            case PdfDictionary dictionary:
                if (!_rootCandidates.Contains(dictionary)) _rootCandidates.Add(dictionary);
                break;
        }
    }

    private bool TryResolveRoot()
    {
        if (_catalog is not null) return true;

        var candidates = new List<PdfObject>();
        var trailerRoot = _trailer.Get("Root");
        if (trailerRoot is not null) candidates.Add(trailerRoot);
        candidates.AddRange(_rootCandidates);

        foreach (var candidate in candidates)
        {
            _token.ThrowIfCancellationRequested();
            if (Resolve(candidate) is not PdfDictionary dictionary) continue;
            if (string.Equals(dictionary.GetName("Type"), "Catalog", StringComparison.Ordinal)
                || dictionary.Get("Pages") is not null)
            {
                _catalog = dictionary;
                return true;
            }
        }

        return false;
    }

    /// <summary>xref 坏掉时的兜底：从文件末尾往前找 <c>trailer</c> 字典 / <c>/Root</c> 引用。</summary>
    private void RecoverTrailerFromRawScan()
    {
        if (_rawTrailerRecovered) return;
        _rawTrailerRecovered = true;

        var searchEnd = _data.Length;
        for (var attempt = 0; attempt < 8 && searchEnd > 0; attempt++)
        {
            var index = LastIndexOfKeyword("trailer", searchEnd);
            if (index < 0) break;

            try
            {
                var parser = new PdfObjectParser(_data, index + "trailer".Length);
                if (parser.ParseObject() is PdfDictionary dictionary) MergeTrailerMissing(dictionary);
            }
            catch (Exception)
            {
                // 这个 trailer 坏了，继续往前找
            }

            searchEnd = index;
        }

        searchEnd = _data.Length;
        for (var attempt = 0; attempt < 8 && searchEnd > 0; attempt++)
        {
            var index = LastIndexOfRootKeyword(searchEnd);
            if (index < 0) break;
            searchEnd = index;

            try
            {
                var parser = new PdfObjectParser(_data, index + "/Root".Length);
                var value = parser.ParseObject();
                AddRootCandidate(value);
            }
            catch (Exception)
            {
                // 跳过
            }
        }
    }

    /// <summary>对象表已经有了（xref 或全文扫描）但找不到 Catalog 时：逐个对象找 /Type /Catalog。</summary>
    private void RecoverCatalogByObjectScan()
    {
        if (_catalog is not null) return;

        var count = 0;
        foreach (var objectNumber in _xrefTable.Keys.OrderBy(number => number))
        {
            _token.ThrowIfCancellationRequested();
            if (++count > MaxCatalogScanObjects) break;

            var dictionary = Resolve(GetObject(objectNumber)) as PdfDictionary;
            if (dictionary is null) continue;
            if (string.Equals(dictionary.GetName("Type"), "Catalog", StringComparison.Ordinal)
                && dictionary.Get("Pages") is not null)
            {
                AddRootCandidate(dictionary);
                return;
            }
        }
    }

    // ================================================================== 对象读取 / 缓存

    /// <summary>取一个间接对象（带缓存）；不存在 / 解析失败返回 <c>null</c>。</summary>
    public PdfObject? GetObject(int objectNumber)
    {
        if (objectNumber <= 0) return null;
        if (_objectCache.TryGetValue(objectNumber, out var cached)) return cached;
        if (!_objectsInProgress.Add(objectNumber)) return null; // 循环引用，先返回 null 打破环

        try
        {
            PdfObject? value = null;
            var hasEntry = _xrefTable.TryGetValue(objectNumber, out var entry);
            if (hasEntry && entry is not null)
            {
                value = entry.Type switch
                {
                    1 => ParseIndirectObjectAt(entry.Offset, objectNumber),
                    2 => GetObjectFromObjectStream(entry.ObjectStream, entry.IndexInStream, objectNumber),
                    _ => null,
                };
            }

            // 自愈：xref 偏移不对（或对象号不在表里）→ 全文扫描一次，用扫到的偏移再试
            if (value is null && (!hasEntry || (entry is not null && entry.Type == 1)))
            {
                EnsureScanIndex();
                if (_scanIndex.TryGetValue(objectNumber, out var scannedOffset))
                    value = ParseIndirectObjectAt(scannedOffset, objectNumber);
            }

            _objectCache[objectNumber] = value;
            return value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _objectCache[objectNumber] = null;
            return null;
        }
        finally
        {
            _objectsInProgress.Remove(objectNumber);
        }
    }

    private PdfObject? ParseIndirectObjectAt(long offset, int expectedObjectNumber)
    {
        foreach (var candidate in new[] { offset, offset + 1, offset - 1, offset + 2, offset - 2 })
        {
            if (candidate < 0 || candidate >= _data.Length) continue;
            try
            {
                var parser = new PdfObjectParser(_data, (int)candidate);
                if (!parser.TryParseIndirectObject(out var objectNumber, out _, out var value)) continue;
                if (objectNumber == expectedObjectNumber) return value;
            }
            catch (Exception)
            {
                // 偏移不对，试下一个
            }
        }

        return null;
    }

    private PdfObject? GetObjectFromObjectStream(int streamObjectNumber, int indexInStream,
        int expectedObjectNumber)
    {
        var info = GetObjectStreamInfo(streamObjectNumber);
        if (info is null) return null;

        var relativeOffset = -1;
        if (indexInStream >= 0 && indexInStream < info.Objects.Count
            && info.Objects[indexInStream].ObjectNumber == expectedObjectNumber)
        {
            relativeOffset = info.Objects[indexInStream].Offset;
        }

        if (relativeOffset < 0)
        {
            foreach (var (number, offset) in info.Objects)
            {
                if (number != expectedObjectNumber) continue;
                relativeOffset = offset;
                break;
            }
        }

        if (relativeOffset < 0) return null;
        var position = info.First + relativeOffset;
        if (position < 0 || position >= info.Data.Length) return null;

        return PdfObjectParser.TryParseObjectAt(info.Data, position, info.Data.Length, out _);
    }

    private ObjectStreamInfo? GetObjectStreamInfo(int streamObjectNumber)
    {
        if (_objectStreams.TryGetValue(streamObjectNumber, out var cached)) return cached;
        if (_objectStreams.Count > MaxObjectStreams) return null;

        ObjectStreamInfo? info = null;
        try
        {
            if (GetObject(streamObjectNumber) is PdfStream stream)
            {
                var dictionary = stream.Dictionary;
                var isObjectStream = string.Equals(dictionary.GetName("Type"), "ObjStm", StringComparison.Ordinal)
                    || dictionary.Get("N") is not null;
                var data = isObjectStream ? GetStreamData(stream) : null;
                if (data is not null)
                {
                    var count = dictionary.GetInt("N") ?? 0;
                    var first = dictionary.GetInt("First") ?? 0;
                    if (count is > 0 and <= 1_000_000 && first >= 0 && first <= data.Length)
                    {
                        var candidate = new ObjectStreamInfo { Data = data, First = first };
                        var headerParser = new PdfObjectParser(data, 0, first);
                        for (var i = 0; i < count; i++)
                        {
                            if (!headerParser.TryReadInteger(out var objectNumber)) break;
                            if (!headerParser.TryReadInteger(out var offset)) break;
                            if (offset < 0 || first + offset >= data.Length) continue;
                            candidate.Objects.Add((objectNumber, offset));
                        }

                        if (candidate.Objects.Count > 0) info = candidate;
                    }
                }
            }
        }
        catch (Exception)
        {
            info = null;
        }

        _objectStreams[streamObjectNumber] = info;
        return info;
    }

    /// <summary>解间接引用（最多 16 层，防止引用环）。</summary>
    public PdfObject? Resolve(PdfObject? value)
    {
        for (var i = 0; i < 16; i++)
        {
            if (value is not PdfReference reference) break;
            value = GetObject(reference.ObjectNumber);
        }

        return value is null or PdfNull ? null : value;
    }

    public PdfDictionary? ResolveDictionary(PdfObject? value) => Resolve(value) as PdfDictionary;

    public PdfArray? ResolveArray(PdfObject? value) => Resolve(value) as PdfArray;

    /// <summary>取流解码后的字节（带缓存 + 循环保护）；失败返回 <c>null</c>。</summary>
    public byte[]? GetStreamData(PdfStream stream)
    {
        if (stream.DecodedData is not null) return stream.DecodedData;
        if (stream.DecodeAttempted) return null;

        stream.DecodeAttempted = true;
        if (!_streamsInProgress.Add(stream)) return null;
        try
        {
            stream.DecodedData = PdfStreamDecoder.Decode(this, stream);
        }
        catch (Exception)
        {
            stream.DecodedData = null;
        }
        finally
        {
            _streamsInProgress.Remove(stream);
        }

        return stream.DecodedData;
    }

    // ================================================================== 页树

    /// <summary>按页树深度优先顺序取所有页（页号 = 遍历顺序，0 起）。</summary>
    public IReadOnlyList<PdfPage> GetPages(CancellationToken cancellationToken)
    {
        if (_pages is not null) return _pages;
        _token = cancellationToken;

        var pages = new List<PdfPage>();
        var visited = new HashSet<int>();
        AddPagesFromNode(GetCatalogValue("Pages"), inheritedResources: null, level: 0, pages, visited);

        if (pages.Count == 0)
        {
            // 页树坏了：退到「扫描对象表找 /Type /Page」
            foreach (var objectNumber in _xrefTable.Keys.OrderBy(number => number))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pages.Count >= MaxPages) break;
                if (Resolve(GetObject(objectNumber)) is not PdfDictionary dictionary) continue;
                if (!string.Equals(dictionary.GetName("Type"), "Page", StringComparison.Ordinal))
                    continue;
                AddPage(dictionary, objectNumber, ResolveDictionary(dictionary.Get("Resources")), pages);
            }
        }

        _pages = pages;
        return pages;
    }

    private PdfObject? GetCatalogValue(string key) => _catalog?.Get(key);

    private void AddPagesFromNode(PdfObject? nodeObject, PdfDictionary? inheritedResources, int level,
        List<PdfPage> pages, HashSet<int> visited)
    {
        if (pages.Count >= MaxPages || level > MaxPageTreeDepth) return;

        var objectNumber = 0;
        if (nodeObject is PdfReference reference)
        {
            objectNumber = reference.ObjectNumber;
            if (!visited.Add(objectNumber)) return; // 环 / 重复引用
        }

        if (Resolve(nodeObject) is not PdfDictionary node) return;

        var resources = ResolveDictionary(node.Get("Resources")) ?? inheritedResources;
        var type = node.GetName("Type");
        var kids = ResolveArray(node.Get("Kids"));

        // 有 /Kids 且不是 /Page：按页树节点递归（有的文件不写 /Type /Pages，所以不能只看 Type）
        if (kids is not null && !string.Equals(type, "Page", StringComparison.Ordinal))
        {
            foreach (var kid in kids.Items)
            {
                AddPagesFromNode(kid, resources, level + 1, pages, visited);
                if (pages.Count >= MaxPages) return;
            }

            return;
        }

        if (string.Equals(type, "Page", StringComparison.Ordinal)
            || node.Get("Contents") is not null
            || node.Get("MediaBox") is not null)
        {
            AddPage(node, objectNumber, resources, pages);
        }
    }

    private void AddPage(PdfDictionary dictionary, int objectNumber, PdfDictionary? resources,
        List<PdfPage> pages)
    {
        var index = pages.Count;
        pages.Add(new PdfPage(index, dictionary, resources));
        _pageIndexByDictionary.TryAdd(dictionary, index);
        if (objectNumber > 0) _pageIndexByObjectNumber.TryAdd(objectNumber, index);
    }

    // ================================================================== 书签目录

    /// <summary>解析 /Root /Outlines 书签目录（按文档顺序；解析失败返回已解析部分，不抛异常）。</summary>
    public IReadOnlyList<PdfOutlineItem> GetOutline(CancellationToken cancellationToken)
    {
        if (_outline is not null) return _outline;
        _token = cancellationToken;

        var items = new List<PdfOutlineItem>();
        try
        {
            if (GetPages(cancellationToken).Count > 0 || _pageIndexByDictionary.Count > 0)
            {
                var outlines = ResolveDictionary(GetCatalogValue("Outlines"));
                if (outlines is not null)
                {
                    EnsureNamedDestinations();
                    WalkOutlineItems(outlines.Get("First"), level: 1, items);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 目录坏了不影响文字层
        }

        _outline = items;
        return items;
    }

    private void WalkOutlineItems(PdfObject? firstObject, int level, List<PdfOutlineItem> items)
    {
        if (level > MaxOutlineDepth) return;

        var visited = new HashSet<int>();
        var visitedDictionaries = new HashSet<PdfDictionary>();
        var current = firstObject;

        while (current is not null
               && _outlineItemCount < MaxOutlineItems
               && items.Count < MaxOutlineItems)
        {
            _token.ThrowIfCancellationRequested();
            _outlineItemCount++;

            if (current is PdfReference reference)
            {
                if (!visited.Add(reference.ObjectNumber)) break; // 兄弟链成环
            }

            if (Resolve(current) is not PdfDictionary item) break;
            if (!visitedDictionaries.Add(item)) break;

            var title = DecodeTextString(item.Get("Title"));
            var target = ResolveOutlineTarget(item);
            if (title.Length > 0 && target is not null)
                items.Add(new PdfOutlineItem(title, target.Value, level));

            var child = item.Get("First");
            if (child is not null) WalkOutlineItems(child, level + 1, items);

            current = item.Get("Next");
        }
    }

    private int? ResolveOutlineTarget(PdfDictionary item)
    {
        var destination = item.Get("Dest");
        if (destination is null)
        {
            var action = ResolveDictionary(item.Get("A"));
            var actionType = action?.GetName("S");
            if (string.Equals(actionType, "GoTo", StringComparison.Ordinal))
                destination = action?.Get("D");
        }

        return ResolveDestination(destination, depth: 0);
    }

    private int? ResolveDestination(PdfObject? destination, int depth)
    {
        if (destination is null || depth > 8) return null;

        if (destination is PdfReference reference)
        {
            // 先看它是不是直接指向某页
            if (_pageIndexByObjectNumber.TryGetValue(reference.ObjectNumber, out var directIndex))
                return directIndex;

            destination = GetObject(reference.ObjectNumber);
        }

        switch (destination)
        {
            case PdfArray array:
            {
                var first = array.Items.Count > 0 ? array.Items[0] : null;
                switch (first)
                {
                    case PdfReference pageReference:
                        if (_pageIndexByObjectNumber.TryGetValue(pageReference.ObjectNumber, out var index))
                            return index;
                        if (Resolve(pageReference) is PdfDictionary pageDictionary
                            && _pageIndexByDictionary.TryGetValue(pageDictionary, out var dictionaryIndex))
                            return dictionaryIndex;
                        return null;
                    case PdfNumber number:
                    {
                        var pageNumber = number.IntValue;
                        return pageNumber >= 0 ? pageNumber : null;
                    }
                    case PdfName or PdfString:
                        return ResolveNamedDestination(first, depth + 1);
                    default:
                        return null;
                }
            }

            case PdfName or PdfString:
                return ResolveNamedDestination(destination, depth + 1);

            case PdfDictionary dictionary:
                return ResolveDestination(dictionary.Get("D"), depth + 1);

            default:
                return null;
        }
    }

    private int? ResolveNamedDestination(PdfObject? nameObject, int depth)
    {
        if (depth > 8) return null;
        var key = GetDestinationName(nameObject);
        if (key.Length == 0) return null;

        EnsureNamedDestinations();
        if (!_namedDestinations.TryGetValue(key, out var value)) return null;

        if (value is PdfDictionary dictionary && dictionary.Get("D") is not null)
            return ResolveDestination(dictionary.Get("D"), depth + 1);
        return ResolveDestination(value, depth + 1);
    }

    private static string GetDestinationName(PdfObject? value)
        => value switch
        {
            PdfName name => name.Value,
            PdfString text => PdfEncodings.DecodeTextString(text.Bytes),
            _ => "",
        };

    private void EnsureNamedDestinations()
    {
        if (_namedDestinationsLoaded) return;
        _namedDestinationsLoaded = true;

        try
        {
            // 老式：catalog /Dests 字典（名字 → 目标）
            var destinations = ResolveDictionary(GetCatalogValue("Dests"));
            if (destinations is not null)
            {
                foreach (var (key, value) in destinations.Entries)
                    _namedDestinations.TryAdd(key, value);
            }

            // 新式：catalog /Names /Dests 名字树
            var names = ResolveDictionary(GetCatalogValue("Names"));
            if (names?.Get("Dests") is not null)
                AddNameTree(names.Get("Dests"), depth: 0);
        }
        catch (Exception)
        {
            // 命名目标坏了不影响其它书签
        }
    }

    private void AddNameTree(PdfObject? nodeObject, int depth)
    {
        if (depth > 16) return;
        var node = ResolveDictionary(nodeObject);
        if (node is null) return;

        var names = ResolveArray(node.Get("Names"));
        if (names is not null)
        {
            for (var i = 0; i + 1 < names.Count; i += 2)
            {
                var key = GetDestinationName(names[i]);
                var target = names[i + 1];
                if (key.Length > 0 && target is not null) _namedDestinations.TryAdd(key, target);
            }
        }

        var kids = ResolveArray(node.Get("Kids"));
        if (kids is not null)
        {
            foreach (var kid in kids.Items) AddNameTree(kid, depth + 1);
        }
    }

    private string DecodeTextString(PdfObject? value)
    {
        var resolved = Resolve(value);
        return resolved switch
        {
            PdfString text => PdfEncodings.DecodeTextString(text.Bytes),
            PdfName name => name.Value,
            _ => "",
        };
    }

    // ================================================================== 全文扫描兜底

    private void DoFullObjectScan(bool overwriteExisting)
    {
        if (_fullScanDone) return;
        _fullScanDone = true;
        ScanObjectHeaders(overwriteExisting);
    }

    private void EnsureScanIndex()
    {
        if (_scanIndexBuilt) return;
        _scanIndexBuilt = true;
        ScanObjectHeaders(overwriteExisting: false);
    }

    /// <summary>全文扫描 <c>N G obj</c>，把对象号 → 文件偏移记下来（xref 坏掉时的唯一兜底）。</summary>
    private void ScanObjectHeaders(bool overwriteExisting)
    {
        var length = _data.Length;
        var position = 0;
        var count = 0;

        while (position < length && count < MaxScanObjects)
        {
            if (_data[position] is < (byte)'0' or > (byte)'9')
            {
                position++;
                continue;
            }

            var headerStart = position;
            long objectNumber = 0;
            while (position < length && _data[position] is >= (byte)'0' and <= (byte)'9')
            {
                objectNumber = objectNumber * 10 + (_data[position] - '0');
                if (objectNumber > MaxObjects) break;
                position++;
            }

            if (objectNumber is <= 0 or > MaxObjects) continue;

            // 「数字 空白 数字 空白 obj」才算对象头；不满足就跳过当前数字继续找
            var whitespaceStart = position;
            while (position < length && PdfObjectParser.IsWhitespace(_data[position])) position++;
            if (position == whitespaceStart) continue;

            var generationStart = position;
            while (position < length && _data[position] is >= (byte)'0' and <= (byte)'9') position++;
            if (position == generationStart) continue;

            whitespaceStart = position;
            while (position < length && PdfObjectParser.IsWhitespace(_data[position])) position++;
            if (position == whitespaceStart) continue;

            if (!MatchesKeywordAt(position, "obj"))
            {
                position = generationStart;
                continue;
            }

            position += "obj".Length;
            var number = (int)objectNumber;
            _scanIndex[number] = headerStart;
            if (overwriteExisting || !_xrefTable.ContainsKey(number))
            {
                _xrefTable[number] = new XrefEntry { Type = 1, Offset = headerStart };
            }

            count++;
        }
    }

    // ================================================================== 字节搜索小工具

    /// <summary>在 [0, before) 里从后往前找关键字，返回起始位置。</summary>
    private int LastIndexOfKeyword(string keyword, int before)
    {
        if (keyword.Length == 0) return -1;
        var end = Math.Min(before - keyword.Length, _data.Length - keyword.Length);
        for (var i = end; i >= 0; i--)
        {
            if (_data[i] != (byte)keyword[0]) continue;
            if (MatchesKeywordAt(i, keyword)) return i;
        }

        return -1;
    }

    /// <summary>从后往前找 <c>/Root</c>（带名称边界）。</summary>
    private int LastIndexOfRootKeyword(int before)
    {
        var end = Math.Min(before - 5, _data.Length - 5);
        for (var i = end; i >= 0; i--)
        {
            if (_data[i] != (byte)'/' || _data[i + 1] != (byte)'R' || _data[i + 2] != (byte)'o'
                || _data[i + 3] != (byte)'o' || _data[i + 4] != (byte)'t')
                continue;

            var after = i + 5;
            if (after >= _data.Length || PdfObjectParser.IsWhitespace(_data[after])
                || PdfObjectParser.IsDelimiter(_data[after]))
                return i;
        }

        return -1;
    }

    /// <summary>精确位置匹配关键字（关键字后必须是边界）。</summary>
    private bool MatchesKeywordAt(int position, string keyword)
    {
        if (position < 0 || position + keyword.Length > _data.Length) return false;
        for (var i = 0; i < keyword.Length; i++)
        {
            if (_data[position + i] != (byte)keyword[i]) return false;
        }

        var after = position + keyword.Length;
        return after >= _data.Length || PdfObjectParser.IsWhitespace(_data[after])
               || PdfObjectParser.IsDelimiter(_data[after]);
    }
}
