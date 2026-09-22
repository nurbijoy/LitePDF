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

    /// <summary>Exact line spacing in twips, or 0 for Word's default for the style.</summary>
    public int LineSpacingTwips { get; init; }

    /// <summary>Write zero paragraph gaps explicitly instead of inheriting the Word style's spacing.</summary>
    public bool ExplicitSpacing { get; init; }

    public DocxListKind List { get; init; }
    public int ListLevel { get; init; }

    /// <summary>The printed label, including punctuation. Keeps restarts and letter/Roman labels intact.</summary>
    public string? ListMarker { get; init; }

    /// <summary>Starts a new page before this paragraph.</summary>
    public bool PageBreakBefore { get; init; }

    /// <summary>Ids of the comments anchored to this paragraph; see <see cref="DocxComment"/>.</summary>
    public IReadOnlyList<int> CommentIds { get; init; } = [];

    public string Text => Runs.Count == 1 ? Runs[0].Text : string.Concat(Runs.Select(r => r.Text));

    public static DocxParagraph Empty { get; } = new([]);
}

/// <summary>An image sized in points, laid out as its own paragraph.</summary>
public sealed record DocxPicture(PlacedImage Image, double WidthPoints, double HeightPoints) : DocxBlock
{
    public DocxAlignment Alignment { get; init; } = DocxAlignment.Center;
    public string? AltText { get; init; }
    public int SpaceBeforeTwips { get; init; }
    public int SpaceAfterTwips { get; init; }
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
}

public sealed record DocxTable(IReadOnlyList<DocxRow> Rows, IReadOnlyList<int> ColumnWidthsTwips) : DocxBlock
{
    public bool HasBorders { get; init; } = true;
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

/// <summary>Running head or foot. <see cref="PageNumberRuns"/> marks which run holds the page number.</summary>
public sealed record DocxHeaderFooter(IReadOnlyList<DocxRun> Runs, DocxAlignment Alignment)
{
    /// <summary>Index of the run whose text is the page number; the writer replaces it with a PAGE field.</summary>
    public int PageNumberRun { get; init; } = -1;
}

/// <summary>
/// One run of pages sharing a paper size and margins. A new section starts where the page size or
/// orientation changes, which is what Word needs to reproduce a mixed-format PDF.
/// </summary>
public sealed record DocxSection(PageSize Size, DocxMargins Margins, IReadOnlyList<DocxBlock> Blocks)
{
    public DocxHeaderFooter? Header { get; init; }
    public DocxHeaderFooter? Footer { get; init; }

    /// <summary>First printed page number, when the original did not start at 1. 0 leaves Word's default.</summary>
    public int PageNumberStart { get; init; }

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
