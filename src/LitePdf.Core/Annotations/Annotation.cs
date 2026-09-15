namespace LitePdf.Core.Annotations;

public enum AnnotationType
{
    Highlight = 9,
    Underline = 10,
    Strikeout = 12,
    Text = 1
}

public sealed class AnnotationColor
{
    public string Name { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public byte A { get; } = 110;

    public AnnotationColor(string name, byte r, byte g, byte b, byte a = 110)
    {
        Name = name; R = r; G = g; B = b; A = a;
    }

    public static readonly IReadOnlyList<AnnotationColor> Palette = new List<AnnotationColor>
    {
        new("Yellow", 0xFF, 0xE5, 0x00),
        new("Green", 0x7C, 0xE0, 0x7C),
        new("Blue", 0x7C, 0xC7, 0xFF),
        new("Pink", 0xFF, 0x8A, 0xC8),
        new("Orange", 0xFF, 0xB3, 0x47),
    };
}

public sealed record AnnotationModel(
    int PageIndex,
    int AnnotIndex,
    AnnotationType Type,
    string ColorName,
    byte R, byte G, byte B,
    IReadOnlyList<RectD> Quads,
    string Text,
    string Contents
)
{
    public string GetDisplayText(int maxLen = 100)
    {
        if (!string.IsNullOrWhiteSpace(Contents)) return Contents.Length > maxLen ? Contents.Substring(0, maxLen) + "…" : Contents;
        if (!string.IsNullOrWhiteSpace(Text)) return Text.Length > maxLen ? Text.Substring(0, maxLen) + "…" : Text;
        return $"{Type} on page {PageIndex + 1}";
    }
}
