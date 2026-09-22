namespace LitePdf.Core.Export;

/// <summary>
/// Finds tables that draw no lines at all, from the alignment of the words alone.
///
/// This is the one rule in the export that can actively damage a document: deciding that three lines of
/// prose are a grid turns readable text into a mangled table, which is worse than leaving the table as
/// paragraphs. So it is deliberately reluctant, and off unless the export asks for it:
///
/// <list type="bullet">
/// <item>three consecutive lines at least, each cut into the <i>same</i> number of pieces;</item>
/// <item>two columns at least, each separated from the next by a gap several spaces wide on every line;</item>
/// <item>the pieces of a column all start (or all end) at the same place, within a hair;</item>
/// <item>no piece long enough to be a sentence, and no line far below the one above it.</item>
/// </list>
///
/// Two columns of prose never pass: they are split into reading columns long before this runs, and even
/// side by side their words do not line up from one line to the next.
/// </summary>
internal static class UnruledTableBuilder
{
    /// <summary>A gap this many times the line's own character width separates two columns.</summary>
    private const double GapInCharacters = 2.5;

    private const int MinRows = 3;
    private const int MinColumns = 2;

    /// <summary>Column edges may wander this far (page fractions) and still be one column.</summary>
    private const double EdgeTolerance = 0.012;

    /// <summary>A piece longer than this is prose, whatever it lines up with.</summary>
    private const int MaxCellCharacters = 80;

    private const int MaxTablesPerColumn = 6;

    internal readonly record struct Fragment(int Start, int End, RectD Bounds);

    /// <summary>
    /// A line as this detector needs it: where its characters are, so it can be cut into pieces.
    /// </summary>
    internal readonly record struct LineSpan(int Start, int End, RectD Bounds);

    public static List<TableGrid> Find(
        PageContent page, IReadOnlyList<LineSpan> lines, RectD column, double glyphHeight)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(lines);

        var grids = new List<TableGrid>();
        if (lines.Count < MinRows || column.Width <= 0) return grids;

        var split = new List<Fragment[]?>(lines.Count);
        foreach (var line in lines) split.Add(Split(page, line, glyphHeight));

        double pitch = MedianPitch(lines);
        int start = 0;
        while (start < lines.Count)
        {
            if (split[start] is not { Length: >= MinColumns })
            {
                start++;
                continue;
            }

            int end = start + 1;
            while (end < lines.Count &&
                   split[end] is { } next && next.Length == split[start]!.Length &&
                   lines[end].Bounds.Top - lines[end - 1].Bounds.Bottom < Math.Max(pitch, glyphHeight) * 1.6)
                end++;

            if (end - start >= MinRows &&
                Aligned(split, start, end) &&
                BuildGrid(lines, split, start, end) is { } grid)
            {
                grids.Add(grid);
                if (grids.Count >= MaxTablesPerColumn) break;
            }
            start = Math.Max(end, start + 1);
        }

