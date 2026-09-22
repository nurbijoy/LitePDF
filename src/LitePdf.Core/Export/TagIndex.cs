namespace LitePdf.Core.Export;

internal enum TagKind
{
    None,
    Paragraph,
    Heading,
    ListItem,
    Caption,
    TableCell,
    Figure,
}

/// <summary>What the tags say about one paragraph. <see cref="Level"/> is 0-based for both kinds.</summary>
internal readonly record struct TagInfo(TagKind Kind, int Level, bool Ordered)
{
    public static TagInfo None { get; } = new(TagKind.None, 0, false);
}

internal sealed record TaggedCell(
    IReadOnlyList<(int Start, int End)> Ranges, RectD Bounds, int ColumnSpan, int RowSpan, bool IsHeader);

internal sealed record TaggedRow(IReadOnlyList<TaggedCell> Cells);

internal sealed record TaggedTable(RectD Bounds, IReadOnlyList<TaggedRow> Rows);

/// <summary>
/// The structure tree of a tagged PDF, resolved against the page's characters.
///
/// A tagged PDF already contains the answer the geometry has to be made to confess: these lines are one
/// paragraph, that one is a level-two heading, these six are one list, those cells are a table. The tags
/// are trusted for <i>grouping and order only</i> — a tagged file will happily mark a run as <c>/P</c>
/// while drawing it bold at 18 pt — so everything about appearance still comes from the glyphs.
///
/// Nothing here can lose content: where a tag is missing, wrong or unrecognized the paragraph falls back
/// to what the geometry said, which is the same answer an untagged PDF would have produced.
/// </summary>
internal sealed class TagIndex
{
    private readonly PageContent _page;
    private readonly List<Node> _nodes;
    private readonly int[] _owner;
    private readonly List<(int Start, int End, int Node)> _runs;
    private readonly Dictionary<int, int> _nodeByMarkedContentId;
    private List<TaggedTable>? _tables;

    private sealed record Node(PageTag Tag, int Parent, int Depth);

    private TagIndex(PageContent page, List<Node> nodes, int[] owner, Dictionary<int, int> nodeByMarkedContentId)
    {
        _page = page;
        _nodes = nodes;
        _owner = owner;
        _nodeByMarkedContentId = nodeByMarkedContentId;

        // The characters each element claims, as runs rather than per character: a page has thousands of
        // characters and a few hundred tagged sequences, and every question below is asked per element.
        _runs = [];
        int start = -1;
        for (int i = 0; i <= owner.Length; i++)
        {
            int node = i < owner.Length ? owner[i] : -1;
            if (start >= 0 && (node != owner[start]))
            {
                _runs.Add((start, i, owner[start]));
                start = -1;
            }
            if (node >= 0 && start < 0) start = i;
        }
    }

    public static TagIndex Empty { get; } = new(PageContent.Empty(0, new PageSize(612, 792)), [], [], []);

    public IReadOnlyList<TaggedTable> Tables => _tables ??= BuildTables();

    public bool IsEmpty => _nodes.Count == 0;

    public static TagIndex Build(PageContent page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Tags.Count == 0 || page.Marks.Count == 0) return Empty;

        var nodes = new List<Node>();
        foreach (var root in page.Tags) Flatten(root, -1, 0, nodes);
        if (nodes.Count == 0) return Empty;

        var rangesByMarkedContentId = new Dictionary<int, List<(int Start, int End)>>();
        foreach (var mark in page.Marks)
        {
            if (!rangesByMarkedContentId.TryGetValue(mark.MarkedContentId, out var list))
                rangesByMarkedContentId[mark.MarkedContentId] = list = [];
            list.Add((mark.Start, mark.End));
        }

        // Parents are written before their children, so the deepest element that claims a character wins.
        var owner = new int[page.Text.Length];
        Array.Fill(owner, -1);
        var nodeByMarkedContentId = new Dictionary<int, int>();

        for (int i = 0; i < nodes.Count; i++)
        {
            foreach (int id in nodes[i].Tag.MarkedContentIds)
            {
                nodeByMarkedContentId[id] = i;
                if (!rangesByMarkedContentId.TryGetValue(id, out var ranges)) continue;
                foreach (var (start, end) in ranges)
                    for (int c = Math.Max(0, start); c < Math.Min(end, owner.Length); c++)
                        owner[c] = i;
            }
        }

