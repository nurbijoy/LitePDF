using System.Xml.Linq;
using LitePdf.Core.Export;

namespace LitePdf.Core.Tests;

/// <summary>
/// The parts of a page that used to be dropped on the floor: the fill behind a table cell, a sticky note,
/// a drawing no image object holds, and the font programs themselves.
/// </summary>
public sealed class ExportExtrasTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly TextStyle Bold = new("Calibri", 11, true, false, 0x000000);

    /// <summary>A ruled three-column, three-row table with its text inside the cells.</summary>
    private static PageBuilder RuledTable()
    {
        var page = new PageBuilder()
            .Row(0.11, 0.02, Bold, ("Name", 0.12, 0.20), ("Role", 0.42, 0.50), ("Year", 0.72, 0.80))
            .Row(0.16, 0.02, null, ("Ada", 0.12, 0.18), ("Analyst", 0.42, 0.52), ("1843", 0.72, 0.80))
            .Row(0.21, 0.02, null, ("Alan", 0.12, 0.19), ("Logician", 0.42, 0.53), ("1936", 0.72, 0.80));

        foreach (double y in (double[])[0.10, 0.15, 0.20, 0.25])
            page.Rule(new RectD(0.10, y - 0.001, 0.90, y + 0.001), true);
        foreach (double x in (double[])[0.10, 0.40, 0.70, 0.90])
            page.Rule(new RectD(x - 0.001, 0.10, x + 0.001, 0.25), false);
        return page;
    }

    // ---- cell shading ----

    [Fact]
    public void Carries_the_fill_behind_a_shaded_header_row()
    {
        // One wide rectangle behind all three header cells, which is how a PDF shades a row.
        var page = RuledTable().Fill(new RectD(0.10, 0.10, 0.90, 0.15), 0xD9DEEB).Build();
        var table = Assert.Single(ContentComposer.Compose([page]).AllBlocks.OfType<DocxTable>());

        Assert.All(table.Rows[0].Cells, cell => Assert.Equal(0xD9DEEBu, cell.Shading));
        Assert.All(table.Rows[1].Cells, cell => Assert.Null(cell.Shading));

        using var package = DocxPackage.Write(ContentComposer.Compose([page]));
        package.AssertValid();
        Assert.Equal(3, package.Document.Descendants(W + "shd")
            .Count(s => (string?)s.Attribute(W + "fill") == "D9DEEB"));
    }

    [Fact]
    public void Shades_a_single_cell_the_PDF_filled()
    {
        var page = RuledTable().Fill(new RectD(0.40, 0.15, 0.70, 0.20), 0xFFF2CC).Build();
        var table = Assert.Single(ContentComposer.Compose([page]).AllBlocks.OfType<DocxTable>());

        Assert.Equal(0xFFF2CCu, table.Rows[1].Cells[1].Shading);
        Assert.Null(table.Rows[1].Cells[0].Shading);
    }

    [Fact]
    public void Ignores_a_panel_behind_the_whole_table()
    {
        // A background behind everything is the table's own, and painting every cell with it would lose
        // the distinction the PDF was drawing.
        var page = RuledTable().Fill(new RectD(0.09, 0.09, 0.91, 0.26), 0xEFEFEF).Build();
        var table = Assert.Single(ContentComposer.Compose([page]).AllBlocks.OfType<DocxTable>());

        Assert.All(table.Rows.SelectMany(r => r.Cells), cell => Assert.Null(cell.Shading));
    }

    [Fact]
    public void Ignores_a_white_fill()
    {
        var page = RuledTable().Fill(new RectD(0.10, 0.10, 0.90, 0.15), 0xFFFFFF).Build();
        var table = Assert.Single(ContentComposer.Compose([page]).AllBlocks.OfType<DocxTable>());
        Assert.All(table.Rows[0].Cells, cell => Assert.Null(cell.Shading));
    }

    // ---- comments ----

    private static PageExtras Note(string contents, string author, RectD at) =>
        new([], [new PdfAnnotation(0, 0, AnnotationKind.Note, at, [], null, contents) { Author = author }]);

    [Fact]
    public void Turns_a_sticky_note_into_a_Word_comment()
    {
        var page = new PageBuilder()
            .Line("The first paragraph of the page.", 0.1, 0.10, 0.6)
            .Line("The second paragraph, which the note sits beside.", 0.1, 0.40, 0.7)
            .Build();

        var document = ContentComposer.Compose([page], [Note("Needs a citation.", "Ada Lovelace", new RectD(0.86, 0.39, 0.90, 0.42))]);

        var comment = Assert.Single(document.Comments);
        Assert.Equal("Needs a citation.", comment.Text);
        Assert.Equal("Ada Lovelace", comment.Author);
        Assert.Equal("AL", comment.Initials);

        var anchored = Assert.Single(document.AllBlocks.OfType<DocxParagraph>(), p => p.CommentIds.Count > 0);
        Assert.Equal("The second paragraph, which the note sits beside.", anchored.Text);
        Assert.Equal(comment.Id, anchored.CommentIds[0]);
    }

    [Fact]
    public void Writes_the_comment_part_and_anchors_it_in_the_body()
    {
        var page = new PageBuilder().Line("A paragraph with a note against it.", 0.1, 0.10, 0.6).Build();
        var document = ContentComposer.Compose([page], [Note("Check this figure.", "Reviewer", new RectD(0.8, 0.10, 0.84, 0.13))]);

        using var package = DocxPackage.Write(document);
        package.AssertValid();
        Assert.True(package.Has("word/comments.xml"));

        var comment = Assert.Single(package.Xml("word/comments.xml").Root!.Elements(W + "comment"));
        Assert.Equal("Reviewer", (string?)comment.Attribute(W + "author"));
        Assert.Contains("Check this figure.", comment.Value);

        var paragraph = package.Paragraphs.First(p => p.Descendants(W + "commentReference").Any());
        Assert.Single(paragraph.Descendants(W + "commentRangeStart"));
        Assert.Single(paragraph.Descendants(W + "commentRangeEnd"));

        // The part has to be declared, or Word calls the document unreadable rather than ignoring it.
        Assert.Contains("/word/comments.xml", package.Xml("[Content_Types].xml").ToString());
    }

    [Fact]
    public void Writes_no_comment_part_when_there_is_nothing_to_say()
    {
        var page = new PageBuilder().Line("A paragraph on its own.", 0.1, 0.10, 0.6).Build();
        using var package = DocxPackage.Write(ContentComposer.Compose([page]));
        Assert.False(package.Has("word/comments.xml"));
    }

    [Fact]
    public void Leaves_notes_out_when_annotations_are_switched_off()
    {
        var page = new PageBuilder().Line("A paragraph with a note against it.", 0.1, 0.10, 0.6).Build();
        var document = ContentComposer.Compose([page], [Note("Ignored.", "Reviewer", new RectD(0.8, 0.10, 0.84, 0.13))],
            null, ExportOptions.Default with { Annotations = false });

        Assert.Empty(document.Comments);
    }

    // ---- drawings ----

    private static PlacedImage Drawing(RectD bounds) =>
        PlacedImage.FromBits(bounds, new ImageBits(8, 8, new byte[8 * 8 * 4])) with { IsDrawing = true };

    [Fact]
    public void Places_a_drawing_in_the_flow_like_any_other_picture()
    {
        var page = new PageBuilder().Line("Text above the chart.", 0.1, 0.10, 0.6).Build()
            with { Images = [Drawing(new RectD(0.2, 0.3, 0.8, 0.6))] };

        var picture = Assert.Single(ContentComposer.Compose([page]).AllBlocks.OfType<DocxPicture>());
        Assert.True(picture.Image.IsDrawing);
        Assert.InRange(picture.WidthPoints, 350, 380);   // 0.6 of a 612 pt page
    }

    [Fact]
    public void Leaves_drawings_out_when_they_are_switched_off()
    {
        var page = new PageBuilder().Line("Text above the chart.", 0.1, 0.10, 0.6).Build()
            with { Images = [Drawing(new RectD(0.2, 0.3, 0.8, 0.6))] };

        Assert.Empty(ContentComposer.Compose([page], null, null, ExportOptions.Default with { Drawings = false })
            .AllBlocks.OfType<DocxPicture>());

        // A drawing is not a picture the reader should have fetched either.
        Assert.False((ExportOptions.Default with { Drawings = false }).ToPageRequest().Drawings);
        Assert.False((ExportOptions.Default with { Images = false }).ToPageRequest().Drawings);
    }

    // ---- embedded fonts ----

    private static byte[] FakeFont(byte fill)
    {
        // An sfnt header is all the writer looks at; what follows only has to survive the round trip.
        var data = new byte[256];
        data[0] = 0x00; data[1] = 0x01; data[2] = 0x00; data[3] = 0x00;
        for (int i = 4; i < data.Length; i++) data[i] = fill;
        return data;
    }

    [Fact]
    public void Embeds_a_font_obfuscated_the_way_Word_expects()
    {
        var font = new EmbeddedFont("Garamond", false, false, FakeFont(0xAB), IsSubset: true);
        var document = new DocxDocument([new DocxSection(new PageSize(612, 792), DocxMargins.Default,
            [new DocxParagraph([new DocxRun("Text", new TextStyle("Garamond", 11, false, false, 0))])])])
        {
            Fonts = [font],
        };

        using var package = DocxPackage.Write(document);
        package.AssertValid();

        var table = package.Xml("word/fontTable.xml").Root!;
        var entry = Assert.Single(table.Elements(W + "font"));
        Assert.Equal("Garamond", (string?)entry.Attribute(W + "name"));

        var embed = Assert.Single(entry.Elements(W + "embedRegular"));
        Assert.Equal("true", (string?)embed.Attribute(W + "subsetted"));
        string key = (string)embed.Attribute(W + "fontKey")!;
        Assert.Matches(@"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$", key);

        // The part is the font XORed against the key: undo it and the original bytes come back.
        string target = (string)package.Xml("word/_rels/document.xml.rels").Root!
            .Elements(PkgRel + "Relationship")
            .Single(r => ((string)r.Attribute("Type")!).EndsWith("/font", StringComparison.Ordinal))
            .Attribute("Target")!;

        var stored = package.Bytes("word/" + target);
        Assert.Equal(font.Data.Length, stored.Length);
        Assert.Equal(font.Data, Deobfuscate(stored, key));

        Assert.Contains("obfuscatedFont", package.Xml("[Content_Types].xml").ToString());
    }

    [Fact]
    public void Names_each_style_of_an_embedded_family_separately()
    {
        var document = new DocxDocument([new DocxSection(new PageSize(612, 792), DocxMargins.Default, [DocxParagraph.Empty])])
        {
            Fonts =
            [
                new EmbeddedFont("Garamond", false, false, FakeFont(1), false),
                new EmbeddedFont("Garamond", true, false, FakeFont(2), false),
                new EmbeddedFont("Garamond", true, true, FakeFont(3), false),
            ],
        };

        using var package = DocxPackage.Write(document);
        package.AssertValid();

        var entry = Assert.Single(package.Xml("word/fontTable.xml").Root!.Elements(W + "font"));
        Assert.Single(entry.Elements(W + "embedRegular"));
        Assert.Single(entry.Elements(W + "embedBold"));
        Assert.Single(entry.Elements(W + "embedBoldItalic"));
    }

    [Fact]
    public void Writes_no_font_table_when_nothing_is_embedded()
    {
        var page = new PageBuilder().Line("Plain text.", 0.1, 0.10, 0.6).Build();
        using var package = DocxPackage.Write(ContentComposer.Compose([page]));
        Assert.False(package.Has("word/fontTable.xml"));
        Assert.DoesNotContain(package.Entries, e => e.StartsWith("word/fonts/", StringComparison.Ordinal));
    }

    [Fact]
    public void Keeps_one_font_per_family_and_style()
    {
        var page = new PageBuilder().Line("Text.", 0.1, 0.10, 0.6).Build() with
        {
            Fonts =
            [
                new EmbeddedFont("Garamond", false, false, FakeFont(1), false),
                new EmbeddedFont("Garamond", false, false, FakeFont(9), false),
                new EmbeddedFont("Garamond", true, false, FakeFont(2), false),
            ],
        };

        var document = ContentComposer.Compose([page], null, null, ExportOptions.Default with { EmbedFonts = true });
        Assert.Equal(2, document.Fonts.Count);

        // And nothing is carried unless the export asked for it.
        Assert.Empty(ContentComposer.Compose([page]).Fonts);
    }

    private static byte[] Deobfuscate(byte[] stored, string key)
    {
        string hex = key.Trim('{', '}').Replace("-", "", StringComparison.Ordinal);
        var mask = new byte[16];
        for (int i = 0; i < 16; i++) mask[i] = Convert.ToByte(hex.Substring(30 - i * 2, 2), 16);

        var plain = (byte[])stored.Clone();
        for (int i = 0; i < Math.Min(32, plain.Length); i++) plain[i] ^= mask[i % 16];
        return plain;
    }
}
