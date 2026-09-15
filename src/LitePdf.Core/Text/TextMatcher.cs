using System.Globalization;
using System.Text;

namespace LitePdf.Core.Text;

public readonly record struct TextMatch(int Start, int Length);

/// <summary>
/// Finds a query in page text. Whitespace runs (including line breaks) match a single space, so phrases that
/// wrap across lines are found; case- and accent-insensitive unless <c>matchCase</c> is set.
/// </summary>
public sealed class TextMatcher
{
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private readonly string _query;
    private readonly CompareOptions _options;
    private readonly bool _wholeWord;

    public TextMatcher(string query, bool matchCase = false, bool wholeWord = false)
    {
        _query = Fold(query ?? string.Empty, out _).Trim();
        _options = matchCase ? CompareOptions.Ordinal : CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        _wholeWord = wholeWord;
    }

    public bool IsValid => _query.Length > 0;

    public IReadOnlyList<TextMatch> FindAll(string text, int maxResults = int.MaxValue)
    {
        var results = new List<TextMatch>();
        if (!IsValid || string.IsNullOrEmpty(text)) return results;

        string folded = Fold(text, out var map);
        int pos = 0;
        while (pos < folded.Length && results.Count < maxResults)
        {
            int idx = Compare.IndexOf(folded.AsSpan(pos), _query, _options, out int matchLength);
            if (idx < 0 || matchLength == 0) break;
            idx += pos;

            if (!_wholeWord || IsWordBoundary(folded, idx, matchLength))
            {
                int start = map[idx];
                int end = map[idx + matchLength - 1] + 1;
                results.Add(new TextMatch(start, end - start));
            }
            pos = idx + Math.Max(1, matchLength);
        }
        return results;
    }

    /// <summary>Context around a match for result lists: (before, match, after), whitespace collapsed.</summary>
    public static (string Before, string Match, string After) Snippet(string text, TextMatch match, int context = 40)
    {
        // Context comes from the match's own line(s); running into neighbouring lines reads poorly in a result list.
        int matchEnd = match.Start + match.Length;
        int lineStart = match.Start > 0 ? text.LastIndexOf('\n', match.Start - 1) + 1 : 0;
        int lineEnd = text.IndexOf('\n', matchEnd);
        if (lineEnd < 0) lineEnd = text.Length;
        int s = Math.Max(lineStart, match.Start - context);
        int e = Math.Min(lineEnd, matchEnd + context);

        string before = Collapse(text[s..match.Start]);
        string hit = Collapse(text[match.Start..matchEnd]);
        string after = Collapse(text[matchEnd..e]);
        if (s > lineStart) before = "…" + before.TrimStart();
        if (e < lineEnd) after = after.TrimEnd() + "…";
        return (before, hit, after);
    }

    /// <summary>Collapses whitespace runs to one space, keeping a leading/trailing space (unlike <see cref="Fold"/>).</summary>
    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inSpace = false;
        foreach (char c in s)
        {
            if (c == '­') continue;
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace) sb.Append(' ');
                inSpace = true;
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }
        return sb.ToString();
    }

    private static bool IsWordBoundary(string s, int start, int length)
    {
        bool leftOk = start == 0 || !char.IsLetterOrDigit(s[start - 1]);
        int end = start + length;
        bool rightOk = end >= s.Length || !char.IsLetterOrDigit(s[end]);
        return leftOk && rightOk;
    }

    /// <summary>Collapses whitespace runs into one space and drops soft hyphens; map[i] = source index.</summary>
    private static string Fold(string text, out int[] map)
    {
        var sb = new StringBuilder(text.Length);
        var indices = new List<int>(text.Length);
        bool pendingSpace = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '­') continue;
            if (char.IsWhiteSpace(c))
            {
                if (!pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    indices.Add(i);
                }
                pendingSpace = true;
                continue;
            }
            pendingSpace = false;
            sb.Append(c);
            indices.Add(i);
        }
        map = indices.ToArray();
        return sb.ToString();
    }
}
