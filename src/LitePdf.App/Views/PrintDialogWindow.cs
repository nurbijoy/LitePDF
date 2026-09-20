using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitePdf.App.Documents;
using LitePdf.App.Infrastructure;
using LitePdf.Core;

namespace LitePdf.App.Views;

public enum PrintOrientation
{
    Auto,
    Portrait,
    Landscape
}

public enum PrintColorMode
{
    Color,
    Grayscale
}

public sealed class PrintRequest
{
    public required PrintQueue Queue { get; init; }
    public required PrintTicket Ticket { get; init; }
    public required IReadOnlyList<int> Pages { get; init; }
    public required PrintOrientation Orientation { get; init; }
    public required PrintColorMode ColorMode { get; init; }
    public PageMediaSize? MediaSize { get; init; }
}

/// <summary>Fluent-styled print dialog: printer selection, page range, duplex, orientation, color mode, and live preview.</summary>
public sealed class PrintDialogWindow : DialogWindow
{
    private readonly IPdfDocument? _document;
    private readonly int _currentPage;
    private readonly string _jobName;
    private readonly LocalPrintServer? _printServer;
    private readonly List<PrintQueue> _printers = [];

    private ComboBox _printerCombo = null!;
    private TextBlock _printerStatusText = null!;
    private RadioButton _radioAll = null!;
    private RadioButton _radioCurrent = null!;
    private RadioButton _radioPages = null!;
    private TextBox _customPagesBox = null!;
    private TextBlock _pagesSummaryText = null!;
    private TextBox _copiesBox = null!;
    private CheckBox _collateCheck = null!;
    private ComboBox _duplexCombo = null!;
    private ComboBox _paperSizeCombo = null!;
    private ComboBox _orientationCombo = null!;
    private ComboBox _colorCombo = null!;
    private Image _previewImage = null!;
    private TextBlock _previewPageLabel = null!;
    private Button _prevButton = null!;
    private Button _nextButton = null!;
    private Button _printButton = null!;

    private int _previewPageIndex;
    private List<int> _selectedPages = [];
    private CancellationTokenSource? _previewCts;
    private PrintRequest? _result;

