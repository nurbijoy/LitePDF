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

    private DocumentProfile(
        double bodySize, string bodyFont, double glyphHeight, double linePitchPoints, DocxMargins margins,
        double[] headingSizes, HashSet<string> runningHeads, DocxHeaderFooter? header, DocxHeaderFooter? footer,
        int pageNumberStart, ILookup<int, (double Y, int Depth, string Title)> outline)
    {
        BodySize = bodySize;
        BodyFont = bodyFont;
        GlyphHeight = glyphHeight;
        LinePitchPoints = linePitchPoints;
        Margins = margins;
        _headingSizes = headingSizes;
        _runningHeads = runningHeads;
        Header = header;
        Footer = footer;
        PageNumberStart = pageNumberStart;
        Outline = outline;
    }

    public double BodySize { get; }

    public string BodyFont { get; }

    /// <summary>Median line height, normalized to the page: the unit the layout thresholds are expressed in.</summary>
    public double GlyphHeight { get; }

    /// <summary>Median baseline-to-baseline distance in points.</summary>
    public double LinePitchPoints { get; }

    public DocxMargins Margins { get; }

    public DocxHeaderFooter? Header { get; }

    public DocxHeaderFooter? Footer { get; }

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
        _runningHeads.Count > 0 && InBand(bounds) && _runningHeads.Contains(NormalizeRunningHead(text));

    private static bool InBand(RectD bounds) =>
        bounds.Bottom <= ContentComposerBands.Header || bounds.Top >= ContentComposerBands.Footer;

    public static DocumentProfile Build(
        IReadOnlyList<PageContent> pages, IReadOnlyList<OutlineItem>? outline, ExportOptions options)
    {
        var (runningHeads, header, footer, pageNumberStart) = options.HeadersFooters
            ? FindRunningHeads(pages)
            : ([], null, null, 0);

        var sizeWeights = new Dictionary<double, int>();
        var fontWeights = new Dictionary<string, int>();
        var lineHeights = new List<double>();
        var pitches = new List<double>();
        var lefts = new List<double>();
        var rights = new List<double>();
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
            foreach (var line in page.Text.Lines)
            {
                if (line.Bounds.IsEmpty || line.End <= line.Start) continue;
                string raw = page.Text.Text[line.Start..line.End];
                if (raw.Trim().Length == 0) continue;
                if (runningHeads.Count > 0 && InBand(line.Bounds) &&
                    runningHeads.Contains(NormalizeRunningHead(raw.Trim()))) continue;

                lineHeights.Add(line.Bounds.Height);
                body.Add(line.Bounds);
            }

            for (int i = 1; i < body.Count; i++)
            {
                double pitch = body[i].Top - body[i - 1].Top;
                if (pitch > 0 && pitch < 0.2) pitches.Add(pitch * page.Size.Height);
            }

            if (body.Count > 0)
            {
                lefts.Add(body.Min(b => b.Left));
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

        return new DocumentProfile(bodySize, bodyFont, glyph, pitchPoints, margins, headingSizes,
            runningHeads, header, footer, pageNumberStart, flattened);
    }

    private static DocxMargins MeasureMargins(
        List<double> lefts, List<double> rights, List<double> tops, List<double> bottoms, PageSize size)
    {
        if (lefts.Count == 0) return DocxMargins.Default;

        // Left and top are read straight off the page: every line starts at the left margin, so the median
        // is exactly right and resists the odd line that hangs into it.
        double left = ContentComposer.Median(lefts) * size.Width;
        double top = ContentComposer.Median(tops) * size.Height;

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
    private const int MaxSamples = 5;

    private static string NormalizeRunningHead(string text) =>
        DigitRun.Replace(text.Trim(), "#").Replace("  ", " ");

    private sealed class Candidate
    {
        public HashSet<int> Pages { get; } = [];
        public List<(int Page, string Text)> Samples { get; } = [];
        public TextStyle Style { get; set; } = TextStyle.Default;
    }

    private static (HashSet<string> Keys, DocxHeaderFooter? Header, DocxHeaderFooter? Footer, int Start)
        FindRunningHeads(IReadOnlyList<PageContent> pages)
    {
        if (pages.Count < 4) return ([], null, null, 0);

        var top = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var bottom = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            var area = TextArea(page);
            foreach (var line in page.Text.Lines)
            {
                if (line.Bounds.IsEmpty || line.End <= line.Start) continue;
                string raw = page.Text.Text[line.Start..line.End].Trim();
                if (raw.Length is 0 or > 120) continue;

                // A running head is short. A full line of prose that happens to sit high on the page is not one.
                if (area.Width > 0 && line.Bounds.Width > area.Width * 0.6) continue;

                var bucket = line.Bounds.Bottom <= ContentComposerBands.Header ? top
                    : line.Bounds.Top >= ContentComposerBands.Footer ? bottom
                    : null;
                if (bucket is null) continue;

                string key = NormalizeRunningHead(raw);
                if (!bucket.TryGetValue(key, out var candidate))
                {
                    bucket[key] = candidate = new Candidate { Style = page.StyleAt(line.Start) };
                }
                if (candidate.Pages.Add(page.PageIndex) && candidate.Samples.Count < MaxSamples)
                    candidate.Samples.Add((page.PageIndex, raw));
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var (header, headerStart) = Pick(top, pages.Count, keys);
        var (footer, footerStart) = Pick(bottom, pages.Count, keys);
        return (keys, header, footer, headerStart != 0 ? headerStart : footerStart);
    }

    private static (DocxHeaderFooter? Content, int Start) Pick(
        Dictionary<string, Candidate> candidates, int pageCount, HashSet<string> keys)
    {
        DocxHeaderFooter? best = null;
        int bestCount = 0, start = 0;

        foreach (var (key, candidate) in candidates)
        {
            int count = candidate.Pages.Count;
            // Three pages and a quarter of the document: enough that it is furniture, not content.
            if (count < ContentComposerBands.MinRepeats || count < pageCount * 0.25) continue;
            keys.Add(key);
            if (count <= bestCount) continue;

            bestCount = count;
            (best, start) = BuildRunning(candidate);
        }
        return (best, start);
    }

    /// <summary>
    /// Splits the sample around its page number so the number becomes a live PAGE field.
    ///
    /// Which number is the page number is not obvious: "Section 1, page 1" has two, and on the first page
    /// they are both 1. The answer is the run whose value tracks the page across <i>every</i> sample, which
    /// also gives the offset when the printed numbering does not start at one.
    /// </summary>
    private static (DocxHeaderFooter? Content, int Start) BuildRunning(Candidate candidate)
    {
        var (_, sample) = candidate.Samples[0];
        var style = candidate.Style;
        var matches = DigitRun.Matches(sample);

        DocxHeaderFooter Literal() => new([new DocxRun(sample, style)], DocxAlignment.Center);
        if (matches.Count == 0) return (Literal(), 0);

        int index = -1, offset = 1;
        for (int i = 0; i < matches.Count; i++)
        {
            int? common = null;
            bool consistent = true;
            foreach (var (page, text) in candidate.Samples)
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

            if (consistent && common is { } k and > 0)
            {
                index = i;
                offset = k;
                break;
            }
        }

        if (index < 0) return (Literal(), 0);

        var match = matches[index];
        var runs2 = new List<DocxRun>();
        string before = sample[..match.Index];
        string after = sample[(match.Index + match.Length)..];
        if (before.Length > 0) runs2.Add(new DocxRun(before, style));
        int numberRun = runs2.Count;
        runs2.Add(new DocxRun(match.Value, style));
        if (after.Length > 0) runs2.Add(new DocxRun(after, style));

        return (new DocxHeaderFooter(runs2, DocxAlignment.Center) { PageNumberRun = numberRun },
                offset == 1 ? 0 : offset);
    }

    private static RectD TextArea(PageContent page)
    {
        var bounds = RectD.Empty;
        foreach (var line in page.Text.Lines)
            if (!line.Bounds.IsEmpty) bounds = bounds.Union(line.Bounds);
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
    public const double Header = 0.085;
    public const double Footer = 0.915;
    public const int MinRepeats = 3;
}
