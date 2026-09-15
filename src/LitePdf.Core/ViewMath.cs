namespace LitePdf.Core;

/// <summary>Unit conversions and zoom math shared by the UI. See BLUEPRINT §5 and §8.</summary>
public static class ViewMath
{
    public const double PointsToDip = 96.0 / 72.0;
    public const double MinZoom = 0.1;
    public const double MaxZoom = 8.0;
    public const double ZoomStep = 1.2;

    public static double ToDip(double points, double zoom) => points * zoom * PointsToDip;

    public static int ToPixels(double points, double zoom, double dpiScale) =>
        Math.Max(1, (int)Math.Round(points * zoom * PointsToDip * dpiScale));

    public static double ClampZoom(double zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);

    public static double ZoomIn(double zoom) => ClampZoom(zoom * ZoomStep);

    public static double ZoomOut(double zoom) => ClampZoom(zoom / ZoomStep);

    public static double FitWidth(double viewportWidthDip, double pageWidthPoints, double horizontalChromeDip) =>
        ClampZoom((viewportWidthDip - horizontalChromeDip) / (pageWidthPoints * PointsToDip));

    public static double FitPage(double viewportWidthDip, double viewportHeightDip, PageSize page,
        double horizontalChromeDip, double verticalChromeDip) =>
        ClampZoom(Math.Min(
            (viewportWidthDip - horizontalChromeDip) / (page.Width * PointsToDip),
            (viewportHeightDip - verticalChromeDip) / (page.Height * PointsToDip)));

    /// <summary>A bitmap needs re-rendering when missing or more than 2 % off the target width.</summary>
    public static bool IsStale(int currentPixelWidth, int targetPixelWidth) =>
        currentPixelWidth <= 0 || Math.Abs(currentPixelWidth - targetPixelWidth) > targetPixelWidth * 0.02;

    /// <summary>Scales (width, height) down, keeping the aspect ratio, so neither exceeds maxDimension.</summary>
    public static (int Width, int Height) ClampToMax(int width, int height, int maxDimension)
    {
        int largest = Math.Max(width, height);
        if (largest <= maxDimension) return (width, height);
        double scale = (double)maxDimension / largest;
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }
}
