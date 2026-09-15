using LitePdf.Core;

namespace LitePdf.App.ViewModels;

public sealed class OutlineNodeViewModel : ObservableObject
{
    public string Title { get; }
    public int PageIndex { get; }
    public IReadOnlyList<OutlineNodeViewModel> Children { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public OutlineNodeViewModel(OutlineItem item)
    {
        Title = string.IsNullOrWhiteSpace(item.Title) ? "(Untitled)" : item.Title;
        PageIndex = item.PageIndex;
        Children = item.Children.Select(c => new OutlineNodeViewModel(c)).ToList();
    }

    public IEnumerable<OutlineNodeViewModel> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var sub in child.Flatten())
                yield return sub;
        }
    }
}
