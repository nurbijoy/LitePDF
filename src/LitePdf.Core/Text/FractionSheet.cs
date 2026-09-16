using LitePdf.Core.Imaging;

namespace LitePdf.Core.Text;

/// <summary>
/// Every half of every stacked fraction on a page, cut out and laid side by side as ordinary lines of type.
/// <para>
/// A recognizer reads lines of words. Handed a lone digit it usually returns nothing at all, which is why a
/// fraction on a page comes back as a stray dash: the numerator and denominator are each a single character
/// surrounded by white paper. Laid out next to each other with word-sized gaps they read as a line and come
/// back reliably, and their position on the sheet says which fraction each one belongs to.
/// </para>
/// </summary>
public sealed class FractionSheet
{
    /// <summary>Halves are enlarged towards this height; the recognizer is least accurate on small type.</summary>
    private const int TargetHeight = 80;

    private readonly IReadOnlyList<FractionRegion> _fractions;
    private readonly RectD[] _slots;

    private FractionSheet(RenderedBitmap image, IReadOnlyList<FractionRegion> fractions, RectD[] slots)
    {
        Image = image;
        _fractions = fractions;
        _slots = slots;
    }

    /// <summary>The sheet to recognize, or null when there was nothing to lay out.</summary>
    public RenderedBitmap Image { get; }

    /// <summary>
    /// Cuts each half out of <paramref name="page"/> and arranges them in rows. Returns null when there are no
    /// fractions or none of them is big enough to be worth recognizing.
    /// </summary>
    /// <param name="maxDimension">Largest width or height the recognizer accepts.</param>
    public static FractionSheet? Build(RenderedBitmap page, IReadOnlyList<FractionRegion> fractions, int maxDimension)
    {
        if (fractions.Count == 0) return null;

        var crops = new List<RenderedBitmap>(fractions.Count * 2);
        foreach (var fraction in fractions)
        {
            crops.Add(Cut(page, fraction.Numerator));
            crops.Add(Cut(page, fraction.Denominator));
        }

        var heights = crops.Select(c => c.Height).Order().ToList();
        int median = Math.Max(1, heights[heights.Count / 2]);
        int scale = Math.Clamp(TargetHeight / median, 1, 4);
        if (scale > 1)
            for (int i = 0; i < crops.Count; i++)
                crops[i] = BitmapOps.Resize(crops[i], crops[i].Width * scale, crops[i].Height * scale);

        int cell = crops.Max(c => c.Height);
        // A word-sized gap, no more: spaced further apart the halves stop reading as a line and are dropped.
        int gap = Math.Max(8, median * scale * 4 / 5);
        int margin = Math.Max(8, cell / 2);
        int rowHeight = cell + gap;

        int widest = maxDimension - margin * 2;
        var rows = new List<List<int>>();
        var current = new List<int>();
        int used = 0;
        for (int i = 0; i < crops.Count; i++)
        {
            int advance = crops[i].Width + gap;
            // Keep the two halves of one fraction on the same row so they read as one pair of words.
            bool wrap = current.Count > 0 && i % 2 == 0 && used + advance * 2 > widest;
            if (wrap)
            {
                rows.Add(current);
                current = [];
                used = 0;
            }
            current.Add(i);
            used += advance;
        }
        if (current.Count > 0) rows.Add(current);

        int width = margin * 2 + rows.Max(r => r.Sum(i => crops[i].Width + gap) - gap);
        int height = margin * 2 + rows.Count * rowHeight - gap;
        if (width > maxDimension || height > maxDimension || width < 4 || height < 4) return null;

        var image = new RenderedBitmap(width, height, new byte[width * height * 4]);
        Array.Fill(image.Pixels, (byte)255);

        var slots = new RectD[crops.Count];
        int y = margin;
        foreach (var row in rows)
        {
            int x = margin;
            foreach (int i in row)
            {
                var crop = crops[i];
                int top = y + (cell - crop.Height) / 2;
                Blit(image, crop, x, top);
                // The slot reaches halfway into the gaps, so a word placed slightly off still lands in it.
                slots[i] = new RectD(
                    (double)(x - gap / 2) / width, (double)y / height,
                    (double)(x + crop.Width + gap / 2) / width, (double)(y + cell) / height);
                x += crop.Width + gap;
            }
            y += rowHeight;
        }
        return new FractionSheet(image, fractions, slots);
    }

    /// <summary>Reads the recognized words back off the sheet and pairs them into fractions.</summary>
    public IReadOnlyList<RecognizedFraction> Assign(IReadOnlyList<OcrLine> lines)
    {
        var parts = new string[_slots.Length];
        foreach (var line in lines)
            foreach (var word in line.Words)
            {
                int slot = IndexOfSlot(word.Bounds.Center);
                if (slot >= 0) parts[slot] = (parts[slot] ?? "") + word.Text.Trim();
            }

        var result = new List<RecognizedFraction>(_fractions.Count);
        for (int i = 0; i < _fractions.Count; i++)
            result.Add(new RecognizedFraction(_fractions[i], parts[i * 2] ?? "", parts[i * 2 + 1] ?? ""));
        return result;
    }

    private int IndexOfSlot(PointD center)
    {
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].Contains(center)) return i;
        return -1;
    }

    private static RenderedBitmap Cut(RenderedBitmap page, RectD area)
    {
        double pad = Math.Max(area.Height, area.Width) * 0.12;
        var padded = new RectD(area.Left - pad, area.Top - pad, area.Right + pad, area.Bottom + pad).ClampToUnit();
        var rect = new PixelRect(
            (int)(padded.Left * page.Width), (int)(padded.Top * page.Height),
            Math.Max(4, (int)Math.Ceiling(padded.Width * page.Width)),
            Math.Max(4, (int)Math.Ceiling(padded.Height * page.Height)));
        return BitmapOps.Crop(page, rect);
    }

    private static void Blit(RenderedBitmap destination, RenderedBitmap source, int x, int y)
    {
        for (int row = 0; row < source.Height; row++)
            Buffer.BlockCopy(source.Pixels, row * source.Width * 4,
                destination.Pixels, ((y + row) * destination.Width + x) * 4, source.Width * 4);
    }
}
