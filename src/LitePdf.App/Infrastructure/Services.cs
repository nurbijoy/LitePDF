using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitePdf.App.Documents;
using LitePdf.Core;
using Windows.Media.Core;
using WinMediaPlayer = Windows.Media.Playback.MediaPlayer;
using Windows.Media.SpeechSynthesis;

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
    /// <summary>Shows the print dialog, then renders and spools pages on a background STA thread.</summary>
    public static Task<int?> PrintAsync(IPdfDocument document, string jobName)
    {
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            MinPage = 1,
            MaxPage = (uint)document.PageCount,
            PageRange = new PageRange(1, document.PageCount),
        };
        if (dialog.ShowDialog() != true) return Task.FromResult<int?>(null);

        var range = dialog.PageRangeSelection == PageRangeSelection.UserPages ? dialog.PageRange : new PageRange(1, document.PageCount);
        var pages = Enumerable.Range(Math.Max(1, range.PageFrom), Math.Max(0, Math.Min(document.PageCount, range.PageTo) - Math.Max(1, range.PageFrom) + 1))
            .Select(p => p - 1).ToList();
        var queue = dialog.PrintQueue;
        var ticket = dialog.PrintTicket;
        var area = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);

        var tcs = new TaskCompletionSource<int?>();
        var thread = new Thread(() =>
        {
            try
            {
                var writer = PrintQueue.CreateXpsDocumentWriter(queue);
                writer.Write(new PdfPaginator(document, pages, area), ticket);
                tcs.SetResult(pages.Count);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Print";
        thread.Start();
        return tcs.Task;
    }

    private sealed class PdfPaginator(IPdfDocument document, IReadOnlyList<int> pages, Size area) : DocumentPaginator
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

            // Rotate landscape pages onto portrait paper (and vice versa) when that makes them larger.
            double scaleUpright = Math.Min(PageSize.Width / w, PageSize.Height / h);
            double scaleRotated = Math.Min(PageSize.Width / h, PageSize.Height / w);
            int rotation = scaleRotated > scaleUpright * 1.05 ? 1 : 0;
            double scale = Math.Min(1, Math.Max(scaleUpright, scaleRotated));
            double drawW = (rotation == 1 ? h : w) * scale, drawH = (rotation == 1 ? w : h) * scale;

            const double dpi = 300;
            var (pw, ph) = ViewMath.ClampToMax((int)(drawW / 96 * dpi), (int)(drawH / 96 * dpi), 7000);
            var rendered = document.RenderAsync(pageIndex, pw, ph, rotation, null, RenderFlags.Annotations | RenderFlags.Printing, RenderPriority.Background)
                .GetAwaiter().GetResult();
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
