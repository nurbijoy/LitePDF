using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LitePdf.App.Documents;
using LitePdf.Core;
using LitePdf.Core.Layout;
using LitePdf.Core.Storage;
using LitePdf.Core.Text;

namespace LitePdf.App.Viewer;

public enum ViewerTool
{
    Select,
    Hand,
    Region,
}

public sealed record ViewState(int PageIndex, double PageOffset, double Zoom, ZoomMode ZoomMode, int Rotation, PageLayoutMode LayoutMode);

public sealed record ViewerContext(int PageIndex, PointD? Point, PdfAnnotation? Annotation, PdfLink? Link, bool HasSelection);

public readonly record struct SearchHitRef(int PageIndex, TextMatch Match);

internal readonly record struct RegionDraftState(int PageIndex, RectD Rect);

/// <summary>Continuous-scroll document viewer with text selection, links, annotations selection and region tool.</summary>
public sealed class PdfViewer : Border
{
    private readonly ScrollViewer _scroll;
    private readonly PagesPanel _panel;
    private readonly DispatcherTimer _settleTimer;
    private readonly DispatcherTimer _autoScrollTimer;
    private IReadOnlyDictionary<int, IReadOnlyList<TextMatch>>? _searchHits;
    private ViewState? _pendingState;
    private bool _layoutReady;

    private DragMode _drag;
    private Point _dragOrigin;
    private bool _dragMoved;
    private (double H, double V) _panStart;
    private TextPosition? _anchor;
    private PdfLink? _pendingLink;
    private PdfAnnotation? _pendingAnnotation;
    private PdfLink? _hoverLink;
    private int _regionPage;
    private PointD _regionStart;
    private double _autoScrollSpeed;

    private enum DragMode
    {
        None,
        Select,
        Pan,
        Region,
    }

