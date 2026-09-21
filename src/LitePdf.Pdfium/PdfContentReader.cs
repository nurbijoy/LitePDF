using System.Text;
using LitePdf.Core;
using LitePdf.Core.Export;
using LitePdf.Core.Text;
using LitePdf.Pdfium.Interop;
using static LitePdf.Pdfium.Interop.NativeMethods;

namespace LitePdf.Pdfium;

/// <summary>
/// Reads the drawing behind a page — styled characters, placed images and thin rules — for the DOCX export.
/// Runs on the PDFium worker like everything else; geometry leaves in normalized page coordinates.
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

    public Task<PageContent> GetPageContentAsync(int pageIndex, int priority, CancellationToken ct = default) =>
        _worker.RunAsync(() => WithPage(pageIndex, page => ReadContent(pageIndex, page, ct)), priority, ct);

    private PageContent ReadContent(int pageIndex, nint page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var frame = GetFrame(pageIndex, page);
        var size = _pageSizes[pageIndex];

        var (text, spans, fromInvisibleLayer) = ReadStyledText(pageIndex, page, frame);
        ct.ThrowIfCancellationRequested();

        bool hasText = fromInvisibleLayer || text.VisibleCharCount >= MinVisibleCharsForRealText;
        var (images, rules) = ReadObjects(page, frame, size, hasText, ct);
        return new PageContent(pageIndex, size, text, spans, images, rules);
    }

    // ---- text ----

    private readonly record struct CharRecord(uint Code, RectD Box, TextStyle Style, bool Invisible);

    private (PageText Text, List<StyledSpan> Spans, bool FromInvisibleLayer) ReadStyledText(
        int pageIndex, nint page, PageFrame frame)
    {
        nint textPage = FPDFText_LoadPage(page);
        if (textPage == 0) return (PageText.Empty(pageIndex), [], false);

        try
        {
            int count = FPDFText_CountChars(textPage);
            if (count <= 0) return (PageText.Empty(pageIndex), [], false);

            var records = new List<CharRecord>(count);
            int visible = 0, invisible = 0;

            // Style is a property of the text object, not of the character, so it is read once per object.
            // A dense page has thousands of characters and a handful of objects; reading per character
            // would multiply the native calls by an order of magnitude for the same answer.
            nint lastObject = -1;
            var style = TextStyle.Default;
            bool lastInvisible = false;

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

                records.Add(new CharRecord(code, box, style, lastInvisible));
            }

            // A searchable scan carries its whole text in an invisible layer under the picture. Keeping both
            // would write every word twice; keeping neither would lose the page. So the layer is used only
            // when there is no real text to use instead.
            bool useInvisible = visible < MinVisibleCharsForRealText && invisible > 0;

            var sb = new StringBuilder(records.Count);
            var boxes = new List<float>(records.Count * 4);
            var spans = new List<StyledSpan>();

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
            }

            var text = PageText.Create(pageIndex, TextSource.Pdf, sb.ToString(), boxes.ToArray());
            return (text, spans, useInvisible);
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

    // ---- images and rules ----

    private (List<PlacedImage> Images, List<RuleSegment> Rules) ReadObjects(
        nint page, PageFrame frame, PageSize size, bool pageHasText, CancellationToken ct)
    {
        var images = new List<PlacedImage>();
        var rules = new List<RuleSegment>();
        long budget = MaxImageBytes;

        int count = FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            if ((i & 63) == 0) ct.ThrowIfCancellationRequested();

            nint obj = FPDFPage_GetObject(page, i);
            if (obj == 0) continue;
            int type = FPDFPageObj_GetType(obj);

            if (type == FPDF_PAGEOBJ_IMAGE)
            {
                if (images.Count >= MaxImagesPerPage) continue;
                if (!TryGetBounds(obj, frame, out var bounds)) continue;

                // The picture a searchable scan is made of: its words are already being exported as text,
                // so writing the picture as well would put every word on the page twice.
                if (pageHasText && bounds.Width * bounds.Height >= ScanBackgroundCoverage) continue;
                if (bounds.Width <= 0.002 || bounds.Height <= 0.002) continue;

                if (ReadImage(page, obj, bounds, size, ref budget) is { } image) images.Add(image);
            }
            else if (type == FPDF_PAGEOBJ_PATH)
            {
                if (!TryGetBounds(obj, frame, out var bounds)) continue;
                if (IsRule(bounds, out bool horizontal)) rules.Add(new RuleSegment(bounds, horizontal));
            }
        }

        return (images, rules);
    }

    private static bool TryGetBounds(nint obj, PageFrame frame, out RectD bounds)
    {
        float left, bottom, right, top;
        if (FPDFPageObj_GetBounds(obj, &left, &bottom, &right, &top) == 0)
        {
            bounds = RectD.Empty;
            return false;
        }
        bounds = frame.ToNormalized(left, top, right, bottom).ClampToUnit();
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

    private PlacedImage? ReadImage(nint page, nint obj, RectD bounds, PageSize size, ref long budget)
    {
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

        // PDFium renders an image at the size it is *placed* at, in points. A 300 DPI scan dropped on a
        // Letter page therefore comes back at 612 pixels wide — 72 DPI — and the detail is gone. When the
        // stored image has more pixels than that, the page region is rendered at the image's own
        // resolution instead, which keeps the scan sharp and costs one extra render.
        uint sourceWidth = 0, sourceHeight = 0;
        if (FPDFImageObj_GetImagePixelSize(obj, &sourceWidth, &sourceHeight) != 0 && sourceWidth > 0)
        {
            double placedWidth = bounds.Width * size.Width;
            if (placedWidth > 1 && sourceWidth > placedWidth * ResolutionGainToReRender)
            {
                if (RenderRegion(page, bounds, size, (int)sourceWidth, ref budget) is { } sharp)
                    return PlacedImage.FromBits(bounds, sharp);
            }
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
    /// Renders the part of the page the image occupies, scaled so the result is
    /// <paramref name="targetWidth"/> pixels across. Uses the same clipped-render trick as the viewer:
    /// negative start offsets against a full-page size render only the region asked for.
    /// </summary>
    private ImageBits? RenderRegion(nint page, RectD bounds, PageSize size, int targetWidth, ref long budget)
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

        nint bitmap = FPDFBitmap_Create(w, h, 0);
        if (bitmap == 0) return null;
        try
        {
            FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
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
}
