namespace LitePdf.Core.Imaging;

/// <summary>Tuning for <see cref="ScanPreprocessor"/>.</summary>
public sealed record ScanPreprocessOptions
{
    /// <summary>Read strongly coloured pixels (pen, highlighter, stamps) as paper instead of ink.</summary>
    public bool SuppressColoredInk { get; init; } = true;

    /// <summary>Divide out uneven lighting and show-through from the reverse side of the sheet.</summary>
    public bool FlattenBackground { get; init; } = true;

    /// <summary>Straighten the page when the measured skew exceeds <see cref="MinSkewDegrees"/>.</summary>
    public bool Deskew { get; init; } = true;

    public double MaxSkewDegrees { get; init; } = 6;

    /// <summary>Below this the rotation costs more (resampling blur) than it gains.</summary>
    public double MinSkewDegrees { get; init; } = 0.3;

    public static ScanPreprocessOptions Default { get; } = new();
}

/// <summary>
/// A scanned page cleaned up for recognition: an 8-bit grey image whose paper is white and whose ink is black,
/// plus the ink mask that <see cref="PageStructure"/> measures. Deskewing rotates the image, so
/// <see cref="ToSource(RectD)"/> maps geometry found here back onto the page as it was rendered.
/// </summary>
public sealed class ScanPage
{
    private readonly double _cos, _sin;

    internal ScanPage(int width, int height, byte[] gray, byte[] ink, int inkThreshold, double skewDegrees)
    {
        Width = width;
        Height = height;
        Gray = gray;
        Ink = ink;
        InkThreshold = inkThreshold;
        SkewDegrees = skewDegrees;
        double radians = skewDegrees * Math.PI / 180;
        _cos = Math.Cos(radians);
        _sin = Math.Sin(radians);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>One byte per pixel: 0 = ink, 255 = paper.</summary>
    public byte[] Gray { get; }

    /// <summary>One byte per pixel: 1 = ink.</summary>
    public byte[] Ink { get; }

    /// <summary>Grey level separating ink from paper.</summary>
    public int InkThreshold { get; }

    /// <summary>Clockwise skew removed from the source, in degrees; 0 when the page was left alone.</summary>
    public double SkewDegrees { get; }

    public bool IsDeskewed => SkewDegrees != 0;

    /// <summary>BGRA copy, for engines that take a bitmap.</summary>
    public RenderedBitmap ToBitmap()
    {
        var pixels = new byte[Width * Height * 4];
        for (int i = 0, o = 0; i < Gray.Length; i++, o += 4)
        {
            byte v = Gray[i];
            pixels[o] = v;
            pixels[o + 1] = v;
            pixels[o + 2] = v;
            pixels[o + 3] = 255;
        }
        return new RenderedBitmap(Width, Height, pixels);
    }

    /// <summary>Maps a normalized point of this image back onto the page it was made from.</summary>
    public PointD ToSource(PointD p)
    {
        if (!IsDeskewed) return p;
        double x = p.X * Width - Width / 2.0, y = p.Y * Height - Height / 2.0;
        double sx = x * _cos - y * _sin, sy = x * _sin + y * _cos;
        return new PointD((sx + Width / 2.0) / Width, (sy + Height / 2.0) / Height);
    }

    /// <summary>Maps a normalized rectangle back, growing it to cover the rotated corners.</summary>
    public RectD ToSource(RectD r)
    {
        if (!IsDeskewed || r.IsEmpty) return r;
        var a = ToSource(new PointD(r.Left, r.Top));
        var b = ToSource(new PointD(r.Right, r.Top));
        var c = ToSource(new PointD(r.Right, r.Bottom));
        var d = ToSource(new PointD(r.Left, r.Bottom));
        return new RectD(
            Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)),
            Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
            Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)),
            Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)));
    }
}

/// <summary>
/// Turns a page rendering into something a recognizer can read: colour pen marks become paper, uneven scanner
/// lighting and show-through are divided out, the sheet is straightened, and contrast is stretched around the
/// measured ink/paper split so faint ghosting disappears without eroding real strokes.
/// </summary>
public static class ScanPreprocessor
{
    /// <summary>Pixels this far from neutral are pen or highlighter rather than print.</summary>
    private const int ChromaThreshold = 56;

    /// <summary>Background is sampled on a grid this many pixels across, which must exceed the stroke width.</summary>
    private const int BackgroundBlock = 8;

