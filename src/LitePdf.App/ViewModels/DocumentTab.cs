using LitePdf.App.Documents;
using LitePdf.App.Infrastructure;
using LitePdf.App.Viewer;
using LitePdf.Core.Text;

namespace LitePdf.App.ViewModels;

public sealed class DocumentTab : ObservableObject
{
    private string _title = "";
    private bool _isActive;
    private bool _isDirty;

    public DocumentTab(DocumentSession session)
    {
        Session = session;
        _title = session.DisplayName;
        _isDirty = session.IsDirty;
    }

    public DocumentSession Session { get; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool IsDirty { get => _isDirty; set => Set(ref _isDirty, value); }
    public string FilePath => Session.FilePath;
    public bool IsVirtual => Session.IsVirtual;
    public bool IsPdf => Session.IsPdf;
    public bool CanAnnotate => Session.CanAnnotate;

    // View & navigation state preserved across tab switches
    public ViewState? ViewState { get; set; }
    public IReadOnlyList<ThumbnailItem> Thumbnails { get; set; } = [];
    public int ThumbnailRotation { get; set; }
    public IReadOnlyList<OutlineNode> Outline { get; set; } = [];
    public IReadOnlyList<OutlineNode> OutlineFlat { get; set; } = [];
    public OutlineNode? CurrentChapter { get; set; }

    // Search state preserved across tab switches
    public string SearchQuery { get; set; } = "";
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public List<SearchResultItem> SearchResults { get; set; } = [];
    public string SearchStatus { get; set; } = "";
    public string? CompletedSearchKey { get; set; }
    public int CurrentHitIndex { get; set; } = -1;
    public IReadOnlyDictionary<int, IReadOnlyList<TextMatch>>? SearchHits { get; set; }

    // Annotations state preserved across tab switches
    public List<AnnotationItem> Annotations { get; set; } = [];
    public string AnnotationsStatus { get; set; } = "";
    public bool AnnotationsStale { get; set; } = true;

    // Sidebar state preserved across tab switches
    public SidebarPanel SelectedPanel { get; set; } = SidebarPanel.Thumbnails;
    public bool IsSidebarOpen { get; set; } = true;

    // Scan banner state preserved across tab switches
    public bool BannerDismissed { get; set; }
}
