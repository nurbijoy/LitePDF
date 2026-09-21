using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitePdf.App.Infrastructure;
using LitePdf.Core;
using LitePdf.Core.Storage;

namespace LitePdf.App.Views;

/// <summary>Base for themed dialogs: body on top, right-aligned buttons in a footer.</summary>
public abstract class DialogWindow : Window
{
    protected DialogWindow(Window? owner, string title)
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = title;
        if (owner is { IsLoaded: true })
            Owner = owner;
        else
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        NativeWindow.Attach(this);
    }

    protected void SetBody(UIElement body, params Button[] buttons)
    {
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            button.MinWidth = 96;
            buttonRow.Children.Add(button);
        }
        var footer = new Border { Style = (Style)FindResource("DialogFooter"), Child = buttonRow };
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new Border { Padding = new Thickness(24, 20, 24, 20), Child = body });
        Content = root;
    }

    protected static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };

    protected static TextBlock Paragraph(string text, double maxWidth = 440) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth, Foreground = (Brush)Application.Current.FindResource("Brush.TextSecondary") };

    protected Button MakeButton(string text, bool accent = false, bool isDefault = false, bool isCancel = false, Action? onClick = null)
    {
        var button = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel };
        if (accent) button.Style = (Style)FindResource("AccentButton");
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }
}

public enum MessageDialogButtons
{
    Ok,
    OkCancel,
    SaveDiscardCancel,
}

public enum MessageDialogResult
{
    Cancel,
    Ok,
    Save,
    Discard,
}

public sealed class MessageDialog : DialogWindow
{
    private MessageDialogResult _result = MessageDialogResult.Cancel;

    internal MessageDialog(Window? owner, string title, string message, MessageDialogButtons buttons, bool isError, string? okText)
        : base(owner, title)
    {
        var icon = new TextBlock
        {
            Text = isError ? Icons.Warning : Icons.Info,
            Style = (Style)FindResource("Icon"),
            FontSize = 26,
            Foreground = (Brush)FindResource(isError ? "Brush.Danger" : "Brush.Accent"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 16, 0),
        };
        var text = new StackPanel();
        text.Children.Add(Heading(title));
        text.Children.Add(Paragraph(message));
        var body = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(text);

        Button Close(string label, MessageDialogResult result, bool accent = false, bool isDefault = false, bool isCancel = false) =>
            MakeButton(label, accent, isDefault, isCancel, () => { _result = result; DialogResult = result != MessageDialogResult.Cancel; });

        switch (buttons)
        {
            case MessageDialogButtons.OkCancel:
                SetBody(body, Close(okText ?? "OK", MessageDialogResult.Ok, accent: true, isDefault: true), Close("Cancel", MessageDialogResult.Cancel, isCancel: true));
                break;
            case MessageDialogButtons.SaveDiscardCancel:
                SetBody(body, Close("Save", MessageDialogResult.Save, accent: true, isDefault: true), Close("Don't save", MessageDialogResult.Discard), Close("Cancel", MessageDialogResult.Cancel, isCancel: true));
                break;
            default:
                SetBody(body, Close(okText ?? "OK", MessageDialogResult.Ok, accent: true, isDefault: true, isCancel: true));
                break;
        }
    }

    public static MessageDialogResult Show(Window? owner, string title, string message, MessageDialogButtons buttons = MessageDialogButtons.Ok,
        bool isError = false, string? okText = null)
    {
        var dialog = new MessageDialog(owner, title, message, buttons, isError, okText);
        dialog.ShowDialog();
        return dialog._result;
    }
}

public sealed class PasswordDialog : DialogWindow
{
    private readonly PasswordBox _box = new() { Width = 320, Margin = new Thickness(0, 14, 0, 0) };

