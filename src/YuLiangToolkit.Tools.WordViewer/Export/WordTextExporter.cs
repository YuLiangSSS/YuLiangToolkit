using YuLiangToolkit.Tools.WordViewer.Parsing;

namespace YuLiangToolkit.Tools.WordViewer.Export;

/// <summary>
/// 纯文本导出：复用 <see cref="WordDocument.ExtractPlainText"/>（段落换行、表格按「单元格 + 制表符」），
/// 写成 **UTF-8 with BOM** 的 <c>.txt</c>。
///
/// <para>为什么带 BOM：Windows 记事本 / 旧版 Excel 打开不带 BOM 的 UTF-8 中文时可能按系统 ANSI 解码变成乱码；
/// 带 BOM 对现代编辑器无影响，兼容性最好。</para>
///
/// <para>和界面一致：页眉页脚 / 脚注尾注不是正文，默认不导出；修订里「删除」的文字不导出。</para>
/// </summary>
public static class WordTextExporter
{
    /// <summary>
    /// 把正文纯文本写进 <paramref name="path"/>，返回写出的字符数。
    /// 写盘异常直接抛给调用方（界面转成状态提示）。
    /// </summary>
    public static int Write(WordDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径为空", nameof(path));

        var text = document.ExtractPlainText();
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return text.Length;
    }
}
