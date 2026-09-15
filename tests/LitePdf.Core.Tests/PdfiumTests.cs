using LitePdf.Core.Text;
using LitePdf.Ocr;
using LitePdf.Pdfium;
using LitePdf.Samples;
using Xunit.Abstractions;

namespace LitePdf.Core.Tests;

internal static class Samples
{
    private static readonly string Directory = Path.Combine(Path.GetTempPath(), "LitePdfTests");

    public static string TextPdf()
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, $"text-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SampleDocuments.CreateTextDocument());
        return path;
    }

    public static string TempPath(string extension = ".pdf")
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, $"out-{Guid.NewGuid():N}{extension}");
    }
}

public sealed class PdfiumTests
{
    [Fact]
    public async Task Opens_and_reports_displayed_page_sizes()
    {
        await using var doc = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        Assert.Equal(SampleDocuments.PageCount, doc.PageCount);
        Assert.Equal(new PageSize(612, 792), doc.PageSizes[0]);
        Assert.Equal(new PageSize(792, 612), doc.PageSizes[SampleDocuments.RotatedPage]);
        Assert.Equal(new PageSize(512, 712), doc.PageSizes[SampleDocuments.CroppedPage]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SampleDocuments.RotatedPage)]
    [InlineData(SampleDocuments.CroppedPage)]
    public async Task Normalized_coordinates_match_pdfium_device_mapping(int pageIndex)
    {
        await using var doc = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var size = doc.PageSizes[pageIndex];
        int w = (int)size.Width * 10, h = (int)size.Height * 10;
        foreach (var (x, y) in new[] { (72.0, 720.0), (300.0, 100.0), (550.0, 700.0) })
        {
            var n = await doc.ToNormalizedAsync(pageIndex, x, y);
            var (dx, dy) = await doc.PageToDeviceAsync(pageIndex, x, y, w, h);
            Assert.InRange(n.X * w - dx, -2, 2);
            Assert.InRange(n.Y * h - dy, -2, 2);
        }
    }

