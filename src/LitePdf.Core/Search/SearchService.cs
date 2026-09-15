using LitePdf.Core.Text;

namespace LitePdf.Core.Search;

public sealed record SearchResult(int PageIndex, int Start, int Count, string Snippet, IReadOnlyList<RectD> Rects);

public sealed class SearchService
{
    private readonly List<PageTextLayer> _layers = new();
    private readonly object _lock = new();

    public void SetLayer(PageTextLayer layer)
    {
        lock (_lock)
        {
            var idx = _layers.FindIndex(l => l.PageIndex == layer.PageIndex);
            if (idx >= 0) _layers[idx] = layer;
            else _layers.Add(layer);
            _layers.Sort((a, b) => a.PageIndex.CompareTo(b.PageIndex));
        }
    }

    public void Clear() { lock (_lock) _layers.Clear(); }

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default, int maxResults = 1000)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<SearchResult>();
        var lowerQuery = query.ToLowerInvariant();

        var results = new List<SearchResult>();
        List<PageTextLayer> snapshot;
        lock (_lock) snapshot = _layers.ToList();

        await Task.Run(() =>
        {
            foreach (var layer in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                string text = layer.GetText();
                string lowerText = text.ToLowerInvariant();
                int pos = 0;
                while (true)
                {
                    int idx = lowerText.IndexOf(lowerQuery, pos, StringComparison.Ordinal);
                    if (idx < 0) break;
                    // Build snippet
                    int snippetStart = Math.Max(0, idx - 20);
                    int snippetLen = Math.Min(text.Length - snippetStart, lowerQuery.Length + 40);
                    string snippet = text.Substring(snippetStart, snippetLen).Replace('\n', ' ').Replace('\r', ' ');

                    var rects = layer.GetLineRects(idx, lowerQuery.Length);
                    results.Add(new SearchResult(layer.PageIndex, idx, lowerQuery.Length, snippet, rects));
                    if (results.Count >= maxResults) return;
                    pos = idx + lowerQuery.Length;
                }
            }
        }, ct).ConfigureAwait(false);

        return results;
    }
}
