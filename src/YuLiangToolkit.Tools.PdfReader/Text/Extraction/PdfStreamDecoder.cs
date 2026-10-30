using System.IO.Compression;

namespace YuLiangToolkit.Tools.PdfReader.Text.Extraction;

/// <summary>
/// PDF 流的解码器：只实现提取文字用得到的过滤器 —— <c>FlateDecode</c>（zlib，失败再试裸 deflate）
/// 和「没有过滤器」；另外实现 xref 流偶尔会用的 PNG 预测器（<c>/Predictor 10~15</c>）。
///
/// <para>防御性上限：单条流解压结果不超过 <see cref="MaxDecodedBytes"/>（防解压炸弹）；
/// 过滤器不支持 / 数据坏了就返回 <c>null</c>，由调用方跳过这一条流，绝不抛到外面。</para>
/// </summary>
internal static class PdfStreamDecoder
{
    /// <summary>单条流解压后的最大字节数（防解压炸弹）。</summary>
    public const int MaxDecodedBytes = 64 * 1024 * 1024;

    private const int CopyBufferSize = 64 * 1024;

    /// <summary>解一条流；返回 <c>null</c> 表示不支持或解码失败。</summary>
    public static byte[]? Decode(PdfDocument document, PdfStream stream)
    {
        var filters = GetFilters(document, stream.Dictionary);
        if (filters is null) return null;

        var parms = GetDecodeParms(document, stream.Dictionary);
        var data = stream.RawData;

        for (var i = 0; i < filters.Count; i++)
        {
            var parm = i < parms.Count ? parms[i] : null;
            switch (NormalizeFilterName(filters[i]))
            {
                case "FlateDecode":
                    data = FlateDecode(data);
                    break;
                case "":
                    break;
                default:
                    // 图片类过滤器（DCT/JPX/CCITT）等：提取文字用不到，按不支持处理
                    return null;
            }

            if (data is null) return null;
            data = ApplyPredictor(data, parm);
            if (data is null) return null;
        }

        return data;
    }

    /// <summary>读 /Filter（名称或数组）；不认识的滤镜像未知也算失败。</summary>
    private static List<string>? GetFilters(PdfDocument document, PdfDictionary dictionary)
    {
        var result = new List<string>();
        var filter = document.Resolve(dictionary.Get("Filter"));
        switch (filter)
        {
            case null:
                return result;
            case PdfName name:
                result.Add(name.Value);
                return result;
            case PdfArray array:
                foreach (var item in array.Items)
                {
                    if (document.Resolve(item) is PdfName n) result.Add(n.Value);
                    else return null;
                }

                return result;
            default:
                return null;
        }
    }

    /// <summary>读 /DecodeParms（单个字典或数组，与 /Filter 一一对应）。</summary>
    private static List<PdfDictionary?> GetDecodeParms(PdfDocument document, PdfDictionary dictionary)
    {
        var result = new List<PdfDictionary?>();
        var parms = document.Resolve(dictionary.Get("DecodeParms")) ?? document.Resolve(dictionary.Get("DP"));
        switch (parms)
        {
            case null:
                return result;
            case PdfDictionary single:
                result.Add(single);
                return result;
            case PdfArray array:
                foreach (var item in array.Items) result.Add(document.ResolveDictionary(item));
                return result;
            default:
                return result;
        }
    }

    /// <summary>过滤器缩写（Fl / AHx / LZW / DCT …）归一成标准名。</summary>
    private static string NormalizeFilterName(string name)
        => name switch
        {
            "Fl" => "FlateDecode",
            "AHx" => "ASCIIHexDecode",
            "A85" => "ASCII85Decode",
            "LZW" => "LZWDecode",
            "DCT" => "DCTDecode",
            "CCF" => "CCITTFaxDecode",
            "RL" => "RunLengthDecode",
            _ => name,
        };

    /// <summary>Flate：先按 zlib（带 2 字节头）解，失败再按裸 deflate 解。</summary>
    private static byte[]? FlateDecode(byte[] data)
    {
        if (data.Length == 0) return Array.Empty<byte>();
        return TryDecompress(data, zlib: true) ?? TryDecompress(data, zlib: false);
    }

    private static byte[]? TryDecompress(byte[] data, bool zlib)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using Stream decoder = zlib
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(Math.Min(data.Length * 2, 1024 * 1024));
            var buffer = new byte[CopyBufferSize];
            while (true)
            {
                var read = decoder.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                if (output.Length + read > MaxDecodedBytes)
                    return null; // 解压炸弹 / 异常文件：放弃这条流
                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (Exception)
        {
            // zlib 头不对 / 数据损坏 → 交给调用方试另一种方式
            return null;
        }
    }

    /// <summary>应用 PNG 预测器（Predictor 10–15）；TIFF 预测器（2）目前原样返回。</summary>
    private static byte[]? ApplyPredictor(byte[] data, PdfDictionary? parms)
    {
        if (parms is null) return data;
        var predictor = parms.GetInt("Predictor") ?? 1;
        if (predictor <= 1) return data;
        if (predictor < 10) return data; // TIFF 预测器：文字层用不到，按原样处理

        var columns = Math.Max(1, parms.GetInt("Columns") ?? 1);
        var colors = Math.Max(1, parms.GetInt("Colors") ?? 1);
        var bits = Math.Max(1, parms.GetInt("BitsPerComponent") ?? 8);
        var bytesPerPixel = Math.Max(1, (colors * bits + 7) / 8);
        var rowLength = Math.Max(1, (columns * colors * bits + 7) / 8);

        var rowCount = data.Length / (rowLength + 1);
        if (rowCount == 0) return data;

        var output = new byte[rowCount * rowLength];
        var previous = new byte[rowLength];
        var current = new byte[rowLength];

        for (var row = 0; row < rowCount; row++)
        {
            var offset = row * (rowLength + 1);
            var filterType = data[offset];
            Array.Copy(data, offset + 1, current, 0, rowLength);

            switch (filterType)
            {
                case 0: // None
                    break;
                case 1: // Sub
                    for (var i = bytesPerPixel; i < rowLength; i++)
                        current[i] = (byte)(current[i] + current[i - bytesPerPixel]);
                    break;
                case 2: // Up
                    for (var i = 0; i < rowLength; i++)
                        current[i] = (byte)(current[i] + previous[i]);
                    break;
                case 3: // Average
                    for (var i = 0; i < rowLength; i++)
                    {
                        var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                        current[i] = (byte)(current[i] + (left + previous[i]) / 2);
                    }

                    break;
                case 4: // Paeth
                    for (var i = 0; i < rowLength; i++)
                    {
                        var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                        var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                        current[i] = (byte)(current[i] + Paeth(left, previous[i], upLeft));
                    }

                    break;
                default:
                    return null; // 未知预测器类型，数据不可信
            }

            Array.Copy(current, 0, output, row * rowLength, rowLength);
            (previous, current) = (current, previous);
        }

        return output;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var p = left + up - upLeft;
        var pa = Math.Abs(p - left);
        var pb = Math.Abs(p - up);
        var pc = Math.Abs(p - upLeft);
        if (pa <= pb && pa <= pc) return left;
        return pb <= pc ? up : upLeft;
    }
}
