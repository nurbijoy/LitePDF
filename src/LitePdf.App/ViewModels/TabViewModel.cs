namespace LitePdf.App.ViewModels;

public sealed class TabViewModel : ObservableObject
{
    public MainViewModel DocumentViewModel { get; }
    public string Title => DocumentViewModel.FileName;

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public TabViewModel(MainViewModel vm)
    {
        DocumentViewModel = vm;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.FileName))
                OnPropertyChanged(nameof(Title));
        };
    }
}

public sealed class TabsViewModel : ObservableObject
{
    public System.Collections.ObjectModel.ObservableCollection<TabViewModel> Tabs { get; } = new();

    private TabViewModel? _selectedTab;
    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab != null) _selectedTab.IsActive = false;
            SetProperty(ref _selectedTab, value);
            if (_selectedTab != null) _selectedTab.IsActive = true;
        }
    }

    public void AddTab(MainViewModel vm)
    {
        var tab = new TabViewModel(vm);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    public void CloseTab(TabViewModel tab)
    {
        Tabs.Remove(tab);
        tab.DocumentViewModel.CloseDocument();
        if (Tabs.Count > 0) SelectedTab = Tabs[^1];
    }
}
