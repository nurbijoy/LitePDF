using System.Globalization;
using System.Text;
using LitePdf.Core;
using LitePdf.Core.Export;
using LitePdf.Core.Text;
using LitePdf.Pdfium.Interop;
using static LitePdf.Pdfium.Interop.NativeMethods;

namespace LitePdf.Pdfium;

/// <summary>
/// A PDF opened with PDFium. All native work is marshalled to <see cref="PdfiumWorker"/>; handles never leave it.
/// Geometry crossing this class's boundary is in normalized page coordinates.
/// </summary>
public sealed unsafe partial class PdfiumDocument : IPdfDocument, IPageContentSource
{
    private const int MaxOutlineItems = 20_000;
    private const int MaxOutlineDepth = 64;
    private const long MaxRenderPixels = 128_000_000;

    private readonly PdfiumWorker _worker = PdfiumWorker.Instance;
    private readonly FileAccessBridge _file;
    private readonly nint _document;
    private readonly PageFrame?[] _frames;
    private readonly PageSize[] _pageSizes;
    private bool _closed; // worker thread only
    private int _disposeRequested;

    private PdfiumDocument(string path, FileAccessBridge file, nint document, PageSize[] pageSizes)
    {
        FilePath = path;
        _file = file;
        _document = document;
        _pageSizes = pageSizes;
        _frames = new PageFrame?[pageSizes.Length];
    }

    public string FilePath { get; }

    public DocumentKind Kind => DocumentKind.Pdf;

    public int PageCount => _pageSizes.Length;

    public IReadOnlyList<PageSize> PageSizes => _pageSizes;

    public bool SupportsAnnotations => true;

    /// <summary>Opens a PDF. Throws <see cref="PdfiumException"/> (Error == Password when a password is needed or wrong).</summary>
    public static Task<PdfiumDocument> OpenAsync(string path, string? password = null) =>
        PdfiumWorker.Instance.RunAsync(() => Open(Path.GetFullPath(path), password), RenderPriority.Interactive);

    private static PdfiumDocument Open(string path, string? password)
    {
        FileAccessBridge file;
        try
        {
            file = FileAccessBridge.Open(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PdfiumException(PdfiumError.File, "The file could not be found.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PdfiumException(PdfiumError.File, "Access to the file was denied.", ex);
        }
        catch (IOException ex)
        {
            throw new PdfiumException(PdfiumError.File, "The file is in use or could not be read.", ex);
        }

        byte[]? passwordBytes = password is null ? null : Encoding.UTF8.GetBytes(password + "\0");
        nint document;
        fixed (byte* pw = passwordBytes)
            document = FPDF_LoadCustomDocument(file.Access, pw);

        if (document == 0)
        {
            uint error = FPDF_GetLastError();
            file.Dispose();
            throw PdfiumException.FromLastError(error);
        }

        int count = FPDF_GetPageCount(document);
        var sizes = new PageSize[Math.Max(0, count)];
        for (int i = 0; i < sizes.Length; i++)
        {
            double w, h;
            sizes[i] = FPDF_GetPageSizeByIndex(document, i, &w, &h) != 0 && w > 0 && h > 0
                ? new PageSize(w, h)
                : new PageSize(612, 792);
        }
        return new PdfiumDocument(path, file, document, sizes);
    }

    public Task<RenderedBitmap> RenderAsync(int pageIndex, int pageWidth, int pageHeight, int rotation,
        PixelRect? clip, RenderFlags flags, int priority, CancellationToken ct = default)
    {
        if (pageWidth <= 0 || pageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pageWidth), "Page size must be positive.");
        var region = clip ?? new PixelRect(0, 0, pageWidth, pageHeight);
        int x = Math.Clamp(region.X, 0, pageWidth), y = Math.Clamp(region.Y, 0, pageHeight);
        int w = Math.Min(region.Width, pageWidth - x), h = Math.Min(region.Height, pageHeight - y);
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(clip), "The render region is empty.");
        if ((long)w * h > MaxRenderPixels) throw new ArgumentOutOfRangeException(nameof(clip), "The render region is too large.");

        return _worker.RunAsync(() => WithPage(pageIndex, page =>
        {
            ct.ThrowIfCancellationRequested();
            nint bitmap = FPDFBitmap_Create(w, h, 0);
            if (bitmap == 0) throw new OutOfMemoryException($"Could not allocate a {w}×{h} bitmap.");
            try
            {
                FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
                FPDF_RenderPageBitmap(bitmap, page, -x, -y, pageWidth, pageHeight, rotation & 3, (int)flags);
                int stride = FPDFBitmap_GetStride(bitmap);
                byte* source = (byte*)FPDFBitmap_GetBuffer(bitmap);
                var pixels = new byte[w * h * 4];
                fixed (byte* dest = pixels)
                {
                    for (int row = 0; row < h; row++)
                        Buffer.MemoryCopy(source + row * stride, dest + row * w * 4, w * 4, w * 4);
                }
                return new RenderedBitmap(w, h, pixels);
            }
            finally
            {
                FPDFBitmap_Destroy(bitmap);
            }
        }), priority, ct);
    }

