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
}
