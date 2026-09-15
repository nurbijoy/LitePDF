using LitePdf.Core;

namespace LitePdf.Core.Tests;

public class ViewMathTests
{
    [Fact]
    public void ToDip_ConvertsCorrectly()
    {
        // 72 points = 1 inch = 96 DIP
        Assert.Equal(96, ViewMath.ToDip(72, 1.0), 3);
        Assert.Equal(48, ViewMath.ToDip(72, 0.5), 3);
    }

    [Fact]
    public void ToPixels_UsesDpiScale()
    {
        int px = ViewMath.ToPixels(72, 1.0, 1.0);
        Assert.Equal(96, px);
        int px2 = ViewMath.ToPixels(72, 1.0, 1.5);
        Assert.Equal(144, px2);
    }

    [Fact]
    public void ClampZoom_RespectsBounds()
    {
        Assert.Equal(ViewMath.MinZoom, ViewMath.ClampZoom(0.01));
        Assert.Equal(ViewMath.MaxZoom, ViewMath.ClampZoom(100));
        Assert.Equal(1.0, ViewMath.ClampZoom(1.0));
    }

    [Fact]
    public void ZoomInOut_Steps()
    {
        double zoom = 1.0;
        double zoomIn = ViewMath.ZoomIn(zoom);
        Assert.Equal(zoom * ViewMath.ZoomStep, zoomIn, 5);
        double zoomOut = ViewMath.ZoomOut(zoom);
        Assert.Equal(zoom / ViewMath.ZoomStep, zoomOut, 5);
    }

    [Fact]
    public void FitWidth_Calculates()
    {
        double viewport = 800;
        double pageWidthPt = 612; // letter
        double chrome = 24;
        double fit = ViewMath.FitWidth(viewport, pageWidthPt, chrome);
        double expected = (viewport - chrome) / (pageWidthPt * ViewMath.PointsToDip);
        Assert.Equal(expected, fit, 3);
    }

    [Fact]
    public void FitPage_Calculates()
    {
        var page = new PageSize(612, 792);
        double vw = 800, vh = 600;
        double fit = ViewMath.FitPage(vw, vh, page, 24, 24);
        Assert.True(fit > 0);
        Assert.True(fit <= ViewMath.MaxZoom);
    }

    [Fact]
    public void IsStale_Detects()
    {
        Assert.True(ViewMath.IsStale(0, 100));
        Assert.True(ViewMath.IsStale(100, 200)); // 50% diff >2%
        Assert.False(ViewMath.IsStale(100, 101)); // 1% diff
        Assert.False(ViewMath.IsStale(100, 100));
    }

    [Fact]
    public void ClampToMax_Scales()
    {
        var (w, h) = ViewMath.ClampToMax(20000, 10000, 10000);
        Assert.True(w <= 10000 && h <= 10000);
        Assert.Equal(2.0, (double)w / h, 1);
    }
}
