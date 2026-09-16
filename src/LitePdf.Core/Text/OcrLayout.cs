using System.Text;
using LitePdf.Core.Imaging;

namespace LitePdf.Core.Text;

/// <summary>A fraction whose halves have been recognized on their own, ready to drop back into the line.</summary>
public sealed record RecognizedFraction(FractionRegion Region, string Numerator, string Denominator)
{
    public bool IsEmpty => Numerator.Length == 0 && Denominator.Length == 0;

    public string Text => Numerator.Length == 0 ? Denominator
        : Denominator.Length == 0 ? Numerator
        : $"{Numerator}/{Denominator}";
}

/// <summary>
/// Rebuilds a page out of what a recognizer returned. Recognizers read a page as a bag of horizontal lines:
/// stacked fractions arrive as unrelated fragments, exponents and degree signs arrive as ordinary digits,
/// diagram labels arrive interleaved with the prose, and the lines themselves arrive in no useful order.
/// Everything here is decided from the measured geometry of the ink, not from the words.
/// </summary>
public static class OcrLayout
{
    /// <summary>Text this much shorter than the line's capitals may be an exponent rather than a letter.</summary>
    private const double ScriptHeightRatio = 0.78;

    /// <summary>A superscript clears the baseline by about this much of a capital; lower case clears none.</summary>
    private const double SuperscriptRise = 0.3;

    /// <summary>
    /// A subscript hangs below the baseline. Lower case rests exactly on it, so requiring a real drop is what
    /// keeps ordinary words out: without it every short letter after a tall one reads as an index.
    /// </summary>
    private const double SubscriptDrop = 0.15;

    /// <summary>Below this many full-height glyphs the line's baseline is a guess, so scripts are left alone.</summary>
    private const int MinGlyphsForScripts = 3;

    /// <summary>Marks where a diagram sat, so copied text keeps its place.</summary>
    public const string FigurePlaceholder = "[Figure]";

    public static OcrPageResult Rebuild(string language, IReadOnlyList<OcrLine> raw, ScanPage page,
        PageStructure structure, IReadOnlyList<RecognizedFraction> fractions, IReadOnlyList<RectD> figures,
        OcrOptions options)
    {
        double glyph = structure.GlyphHeight > 0 ? structure.GlyphHeight : 0.012;
        var fragments = new List<Fragment>();

        foreach (var line in raw)
            foreach (var word in line.Words)
            {
                string text = word.Text.Trim();
                if (text.Length == 0) continue;
                fragments.Add(new Fragment(text, word.Bounds, structure.GlyphBoxes(word.Bounds, text.Length)));
            }

        if (options.ReconstructMath)
            foreach (var fraction in fractions)
            {
                if (fraction.IsEmpty) continue;
                fragments.Add(new Fragment(fraction.Text, fraction.Region.Bounds, null)
                {
                    Band = Grow(fraction.Region.Bar, glyph * 0.3),
                    IsStack = true,
                });
            }

        var blocks = new List<Block>();

        var figureFragments = new List<Fragment>[figures.Count];
        var body = new List<Fragment>();
        foreach (var fragment in fragments)
        {
            int index = IndexOfFigure(figures, fragment.Bounds);
            if (index < 0) body.Add(fragment);
            else (figureFragments[index] ??= []).Add(fragment);
        }

        foreach (var line in GroupLines(body, structure, glyph, options.RebuildReadingOrder))
            blocks.Add(new Block(Bounds(line), BuildLine(line, glyph, options, OcrLineKind.Text)));

        for (int i = 0; i < figures.Count; i++)
        {
            var area = figures[i];
            blocks.Add(new Block(area, new OcrLine([new OcrWord(FigurePlaceholder, area)], OcrLineKind.Figure)));
            foreach (var line in GroupLines(figureFragments[i] ?? [], structure, glyph, reorder: false))
                blocks.Add(new Block(Bounds(line), BuildLine(line, glyph, options, OcrLineKind.FigureLabel)));
        }

        if (options.RebuildReadingOrder) SortIntoReadingOrder(blocks, structure, glyph);

        var lines = new List<OcrLine>(blocks.Count);
        foreach (var block in blocks)
            if (block.Line.Words.Count > 0) lines.Add(MapToSource(block.Line, page));

        return new OcrPageResult(language, lines, figures.Select(page.ToSource).ToList());
    }

