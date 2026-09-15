namespace LitePdf.Core;

/// <summary>One entry of the document outline ("chapters"). PageIndex is -1 when the entry has no page target.</summary>
public sealed record OutlineItem(string Title, int PageIndex, IReadOnlyList<OutlineItem> Children);
