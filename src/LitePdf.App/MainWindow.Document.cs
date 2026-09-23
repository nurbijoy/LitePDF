using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LitePdf.App.Documents;
using LitePdf.App.Infrastructure;
using LitePdf.App.ViewModels;
using LitePdf.App.Viewer;
using LitePdf.App.Views;
using LitePdf.Core;
using LitePdf.Core.Export;
using LitePdf.Core.Storage;
using LitePdf.Pdfium;
using Microsoft.Win32;

namespace LitePdf.App;

public partial class MainWindow
{
    private const string OpenFilter =
        "PDFs and images|*.pdf;*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.webp|PDF documents|*.pdf|Images|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*";

    private SpeechService? _speech;

    private async Task OpenFileDialogAsync()
    {
        var dialog = new OpenFileDialog { Filter = OpenFilter, Title = "Open" };
        if (_session is { IsVirtual: false } s) dialog.InitialDirectory = Path.GetDirectoryName(s.FilePath);
        if (dialog.ShowDialog(this) == true) await OpenDocumentAsync(dialog.FileName);
    }

    public async Task OpenDocumentAsync(string path)
    {
        path = Path.GetFullPath(path);
        var existing = _vm.Tabs.FirstOrDefault(t => !t.IsVirtual && string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectTab(existing);
            Viewer.Focus();
            return;
        }
        if (!File.Exists(path))
        {
            MessageDialog.Show(this, "File not found", $"“{path}” no longer exists or was moved.", isError: true);
            _recent.Remove(path);
            SaveRecent();
            LoadRecentList();
            return;
        }

        DocumentSession session;
        string? password = null;
        bool retry = false;
        _vm.StatusText = $"Opening {Path.GetFileName(path)}…";
        while (true)
        {
            try
            {
                session = await DocumentSession.OpenAsync(path, password, _ocr, () => _settings.OcrLanguage, () => _settings.OcrOptions());
                break;
            }
            catch (PdfiumException ex) when (ex.Error == PdfiumError.Password)
            {
                password = PasswordDialog.Prompt(this, Path.GetFileName(path), retry);
                retry = true;
                if (password is null)
                {
                    _vm.StatusText = "";
                    return;
                }
            }
            catch (Exception ex)
            {
                _vm.StatusText = "";
                Log.Error(ex, $"Opening {path}");
                MessageDialog.Show(this, "Couldn't open this file", $"{Path.GetFileName(path)}\n\n{DescribeOpenError(ex)}", isError: true);
                return;
            }
        }

        var tab = new DocumentTab(session);
        _vm.Tabs.Add(tab);
        SelectTab(tab, recentPath: path);
        await LoadOutlineAsync(session);
    }

    private static string DescribeOpenError(Exception ex) => ex switch
    {
        PdfiumException p => p.Message,
        NotSupportedException or FileFormatException or InvalidDataException => "The file format isn't supported or the file is damaged.",
        UnauthorizedAccessException => "Access to the file was denied.",
        IOException io => io.Message,
        _ => ex.Message,
    };

