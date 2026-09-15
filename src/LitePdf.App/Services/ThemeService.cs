using System.Windows;
using System.Windows.Media;

namespace LitePdf.App.Services;

public enum PageMode
{
    Normal,
    Dark,
    Sepia
}

public static class ThemeService
{
    public static void ApplyPageMode(PageMode mode, System.Windows.Controls.Image image, WriteableBitmapSource? source)
    {
        // Placeholder: actual dark/sepia would be applied via pixel shader or bitmap transform.
        // For now, we adjust via effect or leave normal.
        // Dark mode: invert and reduce brightness
        // Sepia: color matrix
        // Implemented in rendering pipeline: when RenderFlags.Grayscale etc.
        // Here we just set image effect if needed.
    }

    public static RenderFlags GetRenderFlags(PageMode mode) => mode switch
    {
        PageMode.Dark => LitePdf.Core.RenderFlags.Grayscale, // placeholder, could use custom shader
        PageMode.Sepia => LitePdf.Core.RenderFlags.None,
        _ => LitePdf.Core.RenderFlags.Annotations
    };
}

// Dummy type to avoid compile error if needed
public class WriteableBitmapSource { }
