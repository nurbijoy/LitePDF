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

Console.WriteLine($"Samples written to {output}");
