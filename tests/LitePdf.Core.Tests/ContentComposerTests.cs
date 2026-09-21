using LitePdf.Core.Export;

namespace LitePdf.Core.Tests;

public sealed class ContentComposerTests
{
    // A Letter page with one-inch margins: text runs from 72/612 to 540/612, 11 pt on an 18 pt pitch.
    private const double Left = 0.1176;
    private const double Right = 0.8824;
    private const double Height = 0.0167;
    private const double Pitch = 0.0227;

    private static double Top(int line) => 0.1 + line * Pitch;

    private static IReadOnlyList<DocxParagraph> Compose(PageBuilder page, ExportOptions? options = null,
        IReadOnlyList<OutlineItem>? outline = null)
    {
        var document = ContentComposer.Compose([page.Build()], null, outline, options);
        return [.. document.Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>()];
    }

    [Fact]
    public void Joins_the_lines_of_one_paragraph()
    {
        var page = new PageBuilder()
            .Line("The quick brown fox jumps over the lazy dog and keeps", Left, Top(0), Right)
            .Line("running until it reaches the far side of the meadow where", Left, Top(1), Right)
            .Line("the river bends.", Left, Top(2), 0.35);

        var paragraphs = Compose(page);
        var paragraph = Assert.Single(paragraphs);
        Assert.StartsWith("The quick brown fox", paragraph.Text);
        Assert.EndsWith("the river bends.", paragraph.Text);
        Assert.Contains("keeps running", paragraph.Text);
    }

    [Fact]
    public void A_line_that_stops_short_of_the_measure_ends_its_paragraph()
    {
        var page = new PageBuilder()
            .Line("First paragraph running the whole width of the measure", Left, Top(0), Right)
            .Line("and ending here.", Left, Top(1), 0.35)
            .Line("Second paragraph starting on the very next line of type", Left, Top(2), Right)
            .Line("and running on.", Left, Top(3), 0.35);

        var paragraphs = Compose(page);
        Assert.Equal(2, paragraphs.Count);
        Assert.EndsWith("ending here.", paragraphs[0].Text);
        Assert.StartsWith("Second paragraph", paragraphs[1].Text);
    }

    [Fact]
    public void A_wide_vertical_gap_starts_a_paragraph()
    {
        var page = new PageBuilder()
            .Line("One line of text that runs the full width of the measure", Left, Top(0), Right)
            .Line("and a second that also runs the full width of the measure", Left, Top(1), Right)
            .Line("then after a clear gap a new block of text begins here", Left, Top(1) + Pitch * 2.5, Right);

        var paragraphs = Compose(page);
        Assert.Equal(2, paragraphs.Count);
        Assert.StartsWith("then after a clear gap", paragraphs[1].Text);
    }

    [Fact]
    public void A_larger_line_becomes_a_heading()
    {
        var big = new TextStyle("Calibri", 20, true, false, 0x000000);
        var page = new PageBuilder()
            .Line("Chapter One", Left, Top(0), 0.32, 0.03, big)
            .Line("Body text that runs the whole width of the measure and on", Left, Top(2), Right)
            .Line("to a second line before it stops.", Left, Top(3), 0.5);

        var paragraphs = Compose(page);
        Assert.Equal(2, paragraphs.Count);
        Assert.Equal(DocxParagraphStyle.Heading1, paragraphs[0].Style);
        Assert.Equal("Chapter One", paragraphs[0].Text);
        Assert.Equal(DocxParagraphStyle.Body, paragraphs[1].Style);
    }

    [Fact]
    public void The_outline_sets_the_heading_level()
    {
        var big = new TextStyle("Calibri", 20, true, false, 0x000000);
        var page = new PageBuilder()
            .Line("Background", Left, Top(0), 0.32, 0.03, big)
            .Line("Body text that runs the whole width of the measure and on", Left, Top(2), Right)
            .Line("to a second line before it stops.", Left, Top(3), 0.5);

        // A bookmark two levels deep pointing at that line: the level is known, not guessed.
        var child = new OutlineItem("Background", new Destination(0, Top(0) + 0.01), false, []);
        var root = new OutlineItem("Introduction", new Destination(3, 0.2), true, [child]);

        var paragraphs = Compose(page, outline: [root]);
        Assert.Equal(DocxParagraphStyle.Heading2, paragraphs[0].Style);
    }

