using System.Text.Json;
using LitePdf.Core;
using LitePdf.Core.Storage;

namespace LitePdf.Ocr;

/// <summary>
/// OCR results on disk, one small JSON file per page under <c>{root}/{documentKey}/</c>, so recognizing a large
/// document never rewrites a growing file. Thread-safe.
/// </summary>
public sealed class OcrCache
{
    private const int FormatVersion = 3;
    private readonly string _directory;
    private readonly object _gate = new();
    private HashSet<int>? _pages;

    public OcrCache(string rootDirectory, string documentKey)
    {
        if (documentKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid document key.", nameof(documentKey));
        _directory = Path.Combine(rootDirectory, documentKey);
    }

    public IReadOnlySet<int> CachedPages
    {
        get
        {
            lock (_gate) return new HashSet<int>(LoadIndex());
        }
    }

    public bool Contains(int pageIndex)
    {
        lock (_gate) return LoadIndex().Contains(pageIndex);
    }

    public bool TryGet(int pageIndex, out OcrPageResult result)
    {
        result = null!;
        lock (_gate)
        {
            if (!LoadIndex().Contains(pageIndex)) return false;
        }

        var file = JsonFile.Read<CachedPage>(PagePath(pageIndex));
        if (file is null || file.Version != FormatVersion)
        {
            lock (_gate) _pages?.Remove(pageIndex);
            return false;
        }

        result = new OcrPageResult(
            file.Language,
            file.Lines
                .Select(l => new OcrLine(
                    l.Words.Where(w => w.B.Length == 4).Select(w => new OcrWord(w.T, ToRect(w.B))).ToList(),
                    (OcrLineKind)l.K))
                .ToList(),
            file.Figures.Where(f => f.Length == 4).Select(ToRect).ToList());
        return true;
    }

    public void Set(int pageIndex, OcrPageResult result)
    {
        var file = new CachedPage
        {
            Version = FormatVersion,
            Language = result.LanguageTag,
            // Per-character boxes are not kept: they would multiply the size of every page on disk, and without
            // them selection falls back to splitting a word evenly, which is what it did before they existed.
            Lines = result.Lines.Select(l => new CachedLine
            {
                K = (int)l.Kind,
                Words = l.Words.Select(w => new CachedWord { T = w.Text, B = Round(w.Bounds) }).ToList(),
            }).ToList(),
            Figures = result.Figures.Select(Round).ToList(),
        };
        JsonFile.Write(PagePath(pageIndex), file);
        lock (_gate) LoadIndex().Add(pageIndex);
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort; stale files are ignored after a version bump anyway.
            }
            _pages = [];
        }
    }

    private static double[] Round(RectD r) =>
        [Math.Round(r.Left, 5), Math.Round(r.Top, 5), Math.Round(r.Right, 5), Math.Round(r.Bottom, 5)];

    private static RectD ToRect(double[] b) => new(b[0], b[1], b[2], b[3]);

    private string PagePath(int pageIndex) => Path.Combine(_directory, $"{pageIndex}.json");

    private HashSet<int> LoadIndex()
    {
        if (_pages is not null) return _pages;
        _pages = [];
        if (!Directory.Exists(_directory)) return _pages;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), out int page))
                _pages.Add(page);
        return _pages;
    }

    private sealed class CachedPage
    {
        public int Version { get; set; }
        public string Language { get; set; } = "";
        public List<CachedLine> Lines { get; set; } = [];
        public List<double[]> Figures { get; set; } = [];
    }

    private sealed class CachedLine
    {
        /// <summary><see cref="OcrLineKind"/>.</summary>
        public int K { get; set; }

        public List<CachedWord> Words { get; set; } = [];
    }

    private sealed class CachedWord
    {
        public string T { get; set; } = "";
        public double[] B { get; set; } = [];
    }
}
