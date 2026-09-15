using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitePdf.App.Documents;
using LitePdf.App.Infrastructure;
using LitePdf.Core;
using LitePdf.Core.Layout;
using LitePdf.Core.Text;

namespace LitePdf.App.Viewer;

/// <summary>
/// Draws one page: rendered bitmap (plus a sharp detail tile at high zoom) and overlays for search hits,
/// text selection, the selected annotation and the region tool. All state is pulled from the owning viewer.
/// </summary>
internal sealed class PageVisual : FrameworkElement
{
    /// <summary>Base page bitmaps are capped at this many pixels; beyond it a viewport-sized detail tile is rendered.</summary>
    public const long MaxBasePixels = 12_000_000;

    private static readonly Brush ShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)));
    private static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x2E, 0x86, 0xFF)));
    private static readonly Brush SearchBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xC4, 0x00)));
    private static readonly Brush CurrentSearchBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x7A, 0x00)));
    private static readonly Pen CurrentSearchPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x00)), 1.5));
    private static readonly Pen AnnotationPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Brush RegionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0x0F, 0x6C, 0xBD)));
    private static readonly Pen RegionPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)), 1.5));
    private static readonly Pen PageBorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1));

    private readonly PdfViewer _viewer;
    private BitmapSource? _bitmap;
    private int _bitmapWidth;
    private int _bitmapGeneration = -1;
    private PageColorMode _bitmapMode;
    private int _bitmapRotation;
    private DetailTile? _detail;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _dataCts;
    private int _pendingWidth;
    private string? _error;

    public PageVisual(PdfViewer viewer)
    {
        _viewer = viewer;
        IsHitTestVisible = false; // the viewer hit-tests with layout math
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public int PageIndex { get; private set; } = -1;

    public PageText? Text { get; private set; }

    public void Attach(int pageIndex)
    {
        PageIndex = pageIndex;
        _error = null;
        _bitmap = null;
        _bitmapWidth = 0;
        _detail = null;
        Text = _viewer.Session?.TryGetCachedText(pageIndex);
        EnsureBitmap();
        EnsureData();
        InvalidateVisual();
    }

    public void Detach()
    {
        Cancel(ref _renderCts);
        Cancel(ref _detailCts);
        Cancel(ref _dataCts);
        PageIndex = -1;
        _bitmap = null;
        _detail = null;
        Text = null;
        _pendingWidth = 0;
    }

    /// <summary>Drops the current bitmap (keeping it on screen until the new one arrives) and re-renders.</summary>
    public void Refresh(bool reloadText)
    {
        if (PageIndex < 0) return;
        _detail = null;
        _bitmapGeneration = -1;
        if (reloadText)
        {
            Text = _viewer.Session?.TryGetCachedText(PageIndex);
            EnsureData();
        }
        EnsureBitmap();
        InvalidateVisual();
    }

    public void ClearForModeChange()
    {
        _bitmap = null;
        _detail = null;
        _bitmapWidth = 0;
        Cancel(ref _renderCts);
        _pendingWidth = 0;
        EnsureBitmap();
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_detail is { } d && Math.Abs(d.PageWidthDip - RenderSize.Width) > 0.5) _detail = null;
    }

    // ---- Rendering ----

    public async void EnsureBitmap()
    {
        var session = _viewer.Session;
        int page = PageIndex;
        if (session is null || page < 0 || _viewer.Layout is not { } layout) return;

        var rect = layout.GetPageRect(page);
        double dpi = _viewer.DpiScale;
        int fullW = ViewMath.ToPixels(rect.Width, dpi), fullH = ViewMath.ToPixels(rect.Height, dpi);
        var (w, h) = ViewMath.ClampToArea(fullW, fullH, MaxBasePixels);
        int generation = session.GetGeneration(page);
        var mode = _viewer.ColorMode;
        int rotation = _viewer.Rotation;

        bool current = _bitmap is not null && _bitmapGeneration == generation && _bitmapMode == mode && _bitmapRotation == rotation;
        if (current && !ViewMath.IsStale(_bitmapWidth, w)) return;

        if (_viewer.Cache.TryGetPage(page, generation, mode, rotation, out var cached) && !ViewMath.IsStale(cached.PixelWidth, w))
        {
            SetBitmap(cached);
            return;
        }
        if (_pendingWidth == w && _renderCts is not null) return;

        Cancel(ref _renderCts);
        var cts = _renderCts = new CancellationTokenSource();
        _pendingWidth = w;
        var document = session.Document;
        try
        {
            var bitmap = await PageRenderer.RenderAsync(document, page, w, h, rotation, null, mode, RenderPriority.Visible, cts.Token);
            if (cts.IsCancellationRequested || PageIndex != page || !ReferenceEquals(document, _viewer.Session?.Document)) return;
            var entry = new CachedRender(bitmap, w, generation, mode, rotation);
            _viewer.Cache.PutPage(page, entry);
            SetBitmap(entry);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (PageIndex != page || !ReferenceEquals(document, _viewer.Session?.Document)) return;
            Log.Error(ex, $"Rendering page {page + 1}");
            _error = ex.Message;
            InvalidateVisual();
        }
        finally
        {
            if (ReferenceEquals(_renderCts, cts))
            {
                _renderCts = null;
                _pendingWidth = 0;
            }
            cts.Dispose();
        }
    }

    private void SetBitmap(CachedRender entry)
    {
        _bitmap = entry.Bitmap;
        _bitmapWidth = entry.PixelWidth;
        _bitmapGeneration = entry.Generation;
        _bitmapMode = entry.Mode;
        _bitmapRotation = entry.Rotation;
        _error = null;
        InvalidateVisual();
    }

    /// <summary>At high zoom, renders the visible part of the page at full resolution (called once scrolling settles).</summary>
    public async void EnsureDetail(Rect visibleInPage)
    {
        var session = _viewer.Session;
        int page = PageIndex;
        if (session is null || page < 0 || _viewer.Layout is not { } layout) return;

        var rect = layout.GetPageRect(page);
        double dpi = _viewer.DpiScale;
        int fullW = ViewMath.ToPixels(rect.Width, dpi), fullH = ViewMath.ToPixels(rect.Height, dpi);
        if ((long)fullW * fullH <= MaxBasePixels)
        {
            _detail = null;
            return;
        }

        visibleInPage.Intersect(new Rect(0, 0, rect.Width, rect.Height));
        if (visibleInPage.IsEmpty) return;
        int generation = session.GetGeneration(page);
        var mode = _viewer.ColorMode;
        int rotation = _viewer.Rotation;
        if (_detail is { } d && d.Generation == generation && d.Mode == mode && d.Rotation == rotation &&
            Math.Abs(d.PageWidthDip - rect.Width) < 0.5 && d.Rect.Contains(visibleInPage))
            return;

        // Render a bit more than visible so small scrolls don't need a new tile.
        var wanted = Rect.Inflate(visibleInPage, visibleInPage.Width * 0.25, visibleInPage.Height * 0.25);
        wanted.Intersect(new Rect(0, 0, rect.Width, rect.Height));
        var clip = new PixelRect((int)Math.Floor(wanted.X * dpi), (int)Math.Floor(wanted.Y * dpi),
            (int)Math.Ceiling(wanted.Width * dpi), (int)Math.Ceiling(wanted.Height * dpi));

        Cancel(ref _detailCts);
        var cts = _detailCts = new CancellationTokenSource();
        var document = session.Document;
        try
        {
            var bitmap = await PageRenderer.RenderAsync(document, page, fullW, fullH, rotation, clip, mode, RenderPriority.Visible, cts.Token);
            if (cts.IsCancellationRequested || PageIndex != page) return;
            var tileRect = new Rect(clip.X / dpi, clip.Y / dpi, bitmap.PixelWidth / dpi, bitmap.PixelHeight / dpi);
            _detail = new DetailTile(bitmap, tileRect, rect.Width, generation, mode, rotation);
            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Rendering detail for page {page + 1}");
        }
        finally
        {
            if (ReferenceEquals(_detailCts, cts)) _detailCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Loads text, links and annotations used for interaction and overlays.</summary>
    private async void EnsureData()
    {
        var session = _viewer.Session;
        int page = PageIndex;
        if (session is null || page < 0) return;

        Cancel(ref _dataCts);
        var cts = _dataCts = new CancellationTokenSource();
        try
        {
            if (Text is null)
            {
                var text = await session.GetTextAsync(page, RenderPriority.Interactive, cts.Token);
                if (cts.IsCancellationRequested || PageIndex != page) return;
                Text = text;
                InvalidateVisual();
                _viewer.OnPageTextLoaded(page, text);
            }
            if (session.TryGetCachedLinks(page) is null) await session.GetLinksAsync(page, cts.Token);
            if (session.CanAnnotate && session.TryGetCachedAnnotations(page) is null)
                await session.GetAnnotationsAsync(page, RenderPriority.Interactive, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Loading data for page {page + 1}");
        }
        finally
        {
            if (ReferenceEquals(_dataCts, cts)) _dataCts = null;
            cts.Dispose();
        }
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        if (PageIndex < 0) return;
        var size = RenderSize;
        var bounds = new Rect(size);

        dc.DrawRectangle(ShadowBrush, null, new Rect(1, 2, size.Width, size.Height));
        dc.DrawRectangle(PaperBrush(_viewer.ColorMode), PageBorderPen, bounds);
        if (_bitmap is not null) dc.DrawImage(_bitmap, bounds);
        if (_detail is { } detail) dc.DrawImage(detail.Bitmap, detail.Rect);

        if (_error is not null && _bitmap is null)
        {
            var text = new FormattedText($"This page couldn't be displayed.\n{_error}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 13, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(40, size.Width - 40), TextAlignment = TextAlignment.Center };
            dc.DrawText(text, new Point(20, size.Height / 2 - text.Height / 2));
        }

        if (Text is { Length: > 0 } pageText)
        {
            var hits = _viewer.GetSearchHits(PageIndex);
            if (hits is not null)
            {
                var currentHit = _viewer.CurrentSearchHit;
                foreach (var hit in hits)
                {
                    bool isCurrent = currentHit is { } c && c.PageIndex == PageIndex && c.Match == hit;
                    foreach (var r in pageText.GetRangeRects(hit.Start, hit.Start + hit.Length))
                        dc.DrawRectangle(isCurrent ? CurrentSearchBrush : SearchBrush, isCurrent ? CurrentSearchPen : null, ToDip(r.Inflate(0.0015, 0.001)));
                }
            }

            if (_viewer.Selection is { } selection && selection.GetPageSpan(PageIndex, pageText.Length) is { } span)
                foreach (var r in pageText.GetRangeRects(span.Start, span.End))
                    dc.DrawRectangle(SelectionBrush, null, ToDip(r));
        }

        if (_viewer.SelectedAnnotation is { } annotation && annotation.PageIndex == PageIndex)
        {
            var area = annotation.Quads.Count > 0 ? annotation.Quads.Aggregate(RectD.Empty, (a, b) => a.Union(b)) : annotation.Bounds;
            var dip = ToDip(area);
            dip.Inflate(3, 3);
            dc.DrawRoundedRectangle(null, AnnotationPen, dip, 3, 3);
        }

        if (_viewer.RegionDraft is { } region && region.PageIndex == PageIndex)
            dc.DrawRectangle(RegionBrush, RegionPen, ToDip(region.Rect));
    }

    public Rect ToDip(RectD normalized)
    {
        var v = ViewTransform.ToView(normalized, _viewer.Rotation);
        return new Rect(v.Left * RenderSize.Width, v.Top * RenderSize.Height, Math.Max(0, v.Width * RenderSize.Width), Math.Max(0, v.Height * RenderSize.Height));
    }

    private static Brush PaperBrush(PageColorMode mode) => mode switch
    {
        PageColorMode.Dark => DarkPaper,
        PageColorMode.Sepia => SepiaPaper,
        _ => Brushes.White,
    };

    private static readonly Brush DarkPaper = Freeze(new SolidColorBrush(Color.FromRgb(30, 30, 30)));
    private static readonly Brush SepiaPaper = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xEC, 0xD8)));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static void Cancel(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts = null;
    }

    private sealed record DetailTile(BitmapSource Bitmap, Rect Rect, double PageWidthDip, int Generation, PageColorMode Mode, int Rotation);
}
