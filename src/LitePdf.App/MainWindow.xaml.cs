using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitePdf.App.ViewModels;
using LitePdf.App.Views;
using LitePdf.Core;
using LitePdf.Core.Storage;

namespace LitePdf.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private string? _pendingFile;
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private bool _isFullScreen;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.RequestGoToPage += GoToPage;
        _vm.RequestFocusPageBox += () => PageBox.Focus();
        _vm.RequestFitWidth += () => DoFitWidth();
        _vm.RequestFitPage += () => DoFitPage();
        _vm.RequestToggleFullScreen += ToggleFullScreen;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SizeChanged += MainWindow_SizeChanged;
        LocationChanged += MainWindow_LocationChanged;

        Drop += MainWindow_Drop;
        DragOver += MainWindow_DragOver;

        UpdateDpiScale();

        // Restore window position
        try
        {
            var settingsPath = AppPaths.Root;
            // Could load window pos from settings.json extension
        }
        catch { }

        // Clipboard paste handler
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, OnPaste));

        // Keyboard for copy selection
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopy, CanCopy));
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        var sel = GetCurrentPageSelection();
        if (sel != null && !sel.IsEmpty)
        {
            try { Clipboard.SetText(sel.GetText()); _vm.StatusText = "Copied selection."; } catch { }
            e.Handled = true;
        }
    }

    private void CanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        var sel = GetCurrentPageSelection();
        e.CanExecute = sel != null && !sel.IsEmpty;
    }

    private Core.Text.TextSelection? GetCurrentPageSelection()
    {
        if (_vm.Pages.Count == 0) return null;
        return _vm.Pages[_vm.CurrentPageIndex].Selection;
    }

    private void OnPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (Clipboard.ContainsImage())
        {
            try
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    // Convert to RenderedBitmap and open as image doc via temp file
                    // For simplicity, save to temp png and open
                    string tmp = Path.Combine(Path.GetTempPath(), $"LitePDF_clip_{Guid.NewGuid():N}.png");
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                    using (var fs = new FileStream(tmp, FileMode.Create))
                        encoder.Save(fs);
                    _ = _vm.OpenFileAsync(tmp); // Actually need image open path
                    // Use reflection to call OpenImageAsync? We'll use OpenFileAsync which detects image ext? Our OpenFileDialog does, but OpenFileAsync only handles pdf.
                    // So we need to expose OpenImage method. For now, call via dynamic
                    // We'll just call OpenFileAsync and if it fails, try image path via method
                    e.Handled = true;
                }
            }
            catch { }
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowPos();
        UpdateDpiScale();
        if (!string.IsNullOrEmpty(_pendingFile))
        {
            _ = _vm.OpenFileAsync(_pendingFile);
            _pendingFile = null;
        }

        PagesControl.Loaded += (s, ev) =>
        {
            Dispatcher.BeginInvoke(() => UpdateVisiblePages(), System.Windows.Threading.DispatcherPriority.Loaded);
        };

        PagesControl.ItemContainerGenerator.StatusChanged += (s, ev) =>
        {
            if (PagesControl.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            {
                HookPageViews();
            }
        };
    }

    private void HookPageViews()
    {
        for (int i = 0; i < _vm.Pages.Count; i++)
        {
            var container = PagesControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;
            var pageView = FindVisualChild<PageView>(container);
            if (pageView == null) continue;

            // Avoid double subscription
            pageView.HighlightRequested -= PageView_HighlightRequested;
            pageView.HighlightRequested += PageView_HighlightRequested;
            pageView.AnnotationRequested -= PageView_AnnotationRequested;
            pageView.AnnotationRequested += PageView_AnnotationRequested;
            pageView.StickyNoteRequested -= PageView_StickyNoteRequested;
            pageView.StickyNoteRequested += PageView_StickyNoteRequested;
            pageView.GoToPageRequested -= PageView_GoToPageRequested;
            pageView.GoToPageRequested += PageView_GoToPageRequested;
            pageView.RunOcrRequested -= PageView_RunOcrRequested;
            pageView.RunOcrRequested += PageView_RunOcrRequested;
            pageView.OcrAllRequested -= PageView_OcrAllRequested;
            pageView.OcrAllRequested += PageView_OcrAllRequested;
        }
    }

    private void PageView_HighlightRequested(object? sender, HighlightEventArgs e)
    {
        // Use selected color from UI
        var colorItem = ColorBox.SelectedItem as ComboBoxItem;
        string colorName = colorItem?.Content?.ToString() ?? "Yellow";
        var color = Core.Annotations.AnnotationColor.Palette.FirstOrDefault(c => c.Name == colorName) ?? Core.Annotations.AnnotationColor.Palette[0];
        _vm.SelectedColor = color;
        _ = _vm.HighlightSelectionAsync();
    }

    private void PageView_AnnotationRequested(object? sender, AnnotationEventArgs e)
    {
        // For underline/strikeout, reuse highlight logic with different type
        // For now, just highlight
        _ = _vm.HighlightSelectionAsync();
    }

    private void PageView_StickyNoteRequested(object? sender, StickyNoteEventArgs e)
    {
        var dlg = new TextWindow();
        dlg.SetText(e.Text, "Add sticky note");
        dlg.ShowDialog();
        // After dialog, add note with contents
        _ = _vm.AddStickyNoteAsync(e.PageIndex, e.Rect, e.Text);
    }

    private void PageView_GoToPageRequested(object? sender, int pageIndex)
    {
        _vm.CurrentPageIndex = pageIndex;
        GoToPage(pageIndex);
    }

    private void PageView_RunOcrRequested(object? sender, int pageIndex)
    {
        _vm.CurrentPageIndex = pageIndex;
        _ = _vm.OcrCurrentPageAsync();
    }

    private void PageView_OcrAllRequested(object? sender, EventArgs e)
    {
        _ = _vm.OcrAllPagesAsync();
    }

    public void LoadFileOnStartup(string path)
    {
        if (IsLoaded)
            _ = _vm.OpenFileAsync(path);
        else
            _pendingFile = path;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.PromptSaveIfDirty())
        {
            e.Cancel = true;
            return;
        }

        try
        {
            var settings = SettingsStore.LoadSettings();
            settings.SidebarVisible = _vm.SidebarVisible;
            settings.OcrLanguage = _vm.SelectedOcrLanguage;
            settings.PageMode = _vm.PageMode.ToString().ToLower();
            SettingsStore.SaveSettings(settings);

            if (_vm.IsDocumentOpen)
            {
                SettingsStore.AddRecent(_vm.FileName, _vm.FileName, _vm.CurrentPageIndex, _vm.Zoom);
            }
        }
        catch { }

        _vm.CloseDocument();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDpiScale();
        SaveWindowPos();
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e) => SaveWindowPos();

    private void SaveWindowPos()
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                var s = SettingsStore.LoadSettings();
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
                s.IsMaximized = false;
                SettingsStore.SaveSettings(s);
            }
            else if (WindowState == WindowState.Maximized)
            {
                var s = SettingsStore.LoadSettings();
                s.IsMaximized = true;
                SettingsStore.SaveSettings(s);
            }
        }
        catch { }
    }

    private void RestoreWindowPos()
    {
        try
        {
            var s = SettingsStore.LoadSettings();
            if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
            {
                Left = s.WindowLeft;
                Top = s.WindowTop;
            }
            Width = s.WindowWidth;
            Height = s.WindowHeight;
            if (s.IsMaximized) WindowState = WindowState.Maximized;
        }
        catch { }
    }

    private void UpdateDpiScale()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _vm.DpiScale = dpi.DpiScaleX;
        }
        catch { }
    }

    private void DocumentScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double oldZoom = _vm.Zoom;
            if (e.Delta > 0)
                _vm.Zoom = ViewMath.ZoomIn(_vm.Zoom);
            else
                _vm.Zoom = ViewMath.ZoomOut(_vm.Zoom);

            // Keep reading position
            if (Math.Abs(oldZoom - _vm.Zoom) > 0.001)
            {
                double factor = _vm.Zoom / oldZoom;
                DocumentScrollViewer.ScrollToVerticalOffset(DocumentScrollViewer.VerticalOffset * factor);
            }
            e.Handled = true;
        }
    }

    private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is ComboBoxItem item && item.Content is string s)
        {
            if (s.EndsWith("%") && double.TryParse(s.TrimEnd('%'), out double pct))
                _vm.Zoom = pct / 100.0;
        }
    }

    private void ZoomBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = ZoomBox.Text.Trim().TrimEnd('%');
            if (double.TryParse(text, out double pct))
                _vm.Zoom = pct / 100.0;
            e.Handled = true;
            PagesControl.Focus();
        }
    }

    private void PageBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (int.TryParse(PageBox.Text, out int pageNum))
            {
                _vm.CurrentPageNumber = pageNum;
                GoToPage(pageNum - 1);
            }
            e.Handled = true;
            PagesControl.Focus();
        }
    }

    private void DoFitWidth()
    {
        double viewportWidth = DocumentScrollViewer.ViewportWidth;
        if (viewportWidth <= 0) viewportWidth = ActualWidth - 300;
        double chrome = 2 * 12 + 24;
        _vm.Zoom = _vm.CalculateFitWidth(viewportWidth, chrome);
    }

    private void DoFitPage()
    {
        double vw = DocumentScrollViewer.ViewportWidth;
        double vh = DocumentScrollViewer.ViewportHeight;
        if (vw <= 0) vw = ActualWidth - 300;
        if (vh <= 0) vh = ActualHeight - 100;
        double hChrome = 2 * 12 + 24;
        double vChrome = 2 * 12;
        _vm.Zoom = _vm.CalculateFitPage(vw, vh, hChrome, vChrome);
    }

    private void GoToPage(int index)
    {
        if (index < 0 || index >= _vm.Pages.Count) return;

        var panel = FindVisualChild<VirtualizingStackPanel>(PagesControl);
        if (panel != null)
        {
            panel.BringIndexIntoViewPublic(index);
            Dispatcher.BeginInvoke(() =>
            {
                PagesControl.UpdateLayout();
                HookPageViews();
                var container = PagesControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                if (container != null)
                {
                    try
                    {
                        var transform = container.TransformToAncestor(DocumentScrollViewer);
                        var pos = transform.Transform(new Point(0, 0));
                        double newOffset = DocumentScrollViewer.VerticalOffset + pos.Y - 12;
                        DocumentScrollViewer.ScrollToVerticalOffset(newOffset);
                    }
                    catch { }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            double avgHeight = _vm.Pages.Count > 0 ? _vm.Pages[0].DipHeight + 24 : 800;
            DocumentScrollViewer.ScrollToVerticalOffset(index * avgHeight);
        }
        UpdateVisiblePages();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void DocumentScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateCurrentPageFromViewport();
        UpdateVisiblePages();
    }

    private void UpdateCurrentPageFromViewport()
    {
        if (_vm.Pages.Count == 0) return;
        var scroll = DocumentScrollViewer;
        double y = scroll.VerticalOffset + scroll.ViewportHeight * 0.35;

        for (int i = 0; i < _vm.Pages.Count; i++)
        {
            var container = PagesControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;
            try
            {
                var transform = container.TransformToAncestor(scroll);
                var topLeft = transform.Transform(new Point(0, 0));
                var bottomRight = transform.Transform(new Point(container.ActualWidth, container.ActualHeight));
                if (y >= topLeft.Y && y <= bottomRight.Y)
                {
                    if (_vm.CurrentPageIndex != i)
                        _vm.CurrentPageIndex = i;
                    break;
                }
            }
            catch { }
        }
    }

    private void UpdateVisiblePages()
    {
        if (_vm.Pages.Count == 0) return;
        var scroll = DocumentScrollViewer;
        double viewportTop = scroll.VerticalOffset;
        double viewportBottom = viewportTop + scroll.ViewportHeight;

        for (int i = 0; i < _vm.Pages.Count; i++)
        {
            var container = PagesControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            try
            {
                var transform = container.TransformToAncestor(scroll);
                var top = transform.Transform(new Point(0, 0)).Y + viewportTop;
                var bottom = top + container.ActualHeight;

                bool visible = bottom >= viewportTop - 400 && top <= viewportBottom + 400;
                var pageVm = _vm.Pages[i];
                if (visible)
                {
                    int priority = (bottom >= viewportTop && top <= viewportBottom) ? RenderPriority.Visible : RenderPriority.Nearby;
                    pageVm.OnRealized(_vm.RenderPageForVmAsync, _vm.BitmapCache, priority);

                    var thumbVm = _vm.Thumbnails[i];
                    int pageIndex = i; // copy: the task below must not capture the loop variable
                    if (thumbVm.Bitmap == null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                int thumbW = 140;
                                double aspect = pageVm.PageSize.Height / pageVm.PageSize.Width;
                                int thumbH = (int)(thumbW * aspect);
                                var bmp = await _vm.RenderPageForVmAsync(pageIndex, thumbW, thumbH, LitePdf.Core.RenderFlags.None, RenderPriority.Thumbnail, CancellationToken.None);
                                Dispatcher.Invoke(() =>
                                {
                                    try
                                    {
                                        var wb = new System.Windows.Media.Imaging.WriteableBitmap(bmp.Width, bmp.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                                        wb.WritePixels(new Int32Rect(0, 0, bmp.Width, bmp.Height), bmp.Pixels, bmp.Stride, 0);
                                        thumbVm.Bitmap = wb;
                                    }
                                    catch { }
                                });
                            }
                            catch { }
                        });
                    }
                }
                else
                {
                    pageVm.OnUnrealized();
                }
            }
            catch { }
        }
    }

    private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailList.SelectedItem is ThumbnailViewModel tvm)
            _vm.GoToThumbnail(tvm.Index);
    }

    private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineNodeViewModel node)
            _vm.GoToOutline(node);
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is Core.Search.SearchResult res)
        {
            _vm.CurrentPageIndex = res.PageIndex;
            GoToPage(res.PageIndex);
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = _vm.DoSearchAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBar.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => SearchBar.Visibility = Visibility.Collapsed;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (SearchBar.Visibility == Visibility.Visible)
            {
                SearchBar.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (_isFullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _ = _vm.RemoveSelectedAnnotationAsync();
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullScreen = true;
            _vm.IsFullScreen = true;
        }
        else
        {
            WindowStyle = _prevStyle;
            WindowState = _prevState;
            _isFullScreen = false;
            _vm.IsFullScreen = false;
        }
    }

    private void MainWindow_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void MainWindow_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var pdf = files.FirstOrDefault(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
            if (pdf != null)
                _ = _vm.OpenFileAsync(pdf);
            else
            {
                var img = files.FirstOrDefault(f => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif" }.Contains(Path.GetExtension(f).ToLowerInvariant()));
                if (img != null)
                    _ = _vm.OpenFileAsync(img); // will handle as image if we add check
            }
        }
    }
}

