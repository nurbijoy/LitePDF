using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LitePdf.App.Documents;
using LitePdf.App.Infrastructure;
using LitePdf.App.ViewModels;
using LitePdf.App.Viewer;
using LitePdf.Core;
using LitePdf.Core.Text;
using Microsoft.Win32;

namespace LitePdf.App;

public partial class MainWindow
{
    private const double ThumbnailWidth = 150;

    private readonly DispatcherTimer _searchDebounce;
    private bool _syncingThumbnails;
    private bool _syncingSearchResults;
    private IReadOnlyList<OutlineNode> _outlineFlat = [];
    private OutlineNode? _currentChapter;
    private CancellationTokenSource? _searchCts;
    private string? _completedSearchKey;
    private int _currentHitIndex = -1;
    private CancellationTokenSource? _annotationsCts;
    private bool _annotationsStale = true;

    // ---- Thumbnails ----

    private void BuildThumbnails()
    {
        foreach (var old in _vm.Thumbnails) old.Pending?.Cancel();
        if (_session is not { } session)
        {
            _vm.Thumbnails = [];
            return;
        }
        _thumbnailRotation = Viewer.Rotation;
        _vm.Thumbnails = session.PageSizes.Select((size, i) =>
        {
            var r = size.Rotated(_thumbnailRotation);
            double height = Math.Clamp(Math.Round(ThumbnailWidth * r.Height / r.Width), 40, ThumbnailWidth * 3);
            return new ThumbnailItem(i, ThumbnailWidth, height);
        }).ToList();
        SyncThumbnailSelection(Viewer.CurrentPageIndex);
    }

    private void OnThumbnailRealizationChanged(ThumbnailItem item, bool realized)
    {
        var list = _vm.Thumbnails;
        if (item.PageIndex >= list.Count || !ReferenceEquals(list[item.PageIndex], item)) return;
        if (realized)
        {
            RenderThumbnail(item);
        }
        else
        {
            item.Pending?.Cancel();
            item.Pending = null;
            item.Image = null; // the render cache keeps recently used thumbnails
        }
    }

    private async void RenderThumbnail(ThumbnailItem item)
    {
        if (_session is not { } session || item.Pending is not null) return;
        int page = item.PageIndex, generation = session.GetGeneration(page), rotation = Viewer.Rotation;
        var mode = Viewer.ColorMode;
        if (Viewer.Cache.TryGetThumbnail(page, generation, mode, rotation, out var cached))
        {
            item.Image = cached.Bitmap;
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = ViewMath.ToPixels(item.Width, dpi), h = ViewMath.ToPixels(item.Height, dpi);
        var cts = item.Pending = new CancellationTokenSource();
        try
        {
            var bitmap = await PageRenderer.RenderAsync(session.Document, page, w, h, rotation, null, mode, RenderPriority.Thumbnail, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_session, session)) return;
            Viewer.Cache.PutThumbnail(page, new CachedRender(bitmap, w, generation, mode, rotation));
            item.Image = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Thumbnail for page {page + 1}");
        }
        finally
        {
            if (ReferenceEquals(item.Pending, cts)) item.Pending = null;
            cts.Dispose();
        }
    }

    private void RefreshThumbnail(int page)
    {
        if (page >= _vm.Thumbnails.Count) return;
        var item = _vm.Thumbnails[page];
        if (item.Image is null && item.Pending is null) return; // not realized
        item.Pending?.Cancel();
        item.Pending = null;
        RenderThumbnail(item);
    }

    private void RefreshAllThumbnails()
    {
        foreach (var item in _vm.Thumbnails)
            if (item.Image is not null || item.Pending is not null) RefreshThumbnail(item.PageIndex);
    }

    private void SyncThumbnailSelection(int page)
    {
        if (page < 0 || page >= _vm.Thumbnails.Count || ThumbnailList.SelectedIndex == page) return;
        _syncingThumbnails = true;
        try
        {
            ThumbnailList.SelectedIndex = page;
            if (ThumbnailList.IsVisible) ThumbnailList.ScrollIntoView(_vm.Thumbnails[page]);
        }
        finally
        {
            _syncingThumbnails = false;
        }
    }

