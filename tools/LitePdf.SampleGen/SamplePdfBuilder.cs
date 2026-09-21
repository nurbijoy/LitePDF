using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace LitePdf.Samples;

/// <summary>Minimal PDF writer for test and sample documents (no external dependencies).</summary>
public sealed class SamplePdfBuilder
{
    private readonly List<byte[]?> _objects = [];

    public int Reserve()
    {
        _objects.Add(null);
        return _objects.Count;
    }

    public int Add(string body)
    {
        int id = Reserve();
        Set(id, body);
        return id;
    }

    public void Set(int id, string body) => _objects[id - 1] = Encoding.Latin1.GetBytes(body);

    public int AddStream(string dictionaryEntries, byte[] data, bool compress = true)
    {
        byte[] payload = data;
        string filter = "";
        if (compress)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            payload = ms.ToArray();
            filter = " /Filter /FlateDecode";
        }
        var head = Encoding.Latin1.GetBytes($"<< {dictionaryEntries}{filter} /Length {payload.Length} >>\nstream\n");
        var tail = Encoding.Latin1.GetBytes("\nendstream");
        int id = Reserve();
        _objects[id - 1] = [.. head, .. payload, .. tail];
        return id;
    }

    public byte[] Build(int rootId, int? infoId = null)
    {
        using var output = new MemoryStream();
        void Write(string s) => output.Write(Encoding.Latin1.GetBytes(s));

        Write("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[_objects.Count];
        for (int i = 0; i < _objects.Count; i++)
        {
            offsets[i] = output.Position;
            Write($"{i + 1} 0 obj\n");
            output.Write(_objects[i] ?? throw new InvalidOperationException($"Object {i + 1} was reserved but never set."));
            Write("\nendobj\n");
        }
        long xref = output.Position;
        Write($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        string info = infoId is null ? "" : $" /Info {infoId} 0 R";
        Write($"trailer\n<< /Size {_objects.Count + 1} /Root {rootId} 0 R{info} /ID [<4C69746550444653616D706C65303031> <4C69746550444653616D706C65303031>] >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    public static string Escape(string text) => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Sample documents shared by tests and the sample generator.</summary>
public static class SampleDocuments
{
    public const string Sentence = "The quick brown fox jumps over the lazy dog.";
    public const int PageCount = 6;
    public const int RotatedPage = 3;   // /Rotate 90
    public const int CroppedPage = 4;   // /CropBox offset from the media box

    /// <summary>
    /// Six Letter pages. Each starts with "Chapter N" at (72, 720) in 24 pt Helvetica, followed by body lines at
    /// 11 pt. Page 1 has a URI link and an internal link to the last page. Outline: one entry per chapter.
    /// </summary>
    public static byte[] CreateTextDocument()
    {
        var b = new SamplePdfBuilder();
        int catalog = b.Reserve(), pages = b.Reserve(), outlines = b.Reserve();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int bold = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        var pageIds = Enumerable.Range(0, PageCount).Select(_ => b.Reserve()).ToArray();
        var outlineIds = Enumerable.Range(0, PageCount).Select(_ => b.Reserve()).ToArray();

        for (int p = 0; p < PageCount; p++)
        {
            var content = new StringBuilder();
            content.Append($"BT /F2 24 Tf 72 720 Td (Chapter {p + 1}) Tj ET\n");
            for (int line = 0; line < 30; line++)
            {
                double y = 680 - line * 18;
                string text = line == 0 && p == 0 ? "Visit example.com for more information." : $"Page {p + 1} line {line + 1}: {Sentence}";
                content.Append($"BT /F1 11 Tf 72 {SamplePdfBuilder.F(y)} Td ({SamplePdfBuilder.Escape(text)}) Tj ET\n");
            }
            int stream = b.AddStream("", Encoding.Latin1.GetBytes(content.ToString()));

            var annots = new List<string>();
            if (p == 0)
            {
                int uri = b.Add("<< /Type /Annot /Subtype /Link /Rect [70 676 280 692] /Border [0 0 0] /A << /S /URI /URI (https://example.com/) >> >>");
                int gotoLast = b.Add($"<< /Type /Annot /Subtype /Link /Rect [70 658 400 674] /Border [0 0 0] /Dest [{pageIds[^1]} 0 R /XYZ 0 792 0] >>");
                annots.Add($"{uri} 0 R");
                annots.Add($"{gotoLast} 0 R");
            }

            string extra = p switch
            {
                RotatedPage => " /Rotate 90",
                CroppedPage => " /CropBox [50 40 562 752]",
                _ => "",
            };
            string annotsEntry = annots.Count > 0 ? $" /Annots [{string.Join(' ', annots)}]" : "";
            b.Set(pageIds[p], $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792]{extra} /Contents {stream} 0 R /Resources << /Font << /F1 {font} 0 R /F2 {bold} 0 R >> >>{annotsEntry} >>");

            string prev = p > 0 ? $" /Prev {outlineIds[p - 1]} 0 R" : "";
            string next = p < PageCount - 1 ? $" /Next {outlineIds[p + 1]} 0 R" : "";
            b.Set(outlineIds[p], $"<< /Title (Chapter {p + 1}) /Parent {outlines} 0 R{prev}{next} /Dest [{pageIds[p]} 0 R /XYZ 72 740 0] >>");
        }

        b.Set(outlines, $"<< /Type /Outlines /First {outlineIds[0]} 0 R /Last {outlineIds[^1]} 0 R /Count {PageCount} >>");
        b.Set(pages, $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {PageCount} >>");
        b.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {outlines} 0 R /PageMode /UseOutlines >>");
        int info = b.Add("<< /Title (LitePDF sample) /Author (LitePDF tests) /Creator (SamplePdfBuilder) /CreationDate (D:20260915103000+05'30') >>");
        return b.Build(catalog, info);
    }

    /// <summary>A large text-only document (for performance checks) with one outline entry per 10 pages.</summary>
    public static byte[] CreateLargeDocument(int pageCount)
    {
        var b = new SamplePdfBuilder();
        int catalog = b.Reserve(), pages = b.Reserve(), outlines = b.Reserve();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var pageIds = new int[pageCount];
        var outlineIds = new List<int>();
        for (int p = 0; p < pageCount; p++)
        {
            pageIds[p] = b.Reserve();
            var content = new StringBuilder($"BT /F1 20 Tf 72 730 Td (Section {p / 10 + 1}, page {p + 1}) Tj ET\n");
            for (int line = 0; line < 40; line++)
                content.Append($"BT /F1 10 Tf 72 {SamplePdfBuilder.F(700 - line * 16)} Td (Line {line + 1} of page {p + 1}: {Sentence} {Sentence}) Tj ET\n");
            int stream = b.AddStream("", Encoding.Latin1.GetBytes(content.ToString()));
            b.Set(pageIds[p], $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {stream} 0 R /Resources << /Font << /F1 {font} 0 R >> >> >>");
        }
        for (int s = 0; s < pageCount; s += 10) outlineIds.Add(b.Reserve());
        for (int i = 0; i < outlineIds.Count; i++)
        {
            string prev = i > 0 ? $" /Prev {outlineIds[i - 1]} 0 R" : "";
            string next = i < outlineIds.Count - 1 ? $" /Next {outlineIds[i + 1]} 0 R" : "";
            b.Set(outlineIds[i], $"<< /Title (Section {i + 1}) /Parent {outlines} 0 R{prev}{next} /Dest [{pageIds[i * 10]} 0 R /XYZ 0 792 0] >>");
        }
        b.Set(outlines, $"<< /Type /Outlines /First {outlineIds[0]} 0 R /Last {outlineIds[^1]} 0 R /Count {outlineIds.Count} >>");
        b.Set(pages, $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pageCount} >>");
        b.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {outlines} 0 R >>");
        return b.Build(catalog);
    }

    /// <summary>A one-page "scan": an RGB image filling a Letter page, with no text layer.</summary>
    public static byte[] CreateImageDocument(byte[] rgb, int width, int height)
    {
        var b = new SamplePdfBuilder();
        int catalog = b.Reserve(), pages = b.Reserve(), page = b.Reserve();
        int image = b.AddStream($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8", rgb);
        int content = b.AddStream("", Encoding.Latin1.GetBytes("q 612 0 0 792 0 0 cm /Im1 Do Q"));
        b.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R /Resources << /XObject << /Im1 {image} 0 R >> >> >>");
        b.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        b.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        return b.Build(catalog);
    }

    /// <summary>
    /// Four Letter pages exercising everything the Word export has to rebuild: a title and two levels of
    /// heading, ragged and justified prose, a bulleted and a numbered list, a ruled three-column table, a
    /// picture with a caption, and a running head and foot with a page number.
    /// </summary>
    public static byte[] CreateFormattedDocument(byte[] pictureRgb, int pictureWidth, int pictureHeight)
    {
        var b = new SamplePdfBuilder();
        int catalog = b.Reserve(), pages = b.Reserve(), outlines = b.Reserve();
        int roman = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>");
        int bold = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>");
        int italic = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Italic /Encoding /WinAnsiEncoding >>");
        int picture = b.AddStream(
            $"/Type /XObject /Subtype /Image /Width {pictureWidth} /Height {pictureHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8",
            pictureRgb);

        const int PageCount = 4;
        var pageIds = Enumerable.Range(0, PageCount).Select(_ => b.Reserve()).ToArray();
        var outlineIds = Enumerable.Range(0, PageCount).Select(_ => b.Reserve()).ToArray();
        var titles = new[] { "Introduction", "Method", "Results", "Illustration" };

        for (int p = 0; p < PageCount; p++)
        {
            var c = new StringBuilder();

            // Running head and foot, both well inside the margins.
            Text(c, "F3", 9, 72, 756, "LitePDF formatted sample");
            Text(c, "F1", 9, 300, 46, $"Page {p + 1}");

            double y = 700;
            if (p == 0)
            {
                Text(c, "F2", 22, 72, y, "A Formatted Sample Document");
                y -= 44;
                Text(c, "F2", 15, 72, y, "Introduction");
                y -= 26;
                foreach (string line in Wrapped)
                {
                    Text(c, "F1", 11, 72, y, line);
                    y -= 15;
                }
                y -= 12;
                foreach (string item in new[] { "The first thing worth noting", "The second thing worth noting", "The third thing worth noting" })
                {
                    Text(c, "F1", 11, 90, y, $"· {item}");
                    y -= 15;
                }
            }
            else if (p == 1)
            {
                Text(c, "F2", 15, 72, y, "Method");
                y -= 26;
                foreach (string line in Wrapped)
                {
                    Text(c, "F1", 11, 72, y, line);
                    y -= 15;
                }
                y -= 12;
                for (int i = 1; i <= 3; i++)
                {
                    Text(c, "F1", 11, 90, y, $"{i}. Step number {i} of the procedure");
                    y -= 15;
                }
            }
            else if (p == 2)
            {
                Text(c, "F2", 15, 72, y, "Results");
                y -= 30;

                // A ruled table: four horizontal rules and four vertical ones.
                double[] columns = [72, 240, 408, 540];
                double top = y, rowHeight = 22;
                double[] rows = [top, top - rowHeight, top - rowHeight * 2, top - rowHeight * 3];

                c.Append("0 0 0 rg\n");
                foreach (double line in rows) Rect(c, columns[0], line, columns[^1] - columns[0], 0.7);
                foreach (double column in columns) Rect(c, column, rows[^1], 0.7, rows[0] - rows[^1]);

                string[,] cells =
                {
                    { "Sample", "Outcome", "Year" },
                    { "Alpha", "Confirmed", "1843" },
                    { "Beta", "Inconclusive", "1936" },
                };
                for (int r = 0; r < 3; r++)
                    for (int col = 0; col < 3; col++)
                        Text(c, r == 0 ? "F2" : "F1", 11, columns[col] + 6, rows[r] - 15, cells[r, col]);

                y = rows[^1] - 30;
                Text(c, "F1", 11, 72, y, "The table above lists every sample considered in this study.");
            }
            else
            {
                Text(c, "F2", 15, 72, y, "Illustration");
                y -= 30;
                c.Append($"q 288 0 0 216 72 {F(y - 216)} cm /Im1 Do Q\n");
                y -= 234;
                Text(c, "F3", 10, 72, y, "Figure 1. A plate reproduced at its own resolution.");
            }

            int stream = b.AddStream("", Encoding.Latin1.GetBytes(c.ToString()));
            string resources = $"/Font << /F1 {roman} 0 R /F2 {bold} 0 R /F3 {italic} 0 R >>" +
                               (p == 3 ? $" /XObject << /Im1 {picture} 0 R >>" : "");
            b.Set(pageIds[p], $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {stream} 0 R /Resources << {resources} >> >>");

            string prev = p > 0 ? $" /Prev {outlineIds[p - 1]} 0 R" : "";
            string next = p < PageCount - 1 ? $" /Next {outlineIds[p + 1]} 0 R" : "";
            b.Set(outlineIds[p], $"<< /Title ({titles[p]}) /Parent {outlines} 0 R{prev}{next} /Dest [{pageIds[p]} 0 R /XYZ 72 {F(p == 0 ? 656 : 700)} 0] >>");
        }

        b.Set(outlines, $"<< /Type /Outlines /First {outlineIds[0]} 0 R /Last {outlineIds[^1]} 0 R /Count {PageCount} >>");
        b.Set(pages, $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {PageCount} >>");
        b.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {outlines} 0 R >>");
        int info = b.Add("<< /Title (A Formatted Sample Document) /Author (LitePDF tests) >>");
        return b.Build(catalog, info);

        static string F(double v) => SamplePdfBuilder.F(v);

        static string Escape(string s) => SamplePdfBuilder.Escape(s);

        static void Text(StringBuilder c, string font, double size, double x, double y, string text) =>
            c.Append($"BT /{font} {F(size)} Tf {F(x)} {F(y)} Td ({Escape(text)}) Tj ET\n");

        static void Rect(StringBuilder c, double x, double y, double w, double h) =>
            c.Append($"{F(x)} {F(y)} {F(w)} {F(h)} re f\n");
    }

    /// <summary>Body text of uneven line lengths, so paragraph ends are visible in the geometry.</summary>
    private static readonly string[] Wrapped =
    [
        "The question this section sets out to answer is not whether the effect can be",
        "observed at all, which has never been in doubt, but whether it survives once the",
        "obvious confounds have been removed from the measurement.",
        "A second paragraph begins here and runs to very nearly the same measure as the",
        "first, so that the two can be told apart only by the line that stops short.",
    ];

    /// <summary>Converts tight BGRA pixels to packed RGB.</summary>
    public static byte[] BgraToRgb(byte[] bgra)
    {
        var rgb = new byte[bgra.Length / 4 * 3];
        for (int i = 0, j = 0; i + 3 < bgra.Length; i += 4, j += 3)
        {
            rgb[j] = bgra[i + 2];
            rgb[j + 1] = bgra[i + 1];
            rgb[j + 2] = bgra[i];
        }
        return rgb;
    }
}
