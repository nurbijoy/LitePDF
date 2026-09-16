namespace LitePdf.Core.Imaging;

/// <summary>One blob of connected ink: a glyph, a rule, or part of a drawing.</summary>
public readonly record struct InkBlob(int Left, int Top, int Right, int Bottom, int Pixels)
{
    public int Width => Right - Left + 1;

    public int Height => Bottom - Top + 1;

    public int Area => Width * Height;

    /// <summary>Share of the bounding box that is actually inked: near 1 for a rule, low for an outline.</summary>
    public double Density => Area <= 0 ? 0 : (double)Pixels / Area;

    public int CenterX => (Left + Right) / 2;

    public int CenterY => (Top + Bottom) / 2;

    public InkBlob Union(InkBlob other) => new(
        Math.Min(Left, other.Left), Math.Min(Top, other.Top),
        Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom), Pixels + other.Pixels);

    public bool Intersects(InkBlob other) =>
        Left <= other.Right && Right >= other.Left && Top <= other.Bottom && Bottom >= other.Top;
}

/// <summary>
/// Eight-connected component labelling over an ink mask, run-length based so a full page costs one pass over
/// the rows plus a union-find merge rather than a per-pixel flood fill.
/// </summary>
public static class ConnectedComponents
{
    /// <summary>Blobs of at least <paramref name="minPixels"/> ink pixels, in no particular order.</summary>
    public static List<InkBlob> Find(byte[] ink, int width, int height, int minPixels = 4)
    {
        var parent = new List<int>();
        var runs = new List<(int Row, int Start, int End, int Label)>();
        int previousRowStart = 0, previousRowEnd = 0;

        for (int y = 0; y < height; y++)
        {
            int rowStart = runs.Count;
            int row = y * width;
            int x = 0;
            while (x < width)
            {
                while (x < width && ink[row + x] == 0) x++;
                if (x >= width) break;
                int start = x;
                while (x < width && ink[row + x] != 0) x++;
                int end = x - 1;

                int label = -1;
                // Eight-connected: runs touching diagonally on the previous row belong to the same blob.
                for (int i = previousRowStart; i < previousRowEnd; i++)
                {
                    var run = runs[i];
                    if (run.End < start - 1) continue;
                    if (run.Start > end + 1) break;
                    int other = Find(parent, run.Label);
                    if (label < 0) label = other;
                    else if (label != other) Union(parent, label, other);
                }
                if (label < 0)
                {
                    label = parent.Count;
                    parent.Add(label);
                }
                runs.Add((y, start, end, label));
            }
            previousRowStart = rowStart;
            previousRowEnd = runs.Count;
        }

        var boxes = new Dictionary<int, InkBlob>();
        foreach (var (row, start, end, label) in runs)
        {
            int root = Find(parent, label);
            int length = end - start + 1;
            boxes[root] = boxes.TryGetValue(root, out var box)
                ? new InkBlob(Math.Min(box.Left, start), box.Top, Math.Max(box.Right, end), row, box.Pixels + length)
                : new InkBlob(start, row, end, row, length);
        }

        var result = new List<InkBlob>(boxes.Count);
        foreach (var box in boxes.Values)
            if (box.Pixels >= minPixels) result.Add(box);
        return result;
    }

    private static int Find(List<int> parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    private static void Union(List<int> parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a == b) return;
        if (a < b) parent[b] = a;
        else parent[a] = b;
    }
}
