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

    /// <summary>Carry vector artwork — charts, diagrams, logos — across as pictures.</summary>
    public bool Drawings { get; init; } = true;

    /// <summary>Use the structure tree when the PDF is tagged, rather than inferring everything.</summary>
    public bool UseTags { get; init; } = true;

    /// <summary>
    /// Rebuild tables that draw no lines. Off by default: see <see cref="UnruledTableBuilder"/> for why a
    /// false positive here is worse than the table it would have found.
    /// </summary>
    public bool UnruledTables { get; init; }

    /// <summary>Embed the PDF's font programs. Off by default; an embedded subset cannot be typed in.</summary>
    public bool EmbedFonts { get; init; }

    /// <summary>Joins a paragraph that runs from the foot of one page to the head of the next.</summary>
    public bool JoinAcrossPages { get; init; } = true;

    /// <summary>
    /// Keep source line endings inside paragraphs instead of reflowing them. Off by default: Word never
    /// sets a line exactly as wide as the PDF did, so a forced break after a line Word had to wrap leaves
    /// a one-word line behind it, and every one of them pushes the page further down.
    /// </summary>
    public bool PreserveLineBreaks { get; init; }

    /// <summary>Where a source page boundary becomes a page break in Word. See <see cref="PageBreakMode"/>.</summary>
    public PageBreakMode PageBreaks { get; init; } = PageBreakMode.Deliberate;

    /// <summary>
    /// The line height Word gives each installed font. With it, line spacing is written as a multiple that
    /// still behaves when the text is edited; without it, as an exact height that at least matches the page.
    /// </summary>
    public IFontMetrics? FontMetrics { get; init; }

    public static ExportOptions Default { get; } = new();

    /// <summary>What the reader has to go and fetch for these options; the costly parts are opt-in.</summary>
    public PageContentRequest ToPageRequest() => new()
    {
        Drawings = Drawings && Images,
        Tags = UseTags,
        Fonts = EmbedFonts,
    };
}

/// <summary>How the page boundaries of the PDF carry over to Word.</summary>
public enum PageBreakMode
{
    /// <summary>One continuous flow; Word paginates it however the text falls.</summary>
    None,

    /// <summary>
    /// A new page only where the author started one: the previous page stopped well short of its foot, as a
    /// title page or the end of a chapter does. A full page flows on, so text Word sets a little longer
    /// than the PDF runs onto the next page instead of leaving a near-empty page behind it.
    /// </summary>
    Deliberate,

    /// <summary>Every source page starts a new page, whether or not the previous one was full.</summary>
    EveryPage,
}

/// <summary>Font measurements the host can supply; Core has no access to installed fonts of its own.</summary>
public interface IFontMetrics
{
    /// <summary>
    /// Word's single line height for <paramref name="family"/> as a multiple of the font size, or null when
    /// the font is not installed — in which case Word substitutes another and its height is unknowable.
    /// </summary>
    double? LineHeight(string family);

    /// <summary>
    /// How far below the top of Word's line the baseline sits at single spacing, as a multiple of the font
    /// size, or null when the font is not installed.
    /// </summary>
    double? Ascent(string family) => null;
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

    /// <summary>Points left under a full-height picture for the paragraph mark that follows it.</summary>
    private const double PictureHeadroom = 14;

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
        var comments = new CommentSink();

