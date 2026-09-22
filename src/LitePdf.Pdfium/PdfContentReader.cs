using System.Text;
using LitePdf.Core;
using LitePdf.Core.Export;
using LitePdf.Core.Text;
using LitePdf.Pdfium.Interop;
using static LitePdf.Pdfium.Interop.NativeMethods;

namespace LitePdf.Pdfium;

/// <summary>
/// Reads the drawing behind a page — styled characters, placed images, vector artwork, thin rules, filled
/// panels and the structure tree — for the DOCX export. Runs on the PDFium worker like everything else;
/// geometry leaves in normalized page coordinates.
/// </summary>
public sealed unsafe partial class PdfiumDocument
{
    /// <summary>Below this many visible characters a page is a scan, and its invisible OCR layer is the text.</summary>
    private const int MinVisibleCharsForRealText = 8;

    /// <summary>An image covering this much of a scanned page is the scan itself, not an illustration.</summary>
    private const double ScanBackgroundCoverage = 0.6;

    private const int MaxImagesPerPage = 64;
    private const long MaxImageBytes = 48L * 1024 * 1024;

    /// <summary>How much sharper the stored image must be before the page region is re-rendered for it.</summary>
    private const double ResolutionGainToReRender = 1.3;

    private const long MaxRegionPixels = 40_000_000;

    /// <summary>Vector artwork is rasterized at this resolution: crisp on paper, and still a small PNG.</summary>
    private const double DrawingDpi = 300;

    private const int MaxDrawingsPerPage = 8;

    /// <summary>A filled rectangle covering this much of the page is a background, never a drawing.</summary>
    private const double BackgroundCoverage = 0.5;

    private const int MaxFormDepth = 8;
    private const int MaxStructElements = 20_000;
    private const int MaxStructDepth = 24;
    private const long MaxFontBytes = 24L * 1024 * 1024;

    public Task<PageContent> GetPageContentAsync(
        int pageIndex, int priority, PageContentRequest? request = null, CancellationToken ct = default) =>
        _worker.RunAsync(
            () => WithPage(pageIndex, page => ReadContent(pageIndex, page, request ?? PageContentRequest.Default, ct)),
            priority, ct);

    private PageContent ReadContent(int pageIndex, nint page, PageContentRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var frame = GetFrame(pageIndex, page);
        var size = _pageSizes[pageIndex];

        var read = ReadStyledText(pageIndex, page, frame, request);
        ct.ThrowIfCancellationRequested();

        bool hasText = read.FromInvisibleLayer || read.Text.VisibleCharCount >= MinVisibleCharsForRealText;
        var scan = ReadObjects(page, frame, size, read.Text, hasText, request, ct);
        ct.ThrowIfCancellationRequested();

        return new PageContent(pageIndex, size, read.Text, read.Spans, scan.Images, scan.Rules)
        {
            Fills = scan.Fills,
            Marks = read.Marks,
            Fonts = read.Fonts,
            Tags = request.Tags ? ReadTags(page) : [],
        };
    }

    // ---- text ----

    private readonly record struct CharRecord(uint Code, RectD Box, TextStyle Style, bool Invisible, int MarkedContentId);

    private sealed record StyledText(
        PageText Text, List<StyledSpan> Spans, List<MarkedRange> Marks, List<EmbeddedFont> Fonts, bool FromInvisibleLayer)
    {
        public static StyledText Empty(int pageIndex) => new(PageText.Empty(pageIndex), [], [], [], false);
    }

