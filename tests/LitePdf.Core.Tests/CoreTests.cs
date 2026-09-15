using LitePdf.Core.Imaging;
using LitePdf.Core.Layout;
using LitePdf.Core.Storage;
using LitePdf.Core.Text;

namespace LitePdf.Core.Tests;

public sealed class ViewMathTests
{
    [Fact]
    public void Zoom_steps_move_between_presets_and_clamp()
    {
        Assert.Equal(1.1, ViewMath.ZoomIn(1.0));
        Assert.Equal(0.9, ViewMath.ZoomOut(1.0));
        Assert.Equal(1.25, ViewMath.ZoomIn(1.17));
        Assert.Equal(ViewMath.MaxZoom, ViewMath.ZoomIn(ViewMath.MaxZoom));
        Assert.Equal(ViewMath.MinZoom, ViewMath.ZoomOut(ViewMath.MinZoom));
    }

    [Fact]
    public void Fit_width_and_page()
    {
        Assert.Equal(1.0, ViewMath.FitWidth(816, 612), 6);
        Assert.Equal(0.5, ViewMath.FitPage(1000, 528, 612, 792), 6);
    }

    [Theory]
    [InlineData(0, 100, true)]
    [InlineData(100, 102, false)]
    [InlineData(100, 110, true)]
    public void Staleness(int current, int target, bool stale) => Assert.Equal(stale, ViewMath.IsStale(current, target));

    [Fact]
    public void Clamp_keeps_aspect_ratio()
    {
        Assert.Equal((10000, 5000), ViewMath.ClampToMax(20000, 10000, 10000));
        var (w, h) = ViewMath.ClampToArea(4000, 2000, 2_000_000);
        Assert.True(w * h <= 2_000_000);
        Assert.Equal(2.0, (double)w / h, 2);
    }
}

public sealed class LayoutTests
{
    private static readonly PageSize Letter = new(612, 792);

    [Fact]
    public void Single_page_layout_stacks_pages_with_gaps_and_margins()
    {
        var layout = new DocumentLayout([Letter, Letter, Letter], zoom: 1, rotation: 0, PageLayoutMode.SinglePage, gap: 10, margin: 20);
        Assert.Equal(816 + 40, layout.Width, 6);
        Assert.Equal(20 + 1056 * 3 + 10 * 2 + 20, layout.Height, 6);
        var second = layout.GetPageRect(1);
        Assert.Equal(20, second.X, 6);
        Assert.Equal(20 + 1056 + 10, second.Y, 6);
    }

    [Fact]
    public void Visible_range_and_hit_testing()
    {
        var layout = new DocumentLayout(Enumerable.Repeat(Letter, 100).ToList(), 1, 0, PageLayoutMode.SinglePage, gap: 10, margin: 20);
        var r50 = layout.GetPageRect(50);
        Assert.Equal((50, 51), layout.GetPagesInRange(r50.Y + 5, r50.Bottom + 20));
        Assert.Equal(50, layout.HitTest(r50.X + 10, r50.Y + 10));
        Assert.Equal(-1, layout.HitTest(r50.X + 10, r50.Bottom + 5)); // in the gap
        Assert.Equal(50, layout.GetNearestPage(r50.X + 10, r50.Bottom + 3));
        Assert.Equal(51, layout.GetNearestPage(r50.X + 10, r50.Bottom + 8));
        Assert.Equal(0, layout.GetPageAtOffset(-100));
        Assert.Equal(99, layout.GetPageAtOffset(layout.Height + 100));
    }

    [Fact]
    public void Two_page_layout_places_pairs_side_by_side()
    {
        var layout = new DocumentLayout([Letter, Letter, Letter], 1, 0, PageLayoutMode.TwoPage, gap: 10, margin: 20);
        Assert.Equal(816 * 2 + 10 + 40, layout.Width, 6);
        Assert.Equal(layout.GetPageRect(0).Y, layout.GetPageRect(1).Y, 6);
        Assert.True(layout.GetPageRect(1).X > layout.GetPageRect(0).Right);
        Assert.Equal((816 - 0) / 2.0 + 20 + 816 / 2.0 + 5 - 816 / 2.0, layout.GetPageRect(2).X, 0); // last odd page centered
    }

