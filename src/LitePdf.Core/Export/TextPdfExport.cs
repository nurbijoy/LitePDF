namespace LitePdf.Core.Export;

/// <summary>Reject unsupported pages before creating or replacing a Word document.</summary>
public static class TextPdfExport
{
    public static void Validate(IReadOnlyList<PageContent> pages)
    {
        foreach (var page in pages)
        {
            if (page.IsSearchableScan || page.Text.Source == Text.TextSource.Ocr ||
                (page.Text.VisibleCharCount == 0 &&
                 (page.Images.Count > 0 || page.Rules.Count > 0 || page.Fills.Count > 0)))
                throw new NotSupportedException($"Page {page.PageIndex + 1} has no supported PDF text. " +
                    "Scanned and image-only pages cannot be converted to Word yet. Select only text pages. " +
                    "No output file was changed.");
        }

        if (!pages.Any(p => p.Text.VisibleCharCount > 0))
            throw new NotSupportedException("The selected pages contain no PDF text to convert. No output file was changed.");
    }
}
