using LitePdf.Core.Imaging;

namespace LitePdf.Core.Text;

/// <summary>
/// A stacked fraction found on the page: the bar itself and the ink sitting directly above and below it.
/// The regions are recognized on their own, because a recognizer reading a whole page returns a stacked
/// fraction as unrelated fragments on three different lines (or, when the digits are small, as a stray dash).
/// </summary>
public sealed record FractionRegion(RectD Bar, RectD Numerator, RectD Denominator)
{
    public RectD Bounds => Bar.Union(Numerator).Union(Denominator);
}

public static class MathLayout
{
    /// <summary>
    /// How far above and below a bar to look for its numerator and denominator, in glyph heights. Measured to
    /// the centre of the ink, so a little over one glyph is enough; reaching further picks up the line beyond.
    /// </summary>
    private const double SearchHeight = 1.5;

    /// <summary>Widest bar that can still be a fraction, in glyph heights: longer means it is an underline.</summary>
    private const double MaxBarWidth = 14;

    /// <summary>How far the halves of a fraction may sit from its bar, in glyph heights.</summary>
    private const double MaxStackGap = 0.7;

    /// <summary>
    /// Thin horizontal segments with ink stacked directly above and below, centred on the bar. An underline has
    /// ink above only; an em dash has ink to the sides; a table rule is too long.
    /// </summary>
    public static List<FractionRegion> FindFractions(PageStructure structure)
    {
        var found = new List<FractionRegion>();
        double glyph = structure.GlyphHeight;
        if (glyph <= 0) return found;

        foreach (var bar in structure.Rules)
        {
            if (bar.Width > glyph * MaxBarWidth) continue;

            // Digits sit within the bar, so allow only a little overhang when looking for them.
            double pad = Math.Min(bar.Width * 0.25, glyph * 0.4);
            double reach = glyph * SearchHeight;
            var above = structure.InkBoundsIn(new RectD(bar.Left - pad, bar.Top - reach, bar.Right + pad, bar.Top));
            var below = structure.InkBoundsIn(new RectD(bar.Left - pad, bar.Bottom, bar.Right + pad, bar.Bottom + reach));
            if (above.IsEmpty || below.IsEmpty) continue;

            // Both halves must line up with the bar, not merely be near it.
            if (!IsCentered(above, bar) || !IsCentered(below, bar)) continue;
            // An equals sign is two bars of the same size stacked; a fraction has type above and below.
            if (above.Height < bar.Height * 2 || below.Height < bar.Height * 2) continue;
            // Type set over a fraction bar almost touches it, about half a glyph clear. The next line of a
            // paragraph is more than twice that away, which is what separates a fraction from an underline.
            if (bar.Top - above.Bottom > glyph * MaxStackGap || below.Top - bar.Bottom > glyph * MaxStackGap) continue;

            found.Add(new FractionRegion(bar, above, below));
        }

        found.Sort((a, b) => a.Bar.Top.CompareTo(b.Bar.Top));
        return found;
    }

    private static bool IsCentered(RectD part, RectD bar)
    {
        double overlap = Math.Min(part.Right, bar.Right) - Math.Max(part.Left, bar.Left);
        if (overlap <= 0) return false;
        if (overlap < Math.Min(part.Width, bar.Width) * 0.6) return false;
        // The bar is drawn to span the wider half, so a much wider part belongs to something else.
        return part.Width <= bar.Width * 1.6;
    }
}
