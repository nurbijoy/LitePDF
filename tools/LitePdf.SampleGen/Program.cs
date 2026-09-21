using LitePdf.Core;
using LitePdf.Pdfium;
using LitePdf.Samples;

// Writes sample documents for manual testing: dotnet run --project tools/LitePdf.SampleGen [outputDir]
string output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("samples", "generated"));
Directory.CreateDirectory(output);

string textPath = Path.Combine(output, "sample-text.pdf");
File.WriteAllBytes(textPath, SampleDocuments.CreateTextDocument());

await using (var doc = await PdfiumDocument.OpenAsync(textPath))
{
    // Render page 2 at 150 DPI and embed it as an image to simulate a scanned page.
    int w = (int)(612 * 150 / 72.0), h = (int)(792 * 150 / 72.0);
    var bitmap = await doc.RenderAsync(1, w, h, 0, null, RenderFlags.None, RenderPriority.Interactive);
    File.WriteAllBytes(Path.Combine(output, "sample-scanned.pdf"), SampleDocuments.CreateImageDocument(SampleDocuments.BgraToRgb(bitmap.Pixels), w, h));
}

File.WriteAllBytes(Path.Combine(output, "sample-1000-pages.pdf"), SampleDocuments.CreateLargeDocument(1000));

// A document with everything the Word export has to rebuild, and a plate at 200 DPI so the picture is
// stored at a higher resolution than the size it is placed at.
int plateWidth = 800, plateHeight = 600;
var plate = new byte[plateWidth * plateHeight * 3];
for (int y = 0; y < plateHeight; y++)
{
    for (int x = 0; x < plateWidth; x++)
    {
        int i = (y * plateWidth + x) * 3;
        plate[i] = (byte)(x * 255 / plateWidth);
        plate[i + 1] = (byte)(y * 255 / plateHeight);
        plate[i + 2] = (byte)((x ^ y) & 0xFF);
    }
}
File.WriteAllBytes(Path.Combine(output, "sample-formatted.pdf"),
    SampleDocuments.CreateFormattedDocument(plate, plateWidth, plateHeight));

Console.WriteLine($"Samples written to {output}");
