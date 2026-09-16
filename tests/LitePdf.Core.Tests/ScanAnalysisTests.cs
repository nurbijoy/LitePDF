using LitePdf.Core.Imaging;
using LitePdf.Core.Text;

namespace LitePdf.Core.Tests;

/// <summary>Draws pages a pixel at a time so the analysis can be tested without a recognizer or a PDF.</summary>
internal sealed class TestPage(int width, int height)
{
    private readonly byte[] _pixels = Filled(width, height);

    public int Width => width;

    public int Height => height;

    public RenderedBitmap Bitmap => new(width, height, _pixels);

    public TestPage Fill(int x0, int y0, int w, int h, byte value = 0, byte red = 255, byte green = 255)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(height, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(width, x0 + w); x++)
            {
                int o = (y * width + x) * 4;
                _pixels[o] = value;
                _pixels[o + 1] = (byte)(green == 255 ? value : green);
                _pixels[o + 2] = (byte)(red == 255 ? value : red);
            }
        return this;
    }

    /// <summary>A blob standing in for a glyph: solid, so it survives thresholding.</summary>
    public TestPage Glyph(int x, int y, int w, int h) => Fill(x, y, w, h);

    /// <summary>A rectangle outline, which reads as a drawing rather than as type.</summary>
    public TestPage Outline(int x, int y, int w, int h, int thickness = 3)
    {
        Fill(x, y, w, thickness);
        Fill(x, y + h - thickness, w, thickness);
        Fill(x, y, thickness, h);
        Fill(x + w - thickness, y, thickness, h);
        return this;
    }

    private static byte[] Filled(int w, int h)
    {
        var pixels = new byte[w * h * 4];
        Array.Fill(pixels, (byte)255);
        return pixels;
    }
}

public sealed class ScanPreprocessorTests
{
    [Fact]
    public void Coloured_pen_becomes_paper_while_black_print_stays()
    {
        var page = new TestPage(200, 200);
        page.Glyph(20, 20, 12, 20);                                     // black print
        page.Fill(100, 20, 40, 20, value: 90, red: 235, green: 90);      // pink pen stroke

        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });

        Assert.Equal(1, scan.Ink[30 * 200 + 25]);
        Assert.Equal(0, scan.Ink[30 * 200 + 120]);
    }

    [Fact]
    public void Faint_show_through_is_cleared_but_print_survives()
    {
        var page = new TestPage(200, 200);
        page.Glyph(20, 20, 12, 20);
        page.Fill(100, 20, 30, 20, value: 205); // ghost of the reverse side

        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });

        Assert.Equal(1, scan.Ink[30 * 200 + 25]);
        Assert.Equal(0, scan.Ink[30 * 200 + 110]);
    }

    [Fact]
    public void Uneven_lighting_does_not_swallow_print()
    {
        var page = new TestPage(300, 200);
        // Paper shading from white to mid grey across the page, with print on the dark side.
        for (int x = 0; x < 300; x++) page.Fill(x, 0, 1, 200, value: (byte)(255 - x / 3));
        page.Glyph(250, 80, 14, 24);

        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });

        Assert.Equal(1, scan.Ink[90 * 300 + 255]);
        Assert.Equal(0, scan.Ink[90 * 300 + 200]);
    }

    [Fact]
    public void A_level_page_is_left_unrotated()
    {
        var page = new TestPage(600, 400);
        for (int row = 0; row < 8; row++)
            for (int i = 0; i < 20; i++)
                page.Glyph(60 + i * 24, 40 + row * 40, 14, 20);

        Assert.Equal(0, ScanPreprocessor.Prepare(page.Bitmap).SkewDegrees);
    }

    [Fact]
    public void Skew_is_measured_and_taken_out()
    {
        var page = new TestPage(600, 400);
        for (int row = 0; row < 8; row++)
            for (int i = 0; i < 20; i++)
            {
                int x = 60 + i * 24;
                page.Glyph(x, 40 + row * 40 + (int)Math.Round((x - 300) * 0.035), 14, 20); // ~2 degrees
            }

        var scan = ScanPreprocessor.Prepare(page.Bitmap);

        Assert.InRange(scan.SkewDegrees, 1.5, 2.5);
        Assert.True(scan.IsDeskewed);
    }

    [Fact]
    public void Deskewed_geometry_maps_back_onto_the_source()
    {
        var page = new TestPage(600, 400);
        for (int row = 0; row < 8; row++)
            for (int i = 0; i < 20; i++)
            {
                int x = 60 + i * 24;
                page.Glyph(x, 40 + row * 40 + (int)Math.Round((x - 300) * 0.035), 14, 20);
            }

        var scan = ScanPreprocessor.Prepare(page.Bitmap);
        var center = scan.ToSource(new PointD(0.5, 0.5));

        // Rotation is about the middle of the page, so the middle is the one point that does not move.
        Assert.Equal(0.5, center.X, 3);
        Assert.Equal(0.5, center.Y, 3);
        // A point away from the middle does move, and a rectangle grows to cover where it went.
        var mapped = scan.ToSource(new RectD(0.1, 0.1, 0.2, 0.2));
        Assert.True(mapped.Height > 0.1);
    }
}

public sealed class ConnectedComponentsTests
{
    [Fact]
    public void Separate_marks_are_separate_blobs()
    {
        var page = new TestPage(100, 100).Glyph(10, 10, 8, 12).Glyph(40, 10, 8, 12).Glyph(70, 60, 20, 4);
        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });

        var blobs = ConnectedComponents.Find(scan.Ink, scan.Width, scan.Height);

        Assert.Equal(3, blobs.Count);
        Assert.Contains(blobs, b => b.Width == 20 && b.Height == 4);
    }

    [Fact]
    public void Touching_marks_join_and_the_box_covers_both()
    {
        var page = new TestPage(100, 100).Glyph(10, 10, 10, 10).Glyph(20, 10, 10, 10);
        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });

        var blobs = ConnectedComponents.Find(scan.Ink, scan.Width, scan.Height);

        Assert.Single(blobs);
        Assert.Equal(10, blobs[0].Left);
        Assert.Equal(29, blobs[0].Right);
    }

    [Fact]
    public void Marks_meeting_only_at_a_corner_still_join()
    {
        var page = new TestPage(60, 60).Glyph(10, 10, 6, 6).Glyph(16, 16, 6, 6);
        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });

        Assert.Single(ConnectedComponents.Find(scan.Ink, scan.Width, scan.Height));
    }

    [Fact]
    public void Density_tells_a_solid_bar_from_an_outline()
    {
        var page = new TestPage(200, 200).Fill(10, 10, 60, 6).Outline(100, 40, 60, 60);
        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });

        var blobs = ConnectedComponents.Find(scan.Ink, scan.Width, scan.Height);

        Assert.True(blobs.Single(b => b.Left < 80).Density > 0.9);
        Assert.True(blobs.Single(b => b.Left >= 80).Density < 0.5);
    }
}
