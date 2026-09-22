using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LitePdf.App.Documents;
using LitePdf.Core.Export;

namespace LitePdf.App.Views;

/// <summary>
/// Asks which pages to convert, and nothing else.
///
/// Everything the conversion can do — pictures, headings, lists, tables, running heads, links, notes, the
/// document's own tags where it has them — it should simply do, and do well. A row of switches for each of
/// them is a way of asking the reader to debug the converter, and the honest answers to "keep pictures?"
/// and "use the tags?" are both yes. The two that genuinely trade one risk for another — guessing at
/// tables nobody drew, and embedding fonts — stay off, where they belong.
/// </summary>
public sealed class ExportDocxDialog : DialogWindow
{
    private readonly int _pageCount;
    private readonly TextBox _rangeBox;
    private readonly RadioButton _currentPage;
    private readonly RadioButton _customRange;
    private readonly TextBlock _error;
    private readonly int _currentPageIndex;

    internal ExportDocxDialog(Window? owner, int pageCount, int currentPage)
        : base(owner, "Convert to Word")
    {
        _pageCount = Math.Max(1, pageCount);
        _currentPageIndex = Math.Clamp(currentPage, 0, _pageCount - 1);

        var body = new StackPanel { Width = 430 };
        body.Children.Add(Heading("Convert to Word"));
        body.Children.Add(Paragraph(
            "Converts text PDFs to an editable Word document, keeping text, styling and illustrations. " +
            "Layout is rebuilt and may differ from the PDF. Scanned PDFs and image files are not supported.", 430));

        var pages = Section(body, "Pages");
        Radio(pages, "range", $"All {_pageCount} pages", true);
        _currentPage = Radio(pages, "range", $"Current page ({_currentPageIndex + 1})", false);

        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _customRange = new RadioButton { GroupName = "range", Content = "Pages", VerticalAlignment = VerticalAlignment.Center };
        _rangeBox = new TextBox { Width = 180, Margin = new Thickness(10, 0, 0, 0), Text = $"1-{_pageCount}" };
        _rangeBox.GotKeyboardFocus += (_, _) => _customRange.IsChecked = true;
        customRow.Children.Add(_customRange);
        customRow.Children.Add(_rangeBox);
        pages.Children.Add(customRow);
        pages.Children.Add(Paragraph("For example 1-3, 8, 12-14", 400));

        _error = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource("Brush.Danger"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        body.Children.Add(_error);

        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 48);
        SetBody(body,
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
        };
        DialogResult = true;
    }

    public static DocxExportSettings? Show(Window? owner, int pageCount, int currentPage)
    {
        var dialog = new ExportDocxDialog(owner, pageCount, currentPage);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