    public static ScanPage Prepare(RenderedBitmap source, ScanPreprocessOptions? options = null)
    {
        options ??= ScanPreprocessOptions.Default;
        int w = source.Width, h = source.Height;
        var gray = ToGray(source, options.SuppressColoredInk);
        if (options.FlattenBackground) FlattenBackground(gray, w, h);

        int threshold = OtsuThreshold(Histogram(gray));
        double skew = 0;
        if (options.Deskew)
        {
            skew = SkewEstimator.Estimate(gray, w, h, threshold, options.MaxSkewDegrees);
            if (Math.Abs(skew) >= options.MinSkewDegrees)
            {
                gray = Rotate(gray, w, h, -skew);
                threshold = OtsuThreshold(Histogram(gray));
            }
            else
            {
                skew = 0;
            }
        }

        Stretch(gray, threshold, Percentile(Histogram(gray), 0.95));
        return new ScanPage(w, h, gray, BuildMask(gray, 128), 128, skew);
    }

    /// <summary>
    /// Luminance, except where a pixel is strongly coloured: there the brightest channel is used, which turns
    /// pink and blue pen strokes into near-paper while leaving neutral print untouched.
    /// </summary>
    private static byte[] ToGray(RenderedBitmap source, bool suppressColoredInk)
    {
        var p = source.Pixels;
        var gray = new byte[source.Width * source.Height];
        for (int i = 0, o = 0; i < gray.Length; i++, o += 4)
        {
            int b = p[o], g = p[o + 1], r = p[o + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            gray[i] = suppressColoredInk && max - min >= ChromaThreshold
                ? (byte)max
                : (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }
        return gray;
    }

    /// <summary>
    /// Estimates the paper behind the text (brightest value per block, smoothed) and divides it out, so a
    /// shadowed corner or a grey show-through band no longer drags a whole region below the ink threshold.
    /// </summary>
    private static void FlattenBackground(byte[] gray, int w, int h)
    {
        int bw = (w + BackgroundBlock - 1) / BackgroundBlock, bh = (h + BackgroundBlock - 1) / BackgroundBlock;
        if (bw < 4 || bh < 4) return;

        var background = new byte[bw * bh];
        for (int by = 0; by < bh; by++)
        {
            int y0 = by * BackgroundBlock, y1 = Math.Min(y0 + BackgroundBlock, h);
            for (int bx = 0; bx < bw; bx++)
            {
                int x0 = bx * BackgroundBlock, x1 = Math.Min(x0 + BackgroundBlock, w);
                int max = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * w;
                    for (int x = x0; x < x1; x++)
                        if (gray[row + x] > max) max = gray[row + x];
                }
                background[by * bw + bx] = (byte)max;
            }
        }

        // Smooth, so a block landing wholly inside a thick stroke or a filled figure borrows its neighbours' paper.
        background = BoxBlur(background, bw, bh, radius: 6);

        // A large dark area (a solid diagram) must not be normalized into paper: hold the estimate near the
        // page's own paper level.
        int floor = Math.Max(1, Percentile(Histogram(background), 0.90) / 2);

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            double gy = Math.Min((y + 0.5) / BackgroundBlock - 0.5, bh - 1.001);
            int y0 = Math.Max(0, (int)gy), y1 = Math.Min(y0 + 1, bh - 1);
            double wy = Math.Max(0, gy - y0);
            for (int x = 0; x < w; x++)
            {
                double gx = Math.Min((x + 0.5) / BackgroundBlock - 0.5, bw - 1.001);
                int x0 = Math.Max(0, (int)gx), x1 = Math.Min(x0 + 1, bw - 1);
                double wx = Math.Max(0, gx - x0);
                double top = background[y0 * bw + x0] * (1 - wx) + background[y0 * bw + x1] * wx;
                double bottom = background[y1 * bw + x0] * (1 - wx) + background[y1 * bw + x1] * wx;
                double level = Math.Max(floor, top * (1 - wy) + bottom * wy);
                gray[row + x] = (byte)Math.Min(255, gray[row + x] * 255.0 / level);
            }
        }
    }

