using System.Windows;

namespace LitePdf.App.Views;

public partial class PropertiesDialog : Window
{
    public PropertiesDialog()
    {
        InitializeComponent();
    }

    public void SetProperties(string fileName, int pageCount, LitePdf.Core.PageSize pageSize, string docKey)
    {
        FileNameText.Text = fileName;
        PageCountText.Text = pageCount.ToString();
        PageSizeText.Text = $"{pageSize.Width:0.##} x {pageSize.Height:0.##} pts ({pageSize.Width/72:0.##} x {pageSize.Height/72:0.##} in)";
        DocKeyText.Text = docKey;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
