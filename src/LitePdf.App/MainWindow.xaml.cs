using System.ComponentModel;
using System.IO;
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
using LitePdf.App.Views;
using LitePdf.Core;
using LitePdf.Core.Layout;
using LitePdf.Core.Storage;
using LitePdf.Ocr;

namespace LitePdf.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly AppSettings _settings;
    private readonly RecentFileStore _recent = RecentFileStore.Load();
    private readonly WindowsOcrEngine _ocr = new();
    private readonly string? _startupFile;
    private readonly DispatcherTimer _toastTimer;
    private DocumentSession? _session;
    private DocumentTab? _activeTab;
    private bool _allowClose;
    private WindowState _stateBeforeFullScreen;

    public MainWindow(AppSettings settings, string? startupFile)
    {
        _settings = settings;
        _startupFile = startupFile;
        InitializeComponent();
        DataContext = _vm;
        NativeWindow.Attach(this);
        RestoreWindowPlacement();

        _vm.IsSidebarOpen = settings.SidebarOpen;
        _vm.SelectedPanel = Enum.TryParse<SidebarPanel>(settings.SidebarPanel, out var panel) ? panel : SidebarPanel.Thumbnails;
        _vm.HighlightColor = AnnotationColor.Palette.FirstOrDefault(p => p.Color.ToHex() == settings.HighlightColor).Color is { R: > 0 } c ? c : AnnotationColor.Yellow;
        Viewer.SetColorMode(settings.PageColorMode);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _vm.IsToastVisible = false;
        };
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDebounce.Tick += (_, _) => StartSearch(jumpToFirst: false);

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        WireViewer();
        ThumbnailHost.RealizationChanged += OnThumbnailRealizationChanged;
        LoadRecentList();
        UpdateSidebarVisibility();

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += OnDragEnter;
        DragOver += OnDragEnter;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_startupFile is not null) await RunAsync(() => OpenDocumentAsync(_startupFile));
    }

    private void WireViewer()
    {
        Viewer.CurrentPageChanged += OnCurrentPageChanged;
        Viewer.ZoomChanged += OnZoomChanged;
        Viewer.LinkClicked += OnLinkClicked;
        Viewer.LinkHovered += link => _vm.StatusText = link is null ? "" : link.Uri ?? $"Go to page {link.Target.PageIndex + 1}";
        Viewer.RegionSelected += OnRegionSelected;
        Viewer.ContextRequested += OnViewerContextRequested;
        Viewer.NoteActivated += annotation => Run(() => EditNoteAsync(annotation));
        Viewer.PageTextLoaded += (page, _) =>
        {
            if (page == Viewer.CurrentPageIndex) Run(UpdateScanBannerAsync);
        };
        Viewer.DeleteRequested += () => Run(DeleteSelectedAnnotationAsync);
    }

    private void OnCurrentPageChanged()
    {
        int page = Viewer.CurrentPageIndex;
        _vm.CurrentPageNumber = page + 1;
        if (!PageBox.IsKeyboardFocused) PageBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        SyncThumbnailSelection(page);
        UpdateCurrentChapter(page);
        Run(UpdateScanBannerAsync);
    }

    private int _thumbnailRotation;

    private void OnZoomChanged()
    {
        _vm.ZoomText = $"{Viewer.Zoom * 100:0}%";
        if (_session is not null && Viewer.Rotation != _thumbnailRotation) BuildThumbnails();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsSidebarOpen):
            case nameof(MainViewModel.IsFullScreen):
                UpdateSidebarVisibility();
                break;
            case nameof(MainViewModel.SelectedPanel):
                OnPanelChanged();
                break;
            case nameof(MainViewModel.Tool):
                Viewer.Tool = _vm.Tool;
                break;
            case nameof(MainViewModel.SearchQuery):
                _searchDebounce.Stop();
                _searchDebounce.Start();
                break;
        }
    }

    // ---- Keyboard ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool inTextInput = Keyboard.FocusedElement is TextBoxBase or PasswordBox;
        bool hasDocument = _session is not null;

        void Handle(Action action)
        {
            e.Handled = true;
            action();
        }

        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.T: Handle(() => Run(OpenFileDialogAsync)); return;
                case Key.O: Handle(() => Run(OpenFileDialogAsync)); return;
                case Key.S when hasDocument: Handle(() => Run(() => SaveAsync(saveAs: shift))); return;
                case Key.P when hasDocument: Handle(() => Run(PrintAsync)); return;
                case Key.W when hasDocument: Handle(() => Run(() => CloseTabAsync(_activeTab))); return;
                case Key.F when hasDocument: Handle(ShowSearch); return;
                case Key.G when hasDocument: Handle(() => { PageBox.Focus(); PageBox.SelectAll(); }); return;
                case Key.B when hasDocument: Handle(() => _vm.IsSidebarOpen = !_vm.IsSidebarOpen); return;
                case Key.D when hasDocument: Handle(() => Run(ShowPropertiesAsync)); return;
                case Key.V when !inTextInput: Handle(() => Run(PasteImageAsync)); return;
                case Key.Tab when _vm.Tabs.Count > 1: Handle(() => SwitchTab(shift ? -1 : 1)); return;
            }

            if (hasDocument && !inTextInput)
            {
                switch (key)
                {
                    case Key.C when Viewer.Selection is not null: Handle(() => Run(CopySelectionAsync)); return;
                    case Key.H: Handle(() => Run(() => MarkSelectionAsync(AnnotationKind.Highlight))); return;
                    case Key.U: Handle(() => Run(() => MarkSelectionAsync(AnnotationKind.Underline))); return;
                    case Key.R: Handle(() => RotateView(shift ? -1 : 1)); return;
                    case Key.OemPlus or Key.Add: Handle(Viewer.ZoomIn); return;
                    case Key.OemMinus or Key.Subtract: Handle(Viewer.ZoomOut); return;
                    case Key.D0 or Key.NumPad0: Handle(() => Viewer.SetZoom(1)); return;
                    case Key.D1 or Key.NumPad1: Handle(() => Viewer.SetZoomMode(ZoomMode.FitWidth)); return;
                    case Key.D2 or Key.NumPad2: Handle(() => Viewer.SetZoomMode(ZoomMode.FitPage)); return;
                }
            }
            return;
        }

        if (alt) return; // leave Alt combinations (Alt+F4, menu access keys) to the system

        switch (key)
        {
            case Key.F3 when hasDocument: Handle(() => MoveToHit(shift ? -1 : 1)); return;
            case Key.F4 when hasDocument && !shift: Handle(() => _vm.IsSidebarOpen = !_vm.IsSidebarOpen); return;
            case Key.F11 when hasDocument && !shift: Handle(ToggleFullScreen); return;
            case Key.Escape when _vm.IsFullScreen: Handle(ToggleFullScreen); return;
        }

        if (hasDocument && !inTextInput && Keyboard.Modifiers == ModifierKeys.None && Viewer.IsKeyboardFocusWithin)
        {
            switch (key)
            {
                case Key.V: Handle(() => _vm.Tool = ViewerTool.Select); return;
                case Key.H: Handle(() => _vm.Tool = ViewerTool.Hand); return;
                case Key.R: Handle(() => _vm.Tool = ViewerTool.Region); return;
            }
        }
    }

    // ---- Command bar ----

    private void Open_Click(object sender, RoutedEventArgs e) => Run(OpenFileDialogAsync);
    private void Save_Click(object sender, RoutedEventArgs e) => Run(() => SaveAsync(saveAs: false));
    private void Print_Click(object sender, RoutedEventArgs e) => Run(PrintAsync);
    private void PreviousPage_Click(object sender, RoutedEventArgs e) => Viewer.GoToPage(Viewer.CurrentPageIndex - (Viewer.Layout?.PagesPerRow ?? 1));
    private void NextPage_Click(object sender, RoutedEventArgs e) => Viewer.GoToPage(Viewer.CurrentPageIndex + (Viewer.Layout?.PagesPerRow ?? 1));
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Viewer.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Viewer.ZoomOut();
    private void Search_Click(object sender, RoutedEventArgs e) => ShowSearch();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Highlight_Click(object sender, RoutedEventArgs e) => Run(() => MarkSelectionAsync(AnnotationKind.Highlight));

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (int.TryParse(PageBox.Text.Trim(), out int number) && _session is not null)
            {
                Viewer.GoToPage(Math.Clamp(number, 1, _session.PageCount) - 1);
                Viewer.Focus();
            }
            PageBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            PageBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Viewer.Focus();
        }
    }

    private void PageBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        PageBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

    private void SelectAllOnFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) Dispatcher.BeginInvoke(box.SelectAll, DispatcherPriority.Input);
    }

    private void ZoomMenu_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<object?>();
        foreach (int percent in new[] { 50, 75, 100, 125, 150, 200, 300, 400 })
            items.Add(MenuItemFor($"{percent}%", null, () => Viewer.SetZoom(percent / 100.0),
                isChecked: Viewer.ZoomMode == ZoomMode.Custom && Math.Abs(Viewer.Zoom * 100 - percent) < 0.5));
        items.Add(null);
        items.Add(MenuItemFor("Fit width", null, () => Viewer.SetZoomMode(ZoomMode.FitWidth), "Ctrl+1", isChecked: Viewer.ZoomMode == ZoomMode.FitWidth));
        items.Add(MenuItemFor("Fit page", Icons.FitPage, () => Viewer.SetZoomMode(ZoomMode.FitPage), "Ctrl+2", isChecked: Viewer.ZoomMode == ZoomMode.FitPage));
        OpenMenu(items, ZoomMenuButton);
    }

    private void MarkupMenu_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<object?>();
        foreach (var (name, color) in AnnotationColor.Palette)
            items.Add(MenuItemFor(name, null, () => { _vm.HighlightColor = color; Run(() => MarkSelectionAsync(AnnotationKind.Highlight, onlyIfSelection: true)); },
                isChecked: _vm.HighlightColor == color, iconElement: ColorSwatch(color)));
        items.Add(null);
        items.Add(MenuItemFor("Highlight", Icons.Highlight, () => Run(() => MarkSelectionAsync(AnnotationKind.Highlight)), "Ctrl+H"));
        items.Add(MenuItemFor("Underline", Icons.Underline, () => Run(() => MarkSelectionAsync(AnnotationKind.Underline)), "Ctrl+U"));
        items.Add(MenuItemFor("Strikethrough", null, () => Run(() => MarkSelectionAsync(AnnotationKind.StrikeOut))));
        items.Add(null);
        items.Add(MenuItemFor("Add note…", Icons.Note, () => Run(AddNoteAtSelectionAsync)));
        OpenMenu(items, MarkupMenuButton);
    }

    private void OcrMenu_Click(object sender, RoutedEventArgs e)
    {
        string language = _settings.OcrLanguage is { } tag ? OcrResultDialog.CultureName(tag) : "Automatic";
        var items = new List<object?>
        {
            MenuItemFor("Recognize this page", Icons.Ocr, () => Run(() => RecognizePagesAsync([Viewer.CurrentPageIndex]))),
            MenuItemFor("Recognize all pages", null, () => Run(() => RecognizePagesAsync(null))),
            MenuItemFor("Select an area…", Icons.Region, () => { _vm.Tool = ViewerTool.Region; ShowToast("Drag over the area you want to copy or recognize"); }, "R"),
            null,
            MenuItemFor($"Language: {language}", Icons.Settings, ShowSettings),
        };
        OpenMenu(items, OcrMenuButton);
    }

    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        bool hasDocument = _session is not null;
        var colorModes = new MenuItem { Header = "Page color", IsEnabled = hasDocument, Icon = IconText(Icons.Moon) };
        foreach (var (label, mode) in new[] { ("Normal", PageColorMode.Normal), ("Dark", PageColorMode.Dark), ("Sepia", PageColorMode.Sepia) })
            colorModes.Items.Add(MenuItemFor(label, null, () => SetPageColorMode(mode), isChecked: Viewer.ColorMode == mode));

        var items = new List<object?>
        {
            MenuItemFor("Save as…", Icons.SaveAs, () => Run(() => SaveAsync(saveAs: true)), "Ctrl+Shift+S", _session?.IsPdf == true),
            MenuItemFor("Export annotations…", Icons.Export, () => Run(ExportAnnotationsAsync), null, _session?.CanAnnotate == true),
            null,
            colorModes,
            MenuItemFor("Two-page view", Icons.TwoPage, ToggleTwoPage, null, hasDocument, Viewer.LayoutMode == PageLayoutMode.TwoPage),
            MenuItemFor("Rotate clockwise", Icons.Rotate, () => RotateView(1), "Ctrl+R", hasDocument),
            MenuItemFor("Rotate counterclockwise", null, () => RotateView(-1), "Ctrl+Shift+R", hasDocument),
            MenuItemFor(_vm.IsFullScreen ? "Exit full screen" : "Full screen", Icons.FullScreen, ToggleFullScreen, "F11", hasDocument),
            null,
            MenuItemFor(_vm.IsReadingAloud ? "Stop reading" : "Read aloud", _vm.IsReadingAloud ? Icons.Stop : Icons.Speaker, () => Run(ToggleReadAloudAsync), null, hasDocument),
            MenuItemFor("Document properties", Icons.Info, () => Run(ShowPropertiesAsync), "Ctrl+D", hasDocument),
            null,
            MenuItemFor("Settings", Icons.Settings, ShowSettings),
            MenuItemFor("Close tab", Icons.Close, () => Run(() => CloseTabAsync(_activeTab)), "Ctrl+W", hasDocument),
        };
        if (_vm.Tabs.Count > 1)
            items.Add(MenuItemFor("Close all tabs", null, () => Run(CloseAllTabsWithPromptAsync)));
        OpenMenu(items, MoreMenuButton);
    }

    private void RailButton_Click(object sender, RoutedEventArgs e) => _vm.IsSidebarOpen = true;

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (SidebarColumn.ActualWidth < 150)
        {
            _vm.IsSidebarOpen = false;
            return;
        }
        _settings.SidebarWidth = SidebarColumn.ActualWidth;
    }

    // ---- View commands ----

    private void RotateView(int quarterTurns)
    {
        if (_session is null) return;
        Viewer.Rotate(quarterTurns);
        BuildThumbnails();
    }

    private void ToggleTwoPage() =>
        Viewer.SetLayoutMode(Viewer.LayoutMode == PageLayoutMode.TwoPage ? PageLayoutMode.SinglePage : PageLayoutMode.TwoPage);

    private void SetPageColorMode(PageColorMode mode)
    {
        Viewer.SetColorMode(mode);
        _settings.PageColorMode = mode;
        RefreshAllThumbnails();
    }

    private void ToggleFullScreen()
    {
        _vm.IsFullScreen = !_vm.IsFullScreen;
        bool full = _vm.IsFullScreen;
        TabRow.Height = full ? new GridLength(0) : GridLength.Auto;
        CommandRow.Height = full ? new GridLength(0) : GridLength.Auto;
        StatusRow.Height = full ? new GridLength(0) : GridLength.Auto;
        RailColumn.Width = full ? new GridLength(0) : GridLength.Auto;
        if (full)
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // re-maximize to cover the taskbar
            WindowState = WindowState.Maximized;
            ShowToast("Press F11 or Esc to exit full screen");
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
        Viewer.Focus();
    }

    private void UpdateSidebarVisibility()
    {
        bool show = _vm.HasDocument && _vm.IsSidebarOpen && !_vm.IsFullScreen;
        Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = Sidebar.Visibility;
        SidebarColumn.Width = show ? new GridLength(Math.Clamp(_settings.SidebarWidth, 180, 560)) : new GridLength(0);
        SidebarColumn.MinWidth = show ? 120 : 0;
        if (show) OnPanelChanged();
    }

    private void OnPanelChanged()
    {
        if (!_vm.IsSidebarOpen || _session is null) return;
        switch (_vm.SelectedPanel)
        {
            case SidebarPanel.Search:
                Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
                break;
            case SidebarPanel.Annotations:
                if (_annotationsStale) RefreshAnnotations();
                break;
            case SidebarPanel.Thumbnails:
                SyncThumbnailSelection(Viewer.CurrentPageIndex);
                break;
        }
    }

    // ---- Drag & drop ----

    private static string? GetDroppedFile(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ImageDocument.IsImagePath(f))
            : null;

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        bool ok = GetDroppedFile(e) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (GetDroppedFile(e) is { } file)
        {
            Activate();
            Run(() => OpenDocumentAsync(file));
        }
    }

    // ---- Closing & persistence ----

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        try
        {
            if (_vm.Tabs.Count > 1)
            {
                var result = MessageDialog.Show(this, "Close all tabs?",
                    $"You have {_vm.Tabs.Count} open tabs. Do you want to close all tabs?",
                    MessageDialogButtons.OkCancel, okText: "Close all tabs");
                if (result != MessageDialogResult.Ok) return;
            }

            foreach (var tab in _vm.Tabs.ToList())
            {
                if (tab.IsDirty)
                {
                    SelectTab(tab);
                    var result = MessageDialog.Show(this, "Save your changes?",
                        $"“{tab.Title}” has annotations that haven't been saved.",
                        MessageDialogButtons.SaveDiscardCancel);
                    if (result == MessageDialogResult.Cancel) return;
                    if (result == MessageDialogResult.Save)
                    {
                        if (!await SaveTabAsync(tab, saveAs: false)) return;
                    }
                }
            }

            SaveWindowSettings();

            foreach (var tab in _vm.Tabs.ToList())
            {
                SaveReadingPosition(tab);
                await tab.Session.DisposeAsync();
            }
            _vm.Tabs.Clear();
            await CloseSessionAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Closing");
        }
        _allowClose = true;
        _speech?.Dispose();
        // The awaits above may complete synchronously, i.e. still inside Closing; close on the next dispatcher turn.
        await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }

    private void RestoreWindowPlacement()
    {
        if (_settings.Window is { } w && w.Width >= MinWidth && w.Height >= MinHeight &&
            w.Left + 80 > SystemParameters.VirtualScreenLeft && w.Top + 40 > SystemParameters.VirtualScreenTop &&
            w.Left + 80 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            w.Top + 40 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = w.Left;
            Top = w.Top;
            Width = w.Width;
            Height = w.Height;
            if (w.IsMaximized) SourceInitialized += (_, _) => WindowState = WindowState.Maximized;
        }
        else
        {
            // First run: a comfortable size that never extends under the taskbar.
            var area = SystemParameters.WorkArea;
            Width = Math.Min(Width, area.Width * 0.9);
            Height = Math.Min(Height, area.Height * 0.92);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void SaveWindowSettings()
    {
        var bounds = RestoreBounds;
        if (!bounds.IsEmpty && !_vm.IsFullScreen)
            _settings.Window = new WindowPlacement { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, IsMaximized = WindowState == WindowState.Maximized };
        _settings.SidebarOpen = _vm.IsSidebarOpen;
        _settings.SidebarPanel = _vm.SelectedPanel.ToString();
        _settings.HighlightColor = _vm.HighlightColor.ToHex();
        _settings.PageColorMode = Viewer.ColorMode;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving settings");
        }
    }

    private void ShowSettings()
    {
        SettingsDialog.Show(this, _settings, _ocr.AvailableLanguages, () =>
        {
            try
            {
                if (Directory.Exists(AppPaths.OcrDirectory)) Directory.Delete(AppPaths.OcrDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Clearing OCR cache");
            }
        });
        SaveSettings();
    }

    // ---- Helpers ----

    private void ShowToast(string message)
    {
        _vm.ToastText = message;
        _vm.IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Fire-and-forget for UI actions: errors are logged and shown, cancellation is ignored.</summary>
    private async void Run(Func<Task> action) => await RunAsync(action);

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex);
        }
    }

    private static TextBlock IconText(string glyph) =>
        new() { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"), FontSize = 15 };

    private static FrameworkElement ColorSwatch(AnnotationColor color) =>
        new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = AnnotationItem.ToBrush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) };

    private static MenuItem MenuItemFor(string header, string? icon, Action onClick, string? gesture = null, bool isEnabled = true,
        bool isChecked = false, FrameworkElement? iconElement = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = isEnabled, IsChecked = isChecked };
        if (iconElement is not null) item.Icon = iconElement;
        else if (icon is not null) item.Icon = IconText(icon);
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OpenMenu(IEnumerable<object?> items, FrameworkElement? anchor = null)
    {
        var menu = new ContextMenu();
        foreach (var item in items)
        {
            if (item is null)
            {
                if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator());
            }
            else
            {
                menu.Items.Add(item);
            }
        }
        if (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        if (anchor is not null)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
        }
        else
        {
            menu.Placement = PlacementMode.MousePoint;
        }
        menu.IsOpen = true;
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            for (var el = dep; el is not null and not Border; el = VisualTreeHelper.GetParent(el))
                if (el is Button) return; // clicked close button
        }
        if ((sender as FrameworkElement)?.DataContext is DocumentTab tab)
            SelectTab(tab);
    }

    private void TabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is DocumentTab tab)
        {
            e.Handled = true;
            Run(() => CloseTabAsync(tab));
        }
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is DocumentTab tab)
            Run(() => CloseTabAsync(tab));
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => Run(OpenFileDialogAsync);

    private void TabContextClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentTab tab)
            Run(() => CloseTabAsync(tab));
    }

    private void TabContextCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentTab tab)
            Run(() => CloseOtherTabsAsync(tab));
    }

    private void TabContextCloseAll_Click(object sender, RoutedEventArgs e) => Run(CloseAllTabsWithPromptAsync);
}