    [Fact]
    public async Task Clip_render_matches_full_render()
    {
        await using var doc = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var full = await doc.RenderAsync(0, 612, 792, 0, null, RenderFlags.Annotations, RenderPriority.Visible);
        var clip = await doc.RenderAsync(0, 612, 792, 0, new PixelRect(60, 60, 120, 40), RenderFlags.Annotations, RenderPriority.Visible);
        Assert.Equal((120, 40), (clip.Width, clip.Height));

        int maxDiff = 0, dark = 0;
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 120; x++)
                for (int c = 0; c < 3; c++)
                {
                    byte a = full.Pixels[((60 + y) * 612 + 60 + x) * 4 + c], b = clip.Pixels[(y * 120 + x) * 4 + c];
                    maxDiff = Math.Max(maxDiff, Math.Abs(a - b));
                    if (b < 100) dark++;
                }
        Assert.True(dark > 50, "Clip should contain the chapter heading.");
        Assert.True(maxDiff <= 16, $"Clip differs from full render by {maxDiff}.");
    }

    [Fact]
    public async Task Extracts_text_with_boxes_in_normalized_space()
    {
        await using var doc = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var text = await doc.GetTextAsync(0, RenderPriority.Interactive);
        Assert.Equal(TextSource.Pdf, text.Source);
        int idx = text.Text.IndexOf("Chapter 1", StringComparison.Ordinal);
        Assert.True(idx >= 0, text.Text[..Math.Min(200, text.Length)]);
        Assert.True(text.TryGetBox(idx, out var c));
        Assert.InRange(c.Left, 72 / 612.0 - 0.01, 72 / 612.0 + 0.01);
        Assert.InRange(72 / 792.0, c.Top, c.Bottom + 0.005); // baseline at y=720 lies within the loose box

        var rotated = await doc.GetTextAsync(SampleDocuments.RotatedPage, RenderPriority.Interactive);
        int r = rotated.Text.IndexOf("Chapter 4", StringComparison.Ordinal);
        Assert.True(rotated.TryGetBox(r, out var rc));
        Assert.InRange(rc.Center.X, 0.85, 0.95);   // rotated 90°: top of the page is on the right
        Assert.InRange(rc.Top, 0.10, 0.14);
        Assert.Contains("quick brown fox", rotated.Text);
    }

    [Fact]
    public async Task Reads_outline_links_and_metadata()
    {
        await using var doc = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var outline = await doc.GetOutlineAsync();
        Assert.Equal(SampleDocuments.PageCount, outline.Count);
        Assert.Equal("Chapter 3", outline[2].Title);
        Assert.Equal(2, outline[2].Target.PageIndex);
        Assert.InRange(outline[0].Target.Y!.Value, 52 / 792.0 - 0.01, 52 / 792.0 + 0.01);

        var links = await doc.GetLinksAsync(0);
        Assert.Equal(2, links.Count);
        var uri = Assert.Single(links, l => l.Uri is not null);
        Assert.Equal("https://example.com/", uri.Uri);
        Assert.InRange(uri.Bounds.Left, 0.11, 0.12);
        Assert.Contains(links, l => l.Target.PageIndex == SampleDocuments.PageCount - 1);

        var info = await doc.GetInfoAsync();
        Assert.Equal("LitePDF sample", info.Title);
        Assert.Equal("1.7", info.PdfVersion);
        Assert.Equal(2026, info.Created!.Value.Year);
        Assert.False(info.IsEncrypted);
        Assert.NotNull(await doc.GetFileIdentifierAsync());
    }

    [Fact]
    public async Task Markup_and_notes_round_trip_through_save_on_rotated_page()
    {
        string source = Samples.TextPdf(), saved = Samples.TempPath();
        int page = SampleDocuments.RotatedPage;
        IReadOnlyList<RectD> rects;
        await using (var doc = await PdfiumDocument.OpenAsync(source))
        {
            var text = await doc.GetTextAsync(page, RenderPriority.Interactive);
            int start = text.Text.IndexOf("Chapter 4", StringComparison.Ordinal);
            rects = text.GetRangeRects(start, start + 9);
            await doc.AddMarkupAsync(page, AnnotationKind.Highlight, rects, AnnotationColor.Yellow);
            await doc.AddNoteAsync(page, new PointD(0.5, 0.5), "Remember this", AnnotationColor.Orange);
            await doc.RenderAsync(page, 200, 150, 0, null, RenderFlags.Annotations, RenderPriority.Visible); // generates appearances
            await doc.SaveCopyAsync(saved);
        }

        await using (var doc = await PdfiumDocument.OpenAsync(saved))
        {
            var annots = await doc.GetAnnotationsAsync(page, RenderPriority.Interactive);
            Assert.Equal(2, annots.Count);
            var highlight = Assert.Single(annots, a => a.Kind == AnnotationKind.Highlight);
            var quad = Assert.Single(highlight.Quads);
            Assert.InRange(quad.Left - rects[0].Left, -0.003, 0.003);
            Assert.InRange(quad.Bottom - rects[0].Bottom, -0.003, 0.003);
            Assert.Equal(AnnotationColor.Yellow, highlight.Color);
            var text = await doc.GetTextAsync(page, RenderPriority.Interactive);
            Assert.Equal("Chapter 4", text.GetTextInRects(highlight.Quads));

            var note = Assert.Single(annots, a => a.Kind == AnnotationKind.Note);
            Assert.Equal("Remember this", note.Contents);

            await doc.SetAnnotationColorAsync(page, highlight.Index, AnnotationColor.Green);
            await doc.SetAnnotationContentsAsync(page, note.Index, "Updated");
            await doc.RenderAsync(page, 200, 150, 0, null, RenderFlags.Annotations, RenderPriority.Visible);
            annots = await doc.GetAnnotationsAsync(page, RenderPriority.Interactive);
            Assert.Equal(AnnotationColor.Green, annots.Single(a => a.Kind == AnnotationKind.Highlight).Color);
            Assert.Equal("Updated", annots.Single(a => a.Kind == AnnotationKind.Note).Contents);

            await doc.RemoveAnnotationAsync(page, note.Index);
            Assert.Single(await doc.GetAnnotationsAsync(page, RenderPriority.Interactive));
        }
    }

    [Fact]
    public async Task Refuses_to_overwrite_the_open_file()
    {
        string path = Samples.TextPdf();
        await using var doc = await PdfiumDocument.OpenAsync(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveCopyAsync(path));
    }

    [Fact]
    public async Task Reports_friendly_errors()
    {
        var missing = await Assert.ThrowsAsync<PdfiumException>(() => PdfiumDocument.OpenAsync(Samples.TempPath()));
        Assert.Equal(PdfiumError.File, missing.Error);

        string junk = Samples.TempPath();
        File.WriteAllText(junk, "not a pdf at all");
        var format = await Assert.ThrowsAsync<PdfiumException>(() => PdfiumDocument.OpenAsync(junk));
        Assert.Equal(PdfiumError.Format, format.Error);
    }

    [Fact]
    public async Task Survives_concurrent_and_cancelled_requests_and_disposal()
    {
        var doc = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        using var cts = new CancellationTokenSource();
        var tasks = Enumerable.Range(0, 60).Select(i =>
            (Task)doc.RenderAsync(i % doc.PageCount, 300, 300, i % 4, null, RenderFlags.Annotations, i % 3, i % 2 == 0 ? cts.Token : default)).ToList();
        tasks.Add(doc.GetTextAsync(2, RenderPriority.Background));
        cts.Cancel();

        foreach (var task in tasks)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
        Assert.Contains(tasks, t => t.IsCanceled);
        Assert.Contains(tasks, t => t.IsCompletedSuccessfully);

        await doc.DisposeAsync();
        await doc.DisposeAsync(); // idempotent
        await Assert.ThrowsAsync<ObjectDisposedException>(() => doc.GetTextAsync(0, RenderPriority.Visible));
    }
}

