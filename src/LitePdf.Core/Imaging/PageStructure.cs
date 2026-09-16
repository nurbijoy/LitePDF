namespace LitePdf.Core.Imaging;

/// <summary>A vertical slice of the page holding one text column, normalized (0..1).</summary>
public readonly record struct ColumnSpan(double Left, double Right);

/// <summary>
/// What the ink on a page looks like, independent of what it says: where the glyphs are, which thin horizontal
/// segments could be fraction bars or underlines, and which areas are drawings rather than text.
/// A recognizer only returns words and boxes; this is what lets the layout pass rebuild stacked fractions,
/// superscripts and figures out of them. All rectangles are normalized to the analysed image.
/// </summary>
public sealed class PageStructure
{
    /// <summary>More rows of text than this inside a region make it a table or a paragraph, not a drawing.</summary>
    private const int MaxFigureLabelRows = 4;

    private readonly List<InkBlob> _blobs;
    private readonly List<int>[] _grid;
    private readonly int _cell, _columns, _rows;
    private readonly int _width, _height;

    private readonly List<InkBlob> _drawings;

    private PageStructure(int width, int height, List<InkBlob> blobs, double glyphHeight,
        IReadOnlyList<RectD> rules, List<InkBlob> drawings, IReadOnlyList<ColumnSpan> columns)
    {
        _width = width;
        _height = height;
        _blobs = blobs;
        _drawings = drawings;
        GlyphHeight = glyphHeight;
        Rules = rules;
        Columns = columns;

        _cell = Math.Max(8, (int)(glyphHeight * height));
        _columns = Math.Max(1, width / _cell + 1);
        _rows = Math.Max(1, height / _cell + 1);
        _grid = new List<int>[_columns * _rows];
        for (int i = 0; i < blobs.Count; i++)
        {
            var b = blobs[i];
            for (int cy = b.Top / _cell; cy <= Math.Min(b.Bottom / _cell, _rows - 1); cy++)
                for (int cx = b.Left / _cell; cx <= Math.Min(b.Right / _cell, _columns - 1); cx++)
                    (_grid[cy * _columns + cx] ??= []).Add(i);
        }
    }

    /// <summary>Typical glyph height, normalized to the page; the unit every other threshold is expressed in.</summary>
    public double GlyphHeight { get; }

    /// <summary>Thin horizontal segments: fraction bars, underlines, table rules.</summary>
    public IReadOnlyList<RectD> Rules { get; }

    /// <summary>
    /// Text columns, left to right, or a single span covering the page. Reading order follows these, so a
    /// two-column paper is not read straight across.
    /// </summary>
    public IReadOnlyList<ColumnSpan> Columns { get; }

    public static PageStructure Empty { get; } = new(1, 1, [], 0.02, [], [], [new ColumnSpan(0, 1)]);

    /// <summary>Ink that is too big, too sparse or too tall to be type, and so may belong to a drawing.</summary>
    public bool HasDrawings => _drawings.Count > 0;

    /// <param name="textHeight">Typical height of a line of text, normalized; 0 to measure it from the ink.</param>
    public static PageStructure Analyze(ScanPage page, double textHeight)
    {
        int w = page.Width, h = page.Height;
        if (w < 8 || h < 8) return Empty;

        var blobs = ConnectedComponents.Find(page.Ink, w, h);
        if (blobs.Count == 0) return Empty;

        double glyph = textHeight > 0 ? textHeight * 0.72 : MedianGlyphHeight(blobs) / h;
        int glyphPx = Math.Max(4, (int)Math.Round(glyph * h));

        var rules = new List<RectD>();
        var drawings = new List<InkBlob>();
        foreach (var blob in blobs)
        {
            // A rule is a bar, an underline or a table edge. On its own it never makes a drawing, and letting
            // one seed a region chains every underline on the page into one block that swallows the prose.
            if (IsRule(blob, glyphPx)) rules.Add(Normalize(blob, w, h));
            else if (IsDrawing(blob, glyphPx)) drawings.Add(blob);
        }

        return new PageStructure(w, h, blobs, glyph, rules, drawings, FindColumns(page, glyphPx));
    }

