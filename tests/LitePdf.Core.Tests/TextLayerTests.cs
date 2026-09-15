using LitePdf.Core;
using LitePdf.Core.Text;

namespace LitePdf.Core.Tests;

public class TextLayerTests
{
    [Fact]
    public void GetText_ReturnsJoined()
    {
        var glyphs = new List<TextGlyph>
        {
            new('H', new RectD(0,10,5,0)),
            new('i', new RectD(5,10,7,0)),
        };
        var layer = new PageTextLayer(0, TextSource.Pdf, glyphs);
        Assert.Equal("Hi", layer.GetText());
        Assert.Equal("H", layer.GetText(0,1));
        Assert.Equal("i", layer.GetText(1,1));
    }

    [Fact]
    public void HitTest_FindsGlyph()
    {
        var glyphs = new List<TextGlyph>
        {
            new('A', new RectD(0,10,10,0)),
            new('B', new RectD(10,10,20,0)),
        };
        var layer = new PageTextLayer(0, TextSource.Pdf, glyphs);
        int idx = layer.HitTest(new PointD(5,5), 1);
        Assert.Equal(0, idx);
        int idx2 = layer.HitTest(new PointD(15,5), 1);
        Assert.Equal(1, idx2);
    }

    [Fact]
    public void GetLineRects_Merges()
    {
        var glyphs = new List<TextGlyph>
        {
            new('A', new RectD(0,10,10,0)),
            new('B', new RectD(10,10,20,0)),
            new('\n', new RectD(0,0,0,0)),
            new('C', new RectD(0,5,10,0)),
        };
        var layer = new PageTextLayer(0, TextSource.Pdf, glyphs);
        var rects = layer.GetLineRects(0, 4);
        Assert.Equal(2, rects.Count);
    }

    [Fact]
    public void FromOcr_Converts()
    {
        var words = new List<OcrWord>
        {
            new("Hello", new RectD(0,0,50,10)),
            new("World", new RectD(60,0,110,10)),
        };
        var lines = new List<OcrLine> { new("Hello World", words) };
        var ocr = new OcrPageResult("en-US", null, lines);
        var pageSize = new PageSize(612, 792);
        var layer = PageTextLayer.FromOcr(0, ocr, pageSize, 100, 100);
        Assert.True(layer.Glyphs.Count > 0);
        Assert.Contains(layer.Glyphs, g => g.Char == 'H');
    }
}
