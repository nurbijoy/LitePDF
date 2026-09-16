using System.Collections.Concurrent;
using System.IO;
using LitePdf.App.Infrastructure;
using LitePdf.Core;
using LitePdf.Core.Storage;
using LitePdf.Core.Text;
using LitePdf.Ocr;
using LitePdf.Pdfium;

namespace LitePdf.App.Documents;

/// <summary>
/// One open document plus everything derived from it: page text (PDF or OCR), OCR cache, links, annotations,
/// render generations and dirty state. Events are raised on the UI thread (callers use it from the UI thread).
/// </summary>
public sealed class DocumentSession : IAsyncDisposable
{
    /// <summary>Pages with fewer visible characters than this are treated as having no text layer.</summary>
    public const int MinTextChars = 8;

    private readonly IOcrEngine _ocr;
    private readonly Func<string?> _ocrLanguage;
    private readonly Func<OcrOptions> _ocrOptions;
    private readonly LruCache<int, PageText> _pdfText = new(48);
    private readonly ConcurrentDictionary<int, PageText> _ocrText = new();
    private readonly Dictionary<int, IReadOnlyList<PdfLink>> _links = new();
    private readonly Dictionary<int, IReadOnlyList<PdfAnnotation>> _annotations = new();
    private readonly OcrCache? _ocrCache;
    private int[] _generations;

    private DocumentSession(IPdfDocument document, string displayName, string? password, string? documentKey,
        IOcrEngine ocr, Func<string?> ocrLanguage, Func<OcrOptions> ocrOptions)
    {
        Document = document;
        DisplayName = displayName;
        Password = password;
        DocumentKey = documentKey;
        _ocr = ocr;
        _ocrLanguage = ocrLanguage;
        _ocrOptions = ocrOptions;
        _generations = new int[document.PageCount];
        if (documentKey is not null) _ocrCache = new OcrCache(AppPaths.OcrDirectory, documentKey);
    }

    public IPdfDocument Document { get; private set; }

    public string FilePath => Document.FilePath;

    public string DisplayName { get; private set; }

    public string? Password { get; }

    public string? DocumentKey { get; }

    /// <summary>True for documents without a file on disk (e.g. a pasted image).</summary>
    public bool IsVirtual { get; private init; }

    public bool IsPdf => Document.Kind == DocumentKind.Pdf;

    public bool CanAnnotate => Document.SupportsAnnotations;

    public int PageCount => Document.PageCount;

    public IReadOnlyList<PageSize> PageSizes => Document.PageSizes;

    public bool IsDirty { get; private set; }

    public IOcrEngine OcrEngine => _ocr;

    public event Action? DirtyChanged;

    /// <summary>Page pixels changed (annotations edited): re-render.</summary>
    public event Action<int>? PageInvalidated;

    /// <summary>Page text changed (OCR finished): refresh selection/search.</summary>
    public event Action<int>? PageTextChanged;

    public event Action<int>? AnnotationsChanged;

    /// <summary>The underlying document object was replaced (after save).</summary>
    public event Action? DocumentReplaced;

    public static async Task<DocumentSession> OpenAsync(string path, string? password, IOcrEngine ocr,
        Func<string?> ocrLanguage, Func<OcrOptions> ocrOptions)
    {
        IPdfDocument document = ImageDocument.IsImagePath(path)
            ? await ImageDocument.OpenAsync(path)
            : await PdfiumDocument.OpenAsync(path, password);
        try
        {
            string? id = await document.GetFileIdentifierAsync();
            string key = await Task.Run(() => Core.Storage.DocumentKey.Compute(path, id, document.PageCount));
            return new DocumentSession(document, Path.GetFileName(path), password, key, ocr, ocrLanguage, ocrOptions);
        }
        catch
        {
            await document.DisposeAsync();
            throw;
        }
    }

    public static DocumentSession FromImage(ImageDocument document, string displayName, IOcrEngine ocr,
        Func<string?> ocrLanguage, Func<OcrOptions> ocrOptions) =>
        new(document, displayName, null, null, ocr, ocrLanguage, ocrOptions) { IsVirtual = true };

    public int GetGeneration(int pageIndex) => _generations[pageIndex];

    // ---- Text ----

    public PageText? TryGetCachedText(int pageIndex)
    {
        if (_ocrText.TryGetValue(pageIndex, out var ocr)) return ocr;
        return _pdfText.TryGet(pageIndex, out var text) ? text : null;
    }