    public PdfViewer()
    {
        _panel = new PagesPanel(this);
        _scroll = new ScrollViewer
        {
            CanContentScroll = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _panel,
            Focusable = true,
            FocusVisualStyle = null,
        };
        Child = _scroll;

        _settleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _settleTimer.Tick += (_, _) => OnSettled();
        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScrollTimer.Tick += (_, _) => OnAutoScroll();

        _panel.MouseLeftButtonDown += OnMouseLeftButtonDown;
        _panel.MouseMove += OnMouseMove;
        _panel.MouseLeftButtonUp += OnMouseLeftButtonUp;
        _panel.MouseRightButtonUp += OnMouseRightButtonUp;
        _panel.LostMouseCapture += (_, _) => EndDrag();
        _panel.MouseLeave += (_, _) => { if (_hoverLink is not null) { _hoverLink = null; LinkHovered?.Invoke(null); } };
        _scroll.PreviewMouseWheel += OnPreviewMouseWheel;
        _scroll.PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => DpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    public DocumentSession? Session { get; private set; }
    public DocumentLayout? Layout { get; private set; }
    public RenderCache Cache { get; } = new();
    public double DpiScale { get; private set; } = 1;
    public double Zoom { get; private set; } = 1;
    public ZoomMode ZoomMode { get; private set; } = ZoomMode.FitWidth;
    public int Rotation { get; private set; }
    public PageLayoutMode LayoutMode { get; private set; } = PageLayoutMode.SinglePage;
    public PageColorMode ColorMode { get; private set; }
    public int CurrentPageIndex { get; private set; }
    public TextRange? Selection { get; private set; }
    public PdfAnnotation? SelectedAnnotation { get; private set; }
    public SearchHitRef? CurrentSearchHit { get; private set; }
    public IReadOnlyDictionary<int, IReadOnlyList<TextMatch>>? SearchHits => _searchHits;
    internal RegionDraftState? RegionDraft { get; private set; }

    public ViewerTool Tool
    {
        get;
        set
        {
            field = value;
            RegionDraft = null;
            _panel.Cursor = value switch { ViewerTool.Hand => Cursors.Hand, ViewerTool.Region => Cursors.Cross, _ => null };
        }
    }

    public event Action? CurrentPageChanged;
    public event Action? ZoomChanged;
    public event Action? SelectionChanged;
    public event Action? SelectedAnnotationChanged;
    public event Action<PdfLink>? LinkClicked;
    public event Action<PdfLink?>? LinkHovered;
    public event Action<int, RectD>? RegionSelected;
    public event Action<ViewerContext>? ContextRequested;
    public event Action<PdfAnnotation>? NoteActivated;
    public event Action<int, PageText>? PageTextLoaded;
    public event Action? DeleteRequested;

    // ---- Document lifetime ----

    public void Open(DocumentSession session, ViewState? state, ZoomMode defaultZoomMode)
    {
        Close();
        Session = session;
        session.PageInvalidated += OnPageInvalidated;
        session.PageTextChanged += OnPageTextChanged;
        session.DocumentReplaced += OnDocumentReplaced;

        Rotation = state?.Rotation ?? 0;
        LayoutMode = state?.LayoutMode ?? PageLayoutMode.SinglePage;
        ZoomMode = state?.ZoomMode ?? defaultZoomMode;
        Zoom = state?.Zoom ?? 1;
        _pendingState = state;
        _layoutReady = false;
        if (_scroll.ActualWidth > 0) Dispatcher.BeginInvoke(ApplyInitialLayout, DispatcherPriority.Loaded);
    }

    public void Close()
    {
        if (Session is { } old)
        {
            old.PageInvalidated -= OnPageInvalidated;
            old.PageTextChanged -= OnPageTextChanged;
            old.DocumentReplaced -= OnDocumentReplaced;
        }
        _settleTimer.Stop();
        _autoScrollTimer.Stop();
        Session = null;
        Layout = null;
        Selection = null;
        SelectedAnnotation = null;
        _searchHits = null;
        CurrentSearchHit = null;
        RegionDraft = null;
        CurrentPageIndex = 0;
        Cache.Clear();
        _panel.RecycleAll();
        _panel.SetOffsetsForNewLayout(0, 0);
    }

    private void ApplyInitialLayout()
    {
        if (Session is null || _layoutReady || _scroll.ActualWidth <= 0) return;
        _layoutReady = true;
        if (ZoomMode != ZoomMode.Custom) Zoom = ComputeFitZoom(ZoomMode);
        Layout = new DocumentLayout(Session.PageSizes, Zoom, Rotation, LayoutMode);
        _panel.InvalidateMeasure();
        if (_pendingState is { } state && state.PageIndex < Session.PageCount)
            GoToPage(state.PageIndex, pageOffset: state.PageOffset);
        else
            _panel.SetOffsetsForNewLayout(0, 0);
        _pendingState = null;
        UpdateCurrentPage(force: true);
        ZoomChanged?.Invoke();
        ScheduleSettle();
    }

    internal void OnViewportSizeChanged()
    {
        if (Session is null) return;
        if (!_layoutReady)
        {
            Dispatcher.BeginInvoke(ApplyInitialLayout, DispatcherPriority.Loaded);
            return;
        }
        if (ZoomMode != ZoomMode.Custom)
            Dispatcher.BeginInvoke(() => { if (ZoomMode != ZoomMode.Custom) ApplyZoom(ComputeFitZoom(ZoomMode), ZoomMode, null); }, DispatcherPriority.Loaded);
    }

    public ViewState? GetViewState()
    {
        if (Layout is null) return _pendingState;
        var r = Layout.GetPageRect(CurrentPageIndex);
        double offset = Math.Clamp((_panel.VerticalOffset - r.Y) / r.Height, -0.05, 1);
        return new ViewState(CurrentPageIndex, offset, Zoom, ZoomMode, Rotation, LayoutMode);
    }

    // ---- Navigation & zoom ----

    public void GoToPage(int pageIndex, double? y = null, double? pageOffset = null)
    {
        if (Layout is null || Session is null) return;
        pageIndex = Math.Clamp(pageIndex, 0, Session.PageCount - 1);
        var r = Layout.GetPageRect(pageIndex);
        double v = pageOffset is { } f ? r.Y + f * r.Height
            : y is { } ny ? r.Y + ViewTransform.ToView(new PointD(0.5, ny), Rotation).Y * r.Height - 16
            : r.Y - Layout.Gap / 2;

        double h = _panel.HorizontalOffset, vw = _panel.ViewportWidth, cx = Math.Max(0, (vw - Layout.Width) / 2);
        if (r.X + cx - h < 0 || r.Right + cx - h > vw)
            h = r.Width > vw ? r.X + cx - Layout.Margin : r.X + cx + r.Width / 2 - vw / 2;

        _panel.SetOffsetsForNewLayout(h, v);
        SetCurrentPage(pageIndex);
    }

    public void GoToDestination(Destination destination)
    {
        if (destination.IsValid) GoToPage(destination.PageIndex, destination.Y);
    }

    /// <summary>Scrolls so the normalized rectangle on a page is visible, centering it when it isn't.</summary>
    public void ScrollIntoView(int pageIndex, RectD rect)
    {
        if (Layout is null) return;
        var r = Layout.GetPageRect(pageIndex);
        var view = ViewTransform.ToView(rect, Rotation);
        double cx = _panel.ContentOffsetX;
        double top = r.Y + view.Top * r.Height, bottom = r.Y + view.Bottom * r.Height;
        double left = r.X + cx + view.Left * r.Width, right = r.X + cx + view.Right * r.Width;
        double v = _panel.VerticalOffset, h = _panel.HorizontalOffset, vw = _panel.ViewportWidth, vh = _panel.ViewportHeight;
        if (top < v + 32 || bottom > v + vh - 32) v = (top + bottom) / 2 - vh / 2;
        if (left < h || right > h + vw) h = (left + right) / 2 - vw / 2;
        _panel.SetOffsetsForNewLayout(h, v);
    }

    /// <summary>Sets a custom zoom, keeping the point under <paramref name="anchor"/> (default: viewport center) in place.</summary>
    public void SetZoom(double zoom, Point? anchor = null) =>
        ApplyZoom(ViewMath.ClampZoom(zoom), ZoomMode.Custom, anchor ?? new Point(_panel.ViewportWidth / 2, _panel.ViewportHeight / 2));

    public void ZoomIn() => SetZoom(ViewMath.ZoomIn(Zoom));

    public void ZoomOut() => SetZoom(ViewMath.ZoomOut(Zoom));

    public void SetZoomMode(ZoomMode mode)
    {
        if (mode == ZoomMode.Custom || Session is null)
        {
            ZoomMode = mode;
            ZoomChanged?.Invoke();
            return;
        }
        ApplyZoom(ComputeFitZoom(mode), mode, null);
    }

    private void ApplyZoom(double zoom, ZoomMode mode, Point? anchor)
    {
        ZoomMode = mode;
        if (Layout is null || Session is null)
        {
            Zoom = zoom;
            ZoomChanged?.Invoke();
            return;
        }
        if (Math.Abs(zoom - Zoom) < 1e-4)
        {
            ZoomChanged?.Invoke();
            return;
        }

        // Keep the document point under the anchor fixed on screen.
        var a = anchor ?? new Point(_panel.ViewportWidth / 2, 0);
        var lp = _panel.ViewportToLayout(a);
        int page = Layout.GetNearestPage(lp.X, lp.Y);
        var old = Layout.GetPageRect(page);
        double fx = (lp.X - old.X) / old.Width, fy = (lp.Y - old.Y) / old.Height;

        Zoom = zoom;
        Layout = new DocumentLayout(Session.PageSizes, Zoom, Rotation, LayoutMode);
        var r = Layout.GetPageRect(page);
        double cx = Math.Max(0, (_panel.ViewportWidth - Layout.Width) / 2);
        _panel.SetOffsetsForNewLayout(r.X + fx * r.Width + cx - a.X, r.Y + fy * r.Height - a.Y);
        ScheduleSettle();
        ZoomChanged?.Invoke();
    }

    private double ComputeFitZoom(ZoomMode mode)
    {
        if (Session is null || Session.PageCount == 0) return 1;
        const double scrollbarReserve = 14;
        double availableWidth = Math.Max(60, _scroll.ActualWidth - 2 * DocumentLayout.DefaultMargin - scrollbarReserve);
        double availableHeight = Math.Max(60, _scroll.ActualHeight - 2 * DocumentLayout.DefaultMargin);
        if (mode == ZoomMode.FitPage)
        {
            var (w, h, gaps) = DocumentLayout.GetRowSizePoints(Session.PageSizes, CurrentPageIndex, Rotation, LayoutMode);
            return ViewMath.FitPage(availableWidth - gaps * DocumentLayout.DefaultGap, availableHeight, w, h);
        }
        var (width, rowGaps) = DocumentLayout.GetWidestRowPoints(Session.PageSizes, Rotation, LayoutMode);
        return ViewMath.FitWidth(availableWidth - rowGaps * DocumentLayout.DefaultGap, width);
    }

    public void Rotate(int quarterTurns)
    {
        if (Session is null) return;
        int page = CurrentPageIndex;
        Rotation = (Rotation + quarterTurns % 4 + 4) % 4;
        RebuildLayoutKeepingPage(page);
    }

    public void SetLayoutMode(PageLayoutMode mode)
    {
        if (LayoutMode == mode) return;
        LayoutMode = mode;
        if (Session is not null) RebuildLayoutKeepingPage(CurrentPageIndex);
    }

    private void RebuildLayoutKeepingPage(int page)
    {
        if (Session is null) return;
        if (ZoomMode != ZoomMode.Custom) Zoom = ComputeFitZoom(ZoomMode);
        Layout = new DocumentLayout(Session.PageSizes, Zoom, Rotation, LayoutMode);
        foreach (var visual in _panel.ActiveVisuals) visual.ClearForModeChange();
        GoToPage(page);
        ZoomChanged?.Invoke();
        ScheduleSettle();
    }

    public void SetColorMode(PageColorMode mode)
    {
        if (ColorMode == mode) return;
        ColorMode = mode;
        foreach (var visual in _panel.ActiveVisuals) visual.ClearForModeChange();
    }

    // ---- Scrolling state ----

    internal void OnScrolled()
    {
        UpdateCurrentPage(force: false);
        ScheduleSettle();
    }

    private void UpdateCurrentPage(bool force)
    {
        if (Layout is null) return;
        double vh = _panel.ViewportHeight;
        int page = _panel.VerticalOffset + vh >= Layout.Height - 1 && _panel.VerticalOffset > 0
            ? Layout.GetPageAtOffset(Layout.Height)
            : Layout.GetPageAtOffset(_panel.VerticalOffset + vh * 0.3);
        if (force || page != CurrentPageIndex) SetCurrentPage(page);
    }

    private void SetCurrentPage(int page)
    {
        if (page < 0) return;
        CurrentPageIndex = page;
        CurrentPageChanged?.Invoke();
    }

    private void ScheduleSettle()
    {
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void OnSettled()
    {
        _settleTimer.Stop();
        if (Layout is null) return;
        double cx = _panel.ContentOffsetX;
        var viewport = new Rect(_panel.HorizontalOffset - cx, _panel.VerticalOffset, _panel.ViewportWidth, _panel.ViewportHeight);
        foreach (var visual in _panel.ActiveVisuals)
        {
            if (visual.PageIndex < 0) continue;
            visual.EnsureBitmap();
            var r = Layout.GetPageRect(visual.PageIndex);
            var pageRect = new Rect(r.X, r.Y, r.Width, r.Height);
            var visible = Rect.Intersect(pageRect, viewport);
            if (!visible.IsEmpty) visual.EnsureDetail(new Rect(visible.X - r.X, visible.Y - r.Y, visible.Width, visible.Height));
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        DpiScale = newDpi.DpiScaleX;
        ScheduleSettle();
    }

    // ---- Session events ----

    private void OnPageInvalidated(int page)
    {
        if (SelectedAnnotation?.PageIndex == page) SetSelectedAnnotation(null);
        _panel.GetVisual(page)?.Refresh(reloadText: false);
    }

    private void OnPageTextChanged(int page) => _panel.GetVisual(page)?.Refresh(reloadText: true);

    private void OnDocumentReplaced()
    {
        SetSelectedAnnotation(null);
        Cache.Clear();
        var state = GetViewState();
        _panel.RecycleAll();
        if (Session is not null && Layout is not null && Session.PageCount == Layout.PageCount && state is not null)
        {
            _panel.InvalidateMeasure();
            GoToPage(state.PageIndex, pageOffset: state.PageOffset);
        }
    }

    internal void OnPageTextLoaded(int page, PageText text) => PageTextLoaded?.Invoke(page, text);

    // ---- Selection, search & annotations ----

    public void ClearSelection() => SetSelection(null);

    public void SetSelection(TextRange? range)
    {
        if (range is { IsEmpty: true }) range = null;
        if (Nullable.Equals(range, Selection)) return;
        Selection = range;
        InvalidatePages();
        SelectionChanged?.Invoke();
    }

    public async Task SelectAllOnCurrentPageAsync()
    {
        if (Session is null) return;
        int page = CurrentPageIndex;
        var text = await Session.GetTextAsync(page, RenderPriority.Interactive);
        if (text.Length > 0) SetSelection(new TextRange(new TextPosition(page, 0), new TextPosition(page, text.Length)));
    }

    public async Task<string> GetSelectedTextAsync()
    {
        if (Session is null || Selection is not { } range) return string.Empty;
        var sb = new StringBuilder();
        for (int page = range.Start.PageIndex; page <= range.End.PageIndex; page++)
        {
            var text = await Session.GetTextAsync(page, RenderPriority.Interactive);
            if (range.GetPageSpan(page, text.Length) is not { } span) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text.GetText(span.Start, span.End));
        }
        return sb.ToString().Trim().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    public void SetSelectedAnnotation(PdfAnnotation? annotation)
    {
        if (Equals(annotation, SelectedAnnotation)) return;
        SelectedAnnotation = annotation;
        InvalidatePages();
        SelectedAnnotationChanged?.Invoke();
    }

    public void SetSearchHits(IReadOnlyDictionary<int, IReadOnlyList<TextMatch>>? hits)
    {
        _searchHits = hits;
        if (hits is null) CurrentSearchHit = null;
        InvalidatePages();
    }

    public IReadOnlyList<TextMatch>? GetSearchHits(int page) => _searchHits?.GetValueOrDefault(page);

    public void ShowSearchHit(SearchHitRef hit)
    {
        CurrentSearchHit = hit;
        if (Session?.TryGetCachedText(hit.PageIndex) is { } text)
        {
            var rects = text.GetRangeRects(hit.Match.Start, hit.Match.Start + hit.Match.Length);
            if (rects.Count > 0)
            {
                ScrollIntoView(hit.PageIndex, rects.Aggregate(RectD.Empty, (a, b) => a.Union(b)));
                SetCurrentPage(hit.PageIndex);
            }
            else
            {
                GoToPage(hit.PageIndex);
            }
        }
        else
        {
            GoToPage(hit.PageIndex);
        }
        InvalidatePages();
    }

    public void RefreshPage(int page) => _panel.GetVisual(page)?.Refresh(reloadText: false);

    private void InvalidatePages()
    {
        foreach (var visual in _panel.ActiveVisuals) visual.InvalidateVisual();
    }

    // ---- Input ----

    private readonly record struct Hit(int PageIndex, PointD Point, bool OnPage, double Tolerance);

    private Hit? HitTest(Point viewportPoint, bool nearest)
    {
        if (Layout is null || Session is null || Layout.PageCount == 0) return null;
        var lp = _panel.ViewportToLayout(viewportPoint);
        int page = Layout.HitTest(lp.X, lp.Y);
        bool onPage = page >= 0;
        if (!onPage)
        {
            if (!nearest) return null;
            page = Layout.GetNearestPage(lp.X, lp.Y);
        }
        var r = Layout.GetPageRect(page);
        var view = new PointD((lp.X - r.X) / r.Width, (lp.Y - r.Y) / r.Height);
        return new Hit(page, ViewTransform.FromView(view, Rotation), onPage, 3 / Math.Max(1, Math.Min(r.Width, r.Height)));
    }

    private PdfLink? FindLink(Hit hit) =>
        Session?.TryGetCachedLinks(hit.PageIndex)?.LastOrDefault(l => l.Bounds.Contains(hit.Point, hit.Tolerance));

    private PdfAnnotation? FindAnnotation(Hit hit) =>
        Session?.TryGetCachedAnnotations(hit.PageIndex)?.LastOrDefault(a => a.IsEditable && a.HitTest(hit.Point, hit.Tolerance));

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _scroll.Focus();
        if (Session is null) return;
        var position = e.GetPosition(_panel);
        _dragOrigin = position;
        _dragMoved = false;
        e.Handled = true;

        if (Tool == ViewerTool.Hand || Keyboard.IsKeyDown(Key.Space))
        {
            _drag = DragMode.Pan;
            _panStart = (_panel.HorizontalOffset, _panel.VerticalOffset);
            _panel.CaptureMouse();
            return;
        }

        var hit = HitTest(position, nearest: false);
        if (Tool == ViewerTool.Region)
        {
            if (hit is { } h)
            {
                _drag = DragMode.Region;
                _regionPage = h.PageIndex;
                _regionStart = h.Point;
                RegionDraft = new RegionDraftState(h.PageIndex, RectD.FromPoints(h.Point, h.Point));
                _panel.CaptureMouse();
            }
            return;
        }

        _pendingLink = null;
        _pendingAnnotation = null;
        bool extend = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (hit is { } target && Session.TryGetCachedText(target.PageIndex) is { } text)
        {
            int index = text.HitTest(target.Point, target.Tolerance);
            if (e.ClickCount == 2)
            {
                if (FindAnnotation(target) is { Kind: AnnotationKind.Note } note)
                {
                    NoteActivated?.Invoke(note);
                    return;
                }
                var (s, end) = text.GetWordRange(index >= 0 ? index : text.GetCaretIndex(target.Point));
                SetSelection(new TextRange(new TextPosition(target.PageIndex, s), new TextPosition(target.PageIndex, end)));
                return;
            }
            if (e.ClickCount >= 3)
            {
                var (s, end) = text.GetLineRange(index >= 0 ? index : text.GetCaretIndex(target.Point));
                SetSelection(new TextRange(new TextPosition(target.PageIndex, s), new TextPosition(target.PageIndex, end)));
                return;
            }

            var caret = new TextPosition(target.PageIndex, text.GetCaretIndex(target.Point));
            if (extend && Selection is { } existing)
            {
                _anchor = caret >= existing.End ? existing.Start : existing.End;
                SetSelection(new TextRange(_anchor.Value, caret));
            }
            else
            {
                _anchor = caret;
            }
        }
        else
        {
            _anchor = null;
        }

        if (hit is { } h2)
        {
            _pendingLink = FindLink(h2);
            _pendingAnnotation = FindAnnotation(h2);
        }
        if (!extend) SetSelection(null);
        _drag = DragMode.Select;
        _panel.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(_panel);
        switch (_drag)
        {
            case DragMode.Pan:
                _panel.SetHorizontalOffset(_panStart.H - (position.X - _dragOrigin.X));
                _panel.SetVerticalOffset(_panStart.V - (position.Y - _dragOrigin.Y));
                break;

            case DragMode.Region:
                if (HitTest(position, nearest: true) is { } h && h.PageIndex == _regionPage)
                {
                    var p = new PointD(Math.Clamp(h.Point.X, 0, 1), Math.Clamp(h.Point.Y, 0, 1));
                    RegionDraft = new RegionDraftState(_regionPage, RectD.FromPoints(_regionStart, p));
                    _panel.GetVisual(_regionPage)?.InvalidateVisual();
                }
                break;

            case DragMode.Select:
                if (!_dragMoved && (position - _dragOrigin).Length < 4) return;
                _dragMoved = true;
                ExtendSelection(position);
                _autoScrollSpeed = position.Y < 0 ? position.Y : position.Y > _panel.ViewportHeight ? position.Y - _panel.ViewportHeight : 0;
                if (_autoScrollSpeed != 0) _autoScrollTimer.Start();
                else _autoScrollTimer.Stop();
                break;

            default:
                UpdateHover(position);
                break;
        }
    }

    private void ExtendSelection(Point position)
    {
        if (Session is null) return;
        if (_anchor is null)
        {
            if (HitTest(_dragOrigin, nearest: true) is not { } start || Session.TryGetCachedText(start.PageIndex) is not { } startText) return;
            _anchor = new TextPosition(start.PageIndex, startText.GetCaretIndex(start.Point));
        }
        if (HitTest(position, nearest: true) is not { } hit) return;
        if (Session.TryGetCachedText(hit.PageIndex) is not { } text)
        {
            _ = Session.GetTextAsync(hit.PageIndex, RenderPriority.Interactive);
            return;
        }
        SetSelection(new TextRange(_anchor.Value, new TextPosition(hit.PageIndex, text.GetCaretIndex(hit.Point))));
    }

    private void OnAutoScroll()
    {
        if (_drag != DragMode.Select || _autoScrollSpeed == 0)
        {
            _autoScrollTimer.Stop();
            return;
        }
        _panel.SetVerticalOffset(_panel.VerticalOffset + Math.Clamp(_autoScrollSpeed, -60, 60) * 0.6);
        ExtendSelection(Mouse.GetPosition(_panel));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var mode = _drag;
        var link = _pendingLink;
        var annotation = _pendingAnnotation;
        bool moved = _dragMoved;
        EndDrag();
        e.Handled = true;

        switch (mode)
        {
            case DragMode.Select when !moved:
                if (link is not null && annotation is null) LinkClicked?.Invoke(link);
                else SetSelectedAnnotation(annotation);
                break;

            case DragMode.Region:
                var draft = RegionDraft;
                RegionDraft = null;
                _panel.GetVisual(_regionPage)?.InvalidateVisual();
                if (draft is { } d && Layout is not null)
                {
                    var r = Layout.GetPageRect(d.PageIndex);
                    var view = ViewTransform.ToView(d.Rect, Rotation);
                    if (view.Width * r.Width >= 8 && view.Height * r.Height >= 8) RegionSelected?.Invoke(d.PageIndex, d.Rect);
                }
                break;
        }
        UpdateHover(e.GetPosition(_panel));
    }

    private void EndDrag()
    {
        _drag = DragMode.None;
        _autoScrollTimer.Stop();
        _pendingLink = null;
        _pendingAnnotation = null;
        if (_panel.IsMouseCaptured) _panel.ReleaseMouseCapture();
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Session is null) return;
        var hit = HitTest(e.GetPosition(_panel), nearest: false);
        PdfAnnotation? annotation = null;
        PdfLink? link = null;
        if (hit is { } h)
        {
            annotation = FindAnnotation(h);
            link = FindLink(h);
            if (annotation is not null) SetSelectedAnnotation(annotation);
        }
        ContextRequested?.Invoke(new ViewerContext(hit?.PageIndex ?? CurrentPageIndex, hit?.Point, annotation, link, Selection is not null));
        e.Handled = true;
    }

    private void UpdateHover(Point position)
    {
        Cursor? cursor = Tool switch { ViewerTool.Hand => Cursors.Hand, ViewerTool.Region => Cursors.Cross, _ => null };
        PdfLink? link = null;
        if (Tool == ViewerTool.Select && HitTest(position, nearest: false) is { } hit)
        {
            link = FindLink(hit);
            if (link is not null || FindAnnotation(hit) is not null) cursor = Cursors.Hand;
            else if (Session?.TryGetCachedText(hit.PageIndex) is { } text && text.HitTest(hit.Point, hit.Tolerance) >= 0) cursor = Cursors.IBeam;
        }
        if (!ReferenceEquals(_panel.Cursor, cursor)) _panel.Cursor = cursor;
        if (!Equals(link, _hoverLink))
        {
            _hoverLink = link;
            LinkHovered?.Invoke(link);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Session is null) return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetZoom(Zoom * Math.Pow(1.0015, e.Delta), e.GetPosition(_panel));
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _panel.SetHorizontalOffset(_panel.HorizontalOffset - e.Delta);
        else _panel.SetVerticalOffset(_panel.VerticalOffset - e.Delta);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Session is null) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Left when !ctrl && _panel.ExtentWidth <= _panel.ViewportWidth + 1:
                e.Handled = true;
                GoToPage(CurrentPageIndex - (Layout?.PagesPerRow ?? 1));
                break;
            case Key.Right when !ctrl && _panel.ExtentWidth <= _panel.ViewportWidth + 1:
                e.Handled = true;
                GoToPage(CurrentPageIndex + (Layout?.PagesPerRow ?? 1));
                break;
            case Key.Home when !ctrl:
                e.Handled = true;
                GoToPage(0);
                break;
            case Key.End when !ctrl:
                e.Handled = true;
                GoToPage(Session.PageCount - 1);
                break;
            case Key.A when ctrl:
                e.Handled = true;
                await SelectAllOnCurrentPageAsync();
                break;
            case Key.Escape:
                if (Selection is not null || SelectedAnnotation is not null || RegionDraft is not null)
                {
                    e.Handled = true;
                    RegionDraft = null;
                    SetSelection(null);
                    SetSelectedAnnotation(null);
                }
                break;
            case Key.Delete when SelectedAnnotation is not null:
                e.Handled = true;
                DeleteRequested?.Invoke();
                break;
        }
    }

    public new void Focus() => _scroll.Focus();
}
