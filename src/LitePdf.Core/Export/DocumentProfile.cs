using LitePdf.Core.Text;
using System.Text.RegularExpressions;

namespace LitePdf.Core.Export;

/// <summary>
/// What is true of the document as a whole rather than of any one page: the body text size, the text area,
/// which heading sizes exist, and which lines are running heads.
///
/// These have to be measured globally. Body size taken per page would make every page's largest line a
/// heading, and a running head cannot be recognized from a single page at all. Running heads are settled
/// first, because a page number sitting in the margin would otherwise drag the measured margins out to it.
/// </summary>
internal sealed class DocumentProfile
{
    private readonly HashSet<string> _runningHeads;
    private readonly double[] _headingSizes;
    private readonly Dictionary<double, double> _pitchBySize;
    private readonly double[] _textLeft;
    private readonly double[] _textRight;

    private DocumentProfile(
        double bodySize, string bodyFont, double glyphHeight, double linePitchPoints, DocxMargins margins,
        double[] headingSizes, RunningHeads running, Dictionary<double, double> pitchBySize,
        ILookup<int, (double Y, int Depth, string Title)> outline, double[] textLeft, double[] textRight)
    {
        _textLeft = textLeft;
        _textRight = textRight;
        BodySize = bodySize;
        BodyFont = bodyFont;
        GlyphHeight = glyphHeight;
        LinePitchPoints = linePitchPoints;
        Margins = margins;
        _headingSizes = headingSizes;
        _runningHeads = running.Keys;
        _pitchBySize = pitchBySize;
        Header = running.Header;
        Footer = running.Footer;
        FirstPageUnheaded = running.FirstPageUnheaded;
        FirstHeader = running.FirstHeader;
        FirstFooter = running.FirstFooter;
        HeaderDistance = running.HeaderDistance;
        FooterDistance = running.FooterDistance;
        PageNumberStart = running.PageNumberStart;
        Outline = outline;
    }

    public double BodySize { get; }

    public string BodyFont { get; }

    /// <summary>Median line height, normalized to the page: the unit the layout thresholds are expressed in.</summary>
    public double GlyphHeight { get; }

    /// <summary>Median baseline-to-baseline distance in points.</summary>
    public double LinePitchPoints { get; }

    public DocxMargins Margins { get; }

    /// <summary>
    /// Where the text area starts on a page, in points from its left edge: the margin, except in a book set
    /// with mirrored margins, where left and right pages start their text at different places. Indents are
    /// measured from here, so a page's text starts at the margin in Word whichever side it was printed on.
    /// </summary>
    public double TextLeft(int pageIndex) => _textLeft[pageIndex & 1];

    /// <summary>
    /// Where the text on a page ends, in points from its left edge: the right edge the fuller pages reach.
    /// Not Word's right margin, which is set roomier on purpose; this is the measure the lines were set to,
    /// and a line is short only against that.
    /// </summary>
    public double TextRight(int pageIndex) => _textRight[pageIndex & 1];

    public DocxHeaderFooter? Header { get; }

    public DocxHeaderFooter? Footer { get; }

    /// <summary>
    /// The running head or foot is missing from the first page, as it is from a title page. Whichever of
    /// the two the first page does carry is in <see cref="FirstHeader"/> and <see cref="FirstFooter"/>.
    /// </summary>
    public bool FirstPageUnheaded { get; }

    public DocxHeaderFooter? FirstHeader { get; }

    public DocxHeaderFooter? FirstFooter { get; }

    /// <summary>How far the running head sits from the top edge, and the foot from the bottom, in points.</summary>
    public double HeaderDistance { get; }

    public double FooterDistance { get; }

