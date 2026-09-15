namespace LitePdf.Core.Text;

public sealed class TextSelection
{
    public int PageIndex { get; }
    public int Start { get; }
    public int Count { get; }
    public PageTextLayer Layer { get; }

    public TextSelection(int pageIndex, int start, int count, PageTextLayer layer)
    {
        PageIndex = pageIndex;
        Start = Math.Max(0, start);
        Count = Math.Max(0, count);
        Layer = layer;
    }

    public string GetText() => Layer.GetText(Start, Count);

    public IReadOnlyList<RectD> GetRects() => Layer.GetLineRects(Start, Count);

    public bool IsEmpty => Count == 0;
}