    [Fact]
    public void Rotation_swaps_page_dimensions()
    {
        var layout = new DocumentLayout([Letter], 1, rotation: 1, PageLayoutMode.SinglePage);
        Assert.Equal(1056, layout.GetPageRect(0).Width, 6);
        Assert.Equal(816, layout.GetPageRect(0).Height, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void View_transform_round_trips(int rotation)
    {
        var p = new PointD(0.2, 0.7);
        var back = ViewTransform.FromView(ViewTransform.ToView(p, rotation), rotation);
        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Y, back.Y, 9);
    }

    [Fact]
    public void Quarter_turn_moves_top_left_to_top_right()
    {
        var v = ViewTransform.ToView(new PointD(0, 0), 1);
        Assert.Equal(new PointD(1, 0), v);
    }
}

public sealed class PageTextTests
{
    // Two lines: "Hello world" on top, "Second line" below; each char 0.01 wide.
    private static PageText Sample()
    {
        const string line1 = "Hello world", line2 = "Second line";
        string text = line1 + "\n" + line2;
        var boxes = new List<float>();
        for (int i = 0; i < line1.Length; i++) boxes.AddRange([0.1f + i * 0.01f, 0.10f, 0.11f + i * 0.01f, 0.12f]);
        boxes.AddRange([float.NaN, float.NaN, float.NaN, float.NaN]);
        for (int i = 0; i < line2.Length; i++) boxes.AddRange([0.1f + i * 0.01f, 0.15f, 0.11f + i * 0.01f, 0.17f]);
        return PageText.Create(0, TextSource.Pdf, text, boxes.ToArray());
    }

    [Fact]
    public void Builds_lines_and_hit_tests()
    {
        var t = Sample();
        Assert.Equal(2, t.Lines.Count);
        Assert.Equal((0, 11), (t.Lines[0].Start, t.Lines[0].End));
        Assert.Equal(0, t.HitTest(new PointD(0.105, 0.11)));
        Assert.Equal(12, t.HitTest(new PointD(0.105, 0.16)));
        Assert.Equal(-1, t.HitTest(new PointD(0.5, 0.5)));
    }

    [Fact]
    public void Caret_index_uses_character_midpoints()
    {
        var t = Sample();
        Assert.Equal(0, t.GetCaretIndex(new PointD(0.101, 0.11)));
        Assert.Equal(1, t.GetCaretIndex(new PointD(0.108, 0.11)));
        Assert.Equal(11, t.GetCaretIndex(new PointD(0.9, 0.11)));   // right of line 1 → end of line
        Assert.Equal(12, t.GetCaretIndex(new PointD(0.05, 0.16)));  // left of line 2 → its start
    }

    [Fact]
    public void Word_and_line_ranges()
    {
        var t = Sample();
        Assert.Equal((6, 11), t.GetWordRange(8));
        Assert.Equal((5, 6), t.GetWordRange(5));
        Assert.Equal((12, 23), t.GetLineRange(15));
        Assert.Equal("world\nSecond", t.GetText(6, 18));
    }

    [Fact]
    public void Range_rects_are_one_per_line()
    {
        var t = Sample();
        var rects = t.GetRangeRects(6, 18);
        Assert.Equal(2, rects.Count);
        Assert.Equal(0.16, rects[0].Left, 5);
        Assert.Equal(0.21, rects[0].Right, 5);
        Assert.Equal(0.15, rects[1].Top, 5);
    }

    [Fact]
    public void Text_in_rects_joins_runs()
    {
        var t = Sample();
        var rect = new RectD(0.155, 0.09, 0.3, 0.18);
        Assert.Equal("world d line", t.GetTextInRects([rect]));
    }

    [Fact]
    public void Vertical_text_forms_one_line_and_caret_follows_reading_direction()
    {
        // "ABC" rotated 90° clockwise on screen: characters stacked top-to-bottom.
        var boxes = new float[]
        {
            0.90f, 0.10f, 0.92f, 0.12f,
            0.90f, 0.12f, 0.92f, 0.14f,
            0.90f, 0.14f, 0.92f, 0.16f,
        };
        var t = PageText.Create(0, TextSource.Pdf, "ABC", boxes);
        Assert.Single(t.Lines);
        Assert.Single(t.GetRangeRects(0, 3));
        Assert.Equal(1, t.GetCaretIndex(new PointD(0.91, 0.125)));
        Assert.Equal(3, t.GetCaretIndex(new PointD(0.91, 0.30)));
    }

    [Fact]
    public void From_ocr_builds_words_spaces_and_lines()
    {
        var ocr = new OcrPageResult("en-US",
        [
            new OcrLine([new OcrWord("Hello", new RectD(0.1, 0.1, 0.2, 0.12)), new OcrWord("OCR", new RectD(0.22, 0.1, 0.28, 0.12))]),
            new OcrLine([new OcrWord("Next", new RectD(0.1, 0.2, 0.18, 0.22))]),
        ]);
        var t = PageText.FromOcr(3, ocr);
        Assert.Equal("Hello OCR\nNext", t.Text);
        Assert.Equal(TextSource.Ocr, t.Source);
        Assert.Equal(2, t.Lines.Count);
        Assert.True(t.TryGetBox(5, out var space));
        Assert.Equal(0.2, space.Left, 5);
        Assert.Equal(6, t.HitTest(new PointD(0.225, 0.11)));
    }