    /// <summary>
    /// Per-character boxes for a recognized word, read off the ink itself. Returns null when the blobs cannot be
    /// matched to the characters confidently, in which case the caller should fall back to an even split.
    /// </summary>
    public IReadOnlyList<RectD>? GlyphBoxes(RectD word, int characterCount)
    {
        if (characterCount <= 0 || word.IsEmpty || _blobs.Count == 0) return null;

        int left = (int)(word.Left * _width), right = (int)Math.Ceiling(word.Right * _width);
        int top = (int)(word.Top * _height), bottom = (int)Math.Ceiling(word.Bottom * _height);
        int pad = Math.Max(1, (int)(GlyphHeight * _height * 0.15));

        var inside = new List<InkBlob>();
        foreach (int i in Candidates(left - pad, top - pad, right + pad, bottom + pad))
        {
            var b = _blobs[i];
            // Centre inside the word box: a blob shared with a neighbouring word stays with its own word.
            if (b.CenterX >= left - pad && b.CenterX <= right + pad && b.CenterY >= top - pad && b.CenterY <= bottom + pad)
                inside.Add(b);
        }
        if (inside.Count == 0) return null;

        inside.Sort((a, b) => a.Left.CompareTo(b.Left));
        var merged = MergeOverlapping(inside);
        if (merged.Count != characterCount) return null;

        var boxes = new RectD[characterCount];
        for (int i = 0; i < characterCount; i++) boxes[i] = Normalize(merged[i], _width, _height);
        return boxes;
    }

    /// <summary>
    /// Bounding box of the ink whose blobs sit inside <paramref name="area"/>, or empty when there is none.
    /// Used to find what a fraction bar has above and below it without relying on the recognizer.
    /// </summary>
    public RectD InkBoundsIn(RectD area)
    {
        int left = (int)(area.Left * _width), right = (int)Math.Ceiling(area.Right * _width);
        int top = (int)(area.Top * _height), bottom = (int)Math.Ceiling(area.Bottom * _height);
        var found = default(InkBlob);
        bool any = false;
        foreach (int i in Candidates(left, top, right, bottom))
        {
            var b = _blobs[i];
            if (b.CenterX < left || b.CenterX > right || b.CenterY < top || b.CenterY > bottom) continue;
            found = any ? found.Union(b) : b;
            any = true;
        }
        return any ? Normalize(found, _width, _height) : RectD.Empty;
    }