public sealed class OcrTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Scanned_page_is_detected_and_recognized_into_page_text()
    {
        var engine = new WindowsOcrEngine();
        if (!engine.IsAvailable)
        {
            output.WriteLine("No Windows OCR language installed; skipping.");
            return;
        }

        string scanPath = Samples.TempPath();
        await using (var textDoc = await PdfiumDocument.OpenAsync(Samples.TextPdf()))
        {
            int w = (int)(612 * 150 / 72.0), h = (int)(792 * 150 / 72.0);
            var scan = await textDoc.RenderAsync(1, w, h, 0, null, RenderFlags.None, RenderPriority.Interactive);
            File.WriteAllBytes(scanPath, SampleDocuments.CreateImageDocument(SampleDocuments.BgraToRgb(scan.Pixels), w, h));
        }

        await using var doc = await PdfiumDocument.OpenAsync(scanPath);
        Assert.Equal(0, (await doc.GetTextAsync(0, RenderPriority.Interactive)).VisibleCharCount);
        Assert.True(await doc.HasImagesAsync(0));

        var bitmap = await doc.RenderAsync(0, 2550, 3300, 0, null, RenderFlags.None, RenderPriority.Interactive);
        var started = DateTime.UtcNow;
        var result = await engine.RecognizeAsync(bitmap, null);
        output.WriteLine($"OCR {bitmap.Width}x{bitmap.Height}: {(DateTime.UtcNow - started).TotalMilliseconds:0} ms, {result.Lines.Count} lines");

        var text = PageText.FromOcr(0, result);
        var hit = new TextMatcher("quick brown fox").FindAll(text.Text);
        Assert.True(hit.Count >= 10, $"Expected most body lines to be recognized, got {hit.Count}.");
        Assert.Contains("Chapter 2", text.Text);
        Assert.True(text.TryGetBox(text.Text.IndexOf("Chapter 2", StringComparison.Ordinal), out var box));
        Assert.InRange(box.Left, 0.09, 0.14);

        var cache = new OcrCache(Path.Combine(Path.GetTempPath(), "LitePdfTests", "ocr"), Guid.NewGuid().ToString("N"));
        cache.Set(0, result);
        Assert.True(cache.TryGet(0, out var cached));
        Assert.Equal(result.Text, cached.Text);
        Assert.Contains(0, cache.CachedPages);
        cache.Clear();
        Assert.False(cache.Contains(0));
    }
}