    // ---- Lines ----

    /// <summary>
    /// Groups fragments into visual lines, walking left to right and comparing each one against the fragment
    /// already at the end of a line rather than against that line as a whole. Two reasons: a band that grew
    /// with every fragment would soon reach its neighbours and chain whole paragraphs into one line, and a
    /// baseline that drifts across a photographed page stays matchable when only adjacent ink is compared.
    /// A rebuilt fraction offers the band around its bar, so it joins its line instead of straddling three.
    /// </summary>
    private static List<List<Fragment>> GroupLines(List<Fragment> fragments, PageStructure structure, double glyph, bool reorder)
    {
        var lines = new List<List<Fragment>>();
        if (fragments.Count == 0) return lines;

        var ends = new List<Fragment>();
        var columns = new List<int>();
        foreach (var fragment in fragments.OrderBy(f => f.Bounds.Left))
        {
            int column = reorder ? structure.ColumnAt(fragment.Bounds.Center.X) : 0;
            int best = -1;
            double bestOverlap = 0.5;
            for (int i = 0; i < lines.Count; i++)
            {
                if (columns[i] != column) continue;
                var end = ends[i];
                // The next word on a line starts after the last one, give or take a whisker of overlap.
                if (fragment.Bounds.Left < end.Bounds.Right - glyph * 0.5) continue;

                double overlap = Math.Min(end.Band.Bottom, fragment.Band.Bottom) - Math.Max(end.Band.Top, fragment.Band.Top);
                double smaller = Math.Min(end.Band.Height, fragment.Band.Height);
                if (smaller <= 0) continue;
                double ratio = overlap / smaller;
                if (ratio > bestOverlap)
                {
                    bestOverlap = ratio;
                    best = i;
                }
            }

            if (best < 0)
            {
                lines.Add([fragment]);
                ends.Add(fragment);
                columns.Add(column);
            }
            else
            {
                lines[best].Add(fragment);
                // A stacked fraction is not a good yardstick for what follows it; keep the last ordinary word.
                if (!fragment.IsStack || ends[best].IsStack) ends[best] = fragment;
            }
        }

        foreach (var line in lines) line.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
        return lines;
    }

    private static void SortIntoReadingOrder(List<Block> blocks, PageStructure structure, double glyph)
    {
        blocks.Sort((a, b) =>
        {
            int column = structure.ColumnAt(a.Bounds.Center.X).CompareTo(structure.ColumnAt(b.Bounds.Center.X));
            if (column != 0) return column;
            // Lines sharing a row (answer options, a label beside a figure) read left to right.
            if (Math.Abs(a.Bounds.Top - b.Bounds.Top) < glyph * 0.6) return a.Bounds.Left.CompareTo(b.Bounds.Left);
            return a.Bounds.Top.CompareTo(b.Bounds.Top);
        });
    }

    private static OcrLine BuildLine(List<Fragment> fragments, double glyph, OcrOptions options, OcrLineKind kind)
    {
        if (fragments.Count == 0) return new OcrLine([], kind);
        if (!options.ReconstructMath)
            return new OcrLine(fragments.Select(f => new OcrWord(f.Text, f.Bounds, f.Chars)).ToList(), kind);

        var baseline = Baseline.Fit(fragments, glyph);
        if (baseline.Count < MinGlyphsForScripts)
            return new OcrLine(fragments.Select(f => new OcrWord(f.Text, f.Bounds, f.Chars)).ToList(), kind);

        var words = new List<OcrWord>(fragments.Count);
        foreach (var fragment in fragments)
        {
            var (text, boxes) = Compose(fragment, baseline);
            if (text.Length > 0) words.Add(new OcrWord(text, fragment.Bounds, boxes));
        }

        RepairSymbols(words);
        return new OcrLine(words, kind);
    }

