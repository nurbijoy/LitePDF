namespace LitePdf.Core;

public interface IOcrEngine
{
    bool IsAvailable { get; }

    /// <summary>BCP-47 tags of installed recognizer languages, e.g. "en-US".</summary>
    IReadOnlyList<string> AvailableLanguages { get; }

    /// <summary>Largest width or height accepted by <see cref="RecognizeAsync"/>; larger images are downscaled.</summary>
    int MaxImageDimension { get; }

    /// <param name="languageTag">Null or empty picks the user's profile language, then the first installed one.</param>
    /// <param name="options">Null uses <see cref="OcrOptions.Default"/>.</param>
    Task<OcrPageResult> RecognizeAsync(RenderedBitmap bitmap, string? languageTag, OcrOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>How much work goes into a page beyond handing it to the recognizer.</summary>
public sealed record OcrOptions
{
    /// <summary>Clean the image first: drop coloured pen marks, flatten lighting, remove show-through.</summary>
    public bool Preprocess { get; init; } = true;

    /// <summary>Straighten a crooked scan before recognizing it.</summary>
    public bool Deskew { get; init; } = true;

    /// <summary>Rebuild stacked fractions, exponents, indices and degree signs from the page geometry.</summary>
    public bool ReconstructMath { get; init; } = true;

    /// <summary>Report diagrams and charts as figures instead of letting stray labels into the text.</summary>
    public bool DetectFigures { get; init; } = true;

    /// <summary>Order lines by column and position rather than in the order the recognizer emitted them.</summary>
    public bool RebuildReadingOrder { get; init; } = true;

    public static OcrOptions Default { get; } = new();

    /// <summary>Raw recognizer output, with no image cleanup or layout analysis.</summary>
    public static OcrOptions Plain { get; } = new()
    {
        Preprocess = false,
        Deskew = false,
        ReconstructMath = false,
        DetectFigures = false,
        RebuildReadingOrder = false,
    };
}

/// <summary>What a line of recognized text represents.</summary>
public enum OcrLineKind
{
    Text,

    /// <summary>A placeholder standing in for a diagram, so copied text keeps the diagram's place.</summary>
    Figure,

    /// <summary>A label read from inside a figure; kept apart from the prose around it.</summary>
    FigureLabel,
}

/// <summary>
/// A recognized word. <see cref="Bounds"/> and <see cref="CharBounds"/> are normalized to the page (0..1,
/// top-left origin). <see cref="CharBounds"/> is set when the individual glyphs could be measured on the page,
/// which makes selection exact instead of an even split across the word.
/// </summary>
public sealed record OcrWord(string Text, RectD Bounds, IReadOnlyList<RectD>? CharBounds = null);

public sealed record OcrLine(IReadOnlyList<OcrWord> Words, OcrLineKind Kind = OcrLineKind.Text)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text));

    public RectD Bounds => Words.Aggregate(RectD.Empty, (acc, w) => acc.Union(w.Bounds));
}

public sealed record OcrPageResult(string LanguageTag, IReadOnlyList<OcrLine> Lines, IReadOnlyList<RectD>? Figures = null)
{
    /// <summary>Areas holding a diagram, chart or photograph, normalized to the page.</summary>
    public IReadOnlyList<RectD> Figures { get; init; } = Figures ?? [];

    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
}
