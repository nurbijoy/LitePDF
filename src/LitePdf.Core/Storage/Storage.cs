using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LitePdf.Core.Layout;

namespace LitePdf.Core.Storage;

public static class AppPaths
{
    private static string? _root;

    /// <summary>Data folder; overridable for tests.</summary>
    public static string Root
    {
        get => _root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LitePDF");
        set => _root = value;
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string RecentFile => Path.Combine(Root, "recent.json");
    public static string OcrDirectory => Path.Combine(Root, "ocr");
    public static string LogDirectory => Path.Combine(Root, "logs");
}

/// <summary>JSON persistence with atomic replace, so a crash never leaves a half-written file.</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Environment.ProcessId + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, value, Options);
        File.Move(temp, path, overwrite: true);
    }
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum ZoomMode
{
    Custom,
    FitWidth,
    FitPage,
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public PageColorMode PageColorMode { get; set; } = PageColorMode.Normal;
    public ZoomMode DefaultZoomMode { get; set; } = ZoomMode.FitWidth;
    public string? OcrLanguage { get; set; }
    public bool RestoreLastPosition { get; set; } = true;
    public bool SidebarOpen { get; set; } = true;
    public double SidebarWidth { get; set; } = 260;
    public string SidebarPanel { get; set; } = "Thumbnails";
    public string HighlightColor { get; set; } = AnnotationColor.Yellow.ToHex();
    public WindowPlacement? Window { get; set; }

    public static AppSettings Load() => JsonFile.Read<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();

    public void Save() => JsonFile.Write(AppPaths.SettingsFile, this);
}

public sealed class RecentFile
{
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset LastOpened { get; set; }
    public int PageIndex { get; set; }
    public double? PageOffset { get; set; }
    public double Zoom { get; set; } = 1;
    public ZoomMode ZoomMode { get; set; } = ZoomMode.FitWidth;
    public int Rotation { get; set; }
    public PageLayoutMode LayoutMode { get; set; } = PageLayoutMode.SinglePage;
}

/// <summary>Most-recently-used documents with the last reading position of each.</summary>
public sealed class RecentFileStore
{
    public const int MaxEntries = 30;
    private readonly List<RecentFile> _items;

    private RecentFileStore(List<RecentFile> items) => _items = items;

    public IReadOnlyList<RecentFile> Items => _items;

    public static RecentFileStore Load() =>
        new((JsonFile.Read<List<RecentFile>>(AppPaths.RecentFile) ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Path)).ToList());

    public RecentFile? Find(string path) =>
        _items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Upsert(RecentFile entry)
    {
        _items.RemoveAll(i => string.Equals(i.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, entry);
        if (_items.Count > MaxEntries) _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
    }

    public void Remove(string path) =>
        _items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Save() => JsonFile.Write(AppPaths.RecentFile, _items);
}

public static class DocumentKey
{
    /// <summary>
    /// Key for per-document caches (OCR). Uses the PDF's permanent /ID when present (stable across saves and
    /// renames), otherwise a hash of the file's size and first/last 64 KB.
    /// </summary>
    public static string Compute(string path, string? fileIdentifier, int pageCount)
    {
        using var sha = SHA256.Create();
        void Add(byte[] data) => sha.TransformBlock(data, 0, data.Length, null, 0);

        Add(BitConverter.GetBytes(pageCount));
        if (!string.IsNullOrEmpty(fileIdentifier))
        {
            Add(Encoding.UTF8.GetBytes("id:" + fileIdentifier));
        }
        else
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            Add(BitConverter.GetBytes(length));
            var buffer = new byte[(int)Math.Min(65536, length)];
            fs.ReadExactly(buffer);
            Add(buffer);
            if (length > buffer.Length)
            {
                fs.Seek(-buffer.Length, SeekOrigin.End);
                fs.ReadExactly(buffer);
                Add(buffer);
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..32].ToLowerInvariant();
    }
}
