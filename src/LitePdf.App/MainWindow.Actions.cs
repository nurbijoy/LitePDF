using System.Windows;
using LitePdf.App.Documents;
using LitePdf.App.Infrastructure;
using LitePdf.App.ViewModels;
using LitePdf.App.Viewer;
using LitePdf.App.Views;
using LitePdf.Core;
using LitePdf.Core.Text;
using LitePdf.Ocr;

namespace LitePdf.App;

public partial class MainWindow
{
    private CancellationTokenSource? _ocrCts;
    private bool _bannerDismissed;
    private int _bannerVersion;

    // ---- Copy ----

    private async Task CopySelectionAsync()
    {
        string text = await Viewer.GetSelectedTextAsync();
        if (text.Length == 0) return;
        if (ClipboardHelper.TrySetText(text)) ShowToast(text.Length == 1 ? "Copied 1 character" : $"Copied {text.Length:N0} characters");
    }

    private async Task CopyPageTextAsync(int page)
    {
        if (_session is not { } session) return;
        var text = await session.GetTextAsync(page, RenderPriority.Interactive);
        if (text.VisibleCharCount == 0)
        {
            ShowToast("This page has no text. Use Recognize text first.");
            return;
        }
        if (ClipboardHelper.TrySetText(text.Text.Replace("\n", Environment.NewLine))) ShowToast($"Copied the text of page {page + 1}");
    }

    private async Task CopyPageImageAsync(int page)
    {
        if (_session is not { } session) return;
        var image = await session.RenderRegionAsync(page, new RectD(0, 0, 1, 1), minDpi: 150, minWidthPixels: 0);
        if (ClipboardHelper.TrySetImage(PageRenderer.ToBitmapSource(image))) ShowToast($"Copied page {page + 1} as an image");
    }

    // ---- Markup & notes ----

    private async Task MarkSelectionAsync(AnnotationKind kind, AnnotationColor? color = null, bool onlyIfSelection = false)
    {
        if (_session is not { } session) return;
        if (!session.CanAnnotate)
        {
            ShowToast("Images can't be annotated");
            return;
        }
        if (Viewer.Selection is not { } range)
        {
            if (!onlyIfSelection) ShowToast("Select some text first");
            return;
        }
        int pages = await session.AddMarkupAsync(range, kind, color ?? _vm.HighlightColor);
        if (pages == 0)
        {
            ShowToast("There's no text in the selection to mark");
            return;
        }
        Viewer.ClearSelection();
        ShowToast(kind switch
        {
            AnnotationKind.Underline => "Underlined",
            AnnotationKind.StrikeOut => "Struck through",
            _ => "Highlighted",
        });
    }

    private async Task AddNoteAtSelectionAsync()
    {
        if (_session is not { CanAnnotate: true } || Viewer.Layout is null) return;
        int page = Viewer.CurrentPageIndex;
        var position = new PointD(0.05, 0.05);
        if (Viewer.Selection is { } range && _session.TryGetCachedText(range.Start.PageIndex) is { } text &&
            text.GetRangeRects(range.Start.Index, Math.Min(text.Length, range.Start.Index + 1)) is { Count: > 0 } rects)
        {
            page = range.Start.PageIndex;
            position = new PointD(Math.Max(0, rects[0].Left - 0.04), rects[0].Top);
        }
        await AddNoteAsync(page, position);
    }

    private async Task AddNoteAsync(int page, PointD position)
    {
        if (_session is not { CanAnnotate: true } session) return;
        string? text = TextInputDialog.Prompt(this, "Add note", null, "", "Add note", multiline: true, allowDelete: false, out _);
        if (string.IsNullOrWhiteSpace(text)) return;
        await session.AddNoteAsync(page, position, text.Trim(), AnnotationColor.Yellow);
        ShowToast("Note added");
    }

    private async Task EditNoteAsync(PdfAnnotation note)
    {
        if (_session is not { CanAnnotate: true } session) return;
        string? text = TextInputDialog.Prompt(this, "Note", null, note.Contents, "Save", multiline: true, allowDelete: true, out bool deleted);
        if (text is null) return;
        if (deleted) await DeleteAnnotationAsync(note);
        else if (text != note.Contents) await session.SetAnnotationContentsAsync(note, text);
    }

    private Task DeleteSelectedAnnotationAsync() =>
        Viewer.SelectedAnnotation is { } annotation ? DeleteAnnotationAsync(annotation) : Task.CompletedTask;

    private async Task DeleteAnnotationAsync(PdfAnnotation annotation)
    {
        if (_session is not { CanAnnotate: true } session) return;
        await session.RemoveAnnotationAsync(annotation);
        Viewer.SetSelectedAnnotation(null);
        ShowToast(annotation.Kind == AnnotationKind.Note ? "Note deleted" : "Annotation deleted");
    }

    private MenuItemList ColorMenu(string header, Action<AnnotationColor> apply, AnnotationColor? current)
    {
        var menu = new System.Windows.Controls.MenuItem { Header = header, Icon = IconText(Icons.Highlight) };
        foreach (var (name, color) in AnnotationColor.Palette)
            menu.Items.Add(MenuItemFor(name, null, () => apply(color), isChecked: current == color, iconElement: ColorSwatch(color)));
        return new MenuItemList(menu);
    }