    internal PasswordDialog(Window? owner, string fileName, bool retry) : base(owner, "Password required")
    {
        var body = new StackPanel();
        body.Children.Add(Heading("Password required"));
        body.Children.Add(Paragraph($"“{fileName}” is protected. Enter the password to open it."));
        body.Children.Add(_box);
        if (retry)
            body.Children.Add(new TextBlock { Text = "Incorrect password. Try again.", Foreground = (Brush)FindResource("Brush.Danger"), Margin = new Thickness(0, 8, 0, 0) });
        SetBody(body, MakeButton("Open", accent: true, isDefault: true, onClick: () => DialogResult = true), MakeButton("Cancel", isCancel: true));
        Loaded += (_, _) => _box.Focus();
    }

    public static string? Prompt(Window? owner, string fileName, bool retry)
    {
        var dialog = new PasswordDialog(owner, fileName, retry);
        return dialog.ShowDialog() == true ? dialog._box.Password : null;
    }
}

public sealed class TextInputDialog : DialogWindow
{
    private readonly TextBox _box;
    private bool _deleteRequested;

    internal TextInputDialog(Window? owner, string title, string? message, string text, string acceptText, bool multiline, bool allowDelete)
        : base(owner, title)
    {
        _box = new TextBox
        {
            Text = text,
            Width = 420,
            Margin = new Thickness(0, 12, 0, 0),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 120 : 30,
            MaxHeight = 320,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var body = new StackPanel();
        body.Children.Add(Heading(title));
        if (message is not null) body.Children.Add(Paragraph(message));
        body.Children.Add(_box);

        var buttons = new List<Button>();
        if (allowDelete) buttons.Add(MakeButton("Delete", onClick: () => { _deleteRequested = true; DialogResult = true; }));
        buttons.Add(MakeButton(acceptText, accent: true, isDefault: !multiline, onClick: () => DialogResult = true));
        buttons.Add(MakeButton("Cancel", isCancel: true));
        SetBody(body, buttons.ToArray());
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (multiline && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DialogResult = true;
                e.Handled = true;
            }
        };
    }

    /// <summary>Returns the text, or null when cancelled. <paramref name="deleted"/> is true if the user chose Delete.</summary>
    public static string? Prompt(Window? owner, string title, string? message, string text, string acceptText, bool multiline, bool allowDelete, out bool deleted)
    {
        var dialog = new TextInputDialog(owner, title, message, text, acceptText, multiline, allowDelete);
        bool ok = dialog.ShowDialog() == true;
        deleted = ok && dialog._deleteRequested;
        return ok ? dialog._box.Text : null;
    }
}

public sealed class OcrResultDialog : DialogWindow
{
    internal OcrResultDialog(Window? owner, string text, string languageTag, Action? copyImage) : base(owner, "Recognized text")
    {
        var box = new TextBox
        {
            Text = text,
            Width = 520,
            Height = 280,
            Margin = new Thickness(0, 12, 0, 0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var body = new StackPanel();
        body.Children.Add(Heading("Recognized text"));
        body.Children.Add(Paragraph(string.IsNullOrWhiteSpace(text)
            ? "No text was found in the selected area."
            : $"Recognized on this device ({CultureName(languageTag)}). You can edit the text before copying."));
        body.Children.Add(box);

        var buttons = new List<Button>
        {
            MakeButton("Copy text", accent: true, isDefault: true, onClick: () =>
            {
                string value = box.SelectionLength > 0 ? box.SelectedText : box.Text;
                if (ClipboardHelper.TrySetText(value)) DialogResult = true;
            }),
        };
        if (copyImage is not null) buttons.Add(MakeButton("Copy image", onClick: () => { copyImage(); DialogResult = true; }));
        buttons.Add(MakeButton("Close", isCancel: true));
        SetBody(body, buttons.ToArray());
        Loaded += (_, _) => box.Focus();
    }

    public static void Show(Window? owner, string text, string languageTag, Action? copyImage) =>
        new OcrResultDialog(owner, text, languageTag, copyImage).ShowDialog();

    internal static string CultureName(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }
}

public sealed class PropertiesDialog : DialogWindow
{
    internal PropertiesDialog(Window? owner, DocumentInfo info, PageSize firstPage) : base(owner, "Document properties")
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = (Brush)FindResource("Brush.TextSecondary"), Margin = new Thickness(0, 5, 24, 5) };
            var v = new TextBox
            {
                Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
                MinHeight = 0, Margin = new Thickness(0, 5, 0, 5), TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
            };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
        }