    public Task<PageText> GetTextAsync(int pageIndex, int priority, CancellationToken ct = default) =>
        _worker.RunAsync(() => WithPage(pageIndex, page => ExtractText(pageIndex, page)), priority, ct);

    private PageText ExtractText(int pageIndex, nint page)
    {
        var frame = GetFrame(pageIndex, page);
        nint textPage = FPDFText_LoadPage(page);
        if (textPage == 0) return PageText.Empty(pageIndex);
        try
        {
            int count = FPDFText_CountChars(textPage);
            if (count <= 0) return PageText.Empty(pageIndex);

            var sb = new StringBuilder(count);
            var boxes = new List<float>(count * 4);
            for (int i = 0; i < count; i++)
            {
                uint code = FPDFText_GetUnicode(textPage, i);
                if (code is 0 or '\r') continue; // PDFium emits "\r\n" between lines; keep only '\n'.
                if (code is 0xFFFE or 0x02) code = '-'; // hyphen markers at line ends

                RectD box = RectD.Empty;
                if (code != '\n' && FPDFText_IsGenerated(textPage, i) != 1)
                {
                    FS_RECTF r;
                    if (FPDFText_GetLooseCharBox(textPage, i, &r) != 0)
                    {
                        box = frame.ToNormalized(r.Left, r.Top, r.Right, r.Bottom);
                    }
                    else
                    {
                        double left, right, bottom, top;
                        if (FPDFText_GetCharBox(textPage, i, &left, &right, &bottom, &top) != 0)
                            box = frame.ToNormalized(left, top, right, bottom);
                    }
                }

                string chars = code <= 0xFFFF ? ((char)code).ToString() : char.ConvertFromUtf32((int)Math.Min(code, 0x10FFFF));
                foreach (char c in chars)
                {
                    sb.Append(c);
                    if (box.IsEmpty) boxes.AddRange([float.NaN, float.NaN, float.NaN, float.NaN]);
                    else boxes.AddRange([(float)box.Left, (float)box.Top, (float)box.Right, (float)box.Bottom]);
                }
            }
            return PageText.Create(pageIndex, TextSource.Pdf, sb.ToString(), boxes.ToArray());
        }
        finally
        {
            FPDFText_ClosePage(textPage);
        }
    }

    public Task<bool> HasImagesAsync(int pageIndex, CancellationToken ct = default) =>
        _worker.RunAsync(() => WithPage(pageIndex, page =>
        {
            int count = FPDFPage_CountObjects(page);
            for (int i = 0; i < count; i++)
            {
                int type = FPDFPageObj_GetType(FPDFPage_GetObject(page, i));
                if (type == FPDF_PAGEOBJ_IMAGE) return true;
                if (type == FPDF_PAGEOBJ_FORM && FormContainsImage(FPDFPage_GetObject(page, i), 0)) return true;
            }
            return false;
        }), RenderPriority.Interactive, ct);