    private readonly record struct MenuItemList(System.Windows.Controls.MenuItem Item);

    private void OnViewerContextRequested(ViewerContext context)
    {
        if (_session is not { } session) return;
        var items = new List<object?>();
        int page = context.PageIndex;

        if (context.HasSelection)
        {
            items.Add(MenuItemFor("Copy", Icons.Copy, () => Run(CopySelectionAsync), "Ctrl+C"));
            if (session.CanAnnotate)
            {
                items.Add(MenuItemFor("Highlight", Icons.Highlight, () => Run(() => MarkSelectionAsync(AnnotationKind.Highlight)), "Ctrl+H"));
                items.Add(ColorMenu("Highlight with", c => { _vm.HighlightColor = c; Run(() => MarkSelectionAsync(AnnotationKind.Highlight, c)); }, _vm.HighlightColor).Item);
                items.Add(MenuItemFor("Underline", Icons.Underline, () => Run(() => MarkSelectionAsync(AnnotationKind.Underline)), "Ctrl+U"));
                items.Add(MenuItemFor("Strikethrough", null, () => Run(() => MarkSelectionAsync(AnnotationKind.StrikeOut))));
            }
            items.Add(MenuItemFor("Search for this", Icons.Search, ShowSearch));
            if (!_vm.IsReadingAloud) items.Add(MenuItemFor("Read aloud", Icons.Speaker, () => Run(ToggleReadAloudAsync)));
            items.Add(null);
        }

        if (context.Annotation is { } annotation)
        {
            if (annotation.Kind == AnnotationKind.Note) items.Add(MenuItemFor("Edit note…", Icons.Note, () => Run(() => EditNoteAsync(annotation))));
            items.Add(ColorMenu("Change color", c => Run(() => session.SetAnnotationColorAsync(annotation, c)), annotation.Color).Item);
            items.Add(MenuItemFor("Delete annotation", Icons.Delete, () => Run(() => DeleteAnnotationAsync(annotation)), "Del"));
            items.Add(null);
        }

        if (context.Link is { } link)
        {
            items.Add(MenuItemFor(link.Uri is not null ? "Open link" : $"Go to page {link.Target.PageIndex + 1}", Icons.Link, () => OnLinkClicked(link)));
            if (link.Uri is { } uri) items.Add(MenuItemFor("Copy link address", Icons.Copy, () => ClipboardHelper.TrySetText(uri)));
            items.Add(null);
        }

        if (session.CanAnnotate && context.Point is { } point)
            items.Add(MenuItemFor("Add note here…", Icons.Note, () => Run(() => AddNoteAsync(page, point))));
        items.Add(MenuItemFor("Select all on page", null, () => Run(async () =>
        {
            var text = await session.GetTextAsync(page, RenderPriority.Interactive);
            if (text.Length > 0) Viewer.SetSelection(new TextRange(new TextPosition(page, 0), new TextPosition(page, text.Length)));
        }), "Ctrl+A"));
        items.Add(MenuItemFor("Copy page text", Icons.Copy, () => Run(() => CopyPageTextAsync(page))));
        items.Add(MenuItemFor("Copy page as image", Icons.Picture, () => Run(() => CopyPageImageAsync(page))));
        items.Add(MenuItemFor("Recognize text on this page", Icons.Ocr, () => Run(() => RecognizePagesAsync([page]))));
        items.Add(null);
        items.Add(MenuItemFor("Rotate clockwise", Icons.Rotate, () => RotateView(1), "Ctrl+R"));
        items.Add(MenuItemFor("Document properties", Icons.Info, () => Run(ShowPropertiesAsync), "Ctrl+D"));
        OpenMenu(items);
    }

    // ---- OCR ----

    private async Task UpdateScanBannerAsync()
    {
        var session = _session;
        int page = Viewer.CurrentPageIndex;
        int version = ++_bannerVersion;
        UpdateTextSource();
        if (session is null || _bannerDismissed || _vm.IsOcrRunning)
        {
            _vm.IsScanBannerVisible = false;
            return;
        }
        bool needsOcr = await session.NeedsOcrAsync(page);
        if (version != _bannerVersion || !ReferenceEquals(session, _session)) return;
        _vm.IsScanBannerVisible = needsOcr && !_bannerDismissed && !_vm.IsOcrRunning;
        UpdateTextSource();
    }

    private void UpdateTextSource()
    {
        var text = _session?.TryGetCachedText(Viewer.CurrentPageIndex);
        _vm.TextSourceText = text switch
        {
            null => "",
            { Source: TextSource.Ocr } => "Recognized text",
            { VisibleCharCount: < DocumentSession.MinTextChars } => "No text layer",
            _ => "",
        };
    }

    private void OcrPage_Click(object sender, RoutedEventArgs e) => Run(() => RecognizePagesAsync([Viewer.CurrentPageIndex]));

    private void OcrAll_Click(object sender, RoutedEventArgs e) => Run(() => RecognizePagesAsync(null));

