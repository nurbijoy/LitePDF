namespace LitePdf.Core;

/// <summary>Unit conversions and zoom math shared by the UI.</summary>
public static class ViewMath
{
    public const double PointsToDip = 96.0 / 72.0;
    public const double MinZoom = 0.1;
    public const double MaxZoom = 16.0;

    private static readonly double[] ZoomStops =
        [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0, 6.0, 8.0, 10.0, 12.0, 16.0];

    public static double ClampZoom(double zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);

    public static int ToPixels(double dip, double dpiScale) => Math.Max(1, (int)Math.Round(dip * dpiScale));

    /// <summary>Next preset zoom above the current value.</summary>
    public static double ZoomIn(double zoom)
    {
        foreach (var stop in ZoomStops)
            if (stop > zoom * 1.001) return stop;
        return MaxZoom;
    }

    /// <summary>Next preset zoom below the current value.</summary>
    public static double ZoomOut(double zoom)
    {
        for (int i = ZoomStops.Length - 1; i >= 0; i--)
            if (ZoomStops[i] < zoom * 0.999) return ZoomStops[i];
        return MinZoom;
    }

    public static double FitWidth(double availableWidthDip, double contentWidthPoints) =>
        ClampZoom(availableWidthDip / (contentWidthPoints * PointsToDip));

    public static double FitPage(double availableWidthDip, double availableHeightDip, double contentWidthPoints, double contentHeightPoints) =>
        ClampZoom(Math.Min(availableWidthDip / (contentWidthPoints * PointsToDip), availableHeightDip / (contentHeightPoints * PointsToDip)));

    /// <summary>A bitmap needs re-rendering when missing or more than 3 % off the target width.</summary>
    public static bool IsStale(int currentPixelWidth, int targetPixelWidth) =>
        currentPixelWidth <= 0 || Math.Abs(currentPixelWidth - targetPixelWidth) > targetPixelWidth * 0.03;

    /// <summary>Scales (width, height) down, keeping aspect ratio, so neither exceeds maxDimension.</summary>
    public static (int Width, int Height) ClampToMax(int width, int height, int maxDimension)
    {
        int largest = Math.Max(width, height);
        if (largest <= maxDimension) return (width, height);
        double scale = (double)maxDimension / largest;
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

    /// <summary>Scales (width, height) down, keeping aspect ratio, so width × height ≤ maxPixels.</summary>
    public static (int Width, int Height) ClampToArea(int width, int height, long maxPixels)
    {
        long area = (long)width * height;
        if (area <= maxPixels) return (width, height);
        double scale = Math.Sqrt((double)maxPixels / area);
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }
}
