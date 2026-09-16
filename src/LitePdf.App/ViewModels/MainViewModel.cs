using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using LitePdf.App.Infrastructure;
using LitePdf.App.Viewer;
using LitePdf.Core;
using LitePdf.Core.Text;

namespace LitePdf.App.ViewModels;

public enum SidebarPanel
{
    Thumbnails,
    Chapters,
    Search,
    Annotations,
}

/// <summary>Bindable state of the main window. Behaviour lives in MainWindow's partial classes.</summary>
public sealed class MainViewModel : ObservableObject
{
    private bool _hasDocument;
    private string _fileName = "";
    private bool _isDirty;
    private bool _canSave;
    private bool _canAnnotate;
    private int _pageCount;
    private int _currentPageNumber;
    private string _pageNumberText = "";
    private string _zoomText = "100%";
    private bool _isSidebarOpen = true;
    private SidebarPanel _selectedPanel = SidebarPanel.Thumbnails;
    private IReadOnlyList<ThumbnailItem> _thumbnails = [];
    private IReadOnlyList<OutlineNode> _outline = [];
    private string _searchQuery = "";
    private bool _matchCase;
    private bool _wholeWord;
    private string _searchStatus = "";
    private bool _isSearching;
    private double _searchProgress;
    private string _annotationsStatus = "";
    private bool _isScanBannerVisible;
    private bool _isOcrRunning;
    private double _ocrProgress;
    private string _ocrProgressText = "";
    private string _statusText = "";
    private string _toastText = "";
    private bool _isToastVisible;
    private bool _isBusy;
    private string _busyText = "";
    private ViewerTool _tool = ViewerTool.Select;
    private AnnotationColor _highlightColor = AnnotationColor.Yellow;
    private string _textSourceText = "";
    private bool _isFullScreen;
    private bool _isReadingAloud;

    public bool HasDocument { get => _hasDocument; set { if (Set(ref _hasDocument, value)) OnPropertyChanged(nameof(WindowTitle)); } }

    public string FileName { get => _fileName; set { if (Set(ref _fileName, value)) OnPropertyChanged(nameof(WindowTitle)); } }

    public bool IsDirty { get => _isDirty; set { if (Set(ref _isDirty, value)) OnPropertyChanged(nameof(WindowTitle)); } }

    public string WindowTitle => HasDocument ? $"{(IsDirty ? "● " : "")}{FileName} – {AppInfo.Name}" : AppInfo.Name;

    public bool CanSave { get => _canSave; set => Set(ref _canSave, value); }

    public bool CanAnnotate { get => _canAnnotate; set => Set(ref _canAnnotate, value); }

    public int PageCount
    {
        get => _pageCount;
        set
        {
            if (Set(ref _pageCount, value))
            {
                OnPropertyChanged(nameof(PageCountText));
                OnPropertyChanged(nameof(PageStatusText));
            }
        }
    }

    public int CurrentPageNumber
    {
        get => _currentPageNumber;
        set
        {
            if (Set(ref _currentPageNumber, value))
            {
                PageNumberText = value.ToString();
                OnPropertyChanged(nameof(PageStatusText));
            }
        }
    }

    public string PageNumberText { get => _pageNumberText; set => Set(ref _pageNumberText, value); }

    public string PageCountText => $"of {PageCount}";

    public string PageStatusText => HasDocument ? $"Page {CurrentPageNumber} of {PageCount}" : "";

    public string ZoomText { get => _zoomText; set => Set(ref _zoomText, value); }

    public bool IsSidebarOpen { get => _isSidebarOpen; set => Set(ref _isSidebarOpen, value); }

    public SidebarPanel SelectedPanel
    {
        get => _selectedPanel;
        set
        {
            if (!Set(ref _selectedPanel, value)) return;
            OnPropertyChanged(nameof(IsThumbnailsPanel));
            OnPropertyChanged(nameof(IsChaptersPanel));
            OnPropertyChanged(nameof(IsSearchPanel));
            OnPropertyChanged(nameof(IsAnnotationsPanel));
            OnPropertyChanged(nameof(SidebarTitle));
        }
    }

    public bool IsThumbnailsPanel { get => SelectedPanel == SidebarPanel.Thumbnails; set { if (value) SelectedPanel = SidebarPanel.Thumbnails; } }
    public bool IsChaptersPanel { get => SelectedPanel == SidebarPanel.Chapters; set { if (value) SelectedPanel = SidebarPanel.Chapters; } }
    public bool IsSearchPanel { get => SelectedPanel == SidebarPanel.Search; set { if (value) SelectedPanel = SidebarPanel.Search; } }
    public bool IsAnnotationsPanel { get => SelectedPanel == SidebarPanel.Annotations; set { if (value) SelectedPanel = SidebarPanel.Annotations; } }

    public string SidebarTitle => SelectedPanel switch
    {
        SidebarPanel.Chapters => "Chapters",
        SidebarPanel.Search => "Search",
        SidebarPanel.Annotations => "Annotations",
        _ => "Pages",
    };

    public IReadOnlyList<ThumbnailItem> Thumbnails { get => _thumbnails; set => Set(ref _thumbnails, value); }

    public IReadOnlyList<OutlineNode> Outline
    {
        get => _outline;
        set
        {
            if (Set(ref _outline, value)) OnPropertyChanged(nameof(HasOutline));
        }
    }

    public bool HasOutline => Outline.Count > 0;

    public string SearchQuery { get => _searchQuery; set => Set(ref _searchQuery, value); }

    public bool MatchCase { get => _matchCase; set => Set(ref _matchCase, value); }

