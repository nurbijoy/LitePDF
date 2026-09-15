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
