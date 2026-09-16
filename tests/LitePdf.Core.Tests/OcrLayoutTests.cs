using LitePdf.Core.Imaging;
using LitePdf.Core.Text;

namespace LitePdf.Core.Tests;

public sealed class PageStructureTests
{
    private const int GlyphWidth = 14;
    private const int GlyphHeight = 20;

    /// <summary>A page of plain lines of type, with whatever extra ink a test wants on top.</summary>
    private static (ScanPage Page, PageStructure Structure) Analyze(Action<TestPage>? extra = null, int rows = 10)
    {
        var page = new TestPage(600, 800);
        for (int row = 0; row < rows; row++)
            for (int i = 0; i < 18; i++)
                page.Glyph(40 + i * 24, 40 + row * 40, GlyphWidth, GlyphHeight);
        extra?.Invoke(page);

        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });
        return (scan, PageStructure.Analyze(scan, 0));
    }

    [Fact]
    public void Glyph_height_is_measured_from_the_ink()
    {
        var (page, structure) = Analyze();
        Assert.Equal(GlyphHeight, structure.GlyphHeight * page.Height, 0);
    }

    [Fact]
    public void A_thin_bar_is_a_rule_and_a_glyph_is_not()
    {
        var (_, structure) = Analyze(p => p.Fill(300, 500, 40, 4));

        Assert.Contains(structure.Rules, r => Math.Abs(r.Left * 600 - 300) < 2 && r.Width * 600 > 35);
        Assert.DoesNotContain(structure.Rules, r => r.Height * 800 > GlyphHeight * 0.5);
    }

    [Fact]
    public void Glyph_boxes_come_back_one_per_character()
    {
        var (page, structure) = Analyze();
        // The first three glyphs of the first row, as a recognizer would box them together.
        var word = new RectD(40.0 / 600, 40.0 / 800, (40 + 2 * 24 + GlyphWidth) / 600.0, (40 + GlyphHeight) / 800.0);

        var boxes = structure.GlyphBoxes(word, 3);

        Assert.NotNull(boxes);
        Assert.Equal(3, boxes.Count);
        Assert.True(boxes[0].Right < boxes[1].Left);
        Assert.Equal(40.0 / 600, boxes[0].Left, 3);
    }

    [Fact]
    public void Glyph_boxes_are_refused_when_the_count_does_not_match()
    {
        var (_, structure) = Analyze();
        var word = new RectD(40.0 / 600, 40.0 / 800, (40 + 2 * 24 + GlyphWidth) / 600.0, (40 + GlyphHeight) / 800.0);

        Assert.Null(structure.GlyphBoxes(word, 7));
    }

    [Fact]
    public void A_drawing_below_the_text_is_reported_as_a_figure()
    {
        var (_, structure) = Analyze(p => p.Outline(200, 520, 200, 150), rows: 8);
        var textLines = TextLines(rows: 8);

        var figures = structure.FindFigures(textLines);

        var figure = Assert.Single(figures);
        Assert.InRange(figure.Left * 600, 190, 210);
        Assert.InRange(figure.Bottom * 800, 660, 680);
    }

    [Fact]
    public void A_pen_stroke_over_a_line_of_prose_is_not_a_figure()
    {
        // A tall sparse mark in the margin, with prose running through and well past it.
        var (_, structure) = Analyze(p =>
        {
            for (int i = 0; i < 90; i++) p.Fill(20 + i / 2, 60 + i, 3, 3);
        });

        Assert.Empty(structure.FindFigures(TextLines()));
    }

    [Fact]
    public void A_ruled_table_is_text_rather_than_a_figure()
    {
        var page = new TestPage(600, 800);
        // Six ruled rows of figures: drawing-like ink, but full of type.
        for (int row = 0; row < 6; row++)
        {
            page.Fill(60, 300 + row * 40, 480, 3);
            for (int i = 0; i < 16; i++) page.Glyph(70 + i * 28, 308 + row * 40, GlyphWidth, GlyphHeight);
        }
        page.Fill(60, 300, 3, 240);
        page.Fill(537, 300, 3, 240);

        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });
        var structure = PageStructure.Analyze(scan, 0);
        var rows = Enumerable.Range(0, 6)
            .Select(r => new RectD(70.0 / 600, (308 + r * 40) / 800.0, 520.0 / 600, (328 + r * 40) / 800.0))
            .ToList();

        Assert.Empty(structure.FindFigures(rows));
    }

    [Fact]
    public void A_page_of_running_text_is_one_column()
    {
        var (_, structure) = Analyze();
        Assert.Single(structure.Columns);
    }

    [Fact]
    public void A_gutter_down_the_page_splits_it_into_columns()
    {
        var page = new TestPage(600, 800);
        for (int row = 0; row < 16; row++)
        {
            for (int i = 0; i < 8; i++) page.Glyph(40 + i * 24, 40 + row * 40, GlyphWidth, GlyphHeight);
            for (int i = 0; i < 8; i++) page.Glyph(340 + i * 24, 40 + row * 40, GlyphWidth, GlyphHeight);
        }

        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });
        var structure = PageStructure.Analyze(scan, 0);

        Assert.Equal(2, structure.Columns.Count);
        Assert.Equal(0, structure.ColumnAt(0.2));
        Assert.Equal(1, structure.ColumnAt(0.8));
    }

    private static List<RectD> TextLines(int rows = 10) => Enumerable.Range(0, rows)
        .Select(r => new RectD(40.0 / 600, (40 + r * 40) / 800.0, 448.0 / 600, (40 + r * 40 + GlyphHeight) / 800.0))
        .ToList();
}

