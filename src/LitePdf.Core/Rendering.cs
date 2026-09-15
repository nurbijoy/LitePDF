namespace LitePdf.Core;

/// <summary>BGRA32 pixels, top-left origin. The alpha byte is undefined; treat the bitmap as opaque (Bgr32).</summary>
public sealed record RenderedBitmap(int Width, int Height, int Stride, byte[] Pixels);

/// <summary>Values match PDFium's FPDF_RENDER flags so they can be passed straight through.</summary>
[Flags]
public enum RenderFlags
{
    None = 0,
    Annotations = 0x01,
    Grayscale = 0x08,
    Printing = 0x800,
}

/// <summary>Work queue priorities; lower runs first. See BLUEPRINT §4.</summary>
public static class RenderPriority
{
    public const int Visible = 0;
    public const int Interactive = 10;
    public const int Nearby = 20;
    public const int Thumbnail = 30;
    public const int Background = 40;
}