    private static StyledText ReadStyledText(int pageIndex, nint page, PageFrame frame, PageContentRequest request)
    {
        nint textPage = FPDFText_LoadPage(page);
        if (textPage == 0) return StyledText.Empty(pageIndex);

        try
        {
            int count = FPDFText_CountChars(textPage);
            if (count <= 0) return StyledText.Empty(pageIndex);

            var records = new List<CharRecord>(count);
            var fonts = new List<EmbeddedFont>();
            var seenFonts = new HashSet<nint>();
            int visible = 0, invisible = 0;

            // Style is a property of the text object, not of the character, so it is read once per object.
            // A dense page has thousands of characters and a handful of objects; reading per character
            // would multiply the native calls by an order of magnitude for the same answer.
            nint lastObject = -1;
            var style = TextStyle.Default;
            bool lastInvisible = false;
            int lastMarkedContentId = -1;

            for (int i = 0; i < count; i++)
            {
                uint code = FPDFText_GetUnicode(textPage, i);
                if (code is 0 or '\r') continue;
                if (code is 0xFFFE or 0x02) code = '-';

                bool generated = code != '\n' && FPDFText_IsGenerated(textPage, i) == 1;
                nint textObject = FPDFText_GetTextObject(textPage, i);
                if (textObject != lastObject)
                {
                    lastObject = textObject;
                    (style, lastInvisible) = ReadStyle(textPage, i, textObject);
                    lastMarkedContentId = textObject != 0 ? FPDFPageObj_GetMarkedContentID(textObject) : -1;
                    if (request.Fonts && textObject != 0) CollectFont(textObject, style, fonts, seenFonts);
                }

                RectD box = RectD.Empty;
                if (code != '\n' && !generated)
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

                if (code != '\n' && !char.IsWhiteSpace((char)Math.Min(code, 0xFFFF)))
                {
                    if (lastInvisible) invisible++;
                    else visible++;
                }

                records.Add(new CharRecord(code, box, style, lastInvisible, lastMarkedContentId));
            }

            // A searchable scan carries its whole text in an invisible layer under the picture. Keeping both
            // would write every word twice; keeping neither would lose the page. So the layer is used only
            // when there is no real text to use instead.
            bool useInvisible = visible < MinVisibleCharsForRealText && invisible > 0;

            var sb = new StringBuilder(records.Count);
            var boxes = new List<float>(records.Count * 4);
            var spans = new List<StyledSpan>();
            var marks = new List<MarkedRange>();

            foreach (var record in records)
            {
                if (record.Invisible != useInvisible && record.Code != '\n') continue;

                string chars = record.Code <= 0xFFFF
                    ? ((char)record.Code).ToString()
                    : char.ConvertFromUtf32((int)Math.Min(record.Code, 0x10FFFF));

                int start = sb.Length;
                foreach (char c in chars)
                {
                    sb.Append(c);
                    if (record.Box.IsEmpty) boxes.AddRange([float.NaN, float.NaN, float.NaN, float.NaN]);
                    else boxes.AddRange([(float)record.Box.Left, (float)record.Box.Top, (float)record.Box.Right, (float)record.Box.Bottom]);
                }

                if (spans.Count > 0 && spans[^1].End == start && spans[^1].Style.Matches(record.Style))
                    spans[^1] = spans[^1] with { End = sb.Length };
                else
                    spans.Add(new StyledSpan(start, sb.Length, record.Style));

                if (record.MarkedContentId >= 0)
                {
                    if (marks.Count > 0 && marks[^1].End == start && marks[^1].MarkedContentId == record.MarkedContentId)
                        marks[^1] = marks[^1] with { End = sb.Length };
                    else
                        marks.Add(new MarkedRange(start, sb.Length, record.MarkedContentId));
                }
            }

            var text = PageText.Create(pageIndex, TextSource.Pdf, sb.ToString(), boxes.ToArray());
            return new StyledText(text, spans, marks, fonts, useInvisible);
        }
        finally
        {
            FPDFText_ClosePage(textPage);
        }
    }

    private static (TextStyle Style, bool Invisible) ReadStyle(nint textPage, int index, nint textObject)
    {
        int flags = 0;
        string name = ReadFontName(textPage, index, &flags);

        int weight = FPDFText_GetFontWeight(textPage, index);
        bool serif = (flags & FPDF_FONTFLAG_SERIF) != 0;
        bool bold = weight >= 600 || (flags & FPDF_FONTFLAG_FORCEBOLD) != 0 || FontMapper.NameSaysBold(name);
        bool italic = (flags & FPDF_FONTFLAG_ITALIC) != 0 || FontMapper.NameSaysItalic(name);

        // The text state size ignores the text matrix, so a matrix-scaled run would report the wrong size.
        double size = FPDFText_GetFontSize(textPage, index);
        FS_MATRIX matrix;
        if (FPDFText_GetMatrix(textPage, index, &matrix) != 0)
        {
            double determinant = Math.Abs((double)matrix.A * matrix.D - (double)matrix.B * matrix.C);
            if (determinant > 0) size *= Math.Sqrt(determinant);
        }
        if (size is <= 0 or > 1600) size = 11;

        uint r = 0, g = 0, b = 0, a = 255;
        if (FPDFText_GetFillColor(textPage, index, &r, &g, &b, &a) == 0) (r, g, b) = (0, 0, 0);
        uint color = ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);

        bool invisible = textObject != 0 && FPDFTextObj_GetTextRenderMode(textObject) == FPDF_TEXTRENDERMODE_INVISIBLE;

