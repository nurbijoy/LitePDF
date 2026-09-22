using LitePdf.Core.Export;
using LitePdf.Core.Text;

namespace LitePdf.Core.Tests;

/// <summary>
/// What a scanned page has to become once its words have been recognized. The failure these pin down is
/// the one that made a ten-page scan into twenty pages of Word: the picture of the page written out
/// alongside the very text that was read off it.
/// </summary>
public sealed class RecognizedPageTests
{
    private static PageContent Scanned(params (string Text, double Top, double Height)[] lines)
    {
        var builder = new PageBuilder { Size = new PageSize(595, 842) };
        foreach (var (text, top, height) in lines)
            builder.Line(text, 0.08, top, 0.08 + text.Length * 0.011, height);

        var page = builder.Build();
        var ocr = PageText.Create(page.PageIndex, TextSource.Ocr, page.Text.Text, Boxes(page.Text));
        return page with { Text = ocr, Spans = [] };
    }

    private static float[] Boxes(PageText text)
    {
        var boxes = new float[text.Length * 4];
        for (int i = 0; i < text.Length; i++)
        {
            if (text.TryGetBox(i, out var box))
            {
                boxes[i * 4] = (float)box.Left;
                boxes[i * 4 + 1] = (float)box.Top;
                boxes[i * 4 + 2] = (float)box.Right;
                boxes[i * 4 + 3] = (float)box.Bottom;
            }
            else
            {
                for (int o = 0; o < 4; o++) boxes[i * 4 + o] = float.NaN;
            }
        }
        return boxes;
    }

    private static PlacedImage Picture(RectD bounds) =>
        PlacedImage.FromBits(bounds, new ImageBits(8, 8, new byte[8 * 8 * 4]));

    // ---- the scan itself ----

    [Fact]
    public void Knows_the_picture_a_scanned_page_is_made_of()
    {
        Assert.True(RecognizedPage.IsPageScan(Picture(new RectD(0, 0, 1, 1))));
        Assert.True(RecognizedPage.IsPageScan(Picture(new RectD(0.02, 0.02, 0.98, 0.98))));

        // An illustration on the page is not the page.
        Assert.False(RecognizedPage.IsPageScan(Picture(new RectD(0.2, 0.3, 0.8, 0.7))));

        // Nor is a drawing, which is artwork lifted out of a page that had text of its own.
        Assert.False(RecognizedPage.IsPageScan(Picture(new RectD(0, 0, 1, 1)) with { IsDrawing = true }));
    }

