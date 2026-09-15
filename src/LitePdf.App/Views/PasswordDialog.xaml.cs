using System.Windows;

namespace LitePdf.App.Views;

public partial class PasswordDialog : Window
{
    public string Password { get; private set; } = string.Empty;

    public PasswordDialog()
    {
        InitializeComponent();
        PasswordBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordBox.Text;
        DialogResult = true;
        Close();
    }
}
