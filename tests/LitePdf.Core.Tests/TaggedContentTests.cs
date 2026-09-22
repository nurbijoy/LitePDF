using LitePdf.Core.Export;

namespace LitePdf.Core.Tests;

/// <summary>
/// The tagged path of the composer, driven by synthetic pages: a structure tree plus the marked-content
/// ranges that tie it to the text, which is exactly what <c>PdfContentReader</c> hands over.
/// </summary>
public sealed class TaggedContentTests
{
    private static PageTag Tag(string type, params PageTag[] children) => new(type, children);

    private static PageTag Leaf(string type, params int[] ids) =>
        new(type, []) { MarkedContentIds = ids };

    private static List<DocxParagraph> ParagraphsOf(DocxDocument document) =>
        [.. document.AllBlocks.OfType<DocxParagraph>()];

    [Fact]
    public void Takes_the_heading_level_from_the_tag_rather_than_the_type_size()
    {
        // The PDF draws both lines at body size, so no heuristic would call either one a heading.
        var page = new PageBuilder()
            .Mark(0).Line("Chapter One", 0.1, 0.10, 0.4)
            .Mark(1).Line("A subsection", 0.1, 0.14, 0.4)
            .Mark(2).Line("Body text that follows the two headings above.", 0.1, 0.20, 0.8)
            .Tagged(Tag("Document", Leaf("H1", 0), Leaf("H2", 1), Leaf("P", 2)))
            .Build();

        var paragraphs = ParagraphsOf(ContentComposer.Compose([page]));
        Assert.Equal(DocxParagraphStyle.Heading1, paragraphs[0].Style);
        Assert.Equal(DocxParagraphStyle.Heading2, paragraphs[1].Style);
        Assert.Equal(DocxParagraphStyle.Body, paragraphs[2].Style);
    }

    [Fact]
    public void Joins_the_lines_one_tagged_paragraph_was_broken_into()
    {
        // Both lines stop well short of the measure, so the geometry would make three paragraphs of them.
        var page = new PageBuilder()
            .Mark(0)
            .Line("A sentence that stops short,", 0.1, 0.10, 0.45)
            .Line("and carries on here.", 0.1, 0.13, 0.38)
            .Mark(1).Line("A second element entirely.", 0.1, 0.17, 0.45)
            .Tagged(Tag("Document", Leaf("P", 0), Leaf("P", 1)))
            .Build();

        var paragraphs = ParagraphsOf(ContentComposer.Compose([page]));
        Assert.Equal(2, paragraphs.Count);
        Assert.Equal("A sentence that stops short, and carries on here.", paragraphs[0].Text);
        Assert.Equal("A second element entirely.", paragraphs[1].Text);
    }

    [Fact]
    public void Does_not_run_a_page_together_when_one_tag_covers_all_of_it()
    {
        // A badly tagged file wraps the whole page in one /P. Lines far apart are still separate blocks.
        var page = new PageBuilder()
            .Mark(0)
            .Line("The top of the page.", 0.1, 0.10, 0.45)
            .Line("The foot of the page, an inch and a half below.", 0.1, 0.60, 0.8)
            .Tagged(Tag("Document", Leaf("P", 0)))
            .Build();

        var paragraphs = ParagraphsOf(ContentComposer.Compose([page]));
        Assert.Equal(2, paragraphs.Count);
    }