    /// <summary>
    /// The line pitch the document uses for text of <paramref name="size"/>, in points. A paragraph of one
    /// line has no pitch of its own to measure; this is what the multi-line paragraphs of that size show.
    /// </summary>
    public double PitchFor(double size)
    {
        if (size <= 0) size = BodySize;
        if (_pitchBySize.TryGetValue(SizeKey(size), out double pitch)) return pitch;

        // A size nothing else is set in scales with the body text's leading, which is the house style.
        double bodyPitch = _pitchBySize.TryGetValue(SizeKey(BodySize), out double body) ? body : BodySize * 1.2;
        return bodyPitch * size / BodySize;
    }

    private static double SizeKey(double size) => Math.Round(size * 2, MidpointRounding.AwayFromZero) / 2;

    /// <summary>The number printed on the first page, when the document does not start at 1.</summary>
    public int PageNumberStart { get; }

    public ILookup<int, (double Y, int Depth, string Title)> Outline { get; }

    /// <summary>Heading level 0..3 for a font size, by rank among the sizes larger than the body.</summary>
    public int HeadingLevel(double size)
    {
        for (int i = 0; i < _headingSizes.Length; i++)
            if (size >= _headingSizes[i] - 0.25) return i;
        return Math.Max(0, _headingSizes.Length - 1);
    }

    public bool IsRunningHead(RectD bounds, string text) =>
        _runningHeads.Count > 0 && InBand(bounds) && _runningHeads.Contains(RunningKey(bounds, text));

    private static string RunningKey(RectD bounds, string text) =>
        (bounds.Bottom <= ContentComposerBands.Header ? "header:" : "footer:") + NormalizeRunningHead(text);

    private static bool InBand(RectD bounds) =>
        bounds.Bottom <= ContentComposerBands.Header || bounds.Top >= ContentComposerBands.Footer;

