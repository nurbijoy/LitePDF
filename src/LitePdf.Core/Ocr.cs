namespace LitePdf.Core;

public interface IOcrEngine
{
    bool IsAvailable { get; }

    /// <summary>BCP-47 tags of installed recognizer languages, e.g. "en-US".</summary>
    IReadOnlyList<string> AvailableLanguages { get; }

    /// <summary>Largest width or height accepted by <see cref="RecognizeAsync"/>; larger images are downscaled.</summary>
    int MaxImageDimension { get; }

    /// <param name="languageTag">Null or empty picks the user's profile language, then the first installed one.</param>
    Task<OcrPageResult> RecognizeAsync(RenderedBitmap bitmap, string? languageTag, CancellationToken ct = default);
}

/// <summary>A recognized word; Bounds are normalized to the recognized bitmap (0..1, top-left origin).</summary>
public sealed record OcrWord(string Text, RectD Bounds);

public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

public sealed record OcrPageResult(string LanguageTag, IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
}
