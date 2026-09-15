namespace LitePdf.Core;

public interface IOcrEngine
{
    bool IsAvailable { get; }

    /// <summary>BCP-47 tags of installed recognizer languages, e.g. "en-US".</summary>
    IReadOnlyList<string> AvailableLanguages { get; }

    /// <summary>Largest width or height accepted by <see cref="RecognizeAsync"/>.</summary>
    int MaxImageDimension { get; }

    /// <param name="languageTag">Null uses the user's profile language, falling back to the first installed one.</param>
    Task<OcrPageResult> RecognizeAsync(RenderedBitmap bitmap, string? languageTag, CancellationToken ct = default);
}

/// <summary>A recognized word. PixelRect is in bitmap pixels, top-left origin (Top &lt; Bottom).</summary>
public sealed record OcrWord(string Text, RectD PixelRect);

public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

public sealed record OcrPageResult(string LanguageTag, double? TextAngle, IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
}
