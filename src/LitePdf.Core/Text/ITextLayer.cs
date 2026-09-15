namespace LitePdf.Core.Text;

public enum TextSource
{
    Pdf,
    Ocr
}

public readonly record struct TextGlyph(char Char, RectD Box);

public sealed class PageTextLayer
{
    public int PageIndex { get; }
    public TextSource Source { get; }
    public IReadOnlyList<TextGlyph> Glyphs { get; }

    private readonly string _text;

    public PageTextLayer(int pageIndex, TextSource source, IReadOnlyList<TextGlyph> glyphs)
    {
        PageIndex = pageIndex;
        Source = source;
        Glyphs = glyphs;
        _text = new string(glyphs.Select(g => g.Char).ToArray());
    }

    public string GetText() => _text;

    public string GetText(int start, int count)
    {
        if (start < 0) start = 0;
        if (count < 0) count = 0;
        if (start >= Glyphs.Count) return string.Empty;
        int end = Math.Min(Glyphs.Count, start + count);
        return new string(Glyphs.Skip(start).Take(end - start).Select(g => g.Char).ToArray());
    }

    public int HitTest(PointD pagePoint, double tolerance)
    {
        // Find nearest glyph whose box contains point or is closest
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < Glyphs.Count; i++)
        {
            var b = Glyphs[i].Box;
            if (b.Width == 0 && b.Height == 0) continue;
            // Check containment: PDF coords Top > Bottom
            double left = Math.Min(b.Left, b.Right);
            double right = Math.Max(b.Left, b.Right);
            double bottom = Math.Min(b.Top, b.Bottom);
            double top = Math.Max(b.Top, b.Bottom);
            if (pagePoint.X >= left - tolerance && pagePoint.X <= right + tolerance &&
                pagePoint.Y >= bottom - tolerance && pagePoint.Y <= top + tolerance)
            {
                return i;
            }
            // distance to center
            double cx = (left + right) * 0.5;
            double cy = (bottom + top) * 0.5;
            double dx = pagePoint.X - cx;
            double dy = pagePoint.Y - cy;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        // If within tolerance distance
        if (bestDist <= tolerance * tolerance * 4)
            return best;
        return -1;
    }

    public IReadOnlyList<RectD> GetLineRects(int start, int count)
    {
        if (start < 0) start = 0;
        if (count <= 0) return Array.Empty<RectD>();
        int end = Math.Min(Glyphs.Count, start + count);
        if (start >= end) return Array.Empty<RectD>();

        var rects = new List<RectD>();
        int lineStart = start;
        double? currentTop = null;
        const double lineTolerance = 2.0; // points

        for (int i = start; i < end; i++)
        {
            var g = Glyphs[i];
            if (g.Char == '\n' || g.Char == '\r')
            {
                if (lineStart < i)
                    rects.Add(MergeRects(lineStart, i));
                lineStart = i + 1;
                currentTop = null;
                continue;
            }
            if (g.Box.Width == 0 && g.Box.Height == 0) continue;

            double top = Math.Max(g.Box.Top, g.Box.Bottom);
            if (currentTop == null)
            {
                currentTop = top;
            }
            else if (Math.Abs(top - currentTop.Value) > lineTolerance)
            {
                // new line
                if (lineStart < i)
                    rects.Add(MergeRects(lineStart, i));
                lineStart = i;
                currentTop = top;
            }
        }
        if (lineStart < end)
            rects.Add(MergeRects(lineStart, end));

        return rects;

        RectD MergeRects(int s, int e)
        {
            double left = double.MaxValue, right = double.MinValue, bottom = double.MaxValue, top = double.MinValue;
            bool has = false;
            for (int j = s; j < e; j++)
            {
                var b = Glyphs[j].Box;
                if (b.Width == 0 && b.Height == 0) continue;
                double l = Math.Min(b.Left, b.Right);
                double r = Math.Max(b.Left, b.Right);
                double bt = Math.Min(b.Top, b.Bottom);
                double t = Math.Max(b.Top, b.Bottom);
                if (!has)
                {
                    left = l; right = r; bottom = bt; top = t; has = true;
                }
                else
                {
                    left = Math.Min(left, l);
                    right = Math.Max(right, r);
                    bottom = Math.Min(bottom, bt);
                    top = Math.Max(top, t);
                }
            }
            if (!has) return new RectD(0, 0, 0, 0);
            return new RectD(left, top, right, bottom);
        }
    }

    // Build from OCR result
    public static PageTextLayer FromOcr(int pageIndex, OcrPageResult ocr, PageSize pageSize, int bitmapWidth, int bitmapHeight)
    {
        var glyphs = new List<TextGlyph>();
        double sx = pageSize.Width / bitmapWidth;
        double sy = pageSize.Height / bitmapHeight;

        foreach (var line in ocr.Lines)
        {
            foreach (var word in line.Words)
            {
                // word.Text may be multiple chars
                if (string.IsNullOrEmpty(word.Text)) continue;
                double wordPixelWidth = word.PixelRect.Width;
                double charPixelWidth = wordPixelWidth / word.Text.Length;
                for (int ci = 0; ci < word.Text.Length; ci++)
                {
                    char ch = word.Text[ci];
                    double pxLeft = word.PixelRect.Left + ci * charPixelWidth;
                    double pxRight = pxLeft + charPixelWidth;
                    double pxTop = word.PixelRect.Top;
                    double pxBottom = word.PixelRect.Bottom;

                    // Convert pixel (top-left origin) to PDF points (bottom-left)
                    double pdfLeft = pxLeft * sx;
                    double pdfRight = pxRight * sx;
                    double pdfTop = pageSize.Height - pxTop * sy;
                    double pdfBottom = pageSize.Height - pxBottom * sy;

                    var box = new RectD(pdfLeft, pdfTop, pdfRight, pdfBottom);
                    glyphs.Add(new TextGlyph(ch, box));
                }
                // space glyph between words
                glyphs.Add(new TextGlyph(' ', new RectD(0, 0, 0, 0)));
            }
            // Replace last space with newline if exists
            if (glyphs.Count > 0 && glyphs[^1].Char == ' ')
                glyphs[^1] = new TextGlyph('\n', new RectD(0, 0, 0, 0));
            else
                glyphs.Add(new TextGlyph('\n', new RectD(0, 0, 0, 0)));
        }

        // Trim trailing newline
        if (glyphs.Count > 0 && glyphs[^1].Char == '\n')
            glyphs.RemoveAt(glyphs.Count - 1);

        return new PageTextLayer(pageIndex, TextSource.Ocr, glyphs);
    }
}
