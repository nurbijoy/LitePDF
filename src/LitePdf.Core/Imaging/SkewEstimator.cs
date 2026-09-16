namespace LitePdf.Core.Imaging;

/// <summary>
/// Measures how far a scanned page is rotated, by shearing the ink vertically and looking for the angle whose
/// horizontal projection is spikiest: text lines only collapse into tall, narrow peaks when they are level.
/// </summary>
public static class SkewEstimator
{
    /// <summary>The projection runs on a grid at most this wide; skew does not need full resolution.</summary>
    private const int WorkingWidth = 900;

    /// <summary>Below this many baseline samples the projection is noise, so the page is left as it is.</summary>
    private const int MinSamples = 200;

    /// <summary>Estimated clockwise skew in degrees (positive: text slopes down to the right).</summary>
    public static double Estimate(byte[] gray, int width, int height, int inkThreshold, double maxDegrees)
    {
        var (points, sampledHeight) = CollectBaselinePoints(gray, width, height, inkThreshold);
        if (points.Count < MinSamples) return 0;

        double coarse = Search(points, sampledHeight, -maxDegrees, maxDegrees, 0.4);
        return Math.Round(Search(points, sampledHeight, coarse - 0.4, coarse + 0.4, 0.05), 3);
    }

    /// <summary>
    /// The bottom pixel of each vertical ink run, sampled on a coarse grid. Baselines are the sharpest feature
    /// on a text page: using them rather than every ink pixel makes the projection peaks far more pronounced.
    /// </summary>
    private static (List<(int X, int Y)> Points, int Height) CollectBaselinePoints(
        byte[] gray, int width, int height, int inkThreshold)
    {
        int step = Math.Max(1, (int)Math.Ceiling(width / (double)WorkingWidth));
        int sampledHeight = height / step + 1;
        var points = new List<(int, int)>();

        for (int x = 0; x < width; x += step)
        {
            bool inRun = false;
            for (int y = 0; y < height; y += step)
            {
                bool ink = gray[y * width + x] < inkThreshold;
                if (inRun && !ink) points.Add((x / step, y / step));
                inRun = ink;
            }
            if (inRun) points.Add((x / step, sampledHeight - 1));
        }
        return (points, sampledHeight);
    }

    private static double Search(List<(int X, int Y)> points, int height, double from, double to, double stepDegrees)
    {
        double best = 0, bestScore = -1;
        for (double angle = from; angle <= to + 1e-9; angle += stepDegrees)
        {
            double score = Score(points, height, angle);
            if (score > bestScore)
            {
                bestScore = score;
                best = angle;
            }
        }
        return best;
    }

    /// <summary>
    /// Sum of squared bin counts, maximal when every text line lands in a single row. The shear is measured
    /// around x = 0: a constant offset shifts all bins alike and cannot change the score.
    /// </summary>
    private static double Score(List<(int X, int Y)> points, int height, double angleDegrees)
    {
        double slope = Math.Tan(angleDegrees * Math.PI / 180);
        int margin = (int)Math.Abs(slope * WorkingWidth) + 2;
        var bins = new int[height + margin * 2];
        foreach (var (x, y) in points)
        {
            int bin = (int)(y - x * slope) + margin;
            if ((uint)bin < (uint)bins.Length) bins[bin]++;
        }

        double score = 0;
        foreach (int count in bins) score += (double)count * count;
        return score;
    }
}
