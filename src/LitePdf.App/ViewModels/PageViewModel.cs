using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitePdf.App.Services;
using LitePdf.Core;
using LitePdf.Core.Text;

namespace LitePdf.App.ViewModels;

public sealed class PageViewModel : ObservableObject
{
    public int Index { get; }
    public PageSize PageSize { get; }

    private double _zoom = 1.0;
    private double _dpiScale = 1.0;
    private WriteableBitmap? _bitmap;
    private bool _isRendering;
    private CancellationTokenSource? _cts;

    // Selection
    private PageTextLayer? _textLayer;
    public PageTextLayer? TextLayer
    {
        get => _textLayer;
        set
        {
            if (SetProperty(ref _textLayer, value))
            {
                OnPropertyChanged(nameof(HasText));
            }
        }
    }

    public bool HasText => TextLayer != null && TextLayer.Glyphs.Count > 0;

    private TextSelection? _selection;
    public TextSelection? Selection
    {
        get => _selection;
        set => SetProperty(ref _selection, value);
    }

    private IReadOnlyList<RectD> _selectionRects = Array.Empty<RectD>();
    public IReadOnlyList<RectD> SelectionRects
    {
        get => _selectionRects;
        set => SetProperty(ref _selectionRects, value);
    }

    // Links
    private IReadOnlyList<Pdfium.PdfLink> _links = Array.Empty<Pdfium.PdfLink>();
    public IReadOnlyList<Pdfium.PdfLink> Links
    {
        get => _links;
        set => SetProperty(ref _links, value);
    }

    // Search highlight
    private IReadOnlyList<RectD> _searchRects = Array.Empty<RectD>();
    public IReadOnlyList<RectD> SearchRects
    {
        get => _searchRects;
        set => SetProperty(ref _searchRects, value);
    }

    public PageViewModel(int index, PageSize pageSize)
    {
        Index = index;
        PageSize = pageSize;
    }

    public double DipWidth => ViewMath.ToDip(PageSize.Width, _zoom);
    public double DipHeight => ViewMath.ToDip(PageSize.Height, _zoom);

    public WriteableBitmap? Bitmap
    {
        get => _bitmap;
        private set => SetProperty(ref _bitmap, value);
    }

    public bool IsRendering
    {
        get => _isRendering;
        private set => SetProperty(ref _isRendering, value);
    }

    public void UpdateZoom(double zoom, double dpiScale)
    {
        bool sizeChanged = Math.Abs(_zoom - zoom) > 0.001 || Math.Abs(_dpiScale - dpiScale) > 0.001;
        _zoom = zoom;
        _dpiScale = dpiScale;
        if (sizeChanged)
        {
            OnPropertyChanged(nameof(DipWidth));
            OnPropertyChanged(nameof(DipHeight));
        }
    }

    public int TargetPixelWidth => ViewMath.ToPixels(PageSize.Width, _zoom, _dpiScale);
    public int TargetPixelHeight => ViewMath.ToPixels(PageSize.Height, _zoom, _dpiScale);

    public void OnRealized(Func<int, int, int, RenderFlags, int, CancellationToken, Task<RenderedBitmap>> renderFunc, BitmapCache cache, int priority)
    {
        // Check cache first
        if (cache.TryGet(Index, TargetPixelWidth, out var cached))
        {
            Bitmap = cached;
            // Still check if stale? cache already checks, but if zoom changed, it would miss.
            // If we have bitmap but stale, we should re-render but keep showing old.
            if (!ViewMath.IsStale(cached!.PixelWidth, TargetPixelWidth))
                return;
        }

        // Cancel previous
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsRendering = true;
        int w = TargetPixelWidth;
        int h = TargetPixelHeight;

        Task.Run(async () =>
        {
            try
            {
                var rendered = await renderFunc(Index, w, h, RenderFlags.Annotations, priority, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                // Create WriteableBitmap on UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        var wb = new WriteableBitmap(rendered.Width, rendered.Height, 96 * _dpiScale, 96 * _dpiScale, PixelFormats.Bgra32, null);
                        wb.WritePixels(new Int32Rect(0, 0, rendered.Width, rendered.Height), rendered.Pixels, rendered.Stride, 0);
                        // If this page is still the target size (or close), set
                        Bitmap = wb;
                        cache.Add(Index, w, wb);
                    }
                    catch { }
                    finally
                    {
                        IsRendering = false;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Application.Current.Dispatcher.InvokeAsync(() => IsRendering = false);
            }
            catch
            {
                Application.Current.Dispatcher.InvokeAsync(() => IsRendering = false);
            }
        }, token);
    }

    public void OnUnrealized()
    {
        _cts?.Cancel();
        _cts = null;
        IsRendering = false;
    }
}