    private void DismissBanner_Click(object sender, RoutedEventArgs e)
    {
        _bannerDismissed = true;
        _vm.IsScanBannerVisible = false;
    }

    private void CancelOcr_Click(object sender, RoutedEventArgs e) => _ocrCts?.Cancel();

    private bool EnsureOcrAvailable()
    {
        if (_ocr.IsAvailable) return true;
        MessageDialog.Show(this, "Text recognition isn't available",
            "No recognition language is installed on this PC. Open Windows Settings › Time & language › Language & region, add a language and include its optical character recognition feature.",
            isError: true);
        return false;
    }

    /// <summary>Recognizes the given pages, or every page without a text layer when <paramref name="pages"/> is null.</summary>
    private async Task RecognizePagesAsync(IReadOnlyList<int>? pages)
    {
        if (_session is not { } session || !EnsureOcrAvailable()) return;
        if (_vm.IsOcrRunning)
        {
            ShowToast("Text recognition is already running");
            return;
        }

        if (pages is { Count: 1 })
        {
            var existing = await session.GetTextAsync(pages[0], RenderPriority.Interactive);
            if (existing.Source == TextSource.Pdf && existing.VisibleCharCount >= DocumentSession.MinTextChars)
            {
                ShowToast("This page already has selectable text");
                return;
            }
        }

        var cts = _ocrCts = new CancellationTokenSource();
        var candidates = pages ?? Enumerable.Range(0, session.PageCount).ToList();
        int recognized = 0;
        _vm.IsOcrRunning = true;
        _vm.IsScanBannerVisible = false;
        _vm.OcrProgress = 0;
        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                int page = candidates[i];
                _vm.OcrProgressText = candidates.Count == 1 ? $"Page {page + 1}" : $"Page {page + 1} · {i + 1} of {candidates.Count}";
                _vm.OcrProgress = (double)i / candidates.Count;

                if (pages is null && (session.HasOcrText(page) || !await session.NeedsOcrAsync(page, cts.Token))) continue;
                await session.RecognizePageAsync(page, pages is null ? RenderPriority.Background : RenderPriority.Interactive, cts.Token);
                if (!ReferenceEquals(_session, session)) return;
                session.NotifyTextChanged(page);
                recognized++;
            }
            _vm.OcrProgress = 1;
            ShowToast(recognized switch
            {
                0 when pages is null => "Every page already has selectable text",
                1 => "Text recognized. You can now select, copy and search it.",
                _ => $"Recognized text on {recognized} pages",
            });
        }
        catch (OperationCanceledException)
        {
            ShowToast(recognized > 0 ? $"Stopped after {recognized} pages" : "Text recognition cancelled");
        }
        catch (OcrUnavailableException ex)
        {
            MessageDialog.Show(this, "Text recognition isn't available", ex.Message, isError: true);
        }
        finally
        {
            if (ReferenceEquals(_ocrCts, cts)) _ocrCts = null;
            _vm.IsOcrRunning = false;
            Run(UpdateScanBannerAsync);
        }
    }

    private void OnRegionSelected(int page, RectD region)
    {
        OpenMenu(
        [
            MenuItemFor("Copy text", Icons.Copy, () => Run(() => CopyRegionTextAsync(page, region))),
            MenuItemFor("Recognize text…", Icons.Ocr, () => Run(() => RecognizeRegionAsync(page, region))),
            MenuItemFor("Copy as image", Icons.Picture, () => Run(() => CopyRegionImageAsync(page, region))),
        ]);
    }

    private async Task CopyRegionTextAsync(int page, RectD region)
    {
        if (_session is not { } session) return;
        var text = await session.GetTextAsync(page, RenderPriority.Interactive);
        string value = text.VisibleCharCount >= DocumentSession.MinTextChars ? text.GetTextInRects([region]) : "";
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!EnsureOcrAvailable()) return;
            var result = await WithBusyAsync("Recognizing text…", () => session.RecognizeRegionAsync(page, region));
            value = result.Text;
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowToast("No text found in the selected area");
            return;
        }
        if (ClipboardHelper.TrySetText(value)) ShowToast($"Copied {value.Length:N0} characters");
    }

    private async Task RecognizeRegionAsync(int page, RectD region)
    {
        if (_session is not { } session || !EnsureOcrAvailable()) return;
        var result = await WithBusyAsync("Recognizing text…", () => session.RecognizeRegionAsync(page, region));
        OcrResultDialog.Show(this, result.Text, result.LanguageTag, () => Run(() => CopyRegionImageAsync(page, region)));
    }

    private async Task CopyRegionImageAsync(int page, RectD region)
    {
        if (_session is not { } session) return;
        var image = await session.RenderRegionAsync(page, region, minDpi: 200, minWidthPixels: 0);
        if (ClipboardHelper.TrySetImage(PageRenderer.ToBitmapSource(image))) ShowToast("Copied the area as an image");
    }

    private async Task<T> WithBusyAsync<T>(string text, Func<Task<T>> action)
    {
        _vm.BusyText = text;
        _vm.IsBusy = true;
        try
        {
            return await action();
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }
}
