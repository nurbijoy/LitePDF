namespace LitePdf.Core;

/// <summary>Page size in PDF points (1/72 inch), with the page's /Rotate already applied.</summary>
public readonly record struct PageSize(double Width, double Height);

public readonly record struct PointD(double X, double Y);

/// <summary>
/// Rectangle given by its edges. In PDF page space Top &gt; Bottom (origin bottom-left);
/// in pixel space Top &lt; Bottom (origin top-left). Width/Height are always positive.
/// </summary>
public readonly record struct RectD(double Left, double Top, double Right, double Bottom)
{
    public double Width => Math.Abs(Right - Left);
    public double Height => Math.Abs(Top - Bottom);
}
