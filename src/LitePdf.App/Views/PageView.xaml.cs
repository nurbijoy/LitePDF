using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LitePdf.App.ViewModels;
using LitePdf.Core;

namespace LitePdf.App.Views;

public partial class PageView : UserControl
{
    private PageViewModel? _vm;
    private Point _dragStart;
    private bool _isDragging;
    private int _selectionStart = -1;
    private int _clickCount = 0;
    private DateTime _lastClickTime = DateTime.MinValue;
    private Point _lastClickPos;

    public PageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;

        PageGrid.MouseLeftButtonDown += OnMouseDown;
        PageGrid.MouseMove += OnMouseMove;
        PageGrid.MouseLeftButtonUp += OnMouseUp;
        PageGrid.MouseRightButtonUp += OnRightClick;
        LinkCanvas.MouseMove += OnLinkMouseMove;
        LinkCanvas.MouseLeftButtonUp += OnLinkClick;

        // Context menu
        var ctx = new ContextMenu();
        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += (s, e) => CopySelection();
        ctx.Items.Add(copyItem);

        var copyImageItem = new MenuItem { Header = "Copy page as image" };
        copyImageItem.Click += (s, e) => CopyPageAsImage();
        ctx.Items.Add(copyImageItem);

        var highlightMenu = new MenuItem { Header = "Highlight" };
        foreach (var color in Core.Annotations.AnnotationColor.Palette)
        {
            var mi = new MenuItem { Header = color.Name, Tag = color };
            mi.Click += (s, e) => HighlightSelection((Core.Annotations.AnnotationColor)((MenuItem)s).Tag);
            highlightMenu.Items.Add(mi);
        }
        ctx.Items.Add(highlightMenu);

        var underlineItem = new MenuItem { Header = "Underline" };
        underlineItem.Click += (s, e) => AddAnnotation(Core.Annotations.AnnotationType.Underline);
        ctx.Items.Add(underlineItem);

        var strikeItem = new MenuItem { Header = "Strikeout" };
        strikeItem.Click += (s, e) => AddAnnotation(Core.Annotations.AnnotationType.Strikeout);
        ctx.Items.Add(strikeItem);

        var noteItem = new MenuItem { Header = "Add sticky note" };
        noteItem.Click += (s, e) => AddStickyNote();
        ctx.Items.Add(noteItem);

