using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LitePdf.Core;

namespace LitePdf.App.Services;

public static class PrintService
{
    public static async Task PrintAsync(IPdfDocument document, int pageIndex, string printerName = "")
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        // Render at printer DPI
        double dpiX = 300; // default, could get from dlg
        double dpiY = 300;

        // For simplicity, render current page at 300 DPI
        var pageSize = document.PageSizes[pageIndex];
        int pxW = ViewMath.ToPixels(pageSize.Width, 1.0, dpiX / 96.0);
        int pxH = ViewMath.ToPixels(pageSize.Height, 1.0, dpiY / 96.0);

        var rendered = await document.RenderPageAsync(pageIndex, pxW, pxH, RenderFlags.Printing | RenderFlags.Annotations, RenderPriority.Interactive);

        var wb = new WriteableBitmap(rendered.Width, rendered.Height, dpiX, dpiY, System.Windows.Media.PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, rendered.Width, rendered.Height), rendered.Pixels, rendered.Stride, 0);

        var image = new Image { Source = wb, Stretch = System.Windows.Media.Stretch.Uniform };
        // Measure and arrange for printing
        dlg.PrintVisual(image, $"LitePDF - Page {pageIndex + 1}");
    }
}
