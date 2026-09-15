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
    private const int FormatVersion = 2;
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

        result = new OcrPageResult(file.Language, file.Lines
            .Select(l => new OcrLine(l.Words
                .Where(w => w.B.Length == 4)
                .Select(w => new OcrWord(w.T, new RectD(w.B[0], w.B[1], w.B[2], w.B[3])))
                .ToList()))
            .ToList());
        return true;
    }

    public void Set(int pageIndex, OcrPageResult result)
    {
        var file = new CachedPage
        {
            Version = FormatVersion,
            Language = result.LanguageTag,
            Lines = result.Lines.Select(l => new CachedLine
            {
                Words = l.Words.Select(w => new CachedWord
                {
                    T = w.Text,
                    B = [Math.Round(w.Bounds.Left, 5), Math.Round(w.Bounds.Top, 5), Math.Round(w.Bounds.Right, 5), Math.Round(w.Bounds.Bottom, 5)],
                }).ToList(),
            }).ToList(),
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
    }

    private sealed class CachedLine
    {
        public List<CachedWord> Words { get; set; } = [];
    }

    private sealed class CachedWord
    {
        public string T { get; set; } = "";
        public double[] B { get; set; } = [];
    }
}