    /// <summary>The column containing a point, or the whole page when it is not laid out in columns.</summary>
    public int ColumnAt(double x)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (x < Columns[i].Right) return i;
        return Math.Max(0, Columns.Count - 1);
    }

    /// <summary>
    /// Splits the page at vertical gutters: runs of x where almost no ink falls anywhere down the page. Only a
    /// real column break survives that test, because running text crosses any gap left by a single line.
    /// </summary>
    private static List<ColumnSpan> FindColumns(ScanPage page, int glyphPx)
    {
        int w = page.Width, h = page.Height;
        var single = new List<ColumnSpan> { new(0, 1) };
        int step = Math.Max(1, h / 1200);

        var profile = new int[w];
        for (int y = 0; y < h; y += step)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
                if (page.Ink[row + x] != 0) profile[x]++;
        }

        int contentLeft = 0, contentRight = w - 1;
        while (contentLeft < w && profile[contentLeft] == 0) contentLeft++;
        while (contentRight > contentLeft && profile[contentRight] == 0) contentRight--;
        int contentWidth = contentRight - contentLeft + 1;
        if (contentWidth < w / 3) return single;

        // A column is worth splitting out only if it is wide enough to hold a line of text.
        int minColumn = Math.Max(glyphPx * 8, (int)(contentWidth * 0.15));
        int minGutter = Math.Max(glyphPx, (int)(contentWidth * 0.02));
        int noise = Math.Max(1, h / step / 400);

        var cuts = new List<int>();
        for (int x = contentLeft; x <= contentRight;)
        {
            if (profile[x] > noise)
            {
                x++;
                continue;
            }
            int start = x;
            while (x <= contentRight && profile[x] <= noise) x++;
            int end = x - 1;
            if (end - start + 1 >= minGutter &&
                start - contentLeft >= minColumn && contentRight - end >= minColumn)
                cuts.Add((start + end) / 2);
        }
        if (cuts.Count == 0 || cuts.Count > 3) return single;

        var columns = new List<ColumnSpan>();
        int from = 0;
        foreach (int cut in cuts)
        {
            columns.Add(new ColumnSpan((double)from / w, (double)cut / w));
            from = cut;
        }
        columns.Add(new ColumnSpan((double)from / w, 1));
        return columns;
    }

    /// <summary>Merges blobs that sit above one another (the dot of an i, the two bars of an equals sign).</summary>
    private static List<InkBlob> MergeOverlapping(List<InkBlob> sorted)
    {
        var merged = new List<InkBlob>();
        foreach (var blob in sorted)
        {
            bool joined = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var m = merged[i];
                int overlap = Math.Min(m.Right, blob.Right) - Math.Max(m.Left, blob.Left) + 1;
                if (overlap > 0 && overlap >= Math.Min(m.Width, blob.Width) * 0.55)
                {
                    merged[i] = m.Union(blob);
                    joined = true;
                    break;
                }
            }
            if (!joined) merged.Add(blob);
        }
        merged.Sort((a, b) => a.Left.CompareTo(b.Left));
        return merged;
    }

    private IEnumerable<int> Candidates(int left, int top, int right, int bottom)
    {
        int cx0 = Math.Clamp(left / _cell, 0, _columns - 1), cx1 = Math.Clamp(right / _cell, 0, _columns - 1);
        int cy0 = Math.Clamp(top / _cell, 0, _rows - 1), cy1 = Math.Clamp(bottom / _cell, 0, _rows - 1);
        var seen = new HashSet<int>();
        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                var cell = _grid[cy * _columns + cx];
                if (cell is null) continue;
                foreach (int i in cell)
                    if (seen.Add(i)) yield return i;
            }
    }

    /// <summary>
    /// A thin, solid horizontal segment: a fraction bar, an underline, or a table rule. The height allowance is
    /// generous because a bar printed at 300 dpi and rendered at 400 lands at a quarter of the glyph height,
    /// while the shortest real glyph is still three times taller.
    /// </summary>
    private static bool IsRule(InkBlob blob, int glyphPx) =>
        blob.Height <= Math.Max(3, glyphPx * 0.35) &&
        blob.Width >= Math.Max(6, glyphPx * 0.5) &&
        blob.Width >= blob.Height * 3.0 &&
        blob.Density >= 0.55;

    /// <summary>
    /// Ink that cannot be a glyph: too tall or too wide for type, or a large sparse outline — the signature of
    /// a circle, a triangle, an axis pair or a table border.
    /// </summary>
    private static bool IsDrawing(InkBlob blob, int glyphPx) =>
        blob.Height > glyphPx * 2.6 ||
        blob.Width > glyphPx * 9 ||
        (blob.Density < 0.22 && blob.Height > glyphPx * 1.3 && blob.Width > glyphPx * 1.3);

    /// <summary>
    /// Groups drawing-like ink into regions and grows each over the labels inside it, so a diagram and its
    /// annotations are reported as one figure instead of leaking stray words into the reading order.
    /// <para>
    /// <paramref name="textLines"/> is what the recognizer actually read. A region a line of prose runs
    /// through is not a drawing but ink over the text — a pen stroke, a circled number, a stray mark — and
    /// reporting it as a figure would pull whole paragraphs out of the reading order.
    /// </para>
    /// </summary>
    public List<RectD> FindFigures(IReadOnlyList<RectD> textLines)
    {
        int glyphPx = Math.Max(4, (int)Math.Round(GlyphHeight * _height));
        var figures = FindFigures(_drawings, _blobs, glyphPx, _width, _height);
        figures.RemoveAll(f => IsText(f, textLines, GlyphHeight));
        return figures;
    }

    /// <summary>
    /// A drawing sits in a band of its own, or beside short lines, and carries at most a couple of rows of
    /// labels. Two things are therefore not drawings, however much ink they hold: something a line of prose
    /// runs straight through (a pen stroke, a circled number, a crossing-out) and something filled with rows
    /// of text (a ruled table, a boxed paragraph), whose words belong in the reading order like any others.
    /// </summary>
    private static bool IsText(RectD area, IReadOnlyList<RectD> textLines, double glyphHeight)
    {
        var rows = new List<double>();
        foreach (var line in textLines)
        {
            var overlap = line.Intersect(area);
            // The line has to sit at this height, not merely clip a corner of it.
            if (overlap.IsEmpty || overlap.Height < line.Height * 0.5) continue;

            double beyond = Math.Max(area.Left - line.Left, 0) + Math.Max(line.Right - area.Right, 0);
            if (beyond > area.Width * 0.5) return true;

            double center = line.Center.Y;
            if (!rows.Any(r => Math.Abs(r - center) < glyphHeight)) rows.Add(center);
        }
        return rows.Count >= MaxFigureLabelRows;
    }

    private static List<RectD> FindFigures(List<InkBlob> graphics, List<InkBlob> all, int glyphPx, int w, int h)
    {
        var figures = new List<RectD>();
        if (graphics.Count == 0) return figures;

        int gap = glyphPx * 2;
        var regions = new List<(InkBlob Box, int Count)>();
        foreach (var blob in graphics)
        {
            var grown = new InkBlob(blob.Left - gap, blob.Top - gap, blob.Right + gap, blob.Bottom + gap, blob.Pixels);
            int target = -1;
            for (int i = regions.Count - 1; i >= 0; i--)
            {
                if (!regions[i].Box.Intersects(grown)) continue;
                if (target < 0)
                {
                    regions[i] = (regions[i].Box.Union(blob), regions[i].Count + 1);
                    target = i;
                }
                else
                {
                    regions[target] = (regions[target].Box.Union(regions[i].Box), regions[target].Count + regions[i].Count);
                    regions.RemoveAt(i);
                    target--;
                }
            }
            if (target < 0) regions.Add((blob, 1));
        }

        foreach (var (box, count) in regions)
        {
            // A single tall glyph (a bracket, an integral sign) is not a drawing.
            if (box.Width < glyphPx * 3 || box.Height < glyphPx * 2) continue;
            if (count < 2 && box.Width < glyphPx * 6 && box.Height < glyphPx * 4) continue;

            var region = box;
            // Pull in the labels sitting inside the drawing.
            for (int pass = 0; pass < 2; pass++)
                foreach (var blob in all)
                    if (blob.Left >= region.Left && blob.Right <= region.Right &&
                        blob.Top >= region.Top && blob.Bottom <= region.Bottom)
                        region = region.Union(blob);
            figures.Add(Normalize(region, w, h));
        }

        figures.Sort((a, b) => a.Top.CompareTo(b.Top));
        return figures;
    }

    private static double MedianGlyphHeight(List<InkBlob> blobs)
    {
        var heights = blobs.Where(b => b.Pixels >= 8).Select(b => b.Height).OrderBy(x => x).ToList();
        return heights.Count == 0 ? 10 : heights[heights.Count / 2];
    }

    private static RectD Normalize(InkBlob b, int w, int h) =>
        new((double)b.Left / w, (double)b.Top / h, (b.Right + 1.0) / w, (b.Bottom + 1.0) / h);
}
