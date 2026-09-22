using LitePdf.Core.Text;

namespace LitePdf.Core.Export;

/// <summary>
/// What has to be true of a scanned page once its text has been recognized.
///
/// A scan is one big picture with words printed on it. Recognizing it turns those words into text — and
/// the moment that happens the picture is a duplicate of the text, not a companion to it. Writing both is
/// how a ten-page scan becomes twenty pages of Word: a full-page image, then the same words again.
/// </summary>
public static class RecognizedPage
{
    /// <summary>A picture covering this much of the page is the scan itself, not an illustration.</summary>
    public const double ScanCoverage = 0.6;

    /// <summary>
    /// Font size as a fraction of the height of the box the recognizer draws round a line. The box wraps
    /// the ink from the tallest ascender to the deepest descender, which is a little more than the type.
    /// </summary>
    private const double SizeFromLineBox = 0.78;

    /// <summary>A line within this much of the body's line height is body text, whatever it measures.</summary>
    private const double BodyTolerance = 0.18;

    /// <summary>Above this much of the body size, a line has to be short before it is set any larger.</summary>
    private const double HeadingRatio = 1.5;

    private const int HeadingCharacters = 40;

    public static bool IsPageScan(PlacedImage image) =>
        !image.IsDrawing && image.Bounds.Width * image.Bounds.Height >= ScanCoverage;

    /// <summary>True for the placeholder the layout pass writes where it found a picture, not words.</summary>
    public static bool IsFigureLine(string text) =>
        text.AsSpan().Trim().Equals(OcrLayout.FigurePlaceholder, StringComparison.Ordinal);

    /// <summary>The picture regions a recognized page found, in normalized page coordinates.</summary>
    public static List<RectD> Figures(PageText text)
    {
        var figures = new List<RectD>();
        foreach (var line in text.Lines)
        {
            if (line.Bounds.IsEmpty || line.End <= line.Start) continue;
            if (IsFigureLine(text.Text[line.Start..Math.Min(line.End, text.Length)])) figures.Add(line.Bounds);
        }
        return figures;
    }

    /// <summary>
    /// Gives recognized text its type sizes, measured from the lines themselves and settled across the
    /// whole document rather than page by page.
    ///
    /// Two things matter. The body size has to be <i>one</i> size: recognizer boxes wobble by a fifth from
    /// line to line, and a paragraph whose lines each claim a different size is split into one paragraph
    /// per line by every rule that watches for a change of style. And it has to be measured over the whole
    /// document, because a page that happens to open with a banner would otherwise set the body size for
    /// itself alone, and the same scan would come out in three different sizes.
    /// </summary>
    public static List<PageContent> Style(IReadOnlyList<PageContent> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        var boxes = new List<double>();
        var glyphs = new List<double>();
        foreach (var page in pages)
        {
            if (page.Text.Source != TextSource.Ocr) continue;
            foreach (var (box, glyph) in Measurements(page))
            {
                boxes.Add(box);
                glyphs.Add(glyph);
            }
        }

        var styled = new List<PageContent>(pages.Count);
        if (boxes.Count == 0) return [.. pages];

        // Two measurements, each wrong on its own. The line's box is the right size for ordinary text but
        // is thrown by one stacked fraction; the median height of the characters is steady but sits near
        // the x-height. So the body size is calibrated from the boxes, and each line is then sized by how
        // its own characters compare with the body's — a ratio, which neither bias can move.
        double bodyBox = Mode(boxes);
        double bodyGlyph = Mode(glyphs);
        double body = Math.Clamp(Math.Round(bodyBox * SizeFromLineBox * 2) / 2, 6, 36);

        foreach (var page in pages)
        {
            if (page.Text.Source != TextSource.Ocr || page.Text.Length == 0)
            {
                styled.Add(page);
                continue;
            }

            var spans = new List<StyledSpan>();
            foreach (var line in page.Text.Lines)
            {
                if (line.End <= line.Start) continue;
                double glyph = LineHeight(page, line);
                double ratio = bodyGlyph > 0 && glyph > 0 ? glyph / bodyGlyph : 1;

                // A line of prose stays body text however tall its characters measure. Only a short line
                // may be set larger, because a heading is short — a line of answers with a fraction in it
                // measures half as tall again and is still prose.
                bool prose = ratio > HeadingRatio && line.End - line.Start > HeadingCharacters;
                double size = prose || Math.Abs(ratio - 1) <= BodyTolerance
                    ? body
                    : Math.Clamp(Math.Round(body * ratio * 2) / 2, body * 0.7, body * 2);

                var style = new TextStyle("Calibri", size, false, false, 0x000000);
                if (spans.Count > 0 && spans[^1].End <= line.Start && spans[^1].Style.Matches(style))
                    spans[^1] = spans[^1] with { End = line.End };
                else
                    spans.Add(new StyledSpan(line.Start, line.End, style));
            }

            styled.Add(page with { Spans = spans });
        }

        return styled;
    }

    /// <summary>
    /// Per line, the height of its box and the median height of its characters, both in points, leaving
    /// out the figure placeholders, which are not type at all.
    /// </summary>
    private static IEnumerable<(double Box, double Glyph)> Measurements(PageContent page)
    {
        foreach (var line in page.Text.Lines)
        {
            if (line.Bounds.IsEmpty || line.End <= line.Start) continue;
            if (IsFigureLine(page.Text.Text[line.Start..Math.Min(line.End, page.Text.Length)])) continue;

            double box = line.Bounds.Height * page.Size.Height;
            double glyph = LineHeight(page, line);
            if (box is > 2 and < 96 && glyph > 0) yield return (box, glyph);
        }
    }

    /// <summary>
    /// How tall the type on a line is: the median height of its characters, not the height of the line's
    /// own box. One stacked fraction, one superscript, or one pen stroke through the line makes that box
    /// half as tall again as the type in it — and a line measured that way comes out set in 32 pt in the
    /// middle of a paragraph of 13 pt prose.
    /// </summary>
    private static double LineHeight(PageContent page, TextLine line)
    {
        var heights = new List<double>();
        for (int i = line.Start; i < line.End && i < page.Text.Length; i++)
        {
            if (char.IsWhiteSpace(page.Text.Text[i])) continue;
            if (page.Text.TryGetBox(i, out var box) && !box.IsEmpty) heights.Add(box.Height * page.Size.Height);
        }
        if (heights.Count == 0) return 0;
        heights.Sort();
        return heights[heights.Count / 2];
    }

    /// <summary>The commonest line height to the nearest point: the body, not the average of the page.</summary>
    private static double Mode(List<double> heights)
    {
        var buckets = new Dictionary<int, (int Count, double Sum)>();
        foreach (double height in heights)
        {
            int key = (int)Math.Round(height);
            var bucket = buckets.GetValueOrDefault(key);
            buckets[key] = (bucket.Count + 1, bucket.Sum + height);
        }

        int best = 0;
        double bestSum = 0;
        foreach (var (_, bucket) in buckets)
        {
            if (bucket.Count <= best) continue;
            best = bucket.Count;
            bestSum = bucket.Sum;
        }
        return best > 0 ? bestSum / best : heights[heights.Count / 2];
    }
}
