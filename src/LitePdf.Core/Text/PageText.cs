using System.Text;

namespace LitePdf.Core.Text;

public enum TextSource
{
    None,
    Pdf,
    Ocr,
}

/// <summary>A visual line of text: character range [Start, End) and its bounds in normalized page coordinates.</summary>
public readonly record struct TextLine(int Start, int End, RectD Bounds);

/// <summary>
/// The text of one page with a box per character (normalized page coordinates). Immutable and thread-safe.
/// Selection, copy, search and markup all work on this type, so OCR text behaves exactly like PDF text.
/// </summary>
public sealed class PageText
{
    private readonly float[] _boxes; // Left, Top, Right, Bottom per char; NaN when the char has no box.
    private readonly TextLine[] _lines;

    private PageText(int pageIndex, TextSource source, string text, float[] boxes)
    {
        if (boxes.Length != text.Length * 4)
            throw new ArgumentException("Expected four box values per character.", nameof(boxes));
        PageIndex = pageIndex;
        Source = source;
        Text = text;
        _boxes = boxes;
        _lines = BuildLines();
        VisibleCharCount = text.Count(c => !char.IsWhiteSpace(c));
    }

    public int PageIndex { get; }

    public TextSource Source { get; }

    public string Text { get; }

    public int Length => Text.Length;

    public int VisibleCharCount { get; }

    public IReadOnlyList<TextLine> Lines => _lines;

    public static PageText Empty(int pageIndex) => new(pageIndex, TextSource.None, string.Empty, []);

    public static PageText Create(int pageIndex, TextSource source, string text, float[] boxes) =>
        new(pageIndex, source, text, boxes);

    /// <summary>Builds page text from an OCR result whose word bounds are normalized to the page.</summary>
    public static PageText FromOcr(int pageIndex, OcrPageResult ocr)
    {
        var sb = new StringBuilder();
        var boxes = new List<float>();

        void Add(char c, RectD? box)
        {
            sb.Append(c);
            if (box is { } b) boxes.AddRange([(float)b.Left, (float)b.Top, (float)b.Right, (float)b.Bottom]);
            else boxes.AddRange([float.NaN, float.NaN, float.NaN, float.NaN]);
        }

        foreach (var line in ocr.Lines)
        {
            var words = line.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count == 0) continue;
            if (sb.Length > 0) Add('\n', null);

            for (int w = 0; w < words.Count; w++)
            {
                var word = words[w];
                if (w > 0)
                {
                    var prev = words[w - 1].Bounds;
                    var gap = new RectD(prev.Right, Math.Min(prev.Top, word.Bounds.Top), Math.Max(prev.Right, word.Bounds.Left), Math.Max(prev.Bottom, word.Bounds.Bottom));
                    Add(' ', gap.Width > 0 ? gap : null);
                }

                string t = word.Text.Trim();
                double charWidth = word.Bounds.Width / t.Length;
                for (int i = 0; i < t.Length; i++)
                {
                    double left = word.Bounds.Left + i * charWidth;
                    Add(t[i], new RectD(left, word.Bounds.Top, left + charWidth, word.Bounds.Bottom));
                }
            }
        }

