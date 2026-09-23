using System.Text;

namespace LitePdf.Core.Export;

/// <summary>
/// Some of the selected pages have no PDF text — a scanned page, a picture — while others do. The caller can
/// offer to convert the rest: a text book with a picture for a cover is still a text book.
/// </summary>
public sealed class UnsupportedPagesException(IReadOnlyList<int> pages, int selectedCount)
    : NotSupportedException(TextPdfExport.DescribeUnsupported(pages, selectedCount))
{
    /// <summary>Zero-based indexes of the pages without text.</summary>
    public IReadOnlyList<int> Pages { get; } = pages;

    public int SelectedCount { get; } = selectedCount;
}

/// <summary>Reject unsupported pages before creating or replacing a Word document.</summary>
public static class TextPdfExport
{
    /// <summary>
    /// A page with nothing but pictures or drawings on it, or whose only text is the invisible layer a scanner
    /// lays under its image. Word conversion reads text PDFs only.
    /// </summary>
    public static bool IsUnsupported(PageContent page) =>
        page.IsSearchableScan || page.Text.Source == Text.TextSource.Ocr ||
        (page.Text.VisibleCharCount == 0 && (page.Images.Count > 0 || page.Rules.Count > 0 || page.Fills.Count > 0));

    public static IReadOnlyList<int> UnsupportedPages(IReadOnlyList<PageContent> pages) =>
        [.. pages.Where(IsUnsupported).Select(p => p.PageIndex)];

    /// <summary>
    /// Throws unless every page can be converted. When some pages can, the exception is an
    /// <see cref="UnsupportedPagesException"/> naming the others; when none can, a plain one.
    /// </summary>
    public static void Validate(IReadOnlyList<PageContent> pages)
    {
        var unsupported = UnsupportedPages(pages);
        if (!pages.Any(p => p.Text.VisibleCharCount > 0 && !IsUnsupported(p)))
            throw new NotSupportedException(unsupported.Count > 0
                ? "The selected pages have no PDF text: they are scanned or pictures only. Word conversion reads " +
                  "text PDFs. No output file was changed."
                : "The selected pages contain no PDF text to convert. No output file was changed.");

        if (unsupported.Count > 0) throw new UnsupportedPagesException(unsupported, pages.Count);
    }

    internal static string DescribeUnsupported(IReadOnlyList<int> pages, int selectedCount)
    {
        string which = pages.Count == 1 ? $"Page {DescribePages(pages)} has" : $"Pages {DescribePages(pages)} have";
        int rest = selectedCount - pages.Count;
        return $"{which} no PDF text: scanned pages and pictures can't be converted to Word yet. " +
               $"The other {rest} {(rest == 1 ? "page" : "pages")} can be. No output file was changed.";
    }

    /// <summary>One-based page numbers with runs collapsed: "1, 3, 7–9". Long lists end in "and 12 more".</summary>
    public static string DescribePages(IReadOnlyList<int> pages)
    {
        var sorted = pages.Distinct().Order().ToList();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j > i + 1 ? $"{sorted[i] + 1}–{sorted[j] + 1}"
                : j == i + 1 ? $"{sorted[i] + 1}, {sorted[j] + 1}"
                : $"{sorted[i] + 1}");
            i = j + 1;
        }

        const int Shown = 6;
        var text = new StringBuilder(string.Join(", ", parts.Take(Shown)));
        if (parts.Count > Shown) text.Append($" and {parts.Count - Shown} more");
        return text.ToString();
    }
}
