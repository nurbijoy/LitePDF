# AGENTS.md — Start here (humans and AI assistants)

LitePDF is a lightweight Windows PDF reader: thumbnails, chapters (outline), on-device OCR, copy, highlights.

## Read in this order
1. `AGENTS.md` (this file): rules and commands
2. `docs/BLUEPRINT.md`: architecture, contracts, algorithms (the source of truth)
3. `TASKS.md`: ordered work queue with status and handoff notes. **Pick the first unchecked task.**
4. `PLAN.md`: original high-level plan (background only)

## Environment
- Windows 10 19041+ / Windows 11, x64
- .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`)
- No Visual Studio required; VS Code + C# Dev Kit is optional

## Commands (run from the repo root)
```
dotnet build LitePDF.slnx
dotnet run --project src/LitePdf.App -- "C:\path\file.pdf"
dotnet test tests/LitePdf.Core.Tests
```

## Rules
1. **PDFium is not thread-safe.** Every PDFium call goes through `PdfiumWorker` (one thread). Never call `NativeMethods` directly from the UI or thread pool.
2. **Layering:** `Core` references nothing. `Pdfium` and `Ocr` reference only `Core`. `App` references all three. UI types (WPF) exist only in `App`.
3. **No network access, no telemetry.** All data stays in `%LocalAppData%\LitePDF\`.
4. **Keep it lite.** Don't add a NuGet package when ~100 lines of code will do. Ask before adding heavy dependencies.
5. **Units:** PDF points (1/72 in) in Core and Pdfium; DIPs and pixels only in App. See BLUEPRINT §5.
6. **After finishing a task:**
   - Tick it in `TASKS.md` and update the "Handoff notes" section.
   - Commit with the message `T-XX: short description`.
7. Put non-UI logic in `Core` and add xUnit tests for it.
8. Match existing style: file-scoped namespaces, nullable enabled, `sealed` by default, minimal comments that explain *why*.

## Decisions (defaults chosen 2026-09-15; change only with the owner's approval)
| Topic | Decision |
|---|---|
| UI | WPF on .NET 10 |
| PDF engine | PDFium (NuGet `bblanchon.PDFium.Win32`) via `LibraryImport` P/Invoke |
| OCR | Windows.Media.Ocr behind `IOcrEngine`; Tesseract is an optional later addition for unsupported languages |
| Highlights | Standard PDF annotations saved into the file (incremental save via temp file + replace) |
| Distribution | Portable zip first (framework-dependent), installer later |