    private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThumbnails || ThumbnailList.SelectedItem is not ThumbnailItem item) return;
        if (item.PageIndex != Viewer.CurrentPageIndex) Viewer.GoToPage(item.PageIndex);
    }

    // ---- Chapters ----

    private async Task LoadOutlineAsync(DocumentSession session)
    {
        try
        {
            var outline = await session.Document.GetOutlineAsync();
            if (!ReferenceEquals(_session, session)) return;
            _vm.Outline = outline.Select(item => new OutlineNode(item, 0)).ToList();
            _outlineFlat = _vm.Outline.SelectMany(n => n.Flatten()).Where(n => n.Target.IsValid).ToList();
            _currentChapter = null;
            UpdateCurrentChapter(Viewer.CurrentPageIndex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Reading outline");
            _vm.Outline = [];
            _outlineFlat = [];
        }
    }

    private void UpdateCurrentChapter(int page)
    {
        OutlineNode? best = null;
        foreach (var node in _outlineFlat)
            if (node.Target.PageIndex <= page && (best is null || node.Target.PageIndex >= best.Target.PageIndex))
                best = node;
        if (ReferenceEquals(best, _currentChapter)) return;
        if (_currentChapter is not null) _currentChapter.IsCurrent = false;
        _currentChapter = best;
        if (best is not null) best.IsCurrent = true;
    }

    private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Mouse clicks navigate on button-up (so re-clicking the selected chapter works); this handles keyboard.
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        if (e.NewValue is OutlineNode node && OutlineTree.IsKeyboardFocusWithin) Viewer.GoToDestination(node.Target);
    }

    private void OutlineTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null and not TreeView; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ToggleButton) return; // expander
            if (element is TreeViewItem { DataContext: OutlineNode node })
            {
                Viewer.GoToDestination(node.Target);
                return;
            }
        }
    }

    private void OutlineTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OutlineTree.SelectedItem is OutlineNode node)
        {
            e.Handled = true;
            Viewer.GoToDestination(node.Target);
            Viewer.Focus();
        }
    }

    // ---- Search ----

    private async void ShowSearch()
    {
        _vm.IsSidebarOpen = true;
        _vm.SelectedPanel = SidebarPanel.Search;
        if (Viewer.Selection is { } selection && selection.Start.PageIndex == selection.End.PageIndex)
        {
            string text = await Viewer.GetSelectedTextAsync();
            if (text.Length is > 0 and <= 80 && !text.Contains('\n')) _vm.SearchQuery = text;
        }
        await Dispatcher.InvokeAsync(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private string SearchKey => $"{_vm.SearchQuery.Trim()}{_vm.MatchCase}{_vm.WholeWord}";

    private async void StartSearch(bool jumpToFirst)
    {
        _searchDebounce.Stop();
        _searchCts?.Cancel();
        _vm.SearchResults.Clear();
        _currentHitIndex = -1;
        _completedSearchKey = null;
        Viewer.SetSearchHits(null);

        var session = _session;
        string query = _vm.SearchQuery.Trim();
        if (session is null || query.Length == 0)
        {
            _vm.SearchStatus = "";
            _vm.IsSearching = false;
            return;
        }

        var matcher = new TextMatcher(query, _vm.MatchCase, _vm.WholeWord);
        string key = SearchKey;
        var cts = _searchCts = new CancellationTokenSource();
        var hits = new Dictionary<int, IReadOnlyList<TextMatch>>();
        const int maxResults = 5000;
        int total = 0, scannedWithoutText = 0;
        _vm.IsSearching = true;
        _vm.SearchProgress = 0;
        _vm.SearchStatus = "Searching…";
        var sinceUpdate = Stopwatch.StartNew();

        try
        {
            for (int page = 0; page < session.PageCount && total < maxResults; page++)
            {
                var text = await session.GetTextAsync(page, RenderPriority.Background, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (text.VisibleCharCount < DocumentSession.MinTextChars) scannedWithoutText++;

                var matches = matcher.FindAll(text.Text, maxResults - total);
                if (matches.Count > 0)
                {
                    hits[page] = matches;
                    foreach (var match in matches)
                    {
                        var (before, hit, after) = TextMatcher.Snippet(text.Text, match);
                        _vm.SearchResults.Add(new SearchResultItem(page, match, before, hit, after));
                    }
                    total += matches.Count;
                }

                if (sinceUpdate.ElapsedMilliseconds > 120)
                {
                    sinceUpdate.Restart();
                    Viewer.SetSearchHits(new Dictionary<int, IReadOnlyList<TextMatch>>(hits));
                    _vm.SearchProgress = (page + 1.0) / session.PageCount;
                    _vm.SearchStatus = $"{total:N0} found · page {page + 1} of {session.PageCount}";
                }
            }

            Viewer.SetSearchHits(hits);
            _completedSearchKey = key;
            _vm.SearchStatus = total switch
            {
                0 when scannedWithoutText == session.PageCount => "No text to search. Use Recognize text first.",
                0 => "No results",
                >= maxResults => $"First {maxResults:N0} results",
                1 => "1 result",
                _ => $"{total:N0} results",
            };
            if (jumpToFirst && total > 0) GoToHit(FirstHitFromCurrentPage());
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) _vm.IsSearching = false;
        }
    }

    private int FirstHitFromCurrentPage()
    {
        int page = Viewer.CurrentPageIndex;
        for (int i = 0; i < _vm.SearchResults.Count; i++)
            if (_vm.SearchResults[i].PageIndex >= page) return i;
        return 0;
    }

    private void GoToHit(int index)
    {
        if (index < 0 || index >= _vm.SearchResults.Count) return;
        _currentHitIndex = index;
        var item = _vm.SearchResults[index];
        Viewer.ShowSearchHit(new SearchHitRef(item.PageIndex, item.Match));
        _syncingSearchResults = true;
        try
        {
            SearchResultsList.SelectedIndex = index;
            SearchResultsList.ScrollIntoView(item);
        }
        finally
        {
            _syncingSearchResults = false;
        }
        _vm.SearchStatus = $"Result {index + 1:N0} of {_vm.SearchResults.Count:N0}";
    }

    private void MoveToHit(int direction)
    {
        if (_completedSearchKey != SearchKey || _vm.SearchResults.Count == 0)
        {
            if (_vm.SearchQuery.Trim().Length == 0) ShowSearch();
            else StartSearch(jumpToFirst: true);
            return;
        }
        int count = _vm.SearchResults.Count;
        int next = _currentHitIndex < 0
            ? (direction > 0 ? FirstHitFromCurrentPage() : count - 1)
            : (_currentHitIndex + direction + count) % count;
        GoToHit(next);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            MoveToHit(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Viewer.Focus();
        }
    }

    private void SearchOption_Click(object sender, RoutedEventArgs e) => StartSearch(jumpToFirst: false);

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        ClearSearch();
        SearchBox.Focus();
    }

    private void ClearSearch()
    {
        _searchDebounce.Stop();
        _searchCts?.Cancel();
        if (_vm.SearchQuery.Length > 0)
        {
            _vm.SearchQuery = "";
            _searchDebounce.Stop();
        }
        _vm.SearchResults.Clear();
        _vm.SearchStatus = "";
        _vm.IsSearching = false;
        _completedSearchKey = null;
        _currentHitIndex = -1;
        Viewer.SetSearchHits(null);
    }

    private void PreviousHit_Click(object sender, RoutedEventArgs e) => MoveToHit(-1);

    private void NextHit_Click(object sender, RoutedEventArgs e) => MoveToHit(1);

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSearchResults && SearchResultsList.SelectedIndex >= 0) GoToHit(SearchResultsList.SelectedIndex);
    }

    // ---- Annotations ----

    private void OnSessionAnnotationsChanged(int page)
    {
        if (_vm.SelectedPanel == SidebarPanel.Annotations && _vm.IsSidebarOpen) RefreshAnnotations();
        else _annotationsStale = true;
    }

    private async void RefreshAnnotations()
    {
        _annotationsCts?.Cancel();
        _annotationsStale = false;
        var session = _session;
        if (session is null) return;
        if (!session.CanAnnotate)
        {
            _vm.Annotations.Clear();
            _vm.AnnotationsStatus = "Images can't be annotated.";
            return;
        }

        var cts = _annotationsCts = new CancellationTokenSource();
        try
        {
            var items = await CollectAnnotationsAsync(session, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_session, session)) return;
            _vm.Annotations.Clear();
            foreach (var item in items) _vm.Annotations.Add(item);
            _vm.AnnotationsStatus = items.Count switch
            {
                0 => "No annotations yet. Select text and press Ctrl+H to highlight it.",
                1 => "1 annotation",
                _ => $"{items.Count} annotations",
            };
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<List<AnnotationItem>> CollectAnnotationsAsync(DocumentSession session, CancellationToken ct)
    {
        var items = new List<AnnotationItem>();
        for (int page = 0; page < session.PageCount; page++)
        {
            var annotations = await session.GetAnnotationsAsync(page, RenderPriority.Background, ct);
            if (annotations.Count == 0) continue;
            PageText? text = annotations.Any(a => a.IsMarkup) ? await session.GetTextAsync(page, RenderPriority.Background, ct) : null;
            foreach (var annotation in annotations)
            {
                string marked = annotation.IsMarkup && text is not null
                    ? text.GetTextInRects(annotation.Quads.Count > 0 ? annotation.Quads : [annotation.Bounds])
                    : "";
                string display = annotation.Kind == AnnotationKind.Note ? annotation.Contents
                    : string.IsNullOrWhiteSpace(annotation.Contents) ? marked
                    : $"{marked} — {annotation.Contents}";
                items.Add(new AnnotationItem(annotation, display));
            }
        }
        return items;
    }

    private void AnnotationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnnotationsList.SelectedItem is not AnnotationItem item) return;
        var annotation = item.Annotation;
        var area = annotation.Quads.Count > 0 ? annotation.Quads.Aggregate(RectD.Empty, (a, b) => a.Union(b)) : annotation.Bounds;
        Viewer.GoToPage(annotation.PageIndex);
        if (!area.IsEmpty) Viewer.ScrollIntoView(annotation.PageIndex, area);
        Viewer.SetSelectedAnnotation(annotation);
    }

    private void AnnotationsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && AnnotationsList.SelectedItem is AnnotationItem item)
        {
            e.Handled = true;
            Run(() => DeleteAnnotationAsync(item.Annotation));
        }
    }

    private void ExportAnnotations_Click(object sender, RoutedEventArgs e) => Run(ExportAnnotationsAsync);

    private async Task ExportAnnotationsAsync()
    {
        if (_session is not { CanAnnotate: true } session) return;
        var items = await CollectAnnotationsAsync(session, CancellationToken.None);
        if (items.Count == 0)
        {
            ShowToast("There are no annotations to export");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FileName = Path.GetFileNameWithoutExtension(session.DisplayName) + " annotations.md",
            InitialDirectory = session.IsVirtual ? null : Path.GetDirectoryName(session.FilePath),
            Title = "Export annotations",
        };
        if (dialog.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine($"# Annotations – {session.DisplayName}").AppendLine();
        foreach (var group in items.GroupBy(i => i.Annotation.PageIndex))
        {
            sb.AppendLine($"## Page {group.Key + 1}").AppendLine();
            foreach (var item in group)
            {
                string color = item.Annotation.Color is { } c ? AnnotationColor.Palette.FirstOrDefault(p => p.Color == c).Name ?? c.ToHex() : "";
                sb.AppendLine($"- **{item.KindLabel}**{(color.Length > 0 ? $" ({color})" : "")}: {item.Text.Replace("\n", " ")}");
            }
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), Encoding.UTF8);
        ShowToast($"Exported {items.Count} annotations");
    }
}