    [Fact]
    public void Headings_are_not_marked_when_the_option_is_off()
    {
        var big = new TextStyle("Calibri", 20, true, false, 0x000000);
        var page = new PageBuilder()
            .Line("Chapter One", Left, Top(0), 0.32, 0.03, big)
            .Line("Body text that runs the whole width of the measure and on", Left, Top(2), Right);

        var paragraphs = Compose(page, ExportOptions.Default with { Headings = false });
        Assert.All(paragraphs, p => Assert.Equal(DocxParagraphStyle.Body, p.Style));
    }

    [Fact]
    public void Bullets_become_a_list_and_the_marker_is_removed()
    {
        var page = new PageBuilder()
            .Line("• First item in the list", Left, Top(0), 0.45)
            .Line("• Second item in the list", Left, Top(1), 0.45)
            .Line("• Third item in the list", Left, Top(2), 0.45);

        var paragraphs = Compose(page);
        Assert.Equal(3, paragraphs.Count);
        Assert.All(paragraphs, p => Assert.Equal(DocxListKind.Bullet, p.List));
        Assert.Equal("First item in the list", paragraphs[0].Text);
        Assert.DoesNotContain("•", string.Concat(paragraphs.Select(p => p.Text)));
    }

    [Fact]
    public void Numbered_items_become_a_numbered_list()
    {
        var page = new PageBuilder()
            .Line("1. Read the instructions", Left, Top(0), 0.45)
            .Line("2. Follow the instructions", Left, Top(1), 0.45)
            .Line("3. Check the result", Left, Top(2), 0.45);

        var paragraphs = Compose(page);
        Assert.All(paragraphs, p => Assert.Equal(DocxListKind.Number, p.List));
        Assert.Equal("Read the instructions", paragraphs[0].Text);
    }

    [Fact]
    public void A_lone_numeral_at_the_start_of_prose_is_not_a_list()
    {
        var page = new PageBuilder()
            .Line("1914 was the year it began, and the whole of that summer", Left, Top(0), Right)
            .Line("passed without anyone noticing what had changed.", Left, Top(1), 0.6);

        var paragraphs = Compose(page);
        Assert.All(paragraphs, p => Assert.Equal(DocxListKind.None, p.List));
        Assert.StartsWith("1914 was the year", paragraphs[0].Text);
    }

    [Fact]
    public void Joins_a_word_broken_across_lines_by_a_hyphen()
    {
        var page = new PageBuilder()
            .Line("The committee reached an understand-", Left, Top(0), Right)
            .Line("ing before the end of the afternoon session and then left.", Left, Top(1), Right)
            .Line("It was over.", Left, Top(2), 0.3);

        var paragraph = Assert.Single(Compose(page));
        Assert.Contains("understanding before", paragraph.Text);
        Assert.DoesNotContain("understand- ", paragraph.Text);
    }

    [Fact]
    public void Keeps_a_real_hyphen_before_a_capital()
    {
        var page = new PageBuilder()
            .Line("They discussed the Anglo-", Left, Top(0), Right)
            .Line("German question for most of the afternoon before rising.", Left, Top(1), Right)
            .Line("Nothing was settled.", Left, Top(2), 0.3);

        var paragraph = Assert.Single(Compose(page));
        Assert.Contains("Anglo- German", paragraph.Text);
    }

    [Fact]
    public void Detects_centred_text()
    {
        var page = new PageBuilder()
            .Line("A line of body text running the whole width of the measure", Left, Top(0), Right)
            .Line("and stopping here.", Left, Top(1), 0.35)
            .Line("A centred line", 0.40, Top(3), 0.60)
            .Line("More body text running the whole width of the measure now", Left, Top(5), Right)
            .Line("and stopping.", Left, Top(6), 0.3);

        var centred = Compose(page).Single(p => p.Text == "A centred line");
        Assert.Equal(DocxAlignment.Center, centred.Alignment);
    }