    [Fact]
    public void Reads_a_list_from_the_tags_and_strips_the_label()
    {
        var page = new PageBuilder()
            .Mark(0).Line("· The first item", 0.12, 0.10, 0.5)
            .Mark(1).Line("· The second item", 0.12, 0.13, 0.5)
            .Mark(2).Line("1. A numbered item", 0.12, 0.16, 0.5)
            .Tagged(Tag("Document",
                Tag("L", Tag("LI", Leaf("LBody", 0)), Tag("LI", Leaf("LBody", 1))),
                Tag("L", Tag("LI", Leaf("LBody", 2)))))
            .Build();

        var paragraphs = ParagraphsOf(ContentComposer.Compose([page]));
        Assert.Equal(3, paragraphs.Count);
        Assert.All(paragraphs, p => Assert.NotEqual(DocxListKind.None, p.List));
        Assert.Equal(DocxListKind.Bullet, paragraphs[0].List);
        Assert.Equal(DocxListKind.Number, paragraphs[2].List);

        // The marker becomes Word's own numbering, so it must not also be left in the text.
        Assert.Equal("The first item", paragraphs[0].Text);
        Assert.Equal("A numbered item", paragraphs[2].Text);
    }

    [Fact]
    public void Indents_a_list_nested_inside_another()
    {
        var page = new PageBuilder()
            .Mark(0).Line("· Outer", 0.12, 0.10, 0.4)
            .Mark(1).Line("· Inner", 0.16, 0.13, 0.4)
            .Tagged(Tag("Document",
                Tag("L",
                    Tag("LI", Leaf("LBody", 0), Tag("L", Tag("LI", Leaf("LBody", 1)))))))
            .Build();

        var paragraphs = ParagraphsOf(ContentComposer.Compose([page]));
        Assert.Equal(0, paragraphs[0].ListLevel);
        Assert.Equal(1, paragraphs[1].ListLevel);
    }

    [Fact]
    public void Builds_a_table_a_tagged_document_declares_without_drawing_a_single_line()
    {
        var page = new PageBuilder()
            .Mark(0).Line("Region", 0.10, 0.10, 0.20)
            .Mark(1).Line("Orders", 0.40, 0.10, 0.50)
            .Mark(2).Line("North", 0.10, 0.14, 0.20)
            .Mark(3).Line("1,204", 0.40, 0.14, 0.50)
            .Mark(4).Line("South", 0.10, 0.18, 0.20)
            .Mark(5).Line("987", 0.40, 0.18, 0.48)
            .Tagged(Tag("Document", Tag("Table",
                Tag("TR", Leaf("TH", 0), Leaf("TH", 1)),
                Tag("TR", Leaf("TD", 2), Leaf("TD", 3)),
                Tag("TR", Leaf("TD", 4), Leaf("TD", 5)))))
            .Build();

        var document = ContentComposer.Compose([page]);
        var table = Assert.Single(document.AllBlocks.OfType<DocxTable>());

        Assert.Equal(3, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(2, row.Cells.Count));
        Assert.True(table.Rows[0].IsHeader, "a row of /TH is a header row");
        Assert.False(table.HasBorders, "the PDF drew no lines, so Word should not draw any either");
        Assert.Equal("North", CellText(table, 1, 0));
        Assert.Equal("987", CellText(table, 2, 1));

        // And the cell text is not also left loose in the body.
        Assert.DoesNotContain(document.AllBlocks.OfType<DocxParagraph>(), p => p.Text.Contains("North"));
    }

    [Fact]
    public void Keeps_a_line_the_file_forgot_to_tag_inside_a_table()
    {
        // The note sits inside the table's bounds but belongs to no cell. A table that claimed it would
        // swallow it for good, and losing a line is the one thing this export must never do.
        var page = new PageBuilder()
            .Mark(0).Line("Region", 0.10, 0.10, 0.20)
            .Mark(1).Line("Orders", 0.40, 0.10, 0.50)
            .Mark(2).Line("North", 0.10, 0.14, 0.20)
            .Mark(3).Line("1,204", 0.40, 0.14, 0.50)
            .Mark(4).Line("South", 0.10, 0.22, 0.20)
            .Mark(5).Line("987", 0.40, 0.22, 0.48)
            .Mark(-1).Line("Provisional figures only", 0.10, 0.18, 0.45)
            .Tagged(Tag("Document", Tag("Table",
                Tag("TR", Leaf("TH", 0), Leaf("TH", 1)),
                Tag("TR", Leaf("TD", 2), Leaf("TD", 3)),
                Tag("TR", Leaf("TD", 4), Leaf("TD", 5)))))
            .Build();

        var document = ContentComposer.Compose([page]);
        Assert.Single(document.AllBlocks.OfType<DocxTable>());
        Assert.Contains(ParagraphsOf(document), p => p.Text == "Provisional figures only");
    }

