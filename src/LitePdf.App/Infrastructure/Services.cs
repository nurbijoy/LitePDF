using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitePdf.App.Documents;
using LitePdf.Core;
using Windows.Media.Core;
using WinMediaPlayer = Windows.Media.Playback.MediaPlayer;
using Windows.Media.SpeechSynthesis;

using LitePdf.App.Views;

namespace LitePdf.App.Infrastructure;

public static class ClipboardHelper
{
    // The clipboard is a shared resource; another app holding it open makes calls fail transiently.
    public static bool TrySetText(string text) => Retry(() => Clipboard.SetText(text));

    public static bool TrySetImage(BitmapSource image) => Retry(() => Clipboard.SetImage(image));

    private static bool Retry(Action action)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(40);
            }
            catch (COMException ex)
            {
                Log.Error(ex, "Clipboard");
            }
        }
        return false;
    }
}

public static class PrintService
{
    /// <summary>Shows the print dialog, then renders and spools pages.</summary>
    public static async Task<int?> PrintAsync(IPdfDocument document, string jobName, int currentPage = 0, Window? owner = null)
    {
        var request = PrintDialogWindow.Show(owner, document, currentPage, jobName);
        if (request is null) return null;

        var queue = request.Queue;
        var ticket = request.Ticket;
        var pages = request.Pages;
        var orientation = request.Orientation;
        var colorMode = request.ColorMode;

        if (pages.Count == 0) return 0;

        double areaW = 816, areaH = 1056;
        try
        {
            var caps = queue.GetPrintCapabilities(ticket);
            if (caps.OrientedPageMediaWidth is double pw && pw > 0) areaW = pw;
            if (caps.OrientedPageMediaHeight is double ph && ph > 0) areaH = ph;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not query printable area from print capabilities");
        }
        var area = new Size(areaW, areaH);

        await Task.Yield();

        var prevCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(new PdfPaginator(document, pages, area, orientation, colorMode), ticket);
            return pages.Count;
        }
        catch (PrintingCanceledException)
        {
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = prevCursor;
        }
    }

    private sealed class PdfPaginator(
        IPdfDocument document,
        IReadOnlyList<int> pages,
        Size area,
        PrintOrientation orientation,
        PrintColorMode colorMode) : DocumentPaginator
    {
        public override bool IsPageCountValid => true;
        public override int PageCount => pages.Count;
        public override Size PageSize { get; set; } = area;
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            int pageIndex = pages[pageNumber];
            var size = document.PageSizes[pageIndex];
            double w = size.Width * ViewMath.PointsToDip, h = size.Height * ViewMath.PointsToDip;

            int rotation;
            double scale;
            if (orientation == PrintOrientation.Portrait)
            {
                rotation = 0;
                scale = Math.Min(1, Math.Min(PageSize.Width / w, PageSize.Height / h));
            }
            else if (orientation == PrintOrientation.Landscape)
            {
                rotation = 1;
                scale = Math.Min(1, Math.Min(PageSize.Width / h, PageSize.Height / w));
            }
            else
            {
                // Auto: rotate if landscape orientation yields larger scale
                double scaleUpright = Math.Min(PageSize.Width / w, PageSize.Height / h);
                double scaleRotated = Math.Min(PageSize.Width / h, PageSize.Height / w);
                rotation = scaleRotated > scaleUpright * 1.05 ? 1 : 0;
                scale = Math.Min(1, rotation == 1 ? scaleRotated : scaleUpright);
            }

            double drawW = (rotation == 1 ? h : w) * scale, drawH = (rotation == 1 ? w : h) * scale;

            const double dpi = 300;
            var (pw, ph) = ViewMath.ClampToMax((int)(drawW / 96 * dpi), (int)(drawH / 96 * dpi), 7000);
            pw = Math.Max(1, pw);
            ph = Math.Max(1, ph);
            var rendered = document.RenderAsync(pageIndex, pw, ph, rotation, null, RenderFlags.Annotations | RenderFlags.Printing, RenderPriority.Background)
                .GetAwaiter().GetResult();

            if (colorMode == PrintColorMode.Grayscale)
            {
                var pixels = rendered.Pixels;
                for (int i = 0; i + 3 < pixels.Length; i += 4)
                {
                    byte gray = (byte)((pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114) / 1000);
                    pixels[i] = gray;
                    pixels[i + 1] = gray;
                    pixels[i + 2] = gray;
                }
            }

            var bitmap = PageRenderer.ToBitmapSource(rendered);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawImage(bitmap, new Rect((PageSize.Width - drawW) / 2, (PageSize.Height - drawH) / 2, drawW, drawH));
            return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
        }
    }
}

/// <summary>Reads text aloud with the on-device Windows voices.</summary>
public sealed class SpeechService : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly WinMediaPlayer _player = new();

    public SpeechService()
    {
        _player.MediaEnded += (_, _) => Finished?.Invoke();
        _player.MediaFailed += (_, _) => Finished?.Invoke();
    }

    /// <summary>Raised on a background thread when playback ends.</summary>
    public event Action? Finished;

    public async Task SpeakAsync(string text)
    {
        Stop();
        var stream = await _synthesizer.SynthesizeTextToStreamAsync(text);
        _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player.Play();
    }

    public void Stop()
    {
        _player.Pause();
        _player.Source = null;
    }

    public void Dispose()
    {
        _player.Dispose();
        _synthesizer.Dispose();
    }
}
