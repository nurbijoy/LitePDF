namespace LitePdf.Core.Text;

/// <summary>A caret position: page plus character index (0..Length).</summary>
public readonly record struct TextPosition(int PageIndex, int Index) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other) =>
        PageIndex != other.PageIndex ? PageIndex.CompareTo(other.PageIndex) : Index.CompareTo(other.Index);

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>A normalized (Start &lt;= End) text range that may span pages.</summary>
public readonly record struct TextRange
{
    public TextRange(TextPosition a, TextPosition b)
    {
        (Start, End) = a <= b ? (a, b) : (b, a);
    }

    public TextPosition Start { get; }

    public TextPosition End { get; }

    public bool IsEmpty => Start == End;

    public bool ContainsPage(int pageIndex) => pageIndex >= Start.PageIndex && pageIndex <= End.PageIndex;

    /// <summary>The character range [Start, End) this selection covers on one page, or null.</summary>
    public (int Start, int End)? GetPageSpan(int pageIndex, int pageLength)
    {
        if (!ContainsPage(pageIndex)) return null;
        int s = pageIndex == Start.PageIndex ? Math.Min(Start.Index, pageLength) : 0;
        int e = pageIndex == End.PageIndex ? Math.Min(End.Index, pageLength) : pageLength;
        return e > s ? (s, e) : null;
    }
}