    [Fact]
    public void Carries_a_column_span_from_the_tag()
    {
        var page = new PageBuilder()
            .Mark(0).Line("Summary across the top", 0.10, 0.10, 0.50)
            .Mark(1).Line("Left", 0.10, 0.14, 0.20)
            .Mark(2).Line("Right", 0.40, 0.14, 0.50)
            .Mark(3).Line("A", 0.10, 0.18, 0.20)
            .Mark(4).Line("B", 0.40, 0.18, 0.50)
            .Tagged(Tag("Document", Tag("Table",
                Tag("TR", new PageTag("TD", []) { MarkedContentIds = [0], ColumnSpan = 2 }),
                Tag("TR", Leaf("TD", 1), Leaf("TD", 2)),
                Tag("TR", Leaf("TD", 3), Leaf("TD", 4)))))
            .Build();

        var table = Assert.Single(ContentComposer.Compose([page]).AllBlocks.OfType<DocxTable>());
        Assert.Equal(2, table.Rows[0].Cells[0].ColumnSpan);
        Assert.Equal(2, table.ColumnWidthsTwips.Count);
    }

    [Fact]
    public void Gives_a_picture_the_alt_text_of_its_figure()
    {
        var page = new PageBuilder()
            .Mark(0).Line("Below is the plate.", 0.1, 0.10, 0.5)
            .Build();

        var image = PlacedImage.FromBits(new RectD(0.2, 0.3, 0.6, 0.6), new ImageBits(4, 4, new byte[64]))
            with { MarkedContentId = 7 };

        page = page with
        {
            Images = [image],
            Tags = [Tag("Document", Leaf("P", 0), new PageTag("Figure", []) { MarkedContentIds = [7], AltText = "A map of the estuary" })],
        };

        var picture = Assert.Single(ContentComposer.Compose([page]).AllBlocks.OfType<DocxPicture>());
        Assert.Equal("A map of the estuary", picture.AltText);
    }

    [Fact]
    public void Falls_back_to_the_geometry_when_the_tags_are_switched_off()
    {
        var page = new PageBuilder()
            .Mark(0).Line("Chapter One", 0.1, 0.10, 0.4)
            .Mark(1).Line("Body text that follows the heading above.", 0.1, 0.20, 0.8)
            .Tagged(Tag("Document", Leaf("H1", 0), Leaf("P", 1)))
            .Build();

        var paragraphs = ParagraphsOf(ContentComposer.Compose([page], null, null,
            ExportOptions.Default with { UseTags = false }));

        // Set at body size and not bold, nothing marks it out as a heading once the tags are ignored.
        Assert.Equal(DocxParagraphStyle.Body, paragraphs[0].Style);
        Assert.Equal("Chapter One", paragraphs[0].Text);
    }

    [Fact]
    public void Survives_a_structure_tree_that_points_at_nothing()
    {
        var page = new PageBuilder()
            .Line("Text with no marked content at all.", 0.1, 0.10, 0.8)
            .Tagged(Tag("Document", Leaf("H1", 42), Tag("Table", Tag("TR", Leaf("TD", 43)))))
            .Build();

        var paragraphs = ParagraphsOf(ContentComposer.Compose([page]));
        Assert.Equal("Text with no marked content at all.", Assert.Single(paragraphs).Text);
    }

    private static string CellText(DocxTable table, int row, int column) =>
        string.Concat(table.Rows[row].Cells[column].Blocks.OfType<DocxParagraph>().Select(p => p.Text));
}
