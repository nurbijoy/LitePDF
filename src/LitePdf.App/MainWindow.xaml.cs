using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitePdf.App.ViewModels;
using LitePdf.Core;
using LitePdf.Core.Storage;

namespace LitePdf.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private string? _pendingFile;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.RequestGoToPage += GoToPage;
        _vm.RequestFocusPageBox += () => PageBox.Focus();
        _vm.RequestFitWidth += () => DoFitWidth();
        _vm.RequestFitPage += () => DoFitPage();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SizeChanged += MainWindow_SizeChanged;

        // Drag drop
        Drop += MainWindow_Drop;
        DragOver += MainWindow_DragOver;

        // DPI
        UpdateDpiScale();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateDpiScale();
        if (!string.IsNullOrEmpty(_pendingFile))
        {
            _ = _vm.OpenFileAsync(_pendingFile);
            _pendingFile = null;
        }

        // Hook container events for virtualization
        if (PagesControl.ItemContainerGenerator != null)
        {
            // Use status changed to attach
        }

        // Subscribe to scroll viewer scroll changed already via XAML
        // Attach realized events
        PagesControl.Loaded += (s, ev) =>
        {
            var scroll = DocumentScrollViewer;
            if (scroll != null)
            {
                // Force initial render
                Dispatcher.BeginInvoke(() => UpdateVisiblePages(), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
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
        // Save settings
        try
        {
            var settings = SettingsStore.LoadSettings();
            settings.SidebarVisible = _vm.SidebarVisible;
            settings.OcrLanguage = _vm.SelectedOcrLanguage;
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

    // Zoom handling
    private void DocumentScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Delta > 0)
                _vm.Zoom = ViewMath.ZoomIn(_vm.Zoom);
            else
                _vm.Zoom = ViewMath.ZoomOut(_vm.Zoom);

            // Keep reading position
            // Scale offset
            // We will handle in scroll changed? For simplicity, scale vertical offset proportionally
            // Already done via Zoom property, but we need to preserve offset
            e.Handled = true;
        }
    }

    private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is ComboBoxItem item && item.Content is string s)
        {
            if (s.EndsWith("%") && double.TryParse(s.TrimEnd('%'), out double pct))
            {
                _vm.Zoom = pct / 100.0;
            }
        }
    }

    private void ZoomBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = ZoomBox.Text.Trim().TrimEnd('%');
            if (double.TryParse(text, out double pct))
            {
                _vm.Zoom = pct / 100.0;
            }
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
        double chrome = 2 * 12 + 24; // PageMargin *2 + scrollbar approx
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

        // Use BringIndexIntoView
        var panel = FindVisualChild<VirtualizingStackPanel>(PagesControl);
        if (panel != null)
        {
            panel.BringIndexIntoViewPublic(index);
            Dispatcher.BeginInvoke(() =>
            {
                PagesControl.UpdateLayout();
                var container = PagesControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                if (container != null)
                {
                    try
                    {
                        var transform = container.TransformToAncestor(DocumentScrollViewer);
                        var pos = transform.Transform(new Point(0, 0));
                        // Align top
                        double newOffset = DocumentScrollViewer.VerticalOffset + pos.Y - 12;
                        DocumentScrollViewer.ScrollToVerticalOffset(newOffset);
                    }
                    catch { }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            // Fallback: scroll to approximate position
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
        // Hit-test at center X, 35% height
        var scroll = DocumentScrollViewer;
        double centerX = scroll.ViewportWidth / 2;
        double y = scroll.VerticalOffset + scroll.ViewportHeight * 0.35;

        // Find container at that position
        var panel = FindVisualChild<VirtualizingStackPanel>(PagesControl);
        if (panel == null) return;

        // Iterate over realized containers
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
                    {
                        _vm.CurrentPageIndex = i;
                    }
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

                bool visible = bottom >= viewportTop - 200 && top <= viewportBottom + 200;
                var pageVm = _vm.Pages[i];
                if (visible)
                {
                    int priority = (bottom >= viewportTop && top <= viewportBottom) ? RenderPriority.Visible : RenderPriority.Nearby;
                    pageVm.OnRealized(_vm.RenderPageForVmAsync, _vm.BitmapCache, priority);

                    // Thumbnail render at low priority
                    var thumbVm = _vm.Thumbnails[i];
                    if (thumbVm.Bitmap == null)
                    {
                        // Render thumbnail at 140 DIP wide
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Target width 140 DIP at 1.5 dpi? Use 200px
                                int thumbW = 140;
                                double aspect = pageVm.PageSize.Height / pageVm.PageSize.Width;
                                int thumbH = (int)(thumbW * aspect);
                                var bmp = await _vm.RenderPageForVmAsync(i, thumbW, thumbH, LitePdf.Core.RenderFlags.None, RenderPriority.Thumbnail, CancellationToken.None);
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
        {
            _vm.GoToThumbnail(tvm.Index);
        }
    }

    private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineNodeViewModel node)
        {
            _vm.GoToOutline(node);
        }
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

    private void CloseSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBar.Visibility = Visibility.Collapsed;
    }

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
            {
                _ = _vm.OpenFileAsync(pdf);
            }
        }
    }
}

// Extension to access protected BringIndexIntoView
public static class VirtualizingPanelExtensions
{
    public static void BringIndexIntoViewPublic(this VirtualizingStackPanel panel, int index)
    {
        panel.BringIndexIntoView(index);
    }
}
