#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;
using LitePdf.Core;

namespace LitePdf.Ocr;

public sealed class WindowsOcrEngine : IOcrEngine
{
    public bool IsAvailable { get; }
    public IReadOnlyList<string> AvailableLanguages { get; }
    public int MaxImageDimension { get; } = 10000;

    public WindowsOcrEngine()
    {
        try
        {
            var engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
            IsAvailable = engine != null || WinOcr.OcrEngine.AvailableRecognizerLanguages.Count > 0;
            AvailableLanguages = WinOcr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
        }
        catch
        {
            IsAvailable = false;
            AvailableLanguages = Array.Empty<string>();
        }
    }

    public async Task<OcrPageResult> RecognizeAsync(RenderedBitmap bitmap, string? languageTag, CancellationToken ct = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("OCR not available on this system.");

        WinOcr.OcrEngine engine;
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            var lang = new Windows.Globalization.Language(languageTag);
            engine = WinOcr.OcrEngine.TryCreateFromLanguage(lang) ?? WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
                ?? throw new InvalidOperationException($"Language {languageTag} not available.");
        }
        else
        {
            engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
                ?? WinOcr.OcrEngine.TryCreateFromLanguage(WinOcr.OcrEngine.AvailableRecognizerLanguages.First())
                ?? throw new InvalidOperationException("No OCR language available.");
        }

        // Clamp size
        var (clampedW, clampedH) = ViewMath.ClampToMax(bitmap.Width, bitmap.Height, MaxImageDimension);
        byte[] pixels = bitmap.Pixels;
        int stride = bitmap.Stride;
        int width = bitmap.Width;
        int height = bitmap.Height;

        if (clampedW != width || clampedH != height)
        {
            // Simple downscale using nearest? For now, we need to resize.
            // We'll create a scaled bitmap using WPF? But we are in Ocr project (Windows TFM). Use SoftwareBitmap scaling via BitmapTransform? Simpler: just crop? But we must scale.
            // For minimal implementation, we'll do bilinear downscale manually for BGRA.
            double scaleX = (double)clampedW / width;
            double scaleY = (double)clampedH / height;
            var newPixels = new byte[clampedW * clampedH * 4];
            for (int y = 0; y < clampedH; y++)
            {
                int srcY = Math.Min(height - 1, (int)(y / scaleY));
                for (int x = 0; x < clampedW; x++)
                {
                    int srcX = Math.Min(width - 1, (int)(x / scaleX));
                    int srcIdx = srcY * stride + srcX * 4;
                    int dstIdx = (y * clampedW + x) * 4;
                    if (srcIdx + 3 < pixels.Length && dstIdx + 3 < newPixels.Length)
                    {
                        newPixels[dstIdx] = pixels[srcIdx];
                        newPixels[dstIdx + 1] = pixels[srcIdx + 1];
                        newPixels[dstIdx + 2] = pixels[srcIdx + 2];
                        newPixels[dstIdx + 3] = pixels[srcIdx + 3];
                    }
                }
            }
            pixels = newPixels;
            stride = clampedW * 4;
            width = clampedW;
            height = clampedH;
        }

        // SoftwareBitmap needs a tight buffer (stride == width * 4); PDFium's alpha byte is undefined, so ignore alpha.
        if (stride != width * 4)
        {
            var tight = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                System.Buffer.BlockCopy(pixels, y * stride, tight, y * width * 4, width * 4);
            pixels = tight;
        }

        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(0, width * height * 4), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);

        var ocrResult = await engine.RecognizeAsync(softwareBitmap).AsTask(ct).ConfigureAwait(false);

        var lines = new List<OcrLine>();
        foreach (var line in ocrResult.Lines)
        {
            var words = new List<OcrWord>();
            foreach (var word in line.Words)
            {
                // word.BoundingRect is in bitmap pixels, top-left origin
                var r = word.BoundingRect;
                var rect = new RectD(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
                words.Add(new OcrWord(word.Text, rect));
            }
            lines.Add(new OcrLine(line.Text, words));
        }

        double? angle = ocrResult.TextAngle;
        string langTag = engine.RecognizerLanguage.LanguageTag;
        return new OcrPageResult(langTag, angle, lines);
    }
}
#else
using LitePdf.Core;

namespace LitePdf.Ocr;

public sealed class WindowsOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;
    public IReadOnlyList<string> AvailableLanguages => Array.Empty<string>();
    public int MaxImageDimension => 10000;

    public Task<OcrPageResult> RecognizeAsync(RenderedBitmap bitmap, string? languageTag, CancellationToken ct = default)
        => throw new PlatformNotSupportedException("Windows OCR is only available on Windows.");
}
#endif