    [Fact]
    public void Detects_justified_text()
    {
        var page = new PageBuilder()
            .Line("Justified text reaches both edges of the measure on every", Left, Top(0), Right)
            .Line("line except the last one, which is how it can be told from", Left, Top(1), Right)
            .Line("ragged-right setting at all.", Left, Top(2), 0.45);

        var paragraph = Assert.Single(Compose(page));
        Assert.Equal(DocxAlignment.Justify, paragraph.Alignment);
    }

    [Fact]
    public void Reads_two_columns_in_reading_order()
    {
        var page = new PageBuilder();
        for (int i = 0; i < 6; i++) page.Line($"Left column line {i + 1} of text", Left, Top(i), 0.47);
        for (int i = 0; i < 6; i++) page.Line($"Right column line {i + 1} of text", 0.53, Top(i), Right);

        string text = string.Concat(Compose(page).Select(p => p.Text));
        Assert.True(text.IndexOf("Left column line 6", StringComparison.Ordinal) <
                    text.IndexOf("Right column line 1", StringComparison.Ordinal),
            "the whole left column should be read before the right one");
    }

    [Fact]
    public void A_headline_over_two_columns_is_read_first()
    {
        var big = new TextStyle("Calibri", 20, true, false, 0x000000);
        var page = new PageBuilder().Line("The Headline Across The Page", Left, Top(0), Right, 0.03, big);
        for (int i = 0; i < 6; i++) page.Line($"Left column line {i + 1} of text", Left, Top(i + 2), 0.47);
        for (int i = 0; i < 6; i++) page.Line($"Right column line {i + 1} of text", 0.53, Top(i + 2), Right);

        var paragraphs = Compose(page);
        Assert.Equal("The Headline Across The Page", paragraphs[0].Text);
    }

