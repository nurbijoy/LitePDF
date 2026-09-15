namespace LitePdf.Core.Layout;

public enum PageLayoutMode
{
    SinglePage,
    TwoPage,
}

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>
/// Positions of all pages in a continuous vertical layout, in DIPs. Pure math, so it is unit tested and
/// lets the viewer virtualize exactly (no estimated item sizes).
/// </summary>
public sealed class DocumentLayout
{
    public const double DefaultGap = 12;
    public const double DefaultMargin = 16;

    private readonly LayoutRect[] _pages;
    private readonly double[] _rowTops;
    private readonly double[] _rowBottoms;
    private readonly int[] _rowFirstPage;

    public DocumentLayout(IReadOnlyList<PageSize> pages, double zoom, int rotation, PageLayoutMode mode,
        double gap = DefaultGap, double margin = DefaultMargin)
    {
        Zoom = zoom;
        Rotation = rotation & 3;
        Mode = mode;
        Gap = gap;
        Margin = margin;
        PagesPerRow = mode == PageLayoutMode.TwoPage ? 2 : 1;

        int count = pages.Count;
        int rows = (count + PagesPerRow - 1) / PagesPerRow;
        _pages = new LayoutRect[count];
        _rowTops = new double[rows];
        _rowBottoms = new double[rows];
        _rowFirstPage = new int[rows];

        double scale = zoom * ViewMath.PointsToDip;
        var sizes = pages.Select(p => p.Rotated(Rotation)).Select(p => (W: p.Width * scale, H: p.Height * scale)).ToArray();

        double maxRowWidth = 0;
        for (int r = 0; r < rows; r++)
        {
            int first = r * PagesPerRow, last = Math.Min(count, first + PagesPerRow);
            double rowWidth = 0;
            for (int i = first; i < last; i++) rowWidth += sizes[i].W;
            rowWidth += (last - first - 1) * gap;
            maxRowWidth = Math.Max(maxRowWidth, rowWidth);
        }
        Width = maxRowWidth + 2 * margin;

        double y = margin;
        for (int r = 0; r < rows; r++)
        {
            int first = r * PagesPerRow, last = Math.Min(count, first + PagesPerRow);
            double rowWidth = 0, rowHeight = 0;
            for (int i = first; i < last; i++)
            {
                rowWidth += sizes[i].W;
                rowHeight = Math.Max(rowHeight, sizes[i].H);
            }
            rowWidth += (last - first - 1) * gap;

            double x = (Width - rowWidth) / 2;
            for (int i = first; i < last; i++)
            {
                _pages[i] = new LayoutRect(x, y + (rowHeight - sizes[i].H) / 2, sizes[i].W, sizes[i].H);
                x += sizes[i].W + gap;
            }
            _rowTops[r] = y;
            _rowBottoms[r] = y + rowHeight;
            _rowFirstPage[r] = first;
            y += rowHeight + gap;
        }
        Height = rows == 0 ? 2 * margin : y - gap + margin;
    }

    public double Zoom { get; }
    public int Rotation { get; }
    public PageLayoutMode Mode { get; }
    public double Gap { get; }
    public double Margin { get; }
    public int PagesPerRow { get; }
    public int PageCount => _pages.Length;
    public double Width { get; }
    public double Height { get; }

    public LayoutRect GetPageRect(int pageIndex) => _pages[pageIndex];

    public int GetRow(int pageIndex) => pageIndex / PagesPerRow;

    /// <summary>First and last page (inclusive) whose rows intersect [top, bottom]; (-1, -1) if none.</summary>
    public (int First, int Last) GetPagesInRange(double top, double bottom)
    {
        if (_pages.Length == 0 || bottom < top) return (-1, -1);
        int firstRow = FirstRowEndingAfter(top);
        if (firstRow >= _rowTops.Length || _rowTops[firstRow] > bottom) return (-1, -1);
        int lastRow = firstRow;
        while (lastRow + 1 < _rowTops.Length && _rowTops[lastRow + 1] <= bottom) lastRow++;
        int lastPage = Math.Min(_pages.Length - 1, _rowFirstPage[lastRow] + PagesPerRow - 1);
        return (_rowFirstPage[firstRow], lastPage);
    }