        Row("File name", Path.GetFileName(info.FilePath));
        Row("Location", Path.GetDirectoryName(info.FilePath));
        Row("File size", FormatSize(info.FileSize));
        Row("Pages", info.PageCount.ToString("N0"));
        Row("Page size", $"{firstPage.Width / 72 * 25.4:0} × {firstPage.Height / 72 * 25.4:0} mm ({firstPage.Width / 72:0.##} × {firstPage.Height / 72:0.##} in)");
        Row("PDF version", info.PdfVersion);
        Row("Encrypted", info.IsEncrypted ? "Yes" : null);
        Row("Title", info.Title);
        Row("Author", info.Author);
        Row("Subject", info.Subject);
        Row("Keywords", info.Keywords);
        Row("Created", info.Created?.LocalDateTime.ToString("f"));
        Row("Modified", info.Modified?.LocalDateTime.ToString("f"));
        Row("Application", info.Creator);
        Row("PDF producer", info.Producer);

        var body = new StackPanel();
        body.Children.Add(Heading("Document properties"));
        body.Children.Add(grid);
        SetBody(body, MakeButton("Close", accent: true, isDefault: true, isCancel: true));
    }

    public static void Show(Window? owner, DocumentInfo info, PageSize firstPage) => new PropertiesDialog(owner, info, firstPage).ShowDialog();

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}

public sealed class SettingsDialog : DialogWindow
{
    internal SettingsDialog(Window? owner, AppSettings settings, IReadOnlyList<string> ocrLanguages, Action clearOcrCache) : base(owner, "Settings")
    {
        var body = new StackPanel { Width = 460 };
        body.Children.Add(Heading("Settings"));

        StackPanel Section(string title)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0, 0, 0, 6) });
            body.Children.Add(panel);
            return panel;
        }

        void Radio<T>(Panel parent, string group, string label, T value, T current, Action<T> apply)
        {
            var radio = new RadioButton
            {
                Content = label,
                GroupName = group,
                IsChecked = EqualityComparer<T>.Default.Equals(value, current),
                Margin = new Thickness(0, 4, 20, 4),
            };
            radio.Checked += (_, _) => apply(value);
            parent.Children.Add(radio);
        }

        var theme = Section("App theme");
        var themeRow = new WrapPanel();
        theme.Children.Add(themeRow);
        foreach (var (label, value) in new[] { ("Use system setting", AppTheme.System), ("Light", AppTheme.Light), ("Dark", AppTheme.Dark) })
            Radio(themeRow, "theme", label, value, settings.Theme, v => { settings.Theme = v; ThemeManager.Apply(v); });

        var zoom = Section("When opening a document");
        var zoomRow = new WrapPanel();
        zoom.Children.Add(zoomRow);
        foreach (var (label, value) in new[] { ("Fit width", ZoomMode.FitWidth), ("Fit page", ZoomMode.FitPage), ("Actual size", ZoomMode.Custom) })
            Radio(zoomRow, "zoom", label, value, settings.DefaultZoomMode, v => settings.DefaultZoomMode = v);
        var restore = new CheckBox { Content = "Reopen documents where I left off", IsChecked = settings.RestoreLastPosition };
        restore.Click += (_, _) => settings.RestoreLastPosition = restore.IsChecked == true;
        zoom.Children.Add(restore);

        var ocr = Section("Text recognition (OCR)");
        var combo = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 6) };
        combo.Items.Add(new ComboBoxItem { Content = "Automatic (Windows language)", Tag = null });
        foreach (var tag in ocrLanguages) combo.Items.Add(new ComboBoxItem { Content = OcrResultDialog.CultureName(tag), Tag = tag });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, settings.OcrLanguage, StringComparison.OrdinalIgnoreCase)) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) => settings.OcrLanguage = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        ocr.Children.Add(combo);
        ocr.Children.Add(Paragraph(ocrLanguages.Count == 0
            ? "No recognition languages are installed. Add a language in Windows Settings › Time & language › Language & region, including its optical character recognition feature."
            : "Recognition runs entirely on this device. To add languages, install them in Windows Settings › Time & language › Language & region.", 460));

        var advanced = new CheckBox
        {
            Content = "Analyse page layout while recognizing",
            IsChecked = settings.OcrAdvancedLayout,
            Margin = new Thickness(0, 8, 0, 0),
        };
        advanced.Click += (_, _) => settings.OcrAdvancedLayout = advanced.IsChecked == true;
        ocr.Children.Add(advanced);
        ocr.Children.Add(Paragraph("Cleans up scans before reading them, rebuilds stacked fractions, exponents " +
            "and degree signs, marks diagrams, and puts lines back in reading order. Turn off for the plain " +
            "output of the Windows recognizer.", 460));

        var clear = MakeButton("Clear recognized text cache", onClick: () =>
        {
            clearOcrCache();
            MessageDialog.Show(this, "Cache cleared", "Recognized text for all documents was removed from this device.");
        });
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        clear.Margin = new Thickness(0, 10, 0, 0);
        ocr.Children.Add(clear);

        var about = Section("About");
        about.Children.Add(Paragraph($"{AppInfo.Name} {AppInfo.Version} · PDF rendering by PDFium · No data leaves your device."));

        SetBody(body, MakeButton("Done", accent: true, isDefault: true, isCancel: true, onClick: () => DialogResult = true));
    }

    public static void Show(Window? owner, AppSettings settings, IReadOnlyList<string> ocrLanguages, Action clearOcrCache) =>
        new SettingsDialog(owner, settings, ocrLanguages, clearOcrCache).ShowDialog();
}