    [Fact]
    public void Keeps_every_character_of_the_page()
    {
        var page = new PageBuilder()
            .Line("Alpha beta gamma delta epsilon zeta eta theta iota kappa", Left, Top(0), Right)
            .Line("lambda mu nu xi omicron pi rho sigma tau upsilon phi chi", Left, Top(1), Right)
            .Line("psi omega.", Left, Top(2), 0.3)
            .Line("A second paragraph after a clear gap in the page here.", Left, Top(4), Right);

        var content = page.Build();
        string expected = new([.. content.Text.Text.Where(c => !char.IsWhiteSpace(c))]);

        var document = ContentComposer.Compose([content]);
        string actual = new([.. document.Sections
            .SelectMany(s => s.Blocks).OfType<DocxParagraph>()
            .SelectMany(p => p.Text)
            .Where(c => !char.IsWhiteSpace(c))]);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Places_an_image_among_the_paragraphs()
    {
        var page = new PageBuilder()
            .Line("Text above the picture running the width of the measure", Left, Top(0), Right)
            .Line("and stopping here.", Left, Top(1), 0.3)
            .Image(new RectD(0.2, Top(3), 0.8, Top(3) + 0.2))
            .Line("Text below the picture running the width of the measure", Left, Top(12), Right);

        var blocks = ContentComposer.Compose([page.Build()]).Sections.SelectMany(s => s.Blocks).ToList();
        int picture = blocks.FindIndex(b => b is DocxPicture);

        Assert.True(picture > 0, "the picture should not come first");
        Assert.True(picture < blocks.Count - 1, "the picture should not come last");
        Assert.Contains("below the picture", ((DocxParagraph)blocks[^1]).Text);
    }

    [Fact]
    public void Leaves_images_out_when_the_option_is_off()
    {
        var page = new PageBuilder()
            .Line("Some text on the page that runs the width of the measure", Left, Top(0), Right)
            .Image(new RectD(0.2, Top(3), 0.8, Top(3) + 0.2));

        var blocks = ContentComposer.Compose([page.Build()], options: ExportOptions.Default with { Images = false })
            .Sections.SelectMany(s => s.Blocks).ToList();
        Assert.DoesNotContain(blocks, b => b is DocxPicture);
    }

    [Fact]
    public void Measures_the_body_size_and_the_margins()
    {
        var page = new PageBuilder()
            .Line("Body text on an ordinary page with one inch margins set", Left, Top(0), Right)
            .Line("across two lines of type.", Left, Top(1), 0.4);

        var document = ContentComposer.Compose([page.Build()]);
        var section = Assert.Single(document.Sections);

        Assert.Equal(11, document.BodySizePoints);
        Assert.InRange(section.Margins.Left, 66, 78);
        Assert.InRange(section.Margins.Right, 66, 78);
    }

    [Fact]
    public void Joins_a_paragraph_that_runs_over_a_page_break()
    {
        var first = new PageBuilder()
            .Line("The argument set out in the preceding chapter depends on", Left, Top(0), Right)
            .Line("a distinction that has not yet been made explicit, and", Left, Top(1), Right)
            .Build(0);
        var second = new PageBuilder()
            .Line("which the remainder of this section is devoted to drawing.", Left, Top(0), Right)
            .Line("It matters more than it appears.", Left, Top(1), 0.45)
            .Build(1);

        var paragraphs = ContentComposer.Compose([first, second])
            .Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>().ToList();

        Assert.Single(paragraphs);
        Assert.Contains("explicit, and which the remainder", paragraphs[0].Text);
    }

    [Fact]
    public void Does_not_join_across_a_page_break_when_the_sentence_ended()
    {
        var first = new PageBuilder()
            .Line("The argument set out in the preceding chapter is complete.", Left, Top(0), Right)
            .Build(0);
        var second = new PageBuilder()
            .Line("A new chapter opens with an entirely different question now.", Left, Top(0), Right)
            .Build(1);

        var paragraphs = ContentComposer.Compose([first, second])
            .Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>().ToList();
        Assert.Equal(2, paragraphs.Count);
    }

    [Fact]
    public void Drops_a_running_head_that_repeats_and_keeps_it_as_a_header()
    {
        var pages = new List<PageContent>();
        for (int p = 0; p < 6; p++)
        {
            pages.Add(new PageBuilder()
                .Line($"A Short History of Everything    {p + 1}", Left, 0.04, 0.55)
                .Line("Body text on this page running the whole width of the", Left, Top(2), Right)
                .Line("measure and then stopping here.", Left, Top(3), 0.45)
                .Build(p));
        }

        var document = ContentComposer.Compose(pages);
        string body = string.Concat(document.Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>().Select(p => p.Text));

        Assert.DoesNotContain("A Short History", body);
        Assert.NotNull(document.Sections[0].Header);
        Assert.Contains("A Short History", string.Concat(document.Sections[0].Header!.Runs.Select(r => r.Text)));
        Assert.True(document.Sections[0].Header!.PageNumberRun >= 0);
    }

    [Fact]
    public void Picks_the_page_number_out_of_a_running_head_that_has_two_numbers()
    {
        // "Section 1, page 1" has two numbers and on the first page they are both 1. Only the second one
        // tracks the page across the document, and only that one may become a PAGE field.
        var pages = new List<PageContent>();
        for (int p = 0; p < 8; p++)
        {
            pages.Add(new PageBuilder()
                .Line($"Section {p / 4 + 1}, page {p + 1}", Left, 0.04, 0.5)
                .Line("Body text on this page running the whole width of the", Left, Top(2), Right)
                .Build(p));
        }

        var header = ContentComposer.Compose(pages).Sections[0].Header;
        Assert.NotNull(header);
        Assert.Equal(1, header!.PageNumberRun);
        Assert.Equal("Section 1, page ", header.Runs[0].Text);
        Assert.Equal("1", header.Runs[1].Text);
    }

    [Fact]
    public void Carries_over_a_document_that_does_not_start_at_page_one()
    {
        var pages = new List<PageContent>();
        for (int p = 0; p < 8; p++)
        {
            pages.Add(new PageBuilder()
                .Line($"{p + 17}", Left, 0.95, 0.16)
                .Line("Body text on this page running the whole width of the", Left, Top(2), Right)
                .Build(p));
        }

        var section = ContentComposer.Compose(pages).Sections[0];
        Assert.NotNull(section.Footer);
        Assert.Equal(17, section.PageNumberStart);
    }

    [Fact]
    public void Keeps_a_running_head_in_the_body_when_the_option_is_off()
    {
        var pages = new List<PageContent>();
        for (int p = 0; p < 6; p++)
        {
            pages.Add(new PageBuilder()
                .Line($"A Short History of Everything    {p + 1}", Left, 0.04, 0.55)
                .Line("Body text on this page running the whole width of the", Left, Top(2), Right)
                .Build(p));
        }

        var document = ContentComposer.Compose(pages, options: ExportOptions.Default with { HeadersFooters = false });
        string body = string.Concat(document.Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>().Select(p => p.Text));

        Assert.Contains("A Short History", body);
        Assert.Null(document.Sections[0].Header);
    }

    [Fact]
    public void Turns_a_link_rectangle_into_a_hyperlink_run()
    {
        var page = new PageBuilder()
            .Line("Visit example.com for more of this sort of thing today ok", Left, Top(0), Right)
            .Build();

        // The link covers the first twelve characters of the line.
        double width = (Right - Left) / 56;
        var link = new PdfLink(new RectD(Left, Top(0) - 0.002, Left + width * 17, Top(0) + Height + 0.002),
            new Destination(-1), "https://example.com/");

        var document = ContentComposer.Compose([page], [new PageExtras([link], [])]);
        var paragraph = document.Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>().Single();

        var linked = paragraph.Runs.Where(r => r.Hyperlink is not null).ToList();
        Assert.NotEmpty(linked);
        Assert.StartsWith("Visit example", string.Concat(linked.Select(r => r.Text)));
    }

    [Fact]
    public void Turns_a_highlight_annotation_into_a_highlighted_run()
    {
        var page = new PageBuilder()
            .Line("Some of this line is highlighted in the original document", Left, Top(0), Right)
            .Build();

        double width = (Right - Left) / 57;
        var quad = new RectD(Left, Top(0) - 0.002, Left + width * 9, Top(0) + Height + 0.002);
        var annotation = new PdfAnnotation(0, 0, AnnotationKind.Highlight, quad, [quad],
            new AnnotationColor(0xFF, 0xE0, 0x3D), "");

        var document = ContentComposer.Compose([page], [new PageExtras([], [annotation])]);
        var paragraph = document.Sections.SelectMany(s => s.Blocks).OfType<DocxParagraph>().Single();

        var highlighted = paragraph.Runs.Where(r => r.Highlight is not null).ToList();
        Assert.NotEmpty(highlighted);
        Assert.StartsWith("Some of t", string.Concat(highlighted.Select(r => r.Text)));
    }

    [Fact]
    public void An_empty_document_still_produces_a_valid_package()
    {
        var document = ContentComposer.Compose([]);
        using var package = DocxPackage.Write(document);
        package.AssertValid();
    }
}

public sealed class FontMapperTests
{
    [Theory]
    [InlineData("ABCDEF+Times-Roman", "Times New Roman")]
    [InlineData("Times-BoldItalic", "Times New Roman")]
    [InlineData("Helvetica", "Arial")]
    [InlineData("Helvetica-Bold", "Arial")]
    [InlineData("ArialMT", "Arial")]
    [InlineData("Arial-BoldMT", "Arial")]
    [InlineData("Courier", "Courier New")]
    [InlineData("ZapfDingbats", "Wingdings")]
    [InlineData("GHIJKL+Garamond-Regular", "Garamond")]
    [InlineData("SomeUnknownFace", "SomeUnknownFace")]
    public void Maps_pdf_font_names_to_installed_families(string input, string expected) =>
        Assert.Equal(expected, FontMapper.Family(input, serifHint: false));

    [Theory]
    [InlineData("", false, "Calibri")]
    [InlineData("", true, "Times New Roman")]
    public void Falls_back_by_serif_flag(string input, bool serif, string expected) =>
        Assert.Equal(expected, FontMapper.Family(input, serif));

    [Theory]
    [InlineData("Times-Bold", true)]
    [InlineData("Arial-BoldMT", true)]
    [InlineData("Helvetica-Black", true)]
    [InlineData("Times-Roman", false)]
    public void Reads_weight_from_the_name(string input, bool bold) =>
        Assert.Equal(bold, FontMapper.NameSaysBold(input));

    [Theory]
    [InlineData("Times-Italic", true)]
    [InlineData("Helvetica-Oblique", true)]
    [InlineData("Times-Roman", false)]
    public void Reads_slope_from_the_name(string input, bool italic) =>
        Assert.Equal(italic, FontMapper.NameSaysItalic(input));
}