        return grids;
    }

    /// <summary>Cuts a line into the pieces the eye sees, wherever the words leave a wide enough gap.</summary>
    private static Fragment[]? Split(PageContent page, LineSpan line, double glyphHeight)
    {
        var boxes = new List<(int Index, RectD Box)>();
        for (int i = line.Start; i < line.End && i < page.Text.Length; i++)
        {
            if (char.IsWhiteSpace(page.Text.Text[i])) continue;
            if (page.Text.TryGetBox(i, out var box) && !box.IsEmpty) boxes.Add((i, box));
        }
        if (boxes.Count < 2) return null;

        double width = MedianWidth(boxes);
        double threshold = Math.Max(width * GapInCharacters, glyphHeight * 0.55);

        var fragments = new List<Fragment>();
        int first = 0;
        for (int i = 1; i <= boxes.Count; i++)
        {
            bool split = i == boxes.Count || boxes[i].Box.Left - boxes[i - 1].Box.Right > threshold;
            if (!split) continue;

            var bounds = RectD.Empty;
            for (int j = first; j < i; j++) bounds = bounds.Union(boxes[j].Box);
            int characters = boxes[i - 1].Index - boxes[first].Index + 1;
            if (characters > MaxCellCharacters) return null;   // a sentence, not a cell

            fragments.Add(new Fragment(boxes[first].Index, boxes[i - 1].Index + 1, bounds));
            first = i;
        }

        return fragments.Count >= MinColumns ? [.. fragments] : null;
    }

    /// <summary>Every column has to line up down the run, on its left edge or on its right.</summary>
    private static bool Aligned(List<Fragment[]?> split, int start, int end)
    {
        int columns = split[start]!.Length;
        for (int c = 0; c < columns; c++)
        {
            double minLeft = double.MaxValue, maxLeft = double.MinValue;
            double minRight = double.MaxValue, maxRight = double.MinValue;
            for (int r = start; r < end; r++)
            {
                var bounds = split[r]![c].Bounds;
                minLeft = Math.Min(minLeft, bounds.Left);
                maxLeft = Math.Max(maxLeft, bounds.Left);
                minRight = Math.Min(minRight, bounds.Right);
                maxRight = Math.Max(maxRight, bounds.Right);
            }
            if (maxLeft - minLeft > EdgeTolerance && maxRight - minRight > EdgeTolerance) return false;
        }

        // Neighbouring columns must not overlap: a column that runs into the next one is a ragged
        // paragraph whose words happened to fall in the same places.
        for (int c = 1; c < columns; c++)
        {
            double previousRight = double.MinValue, left = double.MaxValue;
            for (int r = start; r < end; r++)
            {
                previousRight = Math.Max(previousRight, split[r]![c - 1].Bounds.Right);
                left = Math.Min(left, split[r]![c].Bounds.Left);
            }
            if (left <= previousRight) return false;
        }
        return true;
    }

    private static TableGrid? BuildGrid(
        IReadOnlyList<LineSpan> lines, List<Fragment[]?> split, int start, int end)
    {
        int columns = split[start]!.Length;
        var bounds = RectD.Empty;
        for (int r = start; r < end; r++)
            foreach (var fragment in split[r]!) bounds = bounds.Union(fragment.Bounds);
        if (bounds.IsEmpty) return null;

        var edges = new double[columns + 1];
        edges[0] = bounds.Left - 0.004;
        edges[columns] = bounds.Right + 0.004;
        for (int c = 1; c < columns; c++)
        {
            double previousRight = double.MinValue, left = double.MaxValue;
            for (int r = start; r < end; r++)
            {
                previousRight = Math.Max(previousRight, split[r]![c - 1].Bounds.Right);
                left = Math.Min(left, split[r]![c].Bounds.Left);
            }
            edges[c] = (previousRight + left) / 2;
        }

        var rows = new double[end - start + 1];
        rows[0] = lines[start].Bounds.Top - 0.002;
        rows[^1] = lines[end - 1].Bounds.Bottom + 0.002;
        for (int r = start + 1; r < end; r++)
            rows[r - start] = (lines[r - 1].Bounds.Bottom + lines[r].Bounds.Top) / 2;

        for (int i = 1; i < edges.Length; i++) if (edges[i] <= edges[i - 1]) return null;
        for (int i = 1; i < rows.Length; i++) if (rows[i] <= rows[i - 1]) return null;

        return new TableGrid(new RectD(edges[0], rows[0], edges[^1], rows[^1]), edges, rows);
    }

    private static double MedianWidth(List<(int Index, RectD Box)> boxes)
    {
        var widths = boxes.Select(b => b.Box.Width).ToList();
        widths.Sort();
        return widths[widths.Count / 2];
    }

    private static double MedianPitch(IReadOnlyList<LineSpan> lines)
    {
        if (lines.Count < 2) return 0;
        var pitches = new List<double>(lines.Count - 1);
        for (int i = 1; i < lines.Count; i++) pitches.Add(lines[i].Bounds.Top - lines[i - 1].Bounds.Top);
        pitches.Sort();
        return pitches[pitches.Count / 2];
    }
}