/// <summary>Template root for thumbnails: reports realization so the window can render or release the image.</summary>
public sealed class ThumbnailHost : StackPanel
{
    public static event Action<ViewModels.ThumbnailItem, bool>? RealizationChanged;

    public ThumbnailHost()
    {
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.ThumbnailItem old) RealizationChanged?.Invoke(old, false);
            if (e.NewValue is ViewModels.ThumbnailItem item && IsLoaded) RealizationChanged?.Invoke(item, true);
        };
        Loaded += (_, _) => { if (DataContext is ViewModels.ThumbnailItem item) RealizationChanged?.Invoke(item, true); };
        Unloaded += (_, _) => { if (DataContext is ViewModels.ThumbnailItem item) RealizationChanged?.Invoke(item, false); };
    }
}

/// <summary>Constructs (without showing) every dialog for the --self-test startup mode.</summary>
internal static class MessageDialogProbe
{
    public static void Create() => new MessageDialog(null, "Test", "Message", MessageDialogButtons.SaveDiscardCancel, isError: true, okText: null).Close();

    public static void CreateAll()
    {
        new PasswordDialog(null, "file.pdf", retry: true).Close();
        new TextInputDialog(null, "Note", "Message", "Text", "Save", multiline: true, allowDelete: true).Close();
        new OcrResultDialog(null, "Recognized", "en-US", () => { }).Close();
        new PropertiesDialog(null, new DocumentInfo("C:\\a.pdf", 1234, 3, "1.7", true, "T", "A", "S", "K", "C", "P", DateTimeOffset.Now, DateTimeOffset.Now), new PageSize(612, 792)).Close();
        new SettingsDialog(null, new AppSettings(), ["en-US"], () => { }).Close();
        new PrintDialogWindow(null, null, 0, "Test").Close();
        new ExportDocxDialog(null, 12, 0, canRecognize: true).Close();
    }
}
