using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitePdf.Core;
using LitePdf.Core.Imaging;

namespace LitePdf.App.Documents;

public sealed record CachedRender(BitmapSource Bitmap, int PixelWidth, int Generation, PageColorMode Mode, int Rotation)
{
    public long Bytes => (long)Bitmap.PixelWidth * Bitmap.PixelHeight * 4;
}

/// <summary>LRU bitmap caches for pages and thumbnails with separate byte budgets. UI thread only.</summary>
public sealed class RenderCache
{
    private readonly Budget _pages = new(320L * 1024 * 1024);
    private readonly Budget _thumbnails = new(64L * 1024 * 1024);

    public bool TryGetPage(int page, int generation, PageColorMode mode, int rotation, out CachedRender bitmap) =>
        _pages.TryGet(page, generation, mode, rotation, out bitmap);

    public void PutPage(int page, CachedRender bitmap) => _pages.Put(page, bitmap);

    public bool TryGetThumbnail(int page, int generation, PageColorMode mode, int rotation, out CachedRender bitmap) =>
        _thumbnails.TryGet(page, generation, mode, rotation, out bitmap);

    public void PutThumbnail(int page, CachedRender bitmap) => _thumbnails.Put(page, bitmap);

    public void Clear()
    {
        _pages.Clear();
        _thumbnails.Clear();
    }

    private sealed class Budget(long maxBytes)
    {
        private readonly Dictionary<int, LinkedListNode<(int Page, CachedRender Bitmap)>> _map = new();
        private readonly LinkedList<(int Page, CachedRender Bitmap)> _lru = new();
        private long _bytes;

        public bool TryGet(int page, int generation, PageColorMode mode, int rotation, out CachedRender bitmap)
        {
            if (_map.TryGetValue(page, out var node) && node.Value.Bitmap.Generation == generation &&
                node.Value.Bitmap.Mode == mode && node.Value.Bitmap.Rotation == rotation)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
            bitmap = null!;
            return false;
        }

        public void Put(int page, CachedRender bitmap)
        {
            if (_map.Remove(page, out var existing))
            {
                _lru.Remove(existing);
                _bytes -= existing.Value.Bitmap.Bytes;
            }
            _map[page] = _lru.AddFirst((page, bitmap));
            _bytes += bitmap.Bytes;
            while (_bytes > maxBytes && _lru.Count > 1)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Page);
                _bytes -= last.Value.Bitmap.Bytes;
            }
        }

        public void Clear()
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }
}

public static class PageRenderer
{
    /// <summary>Renders off the UI thread and returns a frozen bitmap ready to draw.</summary>
    public static async Task<BitmapSource> RenderAsync(IPdfDocument document, int page, int width, int height, int rotation,
        PixelRect? clip, PageColorMode mode, int priority, CancellationToken ct)
    {
        var rendered = await document.RenderAsync(page, width, height, rotation, clip, RenderFlags.Annotations, priority, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return ToBitmapSource(rendered, mode);
    }

    public static BitmapSource ToBitmapSource(RenderedBitmap rendered, PageColorMode mode = PageColorMode.Normal)
    {
        BitmapOps.ApplyColorMode(rendered, mode);
        var bitmap = BitmapSource.Create(rendered.Width, rendered.Height, 96, 96, PixelFormats.Bgr32, null, rendered.Pixels, rendered.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
