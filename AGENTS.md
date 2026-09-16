# AGENTS.md — start here (humans and AI assistants)

LitePDF is a lightweight, private Windows reader for PDFs and scanned images: continuous reading, thumbnails,
chapters, text selection/copy, search, on-device OCR (pages, areas, images), highlights/notes saved into the PDF.

## Read in this order
1. `AGENTS.md` (this file): commands, rules, verification
2. `docs/BLUEPRINT.md`: architecture and the reasons behind it (source of truth)
3. `TASKS.md`: what is done and verified, known gaps, next work

## Environment
- Windows 10 19041+ / Windows 11, x64. .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).
- No Visual Studio required.

## Commands (repo root)
```
dotnet build LitePDF.slnx
dotnet test tests/LitePdf.Core.Tests
dotnet run --project src/LitePdf.App -- "C:\path\file.pdf"
src\LitePdf.App\bin\Debug\net10.0-windows10.0.19041.0\LitePDF.exe --self-test   # exit code 0 = pass
dotnet run --project tools/LitePdf.SampleGen          # writes samples/generated/*.pdf (text, scanned, 1000 pages)
powershell -File publish.ps1                           # self-contained build -> dist/LitePDF-<ver>-setup.exe + portable zip
powershell -File publish.ps1 -FrameworkDependent       # ~33 MB instead of 194 MB, but needs .NET 10 on the target PC
powershell -File publish.ps1 -NoInstaller              # portable zip only (no Inno Setup needed)
```

## Definition of done for any change
1. `dotnet build LitePDF.slnx` → 0 errors, 0 warnings.
2. `dotnet test tests/LitePdf.Core.Tests` → all pass. Add tests for Core/Pdfium logic you touch.
3. `LitePDF.exe --self-test` → `SELF-TEST PASSED` (instantiates every style/resource in both themes, the main window and
   every dialog; WPF otherwise only reports XAML errors when a style is first used).
4. UI changes: run the app against `samples/generated/*` and exercise the feature (mouse, keyboard, both themes).
5. Update `TASKS.md` (and `docs/BLUEPRINT.md` if the design changed). Commit with a descriptive message.

## Rules
1. **PDFium is single-threaded.** All native calls go through `PdfiumWorker`. Never touch `Interop` from elsewhere.
2. **Geometry is normalized page coordinates** (0..1, top-left, as displayed with the page's /Rotate and crop box).
   Only `PdfiumDocument.PageFrame` converts to PDF user space; only the viewer converts to DIPs.
3. **Layering:** `Core` has no dependencies and no UI types. `Pdfium` and `Ocr` depend only on `Core`. `App` is WPF.
4. **No network access, no telemetry.** Data lives in `%LocalAppData%\LitePDF\` (settings, recent files, OCR cache, logs).
5. **Never swallow exceptions silently.** Expected failures (cancellation, a document closed mid-render) are caught
   explicitly; everything else goes to `Log` and `ErrorReporter`.
6. **UI thread:** async/await only. No `Task.Run` + `Dispatcher.Invoke` round trips; renders return frozen bitmaps.
7. **Keep it lite:** no new NuGet packages without a strong reason. Theme colors only via `DynamicResource Brush.*`.
8. **Saving never overwrites in place:** write to a temp file in the target folder, then `File.Replace`.

## Decisions
| Topic | Decision |
|---|---|
| UI | WPF (.NET 10) with a custom Fluent-style theme (light/dark, follows Windows) |
| PDF engine | PDFium (`bblanchon.PDFium.Win32`) via `LibraryImport` |
| OCR | Windows.Media.Ocr (on-device); per-page JSON cache keyed by document ID |
| Annotations | Standard PDF Highlight/Underline/StrikeOut/Text annotations, incremental save |
| Distribution | Inno Setup installer around a self-contained ReadyToRun build, plus a portable zip (`publish.ps1`). Self-contained so a target PC needs nothing installed: .NET 10 is new enough that few have the runtime |
