using System.Diagnostics;
using System.Text;
using LitePdf.Ocr;
using LitePdf.Pdfium;
using Xunit.Abstractions;

namespace LitePdf.Core.Tests;

/// <summary>Builds a small two-page PDF with an outline, so tests exercise real PDFium calls.</summary>
internal static class TestPdf
{
    public static string Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LitePdfTests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"two-pages-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Build());
        return path;
    }

    public static byte[] Build()
    {
        static string Stream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 9 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 9 0 R >> >> >>",
            Stream("BT /F1 36 Tf 72 650 Td (Hello LitePDF) Tj ET"),
            Stream("BT /F1 36 Tf 72 650 Td (Second page) Tj ET"),
            "<< /Type /Outlines /First 8 0 R /Last 10 0 R /Count 2 >>",
            "<< /Title (Intro) /Parent 7 0 R /Next 10 0 R /Dest [3 0 R /XYZ 0 792 0] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Title (Chapter Two) /Parent 7 0 R /Prev 8 0 R /Dest [4 0 R /XYZ 0 792 0] >>",
        ];
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

public sealed class PdfiumEndToEndTests
{
    [Fact]
    public async Task Opens_document_and_reads_page_sizes()
    {
        await using var doc = await PdfiumDocument.OpenAsync(TestPdf.Create(), null);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal(612, doc.PageSizes[0].Width, 3);
        Assert.Equal(792, doc.PageSizes[0].Height, 3);
    }

    [Fact]
    public async Task Renders_page_with_dark_text_pixels()
    {
        await using var doc = await PdfiumDocument.OpenAsync(TestPdf.Create(), null);
        var bmp = await doc.RenderPageAsync(0, 612, 792, RenderFlags.Annotations, RenderPriority.Visible);
        Assert.Equal(612, bmp.Width);
        Assert.True(bmp.Pixels.Length >= bmp.Stride * bmp.Height);
        bool hasDark = false;
        for (int i = 0; i + 2 < bmp.Pixels.Length && !hasDark; i += 4)
            hasDark = bmp.Pixels[i] < 100 && bmp.Pixels[i + 1] < 100 && bmp.Pixels[i + 2] < 100;
        Assert.True(hasDark, "Rendered page contains no dark pixels (text did not render).");
    }

    [Fact]
    public async Task Extracts_text_and_text_layer()
    {
        await using var doc = await PdfiumDocument.OpenAsync(TestPdf.Create(), null);
        Assert.Contains("Hello LitePDF", await doc.GetPageTextAsync(0));
        Assert.True(await doc.GetCharCountAsync(0) >= 13);
        var layer = await doc.GetTextLayerAsync(0);
        Assert.NotNull(layer);
        Assert.Contains("Hello", layer!.GetText());
    }

    [Fact]
    public async Task Reads_outline_with_page_targets()
    {
        await using var doc = await PdfiumDocument.OpenAsync(TestPdf.Create(), null);
        var outline = await doc.GetOutlineAsync();
        Assert.Equal(2, outline.Count);
        Assert.Equal("Intro", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
        Assert.Equal("Chapter Two", outline[1].Title);
        Assert.Equal(1, outline[1].PageIndex);
    }

    [Fact]
    public async Task Highlight_survives_save_and_reopen()
    {
        var source = TestPdf.Create();
        var target = Path.ChangeExtension(source, ".highlighted.pdf");
        await using (var doc = await PdfiumDocument.OpenAsync(source, null))
        {
            Assert.True(await doc.AddHighlightAsync(0, [new RectD(72, 690, 330, 640)], 255, 229, 0, "note"));
            Assert.True(await doc.SaveAsync(target, incremental: true));
        }
        await using var reopened = await PdfiumDocument.OpenAsync(target, null);
        var annots = await reopened.GetAnnotationsAsync(0);
        Assert.Single(annots);
    }

    [Fact]
    public async Task Invalid_file_throws_pdfium_exception()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-a-pdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "this is not a pdf");
        var ex = await Assert.ThrowsAsync<PdfiumException>(() => PdfiumDocument.OpenAsync(path, null));
        Assert.Equal(3, ex.ErrorCode);
    }
}

public sealed class OcrEndToEndTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Recognizes_text_rendered_at_300_dpi()
    {
        var engine = new WindowsOcrEngine();
        if (!engine.IsAvailable)
        {
            output.WriteLine("No Windows OCR language installed; skipping.");
            return;
        }

        await using var doc = await PdfiumDocument.OpenAsync(TestPdf.Create(), null);
        var size = doc.PageSizes[0];
        const double scale = 300.0 / 72;
        var (w, h) = ViewMath.ClampToMax((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale), engine.MaxImageDimension);

        var sw = Stopwatch.StartNew();
        var bmp = await doc.RenderPageAsync(0, w, h, RenderFlags.Annotations, RenderPriority.Visible);
        long renderMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var result = await engine.RecognizeAsync(bmp, null);
        output.WriteLine($"Render {w}x{h}: {renderMs} ms; OCR: {sw.ElapsedMilliseconds} ms; language {result.LanguageTag}; text: {result.Text}");

        Assert.Contains("LitePDF", result.Text.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        var hello = result.Lines.SelectMany(l => l.Words).First(x => x.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(hello.PixelRect.Left, 250, 350); // text starts at x = 72 pt ≈ 300 px
    }
}
