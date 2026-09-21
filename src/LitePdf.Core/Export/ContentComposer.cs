using System.Text;
using System.Text.RegularExpressions;
using LitePdf.Core.Text;

namespace LitePdf.Core.Export;

public sealed record ExportOptions
{
    public bool Headings { get; init; } = true;
    public bool Lists { get; init; } = true;
    public bool Tables { get; init; } = true;
    public bool HeadersFooters { get; init; } = true;
    public bool Images { get; init; } = true;
    public bool Hyperlinks { get; init; } = true;
    public bool Annotations { get; init; } = true;

    /// <summary>Joins a paragraph that runs from the foot of one page to the head of the next.</summary>
    public bool JoinAcrossPages { get; init; } = true;

    public static ExportOptions Default { get; } = new();
}

/// <summary>Per-page input beyond the drawing itself.</summary>
public sealed record PageExtras(IReadOnlyList<PdfLink> Links, IReadOnlyList<PdfAnnotation> Annotations)
{
    public static PageExtras None { get; } = new([], []);
}

/// <summary>
/// Rebuilds a document from the geometry of a page: columns, paragraphs, headings, lists and running heads.
///
/// A PDF says where every glyph sits and nothing about what any of it means, so every rule here is an
/// inference. The ordering principle is that <b>a rule that does not fire must still leave the content
/// intact</b>: every line that is not claimed by a heading, a list or a running head still becomes a
/// correctly styled paragraph in the right place. Nothing is ever dropped to make the output tidier.
/// </summary>
public static class ContentComposer
{
    /// <summary>A line wider than this fraction of the text area spans the columns beneath it.</summary>
    private const double SpanningLineWidth = 0.62;

    /// <summary>A gap this many times the typical glyph height splits two columns.</summary>
    private const double ColumnGapInGlyphs = 1.6;

    private const int MinLinesPerColumn = 4;

    /// <summary>How far from the column's left edge a line can start and still count as flush left.</summary>
    private const double ShortLineFraction = 0.055;

    /// <summary>Vertical gap, against the median for the block, that starts a new paragraph.</summary>
    private const double ParagraphGapRatio = 1.45;

    public static DocxDocument Compose(
        IReadOnlyList<PageContent> pages,
        IReadOnlyList<PageExtras>? extras = null,
        IReadOnlyList<OutlineItem>? outline = null,
        ExportOptions? options = null,
        DocumentInfo? info = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        options ??= ExportOptions.Default;

        var profile = DocumentProfile.Build(pages, outline, options);
        var headings = profile.Outline;

        var composed = new List<(PageContent Page, List<DocxBlock> Blocks)>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var pageExtras = extras is not null && i < extras.Count ? extras[i] : PageExtras.None;
            composed.Add((page, ComposePage(page, pageExtras, profile, headings, options)));
        }

        var sections = BuildSections(composed, profile, options);
        if (profile.PageNumberStart > 0 && sections.Count > 0)
            sections[0] = sections[0] with { PageNumberStart = profile.PageNumberStart };