public sealed class MathLayoutTests
{
    // Rows of type, spaced as printed text is: a 20px glyph on a 48px pitch.
    private const int Pitch = 48;

    private static PageStructure Analyze(TestPage page)
    {
        var scan = ScanPreprocessor.Prepare(page.Bitmap, new ScanPreprocessOptions { Deskew = false });
        return PageStructure.Analyze(scan, 0);
    }

    private static TestPage Body(int rows = 5)
    {
        var page = new TestPage(600, 400);
        for (int row = 0; row < rows; row++)
            for (int i = 0; i < 18; i++)
                page.Glyph(40 + i * 24, 30 + row * Pitch, 14, 20);
        return page;
    }

    [Fact]
    public void A_bar_with_type_above_and_below_is_a_fraction()
    {
        // A stacked fraction in the clear space below the body text.
        var page = Body();
        page.Glyph(300, 300, 14, 20);   // numerator
        page.Fill(298, 326, 18, 4);     // bar
        page.Glyph(300, 336, 14, 20);   // denominator

        var fraction = Assert.Single(MathLayout.FindFractions(Analyze(page)));

        Assert.InRange(fraction.Numerator.Top * 400, 295, 305);
        Assert.InRange(fraction.Denominator.Bottom * 400, 351, 361);
        Assert.True(fraction.Bounds.Height > fraction.Bar.Height * 4);
    }

    [Fact]
    public void An_equals_sign_is_not_a_fraction()
    {
        var page = Body();
        page.Fill(300, 300, 18, 4);
        page.Fill(300, 310, 18, 4);

        Assert.Empty(MathLayout.FindFractions(Analyze(page)));
    }

    [Fact]
    public void An_underline_under_a_word_is_not_a_fraction()
    {
        var page = Body();
        page.Fill(88, 54, 120, 3); // just under the first row of type

        Assert.Empty(MathLayout.FindFractions(Analyze(page)));
    }

    [Fact]
    public void A_bar_with_nothing_under_it_is_not_a_fraction()
    {
        var page = Body();
        page.Glyph(300, 300, 14, 20);
        page.Fill(298, 326, 18, 4);

        Assert.Empty(MathLayout.FindFractions(Analyze(page)));
    }
}

public sealed class FractionSheetTests
{
    [Fact]
    public void Halves_are_laid_out_and_read_back_in_order()
    {
        var page = new TestPage(400, 400);
        page.Glyph(100, 100, 14, 20);
        page.Glyph(100, 140, 14, 20);
        page.Glyph(200, 100, 14, 20);
        page.Glyph(200, 140, 14, 20);
        var regions = new List<FractionRegion>
        {
            new(Rect(98, 126, 18, 4), Rect(100, 100, 14, 20), Rect(100, 140, 14, 20)),
            new(Rect(198, 126, 18, 4), Rect(200, 100, 14, 20), Rect(200, 140, 14, 20)),
        };

        var sheet = FractionSheet.Build(page.Bitmap, regions, 10000);

        Assert.NotNull(sheet);
        // Each half is laid out separately, so the sheet is wider than it is tall.
        Assert.True(sheet.Image.Width > sheet.Image.Height);

        // Words placed across the sheet come back attached to the half they sit on.
        var words = ReadSlots(sheet, ["1", "2", "3", "4"]);
        var recognized = sheet.Assign(words);

        Assert.Equal("1/2", recognized[0].Text);
        Assert.Equal("3/4", recognized[1].Text);
    }

