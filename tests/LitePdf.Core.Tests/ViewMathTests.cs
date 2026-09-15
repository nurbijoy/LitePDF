using LitePdf.Core;
using Xunit;

namespace LitePdf.Core.Tests;

public sealed class ViewMathTests
{
    [Fact]
    public void ToDip_StandardPageAt100Percent_CalculatesCorrectly()
    {
        // 72 points @ zoom 1.0 = 96 DIP (1 inch)
        double dip = ViewMath.ToDip(72.0, 1.0);
        Assert.Equal(96.0, dip, precision: 4);

        // Standard US Letter: 612 x 792 pt
        Assert.Equal(816.0, ViewMath.ToDip(612.0, 1.0), precision: 4);
        Assert.Equal(1056.0, ViewMath.ToDip(792.0, 1.0), precision: 4);
    }

    [Fact]
    public void ToPixels_AccountsForDpiScaleAndZoom()
    {
        // 72 points @ 1.0 zoom @ 1.5 DPI scale = 96 * 1.5 = 144 pixels
        int px = ViewMath.ToPixels(72.0, 1.0, 1.5);
        Assert.Equal(144, px);

        // Clamps to at least 1 pixel
        Assert.Equal(1, ViewMath.ToPixels(0.01, 0.1, 0.5));
    }

    [Fact]
    public void ClampZoom_RespectsMinAndMax()
    {
        Assert.Equal(ViewMath.MinZoom, ViewMath.ClampZoom(0.01));
        Assert.Equal(ViewMath.MaxZoom, ViewMath.ClampZoom(10.0));
        Assert.Equal(1.5, ViewMath.ClampZoom(1.5));
    }

    [Fact]
    public void ZoomIn_And_ZoomOut_MultiplyAndDivideByStep()
    {
        double initial = 1.0;
        double zoomedIn = ViewMath.ZoomIn(initial);
        Assert.Equal(1.2, zoomedIn, precision: 4);

        double zoomedOut = ViewMath.ZoomOut(zoomedIn);
        Assert.Equal(1.0, zoomedOut, precision: 4);
    }

    [Fact]
    public void FitWidth_ComputesCorrectZoom()
    {
        // Viewport 864 DIP, Chrome 48 DIP -> 816 DIP available
        // Page width 612 pt = 816 DIP at 100% zoom -> zoom should be 1.0
        double zoom = ViewMath.FitWidth(viewportWidthDip: 864, pageWidthPoints: 612, horizontalChromeDip: 48);
        Assert.Equal(1.0, zoom, precision: 4);
    }

    [Fact]
    public void FitPage_TakesMinimumOfWidthAndHeightZoom()
    {
        var pageSize = new PageSize(612, 792); // 816 x 1056 DIP at 1.0
        // Viewport allows 1.0 width but only 0.5 height
        double zoom = ViewMath.FitPage(
            viewportWidthDip: 816 + 40,
            viewportHeightDip: (1056 * 0.5) + 40,
            page: pageSize,
            horizontalChromeDip: 40,
            verticalChromeDip: 40);

        Assert.Equal(0.5, zoom, precision: 4);
    }

    [Fact]
    public void IsStale_ReturnsTrueOnlyWhenOverTwoPercentDifference()
    {
        Assert.True(ViewMath.IsStale(0, 1000));
        Assert.True(ViewMath.IsStale(-1, 1000));

        // 1.5% difference -> not stale
        Assert.False(ViewMath.IsStale(1015, 1000));
        Assert.False(ViewMath.IsStale(985, 1000));

        // 2.5% difference -> stale
        Assert.True(ViewMath.IsStale(1026, 1000));
        Assert.True(ViewMath.IsStale(974, 1000));
    }

    [Fact]
    public void ClampToMax_ScalesPreservingAspectRatio()
    {
        var (w1, h1) = ViewMath.ClampToMax(2000, 1000, 1000);
        Assert.Equal(1000, w1);
        Assert.Equal(500, h1);

        var (w2, h2) = ViewMath.ClampToMax(500, 800, 1000);
        Assert.Equal(500, w2);
        Assert.Equal(800, h2);
    }
}
