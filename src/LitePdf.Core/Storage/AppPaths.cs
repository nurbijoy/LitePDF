using System.Text.Json;

namespace LitePdf.Core.Storage;

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LitePDF");

    public static string SettingsPath => Path.Combine(Root, "settings.json");
    public static string RecentPath => Path.Combine(Root, "recent.json");
    public static string ThumbsDir => Path.Combine(Root, "thumbs");
    public static string OcrDir => Path.Combine(Root, "ocr");

    public static void EnsureExists()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ThumbsDir);
        Directory.CreateDirectory(OcrDir);
    }
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "system"; // system, light, dark
    public string PageMode { get; set; } = "normal"; // normal, dark, sepia
    public string DefaultZoom { get; set; } = "fitWidth";
    public string? OcrLanguage { get; set; }
    public bool SidebarVisible { get; set; } = true;
    public double PageMargin { get; set; } = 12;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool IsMaximized { get; set; } = false;
}

public sealed class RecentFileEntry
{
    public string Path { get; set; } = string.Empty;
    public string DocKey { get; set; } = string.Empty;
    public int Page { get; set; }
    public double Zoom { get; set; } = 1.0;
    public DateTime Opened { get; set; } = DateTime.UtcNow;
    public List<BookmarkEntry> Bookmarks { get; set; } = new();
}

public sealed class BookmarkEntry
{
    public string Title { get; set; } = string.Empty;
    public int Page { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                var json = File.ReadAllText(AppPaths.SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureExists();
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(AppPaths.SettingsPath, json);
        }
        catch { }
    }

    public static List<RecentFileEntry> LoadRecent()
    {
        try
        {
            if (File.Exists(AppPaths.RecentPath))
            {
                var json = File.ReadAllText(AppPaths.RecentPath);
                return JsonSerializer.Deserialize<List<RecentFileEntry>>(json) ?? new List<RecentFileEntry>();
            }
        }
        catch { }
        return new List<RecentFileEntry>();
    }

    public static void SaveRecent(List<RecentFileEntry> recent)
    {
        try
        {
            AppPaths.EnsureExists();
            // Keep max 20, sorted by opened desc
            var trimmed = recent.OrderByDescending(r => r.Opened).Take(20).ToList();
            var json = JsonSerializer.Serialize(trimmed, JsonOpts);
            File.WriteAllText(AppPaths.RecentPath, json);
        }
        catch { }
    }

    public static void AddRecent(string path, string docKey, int page, double zoom)
    {
        var recent = LoadRecent();
        recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        recent.Add(new RecentFileEntry { Path = path, DocKey = docKey, Page = page, Zoom = zoom, Opened = DateTime.UtcNow });
        SaveRecent(recent);
    }
}