    [Fact]
    public void A_half_that_could_not_be_read_leaves_the_other_alone()
    {
        var page = new TestPage(400, 400);
        page.Glyph(100, 100, 14, 20);
        page.Glyph(100, 140, 14, 20);
        var regions = new List<FractionRegion>
        {
            new(Rect(98, 126, 18, 4), Rect(100, 100, 14, 20), Rect(100, 140, 14, 20)),
        };
        var sheet = FractionSheet.Build(page.Bitmap, regions, 10000);
        Assert.NotNull(sheet);

        var recognized = sheet.Assign(ReadSlots(sheet, ["7", null]));

        Assert.Equal("7", recognized[0].Text);
        Assert.False(recognized[0].IsEmpty);
    }

    [Fact]
    public void Nothing_to_lay_out_gives_no_sheet() =>
        Assert.Null(FractionSheet.Build(new TestPage(100, 100).Bitmap, [], 10000));

    private static RectD Rect(int x, int y, int w, int h) => new(x / 400.0, y / 400.0, (x + w) / 400.0, (y + h) / 400.0);

    /// <summary>Stands in for a recognizer: puts one word in the middle of each laid-out half.</summary>
    private static List<OcrLine> ReadSlots(FractionSheet sheet, string?[] texts)
    {
        // The halves run left to right across the sheet in the order they were added.
        var lines = new List<OcrLine>();
        double step = 1.0 / texts.Length;
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] is not { } text) continue;
            double center = (i + 0.5) * step;
            lines.Add(new OcrLine([new OcrWord(text, new RectD(center - 0.01, 0.45, center + 0.01, 0.55))]));
        }
        return lines;
    }
}

/// <summary>
/// Drives <see cref="OcrLayout"/> with words placed exactly where a recognizer would have found them on a page
/// the tests draw themselves, so the reconstruction is checked against real ink rather than against invented
/// geometry: the glyph boxes it works from are measured off that ink.
/// </summary>
public sealed class OcrLayoutRebuildTests
{
    private const int Width = 800;
    private const int Height = 600;
    private const int Glyph = 20;
    private const int Cap = 28;
    private const int Pitch = 60;

    private sealed class Sheet
    {
        private readonly TestPage _page = new(Width, Height);
        private readonly List<(string Text, List<(int X, int Y, int W, int H)> Boxes)> _words = [];

        /// <summary>Sets a word on a baseline, one block of ink per character, and remembers where it went.</summary>
        public Sheet Word(string text, int left, int baseline, int[]? heights = null)
        {
            var boxes = new List<(int, int, int, int)>();
            int x = left;
            for (int i = 0; i < text.Length; i++)
            {
                int h = heights?[i] ?? Cap;
                // A raised glyph is given a height that already clears the baseline.
                int top = baseline - h;
                int w = Math.Max(6, h * 14 / Cap);
                _page.Glyph(x, top, w, h);
                boxes.Add((x, top, w, h));
                x += w + 4;
            }
            _words.Add((text, boxes));
            return this;
        }

        /// <summary>Sets a raised run, as an exponent or a degree sign is printed.</summary>
        public Sheet Raised(string text, int left, int baseline, int rise, int size)
        {
            var boxes = new List<(int, int, int, int)>();
            int x = left;
            foreach (char _ in text)
            {
                _page.Glyph(x, baseline - rise - size, 10, size);
                boxes.Add((x, baseline - rise - size, 10, size));
                x += 14;
            }
            _words[^1] = (_words[^1].Text + text, [.. _words[^1].Boxes, .. boxes]);
            return this;
        }

        public Sheet Ink(Action<TestPage> draw)
        {
            draw(_page);
            return this;
        }