        var composed = new List<ComposedPage>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var pageExtras = extras is not null && i < extras.Count ? extras[i] : PageExtras.None;
            composed.Add(ComposePage(page, pageExtras, profile, headings, options, comments));
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
            Comments = comments.Comments,
            Fonts = options.EmbedFonts ? CollectFonts(pages) : [],
        };
    }

    /// <summary>One font program per family and style, the first one seen winning.</summary>
    private static List<EmbeddedFont> CollectFonts(IReadOnlyList<PageContent> pages)
    {
        const int MaxFonts = 24;
        var fonts = new List<EmbeddedFont>();
        var seen = new HashSet<(string, bool, bool)>();

        foreach (var page in pages)
        {
            foreach (var font in page.Fonts)
            {
                if (fonts.Count >= MaxFonts) return fonts;
                if (font.Data.Length == 0 || !seen.Add((font.Family, font.Bold, font.Italic))) continue;
                fonts.Add(font);
            }
        }
        return fonts;
    }

    /// <summary>Collects the document's comments so their ids are unique across every page.</summary>
    private sealed class CommentSink
    {
        public List<DocxComment> Comments { get; } = [];

        public int Add(string author, string text, DateTimeOffset? date)
        {
            int id = Comments.Count + 1;
            string name = string.IsNullOrWhiteSpace(author) ? "PDF" : author.Trim();
            Comments.Add(new DocxComment(id, name, text) { Date = date, Initials = InitialsOf(name) });
            return id;
        }

        private static string InitialsOf(string author)
        {
            var initials = author.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Where(part => char.IsLetter(part[0]))
                .Select(part => char.ToUpperInvariant(part[0]))
                .Take(3);
            string text = new([.. initials]);
            return text.Length > 0 ? text : "PDF";
        }
    }

    // ---- page ----

    /// <summary>
    /// A table found on the page, and how to build it once its place in the flow is reached.
    /// <see cref="Claims"/> decides which paragraphs it swallows, which is the only way text can be lost:
    /// a paragraph the table claims but does not hold would leave the document altogether.
    /// </summary>
    private sealed record TableCandidate(RectD Bounds, Func<DocxTable> Build, Func<Para, bool>? Claims = null)
    {
        public bool Owns(Para para) =>
            Bounds.Contains(para.Bounds.Center) && (Claims is null || Claims(para));
    }

    /// <summary>
    /// What a page contributes: its blocks, and how far down the page its content reaches, normalized. The
    /// extent is what tells a full page from one its author ended early.
    /// </summary>
    private sealed record ComposedPage(PageContent Page, List<DocxBlock> Blocks, double ContentTop, double ContentBottom);

    /// <summary>Something laid out in the text flow of a page, in reading order.</summary>
    private abstract record FlowItem(RectD Bounds);

    private sealed record ParaFlow(Para Para) : FlowItem(Para.Bounds);

    private sealed record TableFlow(DocxTable Table, RectD Area) : FlowItem(Area);

    private sealed record PictureFlow(DocxPicture Picture, RectD Area) : FlowItem(Area);

    /// <summary>Where a zone of the page starts in the flow; columns begin here when it has them.</summary>
    private sealed record ZoneFlow(Zone Zone, int Index) : FlowItem(new RectD(Zone.Rect.Left, Zone.Rect.Top, Zone.Rect.Right, Zone.Rect.Top));

    /// <summary>Where column <see cref="Column"/> of a zone ends in the flow and the next one begins.</summary>
    private sealed record ColumnEndFlow(Zone Zone, int Column) : FlowItem(RectD.Empty);

    /// <summary>Marks the start of a zone in the blocks of a page, for the sections to be cut there.</summary>
    private sealed record ZoneBreak(DocxColumns Columns) : DocxBlock;

    private static void CloseColumns(List<FlowItem> flow, List<Zone> zones, int zone, int unit)
    {
        if (zone < 0 || !zones[zone].IsColumns) return;
        for (int u = unit; u < zones[zone].Units.Count - 1; u++) flow.Add(new ColumnEndFlow(zones[zone], u));
    }

    /// <summary>Pictures wide enough that they could cut across a column block.</summary>
    private static IEnumerable<RectD> WideImages(PageContent page, List<Zone> zones)
    {
        foreach (var image in page.Images)
        {
            var b = image.Bounds;
            if (b.Width * b.Height > 0.5) continue;                    // a page background, not a figure
            if (b.Bottom <= ContentComposerBands.Header || b.Top >= ContentComposerBands.Footer) continue;
            yield return b;
        }
    }

    /// <summary>True when the block sits in a column zone and reaches into more than one of its columns.</summary>
    private static bool CutsAcrossColumns(RectD bounds, List<Zone> zones)
    {
        foreach (var zone in zones)
        {
            if (!zone.IsColumns) continue;
            if (bounds.Bottom <= zone.Rect.Top || bounds.Top >= zone.Rect.Bottom) continue;
            int reached = zone.Bands.Count(band =>
                Math.Min(bounds.Right, band.Right) - Math.Max(bounds.Left, band.Left) > (band.Right - band.Left) * 0.15);
            if (reached > 1) return true;
        }
        return false;
    }

    /// <summary>The Word columns for a zone, fitted to the section's text width.</summary>
    private static DocxColumns ColumnsFor(Zone zone, PageContent page, DocumentProfile profile)
    {
        if (!zone.IsColumns) return DocxColumns.Single;
        double width = page.Size.Width;
        var widths = zone.Bands.Select(b => (b.Right - b.Left) * width).ToList();
        var gaps = new List<double>();
        for (int i = 1; i < zone.Bands.Count; i++) gaps.Add(Math.Max(6, (zone.Bands[i].Left - zone.Bands[i - 1].Right) * width));

        // The bands are measured from the text in them, which stops short of the column's own right edge,
        // so they are scaled up together until they fill the text area Word will have.
        double measure = width - profile.Margins.Left - profile.Margins.Right;
        double total = widths.Sum() + gaps.Sum();
        double scale = total > 0 ? measure / total : 1;
        widths = [.. widths.Select(w => w * scale)];
        gaps = [.. gaps.Select(g => g * scale)];

        bool equal = widths.Max() <= widths.Min() * 1.06;
        return new DocxColumns(zone.Bands.Count, gaps.Average())
        {
            WidthsPoints = equal ? [] : widths,
            GapsPoints = equal ? [] : gaps,
        };
    }

    /// <summary>A picture placed off the flow, and the paragraph it hangs from (null: from the page).</summary>
    private sealed record Floating(DocxPicture Picture, RectD Area, Para? Anchor);

    private static ComposedPage ComposePage(
        PageContent page, PageExtras extras, DocumentProfile profile,
        ILookup<int, (double Y, int Depth, string Title)> headings, ExportOptions options, CommentSink comments)
    {
        var tags = options.UseTags ? TagIndex.Build(page) : TagIndex.Empty;
        var lines = ReadLines(page, profile, options);

        // Tables and panels first: a paragraph never runs from inside one to outside it. A table takes the
        // paragraphs its lines are in and rebuilds them as cells, so a line outside it caught in the same
        // paragraph — a note the file forgot to tag, the sentence just above the first row — would be lost.
        var ruled = TableBuilder.Find(page.Rules, profile.GlyphHeight).Select(g => g.Bounds).ToList();
        var grids = ruled.Concat(tags.Tables.Select(t => t.Bounds)).ToList();
        var claims = tags.Tables.Select(t => t.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Ranges).ToList()).ToList();
        var panels = FindPanels(page, profile, lines, grids);
        Dictionary<Line, (Panel? Panel, int Table)>? regions = null;
        if (panels.Count > 0 || grids.Count > 0)
        {
            regions = [];
            foreach (var line in lines)
            {
                int table = ruled.FindIndex(g => g.Contains(line.Bounds.Center));
                if (table < 0 && claims.FindIndex(c => c.Any(r => r.Start < line.End && r.End > line.Start)) is var claimed and >= 0)
                    table = ruled.Count + claimed;
                regions[line] = (PanelOf(line, panels, page), table);
            }
        }

        var paragraphs = new List<Para>();
        var zones = SplitIntoZones(lines, profile, page, tags.Tables.Count > 0 || options.UnruledTables, grids);
        var columns = zones.SelectMany(z => z.Units).Where(u => u.Lines.Count > 0).ToList();
        var home = new Dictionary<Para, (int Zone, int Unit)>(ReferenceEqualityComparer.Instance);
        for (int z = 0; z < zones.Count; z++)
        {
            for (int u = 0; u < zones[z].Units.Count; u++)
            {
                var (column, columnLines) = zones[z].Units[u];
                foreach (var para in GroupParagraphs(columnLines, column, page, regions))
                {
                    paragraphs.Add(para);
                    home[para] = (z, u);
                }
            }
        }

        if (!tags.IsEmpty) paragraphs = ApplyTags(paragraphs, tags, profile, options);
        if (options.Lists) MarkLists(paragraphs);
        if (options.Headings) MarkHeadings(paragraphs, profile, headings, page.PageIndex);

        var tables = FindTables(page, extras, profile, options, tags, columns);

        // Columns are carried into Word only where nothing on the page cuts across them. A table or a
        // figure spanning two columns in the middle of a column block has no place in Word's sections, and
        // then the page is read column by column into a single measure instead, which loses nothing.
        bool wordColumns = zones.Any(z => z.IsColumns) &&
            !tables.Select(t => t.Bounds).Concat(WideImages(page, zones)).Any(b => CutsAcrossColumns(b, zones));

        var flow = new List<FlowItem>(paragraphs.Count);
        var emitted = new HashSet<int>();
        int currentZone = -1, currentUnit = 0;

        foreach (var para in paragraphs)
        {
            if (wordColumns)
            {
                var (z, u) = home.TryGetValue(para, out var where) ? where : (Math.Max(0, currentZone), currentUnit);
                if (z != currentZone)
                {
                    CloseColumns(flow, zones, currentZone, currentUnit);
                    flow.Add(new ZoneFlow(zones[z], z));
                    (currentZone, currentUnit) = (z, 0);
                }
                for (; zones[z].IsColumns && currentUnit < u; currentUnit++)
                    flow.Add(new ColumnEndFlow(zones[z], currentUnit));
            }

            int table = tables.FindIndex(t => t.Owns(para));
            if (table >= 0)
            {
                // The text of the table is rebuilt from the page rather than from these paragraphs: a row
                // of cells is one visual line, so it has to be cut apart by position, not by line.
                if (emitted.Add(table)) flow.Add(new TableFlow(tables[table].Build(), tables[table].Bounds));
                continue;
            }
            flow.Add(new ParaFlow(para));
        }
        if (wordColumns) CloseColumns(flow, zones, currentZone, currentUnit);

        // A table no paragraph sits inside has nothing to hang off and would be dropped — unless it is
        // empty, in which case there is nothing to lose and a stray grid would only be noise.
        for (int i = 0; i < tables.Count; i++)
        {
            if (!emitted.Add(i)) continue;
            var table = tables[i].Build();
            if (table.Rows.SelectMany(r => r.Cells).Any(c => c.Blocks.Count > 0))
                InsertByPosition(flow, new TableFlow(table, tables[i].Bounds));
        }

        var floating = new List<Floating>();
        if (options.Images && page.Images.Count > 0)
        {
            // What Word now draws itself — a panel, a table's shading and rules — is not also a picture.
            var redrawn = panels.Select(p => p.Bounds).Concat(tables.Select(t => t.Bounds)).ToList();
            PlacePictures(page, profile, tags, options, flow, floating, redrawn);
        }

        var blocks = new List<DocxBlock>(flow.Count);
        var placed = new List<(Para Para, int Block)>(paragraphs.Count);
        LayOut(flow, blocks, placed, page, extras, profile, options);

        AttachRules(blocks, placed, page, profile, tables.Select(t => t.Bounds).ToList(), panels);
        if (panels.Count > 0) AttachPanels(blocks, placed, page, profile, options);
        AttachFloating(blocks, placed, floating, page, profile, options);
        if (options.Annotations) AttachComments(blocks, placed, extras, comments);

        // Pictures behind the text are decoration, and so is anything standing in the margin beside the text
        // area; everything else is content the page had to make room for.
        double areaLeft = profile.Margins.Left / page.Size.Width;
        double areaRight = 1 - profile.Margins.Right / page.Size.Width;
        var extent = flow.Where(f => f is not (ZoneFlow or ColumnEndFlow)).Select(f => f.Bounds)
            .Concat(floating.Where(f => f.Picture.Wrap == DocxWrap.Square).Select(f => f.Area))
            .Where(b => !b.IsEmpty && Math.Min(b.Right, areaRight) - Math.Max(b.Left, areaLeft) >= b.Width * 0.5)
            .ToList();
        double top = extent.Count > 0 ? extent.Min(b => b.Top) : double.NaN;
        double bottom = extent.Count > 0 ? extent.Max(b => b.Bottom) : double.NaN;
        return new ComposedPage(page, blocks, top, bottom);
    }

    /// <summary>
    /// Turns the flow into blocks, spacing each from the one before it.
    ///
    /// Spacing is measured top to top, which is how Word stacks lines: the next block starts one line pitch
    /// below the top of the previous paragraph's last line, plus whatever gap the page shows beyond that.
    /// The pitch subtracted is the one the previous paragraph is actually written with, so the two cannot
    /// disagree and the error cannot accumulate down the page.
    /// </summary>
    private static void LayOut(
        List<FlowItem> flow, List<DocxBlock> blocks, List<(Para Para, int Block)> placed,
        PageContent page, PageExtras extras, DocumentProfile profile, ExportOptions options)
    {
        double height = page.Size.Height;
        double maxGap = height / 3;
        FlowItem? previous = null;
        double previousPitch = 0, previousOffset = 0;

        // At the top of every column the text is spaced from what stood above the zone, as the first
        // column is; and a column the author ended early is ended in Word too.
        (FlowItem? Item, double Pitch, double Offset) zoneEntry = (null, 0, 0);
        int columnStart = 0;
        double columnBottom = double.NaN;
        double breakRoom = profile.PitchFor(profile.BodySize) * 3;

        foreach (var item in flow)
        {
            switch (item)
            {
                case ZoneFlow zone:
                    blocks.Add(new ZoneBreak(ColumnsFor(zone.Zone, page, profile)));
                    zoneEntry = (previous, previousPitch, previousOffset);
                    columnStart = blocks.Count;
                    columnBottom = double.NaN;
                    continue;

                case ColumnEndFlow end:
                    bool empty = blocks.Count == columnStart;
                    bool early = empty || double.IsNaN(columnBottom) ||
                                 (end.Zone.Rect.Bottom - columnBottom) * height > breakRoom;
                    if (early)
                    {
                        if (!empty && blocks[^1] is DocxParagraph last) blocks[^1] = last with { ColumnBreakAfter = true };
                        else blocks.Add(Spacer with { ColumnBreakAfter = true });
                    }
                    (previous, previousPitch, previousOffset) = zoneEntry;
                    columnStart = blocks.Count;
                    columnBottom = double.NaN;
                    continue;
            }
            columnBottom = double.IsNaN(columnBottom) ? item.Bounds.Bottom : Math.Max(columnBottom, item.Bounds.Bottom);

            // What Word places at a block's top is its line box; what the PDF shows is the glyphs, which sit
            // some way down inside it. Between two paragraphs the two offsets largely cancel. Against a
            // table or a picture, whose top is its top, they do not, and ignoring them lifts the text a
            // couple of points before every table and drops it as much after.
            double offset = item is ParaFlow { Para: var next } ? TextOffset(next, PitchOf(next, page, profile), page, options) : 0;
            double gap = 0;
            if (previous is ParaFlow { Para: var above })
                gap = (item.Bounds.Top - above.Lines[^1].Bounds.Top) * height - previousPitch + previousOffset - offset;
            else if (previous is not null)
                gap = (item.Bounds.Top - previous.Bounds.Bottom) * height - offset;
            gap = Math.Clamp(gap, 0, maxGap);

            switch (item)
            {
                case ParaFlow { Para: var para }:
                    double pitch = PitchOf(para, page, profile);
                    placed.Add((para, blocks.Count));
                    blocks.Add(BuildParagraph(para, page, extras, profile, options, gap, pitch));
                    previousPitch = pitch;
                    previousOffset = offset;
                    break;

                case TableFlow table:
                    // A table has no space before of its own; the gap above it belongs to what precedes it.
                    if (blocks.Count > 0 && blocks[^1] is DocxParagraph before)
                        blocks[^1] = before with { SpaceAfterTwips = SpacingTwips(gap) };
                    blocks.Add(table.Table);
                    break;

                case PictureFlow picture:
                    blocks.Add(picture.Picture with { SpaceBeforeTwips = SpacingTwips(gap) });
                    break;
            }
            previous = item;
        }
    }

    /// <summary>
    /// How far below the top of Word's line box the paragraph's glyphs start, in points. Measured against
    /// Word: the baseline sits at the font's ascent for single spacing and any larger multiple (the extra
    /// goes below the text), at that ascent scaled down for a multiple under one, and at four fifths of
    /// the line for exact spacing. The glyph box PDFium reports reaches three quarters of its height above
    /// the baseline. Together these land within a third of a point for every face tried.
    /// </summary>
    private static double TextOffset(Para para, double pitchPoints, PageContent page, ExportOptions options)
    {
        if (para.Lines.Count == 0 || pitchPoints <= 0) return 0;
        double size = para.Size;
        var heights = para.Lines.Select(l => l.Bounds.Height * page.Size.Height).ToList();
        double glyph = Median(heights);
        var (value, rule) = LineSpacingFor(para, page, pitchPoints, options);

        double baseline;
        if (rule == DocxLineRule.Exact)
        {
            baseline = 0.8 * pitchPoints;
        }
        else
        {
            double ascent = options.FontMetrics?.Ascent(para.Lines[0].Style.FontFamily) ?? 0.92;
            double multiple = rule == DocxLineRule.Auto && value > 0 ? value / 240.0 : 1;
            baseline = Math.Min(1, multiple) * ascent * size;
        }
        return Math.Clamp(baseline - 0.75 * glyph, -size, size);
    }

    /// <summary>
    /// A paragraph with its space before changed, and the pictures hung from it moved by as much: they are
    /// placed from the top of that space, so without this they would stay put while the text moved.
    /// </summary>
    private static DocxParagraph WithSpaceBefore(DocxParagraph paragraph, int twips)
    {
        int delta = twips - paragraph.SpaceBeforeTwips;
        if (delta == 0) return paragraph;
        var floating = paragraph.Floating.Count == 0 ? paragraph.Floating
            : [.. paragraph.Floating.Select(f => f.VerticalFromPage ? f : f with { OffsetYPoints = f.OffsetYPoints + delta / 20.0 })];
        return paragraph with { SpaceBeforeTwips = twips, Floating = floating };
    }

    /// <summary>A gap under a point is measurement noise, not spacing anyone set.</summary>
    private static int SpacingTwips(double points) => points < 1 ? 0 : PointsToTwips(points);

    /// <summary>
    /// Puts a block that belongs to no paragraph into the reading order: before the first block below it
    /// that shares its horizontal extent, so a table or figure in the second column follows that column's
    /// text above it rather than the first column's.
    /// </summary>
    private static void InsertByPosition(List<FlowItem> flow, FlowItem item)
    {
        var bounds = item.Bounds;

        // Inside a column zone, the item belongs to the column it stands in, between that column's text
        // above and below it — not to whichever column's text happens to come next in reading order.
        int start = 0, end = flow.Count;
        int zoneAt = flow.FindLastIndex(f => f is ZoneFlow z && z.Zone.Rect.Top <= bounds.Center.Y);
        if (zoneAt >= 0 && flow[zoneAt] is ZoneFlow { Zone: var zone } && bounds.Center.Y <= zone.Rect.Bottom)
        {
            start = zoneAt + 1;
            end = flow.FindIndex(start, f => f is ZoneFlow);
            if (end < 0) end = flow.Count;
            if (zone.IsColumns)
            {
                int column = 0;
                double best = -1;
                for (int i = 0; i < zone.Bands.Count; i++)
                {
                    double overlap = Math.Min(bounds.Right, zone.Bands[i].Right) - Math.Max(bounds.Left, zone.Bands[i].Left);
                    if (overlap > best) (best, column) = (overlap, i);
                }
                for (int i = start, seen = 0; i < end; i++)
                {
                    if (flow[i] is not ColumnEndFlow) continue;
                    if (seen == column - 1) start = i + 1;
                    if (seen == column) { end = i; break; }
                    seen++;
                }
            }
        }
        else if (zoneAt >= 0)
        {
            // Between zones: after the zone it follows, before the next one starts.
            start = flow.FindIndex(zoneAt + 1, f => f is ZoneFlow) is var next and >= 0 ? next : flow.Count;
            end = start;
        }

        int at = -1;
        for (int i = start; i < end && at < 0; i++)
            if (flow[i] is not (ZoneFlow or ColumnEndFlow) && flow[i].Bounds.Top >= bounds.Top - 1e-4 &&
                OverlapsHorizontally(flow[i].Bounds, bounds)) at = i;
        for (int i = start; i < end && at < 0; i++)
            if (flow[i] is not (ZoneFlow or ColumnEndFlow) && flow[i].Bounds.Top >= bounds.Top - 1e-4) at = i;
        flow.Insert(at < 0 ? end : at, item);
    }

    private static bool OverlapsHorizontally(RectD a, RectD b) => a.Left < b.Right && b.Left < a.Right;

    private static bool OverlapsVertically(RectD a, RectD b, double fraction)
    {
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return overlap > Math.Min(a.Height, b.Height) * fraction;
    }

    /// <summary>
    /// The distance from one line's top to the next inside the paragraph, in points. A paragraph of one line
    /// has none to measure, so it takes what the document uses for text of that size.
    /// </summary>
    private static double PitchOf(Para para, PageContent page, DocumentProfile profile)
    {
        double size = para.Size;
        if (para.Lines.Count >= 2)
        {
            var steps = new List<double>(para.Lines.Count - 1);
            for (int i = 1; i < para.Lines.Count; i++)
                steps.Add((para.Lines[i].Bounds.Top - para.Lines[i - 1].Bounds.Top) * page.Size.Height);
            double pitch = Median(steps);
            if (pitch >= size * 0.85 && pitch <= size * 3) return pitch;
        }
        return profile.PitchFor(size);
    }

    /// <summary>
    /// The tables on a page, in the order they are trusted: the grid a PDF actually draws first, then what
    /// a tagged file says is a table, and only then — and only when asked — what the alignment suggests.
    /// </summary>
    private static List<TableCandidate> FindTables(
        PageContent page, PageExtras extras, DocumentProfile profile, ExportOptions options,
        TagIndex tags, List<(RectD Column, List<Line> Lines)> columns)
    {
        var candidates = new List<TableCandidate>();
        if (!options.Tables) return candidates;

        foreach (var grid in TableBuilder.Find(page.Rules, profile.GlyphHeight))
            candidates.Add(new TableCandidate(grid.Bounds, () => BuildTable(grid, page, extras, profile, options)));

        foreach (var table in tags.Tables)
        {
            if (Overlaps(candidates, table.Bounds)) continue;

            // A tagged table holds exactly the characters its cells name. A line that merely sits inside
            // its bounds — a caption the file forgot to tag, a note in a margin — belongs to neither cell
            // and stays a paragraph, because a table that swallowed it would swallow it for good.
            var claimed = table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Ranges).ToList();
            candidates.Add(new TableCandidate(
                table.Bounds.Inflate(0.004, 0.004),
                () => BuildTaggedTable(table, page, extras, profile, options),
                para => para.Ranges.Any(r => claimed.Any(c => c.Start < r.End && c.End > r.Start))));
        }

        if (options.UnruledTables)
        {
            foreach (var (column, lines) in columns)
            {
                var spans = lines
                    .Select(l => new UnruledTableBuilder.LineSpan(l.Start, l.End, l.Bounds))
                    .ToList();
                foreach (var grid in UnruledTableBuilder.Find(page, spans, column, profile.GlyphHeight))
                {
                    if (Overlaps(candidates, grid.Bounds)) continue;
                    candidates.Add(new TableCandidate(grid.Bounds,
                        () => BuildTable(grid, page, extras, profile, options, borders: false)));
                }
            }
        }

        return candidates;

        static bool Overlaps(List<TableCandidate> candidates, RectD bounds) =>
            candidates.Any(c => c.Bounds.Intersects(bounds));
    }

    /// <summary>Visual lines with their text and dominant style, minus blanks and running heads.</summary>
    private static List<Line> ReadLines(PageContent page, DocumentProfile profile, ExportOptions options)
    {
        var text = page.Text;
        var lines = new List<Line>(text.Lines.Count);
        var sources = new List<(TextLine Visual, string Raw)>(text.Lines.Count);

        foreach (var visual in text.Lines)
        {
            if (visual.Bounds.IsEmpty || IsOffPage(visual.Bounds)) continue;
            string raw = visual.End > visual.Start ? text.Text[visual.Start..visual.End] : string.Empty;
            if (raw.Trim().Length == 0) continue;

            if (options.HeadersFooters && profile.IsRunningHead(visual.Bounds, raw)) continue;

            // Where a recognized page found a picture rather than words, the layout pass leaves a marker.
            // It stands for something the export carries as a picture, so it must never be written out as
            // the literal text "[Figure]".
            if (text.Source == TextSource.Ocr && raw.AsSpan().Trim().Equals(OcrLayout.FigurePlaceholder, StringComparison.Ordinal)) continue;

            lines.Add(Line.Create(page, visual, raw));
            sources.Add((visual, raw));
        }

        // Where parts of lines start on two or more lines of the page is a column of the text. Lines are
        // read again knowing those, so an entry whose gap is narrower still lines up with the rest.
        var shared = SharedStops(lines, page);
        if (shared.Count > 0)
            for (int i = 0; i < lines.Count; i++) lines[i] = Line.Create(page, sources[i].Visual, sources[i].Raw, shared);

        lines.Sort((a, b) =>
        {
            int byTop = a.Bounds.Top.CompareTo(b.Bounds.Top);
            return byTop != 0 ? byTop : a.Bounds.Left.CompareTo(b.Bounds.Left);
        });
        return lines;
    }

    /// <summary>Tab stop positions found on at least two lines, within two points of each other.</summary>
    private static List<double> SharedStops(List<Line> lines, PageContent page)
    {
        var all = lines.SelectMany((l, i) => l.TabStops.Select(x => (X: x, Line: i))).OrderBy(p => p.X).ToList();
        var shared = new List<double>();
        double tolerance = 2 / page.Size.Width;
        int start = 0;
        for (int i = 1; i <= all.Count; i++)
        {
            if (i < all.Count && all[i].X - all[i - 1].X <= tolerance) continue;
            var cluster = all.Skip(start).Take(i - start).ToList();
            if (cluster.Select(p => p.Line).Distinct().Count() >= 2) shared.Add(cluster.Average(p => p.X));
            start = i;
        }
        return shared;
    }

    /// <summary>
    /// Splits the page into reading units: one per column, or a single unit covering the whole text area.
    ///
    /// Columns are looked for first, over the narrow lines only. Only if the page really is in columns does
    /// a line that spans the measure (a headline over both columns) close the zone above it. Doing it the
    /// other way round would treat every line of an ordinary single-column page as a spanning line, because
    /// on such a page the text area and the line are the same width.
    /// </summary>
    /// <summary>
    /// A band of the page read as one: either a single measure, or columns side by side. <see cref="Units"/>
    /// are read in order; for columns there is one per band, empty where a column has no text.
    /// </summary>
    private sealed record Zone(List<(RectD Column, List<Line> Lines)> Units, IReadOnlyList<(double Left, double Right)> Bands, RectD Rect)
    {
        public bool IsColumns => Bands.Count > 1;
    }

    private static List<Zone> SplitIntoZones(
        List<Line> lines, DocumentProfile profile, PageContent page, bool hasTables, List<RectD> grids)
    {
        var zones = new List<Zone>();
        var units = SplitIntoColumns(lines, profile, page, hasTables, grids, zones);
        if (zones.Count == 0 && units.Count > 0)
            zones.Add(new Zone(units, [], Bounds(units.SelectMany(u => u.Lines))));
        return zones;
    }

    private static List<(RectD Column, List<Line> Lines)> SplitIntoColumns(
        List<Line> lines, DocumentProfile profile, PageContent page, bool hasTables, List<RectD> grids,
        List<Zone> zones)
    {
        var units = new List<(RectD, List<Line>)>();
        if (lines.Count == 0) return units;

        RectD area = Bounds(lines);
        RectD measure = TextMeasure(area, page, profile);

        // The cells of a table stand side by side like columns, but they are the table's: looked for among
        // them, columns turn up wherever a table does.
        var text = grids.Count == 0 ? lines : lines.Where(l => !grids.Any(g => g.Contains(l.Bounds.Center))).ToList();
        var bands = DetectColumnBands(text, area, profile.GlyphHeight);

        // PDFium can place both columns in one text line when their baselines match. Try splitting wide
        // whitespace corridors, but accept the split only when repeated prose establishes actual columns.
        if (bands.Count < 2 && !hasTables && page.Rules.Count == 0)
        {
            var split = lines.SelectMany(line => SplitColumnLine(line, page)).ToList();
            var candidateBands = DetectColumnBands(split, area, profile.GlyphHeight);
            if (candidateBands.Count >= 2)
            {
                lines = split.OrderBy(l => l.Bounds.Top).ThenBy(l => l.Bounds.Left).ToList();
                bands = candidateBands;
            }
        }

        if (bands.Count < 2)
        {
            units.Add((measure, lines));
            return units;
        }

        var zone = new List<Line>();
        var spanning = new List<Line>();

        // Word can set columns only where each is wide enough to hold a line of text; narrower bands are
        // still read in order, one after the other, but as a single measure.
        bool wordColumns = bands.All(b => b.Right - b.Left >= area.Width * 0.18);

        void FlushSpanning()
        {
            if (spanning.Count == 0) return;
            var bounds = Bounds(spanning);
            var rect = new RectD(measure.Left, bounds.Top, measure.Right, bounds.Bottom);
            units.Add((rect, [.. spanning]));
            zones.Add(new Zone([(rect, [.. spanning])], [], rect));
            spanning.Clear();
        }

        void FlushZone()
        {
            if (zone.Count == 0) return;
            var bounds = Bounds(zone);
            var zoneUnits = new List<(RectD, List<Line>)>();

            // Each line goes to the band it stands in, or the nearest one: a line centred in the gutter is
            // still text, and belongs to some column.
            var home = zone.ToLookup(l => Enumerable.Range(0, bands.Count).MinBy(b =>
                l.Bounds.Center.X < bands[b].Left ? bands[b].Left - l.Bounds.Center.X
                : l.Bounds.Center.X > bands[b].Right ? l.Bounds.Center.X - bands[b].Right : 0));
            for (int b = 0; b < bands.Count; b++)
            {
                var band = bands[b];
                var members = home[b].OrderBy(l => l.Bounds.Top).ToList();
                var rect = members.Count > 0
                    ? new RectD(band.Left, Bounds(members).Top, band.Right, Bounds(members).Bottom)
                    : new RectD(band.Left, bounds.Top, band.Right, bounds.Top);
                if (members.Count > 0) units.Add((rect, members));
                if (members.Count > 0 || wordColumns) zoneUnits.Add((rect, members));
            }
            var zoneRect = new RectD(measure.Left, bounds.Top, measure.Right, bounds.Bottom);
            zones.Add(wordColumns ? new Zone(zoneUnits, bands, zoneRect) : new Zone(zoneUnits, [], zoneRect));
            zone.Clear();
        }

        // Runs of lines that span the measure and of lines that do not, in reading order.
        var runs = new List<(bool Spanning, List<Line> Lines)>();
        foreach (var line in lines)
        {
            bool wide = area.Width > 0 && line.Bounds.Width >= area.Width * SpanningLineWidth;
            if (runs.Count == 0 || runs[^1].Spanning != wide) runs.Add((wide, []));
            runs[^1].Lines.Add(line);
        }

        // Narrow lines that only one column holds are not columns: they are the short lines of the text
        // around them — the end of a paragraph, a heading, the last line of a list item — and belong with it.
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Spanning) continue;
            int filled = bands.Count(b => runs[i].Lines.Any(l => l.Bounds.Center.X >= b.Left && l.Bounds.Center.X <= b.Right));
            if (filled < 2) runs[i] = (true, runs[i].Lines);
        }

        foreach (var (wide, run) in runs)
        {
            if (wide)
            {
                FlushZone();
                spanning.AddRange(run);  // consecutive spanning lines are one headline, not several
            }
            else
            {
                FlushSpanning();
                zone.AddRange(run);
            }
        }
        FlushZone();
        FlushSpanning();

        return units;
    }

    /// <summary>
    /// The measure a page is set to: its text area, from margin to margin, and wider only where a line
    /// reaches past it. The lines alone would do on a page of full paragraphs, but on a page of indented
    /// items or short centred lines they stop short of the margins — and every indent measured from them,
    /// and every centre, would be off by as much.
    /// </summary>
    private static RectD TextMeasure(RectD lines, PageContent page, DocumentProfile profile)
    {
        double width = page.Size.Width;
        double left = profile.TextLeft(page.PageIndex) / width;
        double right = profile.TextRight(page.PageIndex) / width;
        if (lines.IsEmpty || right <= left) return lines;
        return new RectD(Math.Min(lines.Left, left), lines.Top, Math.Max(lines.Right, right), lines.Bottom);
    }

    private static IEnumerable<Line> SplitColumnLine(Line line, PageContent page)
    {
        var pieces = new List<Line>();
        int start = line.Start;
        RectD? previous = null;
        double gap = Math.Max(0.025, line.Style.SizePoints * 2 / page.Size.Width);
        for (int i = line.Start; i < line.End; i++)
        {
            if (char.IsWhiteSpace(page.Text.Text[i]) || !page.Text.TryGetBox(i, out var box)) continue;
            if (previous is { } last && box.Left - last.Right > gap)
            {
                if (Line.FromRange(page, start, i) is { } piece) pieces.Add(piece);
                start = i;
            }
            previous = box;
        }
        if (Line.FromRange(page, start, line.End) is { } tail) pieces.Add(tail);
        return pieces.Count > 1 && pieces.All(p => p.Text.Length >= 20) ? pieces : [line];
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

        /// <summary>Characters that open a new part of the line after a gap no word space explains.</summary>
        public IReadOnlyList<int> TabStarts { get; init; } = [];

        /// <summary>Where each of those parts starts across the page, normalized.</summary>
        public IReadOnlyList<double> TabStops { get; init; } = [];

        /// <summary>A run of dots leading to a page number at the end of the line, as a contents page sets it.</summary>
        public LineLeader? Leader { get; init; }

        /// <summary>Set in a monospaced face, all but a character or two: a line of code.</summary>
        public bool Monospace { get; init; }

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

        public static Line Create(PageContent page, TextLine visual, string raw, IReadOnlyList<double>? knownStops = null)
        {
            // The dominant style of a line is the style of most of its characters: a line is rarely mixed,
            // and when it is, the majority is what the paragraph should inherit.
            var counts = new Dictionary<TextStyle, int>();
            int bold = 0, counted = 0, fixedPitch = 0;
            for (int i = visual.Start; i < visual.End; i++)
            {
                if (i >= page.Text.Length || char.IsWhiteSpace(page.Text.Text[i])) continue;
                var style = page.StyleAt(i);
                counts[style] = counts.GetValueOrDefault(style) + 1;
                if (style.Bold) bold++;
                if (IsMonospace(style)) fixedPitch++;
                counted++;
            }

            var dominant = counts.Count == 0
                ? TextStyle.Default
                : counts.OrderByDescending(p => p.Value).First().Key;

            // An entry with a leader is one tab: the dots are Word's to draw, and the gaps inside them are not
            // the parts of a line set in columns.
            // Code keeps its own spacing, character for character; nothing in it is a tab or a leader. A line
            // is code only when nearly all of it is monospaced: "Study online at quizlet.com/x" with the
            // address in a fixed-width face is a sentence with a link in it.
            bool code = counted > 0 && fixedPitch >= counted * 0.9;
            var leader = code ? null : FindLeader(page, visual.Start, visual.End, visual.Bounds.Right, dominant.SizePoints);
            var (starts, stops) = leader is null && !code
                ? TabGaps(page, visual.Start, visual.End, dominant.SizePoints, knownStops)
                : ([], []);
            return new Line
            {
                Start = visual.Start,
                End = visual.End,
                Bounds = visual.Bounds,
                Text = raw.Trim(),
                Style = dominant,
                AllBold = counted > 0 && bold >= counted * 0.8,
                TabStarts = starts,
                TabStops = stops,
                Leader = leader,
                Monospace = code,
            };
        }

        /// <summary>
        /// A leader: four or more dots (or middle dots, hyphens, underscores), spaced or not, running from the
        /// text of an entry to a short tail at the end of the line — a page number, a price, a reference. It
        /// has to be at least two ems long, which an ellipsis in a sentence never is.
        /// </summary>
        private static LineLeader? FindLeader(PageContent page, int start, int end, double right, double size)
        {
            string text = page.Text.Text;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;

            for (int i = start; i < end; i++)
            {
                if (LeaderKind(text[i]) is not { } kind) continue;

                int count = 0, last = i, j = i;
                for (; j < end; j++)
                {
                    char c = text[j];
                    if (LeaderKind(c) == kind)
                    {
                        count += c == '…' ? 3 : 1;
                        last = j;
                    }
                    else if (!(char.IsWhiteSpace(c) && j + 1 < end && LeaderKind(text[j + 1]) == kind)) break;
                }

                int tail = last + 1;
                while (tail < end && char.IsWhiteSpace(text[tail])) tail++;
                bool before = false;
                for (int k = start; k < i && !before; k++) before = char.IsLetterOrDigit(text[k]);

                if (count >= 4 && before && tail < end && end - tail <= 15 && IsReference(text[tail..end], kind) &&
                    page.Text.TryGetBox(i, out var first) && page.Text.TryGetBox(last, out var final) &&
                    (final.Right - first.Left) * page.Size.Width >= size * 2)
                {
                    // Whatever the tail holds, it has no leader of its own: the last run of dots is the leader.
                    var further = FindLeader(page, tail, end, right, size);
                    return further ?? new LineLeader(i, tail, right, kind);
                }
                i = Math.Max(i, j - 1);
            }
            return null;
        }

        /// <summary>
        /// What a leader leads to: a page number or a numbered reference ("12", "xiv", "A-3", "12–14",
        /// "$4.50"). A blank in a sentence — "the ____ of the results" — leads to the rest of the sentence,
        /// and a line of underscores or hyphens is a blank to fill in unless a bare number follows it.
        /// </summary>
        private static bool IsReference(string tail, DocxTabLeader kind)
        {
            tail = tail.Trim();
            if (tail.Length == 0) return false;
            if (kind is DocxTabLeader.Underscore or DocxTabLeader.Hyphen) return tail.All(char.IsDigit);
            return tail.Any(char.IsDigit) || RomanNumeral.IsMatch(tail);
        }

        private static DocxTabLeader? LeaderKind(char c) => c switch
        {
            '.' or '…' => DocxTabLeader.Dot,
            '·' or '∙' or '•' => DocxTabLeader.MiddleDot,
            '_' => DocxTabLeader.Underscore,
            '-' => DocxTabLeader.Hyphen,
            _ => null,
        };

        /// <summary>
        /// Gaps inside a line far wider than its word spaces: the line is set in parts that line up with
        /// the lines around it — a term and its definition, a label and its value, the cells of a table
        /// drawn without rules. A gap counts only if it is both wider than two and a half em and three
        /// and a half times the line's typical word space, which justified text never is: it stretches
        /// every space of a line alike.
        /// </summary>
        private static (List<int> Starts, List<double> Stops) TabGaps(
            PageContent page, int start, int end, double size, IReadOnlyList<double>? knownStops)
        {
            var words = new List<(int Start, RectD Box)>();
            RectD previous = RectD.Empty;
            int wordStart = -1;
            for (int i = start; i < end; i++)
            {
                if (char.IsWhiteSpace(page.Text.Text[i]) || !page.Text.TryGetBox(i, out var box) || box.IsEmpty)
                {
                    if (wordStart >= 0) words.Add((wordStart, previous));
                    wordStart = -1;
                    continue;
                }
                if (wordStart < 0)
                {
                    wordStart = i;
                    previous = box;
                }
                else previous = previous.Union(box);
            }
            if (wordStart >= 0) words.Add((wordStart, previous));
            if (words.Count < 2) return ([], []);

            double width = page.Size.Width;
            var gaps = new List<double>(words.Count - 1);
            for (int k = 1; k < words.Count; k++) gaps.Add((words[k].Box.Left - words[k - 1].Box.Right) * width);
            // A word space is about a third of an em. The median gap says the same on a line of prose, but on a
            // line of three words, one of them the gap in question, it is that gap.
            double typical = Math.Min(Median([.. gaps]), size * 0.35);
            double threshold = Math.Max(Math.Max(size * 2.5, typical * 3.5), 12);

            // On its own width, a gap is a tab only beside ordinary word spaces: a justified line of two or
            // three words has nothing but wide gaps, and none of them is a tab.
            bool spaced = gaps.Any(g => g < threshold);

            // A narrower gap still counts when the text after it starts exactly where other lines of the page
            // start a part of theirs: a long term leaves less room before its definition, but the definition
            // lines up with all the others.
            double aligned = Math.Max(Math.Max(size * 0.4, typical * 1.5), 3);
            double tolerance = 2 / width;

            var starts = new List<int>();
            var stops = new List<double>();
            for (int k = 1; k < words.Count; k++)
            {
                bool tab = (spaced && gaps[k - 1] >= threshold) ||
                           (gaps[k - 1] >= aligned && knownStops is not null &&
                            knownStops.Any(x => Math.Abs(x - words[k].Box.Left) <= tolerance));
                if (!tab) continue;
                starts.Add(words[k].Start);
                stops.Add(words[k].Box.Left);
            }
            return (starts, stops);
        }
    }

    /// <summary>
    /// The leader of a line: its characters run from <see cref="Start"/> up to where the tail begins at
    /// <see cref="End"/>, and the tail ends at <see cref="Right"/>, normalized — where a right-aligned stop goes.
    /// </summary>
    private sealed record LineLeader(int Start, int End, double Right, DocxTabLeader Kind);

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

        /// <summary>The PDF's own tags settled what this paragraph is, so no heuristic may overrule it.</summary>
        public bool Tagged { get; set; }

        /// <summary>The shaded panel the paragraph is set in, if any.</summary>
        public Panel? Panel { get; set; }

        /// <summary>Set in a monospaced face throughout: code, whose line breaks and spacing are kept.</summary>
        public bool Code => Lines.Count > 0 && Lines.All(l => l.Monospace);

        public IReadOnlyList<(int Start, int End)> Ranges => [.. Lines.Select(l => (l.Start, l.End))];

        public double Size => Lines.Count == 0 ? 11 : Lines[0].Style.SizePoints;
        public bool Bold => Lines.Count > 0 && Lines.All(l => l.AllBold);
        public string Text => string.Join(" ", Lines.Select(l => l.Text));
    }

    private static List<Para> GroupParagraphs(
        List<Line> lines, RectD column, PageContent page, Dictionary<Line, (Panel? Panel, int Table)>? regions = null)
    {
        var result = new List<Para>();
        if (lines.Count == 0) return result;

        double medianGap = MedianGap(lines);
        var current = new List<Line> { lines[0] };

        for (int i = 1; i < lines.Count; i++)
        {
            var previous = lines[i - 1];
            var line = lines[i];
            if (Region(previous) != Region(line) || StartsNewParagraph(previous, line, current, column, medianGap, page))
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

        (Panel? Panel, int Table) Region(Line line) =>
            regions is not null && regions.TryGetValue(line, out var region) ? region : (null, -1);

        Para Finish(List<Line> lines, RectD column)
        {
            var para = new Para { Lines = [.. lines], Column = column, Bounds = Bounds(lines), Panel = Region(lines[0]).Panel };
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
        // Code is set line by line, and its indentation is its structure: a listing stays one block,
        // whatever its short lines and indented lines would say about prose. A blank line or two inside it
        // is part of it — they come back as empty lines — and only a wider gap ends it.
        bool previousCode = previous.Monospace, lineCode = line.Monospace;
        if (previousCode != lineCode) return true;
        if (lineCode) return gap > height * 2.6 || Math.Abs(previous.Style.SizePoints - line.Style.SizePoints) > 0.5;

        if (OpensAListItem(line.Text)) return true;

        // A line set in parts — a row of a table drawn without rules, a term and its definition — is an
        // entry of its own; run into the line before, its parts would lose where they line up.
        if (line.TabStarts.Count > 0) return true;

        // An entry of a contents page ends at its page number. The next line is the next entry, however
        // close under it and however far across it starts.
        if (previous.Leader is not null) return true;

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
        double firstWord = FirstWordWidth(line, page);
        double nextWord = firstWord + AverageCharWidth(previous) * 0.6;

        // A third of the measure left empty ends the paragraph whatever comes next — but only where the next
        // line has no word breaks to measure by: text set without spaces, as Chinese and Japanese are, reads
        // as one word the width of the line. Anywhere else the word decides; in a narrow table cell a third
        // of the measure is less than one long word, and that word wrapped.
        bool clearlyShort(double room) =>
            room > column.Width * 0.35 && firstWord >= line.Bounds.Width * 0.9 && line.Bounds.Width >= column.Width * 0.9;
        if (previousIsFlushLeft)
        {
            double room = column.Right - previous.Bounds.Right;
            if (room > nextWord || clearlyShort(room)) return true;
        }
        else if (Math.Abs(previous.Bounds.Left - line.Bounds.Left) <= Math.Max(height * 0.3, 0.002) &&
                 Math.Abs(previous.Bounds.Left - current[0].Bounds.Left) <= Math.Max(height * 0.3, 0.002))
        {
            // An indented block — a quotation, a note set in from both margins. Its lines share a left edge,
            // and its measure is its own: room is judged against the widest line of the block, not the column.
            double measure = Math.Max(current.Max(l => l.Bounds.Right), line.Bounds.Right);
            if (measure - previous.Bounds.Right > nextWord) return true;
        }
        else if (HangingTextLeft(current[0], page) is { } hang &&
                 Math.Abs(line.Bounds.Left - hang) <= Math.Max(height * 0.5, AverageCharWidth(previous)))
        {
            // The next line of a list item, hanging under its text. The item is set to the column's measure
            // like any paragraph, so its room is judged against the column's edge — never as centred text,
            // however evenly its indent and its last line's shortfall happen to balance.
            double room = column.Right - previous.Bounds.Right;
            if (room > nextWord || clearlyShort(room)) return true;
        }
        else if (IsCentred(previous.Bounds, column))
        {
            // Centred text can grow both ways, so its room is the whole measure less the line. Word wraps
            // centred text only when the next word does not fit the full measure, so a centred line with room
            // for it was ended on purpose: a title broken by its author, or a displayed formula with the
            // text going on beneath it.
            if (column.Width - previous.Bounds.Width > nextWord) return true;
        }

        // A first line indented from the block it follows starts a new paragraph — except the second line of
        // a list item, which hangs at the text after the marker rather than under the marker itself.
        double blockLeft = current.Min(l => l.Bounds.Left);
        bool centredBlock = IsCentredBlock([.. current, line], column);
        if (line.Bounds.Left > blockLeft + height * 0.7 && !centredBlock)
        {
            // A line under the last part of an entry set in parts — the rest of a definition, hanging under
            // its first line — is the same entry.
            double tolerance = Math.Max(height * 0.5, AverageCharWidth(previous));
            bool underLastPart = current[0].TabStops.Count > 0 &&
                                 Math.Abs(line.Bounds.Left - current[0].TabStops[^1]) <= tolerance;
            if (!underLastPart &&
                (HangingTextLeft(current[0], page) is not { } hanging || Math.Abs(line.Bounds.Left - hanging) > tolerance))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Clear of both edges of the column by about the same amount. Both have to be clear: a first line
    /// indented a little and running to the right margin has its centre near the column's too, and taking
    /// it for centred would cut every indented paragraph off from the rest of it.
    /// </summary>
    /// <summary>
    /// Lines centred as a block: every one of them inset by the same amount from both edges of the column,
    /// and at least one inset far enough to show it. Justified text fails on its indented first line and
    /// its short last one, each inset on one side only; full lines alone prove nothing either way.
    /// </summary>
    private static bool IsCentredBlock(List<Line> lines, RectD column)
    {
        if (column.Width <= 0 || lines.Count == 0) return false;
        // Glyph boxes are a point or two off the advance either side; in a narrow cell that is more than
        // a fiftieth of the measure.
        double tolerance = Math.Max(column.Width * 0.02, 0.004);
        bool symmetric = lines.All(l => Math.Abs((l.Bounds.Left - column.Left) - (column.Right - l.Bounds.Right)) <= tolerance);
        return symmetric && lines.Any(l => l.Bounds.Left - column.Left > column.Width * 0.015);
    }

    private static bool IsCentred(RectD line, RectD column)
    {
        if (column.Width <= 0) return false;
        double left = line.Left - column.Left, right = column.Right - line.Right;
        double margin = column.Width * 0.04;
        return left > margin && right > margin && Math.Abs(left - right) <= column.Width * 0.02;
    }

    /// <summary>
    /// Lines centred in the column: each clear of both edges by the same amount, give or take the width of a
    /// letter. Lines that also share a left edge are text set from the left that happens to balance — a
    /// single indented line ending where its indent began, an item whose lines wrap alike — unless they sit
    /// so far in from both edges that nothing but centring puts them there.
    /// </summary>
    private static bool IsCentredText(List<Line> lines, RectD column, double tolerance)
    {
        // A line that fills the measure fits centring as well as anything else does: a centred note of two
        // lines is one full line and a short one in the middle under it.
        double symmetry = Math.Max(column.Width * 0.02, tolerance);
        var inset = new List<Line>();
        foreach (var line in lines)
        {
            double left = line.Bounds.Left - column.Left, right = column.Right - line.Bounds.Right;
            if (Math.Abs(left - right) > symmetry) return false;
            if (left > tolerance && right > tolerance) inset.Add(line);
        }
        if (inset.Count == 0) return false;

        double least = inset.Min(l => l.Bounds.Left) - column.Left;
        double spread = lines.Max(l => l.Bounds.Left) - lines.Min(l => l.Bounds.Left);
        if (lines.Count > 1 && spread > tolerance) return true;
        return least > column.Width * (lines.Count > 1 ? 0.08 : 0.04);
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

        // A list item and an entry with a leader are set from the left whatever their width: an item whose
        // indent happens to match the room its last word left at the right is not centred.
        if (OpensAListItem(lines[0].Text) || AmbiguousBullet.IsMatch(lines[0].Text) || lines.Any(l => l.Leader is not null))
            return DocxAlignment.Left;

        if (IsCentredText(lines, column, tolerance)) return DocxAlignment.Center;

        if (rightFlush) return DocxAlignment.Right;
        return DocxAlignment.Left;
    }

    // ---- tags ----

    /// <summary>
    /// Settles what the tags are certain about — headings and their level, list items and their depth,
    /// captions — before any heuristic runs, and marks those paragraphs so none of them overrules it.
    /// Appearance is never taken from the tags: a tagged file will mark a run as <c>/P</c> and draw it
    /// bold at 18 pt, so size, weight and colour still come from the glyphs.
    /// </summary>
    private static List<Para> ApplyTags(
        List<Para> paragraphs, TagIndex tags, DocumentProfile profile, ExportOptions options)
    {
        // Two paragraphs the geometry split that belong to one tagged element are one paragraph. The gap
        // still has to be a paragraph's worth: a file that wraps a whole page in a single /P — and they
        // exist — must not have the page run together into one block of text.
        var merged = new List<Para>(paragraphs.Count);
        int previousBlock = -1;

        foreach (var para in paragraphs)
        {
            int block = tags.BlockOf(para.Ranges);
            if (block >= 0 && block == previousBlock && merged.Count > 0 &&
                merged[^1].Column == para.Column &&
                para.Bounds.Top - merged[^1].Bounds.Bottom < profile.GlyphHeight * 2.5)
            {
                var target = merged[^1];
                target.Lines.AddRange(para.Lines);
                target.Bounds = target.Bounds.Union(para.Bounds);
                target.Alignment = DetectAlignment(target);
                continue;
            }

            merged.Add(para);
            previousBlock = block;
        }

        foreach (var para in merged)
        {
            var info = tags.Classify(para.Ranges);
            switch (info.Kind)
            {
                case TagKind.Heading when options.Headings:
                    para.Style = LevelToStyle(info.Level);
                    para.Tagged = true;
                    break;

                case TagKind.Caption:
                    para.Style = DocxParagraphStyle.Caption;
                    para.Tagged = true;
                    break;

                case TagKind.ListItem when options.Lists:
                    string text = para.Lines.Count > 0 ? para.Lines[0].Text : string.Empty;
                    var marker = BulletMarker.Match(text) is { Success: true } bullet ? bullet
                        : AmbiguousBullet.Match(text) is { Success: true } dash ? dash
                        : NumberMarker.Match(text);

                    // The label is drawn as part of the line, so it has to come out of the text: left in,
                    // Word numbers the item and the PDF's own marker sits next to it.
                    para.List = info.Ordered || (marker.Success && NumberMarker.IsMatch(text))
                        ? DocxListKind.Number
                        : DocxListKind.Bullet;
                    para.MarkerLength = marker.Success ? marker.Length : 0;
                    para.ListLevel = info.Level;
                    para.Tagged = true;
                    break;

                case TagKind.Paragraph:
                    para.Tagged = true;
                    break;
            }
        }

        return merged;
    }

    // ---- headings and lists ----

    private static void MarkHeadings(
        List<Para> paragraphs, DocumentProfile profile,
        ILookup<int, (double Y, int Depth, string Title)> headings, int pageIndex)
    {
        foreach (var para in paragraphs)
        {
            if (para.List != DocxListKind.None || para.Tagged) continue;

            // A contents entry names a heading, and a bookmark to that heading may even land on it; it is
            // still an entry, set as the page set it.
            if (para.Code || para.Lines.Any(l => l.Leader is not null)) continue;

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

    private static readonly Regex RomanNumeral = new(@"^[ivxlcdm]{1,7}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MonospaceFamily = new(
        @"mono|courier|consolas|menlo|monaco|lucida console|typewriter|inconsolata|fira ?code|source ?code|cascadia|andale",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A monospaced face: what code, a terminal session or a fixed-width listing is set in.</summary>
    private static bool IsMonospace(TextStyle style) => MonospaceFamily.IsMatch(style.FontFamily);

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

    /// <summary>
    /// Where the text of a list item's first line starts, past its marker — the position its later lines
    /// hang at. Null when the line does not open with a marker.
    /// </summary>
    private static double? HangingTextLeft(Line first, PageContent page)
    {
        var match = BulletMarker.Match(first.Text) is { Success: true } bullet ? bullet
            : AmbiguousBullet.Match(first.Text) is { Success: true } dash ? dash
            : NumberMarker.Match(first.Text);
        if (!match.Success) return null;

        string text = page.Text.Text;
        int i = first.Start;
        while (i < first.End && char.IsWhiteSpace(text[i])) i++;
        i += match.Length;
        for (; i < first.End; i++)
            if (!char.IsWhiteSpace(text[i]) && page.Text.TryGetBox(i, out var box) && !box.IsEmpty) return box.Left;
        return null;
    }

    private static void MarkLists(List<Para> paragraphs)
    {
        var kinds = new DocxListKind[paragraphs.Count];
        var certain = new bool[paragraphs.Count];
        var lengths = new int[paragraphs.Count];

        for (int i = 0; i < paragraphs.Count; i++)
        {
            // "2. Portfolio .......... 7" is an entry of a contents page, not the second item of a list, and
            // "- x" in a listing is code.
            if (paragraphs[i].Tagged || paragraphs[i].Code || paragraphs[i].Lines.Any(l => l.Leader is not null)) continue;
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
        ExportOptions options, double gapPoints, double pitchPoints)
    {
        var runs = BuildRuns(para, page, extras, options);
        double pageWidth = page.Size.Width;

        int indent = 0, firstLine = 0;
        if (para.Code)
        {
            // The block's own indentation is in its spaces; the paragraph starts where its leftmost line does.
            double left = para.Lines.Min(l => CodeLineLeft(l, page));
            indent = PointsToTwips((left - para.Column.Left) * pageWidth);
            if (indent < 40) indent = 0;
        }
        else if (para.List == DocxListKind.None)
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
        else if (ListTextLeft(para, page) is { } textLeft)
        {
            // Where the marker and the text after it sit on the page. Word's own list indent is a fixed half
            // inch with a quarter-inch hang, which a "100." does not fit in and a tight list is lost in.
            double markerLeft = para.Lines[0].Bounds.Left;
            indent = PointsToTwips((textLeft - para.Column.Left) * pageWidth);
            firstLine = -PointsToTwips((textLeft - markerLeft) * pageWidth);
            if (indent <= 0 || firstLine >= 0) indent = firstLine = 0;
        }

        // Text set out in the margin — a page number turned on its side, a note beside the column — would
        // get an indent wider than the text area itself, and Word would then set it one letter to a line.
        // Whatever the indent, the paragraph keeps room for a few words.
        double measure = page.Size.Width - profile.Margins.Left - profile.Margins.Right;
        int maxIndent = PointsToTwips(Math.Max(0, measure - Math.Max(72, measure * 0.25)));
        if (indent > maxIndent)
        {
            indent = maxIndent;
            firstLine = Math.Max(firstLine, -indent);
        }
        if (indent + firstLine > maxIndent) firstLine = maxIndent - indent;

        var (lineSpacing, lineRule) = LineSpacingFor(para, page, pitchPoints, options);

        if (para.Alignment is DocxAlignment.Center or DocxAlignment.Right && para.List == DocxListKind.None)
        {
            // Centring is measured across the whole text area; an indent would move the centre with it.
            indent = 0;
            firstLine = 0;
        }

        // An entry set in parts: a tab stop where each part starts, and the lines after the first hanging
        // at the last of them. A numbered entry keeps its number in the hang, with a stop where its text
        // starts, so the number, the term and the definition each land where the page put them.
        var tabStops = new List<DocxTabStop>();
        if (para.Lines.Count > 0 && para.Lines[0].TabStops.Count > 0 && para.Alignment is not (DocxAlignment.Center or DocxAlignment.Right))
        {
            var first = para.Lines[0];
            int Relative(double x) => PointsToTwips((x - para.Column.Left) * pageWidth);
            if (para.List != DocxListKind.None && ListTextLeft(para, page) is { } textLeft && textLeft < first.TabStops[0])
                tabStops.Add(new DocxTabStop(Relative(textLeft), DocxAlignment.Left));
            foreach (double stop in first.TabStops.Take(first.TabStops.Count - 1))
                tabStops.Add(new DocxTabStop(Relative(stop), DocxAlignment.Left));

            int last = Relative(first.TabStops[^1]);
            int start = Relative(first.Bounds.Left);
            if (para.Lines.Count > 1 || para.List != DocxListKind.None)
            {
                // The last stop is the hanging indent, which Word treats as a stop of its own.
                indent = last;
                firstLine = start - last;
            }
            else
            {
                tabStops.Add(new DocxTabStop(last, DocxAlignment.Left));
            }
        }

        // A contents entry: the page number set against a right-aligned stop where it ended, with the dots
        // drawn as the stop's leader. A point or two past the margin is the same margin measured off glyphs.
        if (para.Lines.FirstOrDefault(l => l.Leader is not null)?.Leader is { } leader)
        {
            int position = PointsToTwips((leader.Right - para.Column.Left) * pageWidth);
            int measureTwips = PointsToTwips(measure);
            if (position > measureTwips && position - measureTwips <= PointsToTwips(6)) position = measureTwips;
            tabStops.Add(new DocxTabStop(position, DocxAlignment.Right) { Leader = leader.Kind });
        }

        return new DocxParagraph(runs)
        {
            Style = para.Style,
            Alignment = options.PreserveLineBreaks && para.Alignment == DocxAlignment.Justify
                ? DocxAlignment.Left : para.Alignment,
            IndentTwips = Math.Max(0, indent),
            FirstLineTwips = firstLine,
            SpaceBeforeTwips = SpacingTwips(gapPoints),
            LineSpacing = lineSpacing,
            LineRule = lineRule,
            // Every gap is written, zero included: a paragraph that inherited the style's spacing would sit
            // six points lower than the page put it, and a page of them overflows onto the next.
            ExplicitSpacing = true,
            TabStops = tabStops,
            List = para.List,
            ListLevel = para.ListLevel,
            ListMarker = para.List == DocxListKind.Number && para.MarkerLength > 0
                ? para.Lines[0].Text[..para.MarkerLength].TrimEnd() : null,
            MarkerStyle = para.List != DocxListKind.None ? MarkerStyleOf(para, page) : null,
        };
    }

    /// <summary>The style the list label was printed in: that of its first visible character.</summary>
    private static TextStyle? MarkerStyleOf(Para para, PageContent page)
    {
        if (para.Lines.Count == 0 || para.MarkerLength <= 0) return null;
        var line = para.Lines[0];
        for (int i = line.Start; i < line.End; i++)
        {
            if (char.IsWhiteSpace(page.Text.Text[i])) continue;
            // No larger than the text: Word sizes the first line to its label, so a bullet drawn a point
            // bigger than the words beside it would open a point of extra space above every item.
            var style = page.StyleAt(i);
            return style with { SizePoints = Math.Min(style.SizePoints, para.Size) };
        }
        return null;
    }

    /// <summary>
    /// Where a list item's text starts, after its marker: the left edge of the first character past the
    /// marker, or of the item's second line, which hangs there. Null when neither can be measured.
    /// </summary>
    private static double? ListTextLeft(Para para, PageContent page)
    {
        if (para.Lines.Count == 0 || para.MarkerLength <= 0) return null;
        var line = para.Lines[0];
        string text = page.Text.Text;

        int i = line.Start;
        while (i < line.End && char.IsWhiteSpace(text[i])) i++;
        i += para.MarkerLength;
        while (i < line.End && char.IsWhiteSpace(text[i])) i++;
        for (; i < line.End; i++)
            if (!char.IsWhiteSpace(text[i]) && page.Text.TryGetBox(i, out var box) && !box.IsEmpty) return box.Left;

        return para.Lines.Count > 1 ? para.Lines.Skip(1).Min(l => l.Bounds.Left) : null;
    }

    /// <summary>
    /// The line spacing that reproduces the page's pitch. For an installed font it is a multiple of the
    /// font's own line height — Word's "Multiple" — which still behaves when someone edits the text. For a
    /// font Word will have to substitute, the multiple would be of a height nobody can know in advance, so
    /// the pitch is written exactly; "at least" where a line mixes sizes and exact would clip the larger.
    /// </summary>
    private static (int Value, DocxLineRule Rule) LineSpacingFor(
        Para para, PageContent page, double pitchPoints, ExportOptions options)
    {
        double size = para.Size;
        if (size <= 0 || pitchPoints <= 0 || para.Lines.Count == 0) return (0, DocxLineRule.AtLeast);

        bool uniform = true;
        foreach (var line in para.Lines)
        {
            for (int i = line.Start; i < line.End && uniform; i++)
                if (!char.IsWhiteSpace(page.Text.Text[i]) && page.StyleAt(i).SizePoints > size * 1.15) uniform = false;
        }

        if (uniform && options.FontMetrics?.LineHeight(para.Lines[0].Style.FontFamily) is { } factor && factor is > 0.5 and < 3)
        {
            int multiple = (int)Math.Round(pitchPoints / (factor * size) * 240);
            return (Math.Clamp(multiple, 168, 960), DocxLineRule.Auto);
        }

        return (PointsToTwips(pitchPoints), uniform ? DocxLineRule.Exact : DocxLineRule.AtLeast);
    }

    private static List<DocxRun> BuildRuns(Para para, PageContent page, PageExtras extras, ExportOptions options)
    {
        var runs = new List<DocxRun>();
        var buffer = new StringBuilder();
        RunKey? key = null;
        string text = page.Text.Text;
        double paraSize = para.Size;

        // A centred or right-aligned block — a title, an address, a verse — is broken where its author broke
        // it, and reflowing it would set it differently for no gain in editing: it is short by nature. Code
        // is broken where it was written, always.
        bool keepBreaks = options.PreserveLineBreaks || para.Code || para.Alignment is DocxAlignment.Center or DocxAlignment.Right;
        var grid = para.Code ? CodeGrid(para, page) : null;

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
                CharacterSpacingTwips = k.Spacing,
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
            if (!keepBreaks && index < para.Lines.Count - 1 && to > from && IsSoftHyphenBreak(text, from, to, para.Lines[index + 1], page))
            {
                to--;                 // drop the hyphen: the word continues on the next line
                joinedByHyphen = true;
            }

            var tracking = grid is null ? Tracking(line, page) : NoTracking;
            bool tabs = line.TabStarts.Count > 0 && para.Alignment is not (DocxAlignment.Center or DocxAlignment.Right);

            // Code: every character goes back to its column, the spaces before it made up from where it
            // stands. The PDF draws indentation and runs of spaces as a jump, and reading it back as one
            // space would flatten every block of code to its left margin.
            double origin = grid is { } g0 ? (g0.PerLine ? CodeLineLeft(line, page) : g0.Origin) : 0;
            int column = 0;

            for (int i = from; i < to; i++)
            {
                char c = text[i];
                if (c == '\n') continue;
                if (tracking.Skip.Contains(i)) continue;

                if (grid is { } g)
                {
                    if (char.IsWhiteSpace(c)) continue;
                    if (page.Text.TryGetBox(i, out var cell) && !cell.IsEmpty)
                    {
                        int at = (int)Math.Round((cell.Left - origin) * page.Size.Width / g.CharWidth);
                        if (at > column)
                        {
                            if (key is null) key = KeyFor(i, c, page, extras, para, line, paraSize, options);
                            buffer.Append(' ', Math.Min(at - column, 200));
                            column = at;
                        }
                    }
                    var codeKey = KeyFor(i, c, page, extras, para, line, paraSize, options);
                    if (key is not { } ck || !ck.Equals(codeKey))
                    {
                        Flush();
                        key = codeKey;
                    }
                    buffer.Append(c);
                    column++;
                    continue;
                }

                if (line.Leader is { } leader && i == leader.Start)
                {
                    // The dots become a single tab, and the stop it goes to draws them.
                    while (buffer.Length > 0 && buffer[^1] == ' ') buffer.Length--;
                    if (buffer.Length == 0 && runs.Count > 0 && runs[^1].Text.EndsWith(' '))
                        runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd(' ') };
                    if (key is null) key = KeyFor(i, c, page, extras, para, line, paraSize, options) with { Spacing = tracking.Twips };
                    buffer.Append('\t');
                    i = Math.Max(i, leader.End - 1);
                    continue;
                }

                if (tabs && line.TabStarts.Contains(i))
                {
                    // The spaces of the gap give way to a tab, in whichever run they ended up.
                    while (buffer.Length > 0 && buffer[^1] == ' ') buffer.Length--;
                    if (buffer.Length == 0 && runs.Count > 0 && runs[^1].Text.EndsWith(' '))
                        runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd(' ') };
                    if (key is null) key = KeyFor(i, text[i], page, extras, para, line, paraSize, options) with { Spacing = tracking.Twips };
                    buffer.Append('\t');
                }

                // A space takes the look of the text it follows. Given a run of its own it would carry
                // whatever size and font the PDF drew it with — often none that matches either neighbour —
                // and split the line into more runs than there are styles on it.
                if (char.IsWhiteSpace(c) && key is not null)
                {
                    buffer.Append(c);
                    continue;
                }

                var next = KeyFor(i, c, page, extras, para, line, paraSize, options) with { Spacing = tracking.Twips };
                if (key is not { } k || !k.Equals(next))
                {
                    Flush();
                    key = next;
                }
                buffer.Append(c);
            }

            if (keepBreaks && index < para.Lines.Count - 1)
            {
                buffer.Append('\n');

                // The blank lines of a listing, one for each line step skipped.
                if (grid is { LineStep: > 0 } g1)
                {
                    int blank = (int)Math.Round((para.Lines[index + 1].Bounds.Top - line.Bounds.Top) / g1.LineStep) - 1;
                    if (blank > 0) buffer.Append('\n', Math.Min(blank, 3));
                }
            }
            else if (index < para.Lines.Count - 1 && !joinedByHyphen && buffer.Length > 0 &&
                !(buffer[^1] == '-' && to - from > 1 && char.IsLetter(text[to - 2]) &&
                  char.IsLetter(para.Lines[index + 1].Text[0])))
            {
                // Lines of one paragraph are joined with a single space, which is what reflowing needs.
                if (buffer[^1] is not ' ') buffer.Append(' ');
            }
        }

        Flush();
        if (runs.Count == 0) runs.Add(new DocxRun(string.Empty, para.Lines.Count > 0 ? para.Lines[0].Style : TextStyle.Default));
        return runs;
    }

    /// <summary>
    /// The character grid of a block of code: how wide one character is, from how far apart the letters
    /// of its words stand, and where column zero is — the leftmost character of the block, or of each line
    /// when the block is centred, where the lines' own positions are the alignment's doing.
    /// </summary>
    private sealed record CodeGridInfo(double CharWidth, double Origin, bool PerLine, double LineStep);

    private static CodeGridInfo CodeGrid(Para para, PageContent page)
    {
        var steps = new List<double>();
        foreach (var line in para.Lines)
        {
            for (int i = line.Start; i + 1 < line.End; i++)
            {
                if (char.IsWhiteSpace(page.Text.Text[i]) || char.IsWhiteSpace(page.Text.Text[i + 1])) continue;
                if (page.Text.TryGetBox(i, out var a) && page.Text.TryGetBox(i + 1, out var b) && !a.IsEmpty && !b.IsEmpty)
                    steps.Add((b.Left - a.Left) * page.Size.Width);
            }
        }
        double width = steps.Count >= 3 ? Median(steps) : para.Size * 0.6;
        if (width < para.Size * 0.3 || width > para.Size) width = para.Size * 0.6;

        bool perLine = para.Alignment is DocxAlignment.Center or DocxAlignment.Right;
        double origin = para.Lines.Min(l => CodeLineLeft(l, page));

        // The listing's line step, from its closest lines: a blank line shows as a step twice as long.
        var lineSteps = new List<double>();
        for (int i = 1; i < para.Lines.Count; i++)
            lineSteps.Add(para.Lines[i].Bounds.Top - para.Lines[i - 1].Bounds.Top);
        lineSteps.RemoveAll(d => d <= 0);
        lineSteps.Sort();
        double step = lineSteps.Count > 0 ? lineSteps[lineSteps.Count / 4] : 0;
        return new CodeGridInfo(width, origin, perLine, step);
    }

    /// <summary>Where a line's first visible character starts: leading spaces are drawn, and are no indent.</summary>
    private static double CodeLineLeft(Line line, PageContent page)
    {
        for (int i = line.Start; i < line.End; i++)
            if (!char.IsWhiteSpace(page.Text.Text[i]) && page.Text.TryGetBox(i, out var box) && !box.IsEmpty) return box.Left;
        return line.Bounds.Left;
    }

    private readonly record struct RunKey(
        TextStyle Style, DocxScript Script, bool Underline, bool Strike, string? Link, uint? Highlight)
    {
        public int Spacing { get; init; }
    }

    private static readonly (HashSet<int> Skip, int Twips) NoTracking = ([], 0);

    /// <summary>
    /// Letter-spaced text — "M O D E L  R E P O R T" set in tracked capitals — comes out of PDFium with a
    /// space it invented between every two letters, because each gap is wider than it expects inside a
    /// word. Those spaces are dropped and the spacing is carried as Word's own character spacing, so the
    /// word is a word again: it reads, searches and spell-checks as one.
    ///
    /// Only generated spaces are touched — ones with no glyph box, that PDFium added itself. A line typed
    /// with real spaces between single letters ("A B C D") keeps every one of them.
    /// </summary>
    private static (HashSet<int> Skip, int Twips) Tracking(Line line, PageContent page)
    {
        string text = page.Text.Text;
        var spaces = new List<int>();
        var gaps = new List<double>();
        int letters = 0;

        for (int i = line.Start; i < line.End; i++)
        {
            char c = text[i];
            if (!char.IsWhiteSpace(c))
            {
                letters++;
                continue;
            }
            if (page.Text.TryGetBox(i, out _)) continue;                     // a real space
            if (i == line.Start || i + 1 >= line.End) continue;
            if (!char.IsLetterOrDigit(text[i - 1]) || !char.IsLetterOrDigit(text[i + 1])) continue;

            // Between two single letters: the one before is preceded by a space or the line start.
            bool singleBefore = i - 2 < line.Start || char.IsWhiteSpace(text[i - 2]);
            bool singleAfter = i + 2 >= line.End || char.IsWhiteSpace(text[i + 2]);
            if (!singleBefore && !singleAfter) continue;
            if (!page.Text.TryGetBox(i - 1, out var left) || !page.Text.TryGetBox(i + 1, out var right)) continue;

            spaces.Add(i);
            gaps.Add((right.Left - left.Right) * page.Size.Width);
        }

        // A handful of letters spread apart is tracking; one generated space in a line is just a gap.
        if (spaces.Count < 3 || spaces.Count * 2 < letters - 1) return NoTracking;

        // Between words the gap is the tracking twice over plus a word space, so it stands clear of the
        // gaps inside words, which are the majority. Those wider spaces are word breaks and stay.
        double typical = Median([.. gaps]);
        double wordBreak = typical + Math.Max(typical * 0.5, line.Style.SizePoints * 0.15);
        var skip = new HashSet<int>();
        for (int k = 0; k < spaces.Count; k++)
            if (gaps[k] <= wordBreak) skip.Add(spaces[k]);

        double spacing = Math.Clamp(typical, 0, line.Style.SizePoints);
        return (skip, PointsToTwips(spacing));
    }

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
    /// Only an explicit soft hyphen can safely be removed. Geometry cannot distinguish "understand-ing"
    /// from a real compound such as "well-known"; deleting an ordinary hyphen changes the source text.
    /// </summary>
    private static bool IsSoftHyphenBreak(string text, int from, int to, Line next, PageContent page)
    {
        if (to - from < 2 || text[to - 1] != '\u00AD') return false;
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
        TableGrid grid, PageContent page, PageExtras extras, DocumentProfile profile, ExportOptions options,
        bool borders = true)
    {
        double snap = TableBuilder.SnapFor(profile.GlyphHeight);
        var rules = borders ? page.Rules : [];
        var (thickness, color) = borders ? MeasureRules(grid, page) : (0, (uint?)null);
        var (measuredPadding, topPadding) = borders ? MeasureCellPadding(grid, page, options) : (0, 0);

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
                while (borders && c + span < grid.ColumnCount &&
                       !TableBuilder.HasVerticalEdge(grid, rules, r, c + span, snap))
                    span++;

                var area = new RectD(grid.Columns[c], grid.Rows[r], grid.Columns[c + span], grid.Rows[r + 1]);
                bool continues = borders && r > 0 && !TableBuilder.HasHorizontalEdge(grid, rules, r, c, snap);
                bool startsMerge = borders && !continues && r + 1 < grid.RowCount &&
                                   !TableBuilder.HasHorizontalEdge(grid, rules, r + 1, c, snap);

                cells.Add(new DocxCell(continues ? [] : CellBlocks(area, page, extras, profile, options, measuredPadding))
                {
                    ColumnSpan = span,
                    VerticalMerge = continues ? 2 : startsMerge ? 1 : 0,
                    Shading = ShadingFor(area, grid.Bounds, page),
                    VerticalAlignment = continues || startsMerge ? DocxVerticalAlignment.Top : VerticalAlignmentOf(area, page),
                });
                c += span;
            }

            // The row is as tall as the page drew it: cell padding and rule spacing are part of its look, and
            // a table sized to its text alone comes out cramped and a good deal shorter than the original.
            // Word adds the rule and the cell's top margin to the height it is given — measured, not read —
            // so both come off the pitch the page shows, or every row grows by them.
            var row = new DocxRow(cells)
            {
                MinHeightTwips = borders
                    ? Math.Max(0, PointsToTwips((grid.Rows[r + 1] - grid.Rows[r]) * page.Size.Height - thickness) - topPadding)
                    : 0,
            };
            // A first row set entirely in bold is a header row, and Word should repeat it across a page break.
            if (r == 0 && cells.Count > 1 && cells.All(IsBoldCell)) row = row with { IsHeader = true };
            rows.Add(row);
        }

        TrimEmptyEdgeRows(rows);
        if (rows.Count > 0 && !rows[0].IsHeader && rows[0].Cells.Count > 1 && rows[0].Cells.All(IsBoldCell))
            rows[0] = rows[0] with { IsHeader = true };

        int padding = measuredPadding;
        return new DocxTable(rows, widths)
        {
            HasBorders = borders,
            BorderColor = color,
            BorderEighths = Math.Clamp((int)Math.Round((thickness > 0 ? thickness : 0.5) * 8), 2, 96),
            CellPaddingTwips = padding,
            CellTopPaddingTwips = topPadding,
            IndentTwips = TableIndent(grid.Columns[0], page, profile, borders ? 0 : padding),
        };
    }

    /// <summary>
    /// Where a cell's text sits in a row taller than it: centred when the room above it and below it is
    /// the same. Only a cell with room to spare says anything — in one its text fills, top and centre are
    /// the same place — so anything short of clear centring stays at Word's default, the top.
    /// </summary>
    private static DocxVerticalAlignment VerticalAlignmentOf(RectD area, PageContent page)
    {
        var lines = LinesInside(page, area);
        if (lines.Count == 0) return DocxVerticalAlignment.Top;
        var text = Bounds(lines);
        double height = page.Size.Height;
        double above = (text.Top - area.Top) * height, below = (area.Bottom - text.Bottom) * height;
        double size = lines[0].Style.SizePoints;
        if (above + below < size * 1.5) return DocxVerticalAlignment.Top;
        return Math.Abs(above - below) <= Math.Max(2, size * 0.3) ? DocxVerticalAlignment.Center : DocxVerticalAlignment.Top;
    }

    /// <summary>The weight and colour of a table's rules: the typical one, since a table draws them all alike.</summary>
    private static (double Thickness, uint? Color) MeasureRules(TableGrid grid, PageContent page)
    {
        var area = grid.Bounds.Inflate(0.003, 0.003);
        var inside = page.Rules.Where(r => area.Contains(r.Bounds.Center)).ToList();
        if (inside.Count == 0) return (0.5, null);

        var widths = inside.Select(r => r.ThicknessPoints).Where(t => t > 0).ToList();
        double thickness = widths.Count > 0 ? Math.Clamp(Median(widths), 0.25, 6) : 0.5;
        uint color = inside.GroupBy(r => r.Color).OrderByDescending(g => g.Count()).First().Key;
        return (thickness, color == 0 ? null : color);
    }

    /// <summary>
    /// A row at the top or bottom of a table with nothing in any cell: the gap between a heading's underline
    /// and the table's first rule, snapped into the grid because it lines up with it. It holds no content,
    /// and kept it draws an empty row the page never had.
    /// </summary>
    private static void TrimEmptyEdgeRows(List<DocxRow> rows)
    {
        static bool Empty(DocxRow row) => row.Cells.All(c => c.Blocks.Count == 0 && c.VerticalMerge == 0);
        static bool Continues(DocxRow row) => row.Cells.Any(c => c.VerticalMerge == 2);

        while (rows.Count > 1 && Empty(rows[0]) && !Continues(rows[1])) rows.RemoveAt(0);
        while (rows.Count > 1 && Empty(rows[^1])) rows.RemoveAt(rows.Count - 1);
    }

    /// <summary>
    /// How far the text sits in from the left rule of its cell, from the cells that hold any: the padding
    /// the PDF was laid out with. Word's own default is used when there is nothing to measure.
    /// </summary>
    private static (int Side, int Top) MeasureCellPadding(TableGrid grid, PageContent page, ExportOptions options)
    {
        var insets = new List<double>();
        var tops = new List<double>();
        for (int r = 0; r < grid.RowCount; r++)
        {
            for (int c = 0; c < grid.ColumnCount; c++)
            {
                var area = new RectD(grid.Columns[c], grid.Rows[r], grid.Columns[c + 1], grid.Rows[r + 1]);
                var lines = LinesInside(page, area);
                if (lines.Count == 0) continue;
                insets.Add((lines.Min(l => l.Bounds.Left) - area.Left) * page.Size.Width);

                // Above the text: its distance from the rule, less how far down its line Word sets a glyph.
                var first = lines.OrderBy(l => l.Bounds.Top).First();
                double size = first.Style.SizePoints;
                double ascent = options.FontMetrics?.Ascent(first.Style.FontFamily) ?? 0.92;
                double offset = ascent * size - 0.75 * first.Bounds.Height * page.Size.Height;
                tops.Add((first.Bounds.Top - area.Top) * page.Size.Height - offset);
            }
        }
        if (insets.Count == 0) return (108, 0);

        // The smallest insets are the left-aligned cells; a centred cell's inset says nothing about padding.
        // Likewise the smallest tops are the top-aligned cells.
        insets.Sort();
        tops.Sort();
        double inset = insets[insets.Count / 4];
        double top = tops[tops.Count / 4];
        return (PointsToTwips(Math.Clamp(inset, 0, 14)), top < 1 ? 0 : PointsToTwips(Math.Min(top, 8)));
    }

    /// <summary>
    /// The table's offset from the left margin. Word puts a table's left edge at the margin plus this indent,
    /// so a table set in from the text, or pushed out into the margin, stays where it was.
    /// </summary>
    private static int TableIndent(double left, PageContent page, DocumentProfile profile, int textInsetTwips)
    {
        double points = left * page.Size.Width - profile.Margins.Left - textInsetTwips / 20.0;
        return Math.Abs(points) < 1 ? 0 : PointsToTwips(points);
    }

    /// <summary>
    /// Rebuilds a table the PDF's own tags describe: the rows, the cells and their spans are read rather
    /// than inferred, which is the one case where a table without a single drawn line is not a guess.
    /// </summary>
    private static DocxTable BuildTaggedTable(
        TaggedTable table, PageContent page, PageExtras extras, DocumentProfile profile, ExportOptions options)
    {
        // Cells are laid out the way a browser lays out a table: each takes the next free slot of its row,
        // and a row-spanning cell keeps the slots under it occupied.
        var occupied = new HashSet<(int Row, int Column)>();
        var placed = new List<List<(TaggedCell Cell, int Column)>>(table.Rows.Count);
        int columnCount = 0;

        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = new List<(TaggedCell, int)>();
            int column = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                while (occupied.Contains((r, column))) column++;
                row.Add((cell, column));
                for (int dr = 0; dr < cell.RowSpan; dr++)
                    for (int dc = 0; dc < cell.ColumnSpan; dc++)
                        occupied.Add((r + dr, column + dc));
                column += cell.ColumnSpan;
                columnCount = Math.Max(columnCount, column);
            }
            placed.Add(row);
        }
        if (columnCount == 0) return new DocxTable([], []);

        var widths = MeasureColumns(placed, columnCount, table.Bounds, page.Size.Width);
        var rows = new List<DocxRow>(placed.Count);

        for (int r = 0; r < placed.Count; r++)
        {
            var cells = new List<DocxCell>(placed[r].Count);
            int expected = 0;
            foreach (var (cell, column) in placed[r])
            {
                // A slot covered by a cell from an earlier row is the continuation of that merge.
                while (expected < column)
                {
                    cells.Add(new DocxCell([]) { VerticalMerge = 2 });
                    expected++;
                }

                cells.Add(new DocxCell(CellBlocks(cell.Ranges, cell.Bounds, page, extras, profile, options))
                {
                    ColumnSpan = cell.ColumnSpan,
                    VerticalMerge = cell.RowSpan > 1 ? 1 : 0,
                    Shading = ShadingFor(cell.Bounds, table.Bounds, page),
                });
                expected = column + cell.ColumnSpan;
            }

            var built = new DocxRow(cells);
            bool header = placed[r].Count > 0 && placed[r].All(c => c.Cell.IsHeader);
            if (header || (r == 0 && cells.Count > 1 && cells.All(IsBoldCell))) built = built with { IsHeader = true };
            rows.Add(built);
        }

        // A tagged table is a table whether or not it was ever drawn, so the borders follow the PDF: it
        // gets them if it drew them, and Word's own grid lines are not invented for one that did not.
        bool drawn = page.Rules.Any(r => table.Bounds.Inflate(0.01, 0.01).Contains(r.Bounds.Center));

        // A tagged table's bounds are its text, so the default padding is put back outside it.
        return new DocxTable(rows, widths) { HasBorders = drawn, IndentTwips = TableIndent(table.Bounds.Left, page, profile, 108) };
    }

    /// <summary>
    /// Column widths from where the cells of each column start, not from how wide their text happens to
    /// be: a column holding the word "Year" is as wide as the space the PDF gave it, and a table measured
    /// from its text alone comes out as a huddle of narrow columns in the middle of the page.
    /// </summary>
    private static List<int> MeasureColumns(
        List<List<(TaggedCell Cell, int Column)>> placed, int columnCount, RectD bounds, double pageWidth)
    {
        var lefts = new double[columnCount];
        var text = new double[columnCount];
        Array.Fill(lefts, double.NaN);

        foreach (var row in placed)
        {
            foreach (var (cell, column) in row)
            {
                if (cell.Bounds.IsEmpty || column >= columnCount) continue;
                lefts[column] = double.IsNaN(lefts[column]) ? cell.Bounds.Left : Math.Min(lefts[column], cell.Bounds.Left);
                if (cell.ColumnSpan == 1) text[column] = Math.Max(text[column], cell.Bounds.Width);
            }
        }

        // A column nothing starts in takes the width of its widest text, or an equal share as a last resort.
        double fallback = bounds.Width / columnCount;
        var widths = new List<int>(columnCount);
        for (int c = 0; c < columnCount; c++)
        {
            double next = c + 1 < columnCount ? NextLeft(lefts, c + 1) : bounds.Right;
            double width = double.IsNaN(lefts[c]) || double.IsNaN(next) || next <= lefts[c]
                ? (text[c] > 0 ? text[c] : fallback)
                : next - lefts[c];
            widths.Add(Math.Max(1, PointsToTwips(width * pageWidth)));
        }
        return widths;

        static double NextLeft(double[] lefts, int from)
        {
            for (int i = from; i < lefts.Length; i++)
                if (!double.IsNaN(lefts[i])) return lefts[i];
            return double.NaN;
        }
    }

    private static bool IsBoldCell(DocxCell cell)
    {
        var runs = cell.Blocks.OfType<DocxParagraph>().SelectMany(p => p.Runs)
            .Where(r => r.Text.Trim().Length > 0).ToList();
        return runs.Count > 0 && runs.All(r => r.Style.Bold);
    }

    /// <summary>
    /// The fill behind a cell, when the PDF painted one. A shaded header row is one wide rectangle behind
    /// three cells, so a fill wider than the cell still counts — but one covering the whole table is the
    /// table's own background, and painting every cell with it would lose the distinction it was making.
    /// </summary>
    private static uint? ShadingFor(RectD cell, RectD table, PageContent page)
    {
        if (page.Fills.Count == 0 || cell.IsEmpty) return null;

        double area = cell.Width * cell.Height;
        double tableArea = table.IsEmpty ? 0 : table.Width * table.Height;
        uint? best = null;
        double bestArea = double.MaxValue;

        foreach (var fill in page.Fills)
        {
            var overlap = fill.Bounds.Intersect(cell);
            if (overlap.IsEmpty) continue;
            if (overlap.Width * overlap.Height < area * 0.7) continue;      // it has to cover the cell

            double fillArea = fill.Bounds.Width * fill.Bounds.Height;
            if (tableArea > 0 && fillArea > tableArea * 0.75) continue;     // and not the whole table
            if (fillArea >= bestArea) continue;

            bestArea = fillArea;
            best = fill.Color;
        }

        // White is what Word draws anyway, and writing it in would fight a user's own table style.
        return best is 0xFFFFFF ? null : best;
    }

    /// <summary>
    /// The contents of a ruled cell. Its lines are judged against the cell's text area — inside the padding
    /// on both sides — since that is where they wrapped: measured against the rules, every line that ended
    /// at the padding would look short, and each would become a paragraph of its own.
    /// </summary>
    private static List<DocxBlock> CellBlocks(
        RectD area, PageContent page, PageExtras extras, DocumentProfile profile, ExportOptions options, int paddingTwips)
    {
        double inset = Math.Min(paddingTwips / 20.0 / page.Size.Width, area.Width * 0.25);
        var text = new RectD(area.Left + inset, area.Top, area.Right - inset, area.Bottom);
        return Compose(LinesInside(page, area), text, page, extras, profile, options);
    }

    /// <summary>
    /// The contents of a cell a tag describes. Its characters are known exactly, so they are cut by range
    /// rather than by position — which is what makes a tagged table exact where a ruled one is measured.
    /// </summary>
    private static List<DocxBlock> CellBlocks(
        IReadOnlyList<(int Start, int End)> ranges, RectD area, PageContent page, PageExtras extras,
        DocumentProfile profile, ExportOptions options)
    {
        var lines = new List<Line>();
        foreach (var visual in page.Text.Lines)
        {
            foreach (var (start, end) in ranges)
            {
                int from = Math.Max(visual.Start, start), to = Math.Min(visual.End, end);
                if (to <= from) continue;
                if (Line.FromRange(page, from, to) is { } line) lines.Add(line);
            }
        }

        lines.Sort((a, b) =>
        {
            int byTop = a.Bounds.Top.CompareTo(b.Bounds.Top);
            return byTop != 0 ? byTop : a.Bounds.Left.CompareTo(b.Bounds.Left);
        });

        var bounds = area.IsEmpty ? Bounds(lines) : area;
        return Compose(lines, bounds, page, extras, profile, options);
    }

    private static List<DocxBlock> Compose(
        List<Line> lines, RectD area, PageContent page, PageExtras extras,
        DocumentProfile profile, ExportOptions options)
    {
        if (lines.Count == 0) return [];

        var blocks = new List<DocxBlock>();
        Para? previous = null;
        double previousPitch = 0;
        foreach (var para in GroupParagraphs(lines, area, page))
        {
            para.Column = area;

            // A single line in a cell has no neighbour to measure its leading against, and none after it to
            // take up any excess: whatever it is given, the row grows by. Its font's own single spacing is
            // the height the page shows.
            double pitch = para.Lines.Count > 1 ? PitchOf(para, page, profile)
                : (options.FontMetrics?.LineHeight(para.Lines[0].Style.FontFamily) ?? 1.2) * para.Size;
            double gap = previous is null ? 0
                : Math.Clamp((para.Bounds.Top - previous.Lines[^1].Bounds.Top) * page.Size.Height - previousPitch +
                             TextOffset(previous, previousPitch, page, options) - TextOffset(para, pitch, page, options), 0, 36);
            var paragraph = BuildParagraph(para, page, extras, profile, options, gap, pitch);
            // Indents are measured against the page's text area, which means nothing inside a cell.
            blocks.Add(paragraph with { IndentTwips = 0, FirstLineTwips = 0 });
            previous = para;
            previousPitch = pitch;
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

    // ---- panels ----

    /// <summary>
    /// A shaded panel the page sets text in — a note, a warning, a callout — with the lines drawn along any
    /// of its edges. <see cref="Bounds"/> takes in those lines, so it is the panel as the reader sees it.
    /// </summary>
    private sealed record Panel(RectD Bounds, uint Fill, PanelEdge? Top, PanelEdge? Left, PanelEdge? Bottom, PanelEdge? Right);

    private readonly record struct PanelEdge(uint Color, double WidthPoints);

    /// <summary>
    /// The panels on a page. A PDF draws one as a filled rectangle, often patched together from a piece per
    /// line of text, with its borders as thin rules or thin rectangles of their own; Word, which is where
    /// most of them come from, stores it as shading and borders on the paragraphs inside. Only a panel
    /// that holds whole lines of text is one: a table's cells are the table's, a page background is behind
    /// everything, and a band across part of a line is a highlight.
    /// </summary>
    private static List<Panel> FindPanels(PageContent page, DocumentProfile profile, List<Line> lines, List<RectD> tables)
    {
        var panels = new List<Panel>();
        if (page.Fills.Count == 0 || lines.Count == 0) return panels;
        double width = page.Size.Width, height = page.Size.Height;
        double measure = (width - profile.Margins.Left - profile.Margins.Right) / width;
        const double Thin = 6;   // points: a filled rectangle this narrow is a border, not a panel

        // Pieces of one colour that touch are one panel.
        var regions = new List<(RectD Bounds, uint Color)>();
        foreach (var fill in page.Fills)
        {
            if (fill.Color == 0xFFFFFF) continue;
            if (fill.Bounds.Width * width <= Thin || fill.Bounds.Height * height <= Thin) continue;
            var bounds = fill.Bounds;
            for (int i = regions.Count - 1; i >= 0; i--)
            {
                if (regions[i].Color != fill.Color || !regions[i].Bounds.Inflate(1.5 / width, 1.5 / height).Intersects(bounds)) continue;
                bounds = bounds.Union(regions[i].Bounds);
                regions.RemoveAt(i);
            }
            regions.Add((bounds, fill.Color));
        }

        foreach (var (region, color) in regions)
        {
            if (region.Width < measure * 0.3 || region.Height < profile.GlyphHeight) continue;
            // A fill behind nearly the whole page is the page's colour, not a panel; a listing that runs on
            // for a page or more still leaves the margins clear.
            if (region.Width * region.Height > 0.8) continue;
            if (tables.Any(t => t.Inflate(2 / width, 2 / height).Intersects(region))) continue;

            var hold = region.Inflate(1.5 / width, 1.5 / height);
            bool any = false, crossed = false;
            foreach (var line in lines)
            {
                if (Encloses(hold, line.Bounds)) any = true;
                else if (line.Bounds.Intersect(region) is { IsEmpty: false } part &&
                         part.Width * part.Height > line.Bounds.Width * line.Bounds.Height * 0.2) crossed = true;
            }
            if (!any || crossed) continue;

            var top = Edge(region, color, horizontal: true, atStart: true);
            var left = Edge(region, color, horizontal: false, atStart: true);
            var bottom = Edge(region, color, horizontal: true, atStart: false);
            var right = Edge(region, color, horizontal: false, atStart: false);
            var outer = new RectD(
                region.Left - (left?.WidthPoints ?? 0) / width, region.Top - (top?.WidthPoints ?? 0) / height,
                region.Right + (right?.WidthPoints ?? 0) / width, region.Bottom + (bottom?.WidthPoints ?? 0) / height);
            panels.Add(new Panel(outer, color, top, left, bottom, right));
        }
        return panels;

        // The line along one side: rules and thin rectangles lying on that edge, in a colour of their own,
        // covering at least half of it. A line in the panel's own colour only extends the shading.
        PanelEdge? Edge(RectD region, uint fill, bool horizontal, bool atStart)
        {
            double reach = 1.5;
            double edge = horizontal ? (atStart ? region.Top : region.Bottom) : (atStart ? region.Left : region.Right);
            double scale = horizontal ? height : width;
            var covered = new Dictionary<uint, (double Length, double Thickness)>();

            var segments = page.Rules.Select(r => (r.Bounds, r.Color))
                .Concat(page.Fills.Select(f => (f.Bounds, f.Color)));
            foreach (var (bounds, color) in segments)
            {
                if (color == fill) continue;
                double thickness = (horizontal ? bounds.Height : bounds.Width) * scale;
                if (thickness > Thin || thickness <= 0) continue;
                double from = horizontal ? bounds.Top : bounds.Left, to = horizontal ? bounds.Bottom : bounds.Right;
                if (from * scale > edge * scale + reach || to * scale < edge * scale - reach) continue;

                double overlap = horizontal
                    ? Math.Min(bounds.Right, region.Right) - Math.Max(bounds.Left, region.Left)
                    : Math.Min(bounds.Bottom, region.Bottom) - Math.Max(bounds.Top, region.Top);
                if (overlap <= 0) continue;
                var known = covered.GetValueOrDefault(color);
                covered[color] = (known.Length + overlap, Math.Max(known.Thickness, thickness));
            }

            double span = horizontal ? region.Width : region.Height;
            var best = covered.Where(c => c.Value.Length >= span * 0.5).OrderByDescending(c => c.Value.Length).FirstOrDefault();
            return best.Value.Length > 0 ? new PanelEdge(best.Key, Math.Clamp(best.Value.Thickness, 0.25, 6)) : null;
        }
    }

    /// <summary>The panel a line of text sits in, if any.</summary>
    private static Panel? PanelOf(Line line, List<Panel> panels, PageContent page)
    {
        foreach (var panel in panels)
            if (Encloses(panel.Bounds.Inflate(1.5 / page.Size.Width, 1.5 / page.Size.Height), line.Bounds)) return panel;
        return null;
    }

    private static bool Encloses(RectD outer, RectD inner) =>
        inner.Left >= outer.Left && inner.Right <= outer.Right && inner.Top >= outer.Top && inner.Bottom <= outer.Bottom;

    /// <summary>
    /// Puts each run of paragraphs set in the same panel into a box: shading and a border on every side,
    /// the same on each paragraph so Word draws them as one. The borders' distance from the text is the
    /// padding the panel showed, and the room they take comes out of the spacing around the panel, which
    /// was measured to the text and so already includes it.
    /// </summary>
    private static void AttachPanels(
        List<DocxBlock> blocks, List<(Para Para, int Block)> placed, PageContent page, DocumentProfile profile,
        ExportOptions options)
    {
        double width = page.Size.Width, height = page.Size.Height;
        for (int start = 0; start < placed.Count;)
        {
            var panel = placed[start].Para.Panel;
            int end = start + 1;
            while (end < placed.Count && placed[end].Para.Panel == panel && placed[end].Block == placed[end - 1].Block + 1) end++;
            if (panel is null || placed.Skip(start).Take(end - start).Any(p => blocks[p.Block] is not DocxParagraph))
            {
                start = end;
                continue;
            }

            var first = placed[start].Para;
            var last = placed[end - 1].Para;
            double topWidth = panel.Top?.WidthPoints ?? 0.5, bottomWidth = panel.Bottom?.WidthPoints ?? 0.5;
            double leftWidth = panel.Left?.WidthPoints ?? 0.5, rightWidth = panel.Right?.WidthPoints ?? 0.5;

            // Word's line box starts above the glyphs by the paragraph's text offset, and ends a pitch lower.
            double firstPitch = PitchOf(first, page, profile), lastPitch = PitchOf(last, page, profile);
            double textTop = first.Lines[0].Bounds.Top * height - TextOffset(first, firstPitch, page, options);
            double textBottom = last.Lines[^1].Bounds.Top * height - TextOffset(last, lastPitch, page, options) + lastPitch;
            double topSpace = Math.Clamp(textTop - panel.Bounds.Top * height - topWidth, 0, 31);
            double bottomSpace = Math.Clamp(panel.Bounds.Bottom * height - textBottom - bottomWidth, 0, 31);

            double textRight = placed.Skip(start).Take(end - start).Max(p => p.Para.Bounds.Right) * width;
            for (int i = start; i < end; i++)
            {
                var (para, index) = placed[i];
                var paragraph = (DocxParagraph)blocks[index];

                // The left border is drawn its distance outside the paragraph's leftmost text, which with a
                // hanging first line is the first line's start.
                double textLeft = para.Column.Left * width + (paragraph.IndentTwips + Math.Min(0, paragraph.FirstLineTwips)) / 20.0;
                double leftSpace = Math.Clamp(textLeft - panel.Bounds.Left * width - leftWidth, 0, 31);

                // As much room on the right as on the left, unless the text would then no longer fit the
                // lines it was set in.
                double rightSpace = Math.Clamp(Math.Min(leftSpace, panel.Bounds.Right * width - rightWidth - textRight - 1), 0, 31);
                double rightIndent = para.Column.Right * width - panel.Bounds.Right * width + rightSpace + rightWidth;

                var box = new DocxBox(panel.Fill,
                    Border(panel.Top, panel.Fill, topWidth, topSpace),
                    Border(panel.Left, panel.Fill, leftWidth, Math.Round(leftSpace)),
                    Border(panel.Bottom, panel.Fill, bottomWidth, bottomSpace),
                    Border(panel.Right, panel.Fill, rightWidth, Math.Round(rightSpace)));
                blocks[index] = paragraph with { Box = box, RightIndentTwips = PointsToTwips(rightIndent) };
            }

            // The borders' room was measured into the gaps before and after the panel.
            int firstBlock = placed[start].Block, lastBlock = placed[end - 1].Block;
            var opening = (DocxParagraph)blocks[firstBlock];
            blocks[firstBlock] = WithSpaceBefore(opening, Math.Max(0, opening.SpaceBeforeTwips - PointsToTwips(Math.Round(topSpace) + topWidth)));
            int after = PointsToTwips(Math.Round(bottomSpace) + bottomWidth);
            var closing = (DocxParagraph)blocks[lastBlock];
            if (closing.SpaceAfterTwips > 0)
                blocks[lastBlock] = closing with { SpaceAfterTwips = Math.Max(0, closing.SpaceAfterTwips - after) };
            else if (lastBlock + 1 < blocks.Count && blocks[lastBlock + 1] is DocxParagraph next)
                blocks[lastBlock + 1] = WithSpaceBefore(next, Math.Max(0, next.SpaceBeforeTwips - after));
            else if (lastBlock + 1 < blocks.Count && blocks[lastBlock + 1] is DocxPicture picture)
                blocks[lastBlock + 1] = picture with { SpaceBeforeTwips = Math.Max(0, picture.SpaceBeforeTwips - after) };

            start = end;
        }

        // A side the page drew no line on gets one in the panel's own colour, which keeps the padding shaded.
        static DocxBorder Border(PanelEdge? edge, uint fill, double widthPoints, double space) =>
            new(edge?.Color ?? fill, Math.Clamp((int)Math.Round(widthPoints * 8), 2, 48), Math.Round(space));
    }

    // ---- images ----

    /// <summary>
    /// Decides how each picture sits against the text, the way the page shows it:
    /// <list type="bullet">
    /// <item>Text runs over it — a watermark, a tinted panel — so it goes behind the text, placed on the page.</item>
    /// <item>Text runs beside it: a small one is an icon or a mark and floats in front at its exact spot; a
    /// larger one is a figure the text wraps around.</item>
    /// <item>Nothing beside it: it is a figure in the flow, a paragraph of its own between the text above
    /// and below.</item>
    /// </list>
    /// Laid out inline regardless, a watermark pushes the page's text down by its own height and a column of
    /// answer-choice circles becomes a column of pictures between the answers.
    /// </summary>
    private static void PlacePictures(
        PageContent page, DocumentProfile profile, TagIndex tags, ExportOptions options,
        List<FlowItem> flow, List<Floating> floating, List<RectD> redrawn)
    {
        double pageWidth = page.Size.Width, pageHeight = page.Size.Height;
        var textLines = flow.OfType<ParaFlow>().SelectMany(p => p.Para.Lines.Select(l => (Line: l, p.Para))).ToList();
        double iconLimit = Math.Max(profile.PitchFor(profile.BodySize) * 2.5, 20);

        foreach (var image in page.Images)
        {
            if (image.IsDrawing && !options.Drawings) continue;

            var area = image.Bounds;
            double widthPoints = area.Width * pageWidth;
            double heightPoints = area.Height * pageHeight;
            if (widthPoints < 4 || heightPoints < 4) continue;

            // Artwork drawn over a panel's edge or a table's cells — the corners of a border, the fill of a
            // header row — is the panel or the table, which Word now draws itself.
            if (image.IsDrawing && redrawn.Any(r => r.Inflate(4 / pageWidth, 4 / pageHeight).Intersect(area) is { IsEmpty: false } part &&
                                                    part.Width * part.Height >= area.Width * area.Height * 0.6)) continue;

            // A tagged PDF names its figures, and that description is the only thing in the document a
            // screen reader can use once the picture has been carried across.
            var picture = new DocxPicture(image, widthPoints, heightPoints) { AltText = tags.AltTextFor(image.MarkedContentId) };

            // A line mostly inside the picture is text printed over it.
            var over = textLines.Where(t => Covers(area, t.Line.Bounds)).ToList();
            // Beside a line means sharing some of its height: an icon is often set a little above or below
            // the text it marks, so a quarter of the smaller of the two is enough.
            var beside = textLines.Where(t => !Covers(area, t.Line.Bounds) &&
                                              OverlapsVertically(t.Line.Bounds, area, 0.25) &&
                                              !OverlapsHorizontally(t.Line.Bounds, area)).ToList();

            // Rasterized artwork keeps its labels as text, and those labels are inside it by nature. It is a
            // figure in the flow, not a background, unless it is the size of the page. A picture standing in
            // the band a running head occupies is the head's decoration — a rule, an ornament under the page
            // number — and is placed where it was, behind the text, taking no room in the flow.
            bool inBand = area.Bottom <= ContentComposerBands.Header || area.Top >= ContentComposerBands.Footer;
            bool background = inBand || (over.Count > 0 && (!image.IsDrawing || area.Width * area.Height > 0.5));

            if (background)
            {
                var anchor = NearestPara(textLines, area.Center.Y);
                floating.Add(new Floating(picture with
                {
                    Wrap = DocxWrap.BehindText,
                    OffsetXPoints = area.Left * pageWidth,
                    OffsetYPoints = area.Top * pageHeight,
                    VerticalFromPage = true,
                }, area, anchor));
            }
            else if (beside.Count > 0)
            {
                var anchor = beside.OrderBy(t => t.Line.Bounds.Top).First().Para;
                floating.Add(new Floating(picture with
                {
                    Wrap = heightPoints <= iconLimit ? DocxWrap.InFrontOfText : DocxWrap.Square,
                    OffsetXPoints = area.Left * pageWidth,
                    OffsetYPoints = (area.Top - anchor.Bounds.Top) * pageHeight,
                }, area, anchor));
            }
            else
            {
                InsertByPosition(flow, new PictureFlow(InlinePicture(picture, area, page, profile), area));
            }
        }

        static bool Covers(RectD picture, RectD line)
        {
            var overlap = picture.Intersect(line);
            return !overlap.IsEmpty && overlap.Width * overlap.Height > line.Width * line.Height * 0.5;
        }

        static Para? NearestPara(List<(Line Line, Para Para)> lines, double y) =>
            lines.Count == 0 ? null : lines.OrderBy(t => Math.Abs(t.Line.Bounds.Center.Y - y)).First().Para;
    }

    /// <summary>A picture that is a paragraph of its own: sized to fit the text area, aligned as the page has it.</summary>
    private static DocxPicture InlinePicture(DocxPicture picture, RectD area, PageContent page, DocumentProfile profile)
    {
        double widthPoints = picture.WidthPoints, heightPoints = picture.HeightPoints;

        // A picture has to fit the text area in both directions, not just across. One a single point taller
        // than the page holds pushes itself onto a page of its own and the text that belongs beside it onto
        // the next — which is how a document becomes twice as many pages as it started with.
        double maxWidth = page.Size.Width - profile.Margins.Left - profile.Margins.Right;
        double maxHeight = page.Size.Height - profile.Margins.Top - profile.Margins.Bottom - PictureHeadroom;
        double scale = Math.Min(
            maxWidth > 0 ? maxWidth / widthPoints : 1,
            maxHeight > 0 ? maxHeight / heightPoints : 1);
        if (scale < 1)
        {
            widthPoints *= scale;
            heightPoints *= scale;
        }

        double left = area.Left * page.Size.Width - profile.Margins.Left;
        double right = page.Size.Width - profile.Margins.Right - area.Right * page.Size.Width;
        var alignment = Math.Abs(left - right) <= Math.Max(12, maxWidth * 0.04) ? DocxAlignment.Center
            : right < 6 && left > right ? DocxAlignment.Right
            : DocxAlignment.Left;

        return picture with
        {
            WidthPoints = widthPoints,
            HeightPoints = heightPoints,
            Alignment = alignment,
            IndentTwips = alignment == DocxAlignment.Left && left > 2 && scale >= 1
                ? PointsToTwips(Math.Min(left, maxWidth - widthPoints)) : 0,
        };
    }

    /// <summary>
    /// A rule standing on its own — the line under a title, a separator between sections — becomes a border
    /// on the paragraph it belongs to: the one just above it, or failing that the one just below. A table's
    /// rules are its own, a running head's belong to the head, and an underline is not a separator.
    /// </summary>
    private static void AttachRules(
        List<DocxBlock> blocks, List<(Para Para, int Block)> placed, PageContent page, DocumentProfile profile,
        List<RectD> tables, List<Panel> panels)
    {
        if (page.Rules.Count == 0 || placed.Count == 0) return;
        double width = page.Size.Width, height = page.Size.Height;
        const double Reach = 24;   // points: further than this, the rule is not this paragraph's
        var taken = new List<RectD>();

        // The darkest stroke first: a PDF often lays a pale line and a darker one on the same spot, and the
        // one the reader sees is the darker.
        foreach (var rule in page.Rules.OrderBy(r => Luminance(r.Color)))
        {
            if (!rule.IsHorizontal) continue;
            var bounds = rule.Bounds;

            // The same line drawn twice, or drawn in segments, is one rule.
            if (taken.Any(t => Math.Abs(t.Center.Y - bounds.Center.Y) * height < 1.5 && OverlapsHorizontally(t, bounds))) continue;
            taken.Add(bounds);
            if (bounds.Width * width < 36) continue;
            if (bounds.Bottom <= ContentComposerBands.Header || bounds.Top >= ContentComposerBands.Footer) continue;
            if (tables.Any(t => t.Inflate(0.004, 0.004).Contains(bounds.Center))) continue;
            if (panels.Any(p => p.Bounds.Inflate(2 / width, 2 / height).Contains(bounds.Center))) continue;

            // An underline sits against the foot of a line of text no wider than it.
            bool underline = placed.Any(p => p.Para.Lines.Any(l =>
                bounds.Top >= l.Bounds.Bottom - 2 / height && bounds.Top <= l.Bounds.Bottom + 3 / height &&
                bounds.Left >= l.Bounds.Left - 4 / width && bounds.Right <= l.Bounds.Right + 4 / width));
            if (underline) continue;

            var border = new DocxBorder(rule.Color,
                Math.Clamp((int)Math.Round((rule.ThicknessPoints > 0 ? rule.ThicknessPoints : 0.75) * 8), 2, 96), 0);

            var above = placed.Where(p => p.Para.Bounds.Bottom <= bounds.Top + 1 / height &&
                                          OverlapsHorizontally(p.Para.Bounds, bounds))
                              .OrderByDescending(p => p.Para.Bounds.Bottom).FirstOrDefault();
            if (above.Para is not null && (bounds.Top - above.Para.Bounds.Bottom) * height <= Reach &&
                blocks[above.Block] is DocxParagraph over)
            {
                // One rule to a gap: a second line in the same gap — the foot of one row and the head of the
                // next drawn a point apart — would need room the gap does not have.
                if (over.BorderBelow is not null) continue;

                // Word keeps the distance in whole points, and a rule between paragraphs gets half of it on each
                // side, so it is an even number of them.
                double width2 = border.Eighths / 8.0;
                double space = Math.Min(Math.Max(0, (bounds.Top - above.Para.Bounds.Bottom) * height),
                                        Math.Max(0, Room(blocks, above.Block + 1) - width2));
                space = 2 * Math.Floor(space / 2);
                blocks[above.Block] = over with { BorderBelow = border with { SpacePoints = space } };
                TakeSpace(blocks, above.Block + 1, space + width2, before: true);
                continue;
            }

            var below = placed.Where(p => p.Para.Bounds.Top >= bounds.Bottom - 1 / height &&
                                          OverlapsHorizontally(p.Para.Bounds, bounds))
                              .OrderBy(p => p.Para.Bounds.Top).FirstOrDefault();
            if (below.Para is not null && (below.Para.Bounds.Top - bounds.Bottom) * height <= Reach &&
                blocks[below.Block] is DocxParagraph under && under.BorderAbove is null)
            {
                // Nor a rule over this paragraph when the one before it already has one under it.
                if (below.Block > 0 && blocks[below.Block - 1] is DocxParagraph { BorderBelow: not null }) continue;

                double width2 = border.Eighths / 8.0;
                double space = Math.Floor(Math.Min(Math.Max(0, (below.Para.Bounds.Top - bounds.Bottom) * height),
                                                   Math.Max(0, Room(blocks, below.Block) - width2)));
                blocks[below.Block] = under with { BorderAbove = border with { SpacePoints = space } };
                TakeSpace(blocks, below.Block, space + width2, before: true);
            }
        }

        // The spacing a border can take its room from: the gap before the block at index, whichever side
        // of it the spacing was written on.
        static double Room(List<DocxBlock> blocks, int index)
        {
            double room = 0;
            if (index > 0 && index - 1 < blocks.Count && blocks[index - 1] is DocxParagraph previous) room += previous.SpaceAfterTwips / 20.0;
            if (index < blocks.Count && blocks[index] is DocxParagraph next) room += next.SpaceBeforeTwips / 20.0;
            else if (index < blocks.Count && blocks[index] is DocxPicture picture) room += picture.SpaceBeforeTwips / 20.0;
            else if (index >= blocks.Count) room = 12;
            return room;
        }

        static double Luminance(uint color) =>
            0.299 * ((color >> 16) & 0xFF) + 0.587 * ((color >> 8) & 0xFF) + 0.114 * (color & 0xFF);

        // A border takes room of its own, and the gap it now fills was measured into the spacing already.
        static void TakeSpace(List<DocxBlock> blocks, int index, double points, bool before)
        {
            if (index <= 0 || index > blocks.Count) return;
            int twips = PointsToTwips(points);
            if (blocks[index - 1] is DocxParagraph previous && previous.SpaceAfterTwips > 0)
            {
                blocks[index - 1] = previous with { SpaceAfterTwips = Math.Max(0, previous.SpaceAfterTwips - twips) };
                return;
            }
            if (index < blocks.Count && blocks[index] is DocxParagraph next)
                blocks[index] = WithSpaceBefore(next, Math.Max(0, next.SpaceBeforeTwips - twips));
            else if (index < blocks.Count && blocks[index] is DocxPicture picture)
                blocks[index] = picture with { SpaceBeforeTwips = Math.Max(0, picture.SpaceBeforeTwips - twips) };
        }
    }

    /// <summary>Hangs each floating picture off its paragraph, or off the page when the page has no text.</summary>
    private static void AttachFloating(
        List<DocxBlock> blocks, List<(Para Para, int Block)> placed, List<Floating> floating, PageContent page,
        DocumentProfile profile, ExportOptions options)
    {
        foreach (var (picture, area, anchor) in floating)
        {
            int index = anchor is null ? -1 : placed.FindIndex(p => ReferenceEquals(p.Para, anchor));
            if (index >= 0 && blocks[placed[index].Block] is DocxParagraph paragraph)
            {
                // Word measures from the top of the paragraph's space before, and its glyphs start a little
                // way down their line; the offset was taken from the glyphs, so both are added back.
                var placedPicture = picture.VerticalFromPage ? picture : picture with
                {
                    OffsetYPoints = picture.OffsetYPoints + paragraph.SpaceBeforeTwips / 20.0 +
                                    TextOffset(anchor!, PitchOf(anchor!, page, profile), page, options),
                };
                blocks[placed[index].Block] = paragraph with { Floating = [.. paragraph.Floating, placedPicture] };
                continue;
            }

            // No paragraph to hang it from: it is placed from the top of the page, in a paragraph that
            // takes no room, so it stays exactly where it was.
            blocks.Insert(0, picture with
            {
                Wrap = picture.Wrap == DocxWrap.Inline ? DocxWrap.InFrontOfText : picture.Wrap,
                OffsetXPoints = area.Left * page.Size.Width,
                OffsetYPoints = area.Top * page.Size.Height,
                VerticalFromPage = true,
            });
            for (int i = 0; i < placed.Count; i++) placed[i] = (placed[i].Para, placed[i].Block + 1);
        }
    }

    // ---- comments ----

    /// <summary>
    /// Anchors the page's notes to the paragraphs they sit against. A PDF sticky note is a comment in
    /// everything but name, and its icon is placed beside the text rather than in it, so the anchor is the
    /// nearest paragraph rather than the one underneath. A note left in the body would interrupt the text
    /// at the point it was written, and a note dropped is content lost without a word.
    /// </summary>
    private static void AttachComments(
        List<DocxBlock> blocks, List<(Para Para, int Block)> placed, PageExtras extras, CommentSink comments)
    {
        if (placed.Count == 0) return;

        foreach (var annotation in extras.Annotations)
        {
            if (annotation.Kind is not (AnnotationKind.Note or AnnotationKind.Highlight or
                AnnotationKind.Underline or AnnotationKind.StrikeOut or AnnotationKind.Squiggly)) continue;

            string text = annotation.Contents.Trim();
            if (text.Length == 0) continue;

            var point = annotation.Bounds.IsEmpty
                ? (annotation.Quads.Count > 0 ? annotation.Quads[0].Center : default)
                : annotation.Bounds.Center;

            int target = -1;
            double best = double.MaxValue;
            foreach (var (para, block) in placed)
            {
                if (blocks[block] is not DocxParagraph) continue;
                double distance = para.Bounds.DistanceTo(point);
                if (distance >= best) continue;
                best = distance;
                target = block;
            }
            if (target < 0) continue;

            int id = comments.Add(annotation.Author, text, null);
            var paragraph = (DocxParagraph)blocks[target];
            blocks[target] = paragraph with { CommentIds = [.. paragraph.CommentIds, id] };
        }
    }

    // ---- sections ----

    /// <summary>
    /// A page that ends with this much of its text area still empty was ended on purpose — a title page,
    /// the end of a chapter — and the next page starts a new one in Word too. A page that ends lower ran
    /// out of room, and Word, which never sets the text quite as the PDF did, is left to paginate it.
    /// </summary>
    private const double DeliberateBreakSpace = 0.22;

    private static List<DocxSection> BuildSections(
        List<ComposedPage> composed, DocumentProfile profile, ExportOptions options)
    {
        var sections = new List<DocxSection>();
        if (composed.Count == 0)
            return [new DocxSection(new PageSize(612, 792), DocxMargins.Default, [DocxParagraph.Empty])];

        int typicalGap = TypicalParagraphGap(composed);
        var blocks = new List<DocxBlock>();
        var size = composed[0].Page.Size;
        var columns = DocxColumns.Single;
        bool continuous = false;
        ComposedPage? previous = null;

        void Close()
        {
            sections.Add(MakeSection(size, blocks, profile, options, sections.Count == 0, columns, continuous));
            blocks = [];
        }

        foreach (var current in composed)
        {
            var page = current.Page;
            var pageBlocks = current.Blocks;

            // The layout the page opens with. A page without columns opens with a single measure, which ends
            // a run of column pages before it.
            var opening = DocxColumns.Single;
            if (pageBlocks.Count > 0 && pageBlocks[0] is ZoneBreak zone)
            {
                opening = zone.Columns;
                pageBlocks.RemoveAt(0);
            }

            bool sizeChanged = Math.Abs(page.Size.Width - size.Width) > 1 || Math.Abs(page.Size.Height - size.Height) > 1;
            if (sizeChanged && blocks.Count > 0)
            {
                // A section break starts a new page by itself.
                Close();
                size = page.Size;
                continuous = false;
                columns = opening;
            }

            // A sentence that visibly runs on to the next page settles it: the author did not end the page.
            // So does a table that carries on at the top of the next one with the same columns.
            bool adjacent = options.JoinAcrossPages && options.PageBreaks != PageBreakMode.EveryPage &&
                previous is not null && page.PageIndex == previous.Page.PageIndex + 1 &&
                blocks.Count > 0 && pageBlocks.Count > 0;
            bool tableRunsOn = adjacent && blocks[^1] is DocxTable tailTable && pageBlocks[0] is DocxTable headTable &&
                ContinuesTable(tailTable, headTable);
            bool runsOn = tableRunsOn || (adjacent &&
                blocks[^1] is DocxParagraph lastTail && pageBlocks[0] is DocxParagraph firstHead &&
                ContinuesAcrossPages(lastTail, firstHead));
            bool pageBreak = previous is not null && !sizeChanged && !runsOn && StartsNewPage(previous, current, profile, options);

            // Another layout needs a section of its own. Where the page also starts a new page, the section
            // break is the page break; where the text runs on, the section continues on the same page.
            bool breakBySection = false;
            if (!opening.SameAs(columns))
            {
                if (blocks.Count > 0 && !sizeChanged)
                {
                    Close();
                    continuous = !pageBreak;
                    breakBySection = pageBreak;
                }
                columns = opening;
            }
            // Where the page's text starts well down it — a chapter opening, a title set low — the space
            // above it is part of the layout, and at the top of a page Word keeps it.
            double drop = double.IsNaN(current.ContentTop) ? 0
                : (current.ContentTop * page.Size.Height) - profile.Margins.Top;
            int before = drop > profile.PitchFor(profile.BodySize) * 2 ? SpacingTwips(Math.Min(drop, page.Size.Height / 2)) : 0;

            if (pageBreak || (sizeChanged && previous is not null) || (previous is null && before > 0))
            {
                if (pageBlocks.Count == 0 || pageBlocks[0] is not DocxParagraph)
                    pageBlocks.Insert(0, Spacer);

                var first = (DocxParagraph)pageBlocks[0];
                pageBlocks[0] = WithSpaceBefore(first, Math.Max(before, first.SpaceBeforeTwips)) with
                {
                    PageBreakBefore = pageBreak && !breakBySection,
                };
            }
            else if (tableRunsOn)
            {
                blocks[^1] = JoinTables((DocxTable)blocks[^1], (DocxTable)pageBlocks[0]);
                pageBlocks.RemoveAt(0);
            }
            else if (previous is not null && pageBlocks.Count > 0)
            {
                if (options.JoinAcrossPages && page.PageIndex == previous.Page.PageIndex + 1 &&
                    blocks.Count > 0 && blocks[^1] is DocxParagraph tail && pageBlocks[0] is DocxParagraph head &&
                    ContinuesAcrossPages(tail, head))
                {
                    blocks[^1] = JoinParagraphs(tail, head);
                    pageBlocks.RemoveAt(0);
                }
                else if (pageBlocks[0] is DocxParagraph { Style: DocxParagraphStyle.Body, SpaceBeforeTwips: 0 } starting &&
                         blocks.Count > 0 && blocks[^1] is DocxParagraph closing &&
                         !(starting.List != DocxListKind.None && closing.List != DocxListKind.None))
                {
                    // The page boundary hid the gap between two paragraphs; in one flow it has to be put back.
                    pageBlocks[0] = WithSpaceBefore(starting, typicalGap);
                }
            }

            // Where the layout changes on the page itself — a headline over two columns, the columns, the
            // text under them — each part becomes a section of its own, continuing on the same page.
            foreach (var block in pageBlocks)
            {
                if (block is ZoneBreak change)
                {
                    if (!change.Columns.SameAs(columns))
                    {
                        if (blocks.Count > 0)
                        {
                            Close();
                            continuous = true;
                        }
                        columns = change.Columns;
                    }
                    continue;
                }
                blocks.Add(block);
            }
            if (pageBlocks.Any(b => b is not ZoneBreak) || previous is null) previous = current;
        }

        Close();
        return sections;
    }

    /// <summary>A paragraph that takes no room, for where Word needs one and the layout has none.</summary>
    private static DocxParagraph Spacer { get; } = new([])
    {
        ExplicitSpacing = true,
        LineSpacing = 20,
        LineRule = DocxLineRule.Exact,
    };

    private static bool StartsNewPage(ComposedPage previous, ComposedPage current, DocumentProfile profile, ExportOptions options)
    {
        if (options.PageBreaks == PageBreakMode.EveryPage) return true;
        if (options.PageBreaks == PageBreakMode.None) return false;

        // Pages left out of the export: whatever was on them, the text does not run on across the gap.
        if (current.Page.PageIndex != previous.Page.PageIndex + 1) return true;
        if (double.IsNaN(previous.ContentBottom)) return true;

        double height = previous.Page.Size.Height;
        double areaTop = profile.Margins.Top / height;
        double areaBottom = 1 - profile.Margins.Bottom / height;
        double area = areaBottom - areaTop;
        if (area <= 0) return false;

        double free = (areaBottom - previous.ContentBottom) / area;
        if (free >= DeliberateBreakSpace) return true;

        // A page whose text starts well down it is a chapter opening or a part title, set low on a page of
        // its own: whatever came before, it starts a new one.
        if (!double.IsNaN(current.ContentTop) &&
            (current.ContentTop - profile.Margins.Top / current.Page.Size.Height) / area >= 0.15)
            return true;

        // A page set lower than the text area's top and ending short of its foot was composed as a page — a
        // cover, a title page — however little room either end leaves on its own.
        double drop = double.IsNaN(previous.ContentTop) ? 0 : Math.Max(0, (previous.ContentTop - areaTop) / area);
        if (free >= 0.08 && free + drop >= 0.25) return true;

        // A new chapter starts a new page, and a page ending with room to spare before one was ended for it:
        // two lines' worth is already more than chance leaves at the foot of a full page.
        return free >= 0.03 &&
               current.Blocks.FirstOrDefault(b => b is not ZoneBreak) is DocxParagraph { Style: DocxParagraphStyle.Heading1 };
    }

    /// <summary>
    /// The gap the document puts between paragraphs, when it puts one at all: most paragraphs carry it, so
    /// it is a style rather than an accident. Books that indent instead of spacing have none.
    /// </summary>
    private static int TypicalParagraphGap(List<ComposedPage> composed)
    {
        var gaps = new List<double>();
        int body = 0;
        foreach (var page in composed)
        {
            for (int i = 1; i < page.Blocks.Count; i++)
            {
                if (page.Blocks[i] is not DocxParagraph { Style: DocxParagraphStyle.Body, List: DocxListKind.None } paragraph ||
                    page.Blocks[i - 1] is not DocxParagraph { Style: DocxParagraphStyle.Body }) continue;
                body++;
                if (paragraph.SpaceBeforeTwips > 0) gaps.Add(paragraph.SpaceBeforeTwips);
            }
        }
        return gaps.Count * 2 > body ? (int)Median(gaps) : 0;
    }

    private static DocxSection MakeSection(
        PageSize size, List<DocxBlock> blocks, DocumentProfile profile, ExportOptions options, bool first,
        DocxColumns columns, bool continuous)
    {
        if (blocks.Count == 0) blocks.Add(DocxParagraph.Empty);
        return new DocxSection(size, profile.Margins, blocks)
        {
            Columns = columns,
            Continuous = continuous && !first,
            Header = options.HeadersFooters ? profile.Header : null,
            Footer = options.HeadersFooters ? profile.Footer : null,
            DifferentFirstPage = first && options.HeadersFooters && profile.FirstPageUnheaded,
            FirstHeader = first && options.HeadersFooters ? profile.FirstHeader : null,
            FirstFooter = first && options.HeadersFooters ? profile.FirstFooter : null,
            HeaderDistancePoints = profile.HeaderDistance,
            FooterDistancePoints = profile.FooterDistance,
        };
    }

    /// <summary>
    /// A paragraph broken by a page break: the foot of the page ran to the measure and stopped mid-sentence,
    /// and the head of the next page picks it up in lower case.
    /// </summary>
    private static bool ContinuesAcrossPages(DocxParagraph tail, DocxParagraph head)
    {
        // A listing broken by the page, in the same panel on both sides of the break.
        if (IsListing(tail) && IsListing(head) && tail.Box?.Fill == head.Box?.Fill) return true;

        if (tail.Style != DocxParagraphStyle.Body || head.Style != DocxParagraphStyle.Body) return false;
        if (tail.List != DocxListKind.None || head.List != DocxListKind.None) return false;

        // Pictures hung from the head are placed from its top, which joining would move up a page.
        if (head.Floating.Count > 0) return false;

        string a = tail.Text.TrimEnd(), b = head.Text.TrimStart();
        if (a.Length == 0 || b.Length == 0) return false;
        if (a[^1] is '.' or '!' or '?' or ':' or ';' or '"' or '”') return false;
        return char.IsLower(b[0]) || b[0] is ',' or ';';
    }

    /// <summary>
    /// A table broken by a page break: the next page opens with a table of the same columns, set at the
    /// same place. Two tables that merely look alike are further apart than that — a caption, a heading or
    /// a paragraph stands between them.
    /// </summary>
    private static bool ContinuesTable(DocxTable tail, DocxTable head)
    {
        if (tail.HasBorders != head.HasBorders || tail.ColumnWidthsTwips.Count != head.ColumnWidthsTwips.Count) return false;
        if (tail.Rows.Count == 0 || head.Rows.Count == 0) return false;
        if (Math.Abs(tail.IndentTwips - head.IndentTwips) > 160) return false;
        for (int i = 0; i < tail.ColumnWidthsTwips.Count; i++)
        {
            int a = tail.ColumnWidthsTwips[i], b = head.ColumnWidthsTwips[i];
            if (Math.Abs(a - b) > Math.Max(160, Math.Max(a, b) * 0.04)) return false;
        }
        return true;
    }

    /// <summary>
    /// One table from its two halves. The header row the PDF printed again at the top of the page is
    /// dropped and the first one marked to repeat instead, which is how Word prints a header on every page.
    /// </summary>
    private static DocxTable JoinTables(DocxTable tail, DocxTable head)
    {
        var rows = new List<DocxRow>(tail.Rows);
        var added = head.Rows.ToList();
        if (added.Count > 0 && RowText(added[0]) == RowText(rows[0]) && RowText(rows[0]).Length > 0)
        {
            added.RemoveAt(0);
            rows[0] = rows[0] with { IsHeader = true };
        }
        rows.AddRange(added);
        return tail with { Rows = rows };

        static string RowText(DocxRow row) => string.Join("|", row.Cells.Select(c =>
            string.Concat(c.Blocks.OfType<DocxParagraph>().Select(p => p.Text)).Trim()));
    }

    /// <summary>A paragraph of code: every run of it in a monospaced face.</summary>
    private static bool IsListing(DocxParagraph paragraph) =>
        paragraph.Runs.Count > 0 && paragraph.Runs.Any(r => r.Text.Trim().Length > 0) &&
        paragraph.Runs.All(r => r.Text.Trim().Length == 0 || IsMonospace(r.Style));

    private static DocxParagraph JoinParagraphs(DocxParagraph tail, DocxParagraph head)
    {
        if (IsListing(tail) && IsListing(head))
        {
            // Line for line: the head's first line is the next line of the listing. Its indentation was
            // measured from its own page's leftmost line, which the tail's may not share; the spaces it
            // carries are relative to that, and the difference goes back in front of each of its lines.
            int shift = (int)Math.Round((head.IndentTwips - tail.IndentTwips) / 20.0 / CharWidthOf(tail));
            var lines = new List<DocxRun>(tail.Runs);
            string pad = shift > 0 ? new string(' ', Math.Min(shift, 80)) : string.Empty;
            bool lineStart = true;
            foreach (var run in head.Runs)
            {
                string text = run.Text;
                if (pad.Length > 0)
                {
                    var built = new StringBuilder();
                    foreach (char c in text)
                    {
                        if (lineStart && c != '\n') built.Append(pad);
                        built.Append(c);
                        lineStart = c == '\n';
                    }
                    text = built.ToString();
                }
                lines.Add(run with { Text = text });
            }
            if (lines.Count > tail.Runs.Count) lines[tail.Runs.Count] = lines[tail.Runs.Count] with { Text = "\n" + lines[tail.Runs.Count].Text };
            return tail with { Runs = lines };
        }

        var runs = new List<DocxRun>(tail.Runs);
        if (runs.Count > 0 && runs[^1].Text.Length > 0 && !char.IsWhiteSpace(runs[^1].Text[^1]))
            runs[^1] = runs[^1] with { Text = runs[^1].Text + " " };
        runs.AddRange(head.Runs);
        return tail with { Runs = runs };
    }

    // ---- shared helpers ----

    private static int PointsToTwips(double points) => (int)Math.Round(points * 20, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The width of one character of a listing, in points: six tenths of an em for most monospaced faces,
    /// a little less for Consolas and Inconsolata.
    /// </summary>
    private static double CharWidthOf(DocxParagraph listing)
    {
        var style = listing.Runs.FirstOrDefault(r => r.Text.Trim().Length > 0)?.Style ?? TextStyle.Default;
        string family = style.FontFamily.ToLowerInvariant();
        double em = family.Contains("consolas") ? 0.55 : family.Contains("inconsolata") ? 0.5 : 0.6;
        return Math.Max(1, style.SizePoints * em);
    }

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
    /// <summary>
    /// The usual gap between one line and the next line under it. Only lines stacked one above the other
    /// count: the cells of a table row sit side by side, and the overlaps and hairline gaps between them
    /// would drag the median to nothing — and with it the threshold that tells a paragraph break from the
    /// leading inside one.
    /// </summary>
    private static double MedianGap(List<Line> lines)
    {
        if (lines.Count < 2) return 0;
        var gaps = new List<double>(lines.Count - 1);
        for (int i = 1; i < lines.Count; i++)
        {
            double gap = lines[i].Bounds.Top - lines[i - 1].Bounds.Bottom;
            if (gap > 0 && OverlapsHorizontally(lines[i].Bounds, lines[i - 1].Bounds)) gaps.Add(gap);
        }
        if (gaps.Count == 0) return 0;
        gaps.Sort();
        return gaps[(gaps.Count - 1) / 2];
    }

    /// <summary>
    /// Wholly outside the page as it is shown: a printer's slug below the trim — file name, date, plate
    /// number — that the PDF keeps but nobody sees. It is not part of the document's text, and measured
    /// with it, the text area would run from edge to edge.
    /// </summary>
    internal static bool IsOffPage(RectD bounds) =>
        bounds.Bottom <= 0 || bounds.Top >= 1 || bounds.Right <= 0 || bounds.Left >= 1;

    internal static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }
}
