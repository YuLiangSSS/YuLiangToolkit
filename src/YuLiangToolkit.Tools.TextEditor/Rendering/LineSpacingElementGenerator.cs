using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace YuLiangToolkit.Tools.TextEditor.Rendering;

/// <summary>
/// 给 AvalonEdit 编辑器加「行间距」。
///
/// <para><b>为什么需要这个轮子</b>：AvalonEdit 的 <see cref="TextView"/> 只有只读的
/// <c>DefaultLineHeight</c>，没有公开的 LineHeight 设置，行高完全由字体度量决定（默认很紧）。
/// 这里用 AvalonEdit 官方提供的 <see cref="VisualLineElementGenerator"/> 扩展点：给每个可视行末尾塞一个
/// <b>不占文档字符</b>的元素（<c>DocumentLength = 0</c>、<c>VisualLength = 1</c>），
/// 它的 TextRun 用放大的 <see cref="TextRunProperties.FontRenderingEmSize"/> + 零宽空格（U+200B），
/// 于是该行的 WPF <c>TextLine.Height</c> 被撑到「基础行高 × 系数」，而文字本身 / 光标 / 选择 / 撤销都不受影响。</para>
///
/// <para><b>已知边界</b>：开启「自动换行」时一个文档行会折成多条视觉文本行，行尾的撑高元素只会作用于最后一条，
/// 所以折行后的行距可能不完全均匀（默认不开自动换行，写代码场景一般不受影响）。</para>
/// </summary>
public sealed class LineSpacingElementGenerator : VisualLineElementGenerator
{
    /// <summary>行距下限（1.0 = AvalonEdit 默认的紧凑行高）。</summary>
    public const double MinSpacing = 1.0;

    /// <summary>行距上限（2.0 = 行高翻倍，再大就太散了）。</summary>
    public const double MaxSpacing = 2.0;

    private readonly TextDocument _document;
    private double _spacingFactor = MinSpacing;

    /// <summary>为某个文档创建生成器（每个标签的编辑器一个；文档不会换，所以构造时记下）。</summary>
    public LineSpacingElementGenerator(TextDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>行距系数（1.0 ~ 2.0，越界夹回；非有限值回 1.0）。</summary>
    public double SpacingFactor
    {
        get => _spacingFactor;
        set => _spacingFactor = double.IsFinite(value) ? Math.Clamp(value, MinSpacing, MaxSpacing) : MinSpacing;
    }

    /// <summary>系数 ≤ 1 时完全不插入元素（等于 AvalonEdit 原样，零开销）。</summary>
    public bool IsActive => _spacingFactor > 1.0005;

    /// <summary>只在「当前文档行的行尾」建元素：AvalonEdit 会以零长度元素继续扫描，不会死循环。</summary>
    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!IsActive) return -1;
        if (startOffset < 0 || startOffset > _document.TextLength) return -1;

        var line = _document.GetLineByOffset(startOffset);
        var lineEnd = line.EndOffset;
        return lineEnd >= startOffset ? lineEnd : -1;
    }

    /// <summary>行尾的撑高元素（零宽、零文档长度）。</summary>
    public override VisualLineElement? ConstructElement(int offset)
        => IsActive ? new LineSpacingElement(_spacingFactor) : null;

    private sealed class LineSpacingElement : VisualLineElement
    {
        private readonly double _factor;

        public LineSpacingElement(double factor)
            : base(visualLength: 1, documentLength: 0)
        {
            _factor = factor;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            var properties = TextRunProperties.Clone();
            properties.SetFontRenderingEmSize(properties.FontRenderingEmSize * _factor);
            // 零宽空格：没有可见字形 / 没有宽度，但字体度量照样把 TextLine 撑高
            return new TextCharacters("\u200B", properties);
        }
    }
}