    [Fact]
    public void Never_writes_the_figure_placeholder_out_as_text()
    {
        var page = Scanned(
            ("A line of recognized text on the page.", 0.10, 0.02),
            (OcrLayout.FigurePlaceholder, 0.20, 0.12),
            ("A second line under the picture.", 0.40, 0.02));

        var document = ContentComposer.Compose(RecognizedPage.Style([page]));
        string text = string.Concat(document.AllBlocks.OfType<DocxParagraph>().Select(p => p.Text));

        Assert.DoesNotContain("[Figure]", text, StringComparison.Ordinal);
        Assert.Contains("A line of recognized text", text, StringComparison.Ordinal);
        Assert.Contains("A second line under the picture.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_where_the_pictures_of_a_recognized_page_are()
    {
        var page = Scanned(
            ("Text above the figure.", 0.10, 0.02),
            (OcrLayout.FigurePlaceholder, 0.30, 0.15));

        var figure = Assert.Single(RecognizedPage.Figures(page.Text));
        Assert.InRange(figure.Top, 0.29, 0.31);
        Assert.InRange(figure.Height, 0.14, 0.16);
    }

    // ---- type sizes ----

    [Fact]
    public void Gives_a_scan_one_body_size_across_every_page()
    {
        // Two pages of the same scan. The second opens with a banner, which used to drag that page's
        // whole body size up with it because each page guessed for itself.
        var first = Scanned(
            ("The first line of ordinary body text.", 0.10, 0.020),
            ("The second line of ordinary body text.", 0.14, 0.021),
            ("The third line of ordinary body text.", 0.18, 0.020));
        var second = Scanned(
            ("A BANNER ACROSS THE PAGE", 0.06, 0.050),
            ("More ordinary body text here.", 0.14, 0.020),
            ("And another line of body text.", 0.18, 0.021)) with
        { PageIndex = 1 };

        var styled = RecognizedPage.Style([first, second]);
        var sizes = styled.SelectMany(p => p.Spans).Select(s => s.Style.SizePoints).ToList();

        double body = styled[0].Spans[0].Style.SizePoints;
        Assert.InRange(body, 8, 20);
        Assert.All(styled[1].Spans.Skip(1), span => Assert.Equal(body, span.Style.SizePoints));

        // The banner is set larger, but nothing is set smaller than the body on either page.
        Assert.Contains(sizes, size => size > body);
        Assert.All(sizes, size => Assert.True(size >= body * 0.69, $"{size} is below the body size {body}"));
    }

    [Fact]
    public void Keeps_a_line_of_prose_at_the_body_size_however_tall_it_measures()
    {
        // A line of answers with a stacked fraction in it measures half as tall again as the type around
        // it. Sized from that box it came out at 32 pt in the middle of 13 pt prose.
        var page = Scanned(
            ("An ordinary line of body text to set the size.", 0.10, 0.020),
            ("Another ordinary line of body text on the page.", 0.14, 0.020),
            ("A third ordinary line of body text on this page.", 0.18, 0.021),
            ("A. x2-y2/2 B. x2+y2/2 C. x2+xy/2 D. x2-y2/4 E. None of these", 0.22, 0.045));

        var styled = Assert.Single(RecognizedPage.Style([page]));
        double body = styled.Spans[0].Style.SizePoints;
        Assert.All(styled.Spans, span => Assert.Equal(body, span.Style.SizePoints));
    }

    [Fact]
    public void Still_lets_a_short_tall_line_be_a_heading()
    {
        var page = Scanned(
            ("An ordinary line of body text to set the size.", 0.10, 0.020),
            ("Another ordinary line of body text on the page.", 0.14, 0.020),
            ("A third ordinary line of body text on this page.", 0.18, 0.020),
            ("Section 2", 0.22, 0.042));

        var styled = Assert.Single(RecognizedPage.Style([page]));
        double body = styled.Spans[0].Style.SizePoints;
        Assert.True(styled.Spans[^1].Style.SizePoints > body,
            $"the heading came out at {styled.Spans[^1].Style.SizePoints}, the body at {body}");
    }

    [Fact]
    public void Leaves_a_page_that_was_never_recognized_alone()
    {
        var page = new PageBuilder().Line("Text straight out of the PDF.", 0.1, 0.1, 0.6).Build();
        var styled = Assert.Single(RecognizedPage.Style([page]));
        Assert.Same(page, styled);
    }

    // ---- pictures fit the page ----

    [Fact]
    public void Shrinks_a_picture_that_is_taller_than_the_page_holds()
    {
        // A full-page scan kept as a picture, on a page whose margins leave 770 pt of height.
        var page = new PageBuilder { Size = new PageSize(595, 842) }
            .Line("A caption under the plate.", 0.1, 0.96, 0.5)
            .Build() with
        { Images = [Picture(new RectD(0.02, 0.02, 0.98, 0.99))] };

        var document = ContentComposer.Compose([page]);
        var section = document.Sections[0];
        var picture = Assert.Single(document.AllBlocks.OfType<DocxPicture>());

        double height = section.Size.Height - section.Margins.Top - section.Margins.Bottom;
        double width = section.Size.Width - section.Margins.Left - section.Margins.Right;

        Assert.True(picture.HeightPoints <= height,
            $"the picture is {picture.HeightPoints:F0} pt tall on a page that holds {height:F0} pt");
        Assert.True(picture.WidthPoints <= width);

        // And it keeps its shape while it shrinks.
        Assert.Equal(0.96 * 595 / (0.97 * 842), picture.WidthPoints / picture.HeightPoints, 2);
    }
}