    public void SelectTab(DocumentTab tab, string? recentPath = null)
    {
        if (ReferenceEquals(_activeTab, tab)) return;

        if (_activeTab is not null)
        {
            SaveActiveTabState();
            _activeTab.IsActive = false;
            if (_session is not null)
            {
                _session.DirtyChanged -= OnSessionDirtyChanged;
                _session.PageInvalidated -= OnSessionPageInvalidated;
                _session.PageTextChanged -= OnSessionPageTextChanged;
                _session.AnnotationsChanged -= OnSessionAnnotationsChanged;
                _session.DocumentReplaced -= OnSessionDocumentReplaced;
            }
            _taskCts?.Cancel();
            _speech?.Stop();
            _vm.IsReadingAloud = false;
        }

        _activeTab = tab;
        _session = tab.Session;
        _vm.ActiveTab = tab;
        tab.IsActive = true;

        tab.Session.DirtyChanged += OnSessionDirtyChanged;
        tab.Session.PageInvalidated += OnSessionPageInvalidated;
        tab.Session.PageTextChanged += OnSessionPageTextChanged;
        tab.Session.AnnotationsChanged += OnSessionAnnotationsChanged;
        tab.Session.DocumentReplaced += OnSessionDocumentReplaced;

        if (tab.ViewState is null && recentPath is not null && _settings.RestoreLastPosition && _recent.Find(recentPath) is { } entry && entry.PageIndex < tab.Session.PageCount)
            tab.ViewState = new ViewState(entry.PageIndex, entry.PageOffset ?? 0, entry.Zoom, entry.ZoomMode, entry.Rotation, entry.LayoutMode);

        _bannerDismissed = tab.BannerDismissed;
        _vm.HasDocument = true;
        _vm.FileName = tab.Title;
        _vm.PageCount = tab.Session.PageCount;
        _vm.CurrentPageNumber = (tab.ViewState?.PageIndex ?? 0) + 1;
        _vm.IsDirty = tab.IsDirty;
        _vm.CanSave = tab.IsDirty && tab.IsPdf;
        _vm.CanAnnotate = tab.CanAnnotate;
        _vm.Tool = ViewerTool.Select;
        _vm.StatusText = "";
        if (_vm.SelectedPanel != SidebarPanel.Documents)
        {
            _vm.SelectedPanel = tab.SelectedPanel;
            _vm.IsSidebarOpen = tab.IsSidebarOpen;
        }
        UpdateSidebarVisibility();

        Viewer.Open(tab.Session, tab.ViewState, _settings.DefaultZoomMode);

        if (tab.Thumbnails.Count == tab.Session.PageCount && tab.ThumbnailRotation == Viewer.Rotation)
        {
            _vm.Thumbnails = tab.Thumbnails;
            _thumbnailRotation = tab.ThumbnailRotation;
            SyncThumbnailSelection(Viewer.CurrentPageIndex);
        }
        else
        {
            BuildThumbnails();
        }

        _vm.SearchQuery = tab.SearchQuery;
        _vm.MatchCase = tab.MatchCase;
        _vm.WholeWord = tab.WholeWord;
        _vm.SearchResults.Clear();
        foreach (var item in tab.SearchResults) _vm.SearchResults.Add(item);
        _vm.SearchStatus = tab.SearchStatus;
        _completedSearchKey = tab.CompletedSearchKey;
        _currentHitIndex = tab.CurrentHitIndex;
        Viewer.SetSearchHits(tab.SearchHits);

        _vm.Outline = tab.Outline;
        _outlineFlat = tab.OutlineFlat;
        _currentChapter = tab.CurrentChapter;
        UpdateCurrentChapter(Viewer.CurrentPageIndex);

        _vm.Annotations.Clear();
        foreach (var a in tab.Annotations) _vm.Annotations.Add(a);
        _vm.AnnotationsStatus = tab.AnnotationsStatus;
        _annotationsStale = tab.AnnotationsStale;
        if (_vm.SelectedPanel == SidebarPanel.Annotations && _vm.IsSidebarOpen && _annotationsStale) RefreshAnnotations();

        if (recentPath is not null)
        {
            _recent.Upsert(new RecentFile { Path = recentPath, LastOpened = DateTimeOffset.Now, PageIndex = tab.ViewState?.PageIndex ?? 0, Zoom = tab.ViewState?.Zoom ?? 1, ZoomMode = tab.ViewState?.ZoomMode ?? _settings.DefaultZoomMode });
            SaveRecent();
            LoadRecentList();
        }

        Viewer.Focus();
    }

