using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using LitePdf.Core;
using LitePdf.Core.Imaging;
using LitePdf.Core.Text;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace LitePdf.Ocr;

/// <summary>
/// On-device OCR using the recognizer built into Windows 10/11 (Windows.Media.Ocr).
/// <para>
/// The recognizer reads horizontal lines of type and nothing else, so on a scanned page it is given help at
/// both ends: the image is cleaned up first (<see cref="ScanPreprocessor"/>), stacked fractions are cut out
/// and re-laid as lines of type on a <see cref="FractionSheet"/>, and the words that come back are reassembled
/// by <see cref="OcrLayout"/> into reading order with exponents, degree signs and figures restored.
/// </para>
/// </summary>
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

    public async Task<OcrPageResult> RecognizeAsync(RenderedBitmap bitmap, string? languageTag,
        OcrOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        options ??= OcrOptions.Default;
        var engine = GetEngine(languageTag) ?? throw new OcrUnavailableException(
            "No OCR language is installed. Add a language with the “Optical character recognition” feature in Windows Settings › Time & language › Language & region.");

        var (w, h) = ViewMath.ClampToMax(bitmap.Width, bitmap.Height, MaxImageDimension);
        var input = (w, h) == (bitmap.Width, bitmap.Height) ? bitmap : BitmapOps.Resize(bitmap, w, h);

        if (!options.Preprocess)
        {
            var plain = await RecognizeLinesAsync(engine, input, ct).ConfigureAwait(false);
            return new OcrPageResult(engine.RecognizerLanguage.LanguageTag, plain);
        }

        var page = ScanPreprocessor.Prepare(input, new ScanPreprocessOptions { Deskew = options.Deskew });
        var structure = options.ReconstructMath || options.DetectFigures || options.RebuildReadingOrder
            ? PageStructure.Analyze(page, 0)
            : PageStructure.Empty;

        var clean = page.ToBitmap();
        var fractions = options.ReconstructMath ? MathLayout.FindFractions(structure) : [];
        var sheet = FractionSheet.Build(clean, fractions, MaxImageDimension);

        // Blank the fractions out of the page pass: left in, their digits come back as stray fragments that
        // cannot be told apart from the words they sit between.
        var pageImage = clean;
        if (fractions.Count > 0)
        {
            pageImage = new RenderedBitmap(clean.Width, clean.Height, (byte[])clean.Pixels.Clone());
            foreach (var fraction in fractions) Erase(pageImage, fraction.Bounds);
        }

        var lines = await RecognizeLinesAsync(engine, pageImage, ct).ConfigureAwait(false);
        var recognized = sheet is null
            ? []
            : sheet.Assign(await RecognizeLinesAsync(engine, sheet.Image, ct).ConfigureAwait(false));
        // Figures are settled against the recognized text: ink that prose runs through is a pen mark, not a drawing.
        var figures = options.DetectFigures && structure.HasDrawings
            ? structure.FindFigures(lines.Select(l => l.Bounds).ToList())
            : [];

        return OcrLayout.Rebuild(engine.RecognizerLanguage.LanguageTag, lines, page, structure, recognized, figures, options);
    }

    private static async Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(
        WinOcr.OcrEngine engine, RenderedBitmap bitmap, CancellationToken ct)
    {
        using var software = SoftwareBitmap.CreateCopyFromBuffer(
            bitmap.Pixels.AsBuffer(0, bitmap.Width * bitmap.Height * 4), BitmapPixelFormat.Bgra8,
            bitmap.Width, bitmap.Height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(software).AsTask(ct).ConfigureAwait(false);

        double sx = 1.0 / bitmap.Width, sy = 1.0 / bitmap.Height;
        return result.Lines
            .Select(line => new OcrLine(line.Words
                .Select(word => new OcrWord(word.Text, new RectD(
                    word.BoundingRect.X * sx,
                    word.BoundingRect.Y * sy,
                    (word.BoundingRect.X + word.BoundingRect.Width) * sx,
                    (word.BoundingRect.Y + word.BoundingRect.Height) * sy)))
                .ToList()))
            .Where(l => l.Words.Count > 0)
            .ToList();
    }

    /// <summary>Paints an area of the page white so the whole-page pass does not read it.</summary>
    private static void Erase(RenderedBitmap bitmap, RectD area)
    {
        var rect = area.ClampToUnit();
        int x0 = (int)(rect.Left * bitmap.Width), x1 = (int)Math.Ceiling(rect.Right * bitmap.Width);
        int y0 = (int)(rect.Top * bitmap.Height), y1 = (int)Math.Ceiling(rect.Bottom * bitmap.Height);
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(bitmap.Width, x1);
        y1 = Math.Min(bitmap.Height, y1);
        for (int y = y0; y < y1; y++)
        {
            int offset = (y * bitmap.Width + x0) * 4;
            for (int x = x0; x < x1; x++, offset += 4)
            {
                bitmap.Pixels[offset] = 255;
                bitmap.Pixels[offset + 1] = 255;
                bitmap.Pixels[offset + 2] = 255;
            }
        }
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
