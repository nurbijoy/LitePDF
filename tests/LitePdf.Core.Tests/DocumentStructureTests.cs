using System.Xml.Linq;
using LitePdf.Core.Export;

namespace LitePdf.Core.Tests;

/// <summary>
/// The structure a converted document keeps: contents pages, lists, panels, code, tables across pages and
/// running heads come out as Word's own constructs, not as text that merely looks like them.
/// </summary>
public sealed class DocumentStructureTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // A Letter page with one-inch margins: text runs from 72/612 to 540/612, 11 pt on an 18 pt pitch.
    private const double Left = 0.1176;
    private const double Right = 0.8824;
    private const double Height = 0.0167;
    private const double Pitch = 0.0227;

    private static readonly TextStyle Code = new("Consolas", 10, false, false, 0x000000);
    private static readonly TextStyle Bold = new("Calibri", 11, true, false, 0x000000);

    private static double Top(int line) => 0.1 + line * Pitch;

    private static List<DocxParagraph> Paragraphs(DocxDocument document) =>
        [.. document.Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>()];

    [Fact]
    public void A_contents_entry_is_a_tab_to_its_page_number_with_a_dot_leader()
    {
        var page = new PageBuilder()
            .Line("Contents", Left, Top(0), 0.3, style: new TextStyle("Calibri", 16, true, false, 0))
            .Line("1. Introduction ............................................ 3", Left, Top(2), Right)
            .Line("1.1 Scope and purpose ..................................... 4", Left + 0.03, Top(3), Right)
            .Line("2. Method ................................................. 9", Left, Top(4), Right);

        var entries = Paragraphs(ContentComposer.Compose([page.Build()]))
            .Where(p => p.TabStops.Any(t => t.Leader == DocxTabLeader.Dot)).ToList();

        Assert.Equal(3, entries.Count);
        Assert.Equal("1. Introduction\t3", entries[0].Text);
        Assert.Equal("1.1 Scope and purpose\t4", entries[1].Text);
        // Entries, not a numbered list; one paragraph each; the page numbers against one right-aligned stop.
        Assert.All(entries, e => Assert.Equal(DocxListKind.None, e.List));
        Assert.All(entries, e => Assert.Equal(DocxAlignment.Right, Assert.Single(e.TabStops).Alignment));
        Assert.Equal(entries[0].TabStops[0].PositionTwips, entries[2].TabStops[0].PositionTwips);
        Assert.True(entries[1].IndentTwips > entries[0].IndentTwips, "the second level is indented");

        using var package = DocxPackage.Write(ContentComposer.Compose([page.Build()]));
        package.AssertValid();
        Assert.Contains(package.Document.Descendants(W + "tab"), t => (string?)t.Attribute(W + "leader") == "dot");
    }

    [Fact]
    public void A_blank_to_fill_in_is_text_not_a_leader()
    {
        var page = new PageBuilder()
            .Line("The plan was ____________ by all.", Left, Top(0), 0.6)
            .Line("Choose the word that best completes the sentence . . . . . then move on.", Left, Top(2), Right);

        var paragraphs = Paragraphs(ContentComposer.Compose([page.Build()]));

        Assert.Contains(paragraphs, p => p.Text == "The plan was ____________ by all.");
        Assert.All(paragraphs, p => Assert.Empty(p.TabStops));
    }

    [Fact]
    public void A_list_item_whose_indent_balances_its_short_line_is_not_centred()
    {
        // The item is inset by as much as its first line stops short of the right edge — the look of a
        // centred line — and its second line hangs under its text.
        var page = new PageBuilder()
            .Line("A paragraph of prose runs across the full measure of the column to set it out", Left, Top(0), Right)
            .Line("• Regulatory capital calculation under the guidelines, which is not an approved", Left + 0.02, Top(1), Right - 0.02)
            .Line("use of this model;", Left + 0.045, Top(2), 0.33)
            .Line("• Credit approval at origination, unless separately validated.", Left + 0.02, Top(3), 0.7);

        var paragraphs = Paragraphs(ContentComposer.Compose([page.Build()]));
        var item = Assert.Single(paragraphs, p => p.Text.Contains("Regulatory", StringComparison.Ordinal));

        Assert.Equal(DocxListKind.Bullet, item.List);
        Assert.Equal(DocxAlignment.Left, item.Alignment);
        Assert.EndsWith("approved use of this model;", item.Text);
        Assert.DoesNotContain(paragraphs, p => p.Text.StartsWith("use of this", StringComparison.Ordinal));
    }

    [Fact]
    public void A_shaded_panel_becomes_a_box_with_its_own_border_colours()
    {
        double top = Top(2) - 0.012, bottom = Top(4) + Height + 0.012;
        var page = new PageBuilder()
            .Line("Body text before the panel runs the whole width of the measure here.", Left, Top(0), Right)
            .Line("Consistency requirement", Left + 0.015, Top(2), 0.4, style: Bold)
            .Line("The definition used in development must be identical to the one used", Left + 0.015, Top(3), Right - 0.015)
            .Line("in production.", Left + 0.015, Top(4), 0.3)
            .Line("Body text after the panel runs the whole width of the measure here.", Left, Top(7), Right)
            .Fill(new RectD(Left - 0.004, top, Right + 0.004, bottom), 0xEEF3F8)
            .Fill(new RectD(Left - 0.008, top, Left - 0.004, bottom), 0x2E74B5);   // the bar down its left side

        var document = ContentComposer.Compose([page.Build()]);
        var paragraphs = Paragraphs(document);
        var boxed = paragraphs.Where(p => p.Box is not null).ToList();

        Assert.Equal(2, boxed.Count);
        Assert.StartsWith("Consistency requirement", boxed[0].Text);
        Assert.All(boxed, p => Assert.Equal(boxed[0].Box, p.Box));   // one panel in Word, not two
        Assert.Equal(0xEEF3F8u, boxed[0].Box!.Fill);
        Assert.Equal(0x2E74B5u, boxed[0].Box!.Left.Color);
        Assert.Equal(0xEEF3F8u, boxed[0].Box!.Top.Color);      // no line drawn there: the padding stays shaded
        Assert.Null(paragraphs.First().Box);
        Assert.Null(paragraphs.Last().Box);

        using var package = DocxPackage.Write(document);
        package.AssertValid();
        var shaded = package.Paragraphs.First(p => DocxPackage.TextOf(p).StartsWith("Consistency", StringComparison.Ordinal));
        Assert.Equal("EEF3F8", (string?)shaded.Descendants(W + "shd").Single().Attribute(W + "fill"));
        Assert.Equal("2E74B5", (string?)shaded.Descendants(W + "left").Single().Attribute(W + "color"));
    }

    [Fact]
    public void Code_keeps_its_line_breaks_indentation_and_blank_lines()
    {
        // A monospaced listing whose indentation the PDF drew as a jump to the right, not as spaces.
        const double Char = 0.01;
        var page = new PageBuilder()
            .Line("The function below computes the area of a circle from its radius.", Left, Top(0), Right)
            .Line("def area(r):", Left, Top(2), Left + 12 * Char, style: Code)
            .Line("return 3.14 * r * r", Left + 4 * Char, Top(3), Left + 23 * Char, style: Code)
            .Line("print(area(2))", Left, Top(5), Left + 14 * Char, style: Code);

        var paragraphs = Paragraphs(ContentComposer.Compose([page.Build()]));
        var listing = Assert.Single(paragraphs, p => p.Text.StartsWith("def", StringComparison.Ordinal));

        Assert.Equal("def area(r):\n    return 3.14 * r * r\n\nprint(area(2))", listing.Text);
        Assert.Equal(DocxListKind.None, listing.List);
        Assert.All(listing.Runs, r => Assert.Equal("Consolas", r.Style.FontFamily));
    }

    [Fact]
    public void A_sentence_with_a_monospaced_link_is_not_code()
    {
        var mono = new TextStyle("DejaVu Sans Mono", 7, false, false, 0);
        var page = new PageBuilder()
            .Line("Study online at", Left, Top(0), Left + 0.15, height: 0.012)
            .Then("quizlet.com/_3k3n5x", Left + 0.16, Left + 0.4, mono);

        var paragraph = Assert.Single(Paragraphs(ContentComposer.Compose([page.Build()])));
        Assert.Equal("Study online at quizlet.com/_3k3n5x", paragraph.Text);
    }

    [Fact]
    public void A_table_that_runs_on_to_the_next_page_is_one_table_with_a_repeating_header()
    {
        var first = RuledTable(new PageBuilder()
            .Line("The samples considered are listed in the table that follows below.", Left, Top(0), Right), 0.78, ("Ada", "1843"), ("Alan", "1936"));
        var second = RuledTable(new PageBuilder(), 0.10, ("Grace", "1952"), ("Edsger", "1959"));

        var document = ContentComposer.Compose([first.Build(0), second.Build(1)]);
        var table = Assert.Single(document.Sections.SelectMany(s => s.Blocks).OfType<DocxTable>());

        Assert.Equal(5, table.Rows.Count);                  // one header, four people: the repeat is gone
        Assert.True(table.Rows[0].IsHeader);                // and Word repeats the header instead
        Assert.Equal("Grace", CellText(table.Rows[3].Cells[0]));
    }

    /// <summary>A ruled two-column table with a bold header row, starting at <paramref name="top"/>.</summary>
    private static PageBuilder RuledTable(PageBuilder page, double top, params (string Name, string Year)[] rows)
    {
        const double Row = 0.04;
        page.Row(top + 0.01, 0.02, Bold, ("Name", 0.12, 0.30), ("Year", 0.52, 0.70));
        for (int r = 0; r < rows.Length; r++)
            page.Row(top + Row * (r + 1) + 0.01, 0.02, null, (rows[r].Name, 0.12, 0.30), (rows[r].Year, 0.52, 0.70));

        double bottom = top + Row * (rows.Length + 1);
        for (int r = 0; r <= rows.Length + 1; r++)
            page.Rule(new RectD(0.10, top + Row * r - 0.001, 0.90, top + Row * r + 0.001), horizontal: true);
        foreach (double x in (double[])[0.10, 0.50, 0.90])
            page.Rule(new RectD(x - 0.001, top, x + 0.001, bottom), horizontal: false);
        return page;
    }

    private static string CellText(DocxCell cell) =>
        string.Concat(cell.Blocks.OfType<DocxParagraph>().Select(p => p.Text)).Trim();

    [Fact]
    public void Centres_a_cell_the_page_centred_in_a_taller_row()
    {
        var page = new PageBuilder()
            .Row(0.11, 0.02, Bold, ("Line", 0.12, 0.30), ("Responsibility", 0.52, 0.80))
            .Line("First line", 0.12, 0.19, 0.28)
            .Line("Model development and", 0.52, 0.155, 0.80)
            .Line("documentation, and the", 0.52, 0.18, 0.80)
            .Line("performance testing", 0.52, 0.205, 0.78);
        foreach (double y in (double[])[0.10, 0.15, 0.25])
            page.Rule(new RectD(0.10, y - 0.001, 0.90, y + 0.001), horizontal: true);
        foreach (double x in (double[])[0.10, 0.50, 0.90])
            page.Rule(new RectD(x - 0.001, 0.10, x + 0.001, 0.25), horizontal: false);

        var table = Assert.Single(ContentComposer.Compose([page.Build()]).Sections.SelectMany(s => s.Blocks).OfType<DocxTable>());

        Assert.Equal(DocxVerticalAlignment.Center, table.Rows[1].Cells[0].VerticalAlignment);
        Assert.Equal(DocxVerticalAlignment.Top, table.Rows[1].Cells[1].VerticalAlignment);
    }

    [Fact]
    public void A_line_a_long_word_wrapped_in_a_narrow_cell_is_not_a_paragraph_end()
    {
        // "Collateral" leaves a third of the cell empty, but "Management" is longer still: it wrapped.
        var page = new PageBuilder()
            .Row(0.11, 0.02, Bold, ("System", 0.11, 0.20), ("Owner", 0.29, 0.40))
            .Line("Collateral", 0.11, 0.15, 0.19)
            .Line("Management System", 0.11, 0.175, 0.24)
            .Line("Credit Administration Division", 0.29, 0.15, 0.55);
        foreach (double y in (double[])[0.10, 0.14, 0.21])
            page.Rule(new RectD(0.10, y - 0.001, 0.90, y + 0.001), horizontal: true);
        foreach (double x in (double[])[0.10, 0.27, 0.90])
            page.Rule(new RectD(x - 0.001, 0.10, x + 0.001, 0.21), horizontal: false);

        var table = Assert.Single(ContentComposer.Compose([page.Build()]).Sections.SelectMany(s => s.Blocks).OfType<DocxTable>());
        var cell = Assert.Single(table.Rows[1].Cells[0].Blocks.OfType<DocxParagraph>());

        Assert.Equal("Collateral Management System", cell.Text);
    }

    [Fact]
    public void A_running_head_keeps_the_colour_of_each_of_its_parts()
    {
        var grey = new TextStyle("Calibri", 8, false, false, 0x595959);
        var red = new TextStyle("Calibri", 8, true, false, 0xC00000);
        var pages = Enumerable.Range(0, 5).Select(p => new PageBuilder()
            .Line("Quarterly Credit Risk Review", Left, 0.04, 0.4, height: 0.011, style: grey)
            .Then("CONFIDENTIAL", 0.76, Right, red)
            .Line("Body text on this page running the whole width of the measure.", Left, Top(2), Right)
            .Build(p)).ToList();

        var header = ContentComposer.Compose(pages).Sections[0].Header;

        Assert.NotNull(header);
        Assert.Contains(header!.Runs, r => r.Text.Contains("CONFIDENTIAL", StringComparison.Ordinal) && r.Style.Color == 0xC00000);
        Assert.Contains(header.Runs, r => r.Text.Contains("Quarterly", StringComparison.Ordinal) && r.Style.Color == 0x595959);
    }

    [Fact]
    public void A_chapter_heading_at_the_top_of_a_page_starts_a_new_page()
    {
        var heading = new TextStyle("Calibri", 18, true, false, 0);
        var first = new PageBuilder();
        for (int i = 0; i < 32; i++)
            first.Line($"Line {i} of a long first chapter that runs on down the page to fill it.", Left, Top(i), Right);
        var second = new PageBuilder().Line("Chapter Two", Left, Top(0), 0.4, height: 0.027, style: heading);
        for (int i = 2; i < 35; i++)
            second.Line($"Line {i} of the second chapter, which runs a little further down the page.", Left, Top(i), Right);

        var paragraphs = Paragraphs(ContentComposer.Compose([first.Build(0), second.Build(1)]));
        var chapter = Assert.Single(paragraphs, p => p.Text == "Chapter Two");

        Assert.Equal(DocxParagraphStyle.Heading1, chapter.Style);
        Assert.True(chapter.PageBreakBefore, "the first chapter ended with room to spare, so the second starts a page");
    }

    [Fact]
    public void Ignores_a_printers_slug_outside_the_page()
    {
        var page = new PageBuilder()
            .Line("3.0 Diagnostic Test", 0.34, Top(0), 0.66, height: 0.025, style: new TextStyle("Calibri", 20, true, false, 0))
            .Line("The test that follows is not timed, and it runs the full width of the text.", Left, Top(3), Right)
            .Line("06_449745-ch03.indd 18 2/23/09 10:25:26 AM", 0.02, 1.02, 0.99);

        var paragraphs = Paragraphs(ContentComposer.Compose([page.Build()]));

        // Measured with the slug, the text would run from edge to edge and the title would sit off centre.
        Assert.DoesNotContain(paragraphs, p => p.Text.Contains("indd", StringComparison.Ordinal));
        Assert.Equal(DocxAlignment.Center, paragraphs.Single(p => p.Text == "3.0 Diagnostic Test").Alignment);
    }

    [Fact]
    public void The_page_number_field_keeps_the_footers_look_when_Word_updates_it()
    {
        var small = new TextStyle("Calibri", 8, false, false, 0x7F7F7F);
        var section = new DocxSection(new PageSize(612, 792), DocxMargins.Default, [new DocxParagraph([new DocxRun("Body", PageBuilder.Body)])])
        {
            Footer = new DocxHeaderFooter([new DocxRun("Page ", small), new DocxRun("1", small)], DocxAlignment.Center) { PageNumberRun = 1 },
        };

        using var package = DocxPackage.Write(new DocxDocument([section]));
        package.AssertValid();
        var fieldRuns = package.Xml("word/footer1.xml").Descendants(W + "r")
            .Where(r => r.Element(W + "fldChar") is not null || r.Element(W + "instrText") is not null).ToList();

        Assert.Equal(4, fieldRuns.Count);   // begin, the instruction, separate, end
        Assert.All(fieldRuns, r => Assert.Equal("16", (string?)r.Element(W + "rPr")?.Element(W + "sz")?.Attribute(W + "val")));
    }
}
