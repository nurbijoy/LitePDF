using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitePdf.Core;
using LitePdf.Core.Export;
using LitePdf.Core.Text;

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

/// <summary>What the export dialog collected.</summary>
public sealed record DocxExportSettings
{
    public IReadOnlyList<int> Pages { get; init; } = [];
    public ExportOptions Options { get; init; } = ExportOptions.Default;

    /// <summary>Recognize pages that have no text layer, so a scan converts to text rather than a picture.</summary>
    public bool RecognizeScans { get; init; }
}

/// <summary>
/// Drives a conversion: reads every page, composes the document, and writes the package.
///
/// Reading is the slow half and happens one page at a time on the PDFium worker at background priority, so
/// scrolling stays responsive while a long document converts.
/// </summary>
public static class DocxExport
{
    /// <summary>A page with neither text nor images is rendered at this resolution so nothing is lost.</summary>
    private const double FallbackPageDpi = 150;

    public static async Task<int> ExportAsync(
        DocumentSession session, string targetPath, DocxExportSettings settings,
        IProgress<(double Fraction, string Text)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var pages = new List<PageContent>(settings.Pages.Count);
        var extras = new List<PageExtras>(settings.Pages.Count);

        for (int i = 0; i < settings.Pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int index = settings.Pages[i];
            progress?.Report(((double)i / settings.Pages.Count,
                settings.Pages.Count == 1 ? $"Page {index + 1}" : $"Page {index + 1} · {i + 1} of {settings.Pages.Count}"));

            var content = await ReadPageAsync(session, index, settings, ct);
            pages.Add(content);
            extras.Add(await ReadExtrasAsync(session, index, settings.Options, ct));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report((0.92, "Building the document"));

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
        int blocks = await Task.Run(() =>
        {
            var document = ContentComposer.Compose(pages, extras, outline, settings.Options, info);
            WritePackage(targetPath, document, encoder);
            return document.Sections.Sum(s => s.Blocks.Count);
        }, ct);

        progress?.Report((1, "Done"));
        return blocks;
    }

    private static async Task<PageContent> ReadPageAsync(
        DocumentSession session, int index, DocxExportSettings settings, CancellationToken ct)
    {
        var size = session.PageSizes[index];
        var content = session.Document is IPageContentSource source
            ? await source.GetPageContentAsync(index, RenderPriority.Background, ct)
            : PageContent.Empty(index, size);

        if (content.Text.VisibleCharCount < DocumentSession.MinTextChars && settings.RecognizeScans)
        {
            var recognized = await RecognizeAsync(session, index, ct);
            if (recognized is { VisibleCharCount: > 0 })
                content = content with { Text = recognized, Spans = SpansForRecognizedText(recognized, size) };
        }

        // Nothing readable and nothing drawn that we could lift out: keep the page as a picture rather than
        // writing an empty page. Losing a page is the one outcome the export must never produce.
        if (content.Text.VisibleCharCount == 0 && content.Images.Count == 0)
        {
            var image = await RenderWholePageAsync(session, index, ct);
            if (image is not null) content = content with { Images = [image] };
        }

        return content;
    }

    private static async Task<PageText?> RecognizeAsync(DocumentSession session, int index, CancellationToken ct)
    {
        try
        {
            return await session.RecognizePageAsync(index, RenderPriority.Background, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error(ex, $"Recognizing page {index + 1} for the Word export");
            return null;
        }
    }

    /// <summary>
    /// Recognized text carries no font, so a size is estimated from how tall the lines are. Without this
    /// every scanned page would come out at the default size whatever the original was set in.
    /// </summary>
    private static IReadOnlyList<StyledSpan> SpansForRecognizedText(PageText text, PageSize size)
    {
        if (text.Length == 0) return [];

        var heights = new List<double>();
        foreach (var line in text.Lines)
            if (!line.Bounds.IsEmpty) heights.Add(line.Bounds.Height * size.Height);

        double points = 11;
        if (heights.Count > 0)
        {
            heights.Sort();
            points = Math.Clamp(heights[heights.Count / 2] * 0.78, 6, 48);
        }

        var style = new TextStyle("Calibri", Math.Round(points, 1), false, false, 0x000000);
        return [new StyledSpan(0, text.Length, style)];
    }

    private static async Task<PlacedImage?> RenderWholePageAsync(DocumentSession session, int index, CancellationToken ct)
    {
        try
        {
            var full = new RectD(0, 0, 1, 1);
            var bitmap = await session.RenderRegionAsync(index, full, FallbackPageDpi, 0, ct);
            var bits = new ImageBits(bitmap.Width, bitmap.Height, bitmap.Pixels);
            return PlacedImage.FromBits(full, bits);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error(ex, $"Rendering page {index + 1} for the Word export");
            return null;
        }
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
    private static void WritePackage(string targetPath, DocxDocument document, IImageEncoder encoder)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? ".";
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Path.GetRandomFileName() + ".docx.tmp");

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                DocxWriter.Write(stream, document, encoder);

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