    internal PrintDialogWindow(Window? owner, IPdfDocument? document, int currentPage, string jobName)
        : base(owner, "Print")
    {
        _document = document;
        _currentPage = Math.Max(0, Math.Min(document?.PageCount - 1 ?? 0, currentPage));
        _jobName = jobName;

        Width = 740;
        Height = 560;
        MinWidth = 680;
        MinHeight = 520;

        try
        {
            _printServer = new LocalPrintServer();
            var queues = _printServer.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
            foreach (var q in queues) _printers.Add(q);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Enumerating printers");
        }

        BuildUi();
        Closed += (_, _) =>
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _printServer?.Dispose();
        };
    }

    public static PrintRequest? Show(Window? owner, IPdfDocument document, int currentPage, string jobName)
    {
        var dialog = new PrintDialogWindow(owner, document, currentPage, jobName);
        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    private void BuildUi()
    {
        int totalPages = _document?.PageCount ?? 1;
        _selectedPages = Enumerable.Range(0, totalPages).ToList();

        var rootGrid = new Grid();
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        // Left column: Settings
        var leftScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 24, 0),
        };
        var settingsPanel = new StackPanel();
        leftScroll.Content = settingsPanel;

        settingsPanel.Children.Add(Heading("Print"));

        // 1. Printer
        var printerSection = CreateSection("Printer");
        _printerCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 4) };
        PrintQueue? defaultQueue = null;
        try { defaultQueue = _printServer?.DefaultPrintQueue; } catch { }

        int defaultIndex = 0;
        for (int i = 0; i < _printers.Count; i++)
        {
            var p = _printers[i];
            bool isDefault = defaultQueue is not null && string.Equals(p.FullName, defaultQueue.FullName, StringComparison.OrdinalIgnoreCase);
            _printerCombo.Items.Add(new ComboBoxItem
            {
                Content = isDefault ? $"{p.Name} (Default)" : p.Name,
                Tag = p,
            });
            if (isDefault) defaultIndex = i;
        }

        if (_printers.Count == 0)
        {
            _printerCombo.Items.Add(new ComboBoxItem { Content = "No printers found", IsEnabled = false });
        }

        _printerStatusText = new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 8),
            Text = _printers.Count == 0 ? "No printers installed on this device." : "Ready",
        };
        _printerCombo.SelectionChanged += (_, _) => OnPrinterChanged();
        printerSection.Children.Add(_printerCombo);
        printerSection.Children.Add(_printerStatusText);
        settingsPanel.Children.Add(printerSection);

        // 2. Pages
        var pagesSection = CreateSection("Pages");
        var pagesStack = new StackPanel();

        _radioAll = new RadioButton
        {
            Content = $"All pages (1–{totalPages})",
            GroupName = "PageRange",
            IsChecked = true,
            Margin = new Thickness(0, 2, 0, 4),
        };
        _radioAll.Checked += (_, _) =>
        {
            _selectedPages = Enumerable.Range(0, totalPages).ToList();
            _customPagesBox.IsEnabled = false;
            _pagesSummaryText.Text = totalPages == 1 ? "1 page" : $"{totalPages} pages";
            _pagesSummaryText.Foreground = (Brush)FindResource("Brush.TextSecondary");
            UpdatePrintButtonState();
            _previewPageIndex = 0;
            UpdatePreview();
        };
        pagesStack.Children.Add(_radioAll);

        _radioCurrent = new RadioButton
        {
            Content = $"Current page (Page {_currentPage + 1})",
            GroupName = "PageRange",
            Margin = new Thickness(0, 2, 0, 4),
        };
        _radioCurrent.Checked += (_, _) =>
        {
            _selectedPages = [_currentPage];
            _customPagesBox.IsEnabled = false;
            _pagesSummaryText.Text = $"Page {_currentPage + 1} of {totalPages}";
            _pagesSummaryText.Foreground = (Brush)FindResource("Brush.TextSecondary");
            UpdatePrintButtonState();
            _previewPageIndex = 0;
            UpdatePreview();
        };
        pagesStack.Children.Add(_radioCurrent);

        var customRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _radioPages = new RadioButton
        {
            Content = "Pages:",
            GroupName = "PageRange",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _customPagesBox = new TextBox
        {
            Margin = new Thickness(0),
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _customPagesBox.TextChanged += (_, _) => OnCustomPagesChanged(totalPages);
        _radioPages.Checked += (_, _) =>
        {
            _customPagesBox.IsEnabled = true;
            _customPagesBox.Focus();
            OnCustomPagesChanged(totalPages);
        };
        Grid.SetColumn(_radioPages, 0);
        Grid.SetColumn(_customPagesBox, 1);
        customRow.Children.Add(_radioPages);
        customRow.Children.Add(_customPagesBox);
        pagesStack.Children.Add(customRow);

        _pagesSummaryText = new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            Text = totalPages == 1 ? "1 page" : $"{totalPages} pages",
            Margin = new Thickness(24, 2, 0, 6),
        };
        pagesStack.Children.Add(_pagesSummaryText);
        pagesSection.Children.Add(pagesStack);
        settingsPanel.Children.Add(pagesSection);

        // 3. Copies
        var copiesSection = CreateSection("Copies");
        var copiesRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        _copiesBox = new TextBox { Width = 60, Text = "1", VerticalAlignment = VerticalAlignment.Center };
        _copiesBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        _copiesBox.TextChanged += (_, _) =>
        {
            int.TryParse(_copiesBox.Text, out int c);
            _collateCheck.IsEnabled = c > 1;
        };
        copiesRow.Children.Add(_copiesBox);

        _collateCheck = new CheckBox
        {
            Content = "Collate",
            IsChecked = true,
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        copiesRow.Children.Add(_collateCheck);
        copiesSection.Children.Add(copiesRow);
        settingsPanel.Children.Add(copiesSection);

        // 4. Duplex (Two-Sided)
        var duplexSection = CreateSection("Two-sided printing");
        _duplexCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 6) };
        _duplexCombo.Items.Add(new ComboBoxItem { Content = "Print on one side (Single-sided)", Tag = Duplexing.OneSided });
        _duplexCombo.Items.Add(new ComboBoxItem { Content = "Print on both sides (Flip on long edge)", Tag = Duplexing.TwoSidedLongEdge });
        _duplexCombo.Items.Add(new ComboBoxItem { Content = "Print on both sides (Flip on short edge)", Tag = Duplexing.TwoSidedShortEdge });
        _duplexCombo.SelectedIndex = 0;
        duplexSection.Children.Add(_duplexCombo);
        settingsPanel.Children.Add(duplexSection);

        // 5. Paper, Orientation & Color
        var layoutSection = CreateSection("Paper & Layout");
        var paperStack = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        paperStack.Children.Add(new TextBlock { Text = "Paper size", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 4) });
        _paperSizeCombo = new ComboBox();
        _paperSizeCombo.SelectionChanged += (_, _) => UpdatePreview();
        paperStack.Children.Add(_paperSizeCombo);
        layoutSection.Children.Add(paperStack);

        var layoutGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var orientationStack = new StackPanel();
        orientationStack.Children.Add(new TextBlock { Text = "Orientation", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 4) });
        _orientationCombo = new ComboBox();
        _orientationCombo.Items.Add(new ComboBoxItem { Content = "Auto (match document)", Tag = PrintOrientation.Auto });
        _orientationCombo.Items.Add(new ComboBoxItem { Content = "Portrait", Tag = PrintOrientation.Portrait });
        _orientationCombo.Items.Add(new ComboBoxItem { Content = "Landscape", Tag = PrintOrientation.Landscape });
        _orientationCombo.SelectedIndex = 0;
        _orientationCombo.SelectionChanged += (_, _) => UpdatePreview();
        orientationStack.Children.Add(_orientationCombo);
        Grid.SetColumn(orientationStack, 0);
        layoutGrid.Children.Add(orientationStack);

        var colorStack = new StackPanel();
        colorStack.Children.Add(new TextBlock { Text = "Color mode", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 4) });
        _colorCombo = new ComboBox();
        _colorCombo.Items.Add(new ComboBoxItem { Content = "Color", Tag = PrintColorMode.Color });
        _colorCombo.Items.Add(new ComboBoxItem { Content = "Grayscale", Tag = PrintColorMode.Grayscale });
        _colorCombo.SelectedIndex = 0;
        _colorCombo.SelectionChanged += (_, _) => UpdatePreview();
        colorStack.Children.Add(_colorCombo);
        Grid.SetColumn(colorStack, 2);
        layoutGrid.Children.Add(colorStack);

        layoutSection.Children.Add(layoutGrid);
        settingsPanel.Children.Add(layoutSection);

        Grid.SetColumn(leftScroll, 0);
        rootGrid.Children.Add(leftScroll);

        // Right column: Live Preview
        var rightPanel = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };

        var previewHeader = new TextBlock
        {
            Text = "Preview",
            Style = (Style)FindResource("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(previewHeader, Dock.Top);
        rightPanel.Children.Add(previewHeader);

        // Bottom pagination controls for preview
        var previewNav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        DockPanel.SetDock(previewNav, Dock.Bottom);

        _prevButton = new Button
        {
            Content = "◀",
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            ToolTip = "Previous page",
        };
        _prevButton.Click += (_, _) =>
        {
            if (_previewPageIndex > 0)
            {
                _previewPageIndex--;
                UpdatePreview();
            }
        };
        previewNav.Children.Add(_prevButton);

        _previewPageLabel = new TextBlock
        {
            Text = "1 of 1",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            Style = (Style)FindResource("Caption"),
        };
        previewNav.Children.Add(_previewPageLabel);

        _nextButton = new Button
        {
            Content = "▶",
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            ToolTip = "Next page",
        };
        _nextButton.Click += (_, _) =>
        {
            if (_previewPageIndex < _selectedPages.Count - 1)
            {
                _previewPageIndex++;
                UpdatePreview();
            }
        };
        previewNav.Children.Add(_nextButton);
        rightPanel.Children.Add(previewNav);

        // Preview image container with paper styling
        var previewBorder = new Border
        {
            Background = (Brush)FindResource("Brush.Surface"),
            BorderBrush = (Brush)FindResource("Brush.BorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
        };

        var paperCard = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _previewImage = new Image
        {
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.HighQuality);
        paperCard.Child = _previewImage;
        previewBorder.Child = paperCard;

        rightPanel.Children.Add(previewBorder);

        Grid.SetColumn(rightPanel, 1);
        rootGrid.Children.Add(rightPanel);

        // Footer buttons
        _printButton = MakeButton("Print", accent: true, isDefault: true, onClick: OnPrintConfirmed);
        var cancelButton = MakeButton("Cancel", isCancel: true);

        SetBody(rootGrid, _printButton, cancelButton);

        if (_printers.Count > 0)
        {
            _printerCombo.SelectedIndex = defaultIndex;
        }
        else
        {
            _printButton.IsEnabled = false;
        }

        UpdatePreview();
    }

    private static StackPanel CreateSection(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.FindResource("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        return panel;
    }

    private void OnPrinterChanged()
    {
        if (_printerCombo.SelectedItem is not ComboBoxItem { Tag: PrintQueue queue })
        {
            _printerStatusText.Text = "No printer selected";
            UpdatePrintButtonState();
            return;
        }

        try
        {
            _printerStatusText.Text = queue.IsOffline ? "Offline" : "Ready";

            PrintCapabilities? caps = null;
            try { caps = queue.GetPrintCapabilities(queue.DefaultPrintTicket); } catch { }

            // Populate paper sizes (A4 default)
            _paperSizeCombo.Items.Clear();
            int selectedPaperIndex = -1;
            int a4Index = -1;
            int letterIndex = -1;

            if (caps?.PageMediaSizeCapability is { Count: > 0 } sizes)
            {
                var defSize = queue.DefaultPrintTicket?.PageMediaSize;
                int idx = 0;
                foreach (var s in sizes)
                {
                    string label = GetPaperSizeDisplayName(s);
                    _paperSizeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = s });

                    if (s.PageMediaSizeName == PageMediaSizeName.ISOA4 ||
                        (s.Width is double w && s.Height is double h && Math.Abs(w - 793.7) < 15 && Math.Abs(h - 1122.5) < 15))
                    {
                        a4Index = idx;
                    }
                    else if (s.PageMediaSizeName == PageMediaSizeName.NorthAmericaLetter ||
                             (s.Width is double lw && s.Height is double lh && Math.Abs(lw - 816) < 15 && Math.Abs(lh - 1056) < 15))
                    {
                        letterIndex = idx;
                    }

                    if (defSize is not null && s.PageMediaSizeName == defSize.PageMediaSizeName)
                    {
                        selectedPaperIndex = idx;
                    }
                    idx++;
                }
            }

            if (_paperSizeCombo.Items.Count == 0)
            {
                _paperSizeCombo.Items.Add(new ComboBoxItem { Content = "A4 (210 × 297 mm)", Tag = new PageMediaSize(PageMediaSizeName.ISOA4) });
                _paperSizeCombo.Items.Add(new ComboBoxItem { Content = "Letter (8.5 × 11 in)", Tag = new PageMediaSize(PageMediaSizeName.NorthAmericaLetter) });
                a4Index = 0;
            }

            // Prefer A4 by default as requested
            if (a4Index >= 0) _paperSizeCombo.SelectedIndex = a4Index;
            else if (selectedPaperIndex >= 0) _paperSizeCombo.SelectedIndex = selectedPaperIndex;
            else if (letterIndex >= 0) _paperSizeCombo.SelectedIndex = letterIndex;
            else _paperSizeCombo.SelectedIndex = 0;

            // Check duplex capabilities
            bool supportsDuplex = caps?.DuplexingCapability.Any(d => d is Duplexing.TwoSidedLongEdge or Duplexing.TwoSidedShortEdge) == true;
            _duplexCombo.IsEnabled = supportsDuplex;
            if (!supportsDuplex)
            {
                _duplexCombo.SelectedIndex = 0;
                _printerStatusText.Text += " · Single-sided only";
            }
            else
            {
                // Prefer LongEdge if default ticket suggests duplex
                var defDup = queue.DefaultPrintTicket?.Duplexing;
                if (defDup == Duplexing.TwoSidedLongEdge) _duplexCombo.SelectedIndex = 1;
                else if (defDup == Duplexing.TwoSidedShortEdge) _duplexCombo.SelectedIndex = 2;
                else _duplexCombo.SelectedIndex = 0;
            }

            // Check color capabilities
            bool supportsColor = caps?.OutputColorCapability.Contains(OutputColor.Color) == true;
            if (!supportsColor)
            {
                _colorCombo.SelectedIndex = 1; // Grayscale
                _colorCombo.IsEnabled = false;
            }
            else
            {
                _colorCombo.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Updating printer capabilities");
        }

        UpdatePrintButtonState();
    }

    private static string GetPaperSizeDisplayName(PageMediaSize mediaSize)
    {
        if (mediaSize.PageMediaSizeName is { } name)
        {
            return name switch
            {
                PageMediaSizeName.ISOA4 => "A4 (210 × 297 mm)",
                PageMediaSizeName.NorthAmericaLetter => "Letter (8.5 × 11 in)",
                PageMediaSizeName.NorthAmericaLegal => "Legal (8.5 × 14 in)",
                PageMediaSizeName.ISOA3 => "A3 (297 × 420 mm)",
                PageMediaSizeName.ISOA5 => "A5 (148 × 210 mm)",
                PageMediaSizeName.NorthAmericaExecutive => "Executive (7.25 × 10.5 in)",
                PageMediaSizeName.NorthAmericaTabloid => "Tabloid (11 × 17 in)",
                _ => name.ToString().Replace("NorthAmerica", "").Replace("ISO", "")
            };
        }
        if (mediaSize.Width is double w && mediaSize.Height is double h && w > 0 && h > 0)
        {
            double inW = w / 96.0, inH = h / 96.0;
            return $"{inW:0.#} × {inH:0.#} in";
        }
        return "Custom";
    }

    private void OnCustomPagesChanged(int maxPage)
    {
        if (TryParsePageRange(_customPagesBox.Text, maxPage, out var pages, out var error))
        {
            _selectedPages = pages;
            _pagesSummaryText.Text = pages.Count == 1 ? "1 page selected" : $"{pages.Count} pages selected";
            _pagesSummaryText.Foreground = (Brush)FindResource("Brush.TextSecondary");
            _previewPageIndex = 0;
            UpdatePrintButtonState();
            UpdatePreview();
        }
        else
        {
            _selectedPages = [];
            _pagesSummaryText.Text = error ?? "Invalid page range";
            _pagesSummaryText.Foreground = Brushes.IndianRed;
            UpdatePrintButtonState();
            UpdatePreview();
        }
    }

    private void UpdatePrintButtonState()
    {
        bool hasPrinter = _printerCombo.SelectedItem is ComboBoxItem { Tag: PrintQueue };
        bool hasPages = _selectedPages.Count > 0;
        _printButton.IsEnabled = hasPrinter && hasPages;
    }

    private async void UpdatePreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        if (_selectedPages.Count == 0 || _document is null)
        {
            _previewImage.Source = null;
            _previewPageLabel.Text = "0 of 0";
            _prevButton.IsEnabled = false;
            _nextButton.IsEnabled = false;
            return;
        }

        _previewPageIndex = Math.Clamp(_previewPageIndex, 0, _selectedPages.Count - 1);
        int page = _selectedPages[_previewPageIndex];
        _previewPageLabel.Text = $"{_previewPageIndex + 1} of {_selectedPages.Count}";
        _prevButton.IsEnabled = _previewPageIndex > 0;
        _nextButton.IsEnabled = _previewPageIndex < _selectedPages.Count - 1;

        var orientation = (_orientationCombo.SelectedItem as ComboBoxItem)?.Tag as PrintOrientation? ?? PrintOrientation.Auto;
        var colorMode = (_colorCombo.SelectedItem as ComboBoxItem)?.Tag as PrintColorMode? ?? PrintColorMode.Color;

        int rotation = 0;
        if (orientation == PrintOrientation.Landscape) rotation = 1;
        else if (orientation == PrintOrientation.Portrait) rotation = 0;
        else
        {
            var sz = _document.PageSizes[page];
            rotation = sz.Width > sz.Height ? 1 : 0;
        }

        try
        {
            const int targetW = 400;
            const int targetH = 500;
            var rendered = await _document.RenderAsync(page, targetW, targetH, rotation, null, RenderFlags.Annotations, RenderPriority.Interactive, ct);
            if (ct.IsCancellationRequested) return;

            if (colorMode == PrintColorMode.Grayscale)
            {
                ApplyGrayscale(rendered.Pixels);
            }

            var bmp = PageRenderer.ToBitmapSource(rendered);
            _previewImage.Source = bmp;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Rendering print preview");
        }
    }

    private static void ApplyGrayscale(byte[] pixels)
    {
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            byte gray = (byte)((pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114) / 1000);
            pixels[i] = gray;
            pixels[i + 1] = gray;
            pixels[i + 2] = gray;
        }
    }

    private void OnPrintConfirmed()
    {
        if (_printerCombo.SelectedItem is not ComboBoxItem { Tag: PrintQueue queue }) return;
        if (_selectedPages.Count == 0) return;

        int copies = 1;
        if (int.TryParse(_copiesBox.Text, out int c) && c >= 1) copies = c;

        var duplex = (_duplexCombo.SelectedItem as ComboBoxItem)?.Tag as Duplexing? ?? Duplexing.OneSided;
        var orientation = (_orientationCombo.SelectedItem as ComboBoxItem)?.Tag as PrintOrientation? ?? PrintOrientation.Auto;
        var colorMode = (_colorCombo.SelectedItem as ComboBoxItem)?.Tag as PrintColorMode? ?? PrintColorMode.Color;
        var mediaSize = (_paperSizeCombo.SelectedItem as ComboBoxItem)?.Tag as PageMediaSize;

        var ticket = queue.UserPrintTicket?.Clone() ?? queue.DefaultPrintTicket?.Clone() ?? new PrintTicket();
        ticket.CopyCount = copies;
        ticket.Duplexing = duplex;
        ticket.Collation = _collateCheck.IsChecked == true ? Collation.Collated : Collation.Uncollated;
        ticket.OutputColor = colorMode == PrintColorMode.Grayscale ? OutputColor.Grayscale : OutputColor.Color;
        if (mediaSize is not null) ticket.PageMediaSize = mediaSize;

        if (orientation == PrintOrientation.Portrait) ticket.PageOrientation = PageOrientation.Portrait;
        else if (orientation == PrintOrientation.Landscape) ticket.PageOrientation = PageOrientation.Landscape;

        _result = new PrintRequest
        {
            Queue = queue,
            Ticket = ticket,
            Pages = _selectedPages.ToList(),
            Orientation = orientation,
            ColorMode = colorMode,
            MediaSize = mediaSize,
        };

        DialogResult = true;
    }

    public static bool TryParsePageRange(string input, int maxPage, out List<int> pages, out string? error)
    {
        pages = [];
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter page numbers (e.g. 1-3, 5)";
            return false;
        }

        var seen = new HashSet<int>();
        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.Contains('-'))
            {
                var rangeParts = part.Split('-', StringSplitOptions.TrimEntries);
                if (rangeParts.Length != 2 || !int.TryParse(rangeParts[0], out int start) || !int.TryParse(rangeParts[1], out int end))
                {
                    error = $"Invalid range: '{part}'";
                    return false;
                }
                if (start < 1 || end > maxPage || start > end)
                {
                    error = $"Page range must be between 1 and {maxPage}";
                    return false;
                }
                for (int p = start; p <= end; p++)
                {
                    if (seen.Add(p)) pages.Add(p - 1);
                }
            }
            else
            {
                if (!int.TryParse(part, out int page) || page < 1 || page > maxPage)
                {
                    error = $"Page '{part}' must be between 1 and {maxPage}";
                    return false;
                }
                if (seen.Add(page)) pages.Add(page - 1);
            }
        }

        if (pages.Count == 0)
        {
            error = "No pages selected";
            return false;
        }

        pages.Sort();
        return true;
    }
}
