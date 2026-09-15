namespace LitePdf.Core.Imaging;

public static class BitmapOps
{
    private static readonly byte[] SepiaR = BuildMultiplyLut(0xF4);
    private static readonly byte[] SepiaG = BuildMultiplyLut(0xEC);
    private static readonly byte[] SepiaB = BuildMultiplyLut(0xD8);

    /// <summary>Applies a reading color mode in place.</summary>
    public static void ApplyColorMode(RenderedBitmap bitmap, PageColorMode mode)
    {
        switch (mode)
        {
            case PageColorMode.Dark:
                ApplyDark(bitmap.Pixels);
                break;
            case PageColorMode.Sepia:
                ApplySepia(bitmap.Pixels);
                break;
        }
    }

    /// <summary>
    /// Dark reading mode: invert, rotate hue 180° so colors keep their hue, then compress the range so white paper
    /// becomes dark grey and black text becomes light grey (easier on the eyes than pure black/white).
    /// </summary>
    private static void ApplyDark(byte[] p)
    {
        const int lo = 30, hi = 225;
        for (int i = 0; i + 3 < p.Length; i += 4)
        {
            int b = 255 - p[i], g = 255 - p[i + 1], r = 255 - p[i + 2];
            // Hue rotation by 180° (luminance preserving), coefficients × 1000.
            int nr = (-574 * r + 1430 * g + 144 * b) / 1000;
            int ng = (426 * r + 430 * g + 144 * b) / 1000;
            int nb = (426 * r + 1430 * g - 856 * b) / 1000;
            p[i + 2] = (byte)(lo + Math.Clamp(nr, 0, 255) * (hi - lo) / 255);
            p[i + 1] = (byte)(lo + Math.Clamp(ng, 0, 255) * (hi - lo) / 255);
            p[i] = (byte)(lo + Math.Clamp(nb, 0, 255) * (hi - lo) / 255);
            p[i + 3] = 255;
        }
    }

    /// <summary>Sepia: multiply blend with warm paper color; text stays crisp black.</summary>
    private static void ApplySepia(byte[] p)
    {
        for (int i = 0; i + 3 < p.Length; i += 4)
        {
            p[i] = SepiaB[p[i]];
            p[i + 1] = SepiaG[p[i + 1]];
            p[i + 2] = SepiaR[p[i + 2]];
        }
    }

    private static byte[] BuildMultiplyLut(int paper)
    {
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = (byte)(v * paper / 255);
        return lut;
    }

    /// <summary>Bilinear resize. Used when an image exceeds the OCR engine's size limit.</summary>
    public static RenderedBitmap Resize(RenderedBitmap source, int width, int height)
    {
        if (width == source.Width && height == source.Height) return source;
        var src = source.Pixels;
        var dst = new byte[width * height * 4];
        double sx = (double)source.Width / width, sy = (double)source.Height / height;
        int sw = source.Width, sh = source.Height;

        for (int y = 0; y < height; y++)
        {
            double fy = Math.Max(0, (y + 0.5) * sy - 0.5);
            int y0 = Math.Min((int)fy, sh - 1), y1 = Math.Min(y0 + 1, sh - 1);
            double wy = fy - y0;
            for (int x = 0; x < width; x++)
            {
                double fx = Math.Max(0, (x + 0.5) * sx - 0.5);
                int x0 = Math.Min((int)fx, sw - 1), x1 = Math.Min(x0 + 1, sw - 1);
                double wx = fx - x0;
                int d = (y * width + x) * 4;
                int i00 = (y0 * sw + x0) * 4, i01 = (y0 * sw + x1) * 4, i10 = (y1 * sw + x0) * 4, i11 = (y1 * sw + x1) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double top = src[i00 + c] * (1 - wx) + src[i01 + c] * wx;
                    double bottom = src[i10 + c] * (1 - wx) + src[i11 + c] * wx;
                    dst[d + c] = (byte)Math.Round(top * (1 - wy) + bottom * wy);
                }
                dst[d + 3] = 255;
            }
        }
        return new RenderedBitmap(width, height, dst);
    }

    /// <summary>Copies a pixel region out of a bitmap.</summary>
    public static RenderedBitmap Crop(RenderedBitmap source, PixelRect rect)
    {
        int x = Math.Clamp(rect.X, 0, source.Width), y = Math.Clamp(rect.Y, 0, source.Height);
        int w = Math.Clamp(rect.Width, 0, source.Width - x), h = Math.Clamp(rect.Height, 0, source.Height - y);
        var dst = new byte[Math.Max(1, w) * Math.Max(1, h) * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(source.Pixels, ((y + row) * source.Width + x) * 4, dst, row * w * 4, w * 4);
        return new RenderedBitmap(Math.Max(1, w), Math.Max(1, h), dst);
    }
}
