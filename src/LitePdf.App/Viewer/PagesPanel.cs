using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LitePdf.App.Viewer;

/// <summary>
/// Virtualizing, scrollable panel that shows only the pages intersecting the viewport. Page positions come from
/// <see cref="Core.Layout.DocumentLayout"/>, so scrolling is exact for any mix of page sizes, and children are
/// arranged relative to the viewport (no huge coordinates).
/// </summary>
internal sealed class PagesPanel : Panel, IScrollInfo
{
    private readonly PdfViewer _viewer;
    private readonly Dictionary<int, PageVisual> _active = new();
    private readonly Stack<PageVisual> _pool = new();
    private Size _viewport;
    private Size _extent;
    private double _horizontal;
    private double _vertical;

    public PagesPanel(PdfViewer viewer)
    {
        _viewer = viewer;
        Background = Brushes.Transparent; // receive mouse input between pages
        ClipToBounds = true;
    }

    public IReadOnlyCollection<PageVisual> ActiveVisuals => _active.Values;

    public PageVisual? GetVisual(int pageIndex) => _active.GetValueOrDefault(pageIndex);

    /// <summary>X offset that centers content narrower than the viewport.</summary>
    public double ContentOffsetX => _viewer.Layout is { } layout ? Math.Max(0, (_viewport.Width - layout.Width) / 2) : 0;

    public Point ViewportToLayout(Point p) => new(p.X - ContentOffsetX + _horizontal, p.Y + _vertical);

    public Point LayoutToViewport(Point p) => new(p.X + ContentOffsetX - _horizontal, p.Y - _vertical);

    public void RecycleAll()
    {
        foreach (var visual in _active.Values)
        {
            visual.Detach();
            _pool.Push(visual);
        }
        _active.Clear();
        Children.Clear();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = _viewer.Layout;
        double width = double.IsInfinity(availableSize.Width) ? layout?.Width ?? 0 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? layout?.Height ?? 0 : availableSize.Height;
        var viewport = new Size(width, height);
        bool viewportChanged = viewport != _viewport;
        _viewport = viewport;
        _extent = layout is null ? new Size(0, 0) : new Size(Math.Max(layout.Width, width), layout.Height);
        _horizontal = Math.Clamp(_horizontal, 0, Math.Max(0, _extent.Width - width));
        _vertical = Math.Clamp(_vertical, 0, Math.Max(0, _extent.Height - height));

        Realize();
        foreach (var (page, visual) in _active)
        {
            var r = layout!.GetPageRect(page);
            visual.Measure(new Size(r.Width, r.Height));
        }

        ScrollOwner?.InvalidateScrollInfo();
        if (viewportChanged) _viewer.OnViewportSizeChanged();
        return viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = _viewer.Layout;
        if (layout is null) return finalSize;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double offsetX = ContentOffsetX - _horizontal;
        foreach (var (page, visual) in _active)
        {
            var r = layout.GetPageRect(page);
            double x = Math.Round((r.X + offsetX) * dpi) / dpi;
            double y = Math.Round((r.Y - _vertical) * dpi) / dpi;
            visual.Arrange(new Rect(x, y, r.Width, r.Height));
        }
        return finalSize;
    }

    private void Realize()
    {
        var layout = _viewer.Layout;
        var (first, last) = layout is null || _viewer.Session is null
            ? (-1, -1)
            : layout.GetPagesInRange(_vertical - _viewport.Height * 0.5, _vertical + _viewport.Height * 1.5);

        if (_active.Count > 0)
        {
            foreach (int page in _active.Keys.Where(p => p < first || p > last).ToList())
            {
                var visual = _active[page];
                _active.Remove(page);
                visual.Detach();
                Children.Remove(visual);
                _pool.Push(visual);
            }
        }

        if (first < 0) return;
        for (int page = first; page <= last; page++)
        {
            if (_active.ContainsKey(page)) continue;
            var visual = _pool.Count > 0 ? _pool.Pop() : new PageVisual(_viewer);
            _active[page] = visual;
            Children.Add(visual);
            visual.Attach(page);
        }
    }

    // ---- IScrollInfo ----

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _horizontal;
    public double VerticalOffset => _vertical;

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (double.IsNaN(offset) || Math.Abs(offset - _vertical) < 0.01) return;
        _vertical = offset;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
        _viewer.OnScrolled();
    }

    public void SetHorizontalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        if (double.IsNaN(offset) || Math.Abs(offset - _horizontal) < 0.01) return;
        _horizontal = offset;
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
        _viewer.OnScrolled();
    }

    /// <summary>Sets both offsets after a layout change (zoom) without clamping against a stale extent.</summary>
    public void SetOffsetsForNewLayout(double horizontal, double vertical)
    {
        var layout = _viewer.Layout;
        if (layout is null) return;
        double extentWidth = Math.Max(layout.Width, _viewport.Width);
        _horizontal = Math.Clamp(horizontal, 0, Math.Max(0, extentWidth - _viewport.Width));
        _vertical = Math.Clamp(vertical, 0, Math.Max(0, layout.Height - _viewport.Height));
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
        _viewer.OnScrolled();
    }

    private const double LineStep = 48;

    public void LineUp() => SetVerticalOffset(_vertical - LineStep);
    public void LineDown() => SetVerticalOffset(_vertical + LineStep);
    public void LineLeft() => SetHorizontalOffset(_horizontal - LineStep);
    public void LineRight() => SetHorizontalOffset(_horizontal + LineStep);
    public void PageUp() => SetVerticalOffset(_vertical - Math.Max(LineStep, _viewport.Height - LineStep));
    public void PageDown() => SetVerticalOffset(_vertical + Math.Max(LineStep, _viewport.Height - LineStep));
    public void PageLeft() => SetHorizontalOffset(_horizontal - Math.Max(LineStep, _viewport.Width - LineStep));
    public void PageRight() => SetHorizontalOffset(_horizontal + Math.Max(LineStep, _viewport.Width - LineStep));
    public void MouseWheelUp() => SetVerticalOffset(_vertical - LineStep * 2.5);
    public void MouseWheelDown() => SetVerticalOffset(_vertical + LineStep * 2.5);
    public void MouseWheelLeft() => SetHorizontalOffset(_horizontal - LineStep * 2.5);
    public void MouseWheelRight() => SetHorizontalOffset(_horizontal + LineStep * 2.5);

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle; // navigation is explicit; ignore focus-driven scrolling
}
