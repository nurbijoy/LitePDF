using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using LitePdf.Core.Storage;
using Microsoft.Win32;

namespace LitePdf.App.Infrastructure;

/// <summary>
/// What the application calls itself. Taken from the assembly, so the project file is the single place the
/// name and version are set. This is the display name only: the folder under %LocalAppData% that holds
/// settings and the OCR cache is <see cref="Core.Storage.AppPaths"/>'s own constant, and renaming the app
/// must not send it looking somewhere new.
/// </summary>
public static class AppInfo
{
    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    public static string Name { get; } =
        Self.GetCustomAttribute<AssemblyProductAttribute>()?.Product is { Length: > 0 } product
            ? product
            : "LitePDF";

    public static string Version { get; } = Self.GetName().Version?.ToString(3) ?? "1.0";
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(Exception ex, string context) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                string path = Path.Combine(AppPaths.LogDirectory, "litepdf.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, Path.Combine(AppPaths.LogDirectory, "litepdf.old.log"), overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Central place for unexpected errors: log, then show a friendly message.</summary>
public static class ErrorReporter
{
    private static bool _reporting;

    public static void Report(Exception ex, string? userMessage = null)
    {
        if (ex is OperationCanceledException) return;
        Log.Error(ex, userMessage ?? "Unhandled");

        // Never re-enter: a failure while showing the error must not trigger another report.
        if (_reporting) return;
        _reporting = true;
        string message = userMessage is null ? ex.Message : $"{userMessage}\n\n{ex.Message}";
        try
        {
            var owner = Application.Current?.MainWindow;
            if (owner is { IsLoaded: true })
                Views.MessageDialog.Show(owner, "Something went wrong", message, Views.MessageDialogButtons.Ok, isError: true);
            else
                MessageBox.Show(message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception dialogError)
        {
            Log.Error(dialogError, "Showing error dialog");
            try
            {
                MessageBox.Show(message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception)
            {
                // Nothing more we can do; the error is already logged.
            }
        }
        finally
        {
            _reporting = false;
        }
    }
}

public static class ThemeManager
{
    private static AppTheme _requested = AppTheme.System;
    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }

    public static event Action? ThemeChanged;

    public static void Initialize(AppTheme theme)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _requested == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_requested));
        };
        Apply(theme);
    }

    public static void Apply(AppTheme theme)
    {
        _requested = theme;
        bool dark = theme == AppTheme.Dark || (theme == AppTheme.System && SystemUsesDarkTheme());
        if (_current is not null && dark == IsDark) return;

        var dictionary = new ResourceDictionary { Source = new Uri($"/LitePDF;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;
        // Later merged dictionaries win lookups, so every palette (including the default one from App.xaml) must be
        // removed before inserting the new one ahead of the styles.
        foreach (var palette in merged.Where(IsPalette).ToList()) merged.Remove(palette);
        merged.Insert(0, dictionary);
        _current = dictionary;
        IsDark = dark;

        foreach (Window window in Application.Current.Windows) NativeWindow.ApplyTitleBarTheme(window);
        ThemeChanged?.Invoke();
    }

    private static bool IsPalette(ResourceDictionary dictionary) =>
        ReferenceEquals(dictionary, _current) ||
        dictionary.Source?.OriginalString is { } source &&
        (source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) || source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase));

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public static partial class NativeWindow
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>Call from a window's constructor: keeps the title bar in sync with the app theme.</summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBarTheme(window);
    }

    public static void ApplyTitleBarTheme(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        int value = ThemeManager.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Left margin for a TreeViewItem header based on its depth, so selection highlights span the full row.</summary>
public sealed class TreeIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int depth = 0;
        for (DependencyObject? d = value as DependencyObject; d is not null and not TreeView; d = VisualTreeHelper.GetParent(d))
            if (d is TreeViewItem) depth++;
        return new Thickness(Math.Max(0, depth - 1) * 16, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Small thread-safe LRU cache.</summary>
public sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _list = new();

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_map)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_map)
        {
            if (_map.Remove(key, out var existing)) _list.Remove(existing);
            _map[key] = _list.AddFirst((key, value));
            while (_map.Count > capacity)
            {
                var last = _list.Last!;
                _list.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Remove(TKey key)
    {
        lock (_map)
        {
            if (_map.Remove(key, out var node)) _list.Remove(node);
        }
    }

    public void Clear()
    {
        lock (_map)
        {
            _map.Clear();
            _list.Clear();
        }
    }
}

public static class Icons
{
    public const string Menu = "";
    public const string Open = "";
    public const string Save = "";
    public const string SaveAs = "";
    public const string Print = "";
    public const string Up = "";
    public const string Down = "";
    public const string ZoomOut = "";
    public const string ZoomIn = "";
    public const string Search = "";
    public const string More = "";
    public const string Settings = "";
    public const string Highlight = "";
    public const string Copy = "";
    public const string Region = "";
    public const string Rotate = "";
    public const string FullScreen = "";
    public const string ExitFullScreen = "";
    public const string Info = "";
    public const string Delete = "";
    public const string Close = "";
    public const string Thumbnails = "";
    public const string Chapters = "";
    public const string Note = "";
    public const string Picture = "";
    public const string Paste = "";
    public const string Recent = "";
    public const string Warning = "";
    public const string Check = "";
    public const string Moon = "";
    public const string Sun = "";
    public const string Speaker = "";
    public const string Stop = "";
    public const string Document = "";
    public const string TwoPage = "";
    public const string FitPage = "";
    public const string Ocr = "";
    public const string TextSelect = "";
    public const string Hand = "";
    public const string Underline = "";
    public const string Link = "";
    public const string Export = "";
}
