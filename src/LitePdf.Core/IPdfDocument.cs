using LitePdf.Core.Text;

namespace LitePdf.Core;

/// <summary>
/// An open document. Methods may be called from any thread; implementations serialize native work.
/// All geometry is in normalized page coordinates (see <see cref="RectD"/>).
/// </summary>
public interface IPdfDocument : IAsyncDisposable
{
    string FilePath { get; }

    DocumentKind Kind { get; }

    int PageCount { get; }

    /// <summary>Displayed page sizes in points (page /Rotate applied).</summary>
    IReadOnlyList<PageSize> PageSizes { get; }

    bool SupportsAnnotations { get; }

    /// <summary>
    /// Renders the page scaled to <paramref name="pageWidth"/> × <paramref name="pageHeight"/> pixels
    /// (after <paramref name="rotation"/> quarter turns clockwise). When <paramref name="clip"/> is given,
    /// only that pixel region of the scaled page is rendered and returned.
    /// </summary>
    Task<RenderedBitmap> RenderAsync(int pageIndex, int pageWidth, int pageHeight, int rotation,
        PixelRect? clip, RenderFlags flags, int priority, CancellationToken ct = default);

    Task<IReadOnlyList<OutlineItem>> GetOutlineAsync(CancellationToken ct = default);

    /// <summary>Text with per-character boxes. Pages without a text layer return an empty <see cref="PageText"/>.</summary>
    Task<PageText> GetTextAsync(int pageIndex, int priority, CancellationToken ct = default);

    /// <summary>True when the page contains raster images (used with an empty text layer to detect scans).</summary>
    Task<bool> HasImagesAsync(int pageIndex, CancellationToken ct = default);

    Task<IReadOnlyList<PdfLink>> GetLinksAsync(int pageIndex, CancellationToken ct = default);

    Task<IReadOnlyList<PdfAnnotation>> GetAnnotationsAsync(int pageIndex, int priority, CancellationToken ct = default);

    /// <summary>Adds a text markup annotation covering <paramref name="lineRects"/> (one rect per text line).</summary>
    Task AddMarkupAsync(int pageIndex, AnnotationKind kind, IReadOnlyList<RectD> lineRects, AnnotationColor color, CancellationToken ct = default);

    /// <summary>Adds a sticky note whose icon's top-left corner is at <paramref name="position"/>.</summary>
    Task AddNoteAsync(int pageIndex, PointD position, string contents, AnnotationColor color, CancellationToken ct = default);

    Task SetAnnotationColorAsync(int pageIndex, int annotationIndex, AnnotationColor color, CancellationToken ct = default);

    Task SetAnnotationContentsAsync(int pageIndex, int annotationIndex, string contents, CancellationToken ct = default);

    Task RemoveAnnotationAsync(int pageIndex, int annotationIndex, CancellationToken ct = default);

    /// <summary>Writes the document (with changes) to <paramref name="path"/>, which must not be the open file.</summary>
    Task SaveCopyAsync(string path, CancellationToken ct = default);

    Task<DocumentInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>Stable identifier from the PDF trailer /ID, or null.</summary>
    Task<string?> GetFileIdentifierAsync(CancellationToken ct = default);
}
