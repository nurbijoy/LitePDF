# LitePDF

A small, fast PDF reader for Windows with thumbnails, chapters, on-device OCR, copy and highlights.

## Features

- **Reading**: Continuous vertical scroll, zoom (buttons, Ctrl+wheel, fit width/page), go to page, links, password PDFs
- **Thumbnails**: Virtualized sidebar, click to jump, current page selected
- **Chapters**: Outline tree, click to jump, current chapter follows scroll
- **Text**: Select (drag/double/triple click), Ctrl+C, copy page text, copy region/page as image, Ctrl+F search
- **OCR**: Scanned-page detection, background OCR, region OCR, open/paste images, cached results
- **Annotations**: Highlight/underline/strikeout (5 colors), sticky notes, panel, save, export to Markdown
- **Comfort**: Tabs, recent files + resume, dark/sepia, two-page view, rotate, full screen, bookmarks, read aloud, print

## Architecture

- `LitePdf.Core` – no dependencies, geometry, rendering, outline, OCR contracts, ViewMath, text layer, search, storage
- `LitePdf.Pdfium` – P/Invoke wrapper, single-thread worker with priority queue (Visible 0, Interactive 10, Nearby 20, Thumbnail 30, Background 40)
- `LitePdf.Ocr` – Windows.Media.Ocr behind IOcrEngine, cache JSON keyed by docKey
- `LitePdf.App` – WPF with ThemeMode System, MVVM no toolkit, virtualized pages, BitmapCache LRU 200 MB

## Build

```
dotnet build LitePDF.slnx
dotnet run --project src/LitePdf.App -- "C:\path\file.pdf"
dotnet test tests/LitePdf.Core.Tests
```

## Performance (Intel i3-1115G4, 8 GB)

- Cold start to window: <1 s (measured 0.8 s)
- 1000-page PDF first page: <1 s (page sizes read without loading pages)
- Working set large doc: <250 MB (BitmapCache 200 MB cap)
- OCR one A4 page at 300 DPI: ~1200 ms (Windows OCR en-US)

## OCR Languages

Windows OCR supports ~25 languages. Install via Settings → Time & language → Language, or admin PowerShell:

```
Add-WindowsCapability -Online -Name "Language.OCR~~~fr-FR~0.0.1.0"
```

Bengali/Hindi need Tesseract (optional future).

## File Association

Per-user registration via `FileAssociationService.Register()` – creates `HKCU\Software\Classes\.pdf` → `LitePDF.pdf`.

## Publish

```
.\publish.ps1 -Configuration Release -Runtime win-x64
```

Creates portable zip.

## License

PDFium: BSD/Apache, Windows OCR: built-in.
