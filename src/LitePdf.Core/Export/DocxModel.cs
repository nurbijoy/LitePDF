namespace LitePdf.Core.Export;

/// <summary>
/// The intermediate document the composer builds and the writer serializes. Types are prefixed because WPF
/// has a <c>Paragraph</c>, <c>Run</c>, <c>Table</c> and <c>Section</c> of its own and the app sees both.
/// </summary>
public enum DocxParagraphStyle
{
    Body,
    Heading1,
    Heading2,
    Heading3,
    Heading4,
    Caption,
}

public enum DocxAlignment
{
    Left,
    Center,
    Right,
    Justify,
}

public enum DocxListKind
{
    None,
    Bullet,
    Number,
}

public enum DocxScript
{
    Baseline,
    Superscript,
    Subscript,
}

/// <summary>A stretch of text with one appearance. The smallest thing Word can style.</summary>
public sealed record DocxRun(string Text, TextStyle Style)
{
    public DocxScript Script { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }

    /// <summary>An absolute URI; the writer turns it into a hyperlink relationship.</summary>
    public string? Hyperlink { get; init; }

    /// <summary>Highlight colour as 0xRRGGBB, from a PDF highlight annotation.</summary>
    public uint? Highlight { get; init; }

    /// <summary>Extra space after each character, in twips: the letter-spacing of tracked capitals.</summary>
    public int CharacterSpacingTwips { get; init; }
}

public abstract record DocxBlock;

public sealed record DocxParagraph(IReadOnlyList<DocxRun> Runs) : DocxBlock
{
    public DocxParagraphStyle Style { get; init; } = DocxParagraphStyle.Body;
    public DocxAlignment Alignment { get; init; } = DocxAlignment.Left;

    /// <summary>Left indent in twips (1/20 pt), measured from the section's text area.</summary>
    public int IndentTwips { get; init; }

    /// <summary>First-line indent in twips; negative hangs the first line to the left (lists).</summary>
    public int FirstLineTwips { get; init; }

    public int SpaceBeforeTwips { get; init; }
    public int SpaceAfterTwips { get; init; }

    /// <summary>
    /// Line spacing, or 0 for Word's default for the style. In twips for <see cref="DocxLineRule.Exact"/> and
    /// <see cref="DocxLineRule.AtLeast"/>; in 240ths of a line for <see cref="DocxLineRule.Auto"/>, as Word
    /// stores it (240 is single spacing).
    /// </summary>
    public int LineSpacing { get; init; }

    public DocxLineRule LineRule { get; init; } = DocxLineRule.AtLeast;

    /// <summary>Write zero paragraph gaps explicitly instead of inheriting the Word style's spacing.</summary>
    public bool ExplicitSpacing { get; init; }

    /// <summary>Pictures positioned relative to this paragraph rather than laid out in the text.</summary>
    public IReadOnlyList<DocxPicture> Floating { get; init; } = [];

    /// <summary>Keep this paragraph on the same page as the next, as a heading or a table's caption wants.</summary>
    public bool KeepWithNext { get; init; }

    /// <summary>A rule drawn under the paragraph: the line beneath a title, a separator after a block.</summary>
    public DocxBorder? BorderBelow { get; init; }

    /// <summary>A rule drawn over the paragraph.</summary>
    public DocxBorder? BorderAbove { get; init; }

    public DocxListKind List { get; init; }
    public int ListLevel { get; init; }

    /// <summary>The printed label, including punctuation. Keeps restarts and letter/Roman labels intact.</summary>
    public string? ListMarker { get; init; }

    /// <summary>
    /// How the list label was printed. Word draws a list number in the paragraph mark's font and size, so
    /// without this a label printed small is drawn at body size and overruns the indent measured for it.
    /// </summary>
    public TextStyle? MarkerStyle { get; init; }

    /// <summary>Starts a new page before this paragraph.</summary>
    public bool PageBreakBefore { get; init; }

    /// <summary>Ends the column after this paragraph: the text after it starts at the top of the next one.</summary>
    public bool ColumnBreakAfter { get; init; }

    /// <summary>
    /// Where text set in columns within the line lines up — a term and its definition, the cells of a table
    /// drawn without rules. Measured from the text area's left edge; the runs carry tab characters.
    /// </summary>
    public IReadOnlyList<DocxTabStop> TabStops { get; init; } = [];

    /// <summary>Ids of the comments anchored to this paragraph; see <see cref="DocxComment"/>.</summary>
    public IReadOnlyList<int> CommentIds { get; init; } = [];

