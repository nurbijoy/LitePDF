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
        if (_session is { IsVirtual: false } current && string.Equals(current.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
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
        if (!await ConfirmDiscardChangesAsync()) return;

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

        await AttachSessionAsync(session, path);
    }

    private static string DescribeOpenError(Exception ex) => ex switch
    {
        PdfiumException p => p.Message,
        NotSupportedException or FileFormatException or InvalidDataException => "The file format isn't supported or the file is damaged.",
        UnauthorizedAccessException => "Access to the file was denied.",
        IOException io => io.Message,
        _ => ex.Message,
    };

    private async Task AttachSessionAsync(DocumentSession session, string? recentPath)
    {
        await CloseSessionAsync();
        _session = session;
        session.DirtyChanged += OnSessionDirtyChanged;
        session.PageInvalidated += OnSessionPageInvalidated;
        session.PageTextChanged += OnSessionPageTextChanged;
        session.AnnotationsChanged += OnSessionAnnotationsChanged;
        session.DocumentReplaced += OnSessionDocumentReplaced;

        ViewState? state = null;
        if (recentPath is not null && _settings.RestoreLastPosition && _recent.Find(recentPath) is { } entry && entry.PageIndex < session.PageCount)
            state = new ViewState(entry.PageIndex, entry.PageOffset ?? 0, entry.Zoom, entry.ZoomMode, entry.Rotation, entry.LayoutMode);

        _bannerDismissed = false;
        _vm.HasDocument = true;
        _vm.FileName = session.DisplayName;
        _vm.PageCount = session.PageCount;
        _vm.CurrentPageNumber = (state?.PageIndex ?? 0) + 1;
        _vm.IsDirty = false;
        _vm.CanSave = false;
        _vm.CanAnnotate = session.CanAnnotate;
        _vm.Tool = ViewerTool.Select;
        _vm.StatusText = "";
        UpdateSidebarVisibility();

        Viewer.Open(session, state, _settings.DefaultZoomMode);
        BuildThumbnails();
        ClearSearch();
        _vm.Annotations.Clear();
        _annotationsStale = true;
        if (_vm.SelectedPanel == SidebarPanel.Annotations && _vm.IsSidebarOpen) RefreshAnnotations();

        if (recentPath is not null)
        {
            _recent.Upsert(new RecentFile { Path = recentPath, LastOpened = DateTimeOffset.Now, PageIndex = state?.PageIndex ?? 0, Zoom = state?.Zoom ?? 1, ZoomMode = state?.ZoomMode ?? _settings.DefaultZoomMode });
            SaveRecent();
            LoadRecentList();
        }

        Viewer.Focus();
        await LoadOutlineAsync(session);
    }

    private async Task CloseDocumentAsync()
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        await CloseSessionAsync();
    }

    private async Task CloseSessionAsync()
    {
        var session = _session;
        if (session is null) return;

        SaveReadingPosition();
        _ocrCts?.Cancel();
        _searchCts?.Cancel();
        _annotationsCts?.Cancel();
        _speech?.Stop();
        _vm.IsReadingAloud = false;

        session.DirtyChanged -= OnSessionDirtyChanged;
        session.PageInvalidated -= OnSessionPageInvalidated;
        session.PageTextChanged -= OnSessionPageTextChanged;
        session.AnnotationsChanged -= OnSessionAnnotationsChanged;
        session.DocumentReplaced -= OnSessionDocumentReplaced;
        _session = null;

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

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Closing document");
        }
    }

    private void SaveReadingPosition()
    {
        if (_session is not { IsVirtual: false } session || Viewer.GetViewState() is not { } state) return;
        _recent.Upsert(new RecentFile
        {
            Path = session.FilePath,
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
        await AttachSessionAsync(DocumentSession.FromImage(document, "Pasted image", _ocr, () => _settings.OcrLanguage, () => _settings.OcrOptions()), null);
    }

    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (_session is not { IsDirty: true } session) return true;
        var result = MessageDialog.Show(this, "Save your changes?",
            $"“{session.DisplayName}” has annotations that haven't been saved.", MessageDialogButtons.SaveDiscardCancel);
        return result switch
        {
            MessageDialogResult.Save => await SaveAsync(saveAs: false),
            MessageDialogResult.Discard => true,
            _ => false,
        };
    }

    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (_session is not { IsPdf: true } session) return false;
        if (!saveAs && !session.IsDirty) return true;

        string? target = null;
        if (saveAs || session.IsVirtual)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PDF document (*.pdf)|*.pdf",
                FileName = Path.GetFileName(session.FilePath),
                InitialDirectory = Path.GetDirectoryName(session.FilePath),
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
            await session.SaveAsync(target);
            _vm.FileName = session.DisplayName;
            if (target is not null)
            {
                _recent.Upsert(new RecentFile { Path = session.FilePath, LastOpened = DateTimeOffset.Now });
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

    private async Task PrintAsync()
    {
        if (_session is not { } session) return;
        try
        {
            if (await PrintService.PrintAsync(session.Document, session.DisplayName) is int pages)
                ShowToast(pages == 1 ? "Sent 1 page to the printer" : $"Sent {pages} pages to the printer");
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, "Printing failed.");
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
    }

    private void OnSessionPageInvalidated(int page) => RefreshThumbnail(page);

    private void OnSessionPageTextChanged(int page)
    {
        if (page == Viewer.CurrentPageIndex) Run(UpdateScanBannerAsync);
        if (_vm.SearchQuery.Trim().Length > 0) StartSearch(jumpToFirst: false);
    }

    private void OnSessionDocumentReplaced()
    {
        _vm.FileName = _session?.DisplayName ?? "";
        RefreshAllThumbnails();
        _annotationsStale = true;
        if (_vm.SelectedPanel == SidebarPanel.Annotations && _vm.IsSidebarOpen) RefreshAnnotations();
    }
}
