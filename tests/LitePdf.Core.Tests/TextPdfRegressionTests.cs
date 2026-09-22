using System.Xml.Linq;
using LitePdf.Core.Export;
using LitePdf.Core.Text;

namespace LitePdf.Core.Tests;

public sealed class TextPdfRegressionTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Fact]
    public void Paper_size_changes_do_not_insert_an_extra_blank_paragraph()
    {
        var portrait = new PageBuilder().Line("Portrait page", 0.1, 0.2, 0.7).Build();
        var landscape = new PageBuilder { Size = new PageSize(792, 612) }
            .Line("Landscape page", 0.1, 0.2, 0.7).Build(1);
        using var package = DocxPackage.Write(ContentComposer.Compose([portrait, landscape]));
        package.AssertValid();
        Assert.Equal(2, package.Document.Descendants(W + "sectPr").Count());
        Assert.Equal(2, package.Document.Descendants(W + "p").Count());
        Assert.NotNull(package.Document.Descendants(W + "p").First().Element(W + "pPr")?.Element(W + "sectPr"));
    }

    [Fact]
    public void Layout_mode_keeps_editable_lines_and_source_page_boundaries()
    {
        var pages = Enumerable.Range(0, 3).Select(p => new PageBuilder()
            .Line($"Page {p + 1}, first full line of text.", 0.1, 0.2, 0.9)
            .Line($"Page {p + 1}, second full line of text.", 0.1, 0.223, 0.9)
            .Build(p)).ToList();
        var options = ExportOptions.Default with { PreserveLineBreaks = true, PreservePageBreaks = true };
        using var package = DocxPackage.Write(ContentComposer.Compose(pages, options: options));
        package.AssertValid();
        Assert.Equal(3, package.Document.Descendants(W + "br").Count());
        Assert.Equal(2, package.Document.Descendants(W + "pageBreakBefore").Count());
        Assert.All(package.Document.Descendants(W + "spacing"), spacing =>
            Assert.Equal("0", (string?)spacing.Attribute(W + "after")));
        Assert.DoesNotContain(package.Document.Descendants(W + "jc"), jc => (string?)jc.Attribute(W + "val") == "both");
    }

    [Fact]
    public void Reads_columns_that_share_each_visual_line_in_column_order()
    {
        var page = new PageBuilder();
        for (int i = 0; i < 6; i++)
            page.Row(0.15 + i * 0.03, 0.016, null,
                ($"Left column contains sentence {i}.", 0.1, 0.43),
                ($"Right column contains sentence {i}.", 0.57, 0.9));
        using var package = DocxPackage.Write(ContentComposer.Compose([page.Build()]));
        string text = package.AllText();
        Assert.True(text.IndexOf("Left column contains sentence 5.", StringComparison.Ordinal) <
                    text.IndexOf("Right column contains sentence 0.", StringComparison.Ordinal));
        for (int i = 0; i < 6; i++)
        {
            Assert.Contains($"Left column contains sentence {i}.", text);
            Assert.Contains($"Right column contains sentence {i}.", text);
        }
        package.AssertValid();
    }

    [Fact]
    public void Keeps_literal_figure_text_in_a_text_pdf()
    {
        using var package = DocxPackage.Write(ContentComposer.Compose([
            new PageBuilder().Line("[Figure]", 0.1, 0.2, 0.25).Build()]));
        Assert.Contains("[Figure]", package.AllText());
    }

    [Fact]
    public void Does_not_join_paragraphs_across_skipped_pages()
    {
        var first = new PageBuilder().Line("First page stops here", 0.1, 0.2, 0.8).Build(0);
        var third = new PageBuilder().Line("unrelated text from the third page", 0.1, 0.2, 0.8).Build(2);
        var paragraphs = ContentComposer.Compose([first, third]).Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>();
        Assert.Equal(2, paragraphs.Count());
    }

    [Fact]
    public void Keeps_compound_word_hyphens_without_inserting_spaces()
    {
        var page = new PageBuilder()
            .Line("This is a well-", 0.1, 0.2, 0.9)
            .Line("known fact about the world.", 0.1, 0.223, 0.9).Build();
        using var package = DocxPackage.Write(ContentComposer.Compose([page]));
        Assert.Contains("well-known", package.AllText());
    }

    [Fact]
    public void Keeps_other_repeating_header_lines_in_the_body()
    {
        var pages = Enumerable.Range(0, 6).Select(p => new PageBuilder()
            .Line("Document title", 0.1, 0.02, 0.3)
            .Line("Second header line", 0.1, 0.05, 0.33)
            .Line("Long body text runs across the width of the printed page.", 0.1, 0.2, 0.9).Build(p)).ToList();
        var document = ContentComposer.Compose(pages);
        Assert.NotNull(document.Sections[0].Header);
        var paragraphs = document.Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>();
        Assert.Equal(6, paragraphs.Count(p => p.Text.Contains("Second header line", StringComparison.Ordinal)));
    }

    [Fact]
    public void Header_changes_after_the_first_five_pages_are_preserved()
    {
        var pages = Enumerable.Range(0, 12).Select(p => new PageBuilder()
            .Line($"Section {p / 6 + 1}, page {p + 1}", 0.1, 0.04, 0.4)
            .Line("Long body text runs across the width of the printed page.", 0.1, 0.2, 0.9).Build(p)).ToList();
        using var package = DocxPackage.Write(ContentComposer.Compose(pages));
        Assert.Contains("Section 2, page 12", package.AllText());
    }

    [Theory]
    [InlineData("7.", "8.", "decimal", "7", "%1.")]
    [InlineData("A)", "B)", "upperLetter", "1", "%1)")]
    [InlineData("(iv)", "(v)", "lowerRoman", "4", "(%1)")]
    [InlineData("007.", "008.", "none", "1", "007.")]
    public void Word_numbering_preserves_printed_labels(string first, string second, string format, string start, string label)
    {
        var page = new PageBuilder()
            .Line(first + " First list item", 0.1, 0.2, 0.5)
            .Line(second + " Second list item", 0.1, 0.25, 0.5)
            .Line(first + " Restarted list item", 0.1, 0.35, 0.5).Build();
        using var package = DocxPackage.Write(ContentComposer.Compose([page]));
        package.AssertValid();
        var numbering = package.Xml("word/numbering.xml");
        var ids = package.Document.Descendants(W + "numId").Select(n => (string)n.Attribute(W + "val")!).ToList();
        Assert.Equal(3, ids.Count);
        Assert.NotEqual(ids[0], ids[2]);
        if (format is "decimal" or "upperLetter") Assert.Equal(ids[0], ids[1]);
        foreach (int index in new[] { 0, 2 })
        {
            var num = numbering.Descendants(W + "num").Single(n => (string?)n.Attribute(W + "numId") == ids[index]);
            string abstractId = (string)num.Element(W + "abstractNumId")!.Attribute(W + "val")!;
            var level = numbering.Descendants(W + "abstractNum").Single(n => (string?)n.Attribute(W + "abstractNumId") == abstractId).Element(W + "lvl")!;
            Assert.Equal(format, (string?)level.Element(W + "numFmt")!.Attribute(W + "val"));
            Assert.Equal(start, (string?)level.Element(W + "start")!.Attribute(W + "val"));
            Assert.Equal(label, (string?)level.Element(W + "lvlText")!.Attribute(W + "val"));
        }
    }

    [Fact]
    public void Export_accepts_sparse_text_and_illustrations_but_rejects_scans()
    {
        var text = new PageBuilder().Line("Hi", 0.1, 0.1, 0.2).Image(new RectD(0.2, 0.2, 0.6, 0.6)).Build();
        TextPdfExport.Validate([text, PageContent.Empty(1, text.Size)]);
        var scan = new PageBuilder().Image(new RectD(0, 0, 1, 1)).Build(4);
        Assert.Contains("Page 5", Assert.Throws<NotSupportedException>(() => TextPdfExport.Validate([text, scan])).Message);
        Assert.Throws<NotSupportedException>(() => TextPdfExport.Validate([text with { IsSearchableScan = true }]));
        Assert.Throws<NotSupportedException>(() => TextPdfExport.Validate([PageContent.Empty(0, text.Size)]));
        Assert.Throws<NotSupportedException>(() => TextPdfExport.Validate([text with
        {
            Text = PageText.Create(0, TextSource.Ocr, "", []),
        }]));
    }
}
