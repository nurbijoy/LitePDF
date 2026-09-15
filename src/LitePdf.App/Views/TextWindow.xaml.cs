using System.Windows;

namespace LitePdf.App.Views;

public partial class TextWindow : Window
{
    public TextWindow()
    {
        InitializeComponent();
    }

    public void SetText(string text, string title)
    {
        Title = title;
        TextBox.Text = text;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TextBox.Text))
            Clipboard.SetText(TextBox.Text);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