    public async Task<PageText> GetTextAsync(int pageIndex, int priority, CancellationToken ct = default)
    {
        if (TryGetCachedText(pageIndex) is { } cached) return cached;
        var document = Document;
        var text = await document.GetTextAsync(pageIndex, priority, ct).ConfigureAwait(false);
        if (text.VisibleCharCount < MinTextChars && _ocrCache is not null && _ocrCache.TryGet(pageIndex, out var saved))
        {
            var ocrText = PageText.FromOcr(pageIndex, saved);
            _ocrText[pageIndex] = ocrText;
            return ocrText;
        }
        if (ReferenceEquals(document, Document)) _pdfText.Set(pageIndex, text);
        return text;
    }

    public bool HasOcrText(int pageIndex) => _ocrText.ContainsKey(pageIndex);

    /// <summary>True when the page shows content but has no usable text layer (a scan or photo).</summary>
    public async Task<bool> NeedsOcrAsync(int pageIndex, CancellationToken ct = default)
    {
        var text = await GetTextAsync(pageIndex, RenderPriority.Interactive, ct);
        if (text.Source == TextSource.Ocr || text.VisibleCharCount >= MinTextChars) return false;
        return await Document.HasImagesAsync(pageIndex, ct);
    }

    // ---- OCR ----

    public async Task<PageText> RecognizePageAsync(int pageIndex, int priority, CancellationToken ct)
    {
        var size = PageSizes[pageIndex];
        // Scans in the wild are 200-300 dpi; rendering above that gives the recognizer whole pixels to work
        // with for small type (exponents, fraction digits) without inventing detail.
        const double dpi = 400;
        int w = (int)Math.Round(size.Width * dpi / 72), h = (int)Math.Round(size.Height * dpi / 72);
        (w, h) = ViewMath.ClampToMax(w, h, Math.Min(5200, _ocr.MaxImageDimension));

        var bitmap = await Document.RenderAsync(pageIndex, w, h, 0, null, RenderFlags.None, priority, ct).ConfigureAwait(false);
        var result = await _ocr.RecognizeAsync(bitmap, _ocrLanguage(), _ocrOptions(), ct).ConfigureAwait(false);
        var text = PageText.FromOcr(pageIndex, result);
        _ocrText[pageIndex] = text;
        try
        {
            _ocrCache?.Set(pageIndex, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving OCR cache");
        }
        return text;
    }

    public void NotifyTextChanged(int pageIndex) => PageTextChanged?.Invoke(pageIndex);

    /// <summary>Recognizes text inside a normalized region, rendered at high resolution for accuracy.</summary>
    public async Task<OcrPageResult> RecognizeRegionAsync(int pageIndex, RectD region, CancellationToken ct = default)
    {
        var bitmap = await RenderRegionAsync(pageIndex, region, minDpi: 400, minWidthPixels: 1400, ct);
        // A selected region is rarely a whole page: columns and figure blocks do not apply to it.
        var options = _ocrOptions() with { DetectFigures = false, RebuildReadingOrder = false };
        return await _ocr.RecognizeAsync(bitmap, _ocrLanguage(), options, ct);
    }

    public Task<RenderedBitmap> RenderRegionAsync(int pageIndex, RectD region, double minDpi, int minWidthPixels, CancellationToken ct = default)
    {
        var size = PageSizes[pageIndex];
        region = region.ClampToUnit();
        double scale = minDpi / 72;
        double regionWidth = region.Width * size.Width * scale;
        if (regionWidth < minWidthPixels) scale *= Math.Min(4, minWidthPixels / Math.Max(1, regionWidth));
        int pageW = (int)Math.Round(size.Width * scale), pageH = (int)Math.Round(size.Height * scale);
        var clip = new PixelRect((int)(region.Left * pageW), (int)(region.Top * pageH),
            Math.Max(1, (int)Math.Ceiling(region.Width * pageW)), Math.Max(1, (int)Math.Ceiling(region.Height * pageH)));
        return Document.RenderAsync(pageIndex, pageW, pageH, 0, clip, RenderFlags.Annotations, RenderPriority.Interactive, ct);
    }

    // ---- Links & annotations ----

    public async Task<IReadOnlyList<PdfLink>> GetLinksAsync(int pageIndex, CancellationToken ct = default)
    {
        if (_links.TryGetValue(pageIndex, out var cached)) return cached;
        var links = await Document.GetLinksAsync(pageIndex, ct);
        _links[pageIndex] = links;
        return links;
    }

    public IReadOnlyList<PdfLink>? TryGetCachedLinks(int pageIndex) => _links.GetValueOrDefault(pageIndex);

    public IReadOnlyList<PdfAnnotation>? TryGetCachedAnnotations(int pageIndex) => _annotations.GetValueOrDefault(pageIndex);

    public async Task<IReadOnlyList<PdfAnnotation>> GetAnnotationsAsync(int pageIndex, int priority, CancellationToken ct = default)
    {
        if (_annotations.TryGetValue(pageIndex, out var cached)) return cached;
        int generation = _generations[pageIndex];
        var annotations = await Document.GetAnnotationsAsync(pageIndex, priority, ct);
        if (generation == _generations[pageIndex]) _annotations[pageIndex] = annotations;
        return annotations;
    }

    /// <summary>Adds a markup annotation over the text range. Returns the number of pages changed.</summary>
    public async Task<int> AddMarkupAsync(TextRange range, AnnotationKind kind, AnnotationColor color)
    {
        EnsureCanAnnotate();
        int changed = 0;
        for (int page = range.Start.PageIndex; page <= range.End.PageIndex; page++)
        {
            var text = await GetTextAsync(page, RenderPriority.Interactive);
            if (range.GetPageSpan(page, text.Length) is not { } span) continue;
            var rects = text.GetRangeRects(span.Start, span.End);
            if (rects.Count == 0) continue;
            await Document.AddMarkupAsync(page, kind, rects, color);
            Invalidate(page);
            changed++;
        }
        if (changed > 0) MarkDirty();
        return changed;
    }

    public async Task AddNoteAsync(int pageIndex, PointD position, string contents, AnnotationColor color)
    {
        EnsureCanAnnotate();
        await Document.AddNoteAsync(pageIndex, position, contents, color);
        Invalidate(pageIndex);
        MarkDirty();
    }

    public async Task SetAnnotationColorAsync(PdfAnnotation annotation, AnnotationColor color)
    {
        EnsureCanAnnotate();
        await Document.SetAnnotationColorAsync(annotation.PageIndex, annotation.Index, color);
        Invalidate(annotation.PageIndex);
        MarkDirty();
    }

    public async Task SetAnnotationContentsAsync(PdfAnnotation annotation, string contents)
    {
        EnsureCanAnnotate();
        await Document.SetAnnotationContentsAsync(annotation.PageIndex, annotation.Index, contents);
        Invalidate(annotation.PageIndex);
        MarkDirty();
    }

    public async Task RemoveAnnotationAsync(PdfAnnotation annotation)
    {
        EnsureCanAnnotate();
        await Document.RemoveAnnotationAsync(annotation.PageIndex, annotation.Index);
        Invalidate(annotation.PageIndex);
        MarkDirty();
    }

    // ---- Saving ----

    /// <summary>
    /// Saves changes to <paramref name="targetPath"/> (or the current file). Data is written to a temporary file in the
    /// target folder first and then swapped in, so a failure never corrupts the original.
    /// </summary>
    public async Task SaveAsync(string? targetPath = null)
    {
        if (!IsPdf) throw new NotSupportedException("Only PDF documents can be saved.");
        string target = Path.GetFullPath(targetPath ?? FilePath);
        string directory = Path.GetDirectoryName(target)!;
        string temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await Document.SaveCopyAsync(temp);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        bool sameFile = string.Equals(target, FilePath, StringComparison.OrdinalIgnoreCase);
        var old = Document;
        await old.DisposeAsync(); // releases the file handle so the file can be replaced

        try
        {
            if (sameFile) File.Replace(temp, target, null, ignoreMetadataErrors: true);
            else File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            Document = await PdfiumDocument.OpenAsync(old.FilePath, Password); // keep working on the original
            DocumentReplaced?.Invoke();
            throw;
        }

        Document = await PdfiumDocument.OpenAsync(target, Password);
        DisplayName = Path.GetFileName(target);
        _annotations.Clear();
        _links.Clear();
        _generations = new int[Document.PageCount];
        IsDirty = false;
        DocumentReplaced?.Invoke();
        DirtyChanged?.Invoke();
    }

    private void Invalidate(int pageIndex)
    {
        _generations[pageIndex]++;
        _annotations.Remove(pageIndex);
        PageInvalidated?.Invoke(pageIndex);
        AnnotationsChanged?.Invoke(pageIndex);
    }

    private void MarkDirty()
    {
        if (IsDirty) return;
        IsDirty = true;
        DirtyChanged?.Invoke();
    }

    private void EnsureCanAnnotate()
    {
        if (!CanAnnotate) throw new NotSupportedException("This document can't be annotated.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public ValueTask DisposeAsync() => Document.DisposeAsync();
}
