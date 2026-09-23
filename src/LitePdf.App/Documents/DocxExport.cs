using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitePdf.Core;
using LitePdf.Core.Export;

namespace LitePdf.App.Documents;

/// <summary>Encodes pixels with WPF's PNG encoder; Core deliberately has none of its own.</summary>
public sealed class WpfImageEncoder : IImageEncoder
{
    public byte[] EncodePng(ImageBits bits)
    {
        var source = BitmapSource.Create(bits.Width, bits.Height, 96, 96, PixelFormats.Bgra32, null,
            bits.Bgra, bits.Width * 4);
        source.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

/// <summary>
/// Word's single line height for each installed font, which WPF reads from the same font tables Word does.
/// Measured against Word itself for fifteen common faces, the two agree to the third decimal.
/// </summary>
public sealed class WpfFontMetrics : IFontMetrics
{
    private readonly HashSet<string> _installed;
    private readonly Dictionary<string, double?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double?> _ascents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public WpfFontMetrics()
    {
        _installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            _installed.Add(family.Source);
            foreach (string name in family.FamilyNames.Values) _installed.Add(name);
        }
    }

    public double? LineHeight(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;
        lock (_gate)
        {
            if (_cache.TryGetValue(family, out double? known)) return known;

            // A font that is not installed would be measured as WPF's fallback, which is not what Word will
            // substitute; saying nothing lets the export write the pitch exactly instead.
            double? height = null;
            if (_installed.Contains(family))
            {
                double spacing = new FontFamily(family).LineSpacing;
                if (spacing is > 0.5 and < 3) height = spacing;
            }
            _cache[family] = height;
            return height;
        }
    }

    public double? Ascent(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;
        lock (_gate)
        {
            if (_ascents.TryGetValue(family, out double? known)) return known;
            double? ascent = null;
            if (_installed.Contains(family))
            {
                double baseline = new FontFamily(family).Baseline;
                if (baseline is > 0.3 and < 2) ascent = baseline;
            }
            _ascents[family] = ascent;
            return ascent;
        }
    }
}

/// <summary>What the export dialog collected.</summary>
public sealed record DocxExportSettings
{
    public IReadOnlyList<int> Pages { get; init; } = [];

    /// <summary>
    /// Paragraphs reflow, and a page break is kept only where the PDF's author started a new page. Word
    /// never sets a line exactly as wide as the PDF did, so keeping every line break leaves one-word lines
    /// behind and keeping every page break turns each slightly-overfull page into two.
    /// </summary>
    public ExportOptions Options { get; init; } = ExportOptions.Default with
    {
        PageBreaks = PageBreakMode.Deliberate,
        FontMetrics = new WpfFontMetrics(),
    };

    /// <summary>
    /// Leave out the selected pages that have no PDF text — a scanned page, a picture cover — and convert the
    /// rest. Off until the reader has been told which pages those are and agreed.
    /// </summary>
    public bool SkipUnsupportedPages { get; init; }
}

/// <summary>
/// Drives a conversion: reads every page, composes the document, and writes the package.
///
/// Reading is the slow half and happens one page at a time on the PDFium worker at background priority, so
/// scrolling stays responsive while a long document converts.
/// </summary>
public static class DocxExport
{
    /// <returns>The number of pages converted.</returns>
    public static async Task<int> ExportAsync(
        DocumentSession session, string targetPath, DocxExportSettings settings,
        IProgress<(double Fraction, string Text)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!session.IsPdf) throw new NotSupportedException("Word conversion supports text PDFs only.");
        if (settings.Pages.Count == 0) throw new ArgumentException("Select at least one page.", nameof(settings));

        var pages = new List<PageContent>(settings.Pages.Count);
        var extras = new List<PageExtras>(settings.Pages.Count);
        var pool = new ImagePool();