    private static bool FormContainsImage(nint form, int depth)
    {
        if (depth > 8) return false;
        int count = FPDFFormObj_CountObjects(form);
        for (int i = 0; i < count; i++)
        {
            nint obj = FPDFFormObj_GetObject(form, (uint)i);
            int type = FPDFPageObj_GetType(obj);
            if (type == FPDF_PAGEOBJ_IMAGE) return true;
            if (type == FPDF_PAGEOBJ_FORM && FormContainsImage(obj, depth + 1)) return true;
        }
        return false;
    }

    public Task<IReadOnlyList<OutlineItem>> GetOutlineAsync(CancellationToken ct = default) =>
        _worker.RunAsync<IReadOnlyList<OutlineItem>>(() =>
        {
            EnsureOpen();
            int budget = MaxOutlineItems;
            var visited = new HashSet<nint>();
            return ReadOutline(0, 0, visited, ref budget, ct);
        }, RenderPriority.Interactive, ct);

    private List<OutlineItem> ReadOutline(nint parent, int depth, HashSet<nint> visited, ref int budget, CancellationToken ct)
    {
        var items = new List<OutlineItem>();
        if (depth > MaxOutlineDepth) return items;

        for (nint bookmark = FPDFBookmark_GetFirstChild(_document, parent);
             bookmark != 0 && budget > 0 && visited.Add(bookmark);
             bookmark = FPDFBookmark_GetNextSibling(_document, bookmark))
        {
            ct.ThrowIfCancellationRequested();
            budget--;
            string title = ReadWideString((buf, len) => FPDFBookmark_GetTitle(bookmark, buf, len)).Trim();

            nint dest = FPDFBookmark_GetDest(_document, bookmark);
            if (dest == 0)
            {
                nint action = FPDFBookmark_GetAction(bookmark);
                if (action != 0 && FPDFAction_GetType(action) == PDFACTION_GOTO)
                    dest = FPDFAction_GetDest(_document, action);
            }

            var children = ReadOutline(bookmark, depth + 1, visited, ref budget, ct);
            items.Add(new OutlineItem(title, ResolveDestination(dest), FPDFBookmark_GetCount(bookmark) > 0, children));
        }
        return items;
    }