    public string Text => Runs.Count == 1 ? Runs[0].Text : string.Concat(Runs.Select(r => r.Text));

    public static DocxParagraph Empty { get; } = new([]);
}

/// <summary>A paragraph rule: colour as 0xRRGGBB, width in eighths of a point, and its distance from the text.</summary>
public sealed record DocxBorder(uint Color, int Eighths, double SpacePoints);

public enum DocxLineRule
{
    /// <summary>A multiple of the font's own line height; the only rule that stays right as text is edited.</summary>
    Auto,
    Exact,
    AtLeast,
}

/// <summary>How a picture sits against the text.</summary>
public enum DocxWrap
{
    /// <summary>In the flow, as a paragraph of its own.</summary>
    Inline,

    /// <summary>Floating, with the text wrapping around its bounding box: a figure set beside the text.</summary>
    Square,

    /// <summary>Floating behind the text, which runs over it: a watermark, a panel, a page background.</summary>
    BehindText,

    /// <summary>Floating in front, the text unaware of it: an icon or a mark placed in the margin or a gap.</summary>
    InFrontOfText,
}

/// <summary>
/// An image sized in points. Inline, it is laid out as its own paragraph; floating, it hangs off the
/// paragraph that holds it in <see cref="DocxParagraph.Floating"/>, at an offset from the page's left edge
/// and from that paragraph's top (or the page's, for <see cref="VerticalFromPage"/>).
/// </summary>
public sealed record DocxPicture(PlacedImage Image, double WidthPoints, double HeightPoints) : DocxBlock
{
    public DocxAlignment Alignment { get; init; } = DocxAlignment.Center;
    public string? AltText { get; init; }
    public int SpaceBeforeTwips { get; init; }
    public int SpaceAfterTwips { get; init; }

    /// <summary>Left indent of an inline picture set flush left, in twips.</summary>
    public int IndentTwips { get; init; }

    public DocxWrap Wrap { get; init; } = DocxWrap.Inline;
    public double OffsetXPoints { get; init; }
    public double OffsetYPoints { get; init; }

    /// <summary>The vertical offset is from the top of the page rather than of the anchoring paragraph.</summary>
    public bool VerticalFromPage { get; init; }
}

public sealed record DocxCell(IReadOnlyList<DocxBlock> Blocks)
{
    public int ColumnSpan { get; init; } = 1;

    /// <summary>0 = a normal cell, 1 = the top of a vertical merge, 2 = continuing one.</summary>
    public int VerticalMerge { get; init; }

    /// <summary>Background as 0xRRGGBB, or null for none.</summary>
    public uint? Shading { get; init; }
}

public sealed record DocxRow(IReadOnlyList<DocxCell> Cells)
{
    public bool IsHeader { get; init; }

    /// <summary>The row's height on the page, as a minimum; 0 lets Word size it to its text.</summary>
    public int MinHeightTwips { get; init; }
}

public sealed record DocxTable(IReadOnlyList<DocxRow> Rows, IReadOnlyList<int> ColumnWidthsTwips) : DocxBlock
{
    public bool HasBorders { get; init; } = true;

    /// <summary>Border colour as 0xRRGGBB, as the PDF drew its rules; null for Word's automatic black.</summary>
    public uint? BorderColor { get; init; }

    /// <summary>Border width in eighths of a point, as Word stores it. 4 is half a point.</summary>
    public int BorderEighths { get; init; } = 4;

    /// <summary>Space between a cell's edge and its text, left and right, in twips.</summary>
    public int CellPaddingTwips { get; init; } = 108;

    /// <summary>Space above the text in each cell, in twips.</summary>
    public int CellTopPaddingTwips { get; init; }

    /// <summary>Offset of the table's left edge from the text area's, in twips.</summary>
    public int IndentTwips { get; init; }
}

/// <summary>
/// A margin note. A PDF sticky note is a comment in everything but name, and Word has a place to put it;
/// left in the body it would interrupt the text, and dropped it would be content lost without a word.
/// </summary>
public sealed record DocxComment(int Id, string Author, string Text)
{
    public DateTimeOffset? Date { get; init; }

    /// <summary>What Word shows in the margin bubble; derived from the author when not given.</summary>
    public string Initials { get; init; } = string.Empty;
}

/// <summary>Page margins in points.</summary>
public readonly record struct DocxMargins(double Left, double Top, double Right, double Bottom)
{
    public static DocxMargins Default { get; } = new(72, 72, 72, 72);
}