        return new DocxDocument(sections)
        {
            Title = string.IsNullOrWhiteSpace(info?.Title) ? null : info!.Title,
            Author = string.IsNullOrWhiteSpace(info?.Author) ? null : info!.Author,
            BodySizePoints = profile.BodySize,
            BodyFont = profile.BodyFont,
        };
    }

    // ---- page ----

    private static List<DocxBlock> ComposePage(
        PageContent page, PageExtras extras, DocumentProfile profile,
        ILookup<int, (double Y, int Depth, string Title)> headings, ExportOptions options)
    {
        var lines = ReadLines(page, profile, options);
        var blocks = new List<DocxBlock>();

        var paragraphs = new List<Para>();
        foreach (var (column, columnLines) in SplitIntoColumns(lines, profile))
            paragraphs.AddRange(GroupParagraphs(columnLines, column, page));

        if (options.Lists) MarkLists(paragraphs);
        if (options.Headings) MarkHeadings(paragraphs, profile, headings, page.PageIndex);

        var grids = options.Tables ? TableBuilder.Find(page.Rules, profile.GlyphHeight) : [];
        var emitted = new HashSet<int>();

        double previousBottom = double.NaN;
        foreach (var para in paragraphs)
        {
            int table = grids.FindIndex(g => g.Bounds.Contains(para.Bounds.Center));
            if (table >= 0)
            {
                // The text of the table is rebuilt from the page rather than from these paragraphs: a row
                // of cells is one visual line, so it has to be cut apart by position, not by line.
                if (emitted.Add(table)) blocks.Add(BuildTable(grids[table], page, extras, profile, options));
                continue;
            }

            blocks.Add(BuildParagraph(para, page, extras, profile, options, previousBottom));
            previousBottom = para.Bounds.Bottom;
        }

        if (options.Images && page.Images.Count > 0) InsertImages(blocks, page, paragraphs, profile);
        return blocks;
    }

    /// <summary>Visual lines with their text and dominant style, minus blanks and running heads.</summary>
    private static List<Line> ReadLines(PageContent page, DocumentProfile profile, ExportOptions options)
    {
        var text = page.Text;
        var lines = new List<Line>(text.Lines.Count);

        foreach (var visual in text.Lines)
        {
            if (visual.Bounds.IsEmpty) continue;
            string raw = visual.End > visual.Start ? text.Text[visual.Start..visual.End] : string.Empty;
            if (raw.Trim().Length == 0) continue;

            if (options.HeadersFooters && profile.IsRunningHead(visual.Bounds, raw)) continue;

            lines.Add(Line.Create(page, visual, raw));
        }

        lines.Sort((a, b) =>
        {
            int byTop = a.Bounds.Top.CompareTo(b.Bounds.Top);
            return byTop != 0 ? byTop : a.Bounds.Left.CompareTo(b.Bounds.Left);
        });
        return lines;
    }

    /// <summary>
    /// Splits the page into reading units: one per column, or a single unit covering the whole text area.
    ///
    /// Columns are looked for first, over the narrow lines only. Only if the page really is in columns does
    /// a line that spans the measure (a headline over both columns) close the zone above it. Doing it the
    /// other way round would treat every line of an ordinary single-column page as a spanning line, because
    /// on such a page the text area and the line are the same width.
    /// </summary>
    private static List<(RectD Column, List<Line> Lines)> SplitIntoColumns(List<Line> lines, DocumentProfile profile)
    {
        var units = new List<(RectD, List<Line>)>();
        if (lines.Count == 0) return units;

        RectD area = Bounds(lines);
        var bands = DetectColumnBands(lines, area, profile.GlyphHeight);

        if (bands.Count < 2)
        {
            units.Add((area, lines));
            return units;
        }

        var zone = new List<Line>();
        var spanning = new List<Line>();

        void FlushSpanning()
        {
            if (spanning.Count == 0) return;
            var bounds = Bounds(spanning);
            units.Add((new RectD(area.Left, bounds.Top, area.Right, bounds.Bottom), [.. spanning]));
            spanning.Clear();
        }

        void FlushZone()
        {
            if (zone.Count == 0) return;
            foreach (var band in bands)
            {
                var members = zone.Where(l => l.Bounds.Center.X >= band.Left && l.Bounds.Center.X <= band.Right)
                                  .OrderBy(l => l.Bounds.Top).ToList();
                if (members.Count > 0)
                    units.Add((new RectD(band.Left, Bounds(members).Top, band.Right, Bounds(members).Bottom), members));
            }
            zone.Clear();
        }

        foreach (var line in lines)
        {
            if (area.Width > 0 && line.Bounds.Width >= area.Width * SpanningLineWidth)
            {
                FlushZone();
                spanning.Add(line);      // consecutive spanning lines are one headline, not several
            }
            else
            {
                FlushSpanning();
                zone.Add(line);
            }
        }
        FlushZone();
        FlushSpanning();

        return units;
    }

    /// <summary>
    /// Finds columns by projecting the narrow lines onto the horizontal axis and looking for a corridor of
    /// white that none of them crosses. Deliberately reluctant: a single indented block, a hanging list or
    /// a page number must not split a page in two, so every band has to hold a column's worth of lines.
    /// </summary>
    private static List<(double Left, double Right)> DetectColumnBands(List<Line> lines, RectD area, double glyph)
    {
        if (area.Width <= 0) return [];

        var narrow = lines.Where(l => l.Bounds.Width < area.Width * SpanningLineWidth).ToList();
        if (narrow.Count < MinLinesPerColumn * 2) return [];

        var merged = new List<(double Left, double Right)>();
        foreach (var (left, right) in narrow.Select(l => (l.Bounds.Left, l.Bounds.Right)).OrderBy(i => i.Left))
        {
            if (merged.Count > 0 && left <= merged[^1].Right)
            {
                if (right > merged[^1].Right) merged[^1] = (merged[^1].Left, right);
            }
            else
            {
                merged.Add((left, right));
            }
        }

        double minGap = Math.Max(glyph * ColumnGapInGlyphs, 0.02);
        var bands = new List<(double Left, double Right)>();
        foreach (var band in merged)
        {
            if (bands.Count > 0 && band.Left - bands[^1].Right < minGap)
                bands[^1] = (bands[^1].Left, Math.Max(bands[^1].Right, band.Right));
            else
                bands.Add(band);
        }
        if (bands.Count < 2) return [];

        foreach (var band in bands)
        {
            int members = narrow.Count(l => l.Bounds.Center.X >= band.Left && l.Bounds.Center.X <= band.Right);
            if (members < MinLinesPerColumn) return [];   // not a column layout after all
        }
        return bands;
    }


    // ---- paragraphs ----

    private sealed class Line
    {
        public required int Start { get; init; }
        public required int End { get; init; }
        public required RectD Bounds { get; init; }
        public required string Text { get; init; }
        public required TextStyle Style { get; init; }
        public required bool AllBold { get; init; }

        /// <summary>Left edge of the first non-space character, which is what an indent is measured from.</summary>
        public double TextLeft => Bounds.Left;

        /// <summary>
        /// A line built from an arbitrary range of the page's text, used for the contents of a table cell:
        /// two cells side by side are one visual line, so a cell's text has to be cut out by position.
        /// </summary>
        public static Line? FromRange(PageContent page, int start, int end)
        {
            var bounds = RectD.Empty;
            for (int i = start; i < end; i++)
                if (page.Text.TryGetBox(i, out var box)) bounds = bounds.Union(box);

            string raw = page.Text.Text[start..end].Trim();
            if (raw.Length == 0 || bounds.IsEmpty) return null;

            return Create(page, new TextLine(start, end, bounds), raw);
        }

        public static Line Create(PageContent page, TextLine visual, string raw)
        {
            // The dominant style of a line is the style of most of its characters: a line is rarely mixed,
            // and when it is, the majority is what the paragraph should inherit.
            var counts = new Dictionary<TextStyle, int>();
            int bold = 0, counted = 0;
            for (int i = visual.Start; i < visual.End; i++)
            {
                if (i >= page.Text.Length || char.IsWhiteSpace(page.Text.Text[i])) continue;
                var style = page.StyleAt(i);
                counts[style] = counts.GetValueOrDefault(style) + 1;
                if (style.Bold) bold++;
                counted++;
            }

            var dominant = counts.Count == 0
                ? TextStyle.Default
                : counts.OrderByDescending(p => p.Value).First().Key;

            return new Line
            {
                Start = visual.Start,
                End = visual.End,
                Bounds = visual.Bounds,
                Text = raw.Trim(),
                Style = dominant,
                AllBold = counted > 0 && bold >= counted * 0.8,
            };
        }
    }

    private sealed class Para
    {
        public required List<Line> Lines { get; init; }
        public required RectD Column { get; set; }
        public RectD Bounds { get; set; }
        public DocxAlignment Alignment { get; set; }
        public DocxParagraphStyle Style { get; set; } = DocxParagraphStyle.Body;
        public DocxListKind List { get; set; }
        public int ListLevel { get; set; }

        /// <summary>Characters of the first line taken by a list marker, which the runs skip.</summary>
        public int MarkerLength { get; set; }

        public double Size => Lines.Count == 0 ? 11 : Lines[0].Style.SizePoints;
        public bool Bold => Lines.Count > 0 && Lines.All(l => l.AllBold);
        public string Text => string.Join(" ", Lines.Select(l => l.Text));
    }

    private static List<Para> GroupParagraphs(List<Line> lines, RectD column, PageContent page)
    {
        var result = new List<Para>();
        if (lines.Count == 0) return result;

        double medianGap = MedianGap(lines);
        var current = new List<Line> { lines[0] };

        for (int i = 1; i < lines.Count; i++)
        {
            var previous = lines[i - 1];
            var line = lines[i];
            if (StartsNewParagraph(previous, line, current, column, medianGap, page))
            {
                result.Add(Finish(current, column));
                current = [line];
            }
            else
            {
                current.Add(line);
            }
        }
        result.Add(Finish(current, column));
        return result;

        static Para Finish(List<Line> lines, RectD column)
        {
            var para = new Para { Lines = [.. lines], Column = column, Bounds = Bounds(lines) };
            para.Alignment = DetectAlignment(para);
            return para;
        }
    }

    private static bool StartsNewParagraph(
        Line previous, Line line, List<Line> current, RectD column, double medianGap, PageContent page)
    {
        double gap = line.Bounds.Top - previous.Bounds.Bottom;
        double height = Math.Max(previous.Bounds.Height, 1e-6);

        // A line that opens with a bullet or a number is a new item, whatever the geometry says. Without
        // this a list whose items are all one line and all the same width reads as a single paragraph,
        // because no line in it ever stops short of the others.
        if (OpensAListItem(line.Text)) return true;

        // A clear jump down always ends the paragraph.
        double threshold = medianGap > 0 ? Math.Max(medianGap * ParagraphGapRatio, medianGap + height * 0.35) : height * 0.5;
        if (gap > threshold) return true;

        // The style of the whole line changed: a heading, a caption, a quotation in another face.
        if (!previous.Style.Matches(line.Style) &&
            (Math.Abs(previous.Style.SizePoints - line.Style.SizePoints) > previous.Style.SizePoints * 0.08 ||
             previous.AllBold != line.AllBold))
            return true;

        // The previous line stopped short of the measure. In prose set flush left this is the most reliable
        // end-of-paragraph signal there is — but only if it stopped short *by choice*. A line that ends 20
        // points early because the next word is 30 points wide has not ended anything, so the test is
        // whether that word would have fitted. A fixed fraction of the measure gets this wrong constantly.
        double indentSlack = Math.Max(column.Width * ShortLineFraction, AverageCharWidth(previous) * 2);
        bool previousIsFlushLeft = previous.Bounds.Left <= column.Left + indentSlack;
        if (previousIsFlushLeft)
        {
            double room = column.Right - previous.Bounds.Right;
            double nextWord = FirstWordWidth(line, page) + AverageCharWidth(previous) * 0.6;
            if (room > nextWord || room > column.Width * 0.35) return true;
        }

        // A first line indented from the block it follows starts a new paragraph.
        double blockLeft = current.Min(l => l.Bounds.Left);
        if (line.Bounds.Left > blockLeft + height * 0.7) return true;

        return false;
    }

    private static DocxAlignment DetectAlignment(Para para)
    {
        var lines = para.Lines;
        var column = para.Column;
        if (column.Width <= 0 || lines.Count == 0) return DocxAlignment.Left;

        double tolerance = Math.Max(column.Width * 0.015, 0.004);
        bool leftFlush = lines.All(l => Math.Abs(l.Bounds.Left - column.Left) <= tolerance);
        bool rightFlush = lines.All(l => Math.Abs(l.Bounds.Right - column.Right) <= tolerance);

        if (leftFlush && rightFlush && lines.Count > 1) return DocxAlignment.Justify;

        // All but the last line reaching both edges is justified text; the last line never does.
        if (lines.Count > 2 &&
            lines.Take(lines.Count - 1).All(l => Math.Abs(l.Bounds.Left - column.Left) <= tolerance &&
                                                 Math.Abs(l.Bounds.Right - column.Right) <= tolerance))
            return DocxAlignment.Justify;

        if (leftFlush) return DocxAlignment.Left;

        double center = column.Center.X;
        bool centred = lines.All(l => Math.Abs(l.Bounds.Center.X - center) <= tolerance * 2.5) &&
                       lines.All(l => l.Bounds.Left > column.Left + tolerance);
        if (centred) return DocxAlignment.Center;

        if (rightFlush) return DocxAlignment.Right;
        return DocxAlignment.Left;
    }

    // ---- headings and lists ----

    private static void MarkHeadings(
        List<Para> paragraphs, DocumentProfile profile,
        ILookup<int, (double Y, int Depth, string Title)> headings, int pageIndex)
    {
        foreach (var para in paragraphs)
        {
            if (para.List != DocxListKind.None) continue;

            // A bookmark pointing into this paragraph is not a guess: it names the heading and its depth.
            foreach (var (y, depth, title) in headings[pageIndex])
            {
                // A destination without a Y still names the heading; it just cannot place it.
                if (!double.IsNaN(y) && (y < para.Bounds.Top - 0.02 || y > para.Bounds.Bottom + 0.02)) continue;
                if (!LooksLikeSameText(para.Text, title)) continue;
                para.Style = LevelToStyle(depth);
                break;
            }
            if (para.Style != DocxParagraphStyle.Body) continue;

            if (para.Lines.Count > 3) continue;
            string text = para.Text;
            if (text.Length is 0 or > 200) continue;

            double size = para.Size;
            bool larger = size >= profile.BodySize * 1.12;
            bool boldAndTight = para.Bold && size >= profile.BodySize * 0.97 && text.Length <= 120;
            if (!larger && !boldAndTight) continue;

            // Running prose that happens to be short is not a heading: a full stop at the end gives it away.
            if (text.EndsWith('.') && text.Length > 60) continue;

            para.Style = larger ? LevelToStyle(profile.HeadingLevel(size)) : DocxParagraphStyle.Heading4;
        }
    }

    private static DocxParagraphStyle LevelToStyle(int level) => level switch
    {
        <= 0 => DocxParagraphStyle.Heading1,
        1 => DocxParagraphStyle.Heading2,
        2 => DocxParagraphStyle.Heading3,
        _ => DocxParagraphStyle.Heading4,
    };

    private static bool LooksLikeSameText(string a, string b)
    {
        static string Key(string s) => new([.. s.Where(char.IsLetterOrDigit)]);
        string ka = Key(a), kb = Key(b);
        if (ka.Length == 0 || kb.Length == 0) return false;
        return ka.StartsWith(kb, StringComparison.OrdinalIgnoreCase) || kb.StartsWith(ka, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex BulletMarker = new(@"^([•◦‣▪●○·⁃∙])\s+", RegexOptions.Compiled);
    private static readonly Regex AmbiguousBullet = new(@"^([-–—*o])\s+", RegexOptions.Compiled);
    private static readonly Regex NumberMarker = new(@"^\(?(\d{1,3}|[a-zA-Z]|[ivxlcdmIVXLCDM]{1,6})[.)]\s+", RegexOptions.Compiled);

    /// <summary>
    /// An unmistakable marker at the start of a line. The dash and the star are left out on purpose: they
    /// open ordinary prose often enough that breaking on them would chop real paragraphs in half, and in a
    /// real column a dashed list is caught by the short-line rule anyway.
    /// </summary>
    private static bool OpensAListItem(string text) =>
        BulletMarker.IsMatch(text) || NumberMarker.IsMatch(text);

    private static void MarkLists(List<Para> paragraphs)
    {
        var kinds = new DocxListKind[paragraphs.Count];
        var certain = new bool[paragraphs.Count];
        var lengths = new int[paragraphs.Count];

        for (int i = 0; i < paragraphs.Count; i++)
        {
            string text = paragraphs[i].Lines.Count > 0 ? paragraphs[i].Lines[0].Text : string.Empty;
            if (BulletMarker.Match(text) is { Success: true } bullet)
            {
                kinds[i] = DocxListKind.Bullet;
                certain[i] = true;
                lengths[i] = bullet.Length;
            }
            else if (AmbiguousBullet.Match(text) is { Success: true } dash)
            {
                kinds[i] = DocxListKind.Bullet;
                lengths[i] = dash.Length;
            }
            else if (NumberMarker.Match(text) is { Success: true } number)
            {
                kinds[i] = DocxListKind.Number;
                lengths[i] = number.Length;
            }
        }

        for (int i = 0; i < paragraphs.Count; i++)
        {
            if (kinds[i] == DocxListKind.None) continue;

            // An unmistakable bullet stands on its own. A dash, a star or "3." is ordinary punctuation
            // often enough that it needs corroboration: a neighbour in the same list, or a hanging indent.
            bool corroborated = certain[i]
                || (i > 0 && kinds[i - 1] == kinds[i])
                || (i + 1 < paragraphs.Count && kinds[i + 1] == kinds[i])
                || HasHangingIndent(paragraphs[i]);
            if (!corroborated) continue;

            var para = paragraphs[i];
            para.List = kinds[i];
            para.MarkerLength = lengths[i];
            para.ListLevel = Math.Clamp((int)Math.Round((para.Bounds.Left - para.Column.Left) / 0.035), 0, 4);
        }
    }

    private static bool HasHangingIndent(Para para) =>
        para.Lines.Count > 1 && para.Lines.Skip(1).Min(l => l.Bounds.Left) > para.Lines[0].Bounds.Left + 0.004;

    // ---- runs ----

    private static DocxParagraph BuildParagraph(
        Para para, PageContent page, PageExtras extras, DocumentProfile profile,
        ExportOptions options, double previousBottom)
    {
        var runs = BuildRuns(para, page, extras, options);
        double pageWidth = page.Size.Width, pageHeight = page.Size.Height;

        int indent = 0, firstLine = 0;
        if (para.List == DocxListKind.None)
        {
            double left = para.Lines.Min(l => l.Bounds.Left);
            indent = PointsToTwips((left - para.Column.Left) * pageWidth);
            if (indent < 40) indent = 0; // under two points is measurement noise, not an indent

            if (para.Lines.Count > 1)
            {
                double rest = para.Lines.Skip(1).Min(l => l.Bounds.Left);
                firstLine = PointsToTwips((para.Lines[0].Bounds.Left - rest) * pageWidth);
                if (Math.Abs(firstLine) < 40) firstLine = 0;
                if (firstLine < 0) indent = PointsToTwips((rest - para.Column.Left) * pageWidth);
            }
        }

        int spaceBefore = 0;
        if (!double.IsNaN(previousBottom))
        {
            double gapPoints = (para.Bounds.Top - previousBottom) * pageHeight;
            double normal = profile.LinePitchPoints;
            spaceBefore = PointsToTwips(Math.Clamp(gapPoints - normal * 0.45, 0, 36));
            if (spaceBefore < 40) spaceBefore = 0;
        }

        int lineSpacing = 0;
        if (para.Lines.Count > 1)
        {
            double pitch = (para.Lines[^1].Bounds.Top - para.Lines[0].Bounds.Top) / (para.Lines.Count - 1) * pageHeight;
            double ratio = para.Size > 0 ? pitch / para.Size : 0;
            if (ratio is > 1.05 and < 3.0) lineSpacing = PointsToTwips(pitch);
        }

        return new DocxParagraph(runs)
        {
            Style = para.Style,
            Alignment = para.Alignment,
            IndentTwips = Math.Max(0, indent),
            FirstLineTwips = firstLine,
            SpaceBeforeTwips = spaceBefore,
            LineSpacingTwips = lineSpacing,
            List = para.List,
            ListLevel = para.ListLevel,
        };
    }

    private static List<DocxRun> BuildRuns(Para para, PageContent page, PageExtras extras, ExportOptions options)
    {
        var runs = new List<DocxRun>();
        var buffer = new StringBuilder();
        RunKey? key = null;
        string text = page.Text.Text;
        double paraSize = para.Size;

        void Flush()
        {
            if (buffer.Length == 0 || key is not { } k) { buffer.Clear(); return; }
            runs.Add(new DocxRun(buffer.ToString(), k.Style)
            {
                Script = k.Script,
                Underline = k.Underline,
                Strikethrough = k.Strike,
                Hyperlink = k.Link,
                Highlight = k.Highlight,
            });
            buffer.Clear();
        }

        for (int index = 0; index < para.Lines.Count; index++)
        {
            var line = para.Lines[index];
            int from = line.Start, to = line.End;

            // Trim the whitespace the PDF put at the ends of the line, and any list marker.
            while (from < to && char.IsWhiteSpace(text[from])) from++;
            while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
            if (index == 0 && para.MarkerLength > 0) from = Math.Min(to, from + para.MarkerLength);
            while (from < to && char.IsWhiteSpace(text[from])) from++;

            bool joinedByHyphen = false;
            if (index < para.Lines.Count - 1 && to > from && IsSoftHyphenBreak(text, from, to, para.Lines[index + 1], page))
            {
                to--;                 // drop the hyphen: the word continues on the next line
                joinedByHyphen = true;
            }

            for (int i = from; i < to; i++)
            {
                char c = text[i];
                if (c == '\n') continue;
                var next = KeyFor(i, c, page, extras, para, line, paraSize, options);
                if (key is not { } k || !k.Equals(next))
                {
                    Flush();
                    key = next;
                }
                buffer.Append(c);
            }

            if (index < para.Lines.Count - 1 && !joinedByHyphen && buffer.Length > 0)
            {
                // Lines of one paragraph are joined with a single space, which is what reflowing needs.
                if (buffer[^1] is not ' ') buffer.Append(' ');
            }
        }

        Flush();
        if (runs.Count == 0) runs.Add(new DocxRun(string.Empty, para.Lines.Count > 0 ? para.Lines[0].Style : TextStyle.Default));
        return runs;
    }

    private readonly record struct RunKey(
        TextStyle Style, DocxScript Script, bool Underline, bool Strike, string? Link, uint? Highlight);

    private static RunKey KeyFor(
        int index, char c, PageContent page, PageExtras extras, Para para, Line line,
        double paraSize, ExportOptions options)
    {
        var style = page.StyleAt(index);
        var script = DocxScript.Baseline;

        if (page.Text.TryGetBox(index, out var box) && !box.IsEmpty && line.Bounds.Height > 0 &&
            style.SizePoints < paraSize * 0.88 && !char.IsWhiteSpace(c))
        {
            double offset = box.Center.Y - line.Bounds.Center.Y;
            if (offset < -line.Bounds.Height * 0.08) script = DocxScript.Superscript;
            else if (offset > line.Bounds.Height * 0.08) script = DocxScript.Subscript;

            // Word raises the run itself, so a script keeps the paragraph's size rather than the PDF's.
            if (script != DocxScript.Baseline) style = style with { SizePoints = paraSize };
        }

        string? link = null;
        uint? highlight = null;
        bool underline = false, strike = false;

        if (page.Text.TryGetBox(index, out var charBox) && !charBox.IsEmpty)
        {
            var point = charBox.Center;
            if (options.Hyperlinks)
            {
                foreach (var candidate in extras.Links)
                {
                    if (candidate.Uri is { Length: > 0 } uri && candidate.Bounds.Contains(point))
                    {
                        link = uri;
                        break;
                    }
                }
            }
            if (options.Annotations)
            {
                foreach (var annotation in extras.Annotations)
                {
                    if (!annotation.IsMarkup || !annotation.HitTest(point, 0)) continue;
                    switch (annotation.Kind)
                    {
                        case AnnotationKind.Highlight when annotation.Color is { } color:
                            highlight = (uint)((color.R << 16) | (color.G << 8) | color.B);
                            break;
                        case AnnotationKind.Underline or AnnotationKind.Squiggly:
                            underline = true;
                            break;
                        case AnnotationKind.StrikeOut:
                            strike = true;
                            break;
                    }
                }
            }
        }

        return new RunKey(style, script, underline, strike, link, highlight);
    }

    /// <summary>
    /// A hyphen at the end of a line is a line break inside a word only when the next line continues it in
    /// lower case. Before a capital or a digit it is a real hyphen and has to stay.
    /// </summary>
    private static bool IsSoftHyphenBreak(string text, int from, int to, Line next, PageContent page)
    {
        if (to - from < 2 || text[to - 1] is not ('-' or '­')) return false;
        if (!char.IsLetter(text[to - 2])) return false;

        for (int i = next.Start; i < next.End; i++)
        {
            char c = page.Text.Text[i];
            if (char.IsWhiteSpace(c)) continue;
            return char.IsLower(c);
        }
        return false;
    }

    // ---- tables ----

    private static DocxTable BuildTable(
        TableGrid grid, PageContent page, PageExtras extras, DocumentProfile profile, ExportOptions options)
    {
        double snap = TableBuilder.SnapFor(profile.GlyphHeight);
        var rules = page.Rules;

        var widths = new List<int>(grid.ColumnCount);
        for (int c = 0; c < grid.ColumnCount; c++)
            widths.Add(Math.Max(1, PointsToTwips((grid.Columns[c + 1] - grid.Columns[c]) * page.Size.Width)));

        var rows = new List<DocxRow>(grid.RowCount);
        for (int r = 0; r < grid.RowCount; r++)
        {
            var cells = new List<DocxCell>();
            int c = 0;
            while (c < grid.ColumnCount)
            {
                // A missing interior edge means the cell runs on into the next column.
                int span = 1;
                while (c + span < grid.ColumnCount &&
                       !TableBuilder.HasVerticalEdge(grid, rules, r, c + span, snap))
                    span++;

                var area = new RectD(grid.Columns[c], grid.Rows[r], grid.Columns[c + span], grid.Rows[r + 1]);
                bool continues = r > 0 && !TableBuilder.HasHorizontalEdge(grid, rules, r, c, snap);
                bool startsMerge = !continues && r + 1 < grid.RowCount &&
                                   !TableBuilder.HasHorizontalEdge(grid, rules, r + 1, c, snap);

                cells.Add(new DocxCell(continues ? [] : CellBlocks(area, page, extras, profile, options))
                {
                    ColumnSpan = span,
                    VerticalMerge = continues ? 2 : startsMerge ? 1 : 0,
                });
                c += span;
            }

            var row = new DocxRow(cells);
            // A first row set entirely in bold is a header row, and Word should repeat it across a page break.
            if (r == 0 && cells.Count > 1 && cells.All(IsBold)) row = row with { IsHeader = true };
            rows.Add(row);
        }

        return new DocxTable(rows, widths);

        static bool IsBold(DocxCell cell)
        {
            var runs = cell.Blocks.OfType<DocxParagraph>().SelectMany(p => p.Runs)
                .Where(r => r.Text.Trim().Length > 0).ToList();
            return runs.Count > 0 && runs.All(r => r.Style.Bold);
        }
    }

    private static List<DocxBlock> CellBlocks(
        RectD area, PageContent page, PageExtras extras, DocumentProfile profile, ExportOptions options)
    {
        var lines = LinesInside(page, area);
        if (lines.Count == 0) return [];

        var blocks = new List<DocxBlock>();
        foreach (var para in GroupParagraphs(lines, area, page))
        {
            para.Column = area;
            var paragraph = BuildParagraph(para, page, extras, profile, options, double.NaN);
            // Indents and spacing are measured against the page, which means nothing inside a cell.
            blocks.Add(paragraph with { IndentTwips = 0, FirstLineTwips = 0, SpaceBeforeTwips = 0, LineSpacingTwips = 0 });
        }
        return blocks;
    }

    /// <summary>The page's characters that fall inside a cell, cut into lines.</summary>
    private static List<Line> LinesInside(PageContent page, RectD area)
    {
        var lines = new List<Line>();

        foreach (var visual in page.Text.Lines)
        {
            if (visual.Bounds.IsEmpty || !visual.Bounds.Intersects(area)) continue;

            int start = -1;
            for (int i = visual.Start; i <= visual.End; i++)
            {
                bool inside = i < visual.End && page.Text.TryGetBox(i, out var box) && area.Contains(box.Center);
                if (inside)
                {
                    if (start < 0) start = i;
                    continue;
                }

                // Whitespace between two characters of the cell keeps the run going.
                if (start >= 0 && i < visual.End && char.IsWhiteSpace(page.Text.Text[i])) continue;

                if (start >= 0)
                {
                    if (Line.FromRange(page, start, i) is { } line) lines.Add(line);
                    start = -1;
                }
            }
        }

        lines.Sort((a, b) =>
        {
            int byTop = a.Bounds.Top.CompareTo(b.Bounds.Top);
            return byTop != 0 ? byTop : a.Bounds.Left.CompareTo(b.Bounds.Left);
        });
        return lines;
    }

    // ---- images ----

    private static void InsertImages(List<DocxBlock> blocks, PageContent page, List<Para> paragraphs, DocumentProfile profile)
    {
        foreach (var image in page.Images)
        {
            double widthPoints = image.Bounds.Width * page.Size.Width;
            double heightPoints = image.Bounds.Height * page.Size.Height;
            if (widthPoints < 4 || heightPoints < 4) continue;

            // Never wider than the text measure, or Word pushes it off the page.
            double maxWidth = page.Size.Width - profile.Margins.Left - profile.Margins.Right;
            if (maxWidth > 0 && widthPoints > maxWidth)
            {
                heightPoints *= maxWidth / widthPoints;
                widthPoints = maxWidth;
            }

            var picture = new DocxPicture(image, widthPoints, heightPoints)
            {
                Alignment = DocxAlignment.Center,
                SpaceBeforeTwips = 120,
                SpaceAfterTwips = 120,
            };

            int at = paragraphs.FindIndex(p => p.Bounds.Top >= image.Bounds.Top);
            if (at < 0 || at >= blocks.Count) blocks.Add(picture);
            else blocks.Insert(at, picture);
        }
    }

    // ---- sections ----

    private static List<DocxSection> BuildSections(
        List<(PageContent Page, List<DocxBlock> Blocks)> composed, DocumentProfile profile, ExportOptions options)
    {
        var sections = new List<DocxSection>();
        if (composed.Count == 0)
            return [new DocxSection(new PageSize(612, 792), DocxMargins.Default, [DocxParagraph.Empty])];

        var blocks = new List<DocxBlock>();
        var size = composed[0].Page.Size;

        foreach (var (page, pageBlocks) in composed)
        {
            bool sizeChanged = Math.Abs(page.Size.Width - size.Width) > 1 || Math.Abs(page.Size.Height - size.Height) > 1;
            if (sizeChanged && blocks.Count > 0)
            {
                sections.Add(MakeSection(size, blocks, profile, options));
                blocks = [];
                size = page.Size;
            }

            if (options.JoinAcrossPages && blocks.Count > 0 && pageBlocks.Count > 0 &&
                blocks[^1] is DocxParagraph tail && pageBlocks[0] is DocxParagraph head &&
                ContinuesAcrossPages(tail, head))
            {
                blocks[^1] = JoinParagraphs(tail, head);
                blocks.AddRange(pageBlocks.Skip(1));
            }
            else
            {
                blocks.AddRange(pageBlocks);
            }
        }

        sections.Add(MakeSection(size, blocks, profile, options));
        return sections;
    }

    private static DocxSection MakeSection(
        PageSize size, List<DocxBlock> blocks, DocumentProfile profile, ExportOptions options)
    {
        if (blocks.Count == 0) blocks.Add(DocxParagraph.Empty);
        return new DocxSection(size, profile.Margins, blocks)
        {
            Header = options.HeadersFooters ? profile.Header : null,
            Footer = options.HeadersFooters ? profile.Footer : null,
        };
    }

    /// <summary>
    /// A paragraph broken by a page break: the foot of the page ran to the measure and stopped mid-sentence,
    /// and the head of the next page picks it up in lower case.
    /// </summary>
    private static bool ContinuesAcrossPages(DocxParagraph tail, DocxParagraph head)
    {
        if (tail.Style != DocxParagraphStyle.Body || head.Style != DocxParagraphStyle.Body) return false;
        if (tail.List != DocxListKind.None || head.List != DocxListKind.None) return false;

        string a = tail.Text.TrimEnd(), b = head.Text.TrimStart();
        if (a.Length == 0 || b.Length == 0) return false;
        if (a[^1] is '.' or '!' or '?' or ':' or ';' or '"' or '”') return false;
        return char.IsLower(b[0]) || b[0] is ',' or ';';
    }

    private static DocxParagraph JoinParagraphs(DocxParagraph tail, DocxParagraph head)
    {
        var runs = new List<DocxRun>(tail.Runs);
        if (runs.Count > 0 && runs[^1].Text.Length > 0 && !char.IsWhiteSpace(runs[^1].Text[^1]))
            runs[^1] = runs[^1] with { Text = runs[^1].Text + " " };
        runs.AddRange(head.Runs);
        return tail with { Runs = runs };
    }

    // ---- shared helpers ----

    private static int PointsToTwips(double points) => (int)Math.Round(points * 20, MidpointRounding.AwayFromZero);

    private static RectD Bounds(IEnumerable<Line> lines)
    {
        var bounds = RectD.Empty;
        foreach (var line in lines) bounds = bounds.Union(line.Bounds);
        return bounds;
    }

    /// <summary>Width of the first word of a line, from the boxes of its characters.</summary>
    private static double FirstWordWidth(Line line, PageContent page)
    {
        var bounds = RectD.Empty;
        for (int i = line.Start; i < line.End && i < page.Text.Length; i++)
        {
            if (char.IsWhiteSpace(page.Text.Text[i]))
            {
                if (!bounds.IsEmpty) break;
                continue;
            }
            if (page.Text.TryGetBox(i, out var box) && !box.IsEmpty) bounds = bounds.Union(box);
        }
        return bounds.IsEmpty ? 0 : bounds.Width;
    }

    private static double AverageCharWidth(Line line) =>
        line.Text.Length == 0 ? line.Bounds.Width : line.Bounds.Width / line.Text.Length;

    /// <summary>
    /// The typical gap between two lines of the same paragraph. The lower median, not the upper one: with
    /// an even number of gaps, half of which are the wider gaps *between* paragraphs, the upper median
    /// returns a paragraph gap and then nothing looks like a paragraph break at all.
    /// </summary>
    private static double MedianGap(List<Line> lines)
    {
        if (lines.Count < 2) return 0;
        var gaps = new List<double>(lines.Count - 1);
        for (int i = 1; i < lines.Count; i++)
            gaps.Add(lines[i].Bounds.Top - lines[i - 1].Bounds.Bottom);
        gaps.Sort();
        return gaps[(gaps.Count - 1) / 2];
    }

    internal static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }
}
