namespace LitePdf.Core.Export;

/// <summary>A reconstructed grid: the boundaries between its columns and rows, in normalized coordinates.</summary>
internal sealed record TableGrid(RectD Bounds, double[] Columns, double[] Rows)
{
    public int ColumnCount => Columns.Length - 1;

    public int RowCount => Rows.Length - 1;

    public RectD Cell(int row, int column) =>
        new(Columns[column], Rows[row], Columns[column + 1], Rows[row + 1]);
}

/// <summary>
/// Rebuilds ruled tables from the hairline rectangles a PDF draws its grid with.
///
/// Only tables that draw their own lines are found. A table held together by alignment alone is left as
/// paragraphs on purpose: detecting those means deciding that two columns of prose are a grid, and getting
/// that wrong turns readable text into a mangled table, which is worse than not finding the table at all.
/// </summary>
internal static class TableBuilder
{
    /// <summary>Rules this close together are the same line drawn twice, or one line with a shadow.</summary>
    private const double SnapFraction = 0.004;

    /// <summary>A boundary counts as drawn when a rule covers this much of the edge it should span.</summary>
    private const double CoverageForEdge = 0.6;

    private const int MaxTablesPerPage = 12;

    public static List<TableGrid> Find(IReadOnlyList<RuleSegment> rules, double glyphHeight)
    {
        var grids = new List<TableGrid>();
        if (rules.Count < 4) return grids;

        double snap = Math.Max(glyphHeight * 0.3, SnapFraction);

        foreach (var group in GroupByProximity(rules, Math.Max(glyphHeight * 1.5, 0.02)))
        {
            if (grids.Count >= MaxTablesPerPage) break;

            var horizontal = group.Where(r => r.IsHorizontal).ToList();
            var vertical = group.Where(r => !r.IsHorizontal).ToList();

            var rows = Cluster(horizontal.Select(r => r.Bounds.Center.Y), snap);
            var columns = Cluster(vertical.Select(r => r.Bounds.Center.X), snap);

            // Two columns and two rows at the very least, or a boxed paragraph reads as a table.
            if (rows.Length < 3 || columns.Length < 3) continue;

            var bounds = new RectD(columns[0], rows[0], columns[^1], rows[^1]);
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            grids.Add(new TableGrid(bounds, columns, rows));
        }

        return grids;
    }

    /// <summary>
    /// True when the grid draws the vertical boundary to the left of <paramref name="column"/> across the
    /// whole of <paramref name="row"/>. Where it does not, the cell to its left runs on into this one.
    /// </summary>
    public static bool HasVerticalEdge(
        TableGrid grid, IReadOnlyList<RuleSegment> rules, int row, int column, double snap)
    {
        double x = grid.Columns[column];
        double top = grid.Rows[row], bottom = grid.Rows[row + 1];
        double needed = (bottom - top) * CoverageForEdge;

        foreach (var rule in rules)
        {
            if (rule.IsHorizontal) continue;
            if (Math.Abs(rule.Bounds.Center.X - x) > snap) continue;
            double covered = Math.Min(rule.Bounds.Bottom, bottom) - Math.Max(rule.Bounds.Top, top);
            if (covered >= needed) return true;
        }
        return false;
    }

    /// <summary>True when the horizontal boundary above <paramref name="row"/> is drawn across the cell.</summary>
    public static bool HasHorizontalEdge(
        TableGrid grid, IReadOnlyList<RuleSegment> rules, int row, int column, double snap)
    {
        double y = grid.Rows[row];
        double left = grid.Columns[column], right = grid.Columns[column + 1];
        double needed = (right - left) * CoverageForEdge;

        foreach (var rule in rules)
        {
            if (!rule.IsHorizontal) continue;
            if (Math.Abs(rule.Bounds.Center.Y - y) > snap) continue;
            double covered = Math.Min(rule.Bounds.Right, right) - Math.Max(rule.Bounds.Left, left);
            if (covered >= needed) return true;
        }
        return false;
    }

    public static double SnapFor(double glyphHeight) => Math.Max(glyphHeight * 0.3, SnapFraction);

    /// <summary>Rules whose boxes nearly touch belong to the same grid; anything further away does not.</summary>
    private static List<List<RuleSegment>> GroupByProximity(IReadOnlyList<RuleSegment> rules, double reach)
    {
        var groups = new List<(RectD Bounds, List<RuleSegment> Members)>();

        foreach (var rule in rules)
        {
            var reachBox = rule.Bounds.Inflate(reach, reach);
            int target = -1;
            for (int i = 0; i < groups.Count; i++)
            {
                if (!groups[i].Bounds.Intersects(reachBox)) continue;
                if (target < 0)
                {
                    target = i;
                    groups[i] = (groups[i].Bounds.Union(rule.Bounds), groups[i].Members);
                    groups[i].Members.Add(rule);
                }
                else
                {
                    // The rule bridges two groups that were being built separately: fold them together.
                    groups[target].Members.AddRange(groups[i].Members);
                    groups[target] = (groups[target].Bounds.Union(groups[i].Bounds), groups[target].Members);
                    groups.RemoveAt(i);
                    i--;
                }
            }
            if (target < 0) groups.Add((rule.Bounds, [rule]));
        }

        return [.. groups.Select(g => g.Members)];
    }

    /// <summary>Collapses near-equal positions into single boundaries, in order.</summary>
    private static double[] Cluster(IEnumerable<double> positions, double snap)
    {
        var sorted = positions.OrderBy(p => p).ToList();
        if (sorted.Count == 0) return [];

        var result = new List<double>();
        double sum = sorted[0];
        int count = 1;

        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - sum / count <= snap)
            {
                sum += sorted[i];
                count++;
                continue;
            }
            result.Add(sum / count);
            sum = sorted[i];
            count = 1;
        }
        result.Add(sum / count);
        return [.. result];
    }
}