        return new TagIndex(page, nodes, owner, nodeByMarkedContentId);
    }

    private static void Flatten(PageTag tag, int parent, int depth, List<Node> nodes)
    {
        int self = nodes.Count;
        nodes.Add(new Node(tag, parent, depth));
        foreach (var child in tag.Children) Flatten(child, self, depth + 1, nodes);
    }

    /// <summary>What the tags say about the characters a paragraph is made of, by majority.</summary>
    public TagInfo Classify(IReadOnlyList<(int Start, int End)> ranges)
    {
        int node = Dominant(ranges);
        if (node < 0) return TagInfo.None;

        // The nearest enclosing element that means something decides; a /Span inside a /H2 is still the
        // heading, and a /P inside an /LI is still a list item.
        for (int at = node; at >= 0; at = _nodes[at].Parent)
        {
            string type = _nodes[at].Tag.Type;

            if (HeadingLevel(type) is { } level) return new TagInfo(TagKind.Heading, level, false);
            if (type is "TD" or "TH") return new TagInfo(TagKind.TableCell, 0, false);
            if (type is "Caption") return new TagInfo(TagKind.Caption, 0, false);
            if (type is "Figure") return new TagInfo(TagKind.Figure, 0, false);
            if (type is "LI" or "LBody" or "Lbl")
                return new TagInfo(TagKind.ListItem, ListDepth(at), IsOrdered(at));
            if (type is "P" or "Note" or "Quote" or "BlockQuote") return new TagInfo(TagKind.Paragraph, 0, false);
        }
        return TagInfo.None;
    }

    /// <summary>
    /// The block-level element these characters belong to, or -1. Two paragraphs the geometry split that
    /// answer with the same element are one paragraph, which is the whole point of reading the tags.
    /// </summary>
    public int BlockOf(IReadOnlyList<(int Start, int End)> ranges)
    {
        int node = Dominant(ranges);
        for (int at = node; at >= 0; at = _nodes[at].Parent)
        {
            string type = _nodes[at].Tag.Type;
            if (HeadingLevel(type) is not null) return at;
            if (type is "P" or "LI" or "LBody" or "TD" or "TH" or "Caption" or "Figure" or "Note" or
                "Quote" or "BlockQuote" or "TOCI") return at;
        }
        return -1;
    }

    /// <summary>Alt text for a figure, found by the marked-content id of the image the PDF drew.</summary>
    public string? AltTextFor(int markedContentId)
    {
        if (markedContentId < 0 || !_nodeByMarkedContentId.TryGetValue(markedContentId, out int node)) return null;
        for (int at = node; at >= 0; at = _nodes[at].Parent)
        {
            if (_nodes[at].Tag.AltText is { Length: > 0 } alt) return alt;
            if (_nodes[at].Tag.Title is { Length: > 0 } title && _nodes[at].Tag.Type == "Figure") return title;
        }
        return null;
    }

    private static int? HeadingLevel(string type)
    {
        if (type.Length == 2 && type[0] == 'H' && type[1] is >= '1' and <= '6') return type[1] - '1';
        return type == "H" ? 0 : null;
    }

    /// <summary>Nesting depth of the list this item belongs to, so sub-lists indent.</summary>
    private int ListDepth(int node)
    {
        int depth = 0;
        for (int at = node; at >= 0; at = _nodes[at].Parent)
            if (_nodes[at].Tag.Type == "L") depth++;
        return Math.Clamp(depth - 1, 0, 4);
    }

    /// <summary>A numbered list, read from the label the PDF drew rather than from an attribute.</summary>
    private bool IsOrdered(int node)
    {
        int item = node;
        while (item >= 0 && _nodes[item].Tag.Type != "LI") item = _nodes[item].Parent;
        if (item < 0) return false;

        foreach (int label in Descendants(item).Where(i => _nodes[i].Tag.Type == "Lbl"))
        {
            string text = TextOf(RangesOf(label)).Trim();
            if (text.Length == 0) continue;
            return char.IsLetterOrDigit(text[0]);
        }
        return false;
    }

    private bool IsDescendant(int node, int ancestor)
    {
        for (int at = _nodes[node].Parent; at >= 0; at = _nodes[at].Parent)
            if (at == ancestor) return true;
        return false;
    }

    /// <summary>The node that owns most of the characters in these ranges.</summary>
    private int Dominant(IReadOnlyList<(int Start, int End)> ranges)
    {
        var counts = new Dictionary<int, int>();
        foreach (var (start, end) in ranges)
        {
            for (int i = Math.Max(0, start); i < Math.Min(end, _owner.Length); i++)
            {
                int node = _owner[i];
                if (node < 0 || char.IsWhiteSpace(_page.Text.Text[i])) continue;
                counts[node] = counts.GetValueOrDefault(node) + 1;
            }
        }

        int best = -1, bestCount = 0;
        foreach (var (node, count) in counts)
        {
            if (count <= bestCount) continue;
            best = node;
            bestCount = count;
        }
        return best;
    }

    // ---- tables ----

    private List<TaggedTable> BuildTables()
    {
        var tables = new List<TaggedTable>();
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i].Tag.Type != "Table") continue;
            if (Ancestors(i).Any(a => _nodes[a].Tag.Type == "Table")) continue;   // an inner table is the outer one's cell

            var rows = new List<TaggedRow>();
            var bounds = RectD.Empty;

            foreach (int row in Descendants(i).Where(n => _nodes[n].Tag.Type is "TR"))
            {
                var cells = new List<TaggedCell>();
                foreach (int cell in Descendants(row).Where(n => _nodes[n].Tag.Type is "TD" or "TH"))
                {
                    // A cell of a table nested inside this one belongs to that table, not to this row.
                    if (Ancestors(cell).TakeWhile(a => a != row).Any(a => _nodes[a].Tag.Type == "TR")) continue;

                    var ranges = RangesOf(cell);
                    var cellBounds = BoundsOf(ranges);
                    bounds = bounds.Union(cellBounds);
                    cells.Add(new TaggedCell(ranges, cellBounds,
                        Math.Clamp(_nodes[cell].Tag.ColumnSpan, 1, 64),
                        Math.Clamp(_nodes[cell].Tag.RowSpan, 1, 64),
                        _nodes[cell].Tag.Type == "TH"));
                }
                if (cells.Count > 0) rows.Add(new TaggedRow(cells));
            }

            // One row of one cell is a layout box, not a table; two of either is the least a table can be.
            if (rows.Count < 2 || rows.Sum(r => r.Cells.Count) < 4 || bounds.IsEmpty) continue;
            tables.Add(new TaggedTable(bounds, rows));
        }
        return tables;
    }

    private IEnumerable<int> Ancestors(int node)
    {
        for (int at = _nodes[node].Parent; at >= 0; at = _nodes[at].Parent) yield return at;
    }

    /// <summary>Descendants of a node, in document order (the flattened list is already pre-order).</summary>
    private IEnumerable<int> Descendants(int node)
    {
        for (int i = node + 1; i < _nodes.Count && _nodes[i].Depth > _nodes[node].Depth; i++) yield return i;
    }

    /// <summary>Every character range this element or one of its descendants drew, in order.</summary>
    public IReadOnlyList<(int Start, int End)> RangesOf(int node)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var (start, end, owner) in _runs)
        {
            if (owner != node && !IsDescendant(owner, node)) continue;
            if (ranges.Count > 0 && ranges[^1].End == start) ranges[^1] = (ranges[^1].Start, end);
            else ranges.Add((start, end));
        }
        return ranges;
    }

    private string TextOf(IReadOnlyList<(int Start, int End)> ranges) =>
        string.Concat(ranges.Select(r => _page.Text.Text[r.Start..Math.Min(r.End, _page.Text.Length)]));

    private RectD BoundsOf(IReadOnlyList<(int Start, int End)> ranges)
    {
        var bounds = RectD.Empty;
        foreach (var (start, end) in ranges)
            for (int i = start; i < end; i++)
                if (_page.Text.TryGetBox(i, out var box) && !box.IsEmpty) bounds = bounds.Union(box);
        return bounds;
    }
}