        for (int i = 0; i < settings.Pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int index = settings.Pages[i];
            progress?.Report(((double)i / settings.Pages.Count,
                settings.Pages.Count == 1 ? $"Page {index + 1}" : $"Page {index + 1} · {i + 1} of {settings.Pages.Count}"));

            var content = pool.Share(await ReadPageAsync(session, index, settings, ct));
            pages.Add(content);
            extras.Add(await ReadExtrasAsync(session, index, settings.Options, ct));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report((0.92, "Building the document"));

        if (settings.SkipUnsupportedPages)
        {
            for (int i = pages.Count - 1; i >= 0; i--)
            {
                if (!TextPdfExport.IsUnsupported(pages[i])) continue;
                pages.RemoveAt(i);
                extras.RemoveAt(i);
            }
        }
        TextPdfExport.Validate(pages);

        var outline = settings.Options.Headings
            ? await session.Document.GetOutlineAsync(ct)
            : [];
        DocumentInfo? info = null;
        try
        {
            info = await session.Document.GetInfoAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Infrastructure.Log.Error(ex, "Reading document properties for the Word export");
        }

        var encoder = new WpfImageEncoder();
        await Task.Run(() =>
        {
            var document = ContentComposer.Compose(pages, extras, outline, settings.Options, info);
            ct.ThrowIfCancellationRequested();
            WritePackage(targetPath, document, encoder, ct);
        }, ct);

        progress?.Report((1, "Done"));
        return pages.Count;
    }

    /// <summary>
    /// Makes every page that draws the same picture share one copy of it.
    ///
    /// The whole document is held in memory while it converts, and a logo — rasterized from vector art or
    /// stored as an image — is drawn on every page of a report. Four hundred pages of a quarter-megabyte
    /// logo is a hundred megabytes of the same bytes; the writer would deduplicate them, but only after
    /// they had all been read. Hashing each picture as it arrives costs a millisecond and caps the rest.
    /// </summary>
    private sealed class ImagePool
    {
        private readonly Dictionary<string, PlacedImage> _byContent = new(StringComparer.Ordinal);

        public PageContent Share(PageContent page)
        {
            if (page.Images.Count == 0) return page;

            var shared = new List<PlacedImage>(page.Images.Count);
            foreach (var image in page.Images)
            {
                byte[] bytes = image.Encoded ?? image.Bits?.Bgra ?? [];
                if (bytes.Length == 0)
                {
                    shared.Add(image);
                    continue;
                }

                string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                if (_byContent.TryGetValue(key, out var first))
                {
                    // Same pixels, a different place on a different page: keep this one's own bounds.
                    shared.Add(first with
                    {
                        Bounds = image.Bounds,
                        IsDrawing = image.IsDrawing,
                        MarkedContentId = image.MarkedContentId,
                    });
                    continue;
                }

                _byContent[key] = image;
                shared.Add(image);
            }

            return page with { Images = shared };
        }
    }

    private static async Task<PageContent> ReadPageAsync(
        DocumentSession session, int index, DocxExportSettings settings, CancellationToken ct)
    {
        var size = session.PageSizes[index];
        var content = session.Document is IPageContentSource source
            ? await source.GetPageContentAsync(index, RenderPriority.Background, settings.Options.ToPageRequest(), ct)
            : PageContent.Empty(index, size);

        return content;
    }

    private static async Task<PageExtras> ReadExtrasAsync(
        DocumentSession session, int index, ExportOptions options, CancellationToken ct)
    {
        IReadOnlyList<PdfLink> links = [];
        IReadOnlyList<PdfAnnotation> annotations = [];

        if (options.Hyperlinks)
        {
            try
            {
                links = await session.GetLinksAsync(index, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Infrastructure.Log.Error(ex, $"Reading links on page {index + 1}");
            }
        }

        if (options.Annotations && session.CanAnnotate)
        {
            try
            {
                annotations = await session.GetAnnotationsAsync(index, RenderPriority.Background, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Infrastructure.Log.Error(ex, $"Reading annotations on page {index + 1}");
            }
        }

        return links.Count == 0 && annotations.Count == 0 ? PageExtras.None : new PageExtras(links, annotations);
    }

    /// <summary>Writes through a temp file in the target folder, so a failure never truncates an existing one.</summary>
    private static void WritePackage(string targetPath, DocxDocument document, IImageEncoder encoder, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? ".";
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Path.GetRandomFileName() + ".docx.tmp");

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                DocxWriter.Write(stream, document, encoder);

            ct.ThrowIfCancellationRequested();
            if (File.Exists(targetPath)) File.Replace(temp, targetPath, null);
            else File.Move(temp, targetPath);
        }
        catch
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // The original is intact either way; a stray temp file is not worth masking the real error.
            }
            throw;
        }
    }
}
