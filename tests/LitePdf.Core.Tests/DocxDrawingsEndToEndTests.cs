using System.Text;
using LitePdf.Core.Export;
using LitePdf.Pdfium;
using LitePdf.Samples;

namespace LitePdf.Core.Tests;

/// <summary>
/// The parts of a real page PDFium has to be asked for by hand: vector artwork, a picture inside a form
/// XObject, the fill behind a table cell, a structure tree, and the font programs themselves.
/// </summary>
public sealed class DocxDrawingsEndToEndTests
{
    private static string Visible(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c))]);

    private static byte[] Plate(int width, int height)
    {
        var plate = new byte[width * height * 3];
        for (int i = 0; i < plate.Length; i++) plate[i] = (byte)(i % 251);
        return plate;
    }

    private static async Task<string> DrawingSampleAsync()
    {
        string path = Samples.TempPath();
        await File.WriteAllBytesAsync(path, SampleDocuments.CreateDrawingDocument(Plate(200, 150), 200, 150));
        return path;
    }

    private static async Task<string> TaggedSampleAsync()
    {
        string path = Samples.TempPath();
        await File.WriteAllBytesAsync(path, SampleDocuments.CreateTaggedDocument(Plate(200, 150), 200, 150));
        return path;
    }

    private static async Task<(List<PageContent> Pages, List<PageExtras> Extras)> ReadAllAsync(PdfiumDocument document)
    {
        var pages = new List<PageContent>(document.PageCount);
        var extras = new List<PageExtras>(document.PageCount);
        for (int i = 0; i < document.PageCount; i++)
        {
            pages.Add(await document.GetPageContentAsync(i, RenderPriority.Background));
            extras.Add(new PageExtras([], await document.GetAnnotationsAsync(i, RenderPriority.Background)));
        }
        return (pages, extras);
    }

    // ---- vector artwork ----

    [Fact]
    public async Task Rasterizes_a_chart_that_no_image_object_holds()
    {
        await using var document = await PdfiumDocument.OpenAsync(await DrawingSampleAsync());
        var page = await document.GetPageContentAsync(0, RenderPriority.Background);

        var drawing = Assert.Single(page.Images, image => image.IsDrawing);
        Assert.NotNull(drawing.Bits);

        // The five bars, the axes and the curve, at 300 DPI and no lower.
        Assert.InRange(drawing.Bounds.Left, 0.09, 0.14);
        Assert.InRange(drawing.Bounds.Right, 0.6, 0.75);
        Assert.True(drawing.Bits!.Width > 1000, $"rasterized at only {drawing.Bits.Width} px across");

        // Transparent, so the drawing does not arrive on a white card that hides the page behind it.
        int clear = 0;
        for (int i = 3; i < drawing.Bits.Bgra.Length; i += 4) if (drawing.Bits.Bgra[i] == 0) clear++;
        Assert.True(clear > drawing.Bits.Width * drawing.Bits.Height / 10, "the background should be transparent");
    }

    [Fact]
    public async Task Leaves_the_text_out_of_a_drawing_it_rasterizes()
    {
        // A pale triangle with black type across it. The words are exported as text, so if they were also
        // rendered into the picture every one of them would appear twice.
        var b = new SamplePdfBuilder();
        int catalog = b.Reserve(), pages = b.Reserve(), page = b.Reserve();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var content = new StringBuilder();
        content.Append("0.85 0.9 0.95 rg\n100 400 m 400 400 l 250 650 l h f\n");
        content.Append("0 0 0 rg\nBT /F1 28 Tf 150 500 Td (Overlaid) Tj ET\n");
        int stream = b.AddStream("", Encoding.Latin1.GetBytes(content.ToString()));
        b.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {stream} 0 R " +
                    $"/Resources << /Font << /F1 {font} 0 R >> >> >>");
        b.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        b.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");

        string path = Samples.TempPath();
        await File.WriteAllBytesAsync(path, b.Build(catalog));

        await using var document = await PdfiumDocument.OpenAsync(path);
        var content0 = await document.GetPageContentAsync(0, RenderPriority.Background);

        var drawing = Assert.Single(content0.Images, image => image.IsDrawing);
        var bits = drawing.Bits!;

        int dark = 0;
        for (int i = 0; i < bits.Bgra.Length; i += 4)
        {
            if (bits.Bgra[i + 3] < 200) continue;
            if (bits.Bgra[i] < 80 && bits.Bgra[i + 1] < 80 && bits.Bgra[i + 2] < 80) dark++;
        }
        Assert.True(dark < 200, $"{dark} black pixels: the text was rendered into the drawing");

        // And the words are still in the document, as words.
        using var package = DocxPackage.Write(ContentComposer.Compose([content0]));
        package.AssertValid();
        Assert.Contains("Overlaid", package.AllText());
    }

    [Fact]
    public async Task Keeps_drawings_out_when_the_export_does_not_want_them()
    {
        await using var document = await PdfiumDocument.OpenAsync(await DrawingSampleAsync());
        var page = await document.GetPageContentAsync(0, RenderPriority.Background,
            new PageContentRequest { Drawings = false });

        Assert.DoesNotContain(page.Images, image => image.IsDrawing);
    }

    // ---- form XObjects ----

    [Fact]
    public async Task Finds_the_picture_inside_a_form_xobject()
    {
        await using var document = await PdfiumDocument.OpenAsync(await DrawingSampleAsync());
        var page = await document.GetPageContentAsync(1, RenderPriority.Background);

        var picture = Assert.Single(page.Images, image => !image.IsDrawing);
        Assert.NotNull(picture.Bits);

        // Placed 240 x 180 pt through the form's own matrix, a third of the way across a Letter page.
        Assert.InRange(picture.Bounds.Width * page.Size.Width, 230, 250);
        Assert.InRange(picture.Bounds.Height * page.Size.Height, 170, 190);
        Assert.InRange(picture.Bounds.Left * page.Size.Width, 66, 78);
        Assert.InRange(picture.Bits!.Width, 180, 260);
    }

    // ---- tables ----

    [Fact]
    public async Task Rebuilds_a_table_drawn_with_stroked_lines_and_a_shaded_header()
    {
        await using var document = await PdfiumDocument.OpenAsync(await DrawingSampleAsync());
        var page = await document.GetPageContentAsync(1, RenderPriority.Background);

        // Eight hairlines: four rules across and four down, none of them a filled rectangle.
        Assert.Equal(8, page.Rules.Count);
        Assert.Single(page.Fills);

        var table = Assert.Single(ContentComposer.Compose([page]).AllBlocks.OfType<DocxTable>());
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.HasBorders);
        Assert.All(table.Rows[0].Cells, cell => Assert.Equal(0xD9DEEBu, cell.Shading));
        Assert.All(table.Rows[1].Cells, cell => Assert.Null(cell.Shading));
        Assert.Equal("Q1", CellText(table, 1, 0));
    }

    [Fact]
    public async Task Finds_the_table_with_no_lines_only_when_it_is_asked_to()
    {
        await using var document = await PdfiumDocument.OpenAsync(await DrawingSampleAsync());
        var page = await document.GetPageContentAsync(1, RenderPriority.Background);

        Assert.Single(ContentComposer.Compose([page]).AllBlocks.OfType<DocxTable>());

        var both = ContentComposer.Compose([page], null, null, ExportOptions.Default with { UnruledTables = true })
            .AllBlocks.OfType<DocxTable>().ToList();
        Assert.Equal(2, both.Count);

        var plain = both[1];
        Assert.False(plain.HasBorders);
        Assert.Equal(4, plain.Rows.Count);
        Assert.Equal("Year", CellText(plain, 3, 0));
        Assert.Equal("+12%", CellText(plain, 2, 2));
    }

    // ---- notes ----

    [Fact]
    public async Task Turns_the_sticky_note_of_a_real_document_into_a_comment()
    {
        await using var document = await PdfiumDocument.OpenAsync(await DrawingSampleAsync());
        var (pages, extras) = await ReadAllAsync(document);

        var composed = ContentComposer.Compose(pages, extras);
        var comment = Assert.Single(composed.Comments);
        Assert.Equal("Reviewer", comment.Author);
        Assert.Equal("The figures for Q2 need a citation.", comment.Text);

        using var package = DocxPackage.Write(composed);
        package.AssertValid();
        Assert.Contains("The figures for Q2 need a citation.", package.Xml("word/comments.xml").ToString());
    }

    [Fact]
    public async Task Keeps_every_character_of_the_drawings_document()
    {
        await using var document = await PdfiumDocument.OpenAsync(await DrawingSampleAsync());

        var expected = new StringBuilder();
        for (int i = 0; i < document.PageCount; i++)
            expected.Append((await document.GetTextAsync(i, RenderPriority.Background)).Text);

        var (pages, extras) = await ReadAllAsync(document);
        var composed = ContentComposer.Compose(pages, extras, null, ExportOptions.Default with { UnruledTables = true });

        using var package = DocxPackage.Write(composed);
        package.AssertValid();
        Assert.Equal(Visible(expected.ToString()), Visible(package.AllText()));
    }

    // ---- tagged documents ----

    [Fact]
    public async Task Reads_the_structure_tree_of_a_tagged_document()
    {
        await using var document = await PdfiumDocument.OpenAsync(await TaggedSampleAsync());
        var page = await document.GetPageContentAsync(0, RenderPriority.Background);

        var root = Assert.Single(page.Tags);
        Assert.Equal("Document", root.Type);
        Assert.Equal(5, root.Children.Count);
        // Fifteen marked-content ids carry text; the sixteenth is the figure, which draws a picture.
        Assert.Equal(15, page.Marks.Select(m => m.MarkedContentId).Distinct().Count());
        Assert.Equal(15, Assert.Single(page.Images).MarkedContentId);

        var figure = Assert.Single(root.Descendants(), tag => tag.Type == "Figure");
        Assert.Equal("A plate of graded colour", figure.AltText);
        Assert.Equal(9, root.Descendants().Count(tag => tag.Type is "TD" or "TH"));
    }

    [Fact]
    public async Task Rebuilds_a_tagged_document_from_what_it_says_about_itself()
    {
        await using var document = await PdfiumDocument.OpenAsync(await TaggedSampleAsync());
        var page = await document.GetPageContentAsync(0, RenderPriority.Background);
        var composed = ContentComposer.Compose([page], null, null, ExportOptions.Default,
            await document.GetInfoAsync());

        var paragraphs = composed.AllBlocks.OfType<DocxParagraph>().ToList();
        Assert.Equal(DocxParagraphStyle.Heading1, paragraphs[0].Style);
        Assert.Equal("A Tagged Document", paragraphs[0].Text);

        // Two lines of one tagged element are one paragraph again, wherever the geometry would have split it.
        Assert.Contains(paragraphs, p => p.Text.StartsWith("This paragraph is marked", StringComparison.Ordinal) &&
                                         p.Text.EndsWith("same structure element.", StringComparison.Ordinal));

        var items = paragraphs.Where(p => p.List != DocxListKind.None).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("The first item of a tagged list", items[0].Text);

        // A table that draws not one line, rebuilt because the document says it is a table.
        var table = Assert.Single(composed.AllBlocks.OfType<DocxTable>());
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.HasBorders);
        Assert.Equal("1,204", CellText(table, 1, 1));

        var picture = Assert.Single(composed.AllBlocks.OfType<DocxPicture>());
        Assert.Equal("A plate of graded colour", picture.AltText);

        using var package = DocxPackage.Write(composed);
        package.AssertValid();
    }

    [Fact]
    public async Task Keeps_every_character_of_a_tagged_document()
    {
        await using var document = await PdfiumDocument.OpenAsync(await TaggedSampleAsync());
        var expected = (await document.GetTextAsync(0, RenderPriority.Background)).Text;

        var page = await document.GetPageContentAsync(0, RenderPriority.Background);
        var composed = ContentComposer.Compose([page]);

        using var package = DocxPackage.Write(composed);
        package.AssertValid();

        // The list labels become Word's own numbering rather than characters of the text.
        string pdf = Visible(expected).Replace("·", string.Empty);
        Assert.Equal(pdf, Visible(package.AllText()));
    }

    // ---- embedded fonts ----

    /// <summary>A TrueType font to build the sample with; every Windows install has several.</summary>
    private static string SystemFont()
    {
        string fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        foreach (string name in (string[])["arial.ttf", "tahoma.ttf", "verdana.ttf", "segoeui.ttf", "calibri.ttf"])
        {
            string path = Path.Combine(fonts, name);
            if (File.Exists(path)) return path;
        }
        return Directory.EnumerateFiles(fonts, "*.ttf").FirstOrDefault()
            ?? throw new InvalidOperationException($"no TrueType font in {fonts} to build the sample with");
    }

    [Fact]
    public async Task Lifts_an_embedded_font_out_of_the_document_when_asked()
    {
        var program = await File.ReadAllBytesAsync(SystemFont());
        var b = new SamplePdfBuilder();
        int catalog = b.Reserve(), pages = b.Reserve(), page = b.Reserve();

        int file = b.AddStream($"/Length1 {program.Length}", program);
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /ABCDEF+Sample /Flags 32 " +
                               $"/FontBBox [-665 -325 2000 1006] /ItalicAngle 0 /Ascent 905 /Descent -212 " +
                               $"/CapHeight 716 /StemV 80 /FontFile2 {file} 0 R >>");
        int font = b.Add("<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Sample /FirstChar 32 /LastChar 126 " +
                         $"/Widths [{string.Join(' ', Enumerable.Repeat(500, 95))}] /Encoding /WinAnsiEncoding " +
                         $"/FontDescriptor {descriptor} 0 R >>");

        int stream = b.AddStream("", Encoding.Latin1.GetBytes("BT /F1 14 Tf 72 700 Td (Embedded type) Tj ET\n"));
        b.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {stream} 0 R " +
                    $"/Resources << /Font << /F1 {font} 0 R >> >> >>");
        b.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        b.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");

        string path = Samples.TempPath();
        await File.WriteAllBytesAsync(path, b.Build(catalog));

        await using var document = await PdfiumDocument.OpenAsync(path);

        // Nothing is read unless the export asks: a font program is megabytes per document.
        var plain = await document.GetPageContentAsync(0, RenderPriority.Background);
        Assert.Empty(plain.Fonts);

        var withFonts = await document.GetPageContentAsync(0, RenderPriority.Background,
            new PageContentRequest { Fonts = true });

        var embedded = Assert.Single(withFonts.Fonts);
        // PDFium strips the subset tag from the name, so every embedded font is declared a subset.
        Assert.True(embedded.IsSubset);
        Assert.True(embedded.Data.Length > 1000);
        Assert.Equal(0x00010000u, (uint)((embedded.Data[0] << 24) | (embedded.Data[1] << 16) |
                                         (embedded.Data[2] << 8) | embedded.Data[3]));

        var composed = ContentComposer.Compose([withFonts], null, null,
            ExportOptions.Default with { EmbedFonts = true });
        Assert.Single(composed.Fonts);

        using var package = DocxPackage.Write(composed);
        package.AssertValid();
        Assert.Contains(package.Entries, e => e.StartsWith("word/fonts/", StringComparison.Ordinal));
    }

    private static string CellText(DocxTable table, int row, int column) =>
        string.Concat(table.Rows[row].Cells[column].Blocks.OfType<DocxParagraph>().Select(p => p.Text));
}
