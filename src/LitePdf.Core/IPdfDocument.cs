using LitePdf.Core.Text;

namespace LitePdf.Core;

/// <summary>
/// An open document. All methods are safe to call from any thread; implementations
/// serialize native work internally (PDFium: one worker thread, see BLUEPRINT §4).
/// </summary>
public interface IPdfDocument : IAsyncDisposable
{
    string FilePath { get; }

    int PageCount { get; }

    IReadOnlyList<PageSize> PageSizes { get; }

    Task<RenderedBitmap> RenderPageAsync(int pageIndex, int pixelWidth, int pixelHeight,
        RenderFlags flags, int priority, CancellationToken ct = default);

    Task<IReadOnlyList<OutlineItem>> GetOutlineAsync(CancellationToken ct = default);

    Task<string> GetPageTextAsync(int pageIndex, CancellationToken ct = default);

    /// <summary>Number of text characters on the page; close to 0 means the page is probably scanned.</summary>
    Task<int> GetCharCountAsync(int pageIndex, CancellationToken ct = default);

    // Extended API (T-30, T-14, T-50)
    Task<PageTextLayer?> GetTextLayerAsync(int pageIndex, CancellationToken ct = default) => Task.FromResult<PageTextLayer?>(null);
    Task<IReadOnlyList<PdfLink>> GetLinksAsync(int pageIndex, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PdfLink>>(Array.Empty<PdfLink>());
    Task<byte[]> GetFileIdentifierAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

// Keep PdfLink here for Core reference (actual type is in Pdfium namespace, but we expose a Core version for interface)
public sealed record PdfLink(RectD Rect, int DestPageIndex, string? Uri);