    // ---- Superscripts, subscripts and degree signs ----

    private enum Script
    {
        Normal,
        Super,
        Sub,
    }

    /// <summary>
    /// Where a line of text rests and how tall its capitals are.
    /// <para>
    /// The resting height is read locally, from the glyphs nearest along the line, rather than as one value or
    /// one sloping line. A page photographed out of a bound book does not merely tilt: its baselines curve, and
    /// against any straight reference the glyphs at one end of a line all look raised and read as exponents.
    /// </para>
    /// </summary>
    private sealed class Baseline
    {
        /// <summary>How many neighbours decide the local resting height. Enough to outvote a descender.</summary>
        private const int Window = 5;

        private readonly double[] _x, _y;

        private Baseline(double height, double[] x, double[] y)
        {
            Height = height;
            _x = x;
            _y = y;
        }

        /// <summary>Height of a full-size glyph on this line.</summary>
        public double Height { get; }

        /// <summary>How many full-height glyphs the resting height was measured from.</summary>
        public int Count => _x.Length;

        /// <summary>Where the line rests at this point across the page.</summary>
        public double At(double x)
        {
            if (_x.Length == 0) return 0;
            int at = Array.BinarySearch(_x, x);
            if (at < 0) at = ~at;

            int lo = at, hi = at;
            var near = new List<double>(Window);
            while (near.Count < Window && (lo > 0 || hi < _x.Length))
            {
                bool takeLow = hi >= _x.Length || (lo > 0 && x - _x[lo - 1] <= _x[hi] - x);
                near.Add(takeLow ? _y[--lo] : _y[hi++]);
            }
            near.Sort();
            return near[near.Count / 2];
        }

        /// <summary>Letters that hang below the baseline, which would pull a resting height down with them.</summary>
        private static bool Descends(char c) => c is 'g' or 'j' or 'p' or 'q' or 'y' or 'Q' or 'J' or ',' or ';' or 'µ';

        public static Baseline Fit(List<Fragment> fragments, double glyph)
        {
            var glyphs = new List<(double X, double Bottom, double Height, bool Rests)>();
            foreach (var fragment in fragments)
            {
                // A rebuilt fraction is two lines of type tall and rests nowhere; it would tilt the whole fit.
                if (fragment.IsStack) continue;
                if (fragment.Chars is { } chars && chars.Count == fragment.Text.Length)
                    for (int i = 0; i < chars.Count; i++)
                        glyphs.Add((chars[i].Center.X, chars[i].Bottom, chars[i].Height, !Descends(fragment.Text[i])));
                else
                    glyphs.Add((fragment.Bounds.Center.X, fragment.Bounds.Bottom, fragment.Bounds.Height,
                        !fragment.Text.Any(Descends)));
            }
            if (glyphs.Count == 0) return new Baseline(glyph, [], []);

            // Most glyphs on a line are lower case, so the median would measure the x-height. The tall ones set
            // the scale that an exponent or a degree sign has to be small against.
            var sorted = glyphs.Select(g => g.Height).Order().ToList();
            double height = sorted[Math.Min(sorted.Count - 1, sorted.Count * 4 / 5)];

            // Commas, exponents and specks sit off the baseline, and a descender hangs below it; what is left
            // is the ink that actually rests on the line.
            var resting = glyphs.Where(g => g.Rests && g.Height >= height * 0.7).ToList();
            if (resting.Count == 0) resting = glyphs.Where(g => g.Height >= height * 0.7).ToList();
            if (resting.Count == 0) resting = glyphs;
            resting.Sort((a, b) => a.X.CompareTo(b.X));

            return new Baseline(height,
                resting.Select(g => g.X).ToArray(),
                resting.Select(g => g.Bottom).ToArray());
        }
    }

