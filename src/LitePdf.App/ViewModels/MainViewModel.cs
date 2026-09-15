using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LitePdf.App.Services;
using LitePdf.App.Views;
using LitePdf.Core;
using LitePdf.Core.Annotations;
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
    private bool _isDirty;

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
                UpdateBookmarkSelection();
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
        set
        {
            if (SetProperty(ref _fileName, value))
                OnPropertyChanged(nameof(Title));
        }
    }

    public string Title => _isDirty ? $"{FileName}*" : FileName;

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

    private bool _isOcrRunning;
    public bool IsOcrRunning
    {
        get => _isOcrRunning;
        set => SetProperty(ref _isOcrRunning, value);
    }

    private int _ocrProgress;
    public int OcrProgress
    {
        get => _ocrProgress;
        set => SetProperty(ref _ocrProgress, value);
    }

    private CancellationTokenSource? _ocrCts;

    // Annotations
    public ObservableCollection<AnnotationModel> Annotations { get; } = new();
    private AnnotationColor _selectedColor = AnnotationColor.Palette[0];
    public AnnotationColor SelectedColor
    {
        get => _selectedColor;
        set => SetProperty(ref _selectedColor, value);
    }

    // Bookmarks
    public ObservableCollection<BookmarkEntry> Bookmarks { get; } = new();

    // Recent
    public ObservableCollection<RecentFileEntry> RecentFiles { get; } = new();

    // Tabs
    public TabsViewModel Tabs { get; } = new();

    // Comfort
    private bool _isFullScreen;
    public bool IsFullScreen
    {
        get => _isFullScreen;
        set => SetProperty(ref _isFullScreen, value);
    }

    private int _rotation = 0;
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

    // Events for view
    public event Action<int>? RequestGoToPage;
    public event Action? RequestFocusPageBox;
    public event Action? RequestFitWidth;
    public event Action? RequestFitPage;
    public event Action? RequestToggleFullScreen;

    // Commands
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
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
    public RelayCommand CopyPageAsImageCommand { get; }
    public RelayCommand OcrPageCommand { get; }
    public RelayCommand OcrAllCommand { get; }
    public RelayCommand CancelOcrCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand SearchNextCommand { get; }
    public RelayCommand SearchPrevCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand HighlightCommand { get; }
    public RelayCommand RemoveAnnotationCommand { get; }
    public RelayCommand ExportHighlightsCommand { get; }
    public RelayCommand ToggleDarkModeCommand { get; }
    public RelayCommand RotateCommand { get; }
    public RelayCommand ToggleTwoPageCommand { get; }
    public RelayCommand AddBookmarkCommand { get; }
    public RelayCommand RemoveBookmarkCommand { get; }
    public RelayCommand ReadAloudCommand { get; }
    public RelayCommand StopReadAloudCommand { get; }
    public RelayCommand PrintCommand { get; }
    public RelayCommand PropertiesCommand { get; }
    public RelayCommand FullScreenCommand { get; }
    public RelayCommand OpenRecentCommand { get; }

    private readonly SpeechService _speech = new();

    public MainViewModel()
    {
        OpenCommand = new RelayCommand(async _ => await OpenFileDialogAsync());
        SaveCommand = new RelayCommand(async _ => await SaveAsync(), _ => IsDocumentOpen && _isDirty);
        SaveAsCommand = new RelayCommand(async _ => await SaveAsAsync(), _ => IsDocumentOpen);
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
        CopyPageAsImageCommand = new RelayCommand(async _ => await CopyPageAsImageAsync(), _ => IsDocumentOpen);
        OcrPageCommand = new RelayCommand(async _ => await OcrCurrentPageAsync(), _ => IsDocumentOpen && IsOcrAvailable);
        OcrAllCommand = new RelayCommand(async _ => await OcrAllPagesAsync(), _ => IsDocumentOpen && IsOcrAvailable && !IsOcrRunning);
        CancelOcrCommand = new RelayCommand(_ => CancelOcr(), _ => IsOcrRunning);
        SearchCommand = new RelayCommand(async _ => await DoSearchAsync());
        SearchNextCommand = new RelayCommand(_ => MoveSearch(1), _ => SearchResults.Count > 0);
        SearchPrevCommand = new RelayCommand(_ => MoveSearch(-1), _ => SearchResults.Count > 0);
        ClearSearchCommand = new RelayCommand(_ => ClearSearch());
        HighlightCommand = new RelayCommand(async _ => await HighlightSelectionAsync(), _ => IsDocumentOpen && GetCurrentSelection() != null);
        RemoveAnnotationCommand = new RelayCommand(async _ => await RemoveSelectedAnnotationAsync(), _ => IsDocumentOpen);
        ExportHighlightsCommand = new RelayCommand(async _ => await ExportHighlightsAsync(), _ => Annotations.Count > 0);
        ToggleDarkModeCommand = new RelayCommand(_ => TogglePageMode());
        RotateCommand = new RelayCommand(_ => RotateClockwise());
        ToggleTwoPageCommand = new RelayCommand(_ => TwoPageView = !TwoPageView);
        AddBookmarkCommand = new RelayCommand(_ => AddBookmark(), _ => IsDocumentOpen);
        RemoveBookmarkCommand = new RelayCommand(_ => RemoveBookmark(), _ => Bookmarks.Count > 0);
        ReadAloudCommand = new RelayCommand(async _ => await ReadAloudAsync(), _ => IsDocumentOpen);
        StopReadAloudCommand = new RelayCommand(_ => _speech.Stop());
        PrintCommand = new RelayCommand(async _ => await PrintAsync(), _ => IsDocumentOpen);
        PropertiesCommand = new RelayCommand(_ => ShowProperties(), _ => IsDocumentOpen);
        FullScreenCommand = new RelayCommand(_ => { ToggleFullScreen(); RequestToggleFullScreen?.Invoke(); });
        OpenRecentCommand = new RelayCommand(async p => { if (p is string path) await OpenFileAsync(path); });

        var settings = SettingsStore.LoadSettings();
        SelectedOcrLanguage = settings.OcrLanguage ?? OcrLanguages.FirstOrDefault();
        SidebarVisible = settings.SidebarVisible;
        if (Enum.TryParse<PageMode>(settings.PageMode, true, out var pm)) PageMode = pm;

        LoadRecent();
    }

    private void LoadRecent()
    {
        RecentFiles.Clear();
        foreach (var r in SettingsStore.LoadRecent())
            RecentFiles.Add(r);
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

    private void UpdateBookmarkSelection()
    {
        // Could highlight bookmark if current page matches
    }

    private TextSelection? GetCurrentSelection()
    {
        if (Pages.Count == 0) return null;
        var page = Pages[CurrentPageIndex];
        return page.Selection;
    }

    public async Task OpenFileAsync(string path, string? password = null)
    {
        if (!File.Exists(path))
        {
            StatusText = "File not found.";
            return;
        }

        // Check if already open in tabs
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
            _isDirty = false;
            OnPropertyChanged(nameof(Title));

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

            // Outline
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

            // Text layers
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < doc.PageCount; i++)
                {
                    try
                    {
                        if (_ocrCache != null && _ocrCache.TryGet(i, out var cachedOcr) && cachedOcr != null)
                        {
                            // Need actual bitmap size for accurate conversion, approximate with page size
                            var layer = PageTextLayer.FromOcr(i, cachedOcr, doc.PageSizes[i], 1000, 1000);
                            lock (_textLayers) _textLayers[i] = layer;
                            _searchService.SetLayer(layer);
                            Application.Current.Dispatcher.Invoke(() => Pages[i].TextLayer = layer);
                            continue;
                        }

                        var tl = await doc.GetTextLayerAsync(i);
                        if (tl != null)
                        {
                            lock (_textLayers) _textLayers[i] = tl;
                            _searchService.SetLayer(tl);
                            Application.Current.Dispatcher.Invoke(() => Pages[i].TextLayer = tl);
                        }
                        else
                        {
                            // Scanned detection: char count <8
                            var cnt = await doc.GetCharCountAsync(i);
                            if (cnt < 8)
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    // Banner will show via HasText false
                                    Pages[i].TextLayer = null;
                                });
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(10);
                }
            });

            // Annotations
            _ = Task.Run(async () =>
            {
                try
                {
                    Annotations.Clear();
                    for (int i = 0; i < doc.PageCount; i++)
                    {
                        var annots = await doc.GetAnnotationsAsync(i);
                        foreach (var a in annots)
                        {
                            var model = new AnnotationModel(i, a.Index, (AnnotationType)a.Subtype, GetColorName(a.R, a.G, a.B), a.R, a.G, a.B, a.Quads, a.Contents, a.Contents);
                            Application.Current.Dispatcher.Invoke(() => Annotations.Add(model));
                        }
                    }
                }
                catch { }
            });

            // Bookmarks for this docKey
            Bookmarks.Clear();
            var recent = SettingsStore.LoadRecent().FirstOrDefault(r => r.DocKey == _docKey);
            if (recent != null)
            {
                foreach (var bm in recent.Bookmarks)
                    Bookmarks.Add(bm);
                // Resume
                if (recent.Page >= 0 && recent.Page < PageCount)
                {
                    CurrentPageIndex = recent.Page;
                    RequestGoToPage?.Invoke(recent.Page);
                    if (recent.Zoom > 0) Zoom = recent.Zoom;
                }
            }

            SettingsStore.AddRecent(path, _docKey, 0, Zoom);
            LoadRecent();
            RaiseGoToPageCanExecute();
            SaveCommand.RaiseCanExecuteChanged();
            SaveAsCommand.RaiseCanExecuteChanged();
        }
        catch (PdfiumException ex) when (ex.ErrorCode == 4)
        {
            StatusText = "Password required.";
            throw;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open: {ex.Message}";
            IsDocumentOpen = false;
        }
    }

    private static string GetColorName(byte r, byte g, byte b)
    {
        var closest = AnnotationColor.Palette.OrderBy(c => Math.Abs(c.R - r) + Math.Abs(c.G - g) + Math.Abs(c.B - b)).FirstOrDefault();
        return closest?.Name ?? "Custom";
    }

    public async Task OpenFileDialogAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf|Image files (*.png;*.jpg;*.jpeg;*.bmp;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tiff|All files (*.*)|*.*",
            Title = "Open"
        };
        if (dlg.ShowDialog() == true)
        {
            string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tiff" || ext == ".tif")
            {
                await OpenImageAsync(dlg.FileName);
                return;
            }

            try
            {
                await OpenFileAsync(dlg.FileName);
            }
            catch (PdfiumException ex) when (ex.ErrorCode == 4)
            {
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

    private async Task OpenImageAsync(string path)
    {
        try
        {
            // Load via WPF decoder
            var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            int w = frame.PixelWidth;
            int h = frame.PixelHeight;
            int stride = w * 4;
            byte[] pixels = new byte[stride * h];
            frame.CopyPixels(pixels, stride, 0);
            var rendered = new RenderedBitmap(w, h, stride, pixels);
            // Page size in points: assume 96 DPI -> points = pixels * 72/96
            double pw = w * 72.0 / 96.0;
            double ph = h * 72.0 / 96.0;
            var size = new PageSize(pw, ph);
            var doc = new ImageDocument(path, rendered, size);
            // Use same flow as PDF but with image doc
            CloseDocument();
            _document = doc;
            _filePath = path;
            FileName = System.IO.Path.GetFileName(path);
            PageCount = 1;
            IsDocumentOpen = true;
            _docKey = OcrCache.ComputeDocKey(path, null);
            _ocrCache = new OcrCache(_docKey);

            Pages.Clear();
            Thumbnails.Clear();
            var pvm = new PageViewModel(0, size);
            pvm.UpdateZoom(Zoom, DpiScale);
            Pages.Add(pvm);
            Thumbnails.Add(new ThumbnailViewModel(0, size));

            CurrentPageIndex = 0;
            StatusText = $"{FileName} - Image {w}x{h}";

            // Auto OCR image
            if (IsOcrAvailable)
                await OcrCurrentPageAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open image: {ex.Message}";
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
        Annotations.Clear();
        _textLayers.Clear();
        _searchService.Clear();
        BitmapCache.Clear();
        PageCount = 0;
        IsDocumentOpen = false;
        FileName = "No document";
        StatusText = "Ready";
        _isDirty = false;
        OnPropertyChanged(nameof(Title));
        RaiseGoToPageCanExecute();
    }

    public async Task<RenderedBitmap> RenderPageForVmAsync(int pageIndex, int pixelWidth, int pixelHeight, RenderFlags flags, int priority, CancellationToken ct)
    {
        if (_document == null) throw new InvalidOperationException("No document");

        // Apply dark/sepia transform if needed
        var bmp = await _document.RenderPageAsync(pageIndex, pixelWidth, pixelHeight, flags, priority, ct);
        if (PageMode == PageMode.Dark)
        {
            // Invert colors for dark mode (simple)
            var pixels = bmp.Pixels;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)(255 - pixels[i]); // B
                pixels[i + 1] = (byte)(255 - pixels[i + 1]); // G
                pixels[i + 2] = (byte)(255 - pixels[i + 2]); // R
            }
            return bmp with { Pixels = pixels };
        }
        else if (PageMode == PageMode.Sepia)
        {
            var pixels = bmp.Pixels;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                // Sepia formula
                int tr = (int)(0.393 * r + 0.769 * g + 0.189 * b);
                int tg = (int)(0.349 * r + 0.686 * g + 0.168 * b);
                int tb = (int)(0.272 * r + 0.534 * g + 0.131 * b);
                pixels[i] = (byte)Math.Min(255, tb);
                pixels[i + 1] = (byte)Math.Min(255, tg);
                pixels[i + 2] = (byte)Math.Min(255, tr);
            }
            return bmp with { Pixels = pixels };
        }
        return bmp;
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

    private async Task CopyPageAsImageAsync()
    {
        if (_document == null) return;
        try
        {
            var page = Pages[CurrentPageIndex];
            var bmp = await _document.RenderPageAsync(CurrentPageIndex, page.TargetPixelWidth, page.TargetPixelHeight, RenderFlags.Annotations, RenderPriority.Interactive);
            var wb = new WriteableBitmap(bmp.Width, bmp.Height, 96 * DpiScale, 96 * DpiScale, System.Windows.Media.PixelFormats.Bgra32, null);
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, bmp.Width, bmp.Height), bmp.Pixels, bmp.Stride, 0);
            Clipboard.SetImage(wb);
            StatusText = $"Copied page {CurrentPageNumber} as image.";
        }
        catch (Exception ex)
        {
            StatusText = $"Copy image failed: {ex.Message}";
        }
    }

    public async Task OcrCurrentPageAsync()
    {
        if (_document == null || !IsOcrAvailable) return;
        if (_pdfiumDoc == null && _document is not ImageDocument) return;

        StatusText = $"OCR page {CurrentPageNumber}...";
        IsOcrRunning = true;
        try
        {
            PageSize pageSize = _document.PageSizes[CurrentPageIndex];
            int targetW = (int)(pageSize.Width * 300.0 / 72.0);
            int targetH = (int)(pageSize.Height * 300.0 / 72.0);
            var clamped = ViewMath.ClampToMax(targetW, targetH, _ocrEngine.MaxImageDimension);
            targetW = clamped.Width;
            targetH = clamped.Height;

            var rendered = await _document.RenderPageAsync(CurrentPageIndex, targetW, targetH, RenderFlags.Annotations, RenderPriority.Background);
            var ocrResult = await _ocrEngine.RecognizeAsync(rendered, SelectedOcrLanguage);

            _ocrCache?.Set(CurrentPageIndex, ocrResult, targetW, targetH);

            var layer = PageTextLayer.FromOcr(CurrentPageIndex, ocrResult, pageSize, targetW, targetH);
            _textLayers[CurrentPageIndex] = layer;
            _searchService.SetLayer(layer);
            Pages[CurrentPageIndex].TextLayer = layer;

            var wnd = new Views.TextWindow();
            wnd.SetText(ocrResult.Text, $"OCR - Page {CurrentPageNumber}");
            wnd.Show();

            StatusText = $"OCR done: {ocrResult.Lines.Count} lines.";
        }
        catch (Exception ex)
        {
            StatusText = $"OCR failed: {ex.Message}";
        }
        finally
        {
            IsOcrRunning = false;
        }
    }

    public async Task OcrAllPagesAsync()
    {
        if (_document == null || _pdfiumDoc == null) return;
        IsOcrRunning = true;
        _ocrCts = new CancellationTokenSource();
        var ct = _ocrCts.Token;
        try
        {
            for (int i = 0; i < PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                OcrProgress = (int)((double)i / PageCount * 100);
                StatusText = $"OCR {i + 1}/{PageCount}... {OcrProgress}%";

                if (_textLayers.ContainsKey(i) && _textLayers[i].Source == TextSource.Ocr)
                    continue;

                // Check char count
                int cnt = await _pdfiumDoc.GetCharCountAsync(i, ct);
                if (cnt >= 8) continue;

                var pageSize = _pdfiumDoc.PageSizes[i];
                int targetW = (int)(pageSize.Width * 300.0 / 72.0);
                int targetH = (int)(pageSize.Height * 300.0 / 72.0);
                var clamped = ViewMath.ClampToMax(targetW, targetH, _ocrEngine.MaxImageDimension);
                targetW = clamped.Width;
                targetH = clamped.Height;

                var rendered = await _document.RenderPageAsync(i, targetW, targetH, RenderFlags.Annotations, RenderPriority.Background, ct);
                var ocrResult = await _ocrEngine.RecognizeAsync(rendered, SelectedOcrLanguage, ct);

                _ocrCache?.Set(i, ocrResult, targetW, targetH);

                var layer = PageTextLayer.FromOcr(i, ocrResult, pageSize, targetW, targetH);
                _textLayers[i] = layer;
                _searchService.SetLayer(layer);
                Application.Current.Dispatcher.Invoke(() => Pages[i].TextLayer = layer);
            }
            StatusText = $"OCR all done.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "OCR canceled.";
        }
        catch (Exception ex)
        {
            StatusText = $"OCR failed: {ex.Message}";
        }
        finally
        {
            IsOcrRunning = false;
            OcrProgress = 0;
            _ocrCts = null;
            CancelOcrCommand.RaiseCanExecuteChanged();
            OcrAllCommand.RaiseCanExecuteChanged();
        }
    }

    private void CancelOcr()
    {
        _ocrCts?.Cancel();
    }

    public async Task RegionOcrAsync(int pageIndex, RectD pdfRect)
    {
        if (_document == null || !IsOcrAvailable) return;
        try
        {
            // Render region at 300 DPI
            var pageSize = _document.PageSizes[pageIndex];
            // pdfRect is in PDF points, convert to pixel clip
            // We need to render that region at 300 DPI
            double scale = 300.0 / 72.0;
            int regionW = (int)((pdfRect.Right - pdfRect.Left) * scale);
            int regionH = (int)((pdfRect.Top - pdfRect.Bottom) * scale);
            var clamped = ViewMath.ClampToMax(regionW, regionH, _ocrEngine.MaxImageDimension);
            regionW = clamped.Width;
            regionH = clamped.Height;

            // For simplicity, render whole page then crop
            int fullW = (int)(pageSize.Width * scale);
            int fullH = (int)(pageSize.Height * scale);
            var fullClamped = ViewMath.ClampToMax(fullW, fullH, _ocrEngine.MaxImageDimension);
            fullW = fullClamped.Width;
            fullH = fullClamped.Height;

            var rendered = await _document.RenderPageAsync(pageIndex, fullW, fullH, RenderFlags.Annotations, RenderPriority.Background);

            // Crop to region
            double sx = (double)fullW / pageSize.Width;
            double sy = (double)fullH / pageSize.Height;
            int x = (int)(pdfRect.Left * sx);
            int y = (int)((pageSize.Height - pdfRect.Top) * sy);
            int w = (int)((pdfRect.Right - pdfRect.Left) * sx);
            int h = (int)((pdfRect.Top - pdfRect.Bottom) * sy);

            x = Math.Clamp(x, 0, fullW - 1);
            y = Math.Clamp(y, 0, fullH - 1);
            w = Math.Clamp(w, 1, fullW - x);
            h = Math.Clamp(h, 1, fullH - y);

            byte[] cropped = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
            {
                int srcRow = y + row;
                int srcOffset = srcRow * rendered.Stride + x * 4;
                int dstOffset = row * w * 4;
                Array.Copy(rendered.Pixels, srcOffset, cropped, dstOffset, w * 4);
            }
            var croppedBmp = new RenderedBitmap(w, h, w * 4, cropped);
            var ocrResult = await _ocrEngine.RecognizeAsync(croppedBmp, SelectedOcrLanguage);

            var wnd = new Views.TextWindow();
            wnd.SetText(ocrResult.Text, $"Region OCR - Page {pageIndex + 1}");
            wnd.Show();
        }
        catch (Exception ex)
        {
            StatusText = $"Region OCR failed: {ex.Message}";
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
            var results = await _searchService.SearchAsync(SearchQuery);
            foreach (var r in results)
                SearchResults.Add(r);

            // Update search rects on pages
            foreach (var p in Pages)
                p.SearchRects = Array.Empty<RectD>();

            foreach (var group in results.GroupBy(r => r.PageIndex))
            {
                var rects = group.SelectMany(r => r.Rects).ToList();
                if (group.Key >= 0 && group.Key < Pages.Count)
                    Pages[group.Key].SearchRects = rects;
            }

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
        foreach (var p in Pages)
            p.SearchRects = Array.Empty<RectD>();
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
        if (TwoPageView) maxWidth *= 2;
        return ViewMath.FitWidth(viewportWidthDip, maxWidth, chromeDip);
    }

    public double CalculateFitPage(double viewportWidthDip, double viewportHeightDip, double hChrome, double vChrome)
    {
        if (Pages.Count == 0) return Zoom;
        var page = Pages[CurrentPageIndex].PageSize;
        if (TwoPageView)
        {
            // Two pages side by side
            page = new PageSize(page.Width * 2, page.Height);
        }
        return ViewMath.FitPage(viewportWidthDip, viewportHeightDip, page, hChrome, vChrome);
    }

    // Annotations
    public async Task HighlightSelectionAsync()
    {
        var sel = GetCurrentSelection();
        if (sel == null || sel.IsEmpty || _pdfiumDoc == null) return;
        var rects = sel.GetRects();
        bool ok = await _pdfiumDoc.AddHighlightAsync(sel.PageIndex, rects, SelectedColor.R, SelectedColor.G, SelectedColor.B, sel.GetText());
        if (ok)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(Title));
            SaveCommand.RaiseCanExecuteChanged();
            // Re-render
            Pages[sel.PageIndex].OnUnrealized();
            BitmapCache.Clear();
            // Reload annotations
            var annots = await _pdfiumDoc.GetAnnotationsAsync(sel.PageIndex);
            Annotations.Clear();
            foreach (var a in annots)
            {
                var model = new AnnotationModel(sel.PageIndex, a.Index, (AnnotationType)a.Subtype, GetColorName(a.R, a.G, a.B), a.R, a.G, a.B, a.Quads, a.Contents, a.Contents);
                Annotations.Add(model);
            }
            StatusText = "Highlight added.";
        }
    }

    public async Task RemoveSelectedAnnotationAsync()
    {
        if (_pdfiumDoc == null || Annotations.Count == 0) return;
        // For simplicity, remove last annotation on current page
        var toRemove = Annotations.Where(a => a.PageIndex == CurrentPageIndex).OrderByDescending(a => a.AnnotIndex).FirstOrDefault();
        if (toRemove == null) return;
        bool ok = await _pdfiumDoc.RemoveAnnotationAsync(toRemove.PageIndex, toRemove.AnnotIndex);
        if (ok)
        {
            Annotations.Remove(toRemove);
            _isDirty = true;
            OnPropertyChanged(nameof(Title));
            SaveCommand.RaiseCanExecuteChanged();
            StatusText = "Annotation removed.";
        }
    }

    public async Task AddStickyNoteAsync(int pageIndex, RectD rect, string text)
    {
        if (_pdfiumDoc == null) return;
        // Use highlight API but with text annotation subtype
        // For simplicity, add highlight with contents as note
        var rects = new List<RectD> { rect };
        bool ok = await _pdfiumDoc.AddHighlightAsync(pageIndex, rects, 255, 255, 0, text);
        if (ok)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(Title));
            StatusText = "Sticky note added.";
        }
    }

    public async Task SaveAsync()
    {
        if (_pdfiumDoc == null || string.IsNullOrEmpty(_filePath)) return;
        try
        {
            StatusText = "Saving...";
            bool saved = await _pdfiumDoc.SaveIncrementalToSameFileAsync();
            if (!saved)
            {
                StatusText = "Save failed.";
                return;
            }
            // Need to close and replace file
            string tmpPath = _filePath + ".litepdf-tmp";
            if (!File.Exists(tmpPath))
            {
                StatusText = "Save failed: tmp not found.";
                return;
            }

            // Close doc
            var doc = _pdfiumDoc;
            _pdfiumDoc = null;
            _document = null;
            await doc.DisposeAsync();
            // Replace
            try
            {
                File.Replace(tmpPath, _filePath, null);
            }
            catch
            {
                // If replace fails, keep tmp
                StatusText = $"Saved to temp: {tmpPath}";
                return;
            }

            // Reopen
            await OpenFileAsync(_filePath);
            _isDirty = false;
            OnPropertyChanged(nameof(Title));
            StatusText = "Saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    public async Task SaveAsAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = System.IO.Path.GetFileName(_filePath)
        };
        if (dlg.ShowDialog() != true) return;
        if (_pdfiumDoc == null) return;
        try
        {
            bool ok = await _pdfiumDoc.SaveAsync(dlg.FileName, false);
            if (ok)
            {
                StatusText = $"Saved as {dlg.FileName}";
                _isDirty = false;
                OnPropertyChanged(nameof(Title));
            }
            else
                StatusText = "Save As failed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Save As failed: {ex.Message}";
        }
    }

    public async Task ExportHighlightsAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FileName = System.IO.Path.GetFileNameWithoutExtension(FileName) + "-highlights.md"
        };
        if (dlg.ShowDialog() != true) return;
        ExportHighlightsToMarkdown(dlg.FileName);
    }

    public void TogglePageMode()
    {
        PageMode = PageMode switch
        {
            PageMode.Normal => PageMode.Dark,
            PageMode.Dark => PageMode.Sepia,
            PageMode.Sepia => PageMode.Normal,
            _ => PageMode.Normal
        };
        // Clear cache to force re-render with new mode
        BitmapCache.Clear();
        foreach (var p in Pages) p.OnUnrealized();
        var settings = SettingsStore.LoadSettings();
        settings.PageMode = PageMode.ToString().ToLower();
        SettingsStore.SaveSettings(settings);
        StatusText = $"Page mode: {PageMode}";
    }

    public void RotateClockwise()
    {
        Rotation = (Rotation + 90) % 360;
        // Swap sizes for 90/270
        if (Rotation % 180 != 0)
        {
            // For simplicity, just update dip sizes (swap)
            foreach (var p in Pages)
            {
                // Need to recalc? We'll just trigger zoom update
                p.UpdateZoom(Zoom, DpiScale);
            }
        }
        StatusText = $"Rotated {Rotation}°";
    }

    public void ToggleFullScreen()
    {
        IsFullScreen = !IsFullScreen;
    }

    public void AddBookmark()
    {
        var bm = new BookmarkEntry { Title = $"Page {CurrentPageNumber}", Page = CurrentPageIndex, Created = DateTime.UtcNow };
        Bookmarks.Add(bm);
        // Save to recent
        var recent = SettingsStore.LoadRecent();
        var entry = recent.FirstOrDefault(r => r.DocKey == _docKey);
        if (entry != null)
        {
            entry.Bookmarks.Add(bm);
            SettingsStore.SaveRecent(recent);
        }
        StatusText = $"Bookmark added: {bm.Title}";
    }

    public void RemoveBookmark()
    {
        if (Bookmarks.Count == 0) return;
        var last = Bookmarks[^1];
        Bookmarks.Remove(last);
        var recent = SettingsStore.LoadRecent();
        var entry = recent.FirstOrDefault(r => r.DocKey == _docKey);
        if (entry != null)
        {
            entry.Bookmarks.RemoveAll(b => b.Page == last.Page && b.Title == last.Title);
            SettingsStore.SaveRecent(recent);
        }
    }

    public async Task ReadAloudAsync()
    {
        if (_document == null) return;
        try
        {
            var text = await _document.GetPageTextAsync(CurrentPageIndex);
            if (string.IsNullOrWhiteSpace(text) && _textLayers.TryGetValue(CurrentPageIndex, out var layer))
                text = layer.GetText();

            if (!string.IsNullOrWhiteSpace(text))
            {
                // Find host panel for MediaElement
                var host = Application.Current.MainWindow as MainWindow;
                // For simplicity, use speech service with dummy host
                await _speech.SpeakAsync(text, host!);
                StatusText = "Reading aloud...";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Read aloud failed: {ex.Message}";
        }
    }

    public async Task PrintAsync()
    {
        if (_document == null) return;
        try
        {
            await PrintService.PrintAsync(_document, CurrentPageIndex);
        }
        catch (Exception ex)
        {
            StatusText = $"Print failed: {ex.Message}";
        }
    }

    public void ShowProperties()
    {
        var dlg = new PropertiesDialog();
        dlg.Owner = Application.Current.MainWindow;
        dlg.SetProperties(_filePath, PageCount, Pages.Count > 0 ? Pages[CurrentPageIndex].PageSize : new PageSize(0, 0), _docKey);
        dlg.ShowDialog();
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

    public bool PromptSaveIfDirty()
    {
        if (!_isDirty) return true;
        var result = MessageBox.Show($"Save changes to {FileName}?", "LitePDF", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes)
        {
            _ = SaveAsync();
        }
        return true;
    }
}