        PageGrid.ContextMenu = ctx;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= Vm_PropertyChanged;
        }
        _vm = e.NewValue as PageViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += Vm_PropertyChanged;
            UpdateLayoutFromVm();
        }
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.Bitmap) ||
            e.PropertyName == nameof(PageViewModel.DipWidth) ||
            e.PropertyName == nameof(PageViewModel.DipHeight) ||
            e.PropertyName == nameof(PageViewModel.SelectionRects) ||
            e.PropertyName == nameof(PageViewModel.SearchRects) ||
            e.PropertyName == nameof(PageViewModel.IsRendering) ||
            e.PropertyName == nameof(PageViewModel.Links))
        {
            Dispatcher.BeginInvoke(UpdateLayoutFromVm);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutFromVm();
    }

    private void UpdateLayoutFromVm()
    {
        if (_vm == null) return;
        RootBorder.Width = _vm.DipWidth;
        RootBorder.Height = _vm.DipHeight;
        PageImage.Source = _vm.Bitmap;
        LoadingOverlay.Visibility = _vm.IsRendering ? Visibility.Visible : Visibility.Collapsed;

        // Selection
        SelectionCanvas.Children.Clear();
        if (_vm.SelectionRects != null)
        {
            foreach (var r in _vm.SelectionRects)
            {
                var rect = PdfRectToDip(r);
                var shape = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(80, 0, 120, 215)),
                    Width = rect.Width,
                    Height = rect.Height
                };
                Canvas.SetLeft(shape, rect.Left);
                Canvas.SetTop(shape, rect.Top);
                SelectionCanvas.Children.Add(shape);
            }
        }

        // Search
        SearchCanvas.Children.Clear();
        if (_vm.SearchRects != null)
        {
            foreach (var r in _vm.SearchRects)
            {
                var rect = PdfRectToDip(r);
                var shape = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(80, 255, 235, 59)),
                    Stroke = new SolidColorBrush(Colors.Orange),
                    StrokeThickness = 1,
                    Width = rect.Width,
                    Height = rect.Height
                };
                Canvas.SetLeft(shape, rect.Left);
                Canvas.SetTop(shape, rect.Top);
                SearchCanvas.Children.Add(shape);
            }
        }

        // Links
        LinkCanvas.Children.Clear();
        if (_vm.Links != null)
        {
            foreach (var link in _vm.Links)
            {
                var rect = PdfRectToDip(link.Rect);
                var border = new Border
                {
                    Width = rect.Width,
                    Height = rect.Height,
                    Background = Brushes.Transparent,
                    ToolTip = link.Uri ?? (link.DestPageIndex >= 0 ? $"Go to page {link.DestPageIndex + 1}" : "Link"),
                    Cursor = Cursors.Hand
                };
                Canvas.SetLeft(border, rect.Left);
                Canvas.SetTop(border, rect.Top);
                border.Tag = link;
                LinkCanvas.Children.Add(border);
            }
        }

        // Scanned detection
        if (_vm.TextLayer == null && _vm.Index >= 0)
        {
            // If char count <8 and no OCR yet, show banner (we need char count info; for now check HasText)
            if (!_vm.HasText)
            {
                ScannedBanner.Visibility = Visibility.Visible;
            }
            else
            {
                ScannedBanner.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            ScannedBanner.Visibility = Visibility.Collapsed;
        }
    }

    private Rect PdfRectToDip(RectD pdfRect)
    {
        if (_vm == null) return new Rect(0, 0, 0, 0);
        // pdfRect: Left, Top, Right, Bottom where Top > Bottom
        double left = Math.Min(pdfRect.Left, pdfRect.Right);
        double right = Math.Max(pdfRect.Left, pdfRect.Right);
        double bottom = Math.Min(pdfRect.Top, pdfRect.Bottom);
        double top = Math.Max(pdfRect.Top, pdfRect.Bottom);

        // Convert PDF points to DIP: dip = points * zoom * 96/72
        // But also need to map Y: PDF origin bottom-left, DIP origin top-left
        // So: dipY = (pageHeight - top) * zoom * 96/72 for top edge
        // DipWidth already includes zoom, so scale by DIP-per-point on each axis.
        double factorX = _vm.DipWidth / _vm.PageSize.Width;
        double factorY = _vm.DipHeight / _vm.PageSize.Height;

        double dipX = left * factorX;
        double dipW = (right - left) * factorX;
        double dipTop = (_vm.PageSize.Height - top) * factorY;
        double dipH = (top - bottom) * factorY;

        return new Rect(dipX, dipTop, dipW, dipH);
    }

    private PointD DipToPdfPoint(Point dipPoint)
    {
        if (_vm == null) return new PointD(0, 0);
        double factorX = _vm.PageSize.Width / _vm.DipWidth;
        double factorY = _vm.PageSize.Height / _vm.DipHeight;
        double pdfX = dipPoint.X * factorX;
        double pdfY = _vm.PageSize.Height - dipPoint.Y * factorY;
        return new PointD(pdfX, pdfY);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.TextLayer == null) return;
        var pos = e.GetPosition(PageGrid);
        var pdfPoint = DipToPdfPoint(pos);

        // Handle click count for word/line selection
        var now = DateTime.Now;
        if ((now - _lastClickTime).TotalMilliseconds < 400 && (pos - _lastClickPos).Length < 5)
            _clickCount++;
        else
            _clickCount = 1;
        _lastClickTime = now;
        _lastClickPos = pos;

        int index = _vm.TextLayer.HitTest(pdfPoint, 5);
        if (index < 0) return;

        if (_clickCount == 2)
        {
            // Double-click: select word
            var (start, count) = GetWordBounds(index);
            SetSelection(start, count);
        }
        else if (_clickCount == 3)
        {
            // Triple-click: select line
            var (start, count) = GetLineBounds(index);
            SetSelection(start, count);
        }
        else
        {
            _dragStart = pos;
            _isDragging = true;
            _selectionStart = index;
            SetSelection(index, 1);
            PageGrid.CaptureMouse();
        }
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm?.TextLayer == null) return;
        var pos = e.GetPosition(PageGrid);
        var pdfPoint = DipToPdfPoint(pos);

        // I-beam cursor over text
        int hit = _vm.TextLayer.HitTest(pdfPoint, 3);
        PageGrid.Cursor = hit >= 0 ? Cursors.IBeam : Cursors.Arrow;

        if (!_isDragging) return;
        if (_selectionStart < 0) return;

        int end = _vm.TextLayer.HitTest(pdfPoint, 5);
        if (end < 0) return;

        int start = Math.Min(_selectionStart, end);
        int count = Math.Abs(end - _selectionStart) + 1;
        SetSelection(start, count);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            PageGrid.ReleaseMouseCapture();
        }
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        // Context menu will open; ensure selection exists
    }

    private (int start, int count) GetWordBounds(int index)
    {
        if (_vm?.TextLayer == null) return (index, 1);
        var glyphs = _vm.TextLayer.Glyphs;
        if (index < 0 || index >= glyphs.Count) return (index, 1);

        int start = index;
        while (start > 0 && !char.IsWhiteSpace(glyphs[start - 1].Char) && glyphs[start - 1].Char != '\n')
            start--;
        int end = index;
        while (end < glyphs.Count - 1 && !char.IsWhiteSpace(glyphs[end + 1].Char) && glyphs[end + 1].Char != '\n')
            end++;
        return (start, end - start + 1);
    }

    private (int start, int count) GetLineBounds(int index)
    {
        if (_vm?.TextLayer == null) return (index, 1);
        var glyphs = _vm.TextLayer.Glyphs;
        int start = index;
        while (start > 0 && glyphs[start - 1].Char != '\n')
            start--;
        int end = index;
        while (end < glyphs.Count - 1 && glyphs[end].Char != '\n')
            end++;
        // Include newline? No
        return (start, end - start + 1);
    }

    private void SetSelection(int start, int count)
    {
        if (_vm?.TextLayer == null) return;
        var sel = new Core.Text.TextSelection(_vm.Index, start, count, _vm.TextLayer);
        _vm.Selection = sel;
        _vm.SelectionRects = sel.GetRects();
        UpdateLayoutFromVm();
    }

    private void CopySelection()
    {
        if (_vm?.Selection != null && !_vm.Selection.IsEmpty)
        {
            try { Clipboard.SetText(_vm.Selection.GetText()); } catch { }
        }
    }

    private void CopyPageAsImage()
    {
        if (_vm?.Bitmap != null)
        {
            try { Clipboard.SetImage(_vm.Bitmap); } catch { }
        }
    }

    private void HighlightSelection(Core.Annotations.AnnotationColor color)
    {
        if (_vm?.Selection == null || _vm.Selection.IsEmpty) return;
        // Raise event to MainViewModel to add annotation
        var args = new HighlightEventArgs(_vm.Index, _vm.Selection.GetRects(), color.R, color.G, color.B, _vm.Selection.GetText());
        OnHighlightRequested(args);
    }

    private void AddAnnotation(Core.Annotations.AnnotationType type)
    {
        if (_vm?.Selection == null || _vm.Selection.IsEmpty) return;
        var args = new AnnotationEventArgs(_vm.Index, _vm.Selection.GetRects(), type, _vm.Selection.GetText());
        OnAnnotationRequested(args);
    }

    private void AddStickyNote()
    {
        if (_vm?.Selection == null) return;
        var rect = _vm.Selection.GetRects().FirstOrDefault();
        var args = new StickyNoteEventArgs(_vm.Index, rect, _vm.Selection.GetText());
        OnStickyNoteRequested(args);
    }

    private void OnLinkMouseMove(object sender, MouseEventArgs e)
    {
        // Handled by individual borders
    }

    private void OnLinkClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.Tag is Core.PdfLink link)
        {
            if (!string.IsNullOrEmpty(link.Uri))
            {
                try
                {
                    var psi = new ProcessStartInfo(link.Uri) { UseShellExecute = true };
                    // Confirm
                    if (MessageBox.Show($"Open link?\n{link.Uri}", "LitePDF", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        Process.Start(psi);
                }
                catch { }
            }
            else if (link.DestPageIndex >= 0)
            {
                OnGoToPageRequested(link.DestPageIndex);
            }
        }
    }

    // Events for parent
    public event EventHandler<HighlightEventArgs>? HighlightRequested;
    public event EventHandler<AnnotationEventArgs>? AnnotationRequested;
    public event EventHandler<StickyNoteEventArgs>? StickyNoteRequested;
    public event EventHandler<int>? GoToPageRequested;
    public event EventHandler<int>? RunOcrRequested;
    public event EventHandler? OcrAllRequested;

    protected virtual void OnHighlightRequested(HighlightEventArgs e) => HighlightRequested?.Invoke(this, e);
    protected virtual void OnAnnotationRequested(AnnotationEventArgs e) => AnnotationRequested?.Invoke(this, e);
    protected virtual void OnStickyNoteRequested(StickyNoteEventArgs e) => StickyNoteRequested?.Invoke(this, e);
    protected virtual void OnGoToPageRequested(int page) => GoToPageRequested?.Invoke(this, page);

    private void RunOcr_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) RunOcrRequested?.Invoke(this, _vm.Index);
    }

    private void OcrAll_Click(object sender, RoutedEventArgs e) => OcrAllRequested?.Invoke(this, EventArgs.Empty);

    private void CloseBanner_Click(object sender, RoutedEventArgs e) => ScannedBanner.Visibility = Visibility.Collapsed;
}

public sealed class HighlightEventArgs : EventArgs
{
    public int PageIndex { get; }
    public IReadOnlyList<RectD> Rects { get; }
    public byte R, G, B;
    public string Text { get; }
    public HighlightEventArgs(int pageIndex, IReadOnlyList<RectD> rects, byte r, byte g, byte b, string text)
    { PageIndex = pageIndex; Rects = rects; R = r; G = g; B = b; Text = text; }
}

public sealed class AnnotationEventArgs : EventArgs
{
    public int PageIndex { get; }
    public IReadOnlyList<RectD> Rects { get; }
    public Core.Annotations.AnnotationType Type { get; }
    public string Text { get; }
    public AnnotationEventArgs(int pageIndex, IReadOnlyList<RectD> rects, Core.Annotations.AnnotationType type, string text)
    { PageIndex = pageIndex; Rects = rects; Type = type; Text = text; }
}

public sealed class StickyNoteEventArgs : EventArgs
{
    public int PageIndex { get; }
    public RectD Rect { get; }
    public string Text { get; }
    public StickyNoteEventArgs(int pageIndex, RectD rect, string text) { PageIndex = pageIndex; Rect = rect; Text = text; }
}
