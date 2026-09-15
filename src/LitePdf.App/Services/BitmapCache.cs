using System.Windows.Media.Imaging;
using LitePdf.Core;

namespace LitePdf.App.Services;

/// <summary>
/// LRU cache with byte budget for rendered bitmaps (both WriteableBitmap and raw pixels).
/// </summary>
public sealed class BitmapCache
{
    private sealed class Entry
    {
        public int PageIndex;
        public int PixelWidth;
        public WriteableBitmap Bitmap = null!;
        public long SizeBytes;
        public LinkedListNode<int> LruNode = null!;
    }

    private readonly Dictionary<int, Entry> _map = new(); // pageIndex -> entry (we keep only one per page at highest res for simplicity, but can extend)
    private readonly LinkedList<int> _lru = new();
    private long _totalBytes;
    private readonly long _budgetBytes;

    public BitmapCache(long budgetBytes = 200L * 1024 * 1024)
    {
        _budgetBytes = budgetBytes;
    }

    public bool TryGet(int pageIndex, int targetPixelWidth, out WriteableBitmap? bitmap)
    {
        if (_map.TryGetValue(pageIndex, out var entry))
        {
            // Check stale: >2% off
            if (!ViewMath.IsStale(entry.PixelWidth, targetPixelWidth))
            {
                // Move to front (most recent)
                _lru.Remove(entry.LruNode);
                _lru.AddFirst(entry.LruNode);
                bitmap = entry.Bitmap;
                return true;
            }
        }
        bitmap = null;
        return false;
    }

    public void Add(int pageIndex, int pixelWidth, WriteableBitmap bitmap)
    {
        long size = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
        // If exists, replace
        if (_map.TryGetValue(pageIndex, out var existing))
        {
            _totalBytes -= existing.SizeBytes;
            _lru.Remove(existing.LruNode);
            _map.Remove(pageIndex);
        }

        var node = _lru.AddFirst(pageIndex);
        var entry = new Entry
        {
            PageIndex = pageIndex,
            PixelWidth = pixelWidth,
            Bitmap = bitmap,
            SizeBytes = size,
            LruNode = node
        };
        _map[pageIndex] = entry;
        _totalBytes += size;

        // Evict
        while (_totalBytes > _budgetBytes && _lru.Count > 1)
        {
            int last = _lru.Last!.Value;
            if (_map.TryGetValue(last, out var e))
            {
                _totalBytes -= e.SizeBytes;
                _map.Remove(last);
            }
            _lru.RemoveLast();
        }
    }

    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
        _totalBytes = 0;
    }
}
