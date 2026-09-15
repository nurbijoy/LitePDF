using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitePdf.Core;
using LitePdf.Core.Text;

namespace LitePdf.App.Documents;

/// <summary>
/// Raster images (PNG, JPEG, BMP, GIF, TIFF — one page per frame) exposed as a read-only document,
/// so viewing and OCR work exactly like for PDFs.
/// </summary>
public sealed class ImageDocument : IPdfDocument
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".jfif", ".webp"];

    private readonly BitmapSource[] _frames;

    private ImageDocument(string path, BitmapSource[] frames)
    {
        FilePath = path;
        _frames = frames;
        PageSizes = frames.Select(f => new PageSize(f.PixelWidth * 72.0 / NormalizeDpi(f.DpiX), f.PixelHeight * 72.0 / NormalizeDpi(f.DpiY))).ToArray();
    }

    public string FilePath { get; }

    public DocumentKind Kind => DocumentKind.Image;

    public int PageCount => _frames.Length;

    public IReadOnlyList<PageSize> PageSizes { get; }

    public bool SupportsAnnotations => false;

    public static bool IsImagePath(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static Task<ImageDocument> OpenAsync(string path) => Task.Run(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frames = decoder.Frames.Select(Normalize).ToArray();
        if (frames.Length == 0) throw new InvalidDataException("The image contains no frames.");
        return new ImageDocument(path, frames);
    });

    public static ImageDocument FromBitmap(BitmapSource bitmap, string displayPath) => new(displayPath, [Normalize(bitmap)]);

    private static BitmapSource Normalize(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        // Flatten transparency onto white so transparent screenshots stay readable (and OCR-able).
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a == 255) continue;
            pixels[i] = (byte)(pixels[i] * a / 255 + 255 - a);
            pixels[i + 1] = (byte)(pixels[i + 1] * a / 255 + 255 - a);
            pixels[i + 2] = (byte)(pixels[i + 2] * a / 255 + 255 - a);
            pixels[i + 3] = 255;
        }
        var result = BitmapSource.Create(w, h, NormalizeDpi(source.DpiX), NormalizeDpi(source.DpiY), PixelFormats.Bgra32, null, pixels, w * 4);
        result.Freeze();
        return result;
    }

    private static double NormalizeDpi(double dpi) => dpi is >= 24 and <= 2400 ? dpi : 96;

    public Task<RenderedBitmap> RenderAsync(int pageIndex, int pageWidth, int pageHeight, int rotation, PixelRect? clip,
        RenderFlags flags, int priority, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var frame = _frames[pageIndex];
        bool swap = (rotation & 1) == 1;
        double sx = (double)(swap ? pageHeight : pageWidth) / frame.PixelWidth;
        double sy = (double)(swap ? pageWidth : pageHeight) / frame.PixelHeight;

        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(sx, sy));
        if ((rotation & 3) != 0) transform.Children.Add(new RotateTransform(90 * (rotation & 3)));
        BitmapSource scaled = new TransformedBitmap(frame, transform);

        var region = clip ?? new PixelRect(0, 0, scaled.PixelWidth, scaled.PixelHeight);
        int x = Math.Clamp(region.X, 0, scaled.PixelWidth - 1), y = Math.Clamp(region.Y, 0, scaled.PixelHeight - 1);
        int w = Math.Clamp(region.Width, 1, scaled.PixelWidth - x), h = Math.Clamp(region.Height, 1, scaled.PixelHeight - y);
        var pixels = new byte[w * h * 4];
        scaled.CopyPixels(new System.Windows.Int32Rect(x, y, w, h), pixels, w * 4, 0);
        return new RenderedBitmap(w, h, pixels);
    }, ct);

    public Task<IReadOnlyList<OutlineItem>> GetOutlineAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutlineItem>>([]);

    public Task<PageText> GetTextAsync(int pageIndex, int priority, CancellationToken ct = default) =>
        Task.FromResult(PageText.Empty(pageIndex));

    public Task<bool> HasImagesAsync(int pageIndex, CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<PdfLink>> GetLinksAsync(int pageIndex, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PdfLink>>([]);

    public Task<IReadOnlyList<PdfAnnotation>> GetAnnotationsAsync(int pageIndex, int priority, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PdfAnnotation>>([]);

    public Task AddMarkupAsync(int pageIndex, AnnotationKind kind, IReadOnlyList<RectD> lineRects, AnnotationColor color, CancellationToken ct = default) =>
        throw new NotSupportedException("Images cannot be annotated.");

    public Task AddNoteAsync(int pageIndex, PointD position, string contents, AnnotationColor color, CancellationToken ct = default) =>
        throw new NotSupportedException("Images cannot be annotated.");

    public Task SetAnnotationColorAsync(int pageIndex, int annotationIndex, AnnotationColor color, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task SetAnnotationContentsAsync(int pageIndex, int annotationIndex, string contents, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RemoveAnnotationAsync(int pageIndex, int annotationIndex, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task SaveCopyAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException("Images cannot be saved as PDF.");

    public Task<DocumentInfo> GetInfoAsync(CancellationToken ct = default)
    {
        long size = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
        return Task.FromResult(new DocumentInfo(FilePath, size, PageCount, null, false, null, null, null, null, null, null, null, null));
    }

    public Task<string?> GetFileIdentifierAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