/// <summary>A tab stop, from the paragraph's left edge, and which way text aligns against it.</summary>
public readonly record struct DocxTabStop(int PositionTwips, DocxAlignment Alignment);

/// <summary>Running head or foot. <see cref="PageNumberRun"/> marks which run holds the page number.</summary>
public sealed record DocxHeaderFooter(IReadOnlyList<DocxRun> Runs, DocxAlignment Alignment)
{
    /// <summary>Index of the run whose text is the page number; the writer replaces it with a PAGE field.</summary>
    public int PageNumberRun { get; init; } = -1;

    /// <summary>
    /// Where the parts of a head set in pieces — a title at the left, a reference at the right — line up.
    /// The runs separate the parts with tab characters.
    /// </summary>
    public IReadOnlyList<DocxTabStop> TabStops { get; init; } = [];

    /// <summary>The rule a running head is set over, or a foot under, on every page.</summary>
    public DocxBorder? BorderBelow { get; init; }
    public DocxBorder? BorderAbove { get; init; }
}

/// <summary>
/// The columns a section is set in. Equal columns need only the gap between them; unequal ones list each
/// column's width and the gap after it, as Word stores them.
/// </summary>
public sealed record DocxColumns(int Count, double SpacePoints)
{
    public static DocxColumns Single { get; } = new(1, 36);

    public IReadOnlyList<double> WidthsPoints { get; init; } = [];
    public IReadOnlyList<double> GapsPoints { get; init; } = [];

    public bool IsSingle => Count <= 1;

    /// <summary>The same layout, give or take the few points two measurements of it differ by.</summary>
    public bool SameAs(DocxColumns other)
    {
        if (Count != other.Count) return false;
        if (IsSingle) return true;
        if (WidthsPoints.Count != other.WidthsPoints.Count) return false;
        for (int i = 0; i < WidthsPoints.Count; i++)
            if (Math.Abs(WidthsPoints[i] - other.WidthsPoints[i]) > 12) return false;
        return Math.Abs(SpacePoints - other.SpacePoints) <= 12;
    }
}

/// <summary>
/// One run of pages sharing a paper size and margins. A new section starts where the page size or
/// orientation changes, which is what Word needs to reproduce a mixed-format PDF.
/// </summary>
public sealed record DocxSection(PageSize Size, DocxMargins Margins, IReadOnlyList<DocxBlock> Blocks)
{
    public DocxHeaderFooter? Header { get; init; }
    public DocxHeaderFooter? Footer { get; init; }

    /// <summary>
    /// The section's first page has no running head or foot, as a title page usually does not. Word calls
    /// this "different first page".
    /// </summary>
    public bool DifferentFirstPage { get; init; }

    /// <summary>What the first page carries instead, when <see cref="DifferentFirstPage"/>; null for nothing.</summary>
    public DocxHeaderFooter? FirstHeader { get; init; }
    public DocxHeaderFooter? FirstFooter { get; init; }

    /// <summary>Distance of the running head from the top edge, and of the foot from the bottom, in points.</summary>
    public double HeaderDistancePoints { get; init; } = 36;
    public double FooterDistancePoints { get; init; } = 36;

    /// <summary>First printed page number, when the original did not start at 1. 0 leaves Word's default.</summary>
    public int PageNumberStart { get; init; }

    public DocxColumns Columns { get; init; } = DocxColumns.Single;

    /// <summary>
    /// The section starts on the page the previous one ended on, as a change from one column to two does;
    /// otherwise it starts a new page.
    /// </summary>
    public bool Continuous { get; init; }

    public bool Landscape => Size.Width > Size.Height;
}

public sealed record DocxDocument(IReadOnlyList<DocxSection> Sections)
{
    public string? Title { get; init; }
    public string? Author { get; init; }

    /// <summary>The body text size, which becomes the Normal style. Everything else is relative to it.</summary>
    public double BodySizePoints { get; init; } = 11;

    public string BodyFont { get; init; } = "Calibri";

    /// <summary>Comments referenced by <see cref="DocxParagraph.CommentIds"/>.</summary>
    public IReadOnlyList<DocxComment> Comments { get; init; } = [];

    /// <summary>Font programs to embed. Empty unless the export asked for it.</summary>
    public IReadOnlyList<EmbeddedFont> Fonts { get; init; } = [];

    public IEnumerable<DocxBlock> AllBlocks => Sections.SelectMany(s => s.Blocks);
}