    [Fact]
    public void Selection_spans_pages()
    {
        var range = new TextRange(new TextPosition(3, 10), new TextPosition(1, 5));
        Assert.Equal(new TextPosition(1, 5), range.Start);
        Assert.Equal((5, 40), range.GetPageSpan(1, 40));
        Assert.Equal((0, 30), range.GetPageSpan(2, 30));
        Assert.Equal((0, 10), range.GetPageSpan(3, 50));
        Assert.Null(range.GetPageSpan(4, 50));
    }
}

public sealed class TextMatcherTests
{
    [Fact]
    public void Case_and_accent_insensitive_by_default()
    {
        var m = new TextMatcher("cafe");
        Assert.Single(m.FindAll("Visit the CAFÉ today"));
    }

    [Fact]
    public void Phrases_match_across_line_breaks()
    {
        var hits = new TextMatcher("brown fox").FindAll("quick brown\n  fox jumps");
        var hit = Assert.Single(hits);
        Assert.Equal(6, hit.Start);
        Assert.Equal("brown\n  fox".Length, hit.Length);
    }

    [Fact]
    public void Whole_word_and_match_case()
    {
        Assert.Single(new TextMatcher("cat", wholeWord: true).FindAll("cat concatenate cats"));
        Assert.Empty(new TextMatcher("Cat", matchCase: true).FindAll("cat"));
        Assert.Equal(3, new TextMatcher("cat").FindAll("cat concatenate cats").Count);
    }

    [Fact]
    public void Snippet_contains_match_with_context()
    {
        const string text = "0123456789 The needle is here 0123456789";
        var hit = new TextMatcher("needle").FindAll(text)[0];
        var (before, match, after) = TextMatcher.Snippet(text, hit, context: 8);
        Assert.Equal("needle", match);
        Assert.StartsWith("…", before);
        Assert.EndsWith("…", after);
    }

    [Fact]
    public void Snippet_stays_on_the_matching_line()
    {
        const string text = "first line here\nthe lazy dog sleeps\nnext line";
        var hit = new TextMatcher("lazy").FindAll(text)[0];
        var (before, match, after) = TextMatcher.Snippet(text, hit);
        Assert.Equal("the ", before);
        Assert.Equal("lazy", match);
        Assert.Equal(" dog sleeps", after);
    }
}

public sealed class BitmapOpsTests
{
    [Fact]
    public void Dark_mode_turns_white_dark_and_black_light()
    {
        var bmp = new RenderedBitmap(2, 1, [255, 255, 255, 255, 0, 0, 0, 255]);
        BitmapOps.ApplyColorMode(bmp, PageColorMode.Dark);
        Assert.True(bmp.Pixels[0] < 50 && bmp.Pixels[1] < 50 && bmp.Pixels[2] < 50);
        Assert.True(bmp.Pixels[4] > 200 && bmp.Pixels[5] > 200 && bmp.Pixels[6] > 200);
    }

    [Fact]
    public void Resize_and_crop()
    {
        var pixels = new byte[4 * 4 * 4];
        Array.Fill(pixels, (byte)128);
        var bmp = new RenderedBitmap(4, 4, pixels);
        var small = BitmapOps.Resize(bmp, 2, 2);
        Assert.Equal(16, small.Pixels.Length);
        Assert.Equal(128, small.Pixels[0]);
        var crop = BitmapOps.Crop(bmp, new PixelRect(1, 1, 2, 3));
        Assert.Equal((2, 3), (crop.Width, crop.Height));
    }
}

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LitePdfTests", "storage-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Json_file_round_trips_and_ignores_corrupt_files()
    {
        string path = Path.Combine(_dir, "settings.json");
        JsonFile.Write(path, new AppSettings { Theme = AppTheme.Dark, SidebarWidth = 300 });
        var loaded = JsonFile.Read<AppSettings>(path);
        Assert.Equal(AppTheme.Dark, loaded!.Theme);
        Assert.Equal(300, loaded.SidebarWidth);

        File.WriteAllText(path, "{ not json");
        Assert.Null(JsonFile.Read<AppSettings>(path));
    }

    [Fact]
    public void Document_key_is_stable_and_prefers_file_id()
    {
        string file = Path.Combine(_dir, "a.pdf");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        Assert.Equal(DocumentKey.Compute(file, null, 1), DocumentKey.Compute(file, null, 1));
        Assert.NotEqual(DocumentKey.Compute(file, null, 1), DocumentKey.Compute(file, "ABCD", 1));
        File.WriteAllBytes(file, [9, 9, 9, 9, 9]);
        Assert.Equal(DocumentKey.Compute(file, "ABCD", 1), DocumentKey.Compute(file, "ABCD", 1));
    }
}