        return (new TextStyle(FontMapper.Family(name, serif), Math.Round(size, 2), bold, italic, color), invisible);
    }

    private static string ReadFontName(nint textPage, int index, int* flags)
    {
        const int Capacity = 160;
        byte* buffer = stackalloc byte[Capacity];
        uint written = FPDFText_GetFontInfo(textPage, index, buffer, Capacity, flags);
        if (written <= 1) return string.Empty;

        int length = (int)Math.Min(written, Capacity) - 1; // the reported length includes the NUL
        while (length > 0 && buffer[length - 1] == 0) length--;
        return Encoding.UTF8.GetString(buffer, length);
    }

    // ---- embedded fonts ----

    /// <summary>
    /// Lifts the font program out of the PDF for the optional embedding. Only sfnt fonts (TrueType and
    /// OpenType) are taken: they are the only kind Word can embed, and a Type 1 or bare CFF would be a part
    /// Word refuses to open the document over.
    /// </summary>
    private static void CollectFont(nint textObject, TextStyle style, List<EmbeddedFont> fonts, HashSet<nint> seen)
    {
        nint font = FPDFTextObj_GetFont(textObject);
        if (font == 0 || !seen.Add(font)) return;
        if (FPDFFont_GetIsEmbedded(font) != 1) return;

        nuint length = 0;
        if (FPDFFont_GetFontData(font, null, 0, &length) == 0) return;
        if (length == 0 || (long)length > MaxFontBytes) return;

        var data = new byte[length];
        nuint written = 0;
        fixed (byte* p = data)
        {
            if (FPDFFont_GetFontData(font, p, length, &written) == 0 || written != length) return;
        }
        if (!LooksLikeSfnt(data)) return;

        // Every font taken out of a PDF is declared a subset. A PDF names one with a tag ("ABCDEF+Times"),
        // but PDFium hands back the stripped name, so there is nothing left to tell a subset from a whole
        // font — and nearly all of them are subsets. Saying so is the safe direction: Word then knows the
        // font may not hold every glyph, rather than being surprised by a letter that has none.
        fonts.Add(new EmbeddedFont(style.FontFamily, style.Bold, style.Italic, data, IsSubset: true));
    }

    private static bool LooksLikeSfnt(byte[] data)
    {
        if (data.Length < 12) return false;
        uint tag = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
        return tag is 0x00010000 or 0x74727565 /* true */ or 0x4F54544F /* OTTO */ or 0x74746366 /* ttcf */;
    }

    // ---- page objects ----

    /// <summary>An image found during the walk. Extraction happens afterwards, once text can be hidden.</summary>
    private readonly record struct ImageTask(nint Object, RectD Bounds, bool Nested, int MarkedContentId);

    private sealed class ObjectScan
    {
        public long Budget = MaxImageBytes;
        public List<PlacedImage> Images { get; } = [];
        public List<RuleSegment> Rules { get; } = [];
        public List<FilledArea> Fills { get; } = [];
        public List<ImageTask> ImageTasks { get; } = [];
        public List<RectD> DrawingParts { get; } = [];

        /// <summary>Text and image objects, which are switched off while a drawing region is rasterized.</summary>
        public List<nint> TextObjects { get; } = [];

        public List<nint> ImageObjects { get; } = [];
    }

    private sealed class ScanContext
    {
        public required PageFrame Frame { get; init; }
        public required PageSize Size { get; init; }
        public required bool PageHasText { get; init; }
        public required PageContentRequest Request { get; init; }
        public required List<PointD> TextCentres { get; init; }

        public int CountTextInside(RectD bounds, int stopAt)
        {
            int found = 0;
            foreach (var centre in TextCentres)
            {
                if (!bounds.Contains(centre)) continue;
                if (++found >= stopAt) break;
            }
            return found;
        }
    }

    private ObjectScan ReadObjects(
        nint page, PageFrame frame, PageSize size, PageText text, bool pageHasText,
        PageContentRequest request, CancellationToken ct)
    {
        var scan = new ObjectScan();
        var context = new ScanContext
        {
            Frame = frame,
            Size = size,
            PageHasText = pageHasText,
            Request = request,
            TextCentres = TextCentres(text),
        };

        var identity = new FS_MATRIX { A = 1, B = 0, C = 0, D = 1, E = 0, F = 0 };
        ScanContainer(page, page, isForm: false, identity, 0, scan, context, ct);

        // Text is exported as text, so it is switched off for every rasterization: an image re-rendered
        // from the page would otherwise carry the words printed over it, and they would appear twice.
        SetActive(scan.TextObjects, false);
        try
        {
            ExtractImages(page, scan, context, ct);

            if (request.Drawings)
            {
                SetActive(scan.ImageObjects, false);
                try
                {
                    RenderDrawings(page, scan, context, ct);
                }
                finally
                {
                    SetActive(scan.ImageObjects, true);
                }
            }
        }
        finally
        {
            SetActive(scan.TextObjects, true);
        }

        scan.Images.Sort((a, b) =>
        {
            int byTop = a.Bounds.Top.CompareTo(b.Bounds.Top);
            return byTop != 0 ? byTop : a.Bounds.Left.CompareTo(b.Bounds.Left);
        });
        return scan;
    }

    private static List<PointD> TextCentres(PageText text)
    {
        var centres = new List<PointD>(Math.Min(text.Length, 20_000));
        for (int i = 0; i < text.Length && centres.Count < 20_000; i++)
        {
            if (char.IsWhiteSpace(text.Text[i])) continue;
            if (text.TryGetBox(i, out var box) && !box.IsEmpty) centres.Add(box.Center);
        }
        return centres;
    }

    /// <summary>
    /// Walks a page or a form XObject. A form's objects are drawn through the form's matrix, so their
    /// bounds are in the form's own space and have to be carried back into page space by it — which is why
    /// images inside a form used to be dropped rather than placed in the wrong spot.
    /// </summary>
    private void ScanContainer(
        nint page, nint container, bool isForm, FS_MATRIX matrix, int depth,
        ObjectScan scan, ScanContext context, CancellationToken ct)
    {
        int count = isForm ? FPDFFormObj_CountObjects(container) : FPDFPage_CountObjects(container);
        for (int i = 0; i < count; i++)
        {
            if ((i & 63) == 0) ct.ThrowIfCancellationRequested();

            nint obj = isForm ? FPDFFormObj_GetObject(container, (uint)i) : FPDFPage_GetObject(container, i);
            if (obj == 0) continue;
            int type = FPDFPageObj_GetType(obj);

            if (type == FPDF_PAGEOBJ_TEXT)
            {
                scan.TextObjects.Add(obj);
                continue;
            }

            if (type == FPDF_PAGEOBJ_FORM)
            {
                if (depth >= MaxFormDepth) continue;
                FS_MATRIX form;
                var combined = FPDFPageObj_GetMatrix(obj, &form) != 0 ? Multiply(form, matrix) : matrix;
                ScanContainer(page, obj, isForm: true, combined, depth + 1, scan, context, ct);
                continue;
            }

            if (!TryGetBounds(obj, matrix, context.Frame, out var bounds)) continue;

            switch (type)
            {
                case FPDF_PAGEOBJ_IMAGE:
                    scan.ImageObjects.Add(obj);
                    if (scan.ImageTasks.Count >= MaxImagesPerPage) break;

                    // The picture a searchable scan is made of: its words are already being exported as
                    // text, so writing the picture as well would put every word on the page twice.
                    if (context.PageHasText && bounds.Width * bounds.Height >= ScanBackgroundCoverage) break;
                    if (bounds.Width <= 0.002 || bounds.Height <= 0.002) break;

                    scan.ImageTasks.Add(new ImageTask(obj, bounds, depth > 0, FPDFPageObj_GetMarkedContentID(obj)));
                    break;

                case FPDF_PAGEOBJ_PATH:
                    ClassifyPath(obj, bounds, scan, context);
                    break;

                case FPDF_PAGEOBJ_SHADING:
                    if (context.Request.Drawings) scan.DrawingParts.Add(bounds);
                    break;
            }
        }
    }

    /// <summary>
    /// Sorts a path into the three things one can be: a rule of a table, a panel filled behind something,
    /// or a piece of artwork. Only the last is rasterized, because the first two are carried as data.
    /// </summary>
    private static void ClassifyPath(nint obj, RectD bounds, ObjectScan scan, ScanContext context)
    {
        if (IsRule(bounds, out bool horizontal))
        {
            scan.Rules.Add(new RuleSegment(bounds, horizontal));
            return;
        }

        bool filled = false;
        uint color = 0;
        int fillMode = 0, stroke = 0;
        if (FPDFPath_GetDrawMode(obj, &fillMode, &stroke) != 0 && fillMode != FPDF_FILLMODE_NONE)
        {
            uint r = 0, g = 0, b = 0, a = 255;
            if (FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a) != 0 && a >= 32)
            {
                filled = true;
                color = ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
            }
        }

        bool rectangle = filled && IsRectangle(obj);
        if (rectangle && scan.Fills.Count < 256) scan.Fills.Add(new FilledArea(bounds, color));

        if (!context.Request.Drawings || scan.DrawingParts.Count >= 4096) return;

        // A rectangle that is pale, page-sized, or has the text printed on top of it is the background
        // behind the words rather than a drawing of its own. Rasterizing one would paint over the text.
        if (rectangle &&
            (IsPale(color) ||
             bounds.Width * bounds.Height > BackgroundCoverage ||
             context.CountTextInside(bounds, 1) > 0))
            return;

        scan.DrawingParts.Add(bounds);
    }

    private static bool IsPale(uint color) =>
        ((color >> 16) & 0xFF) >= 0xF0 && ((color >> 8) & 0xFF) >= 0xF0 && (color & 0xFF) >= 0xF0;

    /// <summary>
    /// An axis-aligned rectangle, and nothing else: four corners joined by edges that each run along one
    /// axis. Counting straight segments is not enough — a triangle has three of them, and a triangle with
    /// a caption across it would be filed as a panel behind the text and never drawn.
    /// </summary>
    private static bool IsRectangle(nint obj)
    {
        int segments = FPDFPath_CountSegments(obj);
        if (segments is < 4 or > 5) return false;

        float* xs = stackalloc float[5];
        float* ys = stackalloc float[5];
        int count = 0;

        for (int i = 0; i < segments; i++)
        {
            nint segment = FPDFPath_GetPathSegment(obj, i);
            if (segment == 0) return false;

            int type = FPDFPathSegment_GetType(segment);
            if (type == FPDF_SEGMENT_BEZIERTO) return false;
            if (i > 0 && type == FPDF_SEGMENT_MOVETO) return false;   // a second subpath, so not one box

            float x, y;
            if (FPDFPathSegment_GetPoint(segment, &x, &y) == 0) return false;
            xs[count] = x;
            ys[count] = y;
            count++;
        }

        const float Tolerance = 0.05f;
        // A path closed by repeating its first point has five of them, which is still four corners.
        if (count == 5 && Math.Abs(xs[4] - xs[0]) < Tolerance && Math.Abs(ys[4] - ys[0]) < Tolerance) count = 4;
        if (count != 4) return false;

        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            bool horizontal = Math.Abs(ys[i] - ys[next]) < Tolerance;
            bool vertical = Math.Abs(xs[i] - xs[next]) < Tolerance;
            if (!horizontal && !vertical) return false;
        }
        return true;
    }

    private static void SetActive(List<nint> objects, bool active)
    {
        foreach (nint obj in objects) FPDFPageObj_SetIsActive(obj, active ? 1 : 0);
    }

    /// <summary>Composes a child matrix with its parent's: the child is applied first.</summary>
    private static FS_MATRIX Multiply(in FS_MATRIX child, in FS_MATRIX parent) => new()
    {
        A = child.A * parent.A + child.B * parent.C,
        B = child.A * parent.B + child.B * parent.D,
        C = child.C * parent.A + child.D * parent.C,
        D = child.C * parent.B + child.D * parent.D,
        E = child.E * parent.A + child.F * parent.C + parent.E,
        F = child.E * parent.B + child.F * parent.D + parent.F,
    };

    private static bool TryGetBounds(nint obj, in FS_MATRIX matrix, PageFrame frame, out RectD bounds)
    {
        float left, bottom, right, top;
        if (FPDFPageObj_GetBounds(obj, &left, &bottom, &right, &top) == 0)
        {
            bounds = RectD.Empty;
            return false;
        }

        // The four corners have to go through the matrix separately: a rotated or skewed form turns the
        // box into a quadrilateral, and only its extent is a rectangle again.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in (ReadOnlySpan<(float, float)>)[(left, bottom), (right, bottom), (right, top), (left, top)])
        {
            double px = matrix.A * x + matrix.C * y + matrix.E;
            double py = matrix.B * x + matrix.D * y + matrix.F;
            minX = Math.Min(minX, px);
            maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py);
            maxY = Math.Max(maxY, py);
        }

        // A rule drawn as a stroked line is a box of no width at all, and dropping it as empty would lose
        // every table that draws its grid with `m l S` rather than with filled rectangles.
        const double Hairline = 0.25;
        if (maxX - minX < Hairline) (minX, maxX) = (minX - Hairline / 2, maxX + Hairline / 2);
        if (maxY - minY < Hairline) (minY, maxY) = (minY - Hairline / 2, maxY + Hairline / 2);

        bounds = frame.ToNormalized(minX, maxY, maxX, minY).ClampToUnit();
        return !bounds.IsEmpty;
    }

    /// <summary>A rule is long in one direction and hairline in the other: a table edge or an underline.</summary>
    private static bool IsRule(RectD bounds, out bool horizontal)
    {
        const double Thin = 0.004;  // about 3 pt on a Letter page
        const double Long = 0.012;

        horizontal = bounds.Height <= Thin && bounds.Width >= Long;
        bool vertical = bounds.Width <= Thin && bounds.Height >= Long;
        return horizontal || vertical;
    }

    // ---- images ----

    private void ExtractImages(nint page, ObjectScan scan, ScanContext context, CancellationToken ct)
    {
        foreach (var task in scan.ImageTasks)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadImage(page, task, context.Size, ref scan.Budget) is { } image)
                scan.Images.Add(image with { MarkedContentId = task.MarkedContentId });
        }
    }

    private PlacedImage? ReadImage(nint page, ImageTask task, PageSize size, ref long budget)
    {
        nint obj = task.Object;
        var bounds = task.Bounds;

        // A JPEG in the PDF is a JPEG in the .docx: the bytes are copied across without being decoded,
        // so a photograph survives the conversion exactly and the file stays small.
        if (IsPlainJpeg(obj))
        {
            uint length = FPDFImageObj_GetImageDataDecoded(obj, null, 0);
            if (length > 0 && length <= budget)
            {
                var bytes = new byte[length];
                fixed (byte* p = bytes)
                {
                    if (FPDFImageObj_GetImageDataDecoded(obj, p, length) == length && LooksLikeJpeg(bytes))
                    {
                        budget -= length;
                        return PlacedImage.FromEncoded(bounds, bytes, "jpeg");
                    }
                }
            }
        }

        uint sourceWidth = 0, sourceHeight = 0;
        bool haveSize = FPDFImageObj_GetImagePixelSize(obj, &sourceWidth, &sourceHeight) != 0 && sourceWidth > 0;
        double placedWidth = bounds.Width * size.Width;

        // PDFium renders an image at the size it is *placed* at, in points. A 300 DPI scan dropped on a
        // Letter page therefore comes back at 612 pixels wide — 72 DPI — and the detail is gone. When the
        // stored image has more pixels than that, the page region is rendered at the image's own
        // resolution instead, which keeps the scan sharp and costs one extra render.
        //
        // An image inside a form XObject takes the same path whatever its resolution: PDFium would render
        // it through the form's matrix rather than the page's, and place it at the wrong size.
        bool wantsRegion = task.Nested ||
            (haveSize && placedWidth > 1 && sourceWidth > placedWidth * ResolutionGainToReRender);
        if (wantsRegion)
        {
            int target = haveSize
                ? (int)Math.Max(sourceWidth, 1)
                : (int)Math.Max(1, Math.Round(placedWidth * DrawingDpi / 72.0));
            if (RenderRegion(page, bounds, size, target, transparent: false, ref budget) is { } sharp)
                return PlacedImage.FromBits(bounds, sharp);
        }

        nint bitmap = FPDFImageObj_GetRenderedBitmap(_document, page, obj);
        if (bitmap == 0) return null;
        try
        {
            int width = FPDFBitmap_GetWidth(bitmap);
            int height = FPDFBitmap_GetHeight(bitmap);
            if (width <= 0 || height <= 0) return null;

            long bytes = (long)width * height * 4;
            if (bytes > budget) return null;
            budget -= bytes;

            var pixels = new byte[bytes];
            int stride = FPDFBitmap_GetStride(bitmap);
            int format = FPDFBitmap_GetFormat(bitmap);
            byte* source = (byte*)FPDFBitmap_GetBuffer(bitmap);
            if (source is null) return null;

            fixed (byte* destination = pixels)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* from = source + (long)y * stride;
                    byte* to = destination + (long)y * width * 4;
                    switch (format)
                    {
                        case FPDFBitmap_BGRA:
                            Buffer.MemoryCopy(from, to, width * 4, width * 4);
                            break;
                        case FPDFBitmap_BGR:
                            for (int x = 0; x < width; x++)
                            {
                                to[x * 4] = from[x * 3];
                                to[x * 4 + 1] = from[x * 3 + 1];
                                to[x * 4 + 2] = from[x * 3 + 2];
                                to[x * 4 + 3] = 255;
                            }
                            break;
                        case FPDFBitmap_Gray:
                            for (int x = 0; x < width; x++)
                            {
                                byte v = from[x];
                                to[x * 4] = to[x * 4 + 1] = to[x * 4 + 2] = v;
                                to[x * 4 + 3] = 255;
                            }
                            break;
                        default: // BGRx and anything new: copy the colour and force the alpha opaque
                            for (int x = 0; x < width; x++)
                            {
                                to[x * 4] = from[x * 4];
                                to[x * 4 + 1] = from[x * 4 + 1];
                                to[x * 4 + 2] = from[x * 4 + 2];
                                to[x * 4 + 3] = 255;
                            }
                            break;
                    }
                }
            }

            return PlacedImage.FromBits(bounds, new ImageBits(width, height, pixels));
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
        }
    }

    /// <summary>
    /// Renders the part of the page the object occupies, scaled so the result is
    /// <paramref name="targetWidth"/> pixels across. Uses the same clipped-render trick as the viewer:
    /// negative start offsets against a full-page size render only the region asked for. A transparent
    /// render keeps the page showing through the drawing, so artwork does not arrive on a white card.
    /// </summary>
    private ImageBits? RenderRegion(
        nint page, RectD bounds, PageSize size, int targetWidth, bool transparent, ref long budget)
    {
        double scale = targetWidth / (bounds.Width * size.Width);
        int pageWidth = (int)Math.Round(size.Width * scale);
        int pageHeight = (int)Math.Round(size.Height * scale);
        if (pageWidth <= 0 || pageHeight <= 0) return null;
        if ((long)pageWidth * pageHeight > MaxRegionPixels) return null;

        int x = (int)Math.Floor(bounds.Left * pageWidth);
        int y = (int)Math.Floor(bounds.Top * pageHeight);
        int w = Math.Min((int)Math.Ceiling(bounds.Width * pageWidth), pageWidth - x);
        int h = Math.Min((int)Math.Ceiling(bounds.Height * pageHeight), pageHeight - y);
        if (w <= 0 || h <= 0) return null;

        long bytes = (long)w * h * 4;
        if (bytes > budget || bytes > MaxRegionPixels * 4L) return null;

        nint bitmap = FPDFBitmap_Create(w, h, transparent ? 1 : 0);
        if (bitmap == 0) return null;
        try
        {
            FPDFBitmap_FillRect(bitmap, 0, 0, w, h, transparent ? 0x00000000u : 0xFFFFFFFFu);
            FPDF_RenderPageBitmap(bitmap, page, -x, -y, pageWidth, pageHeight, 0, 0);

            int stride = FPDFBitmap_GetStride(bitmap);
            byte* source = (byte*)FPDFBitmap_GetBuffer(bitmap);
            if (source is null) return null;

            var pixels = new byte[bytes];
            fixed (byte* destination = pixels)
            {
                for (int row = 0; row < h; row++)
                    Buffer.MemoryCopy(source + (long)row * stride, destination + (long)row * w * 4, w * 4, w * 4);
            }

            budget -= bytes;
            return new ImageBits(w, h, pixels);
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
        }
    }

    /// <summary>
    /// True when the image is a single DCTDecode stream, which is exactly a JPEG file. A soft mask or a
    /// second filter means the stored bytes are not what the page shows, so those are rendered instead.
    /// </summary>
    private static bool IsPlainJpeg(nint obj)
    {
        if (FPDFImageObj_GetImageFilterCount(obj) != 1) return false;

        const int Capacity = 32;
        byte* buffer = stackalloc byte[Capacity];
        uint written = FPDFImageObj_GetImageFilter(obj, 0, buffer, Capacity);
        if (written is <= 1 or > Capacity) return false;

        string filter = Encoding.ASCII.GetString(buffer, (int)written - 1).TrimEnd('\0');
        return filter is "DCTDecode";
    }

    private static bool LooksLikeJpeg(byte[] bytes) =>
        bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[^2] == 0xFF && bytes[^1] == 0xD9;

    // ---- vector artwork ----

    /// <summary>
    /// Rasterizes the page's vector artwork, one region at a time, with the text and the images switched
    /// off so only the drawing itself lands in the picture.
    ///
    /// Word's DrawingML could express the beziers, but translating PDF's graphics state — clips, soft
    /// masks, blend modes, shadings and patterns — is a project the size of the rest of the export, and a
    /// half-translated chart is worse than a faithful picture of one. Text that sits inside the region is
    /// still exported as text, so nothing becomes unselectable that was not already a drawing.
    /// </summary>
    private void RenderDrawings(nint page, ObjectScan scan, ScanContext context, CancellationToken ct)
    {
        if (scan.DrawingParts.Count == 0) return;

        foreach (var cluster in ClusterRects(scan.DrawingParts, 0.008))
        {
            ct.ThrowIfCancellationRequested();
            if (scan.Images.Count >= MaxImagesPerPage) break;
            if (scan.Images.Count(i => i.IsDrawing) >= MaxDrawingsPerPage) break;

            // The rules inside a drawing are its axes and its frame, not a table: take them with it.
            var bounds = cluster;
            var reach = cluster.Inflate(0.012, 0.012);
            foreach (var rule in scan.Rules)
                if (reach.Contains(rule.Bounds.Center)) bounds = bounds.Union(rule.Bounds);

            bounds = bounds.Inflate(0.002, 0.002).ClampToUnit();
            if (!IsWorthRasterizing(bounds, context)) continue;

            int width = (int)Math.Round(bounds.Width * context.Size.Width * DrawingDpi / 72.0);
            if (width <= 0) continue;

            if (RenderRegion(page, bounds, context.Size, width, transparent: true, ref scan.Budget) is { } bits)
                scan.Images.Add(PlacedImage.FromBits(bounds, bits) with { IsDrawing = true });
        }
    }

    private static bool IsWorthRasterizing(RectD bounds, ScanContext context)
    {
        double area = bounds.Width * bounds.Height;
        if (bounds.Width < 0.004 || bounds.Height < 0.004) return false;   // a mark, not a drawing
        if (area < 0.0008) return false;
        if (area > 0.92) return false;

        // A region covering half the page with the body text inside it is a frame around the page, and
        // rasterizing it would drop a picture of the page on top of the document it was read from.
        return area <= 0.45 || context.CountTextInside(bounds, 25) < 25;
    }

    /// <summary>Merges rectangles that touch or nearly touch into the regions they make up.</summary>
    private static List<RectD> ClusterRects(List<RectD> rects, double reach)
    {
        var clusters = new List<RectD>();
        foreach (var rect in rects)
        {
            var grown = rect.Inflate(reach, reach);
            int target = -1;
            for (int i = 0; i < clusters.Count; i++)
            {
                if (!clusters[i].Intersects(grown)) continue;
                if (target < 0)
                {
                    target = i;
                    clusters[i] = clusters[i].Union(rect);
                }
                else
                {
                    // The rectangle bridges two clusters that were growing separately: fold them together.
                    clusters[target] = clusters[target].Union(clusters[i]);
                    clusters.RemoveAt(i);
                    i--;
                }
            }
            if (target < 0) clusters.Add(rect);
        }

        clusters.Sort((a, b) =>
        {
            int byTop = a.Top.CompareTo(b.Top);
            return byTop != 0 ? byTop : a.Left.CompareTo(b.Left);
        });
        return clusters;
    }

    // ---- structure tree ----

    /// <summary>
    /// Reads the structure tree of a tagged PDF. Anything exported from Word, InDesign or an accessibility
    /// pipeline carries one, and it states what the geometry can only be made to confess: which lines are a
    /// heading, which are one list, and where a table's cells are.
    /// </summary>
    private static List<PageTag> ReadTags(nint page)
    {
        nint tree = FPDF_StructTree_GetForPage(page);
        if (tree == 0) return [];

        try
        {
            int budget = MaxStructElements;
            var roots = new List<PageTag>();
            int count = FPDF_StructTree_CountChildren(tree);
            for (int i = 0; i < count && budget > 0; i++)
            {
                nint element = FPDF_StructTree_GetChildAtIndex(tree, i);
                if (element == 0) continue;
                if (ReadElement(element, 0, ref budget) is { } tag) roots.Add(tag);
            }
            return roots;
        }
        finally
        {
            FPDF_StructTree_Close(tree);
        }
    }

    private static PageTag? ReadElement(nint element, int depth, ref int budget)
    {
        if (depth > MaxStructDepth || --budget <= 0) return null;

        string type = ReadWideString((buffer, length) => FPDF_StructElement_GetType(element, buffer, length));
        if (type.Length == 0) type = "P";

        var ids = new List<int>();
        int idCount = FPDF_StructElement_GetMarkedContentIdCount(element);
        for (int i = 0; i < idCount; i++)
        {
            int id = FPDF_StructElement_GetMarkedContentIdAtIndex(element, i);
            if (id >= 0 && !ids.Contains(id)) ids.Add(id);
        }

        var children = new List<PageTag>();
        int count = FPDF_StructElement_CountChildren(element);
        for (int i = 0; i < count && budget > 0; i++)
        {
            nint child = FPDF_StructElement_GetChildAtIndex(element, i);
            if (child == 0)
            {
                // A child that is not an element is a marked-content id drawn by this one.
                int id = FPDF_StructElement_GetChildMarkedContentID(element, i);
                if (id >= 0 && !ids.Contains(id)) ids.Add(id);
                continue;
            }
            if (ReadElement(child, depth + 1, ref budget) is { } tag) children.Add(tag);
        }

        string alt = ReadWideString((buffer, length) => FPDF_StructElement_GetAltText(element, buffer, length));
        string title = ReadWideString((buffer, length) => FPDF_StructElement_GetTitle(element, buffer, length));

        return new PageTag(type, children)
        {
            AltText = alt.Length > 0 ? alt : null,
            Title = title.Length > 0 ? title : null,
            MarkedContentIds = ids,
            ColumnSpan = ReadSpan(element, "ColSpan"),
            RowSpan = ReadSpan(element, "RowSpan"),
        };
    }

    /// <summary>A cell's /ColSpan or /RowSpan, which is the only tag attribute the export needs.</summary>
    private static int ReadSpan(nint element, string name)
    {
        int count = FPDF_StructElement_GetAttributeCount(element);
        for (int i = 0; i < count; i++)
        {
            nint attributes = FPDF_StructElement_GetAttributeAtIndex(element, i);
            if (attributes == 0) continue;

            nint value = FPDF_StructElement_Attr_GetValue(attributes, (byte*)Utf8Z(name));
            if (value == 0) continue;
            if (FPDF_StructElement_Attr_GetType(value) != FPDF_OBJECT_NUMBER) continue;

            float number = 0;
            if (FPDF_StructElement_Attr_GetNumberValue(value, &number) == 0) continue;
            return Math.Clamp((int)Math.Round(number), 1, 64);
        }
        return 1;
    }
}