    private void SaveActiveTabState()
    {
        if (_activeTab is null) return;
        _activeTab.ViewState = Viewer.GetViewState();
        _activeTab.Thumbnails = _vm.Thumbnails;
        _activeTab.ThumbnailRotation = _thumbnailRotation;
        _activeTab.Outline = _vm.Outline;
        _activeTab.OutlineFlat = _outlineFlat;
        _activeTab.CurrentChapter = _currentChapter;
        _activeTab.SearchQuery = _vm.SearchQuery;
        _activeTab.MatchCase = _vm.MatchCase;
        _activeTab.WholeWord = _vm.WholeWord;
        _activeTab.SearchResults = _vm.SearchResults.ToList();
        _activeTab.SearchStatus = _vm.SearchStatus;
        _activeTab.CompletedSearchKey = _completedSearchKey;
        _activeTab.CurrentHitIndex = _currentHitIndex;
        _activeTab.SearchHits = Viewer.SearchHits;
        _activeTab.Annotations = _vm.Annotations.ToList();
        _activeTab.AnnotationsStatus = _vm.AnnotationsStatus;
        _activeTab.AnnotationsStale = _annotationsStale;
        _activeTab.SelectedPanel = _vm.SelectedPanel;
        _activeTab.IsSidebarOpen = _vm.IsSidebarOpen;
        _activeTab.BannerDismissed = _bannerDismissed;
    }

    public async Task<bool> CloseTabAsync(DocumentTab? tab)
    {
        if (tab is null) return true;
        if (tab.IsDirty)
        {
            if (!ReferenceEquals(_activeTab, tab)) SelectTab(tab);
            var result = MessageDialog.Show(this, "Save your changes?",
                $"“{tab.Title}” has annotations that haven't been saved.", MessageDialogButtons.SaveDiscardCancel);
            if (result == MessageDialogResult.Cancel) return false;
            if (result == MessageDialogResult.Save)
            {
                if (!await SaveTabAsync(tab, saveAs: false)) return false;
            }
        }

        SaveReadingPosition(tab);

        int index = _vm.Tabs.IndexOf(tab);
        bool wasActive = ReferenceEquals(_activeTab, tab);

        _vm.Tabs.Remove(tab);

        try
        {
            await tab.Session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Disposing closed tab");
        }

        if (wasActive)
        {
            if (_vm.Tabs.Count > 0)
            {
                int nextIndex = Math.Clamp(index, 0, _vm.Tabs.Count - 1);
                SelectTab(_vm.Tabs[nextIndex]);
            }
            else
            {
                await CloseSessionAsync();
            }
        }

        return true;
    }

    public async Task CloseOtherTabsAsync(DocumentTab keepTab)
    {
        var others = _vm.Tabs.Where(t => !ReferenceEquals(t, keepTab)).ToList();
        foreach (var tab in others)
        {
            if (!await CloseTabAsync(tab)) break;
        }
    }

    public async Task<bool> CloseAllTabsWithPromptAsync()
    {
        if (_vm.Tabs.Count == 0) return true;
        if (_vm.Tabs.Count > 1)
        {
            var result = MessageDialog.Show(this, "Close all tabs?",
                $"You have {_vm.Tabs.Count} open tabs. Do you want to close all tabs?",
                MessageDialogButtons.OkCancel, okText: "Close all tabs");
            if (result != MessageDialogResult.Ok) return false;
        }

        foreach (var tab in _vm.Tabs.ToList())
        {
            if (!await CloseTabAsync(tab)) return false;
        }
        return true;
    }

    public void SwitchTab(int direction)
    {
        if (_vm.Tabs.Count <= 1 || _activeTab is null) return;
        int currentIndex = _vm.Tabs.IndexOf(_activeTab);
        if (currentIndex < 0) return;
        int nextIndex = (currentIndex + direction + _vm.Tabs.Count) % _vm.Tabs.Count;
        SelectTab(_vm.Tabs[nextIndex]);
    }

    private async Task CloseDocumentAsync()
    {
        if (_activeTab is not null)
            await CloseTabAsync(_activeTab);
        else
            await CloseSessionAsync();
    }

