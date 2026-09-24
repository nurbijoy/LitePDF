using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace LitePdf.Core.Export;

/// <summary>Encodes raw pixels as PNG. Core has no image encoder, so the host supplies one.</summary>
public interface IImageEncoder
{
    byte[] EncodePng(ImageBits bits);
}

/// <summary>
/// Serializes a <see cref="DocxDocument"/> as an Office Open XML package.
///
/// A .docx is a ZIP of XML parts, so this needs nothing but <c>System.IO.Compression</c> and
/// <c>XmlWriter</c>. Everything goes through <see cref="XmlWriter"/> and never string concatenation: one
/// unescaped ampersand out of a PDF is a "Word found unreadable content" prompt, which is a hard failure.
///
/// Units, which all differ: twips (1/20 pt) for paragraph geometry, half-points for font size, EMU
/// (12700 per point) for image extents.
/// </summary>
public static class DocxWriter
{
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string WpNs = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string PicNs = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string CoreNs = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private const string DcNs = "http://purl.org/dc/elements/1.1/";
    private const string DcTermsNs = "http://purl.org/dc/terms/";
    private const string XsiNs = "http://www.w3.org/2001/XMLSchema-instance";
    private const string ExtendedNs = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

    private const int EmuPerPoint = 12700;

    public const int BulletNumId = 1;
    public const int NumberNumId = 2;

    public static int Twips(double points) => (int)Math.Round(points * 20, MidpointRounding.AwayFromZero);