    public bool WholeWord { get => _wholeWord; set => Set(ref _wholeWord, value); }

    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];

    public string SearchStatus { get => _searchStatus; set => Set(ref _searchStatus, value); }

    public bool IsSearching { get => _isSearching; set => Set(ref _isSearching, value); }

    public double SearchProgress { get => _searchProgress; set => Set(ref _searchProgress, value); }

    public ObservableCollection<AnnotationItem> Annotations { get; } = [];

    public string AnnotationsStatus { get => _annotationsStatus; set => Set(ref _annotationsStatus, value); }

    public ObservableCollection<RecentItem> RecentFiles { get; } = [];

    public bool IsScanBannerVisible { get => _isScanBannerVisible; set => Set(ref _isScanBannerVisible, value); }

    public bool IsOcrRunning { get => _isOcrRunning; set => Set(ref _isOcrRunning, value); }

    public double OcrProgress { get => _ocrProgress; set => Set(ref _ocrProgress, value); }

    public string OcrProgressText { get => _ocrProgressText; set => Set(ref _ocrProgressText, value); }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public string ToastText { get => _toastText; set => Set(ref _toastText, value); }

    public bool IsToastVisible { get => _isToastVisible; set => Set(ref _isToastVisible, value); }

    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public string BusyText { get => _busyText; set => Set(ref _busyText, value); }

    public ViewerTool Tool
    {
        get => _tool;
        set
        {
            if (!Set(ref _tool, value)) return;
            OnPropertyChanged(nameof(IsSelectTool));
            OnPropertyChanged(nameof(IsHandTool));
            OnPropertyChanged(nameof(IsRegionTool));
        }
    }

    public bool IsSelectTool { get => Tool == ViewerTool.Select; set { if (value) Tool = ViewerTool.Select; } }
    public bool IsHandTool { get => Tool == ViewerTool.Hand; set { if (value) Tool = ViewerTool.Hand; } }
    public bool IsRegionTool { get => Tool == ViewerTool.Region; set { if (value) Tool = ViewerTool.Region; } }

    public AnnotationColor HighlightColor
    {
        get => _highlightColor;
        set
        {
            if (Set(ref _highlightColor, value)) OnPropertyChanged(nameof(HighlightBrush));
        }
    }

    public Brush HighlightBrush => AnnotationItem.ToBrush(HighlightColor);

    public string TextSourceText { get => _textSourceText; set => Set(ref _textSourceText, value); }

    public bool IsFullScreen { get => _isFullScreen; set => Set(ref _isFullScreen, value); }

    public bool IsReadingAloud { get => _isReadingAloud; set => Set(ref _isReadingAloud, value); }
}

public sealed class ThumbnailItem(int pageIndex, double width, double height) : ObservableObject
{
    private ImageSource? _image;

    public int PageIndex { get; } = pageIndex;
    public string Label => (PageIndex + 1).ToString();
    public double Width { get; } = width;
    public double Height { get; } = height;
    public ImageSource? Image { get => _image; set => Set(ref _image, value); }

    internal CancellationTokenSource? Pending { get; set; }
}

public sealed class OutlineNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isCurrent;

    public OutlineNode(OutlineItem item, int depth)
    {
        Title = string.IsNullOrWhiteSpace(item.Title) ? "(Untitled)" : item.Title;
        Target = item.Target;
        Children = item.Children.Select(c => new OutlineNode(c, depth + 1)).ToList();
        _isExpanded = item.IsOpen || depth == 0 && Children.Count <= 12;
    }

    public string Title { get; }
    public Destination Target { get; }
    public IReadOnlyList<OutlineNode> Children { get; }
    public string PageLabel => Target.IsValid ? (Target.PageIndex + 1).ToString() : "";
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    public IEnumerable<OutlineNode> Flatten() => Children.SelectMany(c => c.Flatten()).Prepend(this);
}

public sealed class SearchResultItem(int pageIndex, TextMatch match, string before, string matchText, string after)
{
    public int PageIndex { get; } = pageIndex;
    public TextMatch Match { get; } = match;
    public string Before { get; } = before;
    public string MatchText { get; } = matchText;
    public string After { get; } = after;
    public string PageLabel => $"Page {PageIndex + 1}";
}

public sealed class AnnotationItem(PdfAnnotation annotation, string text)
{
    public PdfAnnotation Annotation { get; } = annotation;
    public string Text { get; } = string.IsNullOrWhiteSpace(text) ? "(No text)" : text;
    public string PageLabel => $"Page {Annotation.PageIndex + 1}";
    public string KindLabel => Annotation.Kind switch
    {
        AnnotationKind.Highlight => "Highlight",
        AnnotationKind.Underline => "Underline",
        AnnotationKind.StrikeOut => "Strikethrough",
        AnnotationKind.Squiggly => "Squiggly underline",
        AnnotationKind.Note => "Note",
        _ => "Annotation",
    };
    public Brush ColorBrush => ToBrush(Annotation.Color ?? AnnotationColor.Yellow);

    public static Brush ToBrush(AnnotationColor color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

public sealed class RecentItem(string path, DateTimeOffset lastOpened)
{
    public string Path { get; } = path;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Detail => lastOpened.Year < 2000
        ? System.IO.Path.GetDirectoryName(Path) ?? ""
        : $"{System.IO.Path.GetDirectoryName(Path)} · {Describe(lastOpened)}";

    private static string Describe(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
        if (age.TotalDays < 2) return "yesterday";
        return time.LocalDateTime.ToString("d MMM yyyy");
    }
}
