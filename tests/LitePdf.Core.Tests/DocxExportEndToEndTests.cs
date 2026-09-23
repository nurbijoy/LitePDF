using System.Text;
using LitePdf.Core.Export;
using LitePdf.Pdfium;
using LitePdf.Samples;

namespace LitePdf.Core.Tests;

/// <summary>
/// The export driven end to end against a real PDF: PDFium reads the page, the composer rebuilds it and the
/// writer packages it. The acceptance test is character conservation — every visible character of the PDF
/// reaches the .docx, in order.
/// </summary>
public sealed class DocxExportEndToEndTests
{
    [Fact]
    public async Task Keeps_a_large_illustration_on_a_visible_text_page()
    {
        var b = new SamplePdfBuilder();
        int catalog = b.Reserve(), pages = b.Reserve();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8",
            [220, 180, 100]);
        int stream = b.AddStream("", Encoding.ASCII.GetBytes(
            "q 540 0 0 700 36 36 cm /Im0 Do Q\nBT /F1 12 Tf 72 750 Td (Illustration caption) Tj ET"));
        int pageId = b.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {stream} 0 R /Resources << /Font << /F1 {font} 0 R >> /XObject << /Im0 {image} 0 R >> >> >>");
        b.Set(pages, $"<< /Type /Pages /Kids [{pageId} 0 R] /Count 1 >>");
        b.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        string path = Samples.TempPath();
        await File.WriteAllBytesAsync(path, b.Build(catalog));
        await using var document = await PdfiumDocument.OpenAsync(path);
        var page = await document.GetPageContentAsync(0, RenderPriority.Background);
        TextPdfExport.Validate([page]);
        Assert.Single(page.Images);
        Assert.Contains("Illustration caption", page.Text.Text);
    }

    [Fact]
    public async Task Text_layout_export_keeps_every_character_and_starts_each_source_page_separately()
    {
        await using var document = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var pages = await ReadAllAsync(document);
        TextPdfExport.Validate(pages);
        var options = ExportOptions.Default with { PreserveLineBreaks = true, PageBreaks = PageBreakMode.EveryPage };
        using var package = DocxPackage.Write(ContentComposer.Compose(pages, options: options));
        package.AssertValid();
        Assert.Equal(Visible(string.Concat(pages.Select(p => p.Text.Text))), Visible(package.AllText()));
        var w = System.Xml.Linq.XNamespace.Get("http://schemas.openxmlformats.org/wordprocessingml/2006/main");
        // Section breaks also begin a new page when the paper size changes.
        int breaks = package.Document.Descendants(w + "pageBreakBefore").Count() +
            package.Document.Descendants(w + "sectPr").Count() - 1;
        Assert.Equal(document.PageCount - 1, breaks);
        Assert.True(package.Document.Descendants(w + "br").Count() > 100);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Distinguishes_sparse_visible_text_from_an_invisible_scan_layer(bool visible)
    {
        var b = new SamplePdfBuilder();
        int catalog = b.Reserve(), pages = b.Reserve();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        string content = "BT /F1 12 Tf 3 Tr 72 650 Td (Hidden OCR words) Tj ET\n" +
            (visible ? "BT /F1 12 Tf 0 Tr 72 700 Td (Hi) Tj ET" : "");
        int stream = b.AddStream("", Encoding.ASCII.GetBytes(content));
        int pageId = b.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {stream} 0 R /Resources << /Font << /F1 {font} 0 R >> >> >>");
        b.Set(pages, $"<< /Type /Pages /Kids [{pageId} 0 R] /Count 1 >>");
        b.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        string path = Samples.TempPath();
        await File.WriteAllBytesAsync(path, b.Build(catalog));
        await using var document = await PdfiumDocument.OpenAsync(path);
        var page = await document.GetPageContentAsync(0, RenderPriority.Background);
        Assert.Equal(!visible, page.IsSearchableScan);
        if (visible)
        {
            Assert.Equal("Hi", page.Text.Text.Trim());
            TextPdfExport.Validate([page]);
        }
        else Assert.Throws<NotSupportedException>(() => TextPdfExport.Validate([page]));
    }

    private static string Visible(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c))]);

    private static async Task<List<PageContent>> ReadAllAsync(PdfiumDocument document)
    {
        var pages = new List<PageContent>(document.PageCount);
        for (int i = 0; i < document.PageCount; i++)
            pages.Add(await document.GetPageContentAsync(i, RenderPriority.Background));
        return pages;
    }

    [Fact]
    public async Task Reads_the_style_of_every_character()
    {
        await using var document = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var page = await document.GetPageContentAsync(0, RenderPriority.Background);

        Assert.NotEmpty(page.Spans);

        // "Chapter 1" is set in 24 pt Helvetica-Bold; the body is 11 pt Helvetica.
        int heading = page.Text.Text.IndexOf("Chapter 1", StringComparison.Ordinal);
        Assert.True(heading >= 0, "the heading should be in the page text");
        var headingStyle = page.StyleAt(heading);
        Assert.Equal("Arial", headingStyle.FontFamily);
        Assert.True(headingStyle.Bold, "Helvetica-Bold should come out bold");
        Assert.Equal(24, headingStyle.SizePoints, 1);

        int body = page.Text.Text.IndexOf("Visit example.com", StringComparison.Ordinal);
        Assert.True(body >= 0);
        var bodyStyle = page.StyleAt(body);
        Assert.Equal("Arial", bodyStyle.FontFamily);
        Assert.False(bodyStyle.Bold);
        Assert.Equal(11, bodyStyle.SizePoints, 1);
        Assert.Equal(0x000000u, bodyStyle.Color);
    }

    [Fact]
    public async Task Page_content_text_matches_the_text_layer()
    {
        await using var document = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        for (int i = 0; i < document.PageCount; i++)
        {
            var expected = await document.GetTextAsync(i, RenderPriority.Background);
            var content = await document.GetPageContentAsync(i, RenderPriority.Background);
            Assert.Equal(expected.Text, content.Text.Text);
        }
    }

    [Fact]
    public async Task Keeps_every_character_of_the_document()
    {
        await using var document = await PdfiumDocument.OpenAsync(Samples.TextPdf());

        var expected = new StringBuilder();
        for (int i = 0; i < document.PageCount; i++)
            expected.Append((await document.GetTextAsync(i, RenderPriority.Background)).Text);

        var pages = await ReadAllAsync(document);
        var outline = await document.GetOutlineAsync();
        var composed = ContentComposer.Compose(pages, null, outline, ExportOptions.Default,
            await document.GetInfoAsync());

        using var package = DocxPackage.Write(composed);
        package.AssertValid();

        Assert.Equal(Visible(expected.ToString()), Visible(package.AllText()));
    }

    [Fact]
    public async Task Turns_the_chapter_openings_into_headings()
    {
        await using var document = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var pages = await ReadAllAsync(document);
        var outline = await document.GetOutlineAsync();

        var composed = ContentComposer.Compose(pages, null, outline);
        using var package = DocxPackage.Write(composed);
        package.AssertValid();

        var headings = package.Paragraphs
            .Where(p => DocxPackage.StyleOf(p)?.StartsWith("Heading", StringComparison.Ordinal) == true)
            .Select(DocxPackage.TextOf)
            .ToList();

        Assert.Equal(SampleDocuments.PageCount, headings.Count);
        Assert.Equal("Chapter 1", headings[0]);
        Assert.Equal("Chapter 6", headings[^1]);
    }

    [Fact]
    public async Task Carries_the_document_title_and_the_page_size()
    {
        await using var document = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var pages = await ReadAllAsync(document);
        var composed = ContentComposer.Compose(pages, null, null, ExportOptions.Default, await document.GetInfoAsync());

        Assert.Equal("LitePDF sample", composed.Title);
        Assert.Equal(11, composed.BodySizePoints);

        using var package = DocxPackage.Write(composed);
        package.AssertValid();
        Assert.Contains("LitePDF sample", package.Xml("docProps/core.xml").ToString());

        // The first section is Letter portrait: 612 x 792 points is 12240 x 15840 twips.
        Assert.Contains("12240", package.Document.ToString());
    }

    [Fact]
    public async Task Turns_a_pdf_link_into_a_word_hyperlink()
    {
        await using var document = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var pages = await ReadAllAsync(document);

        var extras = new List<PageExtras>();
        for (int i = 0; i < document.PageCount; i++)
        {
            var links = await document.GetLinksAsync(i);
            extras.Add(new PageExtras(links, []));
        }

        var composed = ContentComposer.Compose(pages, extras);
        using var package = DocxPackage.Write(composed);
        package.AssertValid();

        Assert.Contains("https://example.com/", package.Xml("word/_rels/document.xml.rels").ToString());
    }

    private static string ScanPath(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i += 3) rgb[i] = 200;   // a flat field, enough to be a picture
        string path = Samples.TempPath();
        File.WriteAllBytes(path, SampleDocuments.CreateImageDocument(rgb, width, height));
        return path;
    }

    [Fact]
    public async Task Lifts_an_image_out_of_a_scanned_page()
    {
        await using var document = await PdfiumDocument.OpenAsync(ScanPath(64, 80));
        var page = await document.GetPageContentAsync(0, RenderPriority.Background);

        var image = Assert.Single(page.Images);
        Assert.NotNull(image.Bits);

        // The image fills the page, so it should be placed across the whole of it.
        Assert.InRange(image.Bounds.Width, 0.98, 1.02);
        Assert.InRange(image.Bounds.Height, 0.98, 1.02);

        var encoder = new TestImageEncoder();
        using var package = DocxPackage.Write(ContentComposer.Compose([page]), encoder);
        package.AssertValid();
        Assert.Equal(1, package.ImageCount);
        Assert.Equal(1, encoder.Calls);
    }

    [Fact]
    public async Task Keeps_the_resolution_of_a_high_resolution_scan()
    {
        // 1700 x 2200 on a Letter page is 200 DPI. Rendered at the size it is placed at it would come back
        // 612 pixels wide - 72 DPI - and the detail would be gone.
        await using var document = await PdfiumDocument.OpenAsync(ScanPath(1700, 2200));
        var page = await document.GetPageContentAsync(0, RenderPriority.Background);

        var image = Assert.Single(page.Images);
        Assert.NotNull(image.Bits);
        Assert.InRange(image.Bits!.Width, 1600, 1800);
        Assert.InRange(image.Bits.Height, 2100, 2300);
    }

    private static async Task<string> FormattedSampleAsync()
    {
        const int Width = 800, Height = 600;
        var plate = new byte[Width * Height * 3];
        for (int i = 0; i < plate.Length; i++) plate[i] = (byte)(i % 251);

        string path = Samples.TempPath();
        await File.WriteAllBytesAsync(path, SampleDocuments.CreateFormattedDocument(plate, Width, Height));
        return path;
    }

    [Fact]
    public async Task Rebuilds_a_formatted_document_with_everything_in_it()
    {
        await using var document = await PdfiumDocument.OpenAsync(await FormattedSampleAsync());
        var pages = await ReadAllAsync(document);
        var composed = ContentComposer.Compose(pages, null, await document.GetOutlineAsync(),
            ExportOptions.Default, await document.GetInfoAsync());

        using var package = DocxPackage.Write(composed);
        package.AssertValid();

        // Title, three section headings and the figure caption's heading: five in all.
        Assert.Equal(5, package.Paragraphs.Count(p => DocxPackage.StyleOf(p)?.StartsWith("Heading", StringComparison.Ordinal) == true));

        // Three bullets and three numbered steps.
        Assert.Equal(6, package.Paragraphs.Count(p => DocxPackage.NumIdOf(p) is not null));

        Assert.Single(package.Tables);
        Assert.Equal(1, package.ImageCount);

        var section = composed.Sections[0];
        Assert.NotNull(section.Header);
        Assert.NotNull(section.Footer);
        Assert.InRange(section.Margins.Left, 70, 75);
        Assert.InRange(section.Margins.Right, 70, 75);
        Assert.Equal("Times New Roman", composed.BodyFont);
        Assert.Equal(11, composed.BodySizePoints);
    }

    [Fact]
    public async Task Joins_the_wrapped_lines_of_a_paragraph_back_together()
    {
        await using var document = await PdfiumDocument.OpenAsync(await FormattedSampleAsync());
        var pages = await ReadAllAsync(document);
        var composed = ContentComposer.Compose(pages, null, await document.GetOutlineAsync());

        var body = composed.Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>()
            .Where(p => p.Style == DocxParagraphStyle.Body && p.List == DocxListKind.None)
            .ToList();

        // Two paragraphs of prose on each of the first two pages, the sentence under the table, and the
        // caption: six in all. The three wrapped lines of the first paragraph are one paragraph again.
        Assert.Equal(6, body.Count);
        Assert.Contains("the effect can be observed at all", body[0].Text);
        Assert.EndsWith("removed from the measurement.", body[0].Text);
        Assert.StartsWith("A second paragraph", body[1].Text);
    }

    [Fact]
    public async Task Keeps_a_picture_at_the_resolution_it_was_stored_at()
    {
        await using var document = await PdfiumDocument.OpenAsync(await FormattedSampleAsync());
        var page = await document.GetPageContentAsync(3, RenderPriority.Background);

        var image = Assert.Single(page.Images);
        Assert.NotNull(image.Bits);

        // 800 px placed across 288 pt is 200 DPI; rendered at its placed size it would be 288 px.
        Assert.InRange(image.Bits!.Width, 760, 840);
    }

    [Fact]
    public async Task Keeps_every_character_of_a_formatted_document()
    {
        await using var document = await PdfiumDocument.OpenAsync(await FormattedSampleAsync());

        var expected = new StringBuilder();
        for (int i = 0; i < document.PageCount; i++)
            expected.Append((await document.GetTextAsync(i, RenderPriority.Background)).Text);

        var pages = await ReadAllAsync(document);
        var composed = ContentComposer.Compose(pages, null, await document.GetOutlineAsync());
        using var package = DocxPackage.Write(composed);
        package.AssertValid();

        // The running head and foot move into the header and footer parts, where Word repeats them, so they
        // appear once there instead of once per page in the body.
        string pdf = Visible(expected.ToString());
        pdf = pdf.Replace("LitePDFformattedsample", string.Empty);
        for (int page = 1; page <= document.PageCount; page++) pdf = pdf.Replace($"Page{page}", string.Empty);

        // A list marker is not lost either: the bullet and the "1." become Word's own numbering, drawn
        // from the numbering definition rather than stored as characters.
        pdf = pdf.Replace("·", string.Empty);
        for (int step = 1; step <= 3; step++) pdf = pdf.Replace($"{step}.Stepnumber{step}", $"Stepnumber{step}");

        Assert.Equal(pdf, Visible(package.AllText()));
    }

    [Fact]
    public async Task Handles_a_rotated_page_without_losing_its_text()
    {
        await using var document = await PdfiumDocument.OpenAsync(Samples.TextPdf());
        var page = await document.GetPageContentAsync(SampleDocuments.RotatedPage, RenderPriority.Background);
        var expected = await document.GetTextAsync(SampleDocuments.RotatedPage, RenderPriority.Background);

        var composed = ContentComposer.Compose([page]);
        using var package = DocxPackage.Write(composed);
        package.AssertValid();

        Assert.Equal(Visible(expected.Text), Visible(package.AllText()));

        // The page is /Rotate 90, so the section is landscape.
        Assert.Equal(new PageSize(792, 612), composed.Sections[0].Size);
    }

    [Fact]
    public async Task Converts_a_thousand_page_document_without_running_away()
    {
        string path = Samples.TempPath();
        await File.WriteAllBytesAsync(path, SampleDocuments.CreateLargeDocument(200));

        await using var document = await PdfiumDocument.OpenAsync(path);
        var pages = await ReadAllAsync(document);
        var composed = ContentComposer.Compose(pages, null, await document.GetOutlineAsync());

        using var package = DocxPackage.Write(composed);
        package.AssertValid();
        Assert.True(package.AllText().Length > 1000);
    }
}
