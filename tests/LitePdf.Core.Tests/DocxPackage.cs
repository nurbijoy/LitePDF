using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using LitePdf.Core.Export;
using LitePdf.Core.Text;

namespace LitePdf.Core.Tests;

/// <summary>Stands in for the WPF encoder: the tests care where the bytes land, not what is in them.</summary>
internal sealed class TestImageEncoder : IImageEncoder
{
    public int Calls { get; private set; }

    public byte[] EncodePng(ImageBits bits)
    {
        Calls++;
        var bytes = new byte[8 + bits.Bgra.Length];
        // A PNG signature, so the package looks like what it claims to be.
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        bits.Bgra.CopyTo(bytes, 8);
        return bytes;
    }
}

/// <summary>Reads back a written .docx so tests can assert on it.</summary>
internal sealed class DocxPackage : IDisposable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";

    private readonly ZipArchive _zip;

    private DocxPackage(ZipArchive zip) => _zip = zip;

    public static DocxPackage Write(DocxDocument document, IImageEncoder? encoder = null)
    {
        var buffer = new MemoryStream();
        DocxWriter.Write(buffer, document, encoder ?? new TestImageEncoder());
        buffer.Position = 0;
        return new DocxPackage(new ZipArchive(buffer, ZipArchiveMode.Read));
    }

    public static DocxPackage Open(string path) =>
        new(new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read));

    public IEnumerable<string> Entries => _zip.Entries.Select(e => e.FullName);

    public bool Has(string path) => _zip.GetEntry(path) is not null;

    public byte[] Bytes(string path)
    {
        var entry = _zip.GetEntry(path) ?? throw new FileNotFoundException(path);
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public XDocument Xml(string path) => XDocument.Parse(Encoding.UTF8.GetString(Bytes(path)));

    public XDocument Document => Xml("word/document.xml");

    /// <summary>Every part that claims to be XML parses. A part that does not is a repair prompt in Word.</summary>
    public void AssertEveryPartParses()
    {
        foreach (var entry in _zip.Entries)
        {
            if (!entry.FullName.EndsWith(".xml", StringComparison.Ordinal) &&
                !entry.FullName.EndsWith(".rels", StringComparison.Ordinal)) continue;
            var exception = Record.Exception(() => Xml(entry.FullName));
            Assert.True(exception is null, $"{entry.FullName} is not well-formed XML: {exception?.Message}");
        }
    }

    /// <summary>Every relationship id the body refers to is declared, and every declared target exists.</summary>
    public void AssertRelationshipsResolve()
    {
        var declared = Xml("word/_rels/document.xml.rels")
            .Root!.Elements(PkgRel + "Relationship")
            .ToDictionary(e => (string)e.Attribute("Id")!, e => e);

        foreach (var attribute in Document.Descendants().Attributes()
                     .Where(a => a.Name.Namespace == R && a.Name.LocalName is "id" or "embed"))
        {
            Assert.True(declared.ContainsKey(attribute.Value),
                $"document.xml references {attribute.Value}, which document.xml.rels does not declare");
        }

        foreach (var (id, element) in declared)
        {
            if ((string?)element.Attribute("TargetMode") == "External") continue;
            string target = (string)element.Attribute("Target")!;
            Assert.True(Has("word/" + target), $"relationship {id} targets word/{target}, which is not in the package");
        }
    }

    /// <summary>Every extension in the package has a content type, or Word refuses to open it.</summary>
    public void AssertContentTypesCoverEveryPart()
    {
        var types = Xml("[Content_Types].xml").Root!;
        var defaults = types.Elements(Ct + "Default")
            .Select(e => (string)e.Attribute("Extension")!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrides = types.Elements(Ct + "Override")
            .Select(e => (string)e.Attribute("PartName")!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _zip.Entries)
        {
            if (entry.FullName == "[Content_Types].xml") continue;
            if (overrides.Contains("/" + entry.FullName)) continue;
            string extension = Path.GetExtension(entry.FullName).TrimStart('.');
            Assert.True(defaults.Contains(extension), $"no content type covers {entry.FullName}");
        }
    }

    public void AssertValid()
    {
        Assert.True(Has("[Content_Types].xml"));
        Assert.True(Has("_rels/.rels"));
        Assert.True(Has("word/document.xml"));
        Assert.True(Has("word/styles.xml"));
        AssertEveryPartParses();
        AssertRelationshipsResolve();
        AssertContentTypesCoverEveryPart();
    }

    public IEnumerable<XElement> Paragraphs => Document.Descendants(W + "p");

    public IEnumerable<XElement> Tables => Document.Descendants(W + "tbl");

    /// <summary>The visible text of a paragraph.</summary>
    public static string TextOf(XElement paragraph) =>
        string.Concat(paragraph.Descendants(W + "t").Select(t => t.Value));

    public static string? StyleOf(XElement paragraph) =>
        (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");

    public static int? NumIdOf(XElement paragraph) =>
        (int?)paragraph.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val");

    public static string? AlignmentOf(XElement paragraph) =>
        (string?)paragraph.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val");

    /// <summary>The runs of a paragraph as (text, bold, italic, half-point size, colour).</summary>
    public static IEnumerable<(string Text, bool Bold, bool Italic, int Size, string Color)> RunsOf(XElement paragraph) =>
        paragraph.Descendants(W + "r")
            .Where(r => r.Element(W + "t") is not null)
            .Select(r =>
            {
                var properties = r.Element(W + "rPr");
                return (
                    string.Concat(r.Elements(W + "t").Select(t => t.Value)),
                    properties?.Element(W + "b") is not null,
                    properties?.Element(W + "i") is not null,
                    (int?)properties?.Element(W + "sz")?.Attribute(W + "val") ?? 0,
                    (string?)properties?.Element(W + "color")?.Attribute(W + "val") ?? "");
            });

    /// <summary>All visible text in document order, which is what character conservation is measured on.</summary>
    public string AllText() => string.Concat(Document.Descendants(W + "t").Select(t => t.Value));

    public int ImageCount => _zip.Entries.Count(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));

    public void Dispose() => _zip.Dispose();
}

/// <summary>Lays out synthetic pages so the composer can be tested without a PDF.</summary>
internal sealed class PageBuilder
{
    private readonly StringBuilder _text = new();
    private readonly List<float> _boxes = [];
    private readonly List<StyledSpan> _spans = [];
    private readonly List<PlacedImage> _images = [];
    private readonly List<RuleSegment> _rules = [];

    public PageSize Size { get; init; } = new(612, 792);

    public static TextStyle Body { get; } = new("Calibri", 11, false, false, 0x000000);

    /// <summary>Adds one visual line of text spanning [left, right] at the given top, in page fractions.</summary>
    public PageBuilder Line(string text, double left, double top, double right, double height = 0.0167, TextStyle? style = null)
    {
        StartLine();
        Append(text, left, top, right, height, style);
        return this;
    }

    /// <summary>
    /// Adds one visual line holding several pieces of text at set positions — a row of table cells, which
    /// a PDF draws as a single line of type however many cells it crosses.
    /// </summary>
    public PageBuilder Row(double top, double height, TextStyle? style, params (string Text, double Left, double Right)[] cells)
    {
        StartLine();
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) AppendUnplaced(' ');
            Append(cells[i].Text, cells[i].Left, top, cells[i].Right, height, style);
        }
        return this;
    }

    private void StartLine()
    {
        if (_text.Length == 0) return;
        AppendUnplaced('\n');
    }

    /// <summary>A character with no box: a line break, or the gap between two cells.</summary>
    private void AppendUnplaced(char c)
    {
        _text.Append(c);
        _boxes.AddRange([float.NaN, float.NaN, float.NaN, float.NaN]);
    }

    private void Append(string text, double left, double top, double right, double height, TextStyle? style)
    {
        int start = _text.Length;
        double step = text.Length > 0 ? (right - left) / text.Length : 0;
        for (int i = 0; i < text.Length; i++)
        {
            _text.Append(text[i]);
            double x0 = left + step * i;
            _boxes.AddRange([(float)x0, (float)top, (float)(x0 + step), (float)(top + height)]);
        }

        var applied = style ?? Body;
        if (_spans.Count > 0 && _spans[^1].End == start && _spans[^1].Style.Matches(applied))
            _spans[^1] = _spans[^1] with { End = _text.Length };
        else
            _spans.Add(new StyledSpan(start, _text.Length, applied));
    }

    public PageBuilder Image(RectD bounds, int width = 4, int height = 4)
    {
        _images.Add(PlacedImage.FromBits(bounds, new ImageBits(width, height, new byte[width * height * 4])));
        return this;
    }

    public PageBuilder Rule(RectD bounds, bool horizontal)
    {
        _rules.Add(new RuleSegment(bounds, horizontal));
        return this;
    }

    public PageContent Build(int pageIndex = 0)
    {
        var text = PageText.Create(pageIndex, TextSource.Pdf, _text.ToString(), _boxes.ToArray());
        return new PageContent(pageIndex, Size, text, _spans, _images, _rules);
    }
}
