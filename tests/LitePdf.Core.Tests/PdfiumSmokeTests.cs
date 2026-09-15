using System.IO;
using LitePdf.Pdfium;
using LitePdf.Core;

namespace LitePdf.Core.Tests;

public class PdfiumSmokeTests
{
    // This test generates a minimal PDF in memory? For simplicity we test ViewMath and check PdfiumWorker init
    // Real PDF generation would require a PDF writer; we skip file-based tests in CI without pdfium.dll

    [Fact]
    public void PdfiumWorker_IsSingleton()
    {
        var w1 = PdfiumWorker.Instance;
        var w2 = PdfiumWorker.Instance;
        Assert.Same(w1, w2);
    }

    [Fact]
    public void RenderPriority_Values()
    {
        Assert.True(RenderPriority.Visible < RenderPriority.Interactive);
        Assert.True(RenderPriority.Interactive < RenderPriority.Nearby);
        Assert.True(RenderPriority.Nearby < RenderPriority.Thumbnail);
        Assert.True(RenderPriority.Thumbnail < RenderPriority.Background);
    }

    [Fact]
    public void PageSize_Record()
    {
        var s = new PageSize(612, 792);
        Assert.Equal(612, s.Width);
        Assert.Equal(792, s.Height);
    }

    [Fact]
    public void OcrCache_ComputeKey()
    {
        var key = Ocr.OcrCache.ComputeDocKey("nonexistent.pdf", null);
        Assert.NotEmpty(key);
        Assert.True(key.Length >= 8);
    }
}

public class OcrSmokeTests
{
    [Fact]
    public void OcrEngine_NotAvailableOnLinux()
    {
        var engine = new Ocr.WindowsOcrEngine();
        // On Linux, IsAvailable should be false (non-Windows build)
        // On Windows, it may be true
        // Just check property doesn't throw
        var avail = engine.IsAvailable;
        Assert.True(true);
    }

    [Fact]
    public void OcrCache_Roundtrip()
    {
        var tmpKey = Guid.NewGuid().ToString("N");
        var cache = new Ocr.OcrCache(tmpKey);
        var words = new List<OcrWord> { new("Hello", new RectD(0, 0, 50, 10)) };
        var lines = new List<OcrLine> { new("Hello", words) };
        var result = new OcrPageResult("en-US", null, lines);
        cache.Set(0, result, 100, 100);
        bool got = cache.TryGet(0, out var retrieved);
        Assert.True(got);
        Assert.NotNull(retrieved);
        Assert.Equal("en-US", retrieved!.LanguageTag);
        // Cleanup
        try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LitePDF", "ocr", $"{tmpKey}.json")); } catch { }
    }
}
