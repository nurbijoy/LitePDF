namespace LitePdf.Core;

/// <summary>A navigation target: page index plus an optional normalized vertical position on that page.</summary>
public readonly record struct Destination(int PageIndex, double? Y = null)
{
    public bool IsValid => PageIndex >= 0;
}

/// <summary>One outline ("chapter") entry.</summary>
public sealed record OutlineItem(string Title, Destination Target, bool IsOpen, IReadOnlyList<OutlineItem> Children);

/// <summary>A link on a page. Bounds are normalized page coordinates.</summary>
public sealed record PdfLink(RectD Bounds, Destination Target, string? Uri);

public enum AnnotationKind
{
    Other,
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
    Note,
    Link,
    Widget,
}

public readonly record struct AnnotationColor(byte R, byte G, byte B)
{
    public static readonly AnnotationColor Yellow = new(0xFF, 0xE0, 0x3D);
    public static readonly AnnotationColor Green = new(0x7B, 0xDC, 0x7B);
    public static readonly AnnotationColor Blue = new(0x70, 0xC0, 0xFF);
    public static readonly AnnotationColor Pink = new(0xFF, 0x86, 0xC4);
    public static readonly AnnotationColor Orange = new(0xFF, 0xB0, 0x4A);

    public static IReadOnlyList<(string Name, AnnotationColor Color)> Palette { get; } =
    [
        ("Yellow", Yellow), ("Green", Green), ("Blue", Blue), ("Pink", Pink), ("Orange", Orange),
    ];

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>An annotation on a page. Index is the PDFium annotation index at the time it was read.</summary>
public sealed record PdfAnnotation(
    int PageIndex,
    int Index,
    AnnotationKind Kind,
    RectD Bounds,
    IReadOnlyList<RectD> Quads,
    AnnotationColor? Color,
    string Contents)
{
    /// <summary>The annotation's /T, which is who wrote it. Empty when the PDF does not say.</summary>
    public string Author { get; init; } = string.Empty;

    public bool IsMarkup => Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut or AnnotationKind.Squiggly;

    /// <summary>True for annotation types LitePDF lets the user select, recolor and delete.</summary>
    public bool IsEditable => IsMarkup || Kind == AnnotationKind.Note;

    public bool HitTest(PointD p, double tolerance)
    {
        if (Quads.Count > 0) return Quads.Any(q => q.Contains(p, tolerance));
        return Bounds.Contains(p, tolerance);
    }
}

public sealed record DocumentInfo(
    string FilePath,
    long FileSize,
    int PageCount,
    string? PdfVersion,
    bool IsEncrypted,
    string? Title,
    string? Author,
    string? Subject,
    string? Keywords,
    string? Creator,
    string? Producer,
    DateTimeOffset? Created,
    DateTimeOffset? Modified);

public enum DocumentKind
{
    Pdf,
    Image,
}