    private async Task CloseSessionAsync()
    {
        var session = _session;
        SaveActiveTabState();
        _taskCts?.Cancel();
        _searchCts?.Cancel();
        _annotationsCts?.Cancel();
        _speech?.Stop();
        _vm.IsReadingAloud = false;

        if (session is not null)
        {
            session.DirtyChanged -= OnSessionDirtyChanged;
            session.PageInvalidated -= OnSessionPageInvalidated;
            session.PageTextChanged -= OnSessionPageTextChanged;
            session.AnnotationsChanged -= OnSessionAnnotationsChanged;
            session.DocumentReplaced -= OnSessionDocumentReplaced;
        }
        _session = null;
        _activeTab = null;
        _vm.ActiveTab = null;

        Viewer.Close();
        foreach (var item in _vm.Thumbnails) item.Pending?.Cancel();
        _vm.Thumbnails = [];
        _vm.Outline = [];
        _outlineFlat = [];
        ClearSearch();
        _vm.Annotations.Clear();
        _vm.HasDocument = false;
        _vm.IsDirty = false;
        _vm.CanSave = false;
        _vm.CanAnnotate = false;
        _vm.IsScanBannerVisible = false;
        _vm.TextSourceText = "";
        _vm.PageCount = 0;
        UpdateSidebarVisibility();
        LoadRecentList();

        if (session is not null)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Closing document");
            }
        }
    }

    private void SaveReadingPosition(DocumentTab? tab)
    {
        if (tab is null || tab.IsVirtual) return;
        var state = ReferenceEquals(_activeTab, tab) ? Viewer.GetViewState() : tab.ViewState;
        if (state is null) return;
        _recent.Upsert(new RecentFile
        {
            Path = tab.FilePath,
            LastOpened = DateTimeOffset.Now,
            PageIndex = state.PageIndex,
            PageOffset = state.PageOffset,
            Zoom = state.Zoom,
            ZoomMode = state.ZoomMode,
            Rotation = state.Rotation,
            LayoutMode = state.LayoutMode,
        });
        SaveRecent();
    }

    private void SaveReadingPosition() => SaveReadingPosition(_activeTab);

    private void SaveRecent()
    {
        try
        {
            _recent.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving recent files");
        }
    }

    private void LoadRecentList()
    {
        _vm.RecentFiles.Clear();
        var entries = _recent.Items
            .Where(e => Path.IsPathFullyQualified(e.Path))
            .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Take(8);
        foreach (var entry in entries)
            _vm.RecentFiles.Add(new RecentItem(entry.Path, entry.LastOpened));
        RecentHeader.Visibility = _vm.RecentFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentItem item) Run(() => OpenDocumentAsync(item.Path));
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not RecentItem item) return;
        _recent.Remove(item.Path);
        SaveRecent();
        LoadRecentList();
    }

    private void PasteImage_Click(object sender, RoutedEventArgs e) => Run(PasteImageAsync);

    private async Task PasteImageAsync()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var file = Clipboard.GetFileDropList().Cast<string>()
                .FirstOrDefault(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ImageDocument.IsImagePath(f));
            if (file is not null)
            {
                await OpenDocumentAsync(file);
                return;
            }
        }
        if (!Clipboard.ContainsImage())
        {
            ShowToast("The clipboard doesn't contain an image");
            return;
        }
        if (!await ConfirmDiscardChangesAsync()) return;
        var image = Clipboard.GetImage();
        if (image is null)
        {
            ShowToast("The clipboard image couldn't be read");
            return;
        }
        var document = ImageDocument.FromBitmap(image, "Pasted image");
        var session = DocumentSession.FromImage(document, "Pasted image", _ocr, () => _settings.OcrLanguage, () => _settings.OcrOptions());
        var tab = new DocumentTab(session);
        _vm.Tabs.Add(tab);
        SelectTab(tab, recentPath: null);
    }

    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (_activeTab is not { IsDirty: true } tab) return true;
        var result = MessageDialog.Show(this, "Save your changes?",
            $"“{tab.Title}” has annotations that haven't been saved.", MessageDialogButtons.SaveDiscardCancel);
        return result switch
        {
            MessageDialogResult.Save => await SaveAsync(saveAs: false),
            MessageDialogResult.Discard => true,
            _ => false,
        };
    }

    private async Task<bool> SaveTabAsync(DocumentTab tab, bool saveAs)
    {
        if (!tab.IsPdf) return false;
        if (!saveAs && !tab.IsDirty) return true;

        string? target = null;
        if (saveAs || tab.IsVirtual)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PDF document (*.pdf)|*.pdf",
                FileName = Path.GetFileName(tab.FilePath),
                InitialDirectory = Path.GetDirectoryName(tab.FilePath),
                DefaultExt = ".pdf",
                AddExtension = true,
                Title = "Save as",
            };
            if (dialog.ShowDialog(this) != true) return false;
            target = dialog.FileName;
        }

        _vm.BusyText = "Saving…";
        _vm.IsBusy = true;
        try
        {
            await tab.Session.SaveAsync(target);
            tab.Title = tab.Session.DisplayName;
            tab.IsDirty = tab.Session.IsDirty;
            if (ReferenceEquals(_activeTab, tab))
            {
                _vm.FileName = tab.Title;
                _vm.IsDirty = tab.IsDirty;
                _vm.CanSave = tab.IsDirty && tab.IsPdf;
            }
            if (target is not null)
            {
                _recent.Upsert(new RecentFile { Path = tab.Session.FilePath, LastOpened = DateTimeOffset.Now });
                SaveRecent();
            }
            ShowToast("Saved");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfiumException)
        {
            Log.Error(ex, "Saving");
            MessageDialog.Show(this, "Couldn't save the document",
                $"{ex.Message}\n\nThe original file wasn't changed. Try “Save as” to save a copy in another folder.", isError: true);
            return false;
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }

    private Task<bool> SaveAsync(bool saveAs) =>
        _activeTab is not null ? SaveTabAsync(_activeTab, saveAs) : Task.FromResult(false);

    private async Task PrintAsync()
    {
        if (_session is not { } session) return;
        try
        {
            if (await PrintService.PrintAsync(session.Document, session.DisplayName, Viewer.CurrentPageIndex, this) is int pages)
                ShowToast(pages == 1 ? "Sent 1 page to the printer" : $"Sent {pages} pages to the printer");
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, "Printing failed.");
        }
    }

    private async Task ExportDocxAsync()
    {
        if (_session is not { } session) return;
        if (_vm.IsTaskRunning)
        {
            ShowToast("Another job is already running");
            return;
        }

        if (!session.IsPdf)
        {
            ShowToast("Word conversion supports text PDFs only");
            return;
        }
        if (ExportDocxDialog.Show(this, session.PageCount, Viewer.CurrentPageIndex) is not { } settings) return;

        var save = new SaveFileDialog
        {
            Filter = "Word document (*.docx)|*.docx",
            FileName = Path.GetFileNameWithoutExtension(session.DisplayName) + ".docx",
            InitialDirectory = session.IsVirtual ? null : Path.GetDirectoryName(session.FilePath),
            DefaultExt = ".docx",
            AddExtension = true,
            Title = "Convert to Word",
        };
        if (save.ShowDialog(this) != true) return;

        var cts = _taskCts = new CancellationTokenSource();
        var progress = new Progress<(double Fraction, string Text)>(report =>
        {
            _vm.TaskProgress = report.Fraction;
            _vm.TaskProgressText = report.Text;
        });

        _vm.TaskTitle = "Converting to Word";
        _vm.TaskProgressText = "Reading the document";
        _vm.TaskProgress = 0;
        _vm.IsTaskRunning = true;
        try
        {
            int converted;
            while (true)
            {
                try
                {
                    converted = await DocxExport.ExportAsync(session, save.FileName, settings, progress, cts.Token);
                    break;
                }
                catch (UnsupportedPagesException ex) when (!settings.SkipUnsupportedPages)
                {
                    // A text document with a picture for a cover, or a scanned page bound in: say which pages
                    // they are and offer the rest, rather than send the reader off to work out a page range.
                    _vm.IsTaskRunning = false;
                    int rest = ex.SelectedCount - ex.Pages.Count;
                    string which = ex.Pages.Count == 1
                        ? $"Page {TextPdfExport.DescribePages(ex.Pages)} has"
                        : $"Pages {TextPdfExport.DescribePages(ex.Pages)} have";
                    var answer = MessageDialog.Show(this, "Some pages have no text",
                        $"{which} no text to convert: scanned pages and pictures can't be turned into Word text.\n\n" +
                        $"Convert the other {rest} {(rest == 1 ? "page" : "pages")} and leave {(ex.Pages.Count == 1 ? "it" : "them")} out?",
                        MessageDialogButtons.OkCancel, okText: rest == 1 ? "Convert 1 page" : $"Convert {rest} pages");
                    if (answer != MessageDialogResult.Ok) return;

                    settings = settings with { SkipUnsupportedPages = true };
                    _vm.TaskProgress = 0;
                    _vm.TaskProgressText = "Reading the document";
                    _vm.IsTaskRunning = true;
                }
            }
            ShowToast(converted == 1 ? "Converted 1 page to Word" : $"Converted {converted} pages to Word");
            RevealInExplorer(save.FileName);
        }
        catch (OperationCanceledException)
        {
            ShowToast("Conversion cancelled");
        }
        catch (NotSupportedException ex)
        {
            MessageDialog.Show(this, "Text PDFs only", ex.Message, isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfiumException)
        {
            Log.Error(ex, "Converting to Word");
            MessageDialog.Show(this, "Couldn't create the Word document",
                $"{ex.Message}\n\nThe PDF wasn't changed. Try saving to another folder.", isError: true);
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, "The document could not be converted to Word.");
        }
        finally
        {
            if (ReferenceEquals(_taskCts, cts)) _taskCts = null;
            _vm.IsTaskRunning = false;
        }
    }

    /// <summary>Opens Explorer with the new file selected, which is what people do next anyway.</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Showing the converted file in Explorer");
        }
    }

    private async Task ShowPropertiesAsync()
    {
        if (_session is not { } session) return;
        var info = await session.Document.GetInfoAsync();
        PropertiesDialog.Show(this, info, session.PageSizes.Count > 0 ? session.PageSizes[0] : default);
    }

    private void OnLinkClicked(PdfLink link)
    {
        if (link.Target.IsValid)
        {
            Viewer.GoToDestination(link.Target);
            return;
        }
        if (link.Uri is not { } uri) return;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            MessageDialog.Show(this, "Link blocked", $"For your safety, LitePDF only opens web and email links.\n\n{uri}", isError: true);
            return;
        }
        if (MessageDialog.Show(this, "Open link?", $"This link will open outside LitePDF:\n\n{uri}", MessageDialogButtons.OkCancel, okText: "Open link") != MessageDialogResult.Ok)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, "The link couldn't be opened.");
        }
    }

    private async Task ToggleReadAloudAsync()
    {
        if (_vm.IsReadingAloud)
        {
            _speech?.Stop();
            _vm.IsReadingAloud = false;
            return;
        }
        if (_session is not { } session) return;

        string text = Viewer.Selection is not null
            ? await Viewer.GetSelectedTextAsync()
            : (await session.GetTextAsync(Viewer.CurrentPageIndex, RenderPriority.Interactive)).Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("There's no text to read on this page");
            return;
        }

        if (_speech is null)
        {
            _speech = new SpeechService();
            _speech.Finished += () => Dispatcher.BeginInvoke(() => _vm.IsReadingAloud = false);
        }
        await _speech.SpeakAsync(text);
        _vm.IsReadingAloud = true;
        ShowToast(Viewer.Selection is not null ? "Reading the selection aloud" : $"Reading page {Viewer.CurrentPageIndex + 1} aloud");
    }

    // ---- Session events ----

    private void OnSessionDirtyChanged()
    {
        if (_session is not { } session) return;
        _vm.IsDirty = session.IsDirty;
        _vm.CanSave = session.IsDirty && session.IsPdf;
        if (_activeTab is not null)
        {
            _activeTab.IsDirty = session.IsDirty;
        }
    }

    private void OnSessionPageInvalidated(int page) => RefreshThumbnail(page);

    private void OnSessionPageTextChanged(int page)
    {
        if (page == Viewer.CurrentPageIndex) Run(UpdateScanBannerAsync);
        if (_vm.SearchQuery.Trim().Length > 0) StartSearch(jumpToFirst: false);
    }

    private void OnSessionDocumentReplaced()
    {
        if (_activeTab is not null) _activeTab.Title = _session?.DisplayName ?? "";
        _vm.FileName = _session?.DisplayName ?? "";
        RefreshAllThumbnails();
        _annotationsStale = true;
        if (_vm.SelectedPanel == SidebarPanel.Annotations && _vm.IsSidebarOpen) RefreshAnnotations();
    }
}
