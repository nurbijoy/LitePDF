using LitePdf.Core.Export;

namespace LitePdf.Core.Tests;

public sealed class TableBuilderTests
{
    private static readonly TextStyle Bold = new("Calibri", 11, true, false, 0x000000);

    private static readonly double[] RowLines = [0.10, 0.15, 0.20, 0.25];
    private static readonly double[] ColumnLines = [0.10, 0.40, 0.70, 0.90];

    /// <summary>A three-column, three-row grid with its text laid out inside the cells.</summary>
    private static PageBuilder Grid(bool fullHeightMiddleRule = true)
    {
        var page = new PageBuilder()
            .Row(0.11, 0.02, Bold, ("Name", 0.12, 0.30), ("Role", 0.42, 0.58), ("Year", 0.72, 0.86))
            .Row(0.16, 0.02, null, ("Ada", 0.12, 0.24), ("Analyst", 0.42, 0.60), ("1843", 0.72, 0.86))
            .Row(0.21, 0.02, null, ("Alan", 0.12, 0.26), ("Logician", 0.42, 0.62), ("1936", 0.72, 0.86));

        foreach (double y in RowLines)
            page.Rule(new RectD(0.10, y - 0.001, 0.90, y + 0.001), horizontal: true);

        foreach (double x in ColumnLines)
        {
            // The rule between the first two columns can be cut short so the last row spans them.
            double bottom = !fullHeightMiddleRule && x == 0.40 ? 0.20 : 0.25;
            page.Rule(new RectD(x - 0.001, 0.10, x + 0.001, bottom), horizontal: false);
        }

        return page;
    }

    private static string CellText(DocxCell cell) =>
        string.Concat(cell.Blocks.OfType<DocxParagraph>().Select(p => p.Text)).Trim();

    private static DocxTable TableOf(PageBuilder page, ExportOptions? options = null)
    {
        var blocks = ContentComposer.Compose([page.Build()], options: options).Sections.SelectMany(s => s.Blocks);
        return Assert.Single(blocks.OfType<DocxTable>());
    }

    [Fact]
    public void Rebuilds_a_ruled_table()
    {
        var table = TableOf(Grid());

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(3, table.ColumnWidthsTwips.Count);
        Assert.All(table.Rows, row => Assert.Equal(3, row.Cells.Count));
    }

    [Fact]
    public void Puts_each_cell_of_a_row_in_its_own_cell()
    {
        var table = TableOf(Grid());

        Assert.Equal("Name", CellText(table.Rows[0].Cells[0]));
        Assert.Equal("Role", CellText(table.Rows[0].Cells[1]));
        Assert.Equal("Year", CellText(table.Rows[0].Cells[2]));
        Assert.Equal("Ada", CellText(table.Rows[1].Cells[0]));
        Assert.Equal("Analyst", CellText(table.Rows[1].Cells[1]));
        Assert.Equal("1843", CellText(table.Rows[1].Cells[2]));
        Assert.Equal("Logician", CellText(table.Rows[2].Cells[1]));
    }

    [Fact]
    public void Marks_a_bold_first_row_as_a_header()
    {
        var table = TableOf(Grid());
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.Rows[1].IsHeader);
    }

    [Fact]
    public void Column_widths_follow_the_ruled_grid()
    {
        var table = TableOf(Grid());

        // 0.30 and 0.20 of a 612 pt page are 183.6 pt and 122.4 pt, which is 3672 and 2448 twips.
        Assert.InRange(table.ColumnWidthsTwips[0], 3600, 3740);
        Assert.InRange(table.ColumnWidthsTwips[1], 3600, 3740);
        Assert.InRange(table.ColumnWidthsTwips[2], 2400, 2500);
    }

    [Fact]
    public void A_missing_interior_rule_makes_the_cell_span_two_columns()
    {
        var table = TableOf(Grid(fullHeightMiddleRule: false));

        Assert.Equal(3, table.Rows[0].Cells.Count);
        Assert.Equal(2, table.Rows[2].Cells.Count);
        Assert.Equal(2, table.Rows[2].Cells[0].ColumnSpan);
        Assert.Contains("Alan", CellText(table.Rows[2].Cells[0]));
        Assert.Contains("Logician", CellText(table.Rows[2].Cells[0]));
    }

    [Fact]
    public void The_table_text_is_not_also_left_in_the_body()
    {
        var blocks = ContentComposer.Compose([Grid().Build()]).Sections.SelectMany(s => s.Blocks).ToList();
        string loose = string.Concat(blocks.OfType<DocxParagraph>().Select(p => p.Text));

        Assert.DoesNotContain("Logician", loose);
        Assert.Single(blocks.OfType<DocxTable>());
    }

    [Fact]
    public void Leaves_the_table_as_paragraphs_when_the_option_is_off()
    {
        var blocks = ContentComposer.Compose([Grid().Build()], options: ExportOptions.Default with { Tables = false })
            .Sections.SelectMany(s => s.Blocks).ToList();

        Assert.Empty(blocks.OfType<DocxTable>());
        Assert.Contains("Logician", string.Concat(blocks.OfType<DocxParagraph>().Select(p => p.Text)));
    }

    [Fact]
    public void A_single_ruled_box_is_not_a_table()
    {
        // One box around a paragraph: two horizontal rules and two vertical ones, and no interior grid.
        var page = new PageBuilder().Line("A boxed note that is not a table at all", 0.15, 0.12, 0.8);
        page.Rule(new RectD(0.10, 0.099, 0.90, 0.101), horizontal: true);
        page.Rule(new RectD(0.10, 0.179, 0.90, 0.181), horizontal: true);
        page.Rule(new RectD(0.099, 0.10, 0.101, 0.18), horizontal: false);
        page.Rule(new RectD(0.899, 0.10, 0.901, 0.18), horizontal: false);

        var blocks = ContentComposer.Compose([page.Build()]).Sections.SelectMany(s => s.Blocks).ToList();
        Assert.Empty(blocks.OfType<DocxTable>());
        Assert.Contains("boxed note", string.Concat(blocks.OfType<DocxParagraph>().Select(p => p.Text)));
    }

    [Fact]
    public void An_underline_on_its_own_is_not_a_table()
    {
        var page = new PageBuilder().Line("A heading with a rule under it", 0.12, 0.10, 0.6);
        page.Rule(new RectD(0.12, 0.129, 0.60, 0.131), horizontal: true);

        var blocks = ContentComposer.Compose([page.Build()]).Sections.SelectMany(s => s.Blocks).ToList();
        Assert.Empty(blocks.OfType<DocxTable>());
    }

    [Fact]
    public void The_written_package_holds_the_table()
    {
        using var package = DocxPackage.Write(ContentComposer.Compose([Grid().Build()]));
        package.AssertValid();

        var table = Assert.Single(package.Tables);
        Assert.Contains("Logician", table.ToString());
    }

    [Fact]
    public void Keeps_every_character_of_a_page_with_a_table()
    {
        var content = Grid().Build();
        string expected = new([.. content.Text.Text.Where(c => !char.IsWhiteSpace(c))]);

        var document = ContentComposer.Compose([content]);
        using var package = DocxPackage.Write(document);
        package.AssertValid();

        string actual = new([.. package.AllText().Where(c => !char.IsWhiteSpace(c))]);
        Assert.Equal(expected, actual);
    }
}
