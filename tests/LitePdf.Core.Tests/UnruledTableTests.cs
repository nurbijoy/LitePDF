using LitePdf.Core.Export;

namespace LitePdf.Core.Tests;

/// <summary>
/// The detector for tables that draw no lines. Half of these tests are about what it must <i>not</i> find:
/// turning prose into a grid is the one way this export can damage a document rather than approximate it.
/// </summary>
public sealed class UnruledTableTests
{
    private static readonly TextStyle Bold = new("Calibri", 11, true, false, 0x000000);

    private static readonly ExportOptions On = ExportOptions.Default with { UnruledTables = true };

    private static PageContent Columns(int rows)
    {
        var page = new PageBuilder()
            .Row(0.10, 0.02, Bold, ("Quarter", 0.10, 0.20), ("Orders", 0.40, 0.49), ("Change", 0.70, 0.79));
        for (int r = 1; r < rows; r++)
        {
            page.Row(0.10 + r * 0.04, 0.02, null,
                ($"Q{r}", 0.10, 0.14), ($"{r}00", 0.40, 0.45), ($"+{r}%", 0.70, 0.74));
        }
        return page.Build();
    }

    private static DocxTable? TableOf(PageContent page, ExportOptions? options = null) =>
        ContentComposer.Compose([page], null, null, options ?? On).AllBlocks.OfType<DocxTable>().FirstOrDefault();

    [Fact]
    public void Finds_three_rows_of_aligned_columns()
    {
        var table = TableOf(Columns(4));
        Assert.NotNull(table);
        Assert.Equal(4, table!.Rows.Count);
        Assert.Equal(3, table.ColumnWidthsTwips.Count);
        Assert.All(table.Rows, row => Assert.Equal(3, row.Cells.Count));
        Assert.False(table.HasBorders, "the PDF drew no lines, so neither should Word");
        Assert.Equal("Quarter", Text(table, 0, 0));
        Assert.Equal("+3%", Text(table, 3, 2));
    }

    [Fact]
    public void Leaves_the_text_alone_unless_the_export_asks_for_it()
    {
        Assert.Null(TableOf(Columns(4), ExportOptions.Default));

        var paragraphs = ContentComposer.Compose([Columns(4)]).AllBlocks.OfType<DocxParagraph>().ToList();
        Assert.Contains(paragraphs, p => p.Text.Contains("Quarter"));
    }

    [Fact]
    public void Refuses_two_rows()
    {
        Assert.Null(TableOf(Columns(2)));
    }

    [Fact]
    public void Refuses_ordinary_prose()
    {
        var page = new PageBuilder()
            .Line("The question this section sets out to answer is not whether the effect", 0.10, 0.10, 0.88)
            .Line("can be observed at all, which has never been in doubt, but whether it", 0.10, 0.13, 0.88)
            .Line("survives once the obvious confounds have been removed from it.", 0.10, 0.16, 0.84)
            .Line("A second paragraph begins here and runs to very nearly the same", 0.10, 0.19, 0.86)
            .Build();

        Assert.Null(TableOf(page));
    }

    [Fact]
    public void Refuses_columns_that_do_not_line_up()
    {
        var page = new PageBuilder()
            .Row(0.10, 0.02, null, ("Alpha", 0.10, 0.16), ("one", 0.40, 0.45))
            .Row(0.14, 0.02, null, ("Beta", 0.10, 0.15), ("two", 0.52, 0.57))
            .Row(0.18, 0.02, null, ("Gamma", 0.10, 0.17), ("three", 0.31, 0.38))
            .Build();

        Assert.Null(TableOf(page));
    }

    [Fact]
    public void Refuses_a_cell_long_enough_to_be_a_sentence()
    {
        var page = new PageBuilder()
            .Row(0.10, 0.02, null, ("Note", 0.10, 0.16),
                ("A line of commentary long enough that it is plainly a sentence rather than a cell of a table", 0.30, 0.90))
            .Row(0.14, 0.02, null, ("Note", 0.10, 0.16),
                ("Another line of commentary just as long, which lines up only because both were set flush", 0.30, 0.90))
            .Row(0.18, 0.02, null, ("Note", 0.10, 0.16),
                ("A third line of commentary, still far too long for anything anyone would call a table cell", 0.30, 0.90))
            .Build();

        Assert.Null(TableOf(page));
    }

    [Fact]
    public void Leaves_a_ruled_table_to_the_ruled_detector()
    {
        // Both detectors see this one; the ruled grid is trusted, and the table is emitted only once.
        var page = new PageBuilder()
            .Row(0.11, 0.02, Bold, ("Name", 0.12, 0.20), ("Role", 0.42, 0.50), ("Year", 0.72, 0.80))
            .Row(0.16, 0.02, null, ("Ada", 0.12, 0.18), ("Analyst", 0.42, 0.52), ("1843", 0.72, 0.80))
            .Row(0.21, 0.02, null, ("Alan", 0.12, 0.19), ("Logician", 0.42, 0.53), ("1936", 0.72, 0.80));

        foreach (double y in (double[])[0.10, 0.15, 0.20, 0.25])
            page.Rule(new RectD(0.10, y - 0.001, 0.90, y + 0.001), true);
        foreach (double x in (double[])[0.10, 0.40, 0.70, 0.90])
            page.Rule(new RectD(x - 0.001, 0.10, x + 0.001, 0.25), false);

        var tables = ContentComposer.Compose([page.Build()], null, null, On).AllBlocks.OfType<DocxTable>().ToList();
        var table = Assert.Single(tables);
        Assert.True(table.HasBorders);
    }

    private static string Text(DocxTable table, int row, int column) =>
        string.Concat(table.Rows[row].Cells[column].Blocks.OfType<DocxParagraph>().Select(p => p.Text));
}
