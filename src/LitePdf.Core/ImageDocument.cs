using LitePdf.Core;

namespace LitePdf.Core;

/// <summary>
/// Simple document representing an image file (PNG/JPG/BMP/TIFF) as a single page.
/// Rendering is done via WPF BitmapDecoder, not PDFium.
/// This implements IPdfDocument for uniform handling.
/// </summary>
public sealed class ImageDocument : IPdfDocument
{
    public string FilePath { get; }
    public int PageCount => 1;
    public IReadOnlyList<PageSize> PageSizes { get; }

    private readonly RenderedBitmap _bitmap;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;

    public ImageDocument(string filePath, RenderedBitmap bitmap, PageSize size)
    {
        FilePath = filePath;
        _bitmap = bitmap;
        _pixelWidth = bitmap.Width;
        _pixelHeight = bitmap.Height;
        PageSizes = new List<PageSize> { size };
    }

    public Task<RenderedBitmap> RenderPageAsync(int pageIndex, int pixelWidth, int pixelHeight, RenderFlags flags, int priority, CancellationToken ct = default)
    {
        if (pageIndex != 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        // Simple scaling: if requested size matches stored, return stored; otherwise scale nearest neighbor
        if (pixelWidth == _pixelWidth && pixelHeight == _pixelHeight)
            return Task.FromResult(_bitmap);

        // Scale using simple bilinear (approx) - for thumbnails
        byte[] scaled = ScaleBgra(_bitmap.Pixels, _bitmap.Stride, _pixelWidth, _pixelHeight, pixelWidth, pixelHeight);
        var result = new RenderedBitmap(pixelWidth, pixelHeight, pixelWidth * 4, scaled);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<OutlineItem>> GetOutlineAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OutlineItem>>(Array.Empty<OutlineItem>());

    public Task<string> GetPageTextAsync(int pageIndex, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public Task<int> GetCharCountAsync(int pageIndex, CancellationToken ct = default)
        => Task.FromResult(0);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static byte[] ScaleBgra(byte[] src, int srcStride, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        double xRatio = (double)srcW / dstW;
        double yRatio = (double)srcH / dstH;
        for (int y = 0; y < dstH; y++)
        {
            int srcY = Math.Min(srcH - 1, (int)(y * yRatio));
            for (int x = 0; x < dstW; x++)
            {
                int srcX = Math.Min(srcW - 1, (int)(x * xRatio));
                int srcIdx = srcY * srcStride + srcX * 4;
                int dstIdx = (y * dstW + x) * 4;
                if (srcIdx + 3 < src.Length)
                {
                    dst[dstIdx] = src[srcIdx];
                    dst[dstIdx + 1] = src[srcIdx + 1];
                    dst[dstIdx + 2] = src[srcIdx + 2];
                    dst[dstIdx + 3] = src[srcIdx + 3];
                }
            }
        }
        return dst;
    }
}
