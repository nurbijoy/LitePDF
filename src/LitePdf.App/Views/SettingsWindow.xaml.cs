using System.Windows;
using LitePdf.Core.Storage;

namespace LitePdf.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var settings = SettingsStore.LoadSettings();
        ThemeBox.Text = settings.Theme;
        PageModeBox.Text = settings.PageMode;
        ZoomBox.Text = settings.DefaultZoom;
        OcrLangBox.Text = settings.OcrLanguage ?? "";

        // Populate OCR languages if available
        try
        {
            var ocr = new Ocr.WindowsOcrEngine();
            foreach (var lang in ocr.AvailableLanguages)
                OcrLangBox.Items.Add(lang);
        }
        catch { }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsStore.LoadSettings();
        settings.Theme = ThemeBox.Text;
        settings.PageMode = PageModeBox.Text;
        settings.DefaultZoom = ZoomBox.Text;
        settings.OcrLanguage = string.IsNullOrWhiteSpace(OcrLangBox.Text) ? null : OcrLangBox.Text;
        SettingsStore.SaveSettings(settings);
        DialogResult = true;
        Close();
    }
}
