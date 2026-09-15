namespace LitePdf.Core;

/// <summary>Tightly packed BGRA32 pixels (Stride == Width * 4), top-left origin, opaque.</summary>
public sealed record RenderedBitmap(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>Values match PDFium's FPDF_RENDER flags so they pass straight through.</summary>
[Flags]
public enum RenderFlags
{
    None = 0,
    Annotations = 0x01,
    Grayscale = 0x08,
    Printing = 0x800,
}

/// <summary>Work queue priorities; lower runs first.</summary>
public static class RenderPriority
{
    public const int Visible = 0;
    public const int Interactive = 10;
    public const int Nearby = 20;
    public const int Thumbnail = 30;
    public const int Background = 40;
}

public enum PageColorMode
{
    Normal,
    Dark,
    Sepia,
}
