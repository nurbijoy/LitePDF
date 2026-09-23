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
    /// <summary>True for a drawing rasterized out of path operators rather than an image the PDF stores.</summary>
    public bool IsDrawing { get; init; }

    /// <summary>The marked-content id of the object, or -1: what ties it to a <c>/Figure</c> and its alt text.</summary>
    public int MarkedContentId { get; init; } = -1;

    public static PlacedImage FromEncoded(RectD bounds, byte[] bytes, string extension) =>
        new(bounds, bytes, extension, null);

    public static PlacedImage FromBits(RectD bounds, ImageBits bits) => new(bounds, null, null, bits);
}

/// <summary>A thin filled or stroked line: a table rule, an underline, a box edge.</summary>
public readonly record struct RuleSegment(RectD Bounds, bool IsHorizontal)
{
    /// <summary>The rule's colour as 0xRRGGBB. Black when the PDF did not say.</summary>
    public uint Color { get; init; }

    /// <summary>How thick the rule is drawn, in points; 0 when unknown.</summary>
    public double ThicknessPoints { get; init; }
}

/// <summary>
/// A filled rectangle drawn behind something: a shaded table cell, a banded row, a coloured panel.
/// Kept apart from <see cref="RuleSegment"/> because only the colour of these is of any use.
/// </summary>
public readonly record struct FilledArea(RectD Bounds, uint Color);

/// <summary>Characters [Start, End) of the page's text that were drawn inside one marked-content sequence.</summary>
public readonly record struct MarkedRange(int Start, int End, int MarkedContentId);

/// <summary>
/// One element of a tagged PDF's structure tree, with the marked-content ids that tie it to the page.
///
/// A tagged PDF already knows what its own paragraphs, headings, lists and tables are, which is information
/// no amount of geometry can recover reliably. Tags are trusted for grouping and order only: they routinely
/// mark a run as <c>/P</c> while drawing it bold at 18 pt, so appearance still comes from the glyphs.
/// </summary>
public sealed record PageTag(string Type, IReadOnlyList<PageTag> Children)
{
    public string? AltText { get; init; }

    public string? Title { get; init; }

    /// <summary>Marked-content ids drawn directly by this element (its children carry their own).</summary>
    public IReadOnlyList<int> MarkedContentIds { get; init; } = [];

    public int RowSpan { get; init; } = 1;

    public int ColumnSpan { get; init; } = 1;

    public IEnumerable<PageTag> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var nested in child.Descendants()) yield return nested;
        }
    }
}

/// <summary>
/// A font program lifted out of the PDF, for the optional embedding. <see cref="IsSubset"/> matters: an
/// embedded subset holds only the glyphs the PDF used, so typing a new letter in Word shows a box.
/// </summary>
public sealed record EmbeddedFont(string Family, bool Bold, bool Italic, byte[] Data, bool IsSubset);

/// <summary>What a caller wants read off the page. The costly parts are opt-in.</summary>
public sealed record PageContentRequest
{
    /// <summary>Rasterize vector artwork (charts, logos) that no image object holds.</summary>
    public bool Drawings { get; init; } = true;

    /// <summary>Read the structure tree of a tagged PDF.</summary>
    public bool Tags { get; init; } = true;

    /// <summary>Lift out embedded font programs. Off by default: it copies megabytes per document.</summary>
    public bool Fonts { get; init; }

    public static PageContentRequest Default { get; } = new();
}

/// <summary>Everything the DOCX export needs from one page. Geometry is normalized page coordinates.</summary>
public sealed record PageContent(
    int PageIndex,
    PageSize Size,
    PageText Text,
    IReadOnlyList<StyledSpan> Spans,
    IReadOnlyList<PlacedImage> Images,
    IReadOnlyList<RuleSegment> Rules)
{
    /// <summary>The only usable text came from an invisible OCR layer, not printed PDF text.</summary>
    public bool IsSearchableScan { get; init; }

    /// <summary>Filled rectangles, which is where a shaded table cell gets its colour.</summary>
    public IReadOnlyList<FilledArea> Fills { get; init; } = [];

    /// <summary>The roots of the page's structure tree, empty when the PDF is not tagged.</summary>
    public IReadOnlyList<PageTag> Tags { get; init; } = [];

    /// <summary>Character ranges by marked-content id, which is how a tag finds its text.</summary>
    public IReadOnlyList<MarkedRange> Marks { get; init; } = [];

    /// <summary>Font programs used on the page, read only when the export asks to embed them.</summary>
    public IReadOnlyList<EmbeddedFont> Fonts { get; init; } = [];

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
    Task<PageContent> GetPageContentAsync(
        int pageIndex, int priority, PageContentRequest? request = null, CancellationToken ct = default);
}