    /// <summary>
    /// Rewrites a word so raised and lowered glyphs read as such: "x3" becomes "x^3", "90o" becomes "90°".
    /// Falls back to the word as recognized whenever the glyphs could not be measured one to one.
    /// </summary>
    private static (string Text, IReadOnlyList<RectD>? Boxes) Compose(Fragment fragment, Baseline baseline)
    {
        var chars = fragment.Chars;
        if (chars is null || chars.Count != fragment.Text.Length || baseline.Height <= 0)
            return (fragment.Text, chars);

        var scripts = new Script[chars.Count];
        int marked = 0;
        for (int i = 0; i < chars.Count; i++)
        {
            scripts[i] = Classify(fragment.Text[i], chars[i], baseline);
            if (scripts[i] != Script.Normal) marked++;
        }
        if (marked == 0) return (fragment.Text, chars);

        // Most of a word is never set as a script. When it looks that way the line's resting height is wrong,
        // and marking it up would damage an ordinary word rather than reveal an expression.
        if (chars.Count >= 3 && marked > chars.Count / 2) return (fragment.Text, chars);

        var text = new StringBuilder();
        var boxes = new List<RectD>();
        for (int i = 0; i < chars.Count;)
        {
            if (scripts[i] == Script.Normal)
            {
                text.Append(fragment.Text[i]);
                boxes.Add(chars[i]);
                i++;
                continue;
            }

            int end = i;
            while (end < chars.Count && scripts[end] == scripts[i]) end++;
            string run = fragment.Text[i..end];
            var span = chars[i];
            for (int k = i + 1; k < end; k++) span = span.Union(chars[k]);

            char before = i > 0 ? fragment.Text[i - 1] : '\0';
            bool degree = scripts[i] == Script.Super && IsDegreeMark(run) && char.IsAsciiDigit(before);
            // An index only follows something to be indexed; a raised digit can start a term on its own.
            bool script = run.All(char.IsAsciiDigit) &&
                (scripts[i] == Script.Super || char.IsLetter(before) || char.IsAsciiDigit(before));

            if (!degree && !script)
            {
                text.Append(run);
                for (int k = i; k < end; k++) boxes.Add(chars[k]);
                i = end;
                continue;
            }

            if (degree)
            {
                text.Append('°');
                boxes.Add(span);
            }
            else
            {
                text.Append(scripts[i] == Script.Super ? '^' : '_');
                boxes.Add(span);
                if (run.Length > 1)
                {
                    text.Append('(');
                    boxes.Add(span);
                }
                for (int k = i; k < end; k++)
                {
                    text.Append(fragment.Text[k]);
                    boxes.Add(chars[k]);
                }
                if (run.Length > 1)
                {
                    text.Append(')');
                    boxes.Add(span);
                }
            }
            i = end;
        }
        return (text.ToString(), boxes);
    }

    /// <summary>
    /// Whether a glyph sits off the line's baseline. Only digits and the small ring that stands in for a degree
    /// sign are considered: an exponent or an index in a scanned book is nearly always a digit, whereas a short
    /// letter that measures as raised is nearly always the baseline being off by a pixel or two, and marking it
    /// up would corrupt an ordinary word.
    /// </summary>
    private static Script Classify(char c, RectD box, Baseline baseline)
    {
        if (!char.IsAsciiDigit(c) && !IsRing(c)) return Script.Normal;
        if (box.Height >= baseline.Height * ScriptHeightRatio) return Script.Normal;

        double rest = baseline.At(box.Center.X);
        if (box.Bottom < rest - baseline.Height * SuperscriptRise) return Script.Super;
        if (box.Bottom > rest + baseline.Height * SubscriptDrop && box.Top > rest - baseline.Height * 0.5)
            return Script.Sub;
        return Script.Normal;
    }

    /// <summary>A raised ring after a number is a degree sign, however the recognizer spelled it.</summary>
    private static bool IsDegreeMark(string run) => run.Length == 1 && IsRing(run[0]);

    private static bool IsRing(char c) => c is '0' or 'o' or 'O' or '°' or 'º';