    private Destination ResolveDestination(nint dest)
    {
        if (dest == 0) return new Destination(-1);
        int pageIndex = FPDFDest_GetDestPageIndex(_document, dest);
        if (pageIndex < 0 || pageIndex >= PageCount) return new Destination(-1);

        int hasX, hasY, hasZoom;
        float x, y, zoom;
        if (FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) != 0 && hasY != 0)
        {
            var frame = GetFrame(pageIndex);
            var p = frame.ToNormalized(hasX != 0 ? x : frame.Left, y);
            return new Destination(pageIndex, Math.Clamp(p.Y, 0, 1));
        }
        return new Destination(pageIndex);
    }

    public Task<IReadOnlyList<PdfLink>> GetLinksAsync(int pageIndex, CancellationToken ct = default) =>
        _worker.RunAsync<IReadOnlyList<PdfLink>>(() => WithPage(pageIndex, page =>
        {
            var frame = GetFrame(pageIndex, page);
            var links = new List<PdfLink>();
            int position = 0;
            nint link;
            while (FPDFLink_Enumerate(page, &position, &link) != 0)
            {
                FS_RECTF r;
                if (link == 0 || FPDFLink_GetAnnotRect(link, &r) == 0) continue;
                var bounds = frame.ToNormalized(r.Left, r.Top, r.Right, r.Bottom);

                Destination target = ResolveDestination(FPDFLink_GetDest(_document, link));
                string? uri = null;
                nint action = FPDFLink_GetAction(link);
                if (action != 0)
                {
                    uint type = FPDFAction_GetType(action);
                    if (type == PDFACTION_GOTO && !target.IsValid)
                        target = ResolveDestination(FPDFAction_GetDest(_document, action));
                    else if (type == PDFACTION_URI)
                        uri = ReadUri(action);
                }

                if (target.IsValid || !string.IsNullOrWhiteSpace(uri))
                    links.Add(new PdfLink(bounds, target, uri));
            }
            return links;
        }), RenderPriority.Interactive, ct);

    private string? ReadUri(nint action)
    {
        uint length = FPDFAction_GetURIPath(_document, action, null, 0);
        if (length <= 1) return null;
        var buffer = new byte[length];
        fixed (byte* p = buffer) FPDFAction_GetURIPath(_document, action, p, length);
        return Encoding.UTF8.GetString(buffer, 0, (int)length - 1).Trim();
    }

    public Task<IReadOnlyList<PdfAnnotation>> GetAnnotationsAsync(int pageIndex, int priority, CancellationToken ct = default) =>
        _worker.RunAsync<IReadOnlyList<PdfAnnotation>>(() => WithPage(pageIndex, page =>
        {
            var frame = GetFrame(pageIndex, page);
            var result = new List<PdfAnnotation>();
            int count = FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                nint annot = FPDFPage_GetAnnot(page, i);
                if (annot == 0) continue;
                try
                {
                    var kind = FPDFAnnot_GetSubtype(annot) switch
                    {
                        FPDF_ANNOT_HIGHLIGHT => AnnotationKind.Highlight,
                        FPDF_ANNOT_UNDERLINE => AnnotationKind.Underline,
                        FPDF_ANNOT_STRIKEOUT => AnnotationKind.StrikeOut,
                        FPDF_ANNOT_SQUIGGLY => AnnotationKind.Squiggly,
                        FPDF_ANNOT_TEXT => AnnotationKind.Note,
                        _ => AnnotationKind.Other,
                    };
                    if (kind == AnnotationKind.Other) continue;

                    FS_RECTF r;
                    var bounds = FPDFAnnot_GetRect(annot, &r) != 0 ? frame.ToNormalized(r.Left, r.Top, r.Right, r.Bottom) : RectD.Empty;

                    var quads = new List<RectD>();
                    nuint quadCount = FPDFAnnot_CountAttachmentPoints(annot);
                    for (nuint q = 0; q < quadCount; q++)
                    {
                        FS_QUADPOINTSF qp;
                        if (FPDFAnnot_GetAttachmentPoints(annot, q, &qp) == 0) continue;
                        var p1 = frame.ToNormalized(qp.X1, qp.Y1);
                        var p2 = frame.ToNormalized(qp.X2, qp.Y2);
                        var p3 = frame.ToNormalized(qp.X3, qp.Y3);
                        var p4 = frame.ToNormalized(qp.X4, qp.Y4);
                        quads.Add(new RectD(
                            Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X)),
                            Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y)),
                            Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X)),
                            Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y))));
                    }
                    if (bounds.IsEmpty && quads.Count > 0) bounds = quads.Aggregate(RectD.Empty, (a, b) => a.Union(b));

                    string contents = ReadWideString((buf, len) => FPDFAnnot_GetStringValue(annot, (byte*)Utf8Z("Contents"), buf, len));
                    string author = ReadWideString((buf, len) => FPDFAnnot_GetStringValue(annot, (byte*)Utf8Z("T"), buf, len));
                    result.Add(new PdfAnnotation(pageIndex, i, kind, bounds, quads, ReadAnnotationColor(annot, kind), contents)
                    {
                        Author = author,
                    });
                }
                finally
                {
                    FPDFPage_CloseAnnot(annot);
                }
            }
            return result;
        }), priority, ct);

    private static AnnotationColor? ReadAnnotationColor(nint annot, AnnotationKind kind)
    {
        uint r, g, b, a;
        if (FPDFAnnot_GetColor(annot, FPDFANNOT_COLORTYPE_Color, &r, &g, &b, &a) != 0)
            return new AnnotationColor((byte)r, (byte)g, (byte)b);

        // Annotations with an appearance stream report no /C through the API; read the drawn color instead.
        int count = FPDFAnnot_GetObjectCount(annot);
        for (int i = 0; i < count; i++)
            if (ReadObjectColor(FPDFAnnot_GetObject(annot, i), kind == AnnotationKind.Highlight, 0) is { } c)
                return c;
        return kind == AnnotationKind.Highlight ? AnnotationColor.Yellow : null;
    }

    private static AnnotationColor? ReadObjectColor(nint obj, bool preferFill, int depth)
    {
        if (obj == 0 || depth > 4) return null;
        int type = FPDFPageObj_GetType(obj);
        if (type == FPDF_PAGEOBJ_FORM)
        {
            int count = FPDFFormObj_CountObjects(obj);
            for (int i = 0; i < count; i++)
                if (ReadObjectColor(FPDFFormObj_GetObject(obj, (uint)i), preferFill, depth + 1) is { } c)
                    return c;
            return null;
        }
        if (type != FPDF_PAGEOBJ_PATH) return null;
        uint r, g, b, a;
        bool ok = preferFill
            ? FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a) != 0 || FPDFPageObj_GetStrokeColor(obj, &r, &g, &b, &a) != 0
            : FPDFPageObj_GetStrokeColor(obj, &r, &g, &b, &a) != 0 || FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a) != 0;
        return ok ? new AnnotationColor((byte)r, (byte)g, (byte)b) : null;
    }

    public Task AddMarkupAsync(int pageIndex, AnnotationKind kind, IReadOnlyList<RectD> lineRects, AnnotationColor color, CancellationToken ct = default)
    {
        int subtype = kind switch
        {
            AnnotationKind.Highlight => FPDF_ANNOT_HIGHLIGHT,
            AnnotationKind.Underline => FPDF_ANNOT_UNDERLINE,
            AnnotationKind.StrikeOut => FPDF_ANNOT_STRIKEOUT,
            AnnotationKind.Squiggly => FPDF_ANNOT_SQUIGGLY,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Only text markup kinds are supported."),
        };
        if (lineRects.Count == 0) throw new ArgumentException("At least one rectangle is required.", nameof(lineRects));

        return _worker.RunAsync(() => WithPage(pageIndex, page =>
        {
            var frame = GetFrame(pageIndex, page);
            nint annot = FPDFPage_CreateAnnot(page, subtype);
            if (annot == 0) throw new PdfiumException(PdfiumError.Unknown, "The annotation could not be created.");
            try
            {
                var union = RectD.Empty;
                foreach (var rect in lineRects)
                {
                    // Quad order used by Acrobat: top-left, top-right, bottom-left, bottom-right (as displayed).
                    var tl = frame.FromNormalized(rect.Left, rect.Top);
                    var tr = frame.FromNormalized(rect.Right, rect.Top);
                    var bl = frame.FromNormalized(rect.Left, rect.Bottom);
                    var br = frame.FromNormalized(rect.Right, rect.Bottom);
                    var quad = new FS_QUADPOINTSF
                    {
                        X1 = (float)tl.X, Y1 = (float)tl.Y, X2 = (float)tr.X, Y2 = (float)tr.Y,
                        X3 = (float)bl.X, Y3 = (float)bl.Y, X4 = (float)br.X, Y4 = (float)br.Y,
                    };
                    FPDFAnnot_AppendAttachmentPoints(annot, &quad);
                    union = union.Union(RectD.FromPoints(tl, br));
                }

                var pdfRect = new FS_RECTF { Left = (float)union.Left, Right = (float)union.Right, Bottom = (float)union.Top, Top = (float)union.Bottom };
                FPDFAnnot_SetRect(annot, &pdfRect);
                FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, 255);
                FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);
                SetAuthorAndDate(annot);
                return true;
            }
            finally
            {
                FPDFPage_CloseAnnot(annot);
            }
        }), RenderPriority.Interactive, ct);
    }

    public Task AddNoteAsync(int pageIndex, PointD position, string contents, AnnotationColor color, CancellationToken ct = default) =>
        _worker.RunAsync(() => WithPage(pageIndex, page =>
        {
            var frame = GetFrame(pageIndex, page);
            nint annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_TEXT);
            if (annot == 0) throw new PdfiumException(PdfiumError.Unknown, "The note could not be created.");
            try
            {
                const double iconPoints = 20;
                var anchor = frame.FromNormalized(position.X, position.Y);
                var rect = new FS_RECTF
                {
                    Left = (float)anchor.X,
                    Right = (float)(anchor.X + iconPoints),
                    Top = (float)anchor.Y,
                    Bottom = (float)(anchor.Y - iconPoints),
                };
                FPDFAnnot_SetRect(annot, &rect);
                FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, 255);
                FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);
                SetString(annot, "Contents", contents);
                SetAuthorAndDate(annot);
                return true;
            }
            finally
            {
                FPDFPage_CloseAnnot(annot);
            }
        }), RenderPriority.Interactive, ct);

    public Task SetAnnotationColorAsync(int pageIndex, int annotationIndex, AnnotationColor color, CancellationToken ct = default) =>
        WithAnnotationAsync(pageIndex, annotationIndex, annot =>
        {
            // PDFium refuses to recolor annotations that have an appearance stream; drop it so PDFium regenerates one.
            FPDFAnnot_SetAP(annot, FPDF_ANNOT_APPEARANCEMODE_NORMAL, null);
            if (FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, 255) == 0)
                throw new PdfiumException(PdfiumError.Unknown, "The annotation color could not be changed.");
            SetAuthorAndDate(annot);
        }, ct);

    public Task SetAnnotationContentsAsync(int pageIndex, int annotationIndex, string contents, CancellationToken ct = default) =>
        WithAnnotationAsync(pageIndex, annotationIndex, annot =>
        {
            SetString(annot, "Contents", contents);
            SetAuthorAndDate(annot);
        }, ct);

    public Task RemoveAnnotationAsync(int pageIndex, int annotationIndex, CancellationToken ct = default) =>
        _worker.RunAsync(() => WithPage(pageIndex, page =>
        {
            if (FPDFPage_RemoveAnnot(page, annotationIndex) == 0)
                throw new PdfiumException(PdfiumError.Unknown, "The annotation could not be removed.");
            return true;
        }), RenderPriority.Interactive, ct);

    private Task WithAnnotationAsync(int pageIndex, int annotationIndex, Action<nint> action, CancellationToken ct) =>
        _worker.RunAsync(() => WithPage(pageIndex, page =>
        {
            nint annot = FPDFPage_GetAnnot(page, annotationIndex);
            if (annot == 0) throw new PdfiumException(PdfiumError.Unknown, "The annotation no longer exists.");
            try
            {
                action(annot);
                return true;
            }
            finally
            {
                FPDFPage_CloseAnnot(annot);
            }
        }), RenderPriority.Interactive, ct);

    public Task SaveCopyAsync(string path, CancellationToken ct = default)
    {
        string target = Path.GetFullPath(path);
        if (string.Equals(target, FilePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save to a temporary file first; the open file cannot be overwritten directly.");

        return _worker.RunAsync(() =>
        {
            EnsureOpen();
            using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            using (var writer = new FileWriteBridge(stream))
            {
                // Incremental saves append changes and keep the original bytes (and signatures) intact.
                bool ok = FPDF_SaveAsCopy(_document, writer.Context, FPDF_INCREMENTAL) != 0 && writer.Error is null;
                if (!ok)
                {
                    stream.SetLength(0);
                    ok = FPDF_SaveAsCopy(_document, writer.Context, FPDF_NO_INCREMENTAL) != 0 && writer.Error is null;
                }
                if (!ok) throw new IOException("The document could not be written.", writer.Error);
            }
            stream.Flush(flushToDisk: true);
        }, RenderPriority.Interactive, ct);
    }

    public Task<DocumentInfo> GetInfoAsync(CancellationToken ct = default) =>
        _worker.RunAsync(() =>
        {
            EnsureOpen();
            int version;
            string? pdfVersion = FPDF_GetFileVersion(_document, &version) != 0 ? $"{version / 10}.{version % 10}" : null;
            string? Meta(string tag)
            {
                string value = ReadWideString((buf, len) => FPDF_GetMetaText(_document, (byte*)Utf8Z(tag), buf, len)).Trim();
                return value.Length == 0 ? null : value;
            }
            return new DocumentInfo(
                FilePath,
                _file.Length,
                PageCount,
                pdfVersion,
                FPDF_GetSecurityHandlerRevision(_document) != -1,
                Meta("Title"),
                Meta("Author"),
                Meta("Subject"),
                Meta("Keywords"),
                Meta("Creator"),
                Meta("Producer"),
                ParsePdfDate(Meta("CreationDate")),
                ParsePdfDate(Meta("ModDate")));
        }, RenderPriority.Interactive, ct);

    public Task<string?> GetFileIdentifierAsync(CancellationToken ct = default) =>
        _worker.RunAsync(() =>
        {
            EnsureOpen();
            uint length = FPDF_GetFileIdentifier(_document, 0, null, 0);
            if (length <= 1) return null;
            var buffer = new byte[length];
            fixed (byte* p = buffer) FPDF_GetFileIdentifier(_document, 0, p, length);
            return Convert.ToHexString(buffer, 0, (int)length - 1);
        }, RenderPriority.Interactive, ct);

    /// <summary>Test hook: maps a point in PDF user space to device pixels using PDFium itself.</summary>
    internal Task<(int X, int Y)> PageToDeviceAsync(int pageIndex, double x, double y, int width, int height) =>
        _worker.RunAsync(() => WithPage(pageIndex, page =>
        {
            int dx, dy;
            FPDF_PageToDevice(page, 0, 0, width, height, 0, x, y, &dx, &dy);
            return (dx, dy);
        }), RenderPriority.Interactive);

    /// <summary>Test hook: normalized coordinates of a point in PDF user space.</summary>
    internal Task<PointD> ToNormalizedAsync(int pageIndex, double x, double y) =>
        _worker.RunAsync(() => { EnsureOpen(); return GetFrame(pageIndex).ToNormalized(x, y); }, RenderPriority.Interactive);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 1) return ValueTask.CompletedTask;
        return new ValueTask(_worker.RunAsync(() =>
        {
            if (_closed) return;
            _closed = true;
            FPDF_CloseDocument(_document);
            _file.Dispose();
        }, priority: -1));
    }

    // ---- helpers (worker thread only) ----

    private void EnsureOpen()
    {
        if (_closed) throw new ObjectDisposedException(nameof(PdfiumDocument));
    }

    private T WithPage<T>(int pageIndex, Func<nint, T> action)
    {
        EnsureOpen();
        if ((uint)pageIndex >= (uint)PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        nint page = FPDF_LoadPage(_document, pageIndex);
        if (page == 0) throw new PdfiumException(PdfiumError.Page, $"Page {pageIndex + 1} could not be loaded.");
        try
        {
            return action(page);
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    private PageFrame GetFrame(int pageIndex, nint page = 0)
    {
        if (_frames[pageIndex] is { } cached) return cached;
        if (page == 0) return WithPage(pageIndex, p => GetFrame(pageIndex, p));

        FS_RECTF box;
        PageFrame frame = FPDF_GetPageBoundingBox(page, &box) != 0 && box.Right > box.Left && box.Top > box.Bottom
            ? new PageFrame(box.Left, box.Top, box.Right, box.Bottom, FPDFPage_GetRotation(page) & 3)
            : new PageFrame(0, _pageSizes[pageIndex].Height, _pageSizes[pageIndex].Width, 0, 0);
        _frames[pageIndex] = frame;
        return frame;
    }

    private static void SetString(nint annot, string key, string value)
    {
        fixed (char* v = value + "\0")
            FPDFAnnot_SetStringValue(annot, (byte*)Utf8Z(key), v);
    }

    private static void SetAuthorAndDate(nint annot)
    {
        SetString(annot, "T", Environment.UserName);
        var now = DateTimeOffset.Now;
        string offset = now.Offset < TimeSpan.Zero ? "-" : "+";
        SetString(annot, "M", $"D:{now:yyyyMMddHHmmss}{offset}{Math.Abs(now.Offset.Hours):00}'{Math.Abs(now.Offset.Minutes):00}'");
    }

    private delegate uint WideStringReader(char* buffer, uint byteLength);

    /// <summary>Reads a UTF-16LE string from a PDFium "query length, then fill" API whose length is in bytes incl. NUL.</summary>
    private static string ReadWideString(WideStringReader read)
    {
        uint bytes = read(null, 0);
        if (bytes <= 2) return string.Empty;
        var buffer = new char[bytes / 2];
        fixed (char* p = buffer) read(p, (uint)buffer.Length * 2);
        int length = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, length < 0 ? buffer.Length : length);
    }

    private static readonly Dictionary<string, nint> Utf8ZCache = new();

    /// <summary>Process-lifetime NUL-terminated ASCII copies of dictionary keys (a handful of constants).</summary>
    private static nint Utf8Z(string key)
    {
        lock (Utf8ZCache)
        {
            if (Utf8ZCache.TryGetValue(key, out var ptr)) return ptr;
            var bytes = Encoding.ASCII.GetBytes(key + "\0");
            ptr = (nint)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>((void*)ptr, bytes.Length));
            Utf8ZCache[key] = ptr;
            return ptr;
        }
    }

    private static DateTimeOffset? ParsePdfDate(string? value)
    {
        // Format: D:YYYYMMDDHHmmSSOHH'mm' with every part after the year optional.
        if (string.IsNullOrWhiteSpace(value)) return null;
        string s = value.StartsWith("D:", StringComparison.Ordinal) ? value[2..] : value;
        int Part(int start, int length, int fallback) =>
            s.Length >= start + length && int.TryParse(s.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        try
        {
            int year = Part(0, 4, -1);
            if (year < 1) return null;
            var offset = TimeSpan.Zero;
            if (s.Length > 14 && s[14] is '+' or '-')
            {
                offset = new TimeSpan(Part(15, 2, 0), Part(18, 2, 0), 0);
                if (s[14] == '-') offset = -offset;
            }
            return new DateTimeOffset(year, Math.Clamp(Part(4, 2, 1), 1, 12), Math.Clamp(Part(6, 2, 1), 1, 31),
                Math.Clamp(Part(8, 2, 0), 0, 23), Math.Clamp(Part(10, 2, 0), 0, 59), Math.Clamp(Part(12, 2, 0), 0, 59), offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Maps PDF user space to normalized page coordinates: the page bounding box (crop ∩ media) scaled to 0..1,
/// top-left origin, with the page's /Rotate applied, matching how PDFium renders the page.
/// </summary>
internal readonly record struct PageFrame(double Left, double Top, double Right, double Bottom, int Rotation)
{
    public PointD ToNormalized(double x, double y)
    {
        double nx = (x - Left) / (Right - Left);
        double ny = (Top - y) / (Top - Bottom);
        return Rotation switch
        {
            1 => new PointD(1 - ny, nx),
            2 => new PointD(1 - nx, 1 - ny),
            3 => new PointD(ny, 1 - nx),
            _ => new PointD(nx, ny),
        };
    }

    public RectD ToNormalized(double left, double top, double right, double bottom) =>
        RectD.FromPoints(ToNormalized(left, top), ToNormalized(right, bottom));

    public PointD FromNormalized(double dx, double dy)
    {
        var (nx, ny) = Rotation switch
        {
            1 => (dy, 1 - dx),
            2 => (1 - dx, 1 - dy),
            3 => (1 - dy, dx),
            _ => (dx, dy),
        };
        return new PointD(Left + nx * (Right - Left), Top - ny * (Top - Bottom));
    }
}
