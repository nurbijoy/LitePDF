namespace LitePdf.Core;

/// <summary>Page size in PDF points (1/72 inch) as displayed, i.e. with the page's own /Rotate applied.</summary>
public readonly record struct PageSize(double Width, double Height)
{
    public PageSize Rotated(int quarterTurns) => (quarterTurns & 1) == 1 ? new(Height, Width) : this;
}

public readonly record struct PointD(double X, double Y);

/// <summary>
/// Axis-aligned rectangle with a top-left origin (Top &lt;= Bottom).
/// Page geometry in LitePDF uses <b>normalized page coordinates</b>: 0..1 across the displayed page
/// (page /Rotate applied, crop box honoured), so rotation and zoom never leak into document logic.
/// </summary>
public readonly record struct RectD(double Left, double Top, double Right, double Bottom)
{
    public static RectD Empty => default;

    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PointD Center => new((Left + Right) / 2, (Top + Bottom) / 2);

    public static RectD FromPoints(PointD a, PointD b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public bool Contains(PointD p, double tolerance = 0) =>
        p.X >= Left - tolerance && p.X <= Right + tolerance && p.Y >= Top - tolerance && p.Y <= Bottom + tolerance;

    public bool Intersects(RectD other) =>
        !IsEmpty && !other.IsEmpty && other.Left < Right && other.Right > Left && other.Top < Bottom && other.Bottom > Top;

    public RectD Union(RectD other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public RectD Intersect(RectD other)
    {
        var r = new RectD(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        return r.IsEmpty ? Empty : r;
    }

    public RectD Inflate(double dx, double dy) => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public RectD ClampToUnit() => Intersect(new RectD(0, 0, 1, 1));

    /// <summary>Distance from a point to the rectangle (0 when inside).</summary>
    public double DistanceTo(PointD p)
    {
        double dx = p.X < Left ? Left - p.X : p.X > Right ? p.X - Right : 0;
        double dy = p.Y < Top ? Top - p.Y : p.Y > Bottom ? p.Y - Bottom : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Integer pixel rectangle (top-left origin).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
