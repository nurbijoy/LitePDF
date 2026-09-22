using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LitePdf.App.Documents;
using LitePdf.Core.Export;

namespace LitePdf.App.Views;

/// <summary>Collects what to convert and what to keep, then hands back the settings for the export.</summary>
public sealed class ExportDocxDialog : DialogWindow
{
    private readonly int _pageCount;
    private readonly TextBox _rangeBox;
    private readonly RadioButton _allPages;
    private readonly RadioButton _currentPage;
    private readonly RadioButton _customRange;
    private readonly TextBlock _error;
    private readonly Dictionary<string, CheckBox> _keep = [];
    private readonly CheckBox _recognize;
    private readonly int _currentPageIndex;

    internal ExportDocxDialog(Window? owner, int pageCount, int currentPage, bool canRecognize)
        : base(owner, "Convert to Word")
    {
        _pageCount = Math.Max(1, pageCount);
        _currentPageIndex = Math.Clamp(currentPage, 0, _pageCount - 1);

        var body = new StackPanel { Width = 460 };
        body.Children.Add(Heading("Convert to Word"));
        body.Children.Add(Paragraph(
            "Creates an editable .docx with the text, styling and pictures of this document. " +
            "A PDF has no paragraphs of its own, so the layout is rebuilt from the page and will not match " +
            "it exactly — complex tables and multi-column pages are the hardest to get right.", 460));

        var pages = Section(body, "Pages");
        _allPages = Radio(pages, "range", $"All {_pageCount} pages", true);
        _currentPage = Radio(pages, "range", $"Current page ({_currentPageIndex + 1})", false);

        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _customRange = new RadioButton { GroupName = "range", Content = "Pages", VerticalAlignment = VerticalAlignment.Center };
        _rangeBox = new TextBox { Width = 180, Margin = new Thickness(10, 0, 0, 0), Text = $"1-{_pageCount}" };
        _rangeBox.GotKeyboardFocus += (_, _) => _customRange.IsChecked = true;
        customRow.Children.Add(_customRange);
        customRow.Children.Add(_rangeBox);
        pages.Children.Add(customRow);
        pages.Children.Add(Paragraph("For example 1-3, 8, 12-14", 400));

        var keep = Section(body, "Keep");
        var keepRow = new WrapPanel();
        keep.Children.Add(keepRow);
        Keep(keepRow, "images", "Pictures", true);
        Keep(keepRow, "drawings", "Drawings", true);
        Keep(keepRow, "headings", "Headings", true);
        Keep(keepRow, "lists", "Lists", true);
        Keep(keepRow, "tables", "Tables", true);
        Keep(keepRow, "running", "Headers and footers", true);
        Keep(keepRow, "links", "Links", true);
        Keep(keepRow, "annotations", "Highlights and notes", true);

        var layout = Section(body, "Rebuilding the layout");
        Option(layout, "tags", "Use the document's own tags when it has them", true,
            "A tagged PDF states what its headings, lists and tables are, which beats every guess.");
        Option(layout, "unruled", "Also rebuild tables that draw no lines", false,
            "Off by default: finding a table where there is only aligned text turns readable paragraphs into a mangled grid.");
        Option(layout, "fonts", "Embed the fonts used in the document", false,
            "Makes the file much larger, and most PDFs store only the letters they printed, so typing new text in Word can show empty boxes.");

        var text = Section(body, "Scanned pages");
        _recognize = new CheckBox
        {
            Content = "Recognize text on pages that have none",
            IsChecked = canRecognize,
            IsEnabled = canRecognize,
        };
        text.Children.Add(_recognize);
        text.Children.Add(Paragraph(canRecognize
            ? "Reads scanned pages with on-device recognition so they convert to editable text instead of a picture. This makes the conversion slower."
            : "Text recognition is not available on this device, so scanned pages are kept as pictures.", 460));

        _error = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource("Brush.Danger"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        body.Children.Add(_error);

        // The options outgrew a short screen. The window is capped to the work area and the body scrolls
        // inside it, so the Convert button can never end up below the bottom of the display — which is
        // what a dialog that sizes itself to its content does the moment the content grows.
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 48);
        var scroller = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        SetBody(scroller,
            MakeButton("Convert", accent: true, isDefault: true, onClick: Accept),
            MakeButton("Cancel", isCancel: true, onClick: () => DialogResult = false));
    }

    internal DocxExportSettings? Result { get; private set; }

    private StackPanel Section(Panel parent, string title)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        parent.Children.Add(panel);
        return panel;
    }

    private static RadioButton Radio(Panel parent, string group, string label, bool isChecked)
    {
        var radio = new RadioButton { GroupName = group, Content = label, IsChecked = isChecked, Margin = new Thickness(0, 4, 0, 0) };
        parent.Children.Add(radio);
        return radio;
    }

    private void Keep(Panel parent, string key, string label, bool isChecked)
    {
        var box = new CheckBox { Content = label, IsChecked = isChecked, Margin = new Thickness(0, 4, 20, 4) };
        _keep[key] = box;
        parent.Children.Add(box);
    }

    /// <summary>A choice whose consequence is not obvious from its name, so it carries the reason with it.</summary>
    private void Option(Panel parent, string key, string label, bool isChecked, string note)
    {
        Keep(parent, key, label, isChecked);
        var text = Paragraph(note, 430);
        text.Margin = new Thickness(26, -2, 0, 6);
        parent.Children.Add(text);
    }

    private bool On(string key) => _keep[key].IsChecked == true;

    private void Accept()
    {
        List<int> pages;
        if (_currentPage.IsChecked == true)
        {
            pages = [_currentPageIndex];
        }
        else if (_customRange.IsChecked == true)
        {
            if (!PrintDialogWindow.TryParsePageRange(_rangeBox.Text, _pageCount, out pages, out string? error))
            {
                _error.Text = error;
                _error.Visibility = Visibility.Visible;
                _rangeBox.Focus();
                return;
            }
        }
        else
        {
            pages = [.. Enumerable.Range(0, _pageCount)];
        }

        Result = new DocxExportSettings
        {
            Pages = pages,
            RecognizeScans = _recognize.IsChecked == true && _recognize.IsEnabled,
            Options = new ExportOptions
            {
                Images = On("images"),
                Drawings = On("drawings"),
                Headings = On("headings"),
                Lists = On("lists"),
                Tables = On("tables"),
                HeadersFooters = On("running"),
                Hyperlinks = On("links"),
                Annotations = On("annotations"),
                UseTags = On("tags"),
                UnruledTables = On("unruled"),
                EmbedFonts = On("fonts"),
            },
        };
        DialogResult = true;
    }

    public static DocxExportSettings? Show(Window? owner, int pageCount, int currentPage, bool canRecognize)
    {
        var dialog = new ExportDocxDialog(owner, pageCount, currentPage, canRecognize);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
