using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using LitePdf.Core;
using LitePdf.Core.Imaging;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace LitePdf.Ocr;

/// <summary>On-device OCR using the recognizer built into Windows 10/11 (Windows.Media.Ocr).</summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly ConcurrentDictionary<string, WinOcr.OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable => AvailableLanguages.Count > 0;

    public IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            try
            {
                return WinOcr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            }
            catch (Exception)
            {
                return []; // OCR component missing (e.g. Windows N editions)
            }
        }
    }

    public int MaxImageDimension => (int)WinOcr.OcrEngine.MaxImageDimension;

    public async Task<OcrPageResult> RecognizeAsync(RenderedBitmap bitmap, string? languageTag, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var engine = GetEngine(languageTag) ?? throw new OcrUnavailableException(
            "No OCR language is installed. Add a language with the “Optical character recognition” feature in Windows Settings › Time & language › Language & region.");

        var (w, h) = ViewMath.ClampToMax(bitmap.Width, bitmap.Height, MaxImageDimension);
        var input = (w, h) == (bitmap.Width, bitmap.Height) ? bitmap : BitmapOps.Resize(bitmap, w, h);

        using var software = SoftwareBitmap.CreateCopyFromBuffer(
            input.Pixels.AsBuffer(0, input.Width * input.Height * 4), BitmapPixelFormat.Bgra8, input.Width, input.Height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(software).AsTask(ct).ConfigureAwait(false);

        double sx = 1.0 / input.Width, sy = 1.0 / input.Height;
        var lines = result.Lines
            .Select(line => new OcrLine(line.Words
                .Select(word => new OcrWord(word.Text, new RectD(
                    word.BoundingRect.X * sx,
                    word.BoundingRect.Y * sy,
                    (word.BoundingRect.X + word.BoundingRect.Width) * sx,
                    (word.BoundingRect.Y + word.BoundingRect.Height) * sy)))
                .ToList()))
            .Where(l => l.Words.Count > 0)
            .ToList();

        return new OcrPageResult(engine.RecognizerLanguage.LanguageTag, lines);
    }

    private WinOcr.OcrEngine? GetEngine(string? languageTag)
    {
        string key = string.IsNullOrWhiteSpace(languageTag) ? "" : languageTag;
        if (_engines.TryGetValue(key, out var cached)) return cached;

        WinOcr.OcrEngine? engine = null;
        if (key.Length > 0)
        {
            var language = new Windows.Globalization.Language(key);
            if (WinOcr.OcrEngine.IsLanguageSupported(language))
                engine = WinOcr.OcrEngine.TryCreateFromLanguage(language);
        }
        engine ??= WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null && WinOcr.OcrEngine.AvailableRecognizerLanguages.Count > 0)
            engine = WinOcr.OcrEngine.TryCreateFromLanguage(WinOcr.OcrEngine.AvailableRecognizerLanguages[0]);

        if (engine is not null) _engines[key] = engine;
        return engine;
    }
}

public sealed class OcrUnavailableException(string message) : Exception(message);