    /// <summary>The first page of the row at vertical offset y (nearest row when y is in a gap or margin).</summary>
    public int GetPageAtOffset(double y)
    {
        if (_pages.Length == 0) return -1;
        int row = Math.Min(FirstRowEndingAfter(y), _rowTops.Length - 1);
        return _rowFirstPage[row];
    }

    /// <summary>Page containing the point, or -1.</summary>
    public int HitTest(double x, double y)
    {
        var (first, last) = GetPagesInRange(y, y);
        for (int i = Math.Max(first, 0); first >= 0 && i <= last; i++)
            if (_pages[i].Contains(x, y)) return i;
        return -1;
    }

    /// <summary>Page nearest to the point (never -1 unless there are no pages).</summary>
    public int GetNearestPage(double x, double y)
    {
        if (_pages.Length == 0) return -1;
        int row = Math.Min(FirstRowEndingAfter(y), _rowTops.Length - 1);
        if (row > 0 && y < _rowTops[row] && _rowTops[row] - y > y - _rowBottoms[row - 1]) row--;
        int first = _rowFirstPage[row], last = Math.Min(_pages.Length - 1, first + PagesPerRow - 1);
        int best = first;
        double bestDist = double.MaxValue;
        for (int i = first; i <= last; i++)
        {
            var r = _pages[i];
            double dx = x < r.X ? r.X - x : x > r.Right ? x - r.Right : 0;
            if (dx < bestDist)
            {
                bestDist = dx;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Width and height in points of the row containing a page (for fit-width / fit-page), excluding gaps.</summary>
    public static (double Width, double Height, int Gaps) GetRowSizePoints(IReadOnlyList<PageSize> pages, int pageIndex, int rotation, PageLayoutMode mode)
    {
        int perRow = mode == PageLayoutMode.TwoPage ? 2 : 1;
        int first = pageIndex / perRow * perRow, last = Math.Min(pages.Count, first + perRow);
        double w = 0, h = 0;
        for (int i = first; i < last; i++)
        {
            var s = pages[i].Rotated(rotation);
            w += s.Width;
            h = Math.Max(h, s.Height);
        }
        return (w, h, last - first - 1);
    }

    /// <summary>Width in points of the widest row, excluding gaps.</summary>
    public static (double Width, int Gaps) GetWidestRowPoints(IReadOnlyList<PageSize> pages, int rotation, PageLayoutMode mode)
    {
        int perRow = mode == PageLayoutMode.TwoPage ? 2 : 1;
        double best = 0;
        int gaps = 0;
        for (int first = 0; first < pages.Count; first += perRow)
        {
            var (w, _, g) = GetRowSizePoints(pages, first, rotation, mode);
            if (w > best)
            {
                best = w;
                gaps = g;
            }
        }
        return (best, gaps);
    }

    private int FirstRowEndingAfter(double y)
    {
        int lo = 0, hi = _rowBottoms.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_rowBottoms[mid] < y) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

/// <summary>Maps normalized page coordinates to normalized view coordinates for extra view rotation (quarter turns clockwise).</summary>
public static class ViewTransform
{
    public static PointD ToView(PointD p, int rotation) => (rotation & 3) switch
    {
        1 => new PointD(1 - p.Y, p.X),
        2 => new PointD(1 - p.X, 1 - p.Y),
        3 => new PointD(p.Y, 1 - p.X),
        _ => p,
    };

    public static PointD FromView(PointD v, int rotation) => (rotation & 3) switch
    {
        1 => new PointD(v.Y, 1 - v.X),
        2 => new PointD(1 - v.X, 1 - v.Y),
        3 => new PointD(1 - v.Y, v.X),
        _ => v,
    };

    public static RectD ToView(RectD r, int rotation) =>
        RectD.FromPoints(ToView(new PointD(r.Left, r.Top), rotation), ToView(new PointD(r.Right, r.Bottom), rotation));

    public static RectD FromView(RectD r, int rotation) =>
        RectD.FromPoints(FromView(new PointD(r.Left, r.Top), rotation), FromView(new PointD(r.Right, r.Bottom), rotation));
}