    public static void Write(Stream output, DocxDocument document, IImageEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(encoder);

        var parts = new PackageParts(document, encoder);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // [Content_Types].xml must describe every part, so it is built from what the pre-pass found.
        WritePart(zip, "[Content_Types].xml", w => WriteContentTypes(w, parts));
        WritePart(zip, "_rels/.rels", WriteRootRels);
        WritePart(zip, "docProps/core.xml", w => WriteCoreProps(w, document));
        WritePart(zip, "docProps/app.xml", WriteAppProps);
        WritePart(zip, "word/styles.xml", w => WriteStyles(w, document));
        WritePart(zip, "word/numbering.xml", w => WriteNumbering(w, parts));
        WritePart(zip, "word/settings.xml", WriteSettings);
        WritePart(zip, "word/_rels/document.xml.rels", w => WriteDocumentRels(w, parts));
        WritePart(zip, "word/document.xml", w => WriteDocument(w, document, parts));

        foreach (var (path, header) in parts.HeaderParts)
            WritePart(zip, path, w => WriteHeaderFooter(w, header.Content, header.IsHeader));

        if (parts.HasComments) WritePart(zip, "word/comments.xml", w => WriteComments(w, document));
        if (parts.Fonts.Count > 0) WritePart(zip, "word/fontTable.xml", w => WriteFontTable(w, parts));

        foreach (var media in parts.Media)
        {
            var entry = zip.CreateEntry($"word/media/{media.FileName}", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(media.Bytes);
        }

        foreach (var font in parts.Fonts)
        {
            var entry = zip.CreateEntry($"word/fonts/{font.FileName}", CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(font.Obfuscated);
        }
    }

    private static void WritePart(ZipArchive zip, string path, Action<XmlWriter> body)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            CloseOutput = false,
        });
        writer.WriteStartDocument(standalone: true);
        body(writer);
        writer.WriteEndDocument();
    }

    // ---- package plumbing ----

    private sealed record MediaPart(string FileName, byte[] Bytes, string RelationshipId, int Number);

    private sealed record HeaderPart(string RelationshipId, DocxHeaderFooter Content, bool IsHeader);

    /// <summary>An embedded font: the obfuscated bytes, and the key Word needs to read them back.</summary>
    private sealed record FontPart(
        string FileName, byte[] Obfuscated, string RelationshipId, string Key, EmbeddedFont Font);

    /// <summary>
    /// One pass over the document to collect everything that needs a relationship id before any XML is
    /// written: images (deduplicated, so a logo repeated on 400 pages is stored once), external hyperlinks,
    /// and the header/footer parts.
    /// </summary>
    private sealed class PackageParts
    {
        private readonly Dictionary<string, MediaPart> _mediaByHash = [];
        private readonly Dictionary<PlacedImage, MediaPart> _mediaByImage = [];
        private readonly Dictionary<string, string> _hyperlinks = new(StringComparer.Ordinal);
        private int _nextId = 4; // rId1 = styles, rId2 = numbering, rId3 = settings
        private int _headerCount;
        private int _footerCount;
        private int _drawingId;

        public PackageParts(DocxDocument document, IImageEncoder encoder)
        {
            (string Format, int Start, string Label)? previousNumber = null;
            int previousLevel = -1, previousId = 0;
            foreach (var section in document.Sections)
            {
                foreach (var block in Flatten(section.Blocks))
                {
                    if (block is DocxParagraph { List: DocxListKind.Number, ListMarker.Length: > 0 } numbered)
                    {
                        var label = NumberLabel(numbered.ListMarker, numbered.ListLevel);
                        bool continues = previousNumber is { } previous && previousLevel == numbered.ListLevel &&
                            previous.Format == label.Format && previous.Label == label.Label && previous.Start + 1 == label.Start;
                        if (!continues)
                        {
                            previousId = NumberingDefinitions.Count + 3;
                            NumberingDefinitions.Add(previousId, numbered);
                        }
                        NumberedParagraphs.TryAdd(numbered, previousId);
                        previousNumber = label;
                        previousLevel = numbered.ListLevel;
                    }
                    else previousNumber = null;
                    switch (block)
                    {
                        case DocxPicture picture:
                            AddImage(picture.Image, encoder);
                            break;
                        case DocxParagraph paragraph:
                            foreach (var floating in paragraph.Floating)
                                AddImage(floating.Image, encoder);
                            foreach (var run in paragraph.Runs)
                                if (run.Hyperlink is { Length: > 0 } uri && !_hyperlinks.ContainsKey(uri))
                                    _hyperlinks[uri] = NextId();
                            break;
                    }
                }

                foreach (var header in (DocxHeaderFooter?[])[section.Header, section.DifferentFirstPage ? section.FirstHeader : null])
                    if (header is not null && !HeaderParts.Any(p => ReferenceEquals(p.Part.Content, header)))
                        HeaderParts.Add(($"word/header{++_headerCount}.xml", new HeaderPart(NextId(), header, true)));
                foreach (var footer in (DocxHeaderFooter?[])[section.Footer, section.DifferentFirstPage ? section.FirstFooter : null])
                    if (footer is not null && !HeaderParts.Any(p => ReferenceEquals(p.Part.Content, footer)))
                        HeaderParts.Add(($"word/footer{++_footerCount}.xml", new HeaderPart(NextId(), footer, false)));
            }

            HasComments = document.Comments.Count > 0;
            if (HasComments) CommentsRelationshipId = NextId();

            if (document.Fonts.Count > 0)
            {
                FontTableRelationshipId = NextId();
                foreach (var font in document.Fonts)
                {
                    if (Fonts.Count >= 32 || font.Data.Length == 0) continue;
                    string key = $"{{{Guid.NewGuid().ToString("D").ToUpperInvariant()}}}";
                    Fonts.Add(new FontPart($"font{Fonts.Count + 1}.odttf", Obfuscate(font.Data, key), NextId(), key, font));
                }
            }
        }

        public List<MediaPart> Media { get; } = [];

        public Dictionary<DocxParagraph, int> NumberedParagraphs { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<int, DocxParagraph> NumberingDefinitions { get; } = [];

        public int NumberId(DocxParagraph paragraph) => NumberedParagraphs.TryGetValue(paragraph, out int id)
            ? id : paragraph.List == DocxListKind.Bullet ? BulletNumId : NumberNumId;

        public List<(string Path, HeaderPart Part)> HeaderParts { get; } = [];

        public List<FontPart> Fonts { get; } = [];

        public bool HasComments { get; }

        public string? CommentsRelationshipId { get; }

        public string FontTableRelationshipId { get; } = string.Empty;

        public IReadOnlyDictionary<string, string> Hyperlinks => _hyperlinks;

        public MediaPart? Image(PlacedImage image) => _mediaByImage.GetValueOrDefault(image);

        /// <summary>Drawing ids must be unique across the document, even when two of them share one image.</summary>
        public int NextDrawingId() => ++_drawingId;

        public string HeaderId(DocxHeaderFooter content) =>
            HeaderParts.First(p => ReferenceEquals(p.Part.Content, content)).Part.RelationshipId;

        public IEnumerable<string> Extensions => Media.Select(m => Path.GetExtension(m.FileName).TrimStart('.')).Distinct();

        private string NextId() => $"rId{_nextId++}";

        private void AddImage(PlacedImage image, IImageEncoder encoder)
        {
            if (_mediaByImage.ContainsKey(image)) return;

            byte[] bytes;
            string extension;
            if (image.Encoded is { Length: > 0 } encoded)
            {
                bytes = encoded;
                extension = image.Extension ?? "png";
            }
            else if (image.Bits is { } bits)
            {
                bytes = encoder.EncodePng(bits);
                extension = "png";
            }
            else
            {
                return;
            }

            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (_mediaByHash.TryGetValue(hash, out var existing))
            {
                _mediaByImage[image] = existing;
                return;
            }

            var part = new MediaPart($"image{Media.Count + 1}.{extension}", bytes, NextId(), Media.Count + 1);
            _mediaByHash[hash] = part;
            _mediaByImage[image] = part;
            Media.Add(part);
        }

        /// <summary>
        /// Word stores an embedded font "obfuscated": the first 32 bytes are XORed with the 16 bytes of
        /// the key in the part's own <c>w:fontKey</c>, taken in reverse order. It is not encryption — it
        /// only stops the file being mistaken for an installable font — but a font stored plainly is a
        /// font Word will not load.
        /// </summary>
        private static byte[] Obfuscate(byte[] font, string key)
        {
            var bytes = (byte[])font.Clone();
            string hex = key.Trim('{', '}').Replace("-", "", StringComparison.Ordinal);
            if (hex.Length != 32) return bytes;

            var mask = new byte[16];
            for (int i = 0; i < 16; i++) mask[i] = Convert.ToByte(hex.Substring(30 - i * 2, 2), 16);
            for (int i = 0; i < Math.Min(32, bytes.Length); i++) bytes[i] ^= mask[i % 16];
            return bytes;
        }

        private static IEnumerable<DocxBlock> Flatten(IEnumerable<DocxBlock> blocks)
        {
            foreach (var block in blocks)
            {
                yield return block;
                if (block is DocxTable table)
                    foreach (var nested in Flatten(table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks)))
                        yield return nested;
            }
        }
    }

    private static void WriteContentTypes(XmlWriter w, PackageParts parts)
    {
        w.WriteStartElement("Types", ContentTypesNs);
        Default("rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default("xml", "application/xml");
        foreach (string extension in parts.Extensions)
        {
            Default(extension, extension switch
            {
                "jpeg" or "jpg" => "image/jpeg",
                "png" => "image/png",
                "gif" => "image/gif",
                "bmp" => "image/bmp",
                "tiff" or "tif" => "image/tiff",
                _ => "application/octet-stream",
            });
        }

        if (parts.Fonts.Count > 0)
            Default("odttf", "application/vnd.openxmlformats-officedocument.obfuscatedFont");

        Override("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
        Override("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
        Override("/word/numbering.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml");
        Override("/word/settings.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml");
        if (parts.HasComments)
            Override("/word/comments.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml");
        if (parts.Fonts.Count > 0)
            Override("/word/fontTable.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml");
        foreach (var (path, part) in parts.HeaderParts)
        {
            Override("/" + path, part.IsHeader
                ? "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"
                : "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml");
        }
        Override("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        Override("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
        w.WriteEndElement();

        void Default(string extension, string type)
        {
            w.WriteStartElement("Default", ContentTypesNs);
            w.WriteAttributeString("Extension", extension);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        void Override(string part, string type)
        {
            w.WriteStartElement("Override", ContentTypesNs);
            w.WriteAttributeString("PartName", part);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }
    }

    /// <summary>
    /// Declares the document as current Word. Without it Word opens the file in "Compatibility Mode" as a
    /// Word 2007 document: the title bar says so, newer features are switched off, and tables are laid out
    /// by the old rules, shifted left by their cell margin.
    /// </summary>
    private static void WriteSettings(XmlWriter w)
    {
        w.WriteStartElement("w", "settings", WNs);
        w.WriteStartElement("w", "defaultTabStop", WNs);
        w.WriteAttributeString("w", "val", WNs, "720");
        w.WriteEndElement();
        w.WriteStartElement("w", "characterSpacingControl", WNs);
        w.WriteAttributeString("w", "val", WNs, "doNotCompress");
        w.WriteEndElement();
        w.WriteStartElement("w", "compat", WNs);
        w.WriteStartElement("w", "compatSetting", WNs);
        w.WriteAttributeString("w", "name", WNs, "compatibilityMode");
        w.WriteAttributeString("w", "uri", WNs, "http://schemas.microsoft.com/office/word");
        w.WriteAttributeString("w", "val", WNs, "15");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteRootRels(XmlWriter w)
    {
        w.WriteStartElement("Relationships", PkgRelNs);
        Relationship(w, "rId1", RNs + "/officeDocument", "word/document.xml");
        Relationship(w, "rId2", PkgRelNs.Replace("/relationships", "/relationships/metadata/core-properties"), "docProps/core.xml");
        Relationship(w, "rId3", RNs + "/extended-properties", "docProps/app.xml");
        w.WriteEndElement();
    }

    private static void WriteDocumentRels(XmlWriter w, PackageParts parts)
    {
        w.WriteStartElement("Relationships", PkgRelNs);
        Relationship(w, "rId1", RNs + "/styles", "styles.xml");
        Relationship(w, "rId2", RNs + "/numbering", "numbering.xml");
        Relationship(w, "rId3", RNs + "/settings", "settings.xml");
        foreach (var media in parts.Media)
            Relationship(w, media.RelationshipId, RNs + "/image", $"media/{media.FileName}");
        foreach (var (uri, id) in parts.Hyperlinks)
            Relationship(w, id, RNs + "/hyperlink", uri, external: true);
        foreach (var (path, part) in parts.HeaderParts)
            Relationship(w, part.RelationshipId, RNs + (part.IsHeader ? "/header" : "/footer"), Path.GetFileName(path));
        if (parts.CommentsRelationshipId is { } comments)
            Relationship(w, comments, RNs + "/comments", "comments.xml");
        if (parts.Fonts.Count > 0)
        {
            Relationship(w, parts.FontTableRelationshipId, RNs + "/fontTable", "fontTable.xml");
            foreach (var font in parts.Fonts)
                Relationship(w, font.RelationshipId, RNs + "/font", $"fonts/{font.FileName}");
        }
        w.WriteEndElement();
    }

    private static void Relationship(XmlWriter w, string id, string type, string target, bool external = false)
    {
        w.WriteStartElement("Relationship", PkgRelNs);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        if (external) w.WriteAttributeString("TargetMode", "External");
        w.WriteEndElement();
    }

    private static void WriteCoreProps(XmlWriter w, DocxDocument document)
    {
        w.WriteStartElement("cp", "coreProperties", CoreNs);
        w.WriteAttributeString("xmlns", "dc", null, DcNs);
        w.WriteAttributeString("xmlns", "dcterms", null, DcTermsNs);
        w.WriteAttributeString("xmlns", "xsi", null, XsiNs);
        if (!string.IsNullOrWhiteSpace(document.Title)) w.WriteElementString("title", DcNs, document.Title);
        if (!string.IsNullOrWhiteSpace(document.Author)) w.WriteElementString("creator", DcNs, document.Author);
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        foreach (string element in (string[])["created", "modified"])
        {
            w.WriteStartElement("dcterms", element, DcTermsNs);
            w.WriteAttributeString("xsi", "type", XsiNs, "dcterms:W3CDTF");
            w.WriteString(now);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteAppProps(XmlWriter w)
    {
        w.WriteStartElement("Properties", ExtendedNs);
        w.WriteElementString("Application", ExtendedNs, "LitePDF");
        w.WriteEndElement();
    }

    // ---- styles ----

    private static void WriteStyles(XmlWriter w, DocxDocument document)
    {
        int bodyHalfPoints = Math.Max(2, (int)Math.Round(document.BodySizePoints * 2));
        w.WriteStartElement("w", "styles", WNs);

        w.WriteStartElement("w", "docDefaults", WNs);
        w.WriteStartElement("w", "rPrDefault", WNs);
        w.WriteStartElement("w", "rPr", WNs);
        Fonts(document.BodyFont);
        Val("sz", bodyHalfPoints.ToString());
        Val("szCs", bodyHalfPoints.ToString());
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("w", "pPrDefault", WNs);
        w.WriteStartElement("w", "pPr", WNs);
        w.WriteStartElement("w", "spacing", WNs);
        w.WriteAttributeString("w", "after", WNs, "120");
        w.WriteAttributeString("w", "line", WNs, "259");
        w.WriteAttributeString("w", "lineRule", WNs, "auto");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();

        Style("Normal", "Normal", isDefault: true, null, null);
        // Heading sizes are relative to the measured body size, so a document set in 9 pt does not get 16 pt headings.
        Style("Heading1", "heading 1", false, (int)Math.Round(bodyHalfPoints * 1.75), true, outline: 0);
        Style("Heading2", "heading 2", false, (int)Math.Round(bodyHalfPoints * 1.45), true, outline: 1);
        Style("Heading3", "heading 3", false, (int)Math.Round(bodyHalfPoints * 1.22), true, outline: 2);
        Style("Heading4", "heading 4", false, (int)Math.Round(bodyHalfPoints * 1.08), true, outline: 3);
        Style("Caption", "caption", false, Math.Max(2, bodyHalfPoints - 2), false, italic: true);

        Style("CommentText", "annotation text", false, Math.Max(2, bodyHalfPoints - 2), false);

        w.WriteStartElement("w", "style", WNs);
        w.WriteAttributeString("w", "type", WNs, "character");
        w.WriteAttributeString("w", "styleId", WNs, "CommentReference");
        Val("name", "annotation reference");
        w.WriteStartElement("w", "rPr", WNs);
        Val("sz", "16");
        Val("szCs", "16");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("w", "style", WNs);
        w.WriteAttributeString("w", "type", WNs, "character");
        w.WriteAttributeString("w", "styleId", WNs, "Hyperlink");
        Val("name", "Hyperlink");
        w.WriteStartElement("w", "rPr", WNs);
        w.WriteStartElement("w", "color", WNs);
        w.WriteAttributeString("w", "val", WNs, "0563C1");
        w.WriteEndElement();
        Val("u", "single");
        w.WriteEndElement();
        w.WriteEndElement();

        // Word's own default table style. Without it a table has no cell margins at all.
        w.WriteStartElement("w", "style", WNs);
        w.WriteAttributeString("w", "type", WNs, "table");
        w.WriteAttributeString("w", "default", WNs, "1");
        w.WriteAttributeString("w", "styleId", WNs, "TableNormal");
        Val("name", "Normal Table");
        w.WriteStartElement("w", "tblPr", WNs);
        w.WriteStartElement("w", "tblCellMar", WNs);
        foreach (var (edge, value) in (ReadOnlySpan<(string, string)>)[("top", "0"), ("left", "108"), ("bottom", "0"), ("right", "108")])
        {
            w.WriteStartElement("w", edge, WNs);
            w.WriteAttributeString("w", "w", WNs, value);
            w.WriteAttributeString("w", "type", WNs, "dxa");
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("w", "style", WNs);
        w.WriteAttributeString("w", "type", WNs, "table");
        w.WriteAttributeString("w", "styleId", WNs, "TableGrid");
        Val("name", "Table Grid");
        Val("basedOn", "TableNormal");
        w.WriteStartElement("w", "tblPr", WNs);
        WriteTableBorders(w);
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteEndElement();

        void Fonts(string family)
        {
            w.WriteStartElement("w", "rFonts", WNs);
            w.WriteAttributeString("w", "ascii", WNs, family);
            w.WriteAttributeString("w", "hAnsi", WNs, family);
            w.WriteAttributeString("w", "cs", WNs, family);
            w.WriteEndElement();
        }

        void Val(string name, string value)
        {
            w.WriteStartElement("w", name, WNs);
            w.WriteAttributeString("w", "val", WNs, value);
            w.WriteEndElement();
        }

        void Style(string id, string name, bool isDefault, int? halfPoints, bool? bold, bool italic = false, int? outline = null)
        {
            w.WriteStartElement("w", "style", WNs);
            w.WriteAttributeString("w", "type", WNs, "paragraph");
            if (isDefault) w.WriteAttributeString("w", "default", WNs, "1");
            w.WriteAttributeString("w", "styleId", WNs, id);
            Val("name", name);
            if (!isDefault) Val("basedOn", "Normal");
            if (outline is not null)
            {
                w.WriteStartElement("w", "pPr", WNs);
                Val("keepNext", "1");
                Val("outlineLvl", outline.Value.ToString());
                w.WriteStartElement("w", "spacing", WNs);
                w.WriteAttributeString("w", "before", WNs, "240");
                w.WriteAttributeString("w", "after", WNs, "120");
                w.WriteEndElement();
                w.WriteEndElement();
            }
            if (halfPoints is not null || bold is true || italic)
            {
                w.WriteStartElement("w", "rPr", WNs);
                if (bold is true) w.WriteElementString("w", "b", WNs, null);
                if (italic) w.WriteElementString("w", "i", WNs, null);
                if (halfPoints is { } size)
                {
                    Val("sz", size.ToString());
                    Val("szCs", size.ToString());
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
    }

    private static void WriteTableBorders(XmlWriter w, int eighths = 4, uint? color = null)
    {
        w.WriteStartElement("w", "tblBorders", WNs);
        foreach (string edge in (string[])["top", "left", "bottom", "right", "insideH", "insideV"])
        {
            w.WriteStartElement("w", edge, WNs);
            w.WriteAttributeString("w", "val", WNs, "single");
            w.WriteAttributeString("w", "sz", WNs, BorderSize(eighths).ToString());
            w.WriteAttributeString("w", "space", WNs, "0");
            w.WriteAttributeString("w", "color", WNs, color is { } c ? Hex(c) : "auto");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteNumbering(XmlWriter w, PackageParts parts)
    {
        w.WriteStartElement("w", "numbering", WNs);
        AbstractNum(0, bullet: true);
        AbstractNum(1, bullet: false);
        foreach (var (id, paragraph) in parts.NumberingDefinitions)
        {
            var (format, start, label) = NumberLabel(paragraph.ListMarker!, paragraph.ListLevel);
            w.WriteStartElement("w", "abstractNum", WNs);
            w.WriteAttributeString("w", "abstractNumId", WNs, id.ToString());
            w.WriteStartElement("w", "lvl", WNs);
            w.WriteAttributeString("w", "ilvl", WNs, Math.Clamp(paragraph.ListLevel, 0, 4).ToString());
            Val("start", start.ToString());
            Val("numFmt", format);
            Val("lvlText", label);
            w.WriteStartElement("w", "pPr", WNs);
            w.WriteStartElement("w", "ind", WNs);
            w.WriteAttributeString("w", "left", WNs, (720 * (Math.Clamp(paragraph.ListLevel, 0, 4) + 1)).ToString());
            w.WriteAttributeString("w", "hanging", WNs, "360");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }
        Num(BulletNumId, 0);
        Num(NumberNumId, 1);
        foreach (int id in parts.NumberingDefinitions.Keys) Num(id, id);
        w.WriteEndElement();

        void AbstractNum(int id, bool bullet)
        {
            w.WriteStartElement("w", "abstractNum", WNs);
            w.WriteAttributeString("w", "abstractNumId", WNs, id.ToString());
            for (int level = 0; level < 5; level++)
            {
                w.WriteStartElement("w", "lvl", WNs);
                w.WriteAttributeString("w", "ilvl", WNs, level.ToString());
                Val("start", "1");
                Val("numFmt", bullet ? "bullet" : (level % 3) switch { 0 => "decimal", 1 => "lowerLetter", _ => "lowerRoman" });
                // Symbol/Wingdings bullet code points, which is how Word stores a bulleted list.
                Val("lvlText", bullet
                    ? (level % 3) switch { 0 => ((char)0xF0B7).ToString(), 1 => "o", _ => ((char)0xF0A7).ToString() }
                    : $"%{level + 1}.");
                w.WriteStartElement("w", "pPr", WNs);
                w.WriteStartElement("w", "ind", WNs);
                w.WriteAttributeString("w", "left", WNs, (720 * (level + 1)).ToString());
                w.WriteAttributeString("w", "hanging", WNs, "360");
                w.WriteEndElement();
                w.WriteEndElement();
                if (bullet)
                {
                    w.WriteStartElement("w", "rPr", WNs);
                    w.WriteStartElement("w", "rFonts", WNs);
                    string font = level % 3 == 1 ? "Courier New" : level % 3 == 2 ? "Wingdings" : "Symbol";
                    w.WriteAttributeString("w", "ascii", WNs, font);
                    w.WriteAttributeString("w", "hAnsi", WNs, font);
                    w.WriteAttributeString("w", "hint", WNs, "default");
                    w.WriteEndElement();
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        void Num(int numId, int abstractId)
        {
            w.WriteStartElement("w", "num", WNs);
            w.WriteAttributeString("w", "numId", WNs, numId.ToString());
            w.WriteStartElement("w", "abstractNumId", WNs);
            w.WriteAttributeString("w", "val", WNs, abstractId.ToString());
            w.WriteEndElement();
            w.WriteEndElement();
        }

        void Val(string name, string value)
        {
            w.WriteStartElement("w", name, WNs);
            w.WriteAttributeString("w", "val", WNs, value);
            w.WriteEndElement();
        }
    }

    private static (string Format, int Start, string Label) NumberLabel(string marker, int level)
    {
        string token = marker.Trim('(', ')', '.');
        if (token.Length > 1 && token[0] == '0') return ("none", 1, marker);
        string format;
        int start;
        if (int.TryParse(token, out start)) format = "decimal";
        else if ((token.Length > 1 && token.All(c => "ivxlcdmIVXLCDM".Contains(c))) || token is "i" or "I")
        {
            format = char.IsUpper(token[0]) ? "upperRoman" : "lowerRoman";
            start = 0;
            int previous = 0;
            foreach (char c in token.ToUpperInvariant().Reverse())
            {
                int value = c switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => 0 };
                start += value < previous ? -value : value;
                previous = value;
            }
        }
        else if (token.Length == 1 && char.IsAsciiLetter(token[0]))
        {
            format = char.IsUpper(token[0]) ? "upperLetter" : "lowerLetter";
            start = char.ToUpperInvariant(token[0]) - 'A' + 1;
        }
        else return ("none", 1, marker);

        string label = marker.Replace(token, $"%{Math.Clamp(level, 0, 4) + 1}", StringComparison.Ordinal);
        return (format, Math.Max(0, start), label);
    }

    // ---- body ----

    private static void WriteDocument(XmlWriter w, DocxDocument document, PackageParts parts)
    {
        w.WriteStartElement("w", "document", WNs);
        w.WriteAttributeString("xmlns", "r", null, RNs);
        w.WriteAttributeString("xmlns", "wp", null, WpNs);
        w.WriteAttributeString("xmlns", "a", null, ANs);
        w.WriteAttributeString("xmlns", "pic", null, PicNs);
        w.WriteStartElement("w", "body", WNs);

        for (int s = 0; s < document.Sections.Count; s++)
        {
            var section = document.Sections[s];
            bool last = s == document.Sections.Count - 1;
            var blocks = section.Blocks;

            // A section that is not the last carries its properties on its final paragraph, so the break
            // happens there. Word requires a paragraph to hang them on, and a table may not be last.
            bool endsWithParagraph = blocks.Count > 0 && blocks[^1] is DocxParagraph;
            if (!last && endsWithParagraph)
            {
                WriteBlocks(w, blocks.Take(blocks.Count - 1).ToList(), parts);
                WriteParagraph(w, (DocxParagraph)blocks[^1], parts, section);
            }
            else
            {
                WriteBlocks(w, blocks, parts);
                if (!last) WriteSpacerParagraph(w, section, parts);
                else if (blocks.Count == 0 || blocks[^1] is DocxTable) WriteSpacerParagraph(w);
            }

            if (last) WriteSectionProperties(w, section, parts);
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteSectionProperties(XmlWriter w, DocxSection section, PackageParts parts)
    {
        w.WriteStartElement("w", "sectPr", WNs);

        Reference("headerReference", "default", section.Header);
        Reference("footerReference", "default", section.Footer);
        if (section.DifferentFirstPage)
        {
            Reference("headerReference", "first", section.FirstHeader);
            Reference("footerReference", "first", section.FirstFooter);
        }
        if (section.Continuous)
        {
            w.WriteStartElement("w", "type", WNs);
            w.WriteAttributeString("w", "val", WNs, "continuous");
            w.WriteEndElement();
        }

        w.WriteStartElement("w", "pgSz", WNs);
        w.WriteAttributeString("w", "w", WNs, Twips(section.Size.Width).ToString());
        w.WriteAttributeString("w", "h", WNs, Twips(section.Size.Height).ToString());
        if (section.Landscape) w.WriteAttributeString("w", "orient", WNs, "landscape");
        w.WriteEndElement();

        var margins = section.Margins;
        w.WriteStartElement("w", "pgMar", WNs);
        w.WriteAttributeString("w", "top", WNs, Twips(margins.Top).ToString());
        w.WriteAttributeString("w", "right", WNs, Twips(margins.Right).ToString());
        w.WriteAttributeString("w", "bottom", WNs, Twips(margins.Bottom).ToString());
        w.WriteAttributeString("w", "left", WNs, Twips(margins.Left).ToString());
        // A running head that sits lower than the text area's top would push the body down in Word, so the
        // distance can never exceed the margin it lives in.
        w.WriteAttributeString("w", "header", WNs, Twips(Math.Clamp(section.HeaderDistancePoints, 0, Math.Max(0, margins.Top - 6))).ToString());
        w.WriteAttributeString("w", "footer", WNs, Twips(Math.Clamp(section.FooterDistancePoints, 0, Math.Max(0, margins.Bottom - 6))).ToString());
        w.WriteAttributeString("w", "gutter", WNs, "0");
        w.WriteEndElement();

        // Schema order: pgNumType, then cols, then titlePg.
        if (section.PageNumberStart > 0)
        {
            w.WriteStartElement("w", "pgNumType", WNs);
            w.WriteAttributeString("w", "start", WNs, section.PageNumberStart.ToString());
            w.WriteEndElement();
        }

        WriteColumns(w, section.Columns);

        if (section.DifferentFirstPage) w.WriteElementString("w", "titlePg", WNs, null);

        w.WriteEndElement();

        void Reference(string element, string type, DocxHeaderFooter? content)
        {
            if (content is null) return;
            w.WriteStartElement("w", element, WNs);
            w.WriteAttributeString("w", "type", WNs, type);
            w.WriteAttributeString("r", "id", RNs, parts.HeaderId(content));
            w.WriteEndElement();
        }
    }

    private static void WriteColumns(XmlWriter w, DocxColumns columns)
    {
        w.WriteStartElement("w", "cols", WNs);
        if (columns.IsSingle)
        {
            w.WriteAttributeString("w", "space", WNs, "708");
            w.WriteEndElement();
            return;
        }

        w.WriteAttributeString("w", "num", WNs, columns.Count.ToString());
        w.WriteAttributeString("w", "space", WNs, Twips(Math.Max(0, columns.SpacePoints)).ToString());
        bool unequal = columns.WidthsPoints.Count == columns.Count;
        w.WriteAttributeString("w", "equalWidth", WNs, unequal ? "0" : "1");
        if (unequal)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                w.WriteStartElement("w", "col", WNs);
                w.WriteAttributeString("w", "w", WNs, Twips(Math.Max(36, columns.WidthsPoints[i])).ToString());
                if (i < columns.Count - 1)
                {
                    double gap = i < columns.GapsPoints.Count ? columns.GapsPoints[i] : columns.SpacePoints;
                    w.WriteAttributeString("w", "space", WNs, Twips(Math.Max(0, gap)).ToString());
                }
                w.WriteEndElement();
            }
        }
        w.WriteEndElement();
    }

    private static void WriteBlock(XmlWriter w, DocxBlock block, PackageParts parts)
    {
        switch (block)
        {
            case DocxParagraph paragraph:
                WriteParagraph(w, paragraph, parts);
                break;
            case DocxPicture picture:
                WritePicture(w, picture, parts);
                break;
            case DocxTable table:
                WriteTable(w, table, parts);
                break;
        }
    }

    private static void WriteParagraph(XmlWriter w, DocxParagraph paragraph, PackageParts parts, DocxSection? section = null)
    {
        w.WriteStartElement("w", "p", WNs);
        WriteParagraphProperties(w, paragraph, parts, section);

        foreach (var picture in paragraph.Floating)
        {
            if (parts.Image(picture.Image) is null) continue;
            w.WriteStartElement("w", "r", WNs);
            WriteDrawing(w, picture, parts);
            w.WriteEndElement();
        }

        foreach (int id in paragraph.CommentIds) Marker(w, "commentRangeStart", id);

        foreach (var run in paragraph.Runs)
        {
            if (run.Hyperlink is { Length: > 0 } uri && parts.Hyperlinks.TryGetValue(uri, out string? id))
            {
                w.WriteStartElement("w", "hyperlink", WNs);
                w.WriteAttributeString("r", "id", RNs, id);
                WriteRun(w, run, hyperlinkStyle: true);
                w.WriteEndElement();
            }
            else
            {
                WriteRun(w, run, hyperlinkStyle: false);
            }
        }

        if (paragraph.ColumnBreakAfter)
        {
            w.WriteStartElement("w", "r", WNs);
            w.WriteStartElement("w", "br", WNs);
            w.WriteAttributeString("w", "type", WNs, "column");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        foreach (int id in paragraph.CommentIds)
        {
            Marker(w, "commentRangeEnd", id);

            // The reference is what Word draws the bubble against; without it the range is invisible.
            w.WriteStartElement("w", "r", WNs);
            w.WriteStartElement("w", "rPr", WNs);
            w.WriteStartElement("w", "rStyle", WNs);
            w.WriteAttributeString("w", "val", WNs, "CommentReference");
            w.WriteEndElement();
            w.WriteEndElement();
            Marker(w, "commentReference", id);
            w.WriteEndElement();
        }

        w.WriteEndElement();

        static void Marker(XmlWriter w, string name, int id)
        {
            w.WriteStartElement("w", name, WNs);
            w.WriteAttributeString("w", "id", WNs, id.ToString());
            w.WriteEndElement();
        }
    }

    private static void WriteComments(XmlWriter w, DocxDocument document)
    {
        w.WriteStartElement("w", "comments", WNs);
        foreach (var comment in document.Comments)
        {
            w.WriteStartElement("w", "comment", WNs);
            w.WriteAttributeString("w", "id", WNs, comment.Id.ToString());
            w.WriteAttributeString("w", "author", WNs, comment.Author);
            if (comment.Initials is { Length: > 0 }) w.WriteAttributeString("w", "initials", WNs, comment.Initials);
            if (comment.Date is { } date)
                w.WriteAttributeString("w", "date", WNs, date.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));

            // A note wraps where the writer of it wrapped: each line of the note is a paragraph.
            foreach (string line in comment.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                w.WriteStartElement("w", "p", WNs);
                w.WriteStartElement("w", "pPr", WNs);
                w.WriteStartElement("w", "pStyle", WNs);
                w.WriteAttributeString("w", "val", WNs, "CommentText");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteStartElement("w", "r", WNs);
                WriteText(w, line);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    /// <summary>
    /// Names the embedded fonts and hands Word the key to each. <c>w:subsetted</c> is the honest part: the
    /// PDF stored only the glyphs it printed, so a letter typed into the document afterwards has no glyph.
    /// </summary>
    private static void WriteFontTable(XmlWriter w, PackageParts parts)
    {
        w.WriteStartElement("w", "fonts", WNs);
        w.WriteAttributeString("xmlns", "r", null, RNs);

        foreach (var group in parts.Fonts.GroupBy(f => f.Font.Family, StringComparer.Ordinal))
        {
            w.WriteStartElement("w", "font", WNs);
            w.WriteAttributeString("w", "name", WNs, group.Key);
            foreach (var font in group)
            {
                string element = (font.Font.Bold, font.Font.Italic) switch
                {
                    (true, true) => "embedBoldItalic",
                    (true, false) => "embedBold",
                    (false, true) => "embedItalic",
                    _ => "embedRegular",
                };
                w.WriteStartElement("w", element, WNs);
                w.WriteAttributeString("r", "id", RNs, font.RelationshipId);
                w.WriteAttributeString("w", "fontKey", WNs, font.Key);
                if (font.Font.IsSubset) w.WriteAttributeString("w", "subsetted", WNs, "true");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void WriteParagraphProperties(XmlWriter w, DocxParagraph paragraph, PackageParts parts, DocxSection? section)
    {
        bool hasStyle = paragraph.Style != DocxParagraphStyle.Body;
        bool hasIndent = paragraph.IndentTwips != 0 || paragraph.FirstLineTwips != 0 || paragraph.RightIndentTwips != 0;
        bool hasSpacing = paragraph.ExplicitSpacing || paragraph.SpaceBeforeTwips != 0 || paragraph.SpaceAfterTwips != 0 || paragraph.LineSpacing != 0;
        bool hasList = paragraph.List != DocxListKind.None;
        if (!hasStyle && !hasIndent && !hasSpacing && !hasList && !paragraph.KeepWithNext && paragraph.MarkerStyle is null &&
            paragraph.BorderAbove is null && paragraph.BorderBelow is null && paragraph.Box is null && paragraph.TabStops.Count == 0 &&
            paragraph.Alignment == DocxAlignment.Left && !paragraph.PageBreakBefore && section is null)
            return;

        w.WriteStartElement("w", "pPr", WNs);
        if (hasStyle)
        {
            w.WriteStartElement("w", "pStyle", WNs);
            w.WriteAttributeString("w", "val", WNs, paragraph.Style.ToString());
            w.WriteEndElement();
        }
        if (paragraph.KeepWithNext) w.WriteElementString("w", "keepNext", WNs, null);
        if (paragraph.PageBreakBefore) w.WriteElementString("w", "pageBreakBefore", WNs, null);
        if (hasList)
        {
            w.WriteStartElement("w", "numPr", WNs);
            w.WriteStartElement("w", "ilvl", WNs);
            w.WriteAttributeString("w", "val", WNs, Math.Clamp(paragraph.ListLevel, 0, 4).ToString());
            w.WriteEndElement();
            w.WriteStartElement("w", "numId", WNs);
            w.WriteAttributeString("w", "val", WNs, parts.NumberId(paragraph).ToString());
            w.WriteEndElement();
            w.WriteEndElement();
        }
        if (paragraph.Box is { } box) WriteBox(w, box);
        else WriteParagraphBorders(w, paragraph.BorderAbove, paragraph.BorderBelow);
        WriteTabs(w, paragraph.TabStops);
        if (hasSpacing)
        {
            w.WriteStartElement("w", "spacing", WNs);
            if (paragraph.ExplicitSpacing || paragraph.SpaceBeforeTwips != 0) w.WriteAttributeString("w", "before", WNs, paragraph.SpaceBeforeTwips.ToString());
            if (paragraph.ExplicitSpacing || paragraph.SpaceAfterTwips != 0) w.WriteAttributeString("w", "after", WNs, paragraph.SpaceAfterTwips.ToString());
            if (paragraph.LineSpacing != 0)
            {
                w.WriteAttributeString("w", "line", WNs, paragraph.LineSpacing.ToString());
                w.WriteAttributeString("w", "lineRule", WNs, paragraph.LineRule switch
                {
                    DocxLineRule.Auto => "auto",
                    DocxLineRule.Exact => "exact",
                    _ => "atLeast",
                });
            }
            w.WriteEndElement();
        }
        if (hasIndent)
        {
            w.WriteStartElement("w", "ind", WNs);
            if (paragraph.IndentTwips != 0) w.WriteAttributeString("w", "left", WNs, paragraph.IndentTwips.ToString());
            if (paragraph.RightIndentTwips != 0) w.WriteAttributeString("w", "right", WNs, paragraph.RightIndentTwips.ToString());
            if (paragraph.FirstLineTwips > 0) w.WriteAttributeString("w", "firstLine", WNs, paragraph.FirstLineTwips.ToString());
            else if (paragraph.FirstLineTwips < 0) w.WriteAttributeString("w", "hanging", WNs, (-paragraph.FirstLineTwips).ToString());
            w.WriteEndElement();
        }
        if (paragraph.Alignment != DocxAlignment.Left)
        {
            w.WriteStartElement("w", "jc", WNs);
            w.WriteAttributeString("w", "val", WNs, paragraph.Alignment switch
            {
                DocxAlignment.Center => "center",
                DocxAlignment.Right => "right",
                DocxAlignment.Justify => "both",
                _ => "left",
            });
            w.WriteEndElement();
        }
        if (hasList && paragraph.MarkerStyle is { } marker)
        {
            // The paragraph mark's look, which is what Word draws the list label in.
            w.WriteStartElement("w", "rPr", WNs);
            w.WriteStartElement("w", "rFonts", WNs);
            w.WriteAttributeString("w", "ascii", WNs, marker.FontFamily);
            w.WriteAttributeString("w", "hAnsi", WNs, marker.FontFamily);
            w.WriteAttributeString("w", "cs", WNs, marker.FontFamily);
            w.WriteEndElement();
            if (marker.Bold) w.WriteElementString("w", "b", WNs, null);
            if (marker.Italic) w.WriteElementString("w", "i", WNs, null);
            w.WriteStartElement("w", "color", WNs);
            w.WriteAttributeString("w", "val", WNs, Hex(marker.Color));
            w.WriteEndElement();
            string half = Math.Clamp((int)Math.Round(marker.SizePoints * 2), 2, 3276).ToString();
            w.WriteStartElement("w", "sz", WNs);
            w.WriteAttributeString("w", "val", WNs, half);
            w.WriteEndElement();
            w.WriteStartElement("w", "szCs", WNs);
            w.WriteAttributeString("w", "val", WNs, half);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        if (section is not null) WriteSectionProperties(w, section, parts);
        w.WriteEndElement();
    }

    private static void WriteRun(XmlWriter w, DocxRun run, bool hyperlinkStyle)
    {
        w.WriteStartElement("w", "r", WNs);
        WriteRunProperties(w, run, hyperlinkStyle);
        WriteText(w, run.Text);
        w.WriteEndElement();
    }

    private static void WriteRunProperties(XmlWriter w, DocxRun run, bool hyperlinkStyle)
    {
        w.WriteStartElement("w", "rPr", WNs);
        if (hyperlinkStyle)
        {
            w.WriteStartElement("w", "rStyle", WNs);
            w.WriteAttributeString("w", "val", WNs, "Hyperlink");
            w.WriteEndElement();
        }
        w.WriteStartElement("w", "rFonts", WNs);
        w.WriteAttributeString("w", "ascii", WNs, run.Style.FontFamily);
        w.WriteAttributeString("w", "hAnsi", WNs, run.Style.FontFamily);
        w.WriteAttributeString("w", "cs", WNs, run.Style.FontFamily);
        w.WriteEndElement();
        // Schema order: b, i, strike, color, spacing, sz, szCs, highlight, u, vertAlign.
        if (run.Style.Bold) w.WriteElementString("w", "b", WNs, null);
        if (run.Style.Italic) w.WriteElementString("w", "i", WNs, null);
        if (run.Strikethrough) w.WriteElementString("w", "strike", WNs, null);
        if (!hyperlinkStyle)
        {
            w.WriteStartElement("w", "color", WNs);
            w.WriteAttributeString("w", "val", WNs, Hex(run.Style.Color));
            w.WriteEndElement();
        }
        if (run.CharacterSpacingTwips != 0)
        {
            w.WriteStartElement("w", "spacing", WNs);
            w.WriteAttributeString("w", "val", WNs, Math.Clamp(run.CharacterSpacingTwips, -1000, 1000).ToString());
            w.WriteEndElement();
        }
        int halfPoints = Math.Clamp((int)Math.Round(run.Style.SizePoints * 2), 2, 3276);
        w.WriteStartElement("w", "sz", WNs);
        w.WriteAttributeString("w", "val", WNs, halfPoints.ToString());
        w.WriteEndElement();
        w.WriteStartElement("w", "szCs", WNs);
        w.WriteAttributeString("w", "val", WNs, halfPoints.ToString());
        w.WriteEndElement();
        if (run.Highlight is { } highlight)
        {
            w.WriteStartElement("w", "highlight", WNs);
            w.WriteAttributeString("w", "val", WNs, NearestHighlight(highlight));
            w.WriteEndElement();
        }
        if (run.Underline)
        {
            w.WriteStartElement("w", "u", WNs);
            w.WriteAttributeString("w", "val", WNs, "single");
            w.WriteEndElement();
        }
        if (run.Script != DocxScript.Baseline)
        {
            w.WriteStartElement("w", "vertAlign", WNs);
            w.WriteAttributeString("w", "val", WNs, run.Script == DocxScript.Superscript ? "superscript" : "subscript");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    /// <summary>Writes run text, turning newlines into breaks and dropping characters XML cannot carry.</summary>
    private static void WriteText(XmlWriter w, string text)
    {
        var buffer = new StringBuilder(text.Length);
        bool first = true;

        void Flush()
        {
            if (buffer.Length == 0 && !first) return;
            w.WriteStartElement("w", "t", WNs);
            w.WriteAttributeString("xml", "space", null, "preserve");
            w.WriteString(buffer.ToString());
            w.WriteEndElement();
            buffer.Clear();
            first = false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == (char)0x2028 || c == (char)0x2029)
            {
                Flush();
                w.WriteElementString("w", "br", WNs, null);
                first = true;
                continue;
            }
            if (c == '\t')
            {
                Flush();
                w.WriteElementString("w", "tab", WNs, null);
                first = true;
                continue;
            }
            if (c < 0x20 || c == 0x7F || c >= 0xFFFE) continue; // not valid XML content
            if (char.IsHighSurrogate(c))
            {
                // A lone half of a pair would make XmlWriter throw, so only complete pairs are kept.
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    buffer.Append(c).Append(text[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            buffer.Append(c);
        }
        Flush();
    }

    /// <summary>An inline picture, as a paragraph of its own.</summary>
    private static void WritePicture(XmlWriter w, DocxPicture picture, PackageParts parts)
    {
        if (parts.Image(picture.Image) is null) return;

        if (picture.Wrap != DocxWrap.Inline)
        {
            // A floating picture with no paragraph to hang from gets one that takes no room.
            w.WriteStartElement("w", "p", WNs);
            w.WriteStartElement("w", "pPr", WNs);
            w.WriteStartElement("w", "spacing", WNs);
            w.WriteAttributeString("w", "before", WNs, "0");
            w.WriteAttributeString("w", "after", WNs, "0");
            w.WriteAttributeString("w", "line", WNs, "20");
            w.WriteAttributeString("w", "lineRule", WNs, "exact");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("w", "r", WNs);
            WriteDrawing(w, picture, parts);
            w.WriteEndElement();
            w.WriteEndElement();
            return;
        }

        w.WriteStartElement("w", "p", WNs);
        w.WriteStartElement("w", "pPr", WNs);

        // The paragraph is exactly as tall as the picture. Inherited spacing would stretch it: a multiple
        // applies to a picture as it does to text, and a 200 pt figure at 1.08 lines gains 16 pt.
        w.WriteStartElement("w", "spacing", WNs);
        w.WriteAttributeString("w", "before", WNs, picture.SpaceBeforeTwips.ToString());
        w.WriteAttributeString("w", "after", WNs, picture.SpaceAfterTwips.ToString());
        w.WriteAttributeString("w", "line", WNs, "240");
        w.WriteAttributeString("w", "lineRule", WNs, "auto");
        w.WriteEndElement();
        if (picture.Alignment == DocxAlignment.Left && picture.IndentTwips > 0)
        {
            w.WriteStartElement("w", "ind", WNs);
            w.WriteAttributeString("w", "left", WNs, picture.IndentTwips.ToString());
            w.WriteEndElement();
        }
        if (picture.Alignment != DocxAlignment.Left)
        {
            w.WriteStartElement("w", "jc", WNs);
            w.WriteAttributeString("w", "val", WNs, picture.Alignment == DocxAlignment.Right ? "right" : "center");
            w.WriteEndElement();
        }
        // The picture sits on the baseline, and below it the line keeps its font's descent. At one point
        // that is a fifth of a point, and the paragraph is as tall as the picture it holds.
        WriteTinyMark(w);
        w.WriteEndElement();

        w.WriteStartElement("w", "r", WNs);
        WriteTinyMark(w);
        WriteDrawing(w, picture with { Wrap = DocxWrap.Inline }, parts);
        w.WriteEndElement();
        w.WriteEndElement();

        static void WriteTinyMark(XmlWriter w)
        {
            w.WriteStartElement("w", "rPr", WNs);
            w.WriteStartElement("w", "sz", WNs);
            w.WriteAttributeString("w", "val", WNs, "2");
            w.WriteEndElement();
            w.WriteStartElement("w", "szCs", WNs);
            w.WriteAttributeString("w", "val", WNs, "2");
            w.WriteEndElement();
            w.WriteEndElement();
        }
    }

    /// <summary>
    /// The <c>w:drawing</c> for a picture: <c>wp:inline</c> in the flow, or <c>wp:anchor</c> positioned from
    /// the page's left edge and from the anchoring paragraph's top (or the page's). The caller supplies the run.
    /// </summary>
    private static void WriteDrawing(XmlWriter w, DocxPicture picture, PackageParts parts)
    {
        if (parts.Image(picture.Image) is not { } media) return;

        string relationshipId = media.RelationshipId;
        long cx = Math.Max(1, (long)Math.Round(picture.WidthPoints * EmuPerPoint));
        long cy = Math.Max(1, (long)Math.Round(picture.HeightPoints * EmuPerPoint));
        string name = media.FileName;
        int id = parts.NextDrawingId();
        bool inline = picture.Wrap == DocxWrap.Inline;

        w.WriteStartElement("w", "drawing", WNs);
        if (inline)
        {
            w.WriteStartElement("wp", "inline", WpNs);
            foreach (string edge in (string[])["distT", "distB", "distL", "distR"]) w.WriteAttributeString(edge, "0");
        }
        else
        {
            // The order of the children is fixed by the schema, and Word refuses a document that breaks it.
            bool square = picture.Wrap == DocxWrap.Square;
            w.WriteStartElement("wp", "anchor", WpNs);
            w.WriteAttributeString("distT", "0");
            w.WriteAttributeString("distB", "0");
            w.WriteAttributeString("distL", square ? "114300" : "0");
            w.WriteAttributeString("distR", square ? "114300" : "0");
            w.WriteAttributeString("simplePos", "0");
            w.WriteAttributeString("relativeHeight", (251658240 + id).ToString());
            w.WriteAttributeString("behindDoc", picture.Wrap == DocxWrap.BehindText ? "1" : "0");
            w.WriteAttributeString("locked", "0");
            w.WriteAttributeString("layoutInCell", "1");
            w.WriteAttributeString("allowOverlap", "1");

            w.WriteStartElement("wp", "simplePos", WpNs);
            w.WriteAttributeString("x", "0");
            w.WriteAttributeString("y", "0");
            w.WriteEndElement();

            w.WriteStartElement("wp", "positionH", WpNs);
            w.WriteAttributeString("relativeFrom", "page");
            w.WriteElementString("wp", "posOffset", WpNs, ((long)Math.Round(picture.OffsetXPoints * EmuPerPoint)).ToString());
            w.WriteEndElement();

            w.WriteStartElement("wp", "positionV", WpNs);
            w.WriteAttributeString("relativeFrom", picture.VerticalFromPage ? "page" : "paragraph");
            w.WriteElementString("wp", "posOffset", WpNs, ((long)Math.Round(picture.OffsetYPoints * EmuPerPoint)).ToString());
            w.WriteEndElement();
        }

        w.WriteStartElement("wp", "extent", WpNs);
        w.WriteAttributeString("cx", cx.ToString());
        w.WriteAttributeString("cy", cy.ToString());
        w.WriteEndElement();

        w.WriteStartElement("wp", "effectExtent", WpNs);
        foreach (string edge in (string[])["l", "t", "r", "b"]) w.WriteAttributeString(edge, "0");
        w.WriteEndElement();

        if (!inline)
        {
            if (picture.Wrap == DocxWrap.Square)
            {
                w.WriteStartElement("wp", "wrapSquare", WpNs);
                w.WriteAttributeString("wrapText", "bothSides");
                w.WriteEndElement();
            }
            else
            {
                w.WriteElementString("wp", "wrapNone", WpNs, null);
            }
        }

        w.WriteStartElement("wp", "docPr", WpNs);
        w.WriteAttributeString("id", id.ToString());
        w.WriteAttributeString("name", name);
        if (!string.IsNullOrWhiteSpace(picture.AltText)) w.WriteAttributeString("descr", picture.AltText);
        w.WriteEndElement();

        w.WriteStartElement("wp", "cNvGraphicFramePr", WpNs);
        w.WriteStartElement("a", "graphicFrameLocks", ANs);
        w.WriteAttributeString("noChangeAspect", "1");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("a", "graphic", ANs);
        w.WriteStartElement("a", "graphicData", ANs);
        w.WriteAttributeString("uri", PicNs);
        w.WriteStartElement("pic", "pic", PicNs);

        w.WriteStartElement("pic", "nvPicPr", PicNs);
        w.WriteStartElement("pic", "cNvPr", PicNs);
        w.WriteAttributeString("id", id.ToString());
        w.WriteAttributeString("name", name);
        w.WriteEndElement();
        w.WriteElementString("pic", "cNvPicPr", PicNs, null);
        w.WriteEndElement();

        w.WriteStartElement("pic", "blipFill", PicNs);
        w.WriteStartElement("a", "blip", ANs);
        w.WriteAttributeString("r", "embed", RNs, relationshipId);
        w.WriteEndElement();
        w.WriteStartElement("a", "stretch", ANs);
        w.WriteElementString("a", "fillRect", ANs, null);
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("pic", "spPr", PicNs);
        w.WriteStartElement("a", "xfrm", ANs);
        w.WriteStartElement("a", "off", ANs);
        w.WriteAttributeString("x", "0");
        w.WriteAttributeString("y", "0");
        w.WriteEndElement();
        w.WriteStartElement("a", "ext", ANs);
        w.WriteAttributeString("cx", cx.ToString());
        w.WriteAttributeString("cy", cy.ToString());
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("a", "prstGeom", ANs);
        w.WriteAttributeString("prst", "rect");
        w.WriteElementString("a", "avLst", ANs, null);
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteEndElement(); // pic:pic
        w.WriteEndElement(); // a:graphicData
        w.WriteEndElement(); // a:graphic
        w.WriteEndElement(); // wp:inline or wp:anchor
        w.WriteEndElement(); // w:drawing
    }

    private static void WriteTable(XmlWriter w, DocxTable table, PackageParts parts)
    {
        w.WriteStartElement("w", "tbl", WNs);

        w.WriteStartElement("w", "tblPr", WNs);
        w.WriteStartElement("w", "tblStyle", WNs);
        w.WriteAttributeString("w", "val", WNs, "TableGrid");
        w.WriteEndElement();
        w.WriteStartElement("w", "tblW", WNs);
        w.WriteAttributeString("w", "w", WNs, table.ColumnWidthsTwips.Sum().ToString());
        w.WriteAttributeString("w", "type", WNs, "dxa");
        w.WriteEndElement();
        if (table.IndentTwips != 0)
        {
            w.WriteStartElement("w", "tblInd", WNs);
            w.WriteAttributeString("w", "w", WNs, table.IndentTwips.ToString());
            w.WriteAttributeString("w", "type", WNs, "dxa");
            w.WriteEndElement();
        }
        if (table.HasBorders) WriteTableBorders(w, table.BorderEighths, table.BorderColor);
        else WriteNoTableBorders(w);
        w.WriteStartElement("w", "tblLayout", WNs);
        w.WriteAttributeString("w", "type", WNs, "fixed");
        w.WriteEndElement();

        // Without a cell margin the text touches the rules; with Word's default it sits where Word puts it
        // rather than where the PDF did. The padding is measured from the page.
        w.WriteStartElement("w", "tblCellMar", WNs);
        foreach (var (edge, value) in (ReadOnlySpan<(string, int)>)[("top", table.CellTopPaddingTwips), ("left", table.CellPaddingTwips), ("bottom", 0), ("right", table.CellPaddingTwips)])
        {
            w.WriteStartElement("w", edge, WNs);
            w.WriteAttributeString("w", "w", WNs, Math.Max(0, value).ToString());
            w.WriteAttributeString("w", "type", WNs, "dxa");
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("w", "tblGrid", WNs);
        foreach (int width in table.ColumnWidthsTwips)
        {
            w.WriteStartElement("w", "gridCol", WNs);
            w.WriteAttributeString("w", "w", WNs, Math.Max(1, width).ToString());
            w.WriteEndElement();
        }
        w.WriteEndElement();

        foreach (var row in table.Rows)
        {
            w.WriteStartElement("w", "tr", WNs);
            if (row.IsHeader || row.MinHeightTwips > 0)
            {
                w.WriteStartElement("w", "trPr", WNs);
                if (row.MinHeightTwips > 0)
                {
                    w.WriteStartElement("w", "trHeight", WNs);
                    w.WriteAttributeString("w", "val", WNs, row.MinHeightTwips.ToString());
                    w.WriteAttributeString("w", "hRule", WNs, "atLeast");
                    w.WriteEndElement();
                }
                if (row.IsHeader) w.WriteElementString("w", "tblHeader", WNs, null);
                w.WriteEndElement();
            }

            int column = 0;
            foreach (var cell in row.Cells)
            {
                int span = Math.Max(1, cell.ColumnSpan);
                int width = 0;
                for (int i = 0; i < span && column + i < table.ColumnWidthsTwips.Count; i++)
                    width += table.ColumnWidthsTwips[column + i];
                column += span;

                w.WriteStartElement("w", "tc", WNs);
                w.WriteStartElement("w", "tcPr", WNs);
                w.WriteStartElement("w", "tcW", WNs);
                w.WriteAttributeString("w", "w", WNs, Math.Max(1, width).ToString());
                w.WriteAttributeString("w", "type", WNs, "dxa");
                w.WriteEndElement();
                if (span > 1)
                {
                    w.WriteStartElement("w", "gridSpan", WNs);
                    w.WriteAttributeString("w", "val", WNs, span.ToString());
                    w.WriteEndElement();
                }
                if (cell.VerticalMerge != 0)
                {
                    w.WriteStartElement("w", "vMerge", WNs);
                    if (cell.VerticalMerge == 1) w.WriteAttributeString("w", "val", WNs, "restart");
                    w.WriteEndElement();
                }
                if (cell.Shading is { } shading)
                {
                    w.WriteStartElement("w", "shd", WNs);
                    w.WriteAttributeString("w", "val", WNs, "clear");
                    w.WriteAttributeString("w", "color", WNs, "auto");
                    w.WriteAttributeString("w", "fill", WNs, Hex(shading));
                    w.WriteEndElement();
                }
                if (cell.VerticalAlignment != DocxVerticalAlignment.Top)
                {
                    w.WriteStartElement("w", "vAlign", WNs);
                    w.WriteAttributeString("w", "val", WNs, cell.VerticalAlignment == DocxVerticalAlignment.Center ? "center" : "bottom");
                    w.WriteEndElement();
                }
                w.WriteEndElement();

                // A cell must hold at least one block-level element, and it must end with a paragraph.
                WriteBlocks(w, cell.Blocks, parts);
                if (cell.Blocks.Count == 0 || cell.Blocks[^1] is DocxTable) WriteSpacerParagraph(w);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    /// <summary>
    /// Writes a run of blocks. Two tables in a row need a paragraph between them, or Word merges them into
    /// one; that paragraph is made as small as Word allows so it does not open a gap the PDF did not have.
    /// </summary>
    private static void WriteBlocks(XmlWriter w, IReadOnlyList<DocxBlock> blocks, PackageParts parts)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            WriteBlock(w, blocks[i], parts);
            if (blocks[i] is DocxTable && i + 1 < blocks.Count && blocks[i + 1] is DocxTable) WriteSpacerParagraph(w);
        }
    }

    /// <summary>A one-point empty paragraph: where Word demands a paragraph the layout has no room for.</summary>
    private static void WriteSpacerParagraph(XmlWriter w, DocxSection? section = null, PackageParts? parts = null)
    {
        w.WriteStartElement("w", "p", WNs);
        w.WriteStartElement("w", "pPr", WNs);
        w.WriteStartElement("w", "spacing", WNs);
        w.WriteAttributeString("w", "before", WNs, "0");
        w.WriteAttributeString("w", "after", WNs, "0");
        w.WriteAttributeString("w", "line", WNs, "20");
        w.WriteAttributeString("w", "lineRule", WNs, "exact");
        w.WriteEndElement();
        w.WriteStartElement("w", "rPr", WNs);
        w.WriteStartElement("w", "sz", WNs);
        w.WriteAttributeString("w", "val", WNs, "2");
        w.WriteEndElement();
        w.WriteEndElement();
        if (section is not null && parts is not null) WriteSectionProperties(w, section, parts);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>Rules over and under a paragraph. Schema order puts <c>w:pBdr</c> before tabs and spacing.</summary>
    private static void WriteParagraphBorders(XmlWriter w, DocxBorder? above, DocxBorder? below)
    {
        if (above is null && below is null) return;
        w.WriteStartElement("w", "pBdr", WNs);
        Edge("top", above);
        Edge("bottom", below);
        // Word draws one border round a run of paragraphs that share it, and the bottom rule under the last
        // one only. A rule under every entry of a list is the border between them — and Word sets its space
        // both above and below the line, measured, so it gets half: the rule takes the same room either way.
        Edge("between", below is null ? null : below with { SpacePoints = Math.Round(below.SpacePoints) / 2 });
        w.WriteEndElement();

        void Edge(string name, DocxBorder? border)
        {
            if (border is null) return;
            w.WriteStartElement("w", name, WNs);
            w.WriteAttributeString("w", "val", WNs, "single");
            w.WriteAttributeString("w", "sz", WNs, BorderSize(border.Eighths).ToString());
            // Word takes the distance from the text in whole points, and no more than 31 of them.
            w.WriteAttributeString("w", "space", WNs, ((int)Math.Floor(Math.Clamp(border.SpacePoints, 0, 31))).ToString());
            w.WriteAttributeString("w", "color", WNs, Hex(border.Color));
            w.WriteEndElement();
        }
    }

    /// <summary>
    /// A panel: a border on all four sides and the fill behind them, with no border between the paragraphs,
    /// which is what makes Word draw a run of them as one panel.
    /// </summary>
    private static void WriteBox(XmlWriter w, DocxBox box)
    {
        w.WriteStartElement("w", "pBdr", WNs);
        Edge("top", box.Top);
        Edge("left", box.Left);
        Edge("bottom", box.Bottom);
        Edge("right", box.Right);
        w.WriteEndElement();

        w.WriteStartElement("w", "shd", WNs);
        w.WriteAttributeString("w", "val", WNs, "clear");
        w.WriteAttributeString("w", "color", WNs, "auto");
        w.WriteAttributeString("w", "fill", WNs, Hex(box.Fill));
        w.WriteEndElement();

        void Edge(string name, DocxBorder border)
        {
            w.WriteStartElement("w", name, WNs);
            w.WriteAttributeString("w", "val", WNs, "single");
            w.WriteAttributeString("w", "sz", WNs, BorderSize(border.Eighths).ToString());
            w.WriteAttributeString("w", "space", WNs, ((int)Math.Round(Math.Clamp(border.SpacePoints, 0, 31))).ToString());
            w.WriteAttributeString("w", "color", WNs, Hex(border.Color));
            w.WriteEndElement();
        }
    }

    /// <summary>
    /// The nearest of the line weights Word offers, in eighths of a point. Word draws any weight, but its
    /// borders dialog shows only these, and a border of another weight comes up there as no weight at all.
    /// </summary>
    private static int BorderSize(int eighths)
    {
        int[] sizes = [2, 4, 6, 8, 12, 18, 24, 36, 48];
        return sizes.MinBy(s => Math.Abs(s - eighths));
    }

    private static void WriteTabs(XmlWriter w, IReadOnlyList<DocxTabStop> stops)
    {
        if (stops.Count == 0) return;
        w.WriteStartElement("w", "tabs", WNs);
        foreach (var stop in stops)
        {
            w.WriteStartElement("w", "tab", WNs);
            w.WriteAttributeString("w", "val", WNs, stop.Alignment switch
            {
                DocxAlignment.Center => "center",
                DocxAlignment.Right => "right",
                _ => "left",
            });
            if (stop.Leader != DocxTabLeader.None)
                w.WriteAttributeString("w", "leader", WNs, stop.Leader switch
                {
                    DocxTabLeader.Hyphen => "hyphen",
                    DocxTabLeader.Underscore => "underscore",
                    DocxTabLeader.MiddleDot => "middleDot",
                    _ => "dot",
                });
            w.WriteAttributeString("w", "pos", WNs, Math.Max(0, stop.PositionTwips).ToString());
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteNoTableBorders(XmlWriter w)
    {
        w.WriteStartElement("w", "tblBorders", WNs);
        foreach (string edge in (string[])["top", "left", "bottom", "right", "insideH", "insideV"])
        {
            w.WriteStartElement("w", edge, WNs);
            w.WriteAttributeString("w", "val", WNs, "nil");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteHeaderFooter(XmlWriter w, DocxHeaderFooter content, bool isHeader)
    {
        w.WriteStartElement("w", isHeader ? "hdr" : "ftr", WNs);
        w.WriteAttributeString("xmlns", "r", null, RNs);
        w.WriteStartElement("w", "p", WNs);
        w.WriteStartElement("w", "pPr", WNs);
        WriteParagraphBorders(w, content.BorderAbove, content.BorderBelow);
        WriteTabs(w, content.TabStops);
        // The document's default gap after a paragraph would sit under the running head and push the body.
        w.WriteStartElement("w", "spacing", WNs);
        w.WriteAttributeString("w", "before", WNs, "0");
        w.WriteAttributeString("w", "after", WNs, "0");
        w.WriteAttributeString("w", "line", WNs, "240");
        w.WriteAttributeString("w", "lineRule", WNs, "auto");
        w.WriteEndElement();
        if (content.Alignment != DocxAlignment.Left)
        {
            w.WriteStartElement("w", "jc", WNs);
            w.WriteAttributeString("w", "val", WNs, content.Alignment switch
            {
                DocxAlignment.Center => "center",
                DocxAlignment.Right => "right",
                _ => "left",
            });
            w.WriteEndElement();
        }
        w.WriteEndElement();

        for (int i = 0; i < content.Runs.Count; i++)
        {
            if (i == content.PageNumberRun) WritePageField(w, content.Runs[i]);
            else WriteRun(w, content.Runs[i], hyperlinkStyle: false);
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>
    /// A PAGE field, so the number follows Word's pagination instead of being frozen text. Every run of it
    /// carries the number's look: Word formats the result it computes like the field's first run, and a
    /// bare one would print the number at the body text's size in the middle of a small footer.
    /// </summary>
    private static void WritePageField(XmlWriter w, DocxRun run)
    {
        Fld("begin");
        w.WriteStartElement("w", "r", WNs);
        WriteRunProperties(w, run, hyperlinkStyle: false);
        w.WriteStartElement("w", "instrText", WNs);
        w.WriteAttributeString("xml", "space", null, "preserve");
        w.WriteString(" PAGE ");
        w.WriteEndElement();
        w.WriteEndElement();
        Fld("separate");
        WriteRun(w, run, hyperlinkStyle: false);
        Fld("end");

        void Fld(string type)
        {
            w.WriteStartElement("w", "r", WNs);
            WriteRunProperties(w, run, hyperlinkStyle: false);
            w.WriteStartElement("w", "fldChar", WNs);
            w.WriteAttributeString("w", "fldCharType", WNs, type);
            w.WriteEndElement();
            w.WriteEndElement();
        }
    }

    private static string Hex(uint rgb) => (rgb & 0xFFFFFF).ToString("X6");

    /// <summary>Word's highlight takes one of sixteen names, so an arbitrary colour snaps to the nearest.</summary>
    private static string NearestHighlight(uint rgb)
    {
        (string Name, uint Value)[] palette =
        [
            ("yellow", 0xFFFF00), ("green", 0x00FF00), ("cyan", 0x00FFFF), ("magenta", 0xFF00FF),
            ("blue", 0x0000FF), ("red", 0xFF0000), ("darkBlue", 0x000080), ("darkCyan", 0x008080),
            ("darkGreen", 0x008000), ("darkMagenta", 0x800080), ("darkRed", 0x800000), ("darkYellow", 0x808000),
            ("darkGray", 0x808080), ("lightGray", 0xC0C0C0), ("black", 0x000000), ("white", 0xFFFFFF),
        ];

        int r = (int)((rgb >> 16) & 0xFF), g = (int)((rgb >> 8) & 0xFF), b = (int)(rgb & 0xFF);
        string best = "yellow";
        long bestDistance = long.MaxValue;
        foreach (var (name, value) in palette)
        {
            long dr = r - (int)((value >> 16) & 0xFF), dg = g - (int)((value >> 8) & 0xFF), db = b - (int)(value & 0xFF);
            long distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = name;
            }
        }
        return best;
    }
}