    public static DocumentProfile Build(
        IReadOnlyList<PageContent> pages, IReadOnlyList<OutlineItem>? outline, ExportOptions options)
    {
        var running = options.HeadersFooters ? FindRunningHeads(pages) : RunningHeads.None;
        var runningHeads = running.Keys;
        var pitchSamples = new Dictionary<double, List<double>>();

        var sizeWeights = new Dictionary<double, int>();
        var fontWeights = new Dictionary<string, int>();
        var lineHeights = new List<double>();
        var pitches = new List<double>();
        var lefts = new List<double>();
        var leftsByParity = new[] { new List<double>(), new List<double>() };
        var rights = new List<double>();
        var rightsByParity = new[] { new List<double>(), new List<double>() };
        var tops = new List<double>();
        var bottoms = new List<double>();

        foreach (var page in pages)
        {
            foreach (var span in page.Spans)
            {
                int weight = Math.Max(0, span.End - span.Start);
                if (weight == 0) continue;
                double size = Math.Round(span.Style.SizePoints * 2, MidpointRounding.AwayFromZero) / 2;
                sizeWeights[size] = sizeWeights.GetValueOrDefault(size) + weight;
                fontWeights[span.Style.FontFamily] = fontWeights.GetValueOrDefault(span.Style.FontFamily) + weight;
            }

            var body = new List<RectD>();
            var sizes = new List<double>();
            foreach (var line in page.Text.Lines)
            {
                if (line.Bounds.IsEmpty || line.End <= line.Start || ContentComposer.IsOffPage(line.Bounds)) continue;
                string raw = page.Text.Text[line.Start..line.End];
                if (raw.Trim().Length == 0) continue;
                if (runningHeads.Count > 0 && InBand(line.Bounds) &&
                    runningHeads.Contains(RunningKey(line.Bounds, raw.Trim()))) continue;

                lineHeights.Add(line.Bounds.Height);
                body.Add(line.Bounds);
                int first = line.Start;
                while (first < line.End - 1 && char.IsWhiteSpace(page.Text.Text[first])) first++;
                sizes.Add(page.StyleAt(first).SizePoints);
            }

            for (int i = 1; i < body.Count; i++)
            {
                double pitch = body[i].Top - body[i - 1].Top;
                if (pitch > 0 && pitch < 0.2) pitches.Add(pitch * page.Size.Height);

                // Two lines of one size, one under the other and no further apart than leading allows:
                // consecutive lines of a paragraph, whose distance is the pitch that size is set on.
                double points = pitch * page.Size.Height;
                if (Math.Abs(sizes[i] - sizes[i - 1]) < 0.3 && points >= sizes[i] * 0.9 && points <= sizes[i] * 1.8)
                {
                    double key = SizeKey(sizes[i]);
                    if (!pitchSamples.TryGetValue(key, out var list)) pitchSamples[key] = list = [];
                    list.Add(points);
                }
            }

            if (body.Count > 0)
            {
                lefts.Add(body.Min(b => b.Left));
                leftsByParity[page.PageIndex & 1].Add(body.Min(b => b.Left) * page.Size.Width);
                rightsByParity[page.PageIndex & 1].Add(body.Max(b => b.Right) * page.Size.Width);
                rights.Add(body.Max(b => b.Right));
                tops.Add(body.Min(b => b.Top));
                bottoms.Add(body.Max(b => b.Bottom));
            }
        }

        double bodySize = sizeWeights.Count == 0
            ? 11
            : sizeWeights.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key;
        if (bodySize is <= 0 or > 96) bodySize = 11;

        string bodyFont = fontWeights.Count == 0
            ? "Calibri"
            : fontWeights.OrderByDescending(p => p.Value).First().Key;

        double glyph = lineHeights.Count == 0 ? 0.015 : ContentComposer.Median(lineHeights);
        if (glyph is <= 0 or > 0.2) glyph = 0.015;

        double pitchPoints = pitches.Count == 0 ? bodySize * 1.2 : ContentComposer.Median(pitches);

        var reference = pages.Count > 0 ? pages[0].Size : new PageSize(612, 792);
        var margins = MeasureMargins(lefts, rights, tops, bottoms, reference);

        var headingSizes = sizeWeights.Keys
            .Where(s => s >= bodySize * 1.12 && s <= bodySize * 6)
            .OrderByDescending(s => s)
            .Take(4)
            .ToArray();

        var flattened = Flatten(outline).ToLookup(item => item.Page, item => (item.Y, item.Depth, item.Title));

        // Three samples make a pitch; fewer are as likely to be a heading over its first line. The lower
        // quartile rather than the median: lines of one size one under the other are not always one
        // paragraph — table rows and list items are spaced apart — and the tightest regular spacing is the
        // leading itself.
        var pitchBySize = pitchSamples.Where(p => p.Value.Count >= 3)
            .ToDictionary(p => p.Key, p => Percentile(p.Value, 0.25));

        // The margin is where Word's first line box starts; the page shows where its glyphs start, some way
        // down inside it. Taken as it stands, every page of the document would sit that much lower.
        double bodyPitch = pitchBySize.TryGetValue(SizeKey(bodySize), out double measured) ? measured : bodySize * 1.2;
        double glyphPoints = glyph * reference.Height;
        double baseline = options.FontMetrics?.LineHeight(bodyFont) is { } factor && options.FontMetrics.Ascent(bodyFont) is { } ascent
            ? Math.Min(1, bodyPitch / (factor * bodySize)) * ascent * bodySize
            : 0.8 * bodyPitch;
        double offset = Math.Clamp(baseline - 0.75 * glyphPoints, 0, bodySize);
        margins = margins with { Top = Math.Max(18, margins.Top - offset) };

        // The fullest page fills the text area to the point, and Word never sets text quite as the PDF did:
        // a point or two of drift and its last line would go over onto a page of its own. A quarter of a
        // line of slack absorbs that and can never let an extra line in.
        margins = margins with { Bottom = Math.Max(18, margins.Bottom - Math.Min(3, bodyPitch * 0.25)) };

        // Tab stops are measured from the margin, which is only now known.
        running = running with
        {
            Header = WithTabs(running.Header, running.HeaderTabs, reference, margins),
            Footer = WithTabs(running.Footer, running.FooterTabs, reference, margins),
            FirstHeader = WithTabs(running.FirstHeader, running.HeaderTabs, reference, margins),
            FirstFooter = WithTabs(running.FirstFooter, running.FooterTabs, reference, margins),
        };

        // Mirrored margins show as two left edges, one for each side of the spread. Anything less than a
        // few points apart is the same margin measured twice.
        double[] textLeft = [margins.Left, margins.Left];
        double right = rights.Count > 0 ? Percentile(rights, 0.85) * reference.Width : reference.Width - margins.Right;
        double[] textRight = [right, right];
        if (leftsByParity.All(l => l.Count >= 2))
        {
            double even = ContentComposer.Median(leftsByParity[0]), odd = ContentComposer.Median(leftsByParity[1]);
            if (Math.Abs(even - odd) > 4)
            {
                textLeft = [even, odd];
                textRight = [Percentile(rightsByParity[0], 0.85), Percentile(rightsByParity[1], 0.85)];
            }
        }

        return new DocumentProfile(bodySize, bodyFont, glyph, pitchPoints, margins, headingSizes,
            running, pitchBySize, flattened, textLeft, textRight);
    }

