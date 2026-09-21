namespace LitePdf.Core.Export;

/// <summary>
/// Turns a PDF base font name into something Word can resolve.
///
/// PDF font names carry a six-letter subset prefix (<c>ABCDEF+Times</c>), style suffixes
/// (<c>-BoldItalic</c>, <c>,Bold</c>, <c>MT</c>, <c>PS</c>) and the base-14 names, none of which are
/// installed on Windows under those names. A name Word cannot resolve falls back to a default that looks
/// nothing like the original, so mapping the standard families is worth more than it costs.
/// </summary>
public static class FontMapper
{
    private static readonly (string Prefix, string Family)[] Standard =
    [
        ("Times", "Times New Roman"),
        ("TimesNewRoman", "Times New Roman"),
        ("Helvetica", "Arial"),
        ("Arial", "Arial"),
        ("Courier", "Courier New"),
        ("CourierNew", "Courier New"),
        ("Symbol", "Symbol"),
        ("ZapfDingbats", "Wingdings"),
        ("Calibri", "Calibri"),
        ("Cambria", "Cambria"),
        ("Georgia", "Georgia"),
        ("Verdana", "Verdana"),
        ("Tahoma", "Tahoma"),
        ("Garamond", "Garamond"),
        ("BookAntiqua", "Book Antiqua"),
        ("Palatino", "Palatino Linotype"),
        ("CenturySchoolbook", "Century Schoolbook"),
        ("Consolas", "Consolas"),
        ("SegoeUI", "Segoe UI"),
        ("Minion", "Minion Pro"),
        ("Myriad", "Myriad Pro"),
    ];

    /// <summary>The family name, with any subset prefix and style suffix removed.</summary>
    public static string Family(string? baseFontName, bool serifHint)
    {
        string name = Clean(baseFontName);
        if (name.Length == 0) return serifHint ? "Times New Roman" : "Calibri";

        string compact = name.Replace(" ", string.Empty);
        foreach (var (prefix, family) in Standard)
            if (compact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return family;

        return name;
    }

    /// <summary>True when the name itself says the font is bold (many PDFs leave the weight at 400 anyway).</summary>
    public static bool NameSaysBold(string? baseFontName)
    {
        string name = Normalize(baseFontName);
        return name.Contains("bold", StringComparison.Ordinal)
            || name.Contains("black", StringComparison.Ordinal)
            || name.Contains("heavy", StringComparison.Ordinal)
            || name.Contains("semibold", StringComparison.Ordinal);
    }

    public static bool NameSaysItalic(string? baseFontName)
    {
        string name = Normalize(baseFontName);
        return name.Contains("italic", StringComparison.Ordinal) || name.Contains("oblique", StringComparison.Ordinal);
    }

    private static string Normalize(string? baseFontName) =>
        (baseFontName ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    /// <summary>Strips the subset prefix and the style suffix, leaving the family.</summary>
    private static string Clean(string? baseFontName)
    {
        string name = (baseFontName ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;

        // "ABCDEF+Times-Roman": six upper-case letters and a plus.
        if (name.Length > 7 && name[6] == '+' && name.AsSpan(0, 6).ToString().All(char.IsAsciiLetterUpper))
            name = name[7..];

        int cut = name.IndexOfAny([',']);
        if (cut > 0) name = name[..cut];

        foreach (string suffix in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return name.TrimEnd('-', ' ');
    }

    // Longest first: "-BoldItalic" must win over "-Bold".
    private static readonly string[] Suffixes =
    [
        "-BoldItalic", "-BoldOblique", "-SemiBoldItalic", "-LightItalic",
        "-Bold", "-Italic", "-Oblique", "-Roman", "-Regular", "-Light", "-SemiBold", "-Medium", "-Black",
        "BoldItalic", "BoldMT", "ItalicMT", "PSMT", "MT", "PS",
    ];
}
