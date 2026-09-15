using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LitePdf.Core;

namespace LitePdf.Ocr;

public sealed class OcrCache
{
    private sealed class CacheFile
    {
        public int Version { get; set; } = 1;
        public string Engine { get; set; } = "windows";
        public Dictionary<string, CachedPage> Pages { get; set; } = new();
    }

    private sealed class CachedPage
    {
        public string Lang { get; set; } = string.Empty;
        public int W { get; set; }
        public int H { get; set; }
        public List<CachedLine> Lines { get; set; } = new();
    }

    private sealed class CachedLine
    {
        public List<CachedWord> Words { get; set; } = new();
    }

    private sealed class CachedWord
    {
        public string T { get; set; } = string.Empty;
        public double[] R { get; set; } = Array.Empty<double>(); // [l,t,r,b]
    }

    private readonly string _filePath;
    private CacheFile _cache;
    private readonly object _lock = new();

    public OcrCache(string docKey)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LitePDF", "ocr");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, $"{Sanitize(docKey)}.json");
        _cache = Load();
    }

    private CacheFile Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<CacheFile>(json) ?? new CacheFile();
            }
        }
        catch { }
        return new CacheFile();
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(_filePath, json);
            }
            catch { }
        }
    }

    public bool TryGet(int pageIndex, out OcrPageResult? result)
    {
        lock (_lock)
        {
            if (_cache.Pages.TryGetValue(pageIndex.ToString(), out var cp))
            {
                var lines = cp.Lines.Select(l =>
                {
                    var words = l.Words.Select(w =>
                    {
                        var r = w.R;
                        RectD rect = r.Length >= 4 ? new RectD(r[0], r[1], r[2], r[3]) : new RectD(0, 0, 0, 0);
                        return new OcrWord(w.T, rect);
                    }).ToList();
                    string text = string.Join(" ", words.Select(x => x.Text));
                    return new OcrLine(text, words);
                }).ToList();

                result = new OcrPageResult(cp.Lang, null, lines);
                return true;
            }
        }
        result = null;
        return false;
    }

    public void Set(int pageIndex, OcrPageResult result, int w, int h)
    {
        lock (_lock)
        {
            var cp = new CachedPage
            {
                Lang = result.LanguageTag,
                W = w,
                H = h,
                Lines = result.Lines.Select(l => new CachedLine
                {
                    Words = l.Words.Select(wd => new CachedWord
                    {
                        T = wd.Text,
                        R = new[] { wd.PixelRect.Left, wd.PixelRect.Top, wd.PixelRect.Right, wd.PixelRect.Bottom }
                    }).ToList()
                }).ToList()
            };
            _cache.Pages[pageIndex.ToString()] = cp;
        }
        Save();
    }

    public static string ComputeDocKey(string filePath, byte[]? fileId = null)
    {
        if (fileId != null && fileId.Length > 0)
        {
            return Convert.ToHexString(fileId).ToLowerInvariant();
        }
        try
        {
            var info = new FileInfo(filePath);
            long len = info.Length;
            byte[] head = new byte[Math.Min(65536, len)];
            byte[] tail = new byte[Math.Min(65536, len)];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.ReadExactly(head);
                if (len > head.Length)
                {
                    fs.Seek(-tail.Length, SeekOrigin.End);
                    fs.ReadExactly(tail);
                }
            }
            using var sha = SHA256.Create();
            sha.TransformBlock(BitConverter.GetBytes(len), 0, 8, null, 0);
            sha.TransformBlock(head, 0, head.Length, null, 0);
            sha.TransformFinalBlock(tail, 0, tail.Length);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filePath))).ToLowerInvariant().Substring(0, 16);
        }
    }

    private static string Sanitize(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (invalid.Contains(c)) sb.Append('_');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