        return new PageText(pageIndex, TextSource.Ocr, sb.ToString(), boxes.ToArray());
    }

    public bool TryGetBox(int index, out RectD box)
    {
        int o = index * 4;
        if ((uint)index >= (uint)Text.Length || float.IsNaN(_boxes[o]))
        {
            box = RectD.Empty;
            return false;
        }
        box = new RectD(_boxes[o], _boxes[o + 1], _boxes[o + 2], _boxes[o + 3]);
        return true;
    }

    /// <summary>Index of the character whose box contains the point (within tolerance), or -1.</summary>
    public int HitTest(PointD p, double tolerance = 0.002)
    {
        foreach (var line in _lines)
        {
            if (line.Bounds.IsEmpty || !line.Bounds.Contains(p, tolerance)) continue;
            for (int i = line.Start; i < line.End; i++)
                if (TryGetBox(i, out var b) && b.Contains(p, tolerance))
                    return i;
        }
        return -1;
    }

    /// <summary>Caret position (0..Length) nearest to the point, used for text selection.</summary>
    public int GetCaretIndex(PointD p)
    {
        TextLine? best = null;
        double bestScore = double.MaxValue;
        foreach (var line in _lines)
        {
            if (line.Bounds.IsEmpty) continue;
            var b = line.Bounds;
            double dy = p.Y < b.Top ? b.Top - p.Y : p.Y > b.Bottom ? p.Y - b.Bottom : 0;
            double dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;
            double score = dy * 4 + dx; // prefer the line at the pointer's height
            if (score < bestScore)
            {
                bestScore = score;
                best = line;
            }
        }

        if (best is not { } l) return 0;

        // Project onto the line's reading direction so rotated text (vertical or right-to-left on screen) works.
        var (ax, ay) = GetFlowDirection(l);
        double target = p.X * ax + p.Y * ay;
        int lastBoxed = l.Start - 1;
        for (int i = l.Start; i < l.End; i++)
        {
            if (!TryGetBox(i, out var box)) continue;
            var c = box.Center;
            if (target < c.X * ax + c.Y * ay) return i;
            lastBoxed = i;
        }
        return Math.Min(lastBoxed + 1, l.End);
    }

    /// <summary>Unit vector from the first to the last character of a line (left-to-right when unknown).</summary>
    private (double X, double Y) GetFlowDirection(TextLine line)
    {
        RectD? first = null, last = null;
        for (int i = line.Start; i < line.End; i++)
        {
            if (!TryGetBox(i, out var b)) continue;
            first ??= b;
            last = b;
        }
        if (first is not { } f || last is not { } l || f == l) return (1, 0);
        double dx = l.Center.X - f.Center.X, dy = l.Center.Y - f.Center.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        return length < 1e-9 ? (1, 0) : (dx / length, dy / length);
    }

    /// <summary>Range [Start, End) of the word (or single non-word character) at index.</summary>
    public (int Start, int End) GetWordRange(int index)
    {
        if (Length == 0) return (0, 0);
        index = Math.Clamp(index, 0, Length - 1);
        if (!IsWordChar(Text[index])) return (index, index + 1);
        int start = index, end = index + 1;
        while (start > 0 && IsWordChar(Text[start - 1])) start--;
        while (end < Length && IsWordChar(Text[end])) end++;
        return (start, end);
    }

    /// <summary>Range [Start, End) of the visual line containing index.</summary>
    public (int Start, int End) GetLineRange(int index)
    {
        int li = FindLine(index);
        return li < 0 ? (0, 0) : (_lines[li].Start, _lines[li].End);
    }

    /// <summary>Rectangles covering [start, end): one per line, split where large horizontal gaps occur.</summary>
    public IReadOnlyList<RectD> GetRangeRects(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, 0, Length);
        var rects = new List<RectD>();
        if (start >= end) return rects;

        int li = Math.Max(0, FindLine(start));
        for (; li < _lines.Length && _lines[li].Start < end; li++)
        {
            var line = _lines[li];
            int s = Math.Max(start, line.Start), e = Math.Min(end, line.End);
            var current = RectD.Empty;
            for (int i = s; i < e; i++)
            {
                if (!TryGetBox(i, out var b)) continue;
                if (!current.IsEmpty && b.Left - current.Right > Math.Max(b.Height, current.Height) * 3)
                {
                    rects.Add(current);
                    current = b;
                }
                else
                {
                    current = current.Union(b);
                }
            }
            if (!current.IsEmpty) rects.Add(current);
        }
        return rects;
    }

    public string GetText(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, 0, Length);
        return start >= end ? string.Empty : Text[start..end];
    }

    /// <summary>Text whose character centers fall inside any of the rectangles (e.g. under a highlight).</summary>
    public string GetTextInRects(IReadOnlyList<RectD> rects, double tolerance = 0.002)
    {
        if (rects.Count == 0 || Length == 0) return string.Empty;
        var included = new bool[Length];
        for (int i = 0; i < Length; i++)
            if (TryGetBox(i, out var b) && rects.Any(r => r.Contains(b.Center, tolerance)))
                included[i] = true;

        var sb = new StringBuilder();
        int last = -1;
        for (int i = 0; i < Length; i++)
        {
            if (!included[i]) continue;
            if (last >= 0 && i > last + 1)
            {
                // Keep the separator between included runs (space or line break) readable.
                string gap = Text[(last + 1)..i];
                sb.Append(gap.Contains('\n') ? ' ' : gap.Any(char.IsWhiteSpace) ? " " : gap.Length <= 1 ? gap : " ");
            }
            sb.Append(Text[i]);
            last = i;
        }
        return sb.ToString().Trim();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '’';

    private int FindLine(int index)
    {
        int lo = 0, hi = _lines.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_lines[mid].Start <= index)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    private TextLine[] BuildLines()
    {
        var lines = new List<TextLine>();
        int lineStart = 0;
        var bounds = RectD.Empty;
        RectD? prev = null;

        void Close(int end)
        {
            if (end > lineStart || !bounds.IsEmpty) lines.Add(new TextLine(lineStart, end, bounds));
            bounds = RectD.Empty;
            prev = null;
        }

        for (int i = 0; i < Text.Length; i++)
        {
            if (Text[i] == '\n')
            {
                Close(i);
                lineStart = i + 1;
                continue;
            }
            if (!TryGetBox(i, out var box)) continue;
            if (prev is { } p && IsNewVisualLine(p, box))
            {
                Close(i);
                lineStart = i;
            }
            bounds = bounds.Union(box);
            prev = box;
        }
        Close(Text.Length);
        return lines.ToArray();
    }

    /// <summary>
    /// Consecutive characters stay on one line when they overlap vertically (horizontal text) or, for text rotated
    /// by 90°/270°, when they overlap horizontally and are stacked closely.
    /// </summary>
    private static bool IsNewVisualLine(RectD previous, RectD current)
    {
        double vOverlap = Math.Min(previous.Bottom, current.Bottom) - Math.Max(previous.Top, current.Top);
        double minHeight = Math.Min(previous.Height, current.Height);
        if (minHeight > 0 && vOverlap >= minHeight * 0.3) return false;

        double hOverlap = Math.Min(previous.Right, current.Right) - Math.Max(previous.Left, current.Left);
        double minWidth = Math.Min(previous.Width, current.Width);
        double verticalGap = Math.Max(current.Top - previous.Bottom, previous.Top - current.Bottom);
        bool stacked = minWidth > 0 && hOverlap >= minWidth * 0.5 && verticalGap < Math.Max(previous.Height, current.Height);
        return !stacked;
    }
}