    private static DocxHeaderFooter? WithTabs(
        DocxHeaderFooter? content, List<(DocxAlignment Alignment, double X)> tabs, PageSize size, DocxMargins margins)
    {
        if (content is null || tabs.Count == 0) return content;
        double width = size.Width - margins.Left - margins.Right;
        var stops = tabs.Select(t => new DocxTabStop(
            DocxWriter.Twips(Math.Clamp(t.X * size.Width - margins.Left, 0, width)), t.Alignment)).ToList();
        return content with { TabStops = stops };
    }

    private static DocxMargins MeasureMargins(
        List<double> lefts, List<double> rights, List<double> tops, List<double> bottoms, PageSize size)
    {
        if (lefts.Count == 0) return DocxMargins.Default;

        // Left and top are read straight off the page: every line starts at the left margin, so the median
        // is exactly right and resists the odd line that hangs into it.
        // A low percentile for the top: a page whose text starts lower — a title set down, a chapter
        // opening — moves its own first line, not the margin, and in a short document it would be half the
        // sample. The left edge is where nearly every line starts, and the median of it is exact.
        double left = ContentComposer.Median(lefts) * size.Width;
        double top = Percentile(tops, 0.2) * size.Height;

        // Right and bottom are not: a page only reaches them when its content happens to be long enough, so
        // the median would report the margins of the emptiest half of the document. The measure has to hold
        // the *widest* content, so a high percentile is taken, and the result is then capped by the facing
        // margin. Erring towards a roomier text area only reflows lines earlier; erring the other way
        // pushes every page of the document onto two.
        double right = (1 - Percentile(rights, 0.85)) * size.Width;
        double bottom = (1 - Percentile(bottoms, 0.85)) * size.Height;
        right = Math.Min(right, left);
        bottom = Math.Min(bottom, top);

        double maxH = size.Width * 0.35, maxV = size.Height * 0.35;
        return new DocxMargins(
            Math.Clamp(left, 18, maxH),
            Math.Clamp(top, 18, maxV),
            Math.Clamp(right, 18, maxH),
            Math.Clamp(bottom, 18, maxV));
    }

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        int index = Math.Clamp((int)Math.Round((values.Count - 1) * fraction), 0, values.Count - 1);
        return values[index];
    }

    // ---- running heads ----

    private static readonly Regex DigitRun = new(@"\d+", RegexOptions.Compiled);

    /// <summary>Samples kept per candidate; two are enough to tell a page number from a section number.</summary>

    private static string NormalizeRunningHead(string text) =>
        DigitRun.Replace(text.Trim(), "#").Replace("  ", " ");

    private sealed class Candidate
    {
        public HashSet<int> Pages { get; } = [];
        public List<(int Page, string Text, RectD Bounds, double PageHeight, RectD Area)> Samples { get; } = [];

        /// <summary>The parts of the first sample, when the line is set in pieces across the page.</summary>
        public List<RectD> Pieces { get; set; } = [];
        public TextStyle Style { get; set; } = TextStyle.Default;

        /// <summary>
        /// The look of each part of the first sample, one per part: a head is often a grey title at one
        /// side and a red "CONFIDENTIAL" at the other, and one style for the line would lose the red.
        /// </summary>
        public List<TextStyle> PieceStyles { get; set; } = [];
    }

    /// <summary>What the running-head pass settled.</summary>
    internal sealed record RunningHeads(HashSet<string> Keys, DocxHeaderFooter? Header, DocxHeaderFooter? Footer)
    {
        public static RunningHeads None => new([], null, null);

        public int PageNumberStart { get; init; }
        public bool FirstPageUnheaded { get; init; }
        public DocxHeaderFooter? FirstHeader { get; init; }
        public DocxHeaderFooter? FirstFooter { get; init; }
        public double HeaderDistance { get; init; } = 36;
        public double FooterDistance { get; init; } = 36;
        public List<(DocxAlignment Alignment, double X)> HeaderTabs { get; init; } = [];
        public List<(DocxAlignment Alignment, double X)> FooterTabs { get; init; } = [];
    }

    /// <summary>A running head or foot that was chosen, and where it sits.</summary>
    private sealed record Chosen(DocxHeaderFooter Content, int Start, bool SkipsFirst, double Distance)
    {
        /// <summary>Tab stops for a head set in pieces, as normalized page positions until margins are known.</summary>
        public List<(DocxAlignment Alignment, double X)> Tabs { get; init; } = [];
    }

    private static RunningHeads FindRunningHeads(IReadOnlyList<PageContent> pages)
    {
        if (pages.Count < 4 || pages.Zip(pages.Skip(1)).Any(p => p.Second.PageIndex != p.First.PageIndex + 1))
            return RunningHeads.None;

        var top = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var bottom = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        // A running head is set no larger than the text it runs over. A line in the band set larger is a
        // title — "Chapter 3" at the head of each chapter's page — however regularly it recurs.
        var sizes = new Dictionary<double, int>();
        foreach (var page in pages)
            foreach (var span in page.Spans)
                sizes[SizeKey(span.Style.SizePoints)] = sizes.GetValueOrDefault(SizeKey(span.Style.SizePoints)) + (span.End - span.Start);
        double body = sizes.Count == 0 ? 11 : sizes.OrderByDescending(p => p.Value).First().Key;

        foreach (var page in pages)
        {
            var area = TextArea(page);
            foreach (var line in page.Text.Lines)
            {
                if (line.Bounds.IsEmpty || line.End <= line.Start || ContentComposer.IsOffPage(line.Bounds)) continue;
                string raw = page.Text.Text[line.Start..line.End].Trim();
                if (raw.Length is 0 or > 120) continue;

                var bucket = line.Bounds.Bottom <= ContentComposerBands.Header ? top
                    : line.Bounds.Top >= ContentComposerBands.Footer ? bottom
                    : null;
                if (bucket is null) continue;

                // A running head is short, or set in pieces across the page — a title at one side, a
                // reference at the other. A full line of prose that happens to sit high on the page is neither.
                var pieces = Pieces(page, line);
                if (pieces.Count < 2 && area.Width > 0 && line.Bounds.Width > area.Width * 0.6) continue;

                var style = page.StyleAt(line.Start);
                if (style.SizePoints > body * 1.15) continue;

                string key = RunningKey(line.Bounds, raw);
                if (!bucket.TryGetValue(key, out var candidate))
                {
                    bucket[key] = candidate = new Candidate
                    {
                        Style = page.StyleAt(line.Start),
                        Pieces = pieces.Count > 1 ? [.. pieces.Select(p => p.Bounds)] : [],
                        PieceStyles = [.. pieces.Select(p => page.StyleAt(p.Start))],
                    };
                }
                string text = pieces.Count > 1 ? string.Join("\t", pieces.Select(p => p.Text)) : raw;
                candidate.Pages.Add(page.PageIndex);
                candidate.Samples.Add((page.PageIndex, text, line.Bounds, page.Size.Height, area));
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        int firstPage = pages[0].PageIndex;
        var header = Pick(top, pages, firstPage, keys, isHeader: true);
        var footer = Pick(bottom, pages, firstPage, keys, isHeader: false);

        // Word can leave the running head and foot off the first page only together. When just one of them
        // is missing there, the other is given to the first page separately, so it is not lost with it.
        bool skipsFirst = header?.SkipsFirst == true || footer?.SkipsFirst == true;
        int start = header?.Start ?? 0;
        if (start == 0) start = footer?.Start ?? 0;

        return new RunningHeads(keys, header?.Content, footer?.Content)
        {
            PageNumberStart = start,
            FirstPageUnheaded = skipsFirst,
            FirstHeader = skipsFirst && header is { SkipsFirst: false } ? header.Content with { } : null,
            FirstFooter = skipsFirst && footer is { SkipsFirst: false } ? footer.Content with { } : null,
            HeaderDistance = header?.Distance ?? 36,
            FooterDistance = footer?.Distance ?? 36,
            HeaderTabs = header?.Tabs ?? [],
            FooterTabs = footer?.Tabs ?? [],
        };
    }

    private static Chosen? Pick(
        Dictionary<string, Candidate> candidates, IReadOnlyList<PageContent> pages, int firstPage,
        HashSet<string> keys, bool isHeader)
    {
        int pageCount = pages.Count;
        Chosen? best = null;
        int bestCount = 0;
        string? bestKey = null;

        foreach (var (key, candidate) in candidates)
        {
            int count = candidate.Pages.Count;

            // A running head is on every page — or nearly: a book leaves it off its chapter openings, and a
            // report off its title page. Word repeats it on every page of the section, which puts it back on
            // those few; that costs far less than leaving it in the body, where it interrupts the text on
            // every page. It has to appear exactly once on each page that has it.
            bool allButFirst = !candidate.Pages.Contains(firstPage);
            bool nearlyEvery = count == pageCount || count >= Math.Max(3, (int)Math.Ceiling(pageCount * 0.75));
            if (!nearlyEvery || candidate.Samples.Count != count) continue;
            if (count <= bestCount) continue;

            var (content, value) = BuildRunning(candidate);
            if (content is null) continue;

            // The number printed on the first page this appears on; the section starts a page or so earlier.
            int start = value == 0 ? 0 : value - (candidate.Samples[0].Page - firstPage);

            var samples = candidate.Samples;
            double distance = isHeader
                ? samples.Min(s => s.Bounds.Top * s.PageHeight)
                : samples.Min(s => (1 - s.Bounds.Bottom) * s.PageHeight);

            bestCount = count;
            var area = samples[0].Area;
            var tabs = candidate.Pieces.Skip(1).Select(piece => AlignmentOf(piece, area) switch
            {
                DocxAlignment.Right => (DocxAlignment.Right, piece.Right),
                DocxAlignment.Center => (DocxAlignment.Center, piece.Center.X),
                _ => (DocxAlignment.Left, piece.Left),
            }).ToList();
            var rule = RepeatedRule(pages, candidate, isHeader);
            best = new Chosen(content with
            {
                Alignment = tabs.Count > 0 ? DocxAlignment.Left : AlignmentOf(samples[0].Bounds, area),
                BorderBelow = isHeader ? rule : null,
                BorderAbove = isHeader ? null : rule,
            }, Math.Max(0, start), allButFirst, Math.Max(0, distance)) { Tabs = tabs };
            bestKey = key;
        }
        if (bestKey is not null) keys.Add(bestKey);
        return best;
    }

    /// <summary>
    /// A line cut where a gap is far wider than a word space: the parts of a running head spread across the
    /// page. PDFium joins them into one line with a single space, which hides that they were ever apart.
    /// </summary>
    private static List<(string Text, RectD Bounds, int Start)> Pieces(PageContent page, TextLine line)
    {
        var pieces = new List<(string, RectD, int)>();
        double gap = Math.Max(0.02, line.Bounds.Height * 3);
        int start = -1;
        var bounds = RectD.Empty;
        RectD previous = RectD.Empty;

        void Close(int end)
        {
            if (start < 0) return;
            string text = page.Text.Text[start..end].Trim();
            if (text.Length > 0 && !bounds.IsEmpty) pieces.Add((text, bounds, start));
            start = -1;
            bounds = RectD.Empty;
        }

        for (int i = line.Start; i < line.End; i++)
        {
            if (char.IsWhiteSpace(page.Text.Text[i]) || !page.Text.TryGetBox(i, out var box) || box.IsEmpty) continue;
            if (start >= 0 && box.Left - previous.Right > gap) Close(i);
            if (start < 0) start = i;
            bounds = bounds.Union(box);
            previous = box;
        }
        Close(line.End);
        return pieces;
    }

    /// <summary>
    /// The rule a running head sits on, or a foot sits under: a long horizontal line between the head and
    /// the body, at the same height on the pages that carry the head. It belongs to the head, so it goes
    /// into Word's header with it rather than being left behind in the body or dropped.
    /// </summary>
    private static DocxBorder? RepeatedRule(IReadOnlyList<PageContent> pages, Candidate candidate, bool isHeader)
    {
        var found = new List<(RuleSegment Rule, double Gap)>();
        foreach (var (pageIndex, _, bounds, height, area) in candidate.Samples)
        {
            var page = pages.FirstOrDefault(p => p.PageIndex == pageIndex);
            if (page is null) continue;

            RuleSegment? best = null;
            double bestGap = double.MaxValue;
            foreach (var rule in page.Rules)
            {
                if (!rule.IsHorizontal || rule.Bounds.Width < area.Width * 0.3) continue;
                double gap = isHeader ? rule.Bounds.Top - bounds.Bottom : bounds.Top - rule.Bounds.Bottom;
                if (gap < -1 / height || gap * height > 18) continue;
                if (gap < bestGap) (best, bestGap) = (rule, gap);
            }
            if (best is { } found1) found.Add((found1, Math.Max(0, bestGap * height)));
        }

        // On most of the pages, or it is a line that happens to be there, not the head's own.
        if (found.Count == 0 || found.Count * 5 < candidate.Samples.Count * 4) return null;
        var typical = found[found.Count / 2];
        double thickness = typical.Rule.ThicknessPoints > 0 ? typical.Rule.ThicknessPoints : 0.75;
        return new DocxBorder(typical.Rule.Color, Math.Clamp((int)Math.Round(thickness * 8), 2, 96), typical.Gap);
    }

    /// <summary>Where the line sits across the text area: flush with one edge, or centred between them.</summary>
    private static DocxAlignment AlignmentOf(RectD line, RectD area)
    {
        if (area.IsEmpty || area.Width <= 0) return DocxAlignment.Center;
        double left = line.Left - area.Left, right = area.Right - line.Right;
        if (Math.Abs(left - right) <= area.Width * 0.04) return DocxAlignment.Center;
        return left < right ? DocxAlignment.Left : DocxAlignment.Right;
    }

    /// <summary>
    /// Splits the sample around its page number so the number becomes a live PAGE field.
    ///
    /// Which number is the page number is not obvious: "Section 1, page 1" has two, and on the first page
    /// they are both 1. The answer is the run whose value tracks the page across <i>every</i> sample, which
    /// also gives the offset when the printed numbering does not start at one.
    /// </summary>
    private static (DocxHeaderFooter? Content, int Value) BuildRunning(Candidate candidate)
    {
        string sample = candidate.Samples[0].Text;
        var matches = DigitRun.Matches(sample);

        // Each part of the head in its own look; the parts are separated by tabs.
        var styles = new TextStyle[sample.Length];
        for (int c = 0, piece = 0; c < sample.Length; c++)
        {
            if (sample[c] == '\t') piece++;
            styles[c] = piece < candidate.PieceStyles.Count ? candidate.PieceStyles[piece] : candidate.Style;
        }

        List<DocxRun> Runs(int from, int to)
        {
            var runs = new List<DocxRun>();
            for (int c = from; c < to;)
            {
                int end = c + 1;
                while (end < to && styles[end] == styles[c]) end++;
                runs.Add(new DocxRun(sample[c..end], styles[c]));
                c = end;
            }
            return runs;
        }

        DocxHeaderFooter Literal() => new(Runs(0, sample.Length), DocxAlignment.Center);
        if (matches.Count == 0) return (Literal(), 0);

        int index = -1;
        for (int i = 0; i < matches.Count; i++)
        {
            int? common = null;
            bool consistent = true;
            foreach (var (page, text, _, _, _) in candidate.Samples)
            {
                var runs = DigitRun.Matches(text);
                if (runs.Count != matches.Count || !int.TryParse(runs[i].Value, out int value))
                {
                    consistent = false;
                    break;
                }
                int difference = value - page;
                common ??= difference;
                if (common != difference)
                {
                    consistent = false;
                    break;
                }
            }

            if (consistent && common is > 0)
            {
                index = i;
                break;
            }
        }

        if (index < 0) return candidate.Samples.All(s => s.Text == sample) ? (Literal(), 0) : (null, 0);

        var match = matches[index];
        var runs2 = new List<DocxRun>();
        string before = sample[..match.Index];
        string after = sample[(match.Index + match.Length)..];
        // Only the page number may vary. "Section 2" must never become "Section 1".
        foreach (var (_, text, _, _, _) in candidate.Samples)
        {
            var number = DigitRun.Matches(text)[index];
            if (text[..number.Index] != before || text[(number.Index + number.Length)..] != after)
                return (null, 0);
        }
        runs2.AddRange(Runs(0, match.Index));
        int numberRun = runs2.Count;
        runs2.Add(new DocxRun(match.Value, styles[match.Index]));
        runs2.AddRange(Runs(match.Index + match.Length, sample.Length));

        return (new DocxHeaderFooter(runs2, DocxAlignment.Center) { PageNumberRun = numberRun },
                int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static RectD TextArea(PageContent page)
    {
        var bounds = RectD.Empty;
        foreach (var line in page.Text.Lines)
            if (!line.Bounds.IsEmpty && !ContentComposer.IsOffPage(line.Bounds)) bounds = bounds.Union(line.Bounds);
        return bounds;
    }

    // ---- outline ----

    private static IEnumerable<(int Page, double Y, int Depth, string Title)> Flatten(
        IReadOnlyList<OutlineItem>? items, int depth = 0)
    {
        if (items is null || depth > 8) yield break;
        foreach (var item in items)
        {
            if (item.Target.IsValid && !string.IsNullOrWhiteSpace(item.Title))
                yield return (item.Target.PageIndex, item.Target.Y ?? double.NaN, depth, item.Title.Trim());
            foreach (var child in Flatten(item.Children, depth + 1))
                yield return child;
        }
    }
}

/// <summary>Band limits shared by the profile and the composer, so they cannot drift apart.</summary>
internal static class ContentComposerBands
{
    // A running head has to repeat, digits aside, on every page to be taken as one, which is a far stronger
    // test than position; the bands only need to be wide enough to hold the heads real documents set.
    public const double Header = 0.11;
    public const double Footer = 0.89;
    public const int MinRepeats = 3;
}
