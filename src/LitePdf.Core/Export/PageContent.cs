using LitePdf.Core.Text;

namespace LitePdf.Core.Export;

/// <summary>
/// How a character is drawn. Runs are split wherever any of these change, so this is deliberately small:
/// anything finer would fragment every line into single-character runs.
/// </summary>
public readonly record struct TextStyle(string FontFamily, double SizePoints, bool Bold, bool Italic, uint Color)
{
    public static TextStyle Default { get; } = new("Calibri", 11, false, false, 0x000000);

    /// <summary>Same appearance? Sizes within a twentieth of a point are the same size.</summary>
    public bool Matches(TextStyle other) =>
        Bold == other.Bold && Italic == other.Italic && Color == other.Color &&
        Math.Abs(SizePoints - other.SizePoints) < 0.05 &&
        string.Equals(FontFamily, other.FontFamily, StringComparison.Ordinal);
}

/// <summary>Characters [Start, End) of the page's text, all drawn with one style.</summary>
public sealed record StyledSpan(int Start, int End, TextStyle Style);

/// <summary>
/// BGRA32 pixels with alpha preserved. <see cref="RenderedBitmap"/> is opaque by contract, and an image
/// lifted out of a PDF may carry a soft mask, so it needs its own type.
/// </summary>
public sealed record ImageBits(int Width, int Height, byte[] Bgra);

/// <summary>
/// An image placed on the page, in normalized page coordinates. Exactly one of <see cref="Encoded"/> and
/// <see cref="Bits"/> is set: encoded bytes come straight out of the PDF and are stored untouched, while
/// pixels still have to be encoded by the host (Core has no image encoder).
/// </summary>
public sealed record PlacedImage(RectD Bounds, byte[]? Encoded, string? Extension, ImageBits? Bits)
{
    public static PlacedImage FromEncoded(RectD bounds, byte[] bytes, string extension) =>
        new(bounds, bytes, extension, null);

    public static PlacedImage FromBits(RectD bounds, ImageBits bits) => new(bounds, null, null, bits);
}

/// <summary>A thin filled or stroked line: a table rule, an underline, a box edge.</summary>
public readonly record struct RuleSegment(RectD Bounds, bool IsHorizontal);

/// <summary>Everything the DOCX export needs from one page. Geometry is normalized page coordinates.</summary>
public sealed record PageContent(
    int PageIndex,
    PageSize Size,
    PageText Text,
    IReadOnlyList<StyledSpan> Spans,
    IReadOnlyList<PlacedImage> Images,
    IReadOnlyList<RuleSegment> Rules)
{
    public static PageContent Empty(int pageIndex, PageSize size) =>
        new(pageIndex, size, PageText.Empty(pageIndex), [], [], []);

    /// <summary>The style of the character at <paramref name="index"/>, or the default when none was recorded.</summary>
    public TextStyle StyleAt(int index)
    {
        // Spans are ordered and disjoint; pages rarely hold more than a few hundred.
        int lo = 0, hi = Spans.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var span = Spans[mid];
            if (index < span.Start) hi = mid - 1;
            else if (index >= span.End) lo = mid + 1;
            else return span.Style;
        }
        return TextStyle.Default;
    }
}

/// <summary>A document that can report the drawing behind its text, over and above <see cref="IPdfDocument"/>.</summary>
public interface IPageContentSource
{
    Task<PageContent> GetPageContentAsync(int pageIndex, int priority, CancellationToken ct = default);
}