    // ---- Symbol repair ----

    /// <summary>
    /// Two fixes for shapes no recognizer has a glyph for. Both are deliberately narrow: they only fire on
    /// letter pairs that cannot occur in ordinary words, so prose is never rewritten.
    /// </summary>
    private static void RepairSymbols(List<OcrWord> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            string text = words[i].Text;
            string fixedText = ReplacePi(text);

            // "LBAC =" is an angle: a lone L or Z in front of point names, which type never produces.
            if (fixedText.Length is 3 or 4 && (fixedText[0] is 'L' or 'Z' or 'l') &&
                fixedText.Skip(1).All(char.IsAsciiLetterUpper) &&
                i + 1 < words.Count && words[i + 1].Text.StartsWith('='))
                fixedText = '∠' + fixedText[1..];

            if (fixedText != text) words[i] = words[i] with { Text = fixedText, CharBounds = Rebox(words[i], fixedText) };
        }
    }

    /// <summary>Pi is read as a pair of upright strokes; no English word contains these pairs between symbols.</summary>
    private static readonly string[] PiSpellings = ["TT", "Tt", "tt", "1t", "It", "lt", "7t"];

    private static string ReplacePi(string text)
    {
        // Only inside an expression: a token that is doing arithmetic, not a word.
        if (text.Length < 3 || !text.Any(c => c is '(' or ')' or '-' or '+' or '−') || !text.Any(char.IsDigit))
            return text;

        foreach (string spelling in PiSpellings)
        {
            int at = text.IndexOf(spelling, StringComparison.Ordinal);
            while (at >= 0)
            {
                bool isolated = (at == 0 || !char.IsLetter(text[at - 1])) &&
                                (at + spelling.Length >= text.Length || !char.IsLetter(text[at + spelling.Length]));
                if (isolated) return text[..at] + 'π' + text[(at + spelling.Length)..];
                at = text.IndexOf(spelling, at + 1, StringComparison.Ordinal);
            }
        }
        return text;
    }

    /// <summary>Spreads the word's boxes over a replacement of a different length.</summary>
    private static IReadOnlyList<RectD>? Rebox(OcrWord word, string text)
    {
        if (word.CharBounds is null) return null;
        var boxes = new RectD[text.Length];
        double width = word.Bounds.Width / Math.Max(1, text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            double left = word.Bounds.Left + i * width;
            boxes[i] = new RectD(left, word.Bounds.Top, left + width, word.Bounds.Bottom);
        }
        return boxes;
    }

    // ---- Helpers ----

    private static int IndexOfFigure(IReadOnlyList<RectD> figures, RectD bounds)
    {
        var center = bounds.Center;
        for (int i = 0; i < figures.Count; i++)
            if (figures[i].Contains(center)) return i;
        return -1;
    }

    private static OcrLine MapToSource(OcrLine line, ScanPage page)
    {
        if (!page.IsDeskewed) return line;
        return line with
        {
            Words = line.Words
                .Select(w => new OcrWord(w.Text, page.ToSource(w.Bounds), w.CharBounds?.Select(page.ToSource).ToList()))
                .ToList(),
        };
    }

    private static RectD Bounds(List<Fragment> line) =>
        line.Aggregate(RectD.Empty, (acc, f) => acc.Union(f.Bounds));

    private static RectD Grow(RectD r, double dy) => new(r.Left, r.Top - dy, r.Right, r.Bottom + dy);

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    private sealed record Block(RectD Bounds, OcrLine Line);

    private sealed class Fragment(string text, RectD bounds, IReadOnlyList<RectD>? chars)
    {
        public string Text { get; } = text;

        public RectD Bounds { get; } = bounds;

        public IReadOnlyList<RectD>? Chars { get; } = chars;

        /// <summary>The vertical band used to decide which line this belongs to.</summary>
        public RectD Band { get; init; } = bounds;

        /// <summary>A rebuilt stacked fraction rather than a run of glyphs on the baseline.</summary>
        public bool IsStack { get; init; }
    }
}