        public OcrPageResult Rebuild(OcrOptions? options = null)
        {
            var scan = ScanPreprocessor.Prepare(_page.Bitmap, new ScanPreprocessOptions { Deskew = false });
            var structure = PageStructure.Analyze(scan, 0);
            var raw = _words
                .Select(w => new OcrLine([new OcrWord(w.Text, Bounds(w.Boxes))]))
                .ToList();
            var figures = structure.FindFigures(raw.Select(l => l.Bounds).ToList());
            return OcrLayout.Rebuild("en-US", raw, scan, structure,
                MathLayout.FindFractions(structure)
                    .Select(f => new RecognizedFraction(f, "1", "2"))
                    .ToList(),
                figures, options ?? OcrOptions.Default);
        }

        private static RectD Bounds(List<(int X, int Y, int W, int H)> boxes)
        {
            int left = boxes.Min(b => b.X), top = boxes.Min(b => b.Y);
            int right = boxes.Max(b => b.X + b.W), bottom = boxes.Max(b => b.Y + b.H);
            return new RectD((double)left / Width, (double)top / Height, (double)right / Width, (double)bottom / Height);
        }
    }

    /// <summary>Rows of filler type, so a line has enough glyphs to measure a baseline from.</summary>
    private static Sheet Filled(int rows = 6)
    {
        var sheet = new Sheet();
        for (int row = 0; row < rows; row++)
            sheet.Word("mmmmmmmm", 40, 40 + row * Pitch);
        return sheet;
    }

    [Fact]
    public void A_raised_digit_becomes_an_exponent()
    {
        var result = Filled().Word("x", 400, 40).Raised("3", 420, 40, rise: Cap - 10, size: 14).Rebuild();

        Assert.Contains(result.Lines, l => l.Text.Contains("x^3"));
    }

    [Fact]
    public void A_raised_ring_after_a_number_becomes_a_degree_sign()
    {
        var result = Filled().Word("90", 400, 40).Raised("0", 450, 40, rise: Cap - 10, size: 12).Rebuild();

        Assert.Contains(result.Lines, l => l.Text.Contains("90°"));
        Assert.DoesNotContain(result.Lines, l => l.Text.Contains("900"));
    }

    [Fact]
    public void Ordinary_words_are_left_alone()
    {
        // Lower case rests on the baseline and is shorter than a capital; none of it is a script.
        var heights = new[] { Cap, Glyph, Glyph, Glyph };
        var result = Filled().Word("Type", 400, 40, heights).Rebuild();

        var line = Assert.Single(result.Lines, l => l.Text.Contains("Type"));
        Assert.DoesNotContain('^', line.Text);
        Assert.DoesNotContain('_', line.Text);
    }

    [Fact]
    public void Words_on_one_row_become_one_line_in_left_to_right_order()
    {
        var result = Filled(rows: 1).Word("beta", 400, 40).Word("alpha", 250, 40).Rebuild();

        var line = Assert.Single(result.Lines);
        Assert.Equal("mmmmmmmm alpha beta", line.Text);
    }

    [Fact]
    public void Rows_stay_apart_and_are_read_top_to_bottom()
    {
        var sheet = new Sheet();
        sheet.Word("second", 40, 40 + Pitch);
        sheet.Word("first", 40, 40);

        var result = sheet.Rebuild();

        Assert.Equal(["first", "second"], result.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_drawing_is_reported_as_a_figure_in_its_place()
    {
        var result = Filled(rows: 2)
            .Ink(p => p.Outline(300, 220, 260, 180))
            .Rebuild();

        Assert.Single(result.Figures);
        var figure = Assert.Single(result.Lines, l => l.Kind == OcrLineKind.Figure);
        Assert.Equal(OcrLayout.FigurePlaceholder, figure.Text);
        // The placeholder sits after the text above it and before the text below.
        int at = result.Lines.ToList().IndexOf(figure);
        Assert.Equal(2, at);
    }

    [Fact]
    public void Turning_the_analysis_off_leaves_the_recognizer_output_as_it_is()
    {
        var result = Filled().Word("x", 400, 40).Raised("3", 420, 40, rise: Cap - 10, size: 14)
            .Rebuild(OcrOptions.Plain with { Preprocess = true });

        Assert.Contains(result.Lines, l => l.Text.Contains("x3"));
        Assert.DoesNotContain(result.Lines, l => l.Text.Contains("x^3"));
    }
}
