using System.Collections.ObjectModel;
using System.Windows;
using LitePdf.App.Services;
using LitePdf.Core;
using LitePdf.Core.Search;
using LitePdf.Core.Storage;
using LitePdf.Core.Text;
using LitePdf.Ocr;
using LitePdf.Pdfium;

namespace LitePdf.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    // Document
    private IPdfDocument? _document;
    private PdfiumDocument? _pdfiumDoc;
    private string _filePath = string.Empty;
    private string _docKey = string.Empty;
    private OcrCache? _ocrCache;

    // Pages
    public ObservableCollection<PageViewModel> Pages { get; } = new();
    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = new();
    public ObservableCollection<OutlineNodeViewModel> Outline { get; } = new();

    // UI state
    private double _zoom = 1.0;
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (SetProperty(ref _zoom, ViewMath.ClampZoom(value)))
            {
                OnPropertyChanged(nameof(ZoomPercent));
                UpdateAllPageZooms();
                RaiseGoToPageCanExecute();
            }
        }
    }

    public string ZoomPercent => $"{Zoom * 100:0}%";

    private int _currentPageIndex;
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            if (SetProperty(ref _currentPageIndex, Math.Clamp(value, 0, Math.Max(0, Pages.Count - 1))))
            {
                OnPropertyChanged(nameof(CurrentPageNumber));
                UpdateCurrentChapter();
                UpdateThumbnailSelection();
            }
        }
    }

    public int CurrentPageNumber
    {
        get => _currentPageIndex + 1;
        set
        {
            int idx = value - 1;
            if (idx < 0) idx = 0;
            if (idx >= Pages.Count) idx = Pages.Count - 1;
            CurrentPageIndex = idx;
            RequestGoToPage?.Invoke(idx);
        }
    }

    private int _pageCount;
    public int PageCount
    {
        get => _pageCount;
        private set => SetProperty(ref _pageCount, value);
    }

    private bool _sidebarVisible = true;
    public bool SidebarVisible
    {
        get => _sidebarVisible;
        set => SetProperty(ref _sidebarVisible, value);
    }

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private string _fileName = "No document";
    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    private bool _isDocumentOpen;
    public bool IsDocumentOpen
    {
        get => _isDocumentOpen;
        set => SetProperty(ref _isDocumentOpen, value);
    }

    private double _dpiScale = 1.0;
    public double DpiScale
    {
        get => _dpiScale;
        set
        {
            if (SetProperty(ref _dpiScale, value))
                UpdateAllPageZooms();
        }
    }

    // Search
    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    private int _currentSearchIndex = -1;
    public int CurrentSearchIndex
    {
        get => _currentSearchIndex;
        set => SetProperty(ref _currentSearchIndex, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    // Text layers cache
    private readonly Dictionary<int, PageTextLayer> _textLayers = new();
    private readonly SearchService _searchService = new();

    // Bitmap cache
    public BitmapCache BitmapCache { get; } = new();

    // OCR
    private readonly WindowsOcrEngine _ocrEngine = new();
    public bool IsOcrAvailable => _ocrEngine.IsAvailable;
    public IReadOnlyList<string> OcrLanguages => _ocrEngine.AvailableLanguages;
    private string? _selectedOcrLanguage;
    public string? SelectedOcrLanguage
    {
        get => _selectedOcrLanguage;
        set => SetProperty(ref _selectedOcrLanguage, value);
    }

    // Annotations
    public ObservableCollection<Core.Annotations.AnnotationModel> Annotations { get; } = new();

    // Events for view
    public event Action<int>? RequestGoToPage;
    public event Action? RequestFocusPageBox;
    public event Action? RequestFitWidth;
    public event Action? RequestFitPage;

    // Commands
    public RelayCommand OpenCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomResetCommand { get; }
    public RelayCommand FitWidthCommand { get; }
    public RelayCommand FitPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand FirstPageCommand { get; }
    public RelayCommand LastPageCommand { get; }
    public RelayCommand GoToPageCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand CopyPageTextCommand { get; }
    public RelayCommand OcrPageCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand SearchNextCommand { get; }
    public RelayCommand SearchPrevCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    public MainViewModel()
    {
        OpenCommand = new RelayCommand(async _ => await OpenFileDialogAsync());
        ZoomInCommand = new RelayCommand(_ => Zoom = ViewMath.ZoomIn(Zoom));
        ZoomOutCommand = new RelayCommand(_ => Zoom = ViewMath.ZoomOut(Zoom));
        ZoomResetCommand = new RelayCommand(_ => Zoom = 1.0);
        FitWidthCommand = new RelayCommand(_ => RequestFitWidth?.Invoke());
        FitPageCommand = new RelayCommand(_ => RequestFitPage?.Invoke());
        PrevPageCommand = new RelayCommand(_ => { if (CurrentPageIndex > 0) { CurrentPageIndex--; RequestGoToPage?.Invoke(CurrentPageIndex); } }, _ => IsDocumentOpen && CurrentPageIndex > 0);
        NextPageCommand = new RelayCommand(_ => { if (CurrentPageIndex < PageCount - 1) { CurrentPageIndex++; RequestGoToPage?.Invoke(CurrentPageIndex); } }, _ => IsDocumentOpen && CurrentPageIndex < PageCount - 1);
        FirstPageCommand = new RelayCommand(_ => { CurrentPageIndex = 0; RequestGoToPage?.Invoke(0); }, _ => IsDocumentOpen);
        LastPageCommand = new RelayCommand(_ => { CurrentPageIndex = PageCount - 1; RequestGoToPage?.Invoke(PageCount - 1); }, _ => IsDocumentOpen);
        GoToPageCommand = new RelayCommand(_ => RequestFocusPageBox?.Invoke());
        ToggleSidebarCommand = new RelayCommand(_ => SidebarVisible = !SidebarVisible);
        CopyPageTextCommand = new RelayCommand(async _ => await CopyCurrentPageTextAsync(), _ => IsDocumentOpen);
        OcrPageCommand = new RelayCommand(async _ => await OcrCurrentPageAsync(), _ => IsDocumentOpen && IsOcrAvailable);
        SearchCommand = new RelayCommand(async _ => await DoSearchAsync());
        SearchNextCommand = new RelayCommand(_ => MoveSearch(1), _ => SearchResults.Count > 0);
        SearchPrevCommand = new RelayCommand(_ => MoveSearch(-1), _ => SearchResults.Count > 0);
        ClearSearchCommand = new RelayCommand(_ => ClearSearch());

        var settings = SettingsStore.LoadSettings();
        SelectedOcrLanguage = settings.OcrLanguage ?? OcrLanguages.FirstOrDefault();
        SidebarVisible = settings.SidebarVisible;
    }

    private void RaiseGoToPageCanExecute()
    {
        PrevPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        FirstPageCommand.RaiseCanExecuteChanged();
        LastPageCommand.RaiseCanExecuteChanged();
    }

    private void UpdateAllPageZooms()
    {
        foreach (var p in Pages)
            p.UpdateZoom(Zoom, DpiScale);
        OnPropertyChanged(nameof(ZoomPercent));
    }

    private void UpdateThumbnailSelection()
    {
        foreach (var t in Thumbnails)
            t.IsSelected = t.Index == CurrentPageIndex;
    }

    private OutlineNodeViewModel? _currentChapter;
    public OutlineNodeViewModel? CurrentChapter
    {
        get => _currentChapter;
        set => SetProperty(ref _currentChapter, value);
    }

    private void UpdateCurrentChapter()
    {
        if (Outline.Count == 0)
        {
            CurrentChapter = null;
            return;
        }
        // Find last item with PageIndex <= current page
        OutlineNodeViewModel? best = null;
        int bestPage = -1;
        foreach (var node in Outline.SelectMany(n => n.Flatten()))
        {
            if (node.PageIndex >= 0 && node.PageIndex <= CurrentPageIndex && node.PageIndex > bestPage)
            {
                bestPage = node.PageIndex;
                best = node;
            }
        }
        if (best != null)
        {
            if (_currentChapter != null) _currentChapter.IsSelected = false;
            best.IsSelected = true;
            CurrentChapter = best;
        }
    }

    public async Task OpenFileAsync(string path, string? password = null)
    {
        if (!File.Exists(path))
        {
            StatusText = "File not found.";
            return;
        }

        CloseDocument();

        StatusText = $"Opening {System.IO.Path.GetFileName(path)}...";
        try
        {
            PdfiumDocument doc = await PdfiumDocument.OpenAsync(path, password);
            _pdfiumDoc = doc;
            _document = doc;
            _filePath = path;
            FileName = System.IO.Path.GetFileName(path);
            PageCount = doc.PageCount;
            IsDocumentOpen = true;

            // Doc key for caches
            try
            {
                var id = await doc.GetFileIdentifierAsync();
                _docKey = OcrCache.ComputeDocKey(path, id);
            }
            catch
            {
                _docKey = OcrCache.ComputeDocKey(path, null);
            }
            _ocrCache = new OcrCache(_docKey);

            // Build page view models
            Pages.Clear();
            Thumbnails.Clear();
            for (int i = 0; i < doc.PageCount; i++)
            {
                var size = doc.PageSizes[i];
                var pvm = new PageViewModel(i, size);
                pvm.UpdateZoom(Zoom, DpiScale);
                Pages.Add(pvm);
                Thumbnails.Add(new ThumbnailViewModel(i, size));
            }

            CurrentPageIndex = 0;
            StatusText = $"{FileName} - {PageCount} pages";

            // Load outline async
            _ = Task.Run(async () =>
            {
                try
                {
                    var outline = await doc.GetOutlineAsync();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Outline.Clear();
                        foreach (var item in outline)
                            Outline.Add(new OutlineNodeViewModel(item));
                        UpdateCurrentChapter();
                    });
                }
                catch { }
            });

            // Preload text layers for search (background)
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < doc.PageCount; i++)
                {
                    try
                    {
                        // Check OCR cache first
                        if (_ocrCache != null && _ocrCache.TryGet(i, out var cachedOcr) && cachedOcr != null)
                        {
                            var layer = PageTextLayer.FromOcr(i, cachedOcr, doc.PageSizes[i], 1000, 1000); // approx, will need actual bitmap size
                            // For cached, we don't have bitmap size, use page size mapping
                            // We'll store directly
                            lock (_textLayers)
                            {
                                _textLayers[i] = layer;
                            }
                            _searchService.SetLayer(layer);
                            Pages[i].TextLayer = layer;
                            continue;
                        }

                        var tl = await doc.GetTextLayerAsync(i);
                        if (tl != null)
                        {
                            lock (_textLayers) _textLayers[i] = tl;
                            _searchService.SetLayer(tl);
                            Application.Current.Dispatcher.Invoke(() => Pages[i].TextLayer = tl);
                        }
                    }
                    catch { }
                    await Task.Delay(10);
                }
            });

            // Recent
            SettingsStore.AddRecent(path, _docKey, 0, Zoom);

            RaiseGoToPageCanExecute();
        }
        catch (PdfiumException ex) when (ex.ErrorCode == 4)
        {
            // Password required
            StatusText = "Password required.";
            // Prompt via dialog handled in MainWindow
            throw;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open: {ex.Message}";
            IsDocumentOpen = false;
        }
    }

    public async Task OpenFileDialogAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            Title = "Open PDF"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                await OpenFileAsync(dlg.FileName);
            }
            catch (PdfiumException ex) when (ex.ErrorCode == 4)
            {
                // Ask password 3 times
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var pwdDlg = new Views.PasswordDialog();
                    pwdDlg.Owner = Application.Current.MainWindow;
                    if (pwdDlg.ShowDialog() != true) break;
                    try
                    {
                        await OpenFileAsync(dlg.FileName, pwdDlg.Password);
                        return;
                    }
                    catch (PdfiumException ex2) when (ex2.ErrorCode == 4)
                    {
                        if (attempt == 2)
                            MessageBox.Show("Incorrect password.", "LitePDF", MessageBoxButton.OK, MessageBoxImage.Error);
                        continue;
                    }
                }
            }
        }
    }

    public void CloseDocument()
    {
        if (_document != null)
        {
            var doc = _document;
            _document = null;
            _pdfiumDoc = null;
            Task.Run(async () => await doc.DisposeAsync());
        }
        Pages.Clear();
        Thumbnails.Clear();
        Outline.Clear();
        SearchResults.Clear();
        _textLayers.Clear();
        _searchService.Clear();
        BitmapCache.Clear();
        PageCount = 0;
        IsDocumentOpen = false;
        FileName = "No document";
        StatusText = "Ready";
        RaiseGoToPageCanExecute();
    }

    public async Task<RenderedBitmap> RenderPageForVmAsync(int pageIndex, int pixelWidth, int pixelHeight, RenderFlags flags, int priority, CancellationToken ct)
    {
        if (_document == null) throw new InvalidOperationException("No document");
        return await _document.RenderPageAsync(pageIndex, pixelWidth, pixelHeight, flags, priority, ct);
    }

    private async Task CopyCurrentPageTextAsync()
    {
        if (_document == null) return;
        try
        {
            var text = await _document.GetPageTextAsync(CurrentPageIndex);
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
                StatusText = $"Copied page {CurrentPageNumber} text.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed: {ex.Message}";
        }
    }

    private async Task OcrCurrentPageAsync()
    {
        if (_document == null || _pdfiumDoc == null) return;
        if (!IsOcrAvailable) return;

        StatusText = $"OCR page {CurrentPageNumber}...";
        try
        {
            // Render at 300 DPI: scale = 300/72 = 4.1667
            var pageSize = _pdfiumDoc.PageSizes[CurrentPageIndex];
            int targetW = (int)(pageSize.Width * 300.0 / 72.0);
            int targetH = (int)(pageSize.Height * 300.0 / 72.0);
            var clamped = ViewMath.ClampToMax(targetW, targetH, _ocrEngine.MaxImageDimension);
            targetW = clamped.Width;
            targetH = clamped.Height;

            var rendered = await _document.RenderPageAsync(CurrentPageIndex, targetW, targetH, RenderFlags.Annotations, RenderPriority.Background);
            var ocrResult = await _ocrEngine.RecognizeAsync(rendered, SelectedOcrLanguage);

            // Save to cache
            _ocrCache?.Set(CurrentPageIndex, ocrResult, targetW, targetH);

            // Build text layer
            var layer = PageTextLayer.FromOcr(CurrentPageIndex, ocrResult, pageSize, targetW, targetH);
            _textLayers[CurrentPageIndex] = layer;
            _searchService.SetLayer(layer);
            Pages[CurrentPageIndex].TextLayer = layer;

            // Show text window
            var wnd = new Views.TextWindow();
            wnd.SetText(ocrResult.Text, $"OCR - Page {CurrentPageNumber}");
            wnd.Show();

            StatusText = $"OCR done: {ocrResult.Lines.Count} lines.";
        }
        catch (Exception ex)
        {
            StatusText = $"OCR failed: {ex.Message}";
        }
    }

    public async Task DoSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery) || _document == null) return;
        IsSearching = true;
        SearchResults.Clear();
        CurrentSearchIndex = -1;
        try
        {
            // Ensure text layers are loaded for all pages? Search over existing layers, but also need to ensure layers loaded
            // For pages not yet loaded, load synchronously in background
            var results = await _searchService.SearchAsync(SearchQuery);
            foreach (var r in results)
                SearchResults.Add(r);
            StatusText = $"Found {results.Count} results for '{SearchQuery}'.";
            if (results.Count > 0)
            {
                CurrentSearchIndex = 0;
                RequestGoToPage?.Invoke(results[0].PageIndex);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
            SearchNextCommand.RaiseCanExecuteChanged();
            SearchPrevCommand.RaiseCanExecuteChanged();
        }
    }

    private void MoveSearch(int delta)
    {
        if (SearchResults.Count == 0) return;
        int newIdx = CurrentSearchIndex + delta;
        if (newIdx < 0) newIdx = SearchResults.Count - 1;
        if (newIdx >= SearchResults.Count) newIdx = 0;
        CurrentSearchIndex = newIdx;
        var res = SearchResults[newIdx];
        CurrentPageIndex = res.PageIndex;
        RequestGoToPage?.Invoke(res.PageIndex);
    }

    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        SearchResults.Clear();
        CurrentSearchIndex = -1;
        StatusText = "Search cleared.";
    }

    public void GoToOutline(OutlineNodeViewModel node)
    {
        if (node.PageIndex >= 0)
        {
            CurrentPageIndex = node.PageIndex;
            RequestGoToPage?.Invoke(node.PageIndex);
        }
    }

    public void GoToThumbnail(int index)
    {
        CurrentPageIndex = index;
        RequestGoToPage?.Invoke(index);
    }

    public double CalculateFitWidth(double viewportWidthDip, double chromeDip)
    {
        if (Pages.Count == 0) return Zoom;
        double maxWidth = Pages.Max(p => p.PageSize.Width);
        return ViewMath.FitWidth(viewportWidthDip, maxWidth, chromeDip);
    }

    public double CalculateFitPage(double viewportWidthDip, double viewportHeightDip, double hChrome, double vChrome)
    {
        if (Pages.Count == 0) return Zoom;
        var page = Pages[CurrentPageIndex].PageSize;
        return ViewMath.FitPage(viewportWidthDip, viewportHeightDip, page, hChrome, vChrome);
    }

    // Comfort features (stubs for T-62..T-69)

    private bool _isFullScreen;
    public bool IsFullScreen
    {
        get => _isFullScreen;
        set => SetProperty(ref _isFullScreen, value);
    }

    private int _rotation = 0; // 0,90,180,270
    public int Rotation
    {
        get => _rotation;
        set => SetProperty(ref _rotation, value % 360);
    }

    private bool _twoPageView;
    public bool TwoPageView
    {
        get => _twoPageView;
        set => SetProperty(ref _twoPageView, value);
    }

    private PageMode _pageMode = PageMode.Normal;
    public PageMode PageMode
    {
        get => _pageMode;
        set => SetProperty(ref _pageMode, value);
    }

    public enum PageMode { Normal, Dark, Sepia }

    public void ToggleFullScreen()
    {
        IsFullScreen = !IsFullScreen;
    }

    public void RotateClockwise()
    {
        Rotation = (Rotation + 90) % 360;
        // In real implementation, page sizes would swap width/height for 90/270
    }

    public void ExportHighlightsToMarkdown(string path)
    {
        try
        {
            var lines = new List<string> { "# Highlights from " + FileName, "" };
            foreach (var ann in Annotations.OrderBy(a => a.PageIndex))
            {
                lines.Add($"## Page {ann.PageIndex + 1}");
                lines.Add($"- \"{ann.Text}\" ({ann.ColorName})");
                if (!string.IsNullOrWhiteSpace(ann.Contents))
                    lines.Add($"  Note: {ann.Contents}");
                lines.Add("");
            }
            File.WriteAllLines(path, lines);
            StatusText = $"Exported to {path}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }
}