    /// <summary>Separable box blur with running sums.</summary>
    private static byte[] BoxBlur(byte[] src, int w, int h, int radius)
    {
        radius = Math.Max(1, Math.Min(radius, Math.Min(w, h) / 2));
        int window = radius * 2 + 1;
        var tmp = new byte[src.Length];
        var dst = new byte[src.Length];

        for (int y = 0; y < h; y++)
        {
            int row = y * w, sum = 0;
            for (int x = -radius; x <= radius; x++) sum += src[row + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = (byte)(sum / window);
                sum += src[row + Math.Clamp(x + radius + 1, 0, w - 1)] - src[row + Math.Clamp(x - radius, 0, w - 1)];
            }
        }

        for (int x = 0; x < w; x++)
        {
            int sum = 0;
            for (int y = -radius; y <= radius; y++) sum += tmp[Math.Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = (byte)(sum / window);
                sum += tmp[Math.Clamp(y + radius + 1, 0, h - 1) * w + x] - tmp[Math.Clamp(y - radius, 0, h - 1) * w + x];
            }
        }
        return dst;
    }

    /// <summary>
    /// Maps the measured ink/paper split to mid grey and clips beyond it: anything much lighter becomes pure
    /// white (show-through, scanner noise), anything much darker becomes solid black.
    /// </summary>
    private static void Stretch(byte[] gray, int threshold, int paper)
    {
        int spread = Math.Clamp((int)((paper - threshold) * 0.55), 12, 90);
        int lo = Math.Max(0, threshold - spread), hi = Math.Min(255, threshold + spread);
        if (hi <= lo) return;
        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
            lut[v] = v <= lo ? (byte)0 : v >= hi ? (byte)255 : (byte)((v - lo) * 255 / (hi - lo));
        for (int i = 0; i < gray.Length; i++) gray[i] = lut[gray[i]];
    }

    private static byte[] BuildMask(byte[] gray, int threshold)
    {
        var ink = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++) ink[i] = gray[i] < threshold ? (byte)1 : (byte)0;
        return ink;
    }

    private static byte[] Rotate(byte[] gray, int w, int h, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double cx = w / 2.0, cy = h / 2.0;
        var dst = new byte[gray.Length];

        for (int y = 0; y < h; y++)
        {
            double dy = y + 0.5 - cy;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - cx;
                // Inverse mapping: read the source at the un-rotated position.
                double sx = dx * cos + dy * sin + cx - 0.5, sy = -dx * sin + dy * cos + cy - 0.5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                if (x0 < 0 || y0 < 0 || x0 + 1 >= w || y0 + 1 >= h)
                {
                    dst[row + x] = 255; // off the sheet: paper
                    continue;
                }
                double fx = sx - x0, fy = sy - y0;
                int i0 = y0 * w + x0, i1 = i0 + w;
                double top = gray[i0] * (1 - fx) + gray[i0 + 1] * fx;
                double bottom = gray[i1] * (1 - fx) + gray[i1 + 1] * fx;
                dst[row + x] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
            }
        }
        return dst;
    }

    private static int[] Histogram(byte[] gray)
    {
        var histogram = new int[256];
        foreach (byte v in gray) histogram[v]++;
        return histogram;
    }

    private static int Percentile(int[] histogram, double fraction)
    {
        long total = 0;
        foreach (int c in histogram) total += c;
        long target = (long)(total * fraction), seen = 0;
        for (int v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen >= target) return v;
        }
        return 255;
    }

    /// <summary>
    /// Otsu's method: the grey level that best separates ink from paper, as the first level counted as paper.
    /// <para>
    /// Every level across the empty gap between ink and paper scores alike, so the run of equally good levels
    /// is taken and its middle returned. Picking either end instead collapses on a page of pure black on pure
    /// white — the whole gap ties, and the threshold lands on the ink itself, leaving nothing above it.
    /// </para>
    /// </summary>
    private static int OtsuThreshold(int[] histogram)
    {
        long total = 0, sum = 0;
        for (int v = 0; v < 256; v++)
        {
            total += histogram[v];
            sum += (long)v * histogram[v];
        }
        if (total == 0) return 128;

        long darkWeight = 0, darkSum = 0;
        double best = -1;
        int low = 127, high = 127;
        for (int v = 0; v < 256; v++)
        {
            darkWeight += histogram[v];
            darkSum += (long)v * histogram[v];
            long lightWeight = total - darkWeight;
            if (darkWeight == 0 || lightWeight == 0) continue;

            double darkMean = (double)darkSum / darkWeight;
            double lightMean = (double)(sum - darkSum) / lightWeight;
            double spread = darkMean - lightMean;
            double variance = (double)darkWeight * lightWeight * spread * spread;
            if (variance > best)
            {
                best = variance;
                low = high = v;
            }
            else if (variance == best)
            {
                high = v;
            }
        }
        return (low + high) / 2 + 1;
    }
}
