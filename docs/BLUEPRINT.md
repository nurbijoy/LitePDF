# LitePDF blueprint (v2)

Describes the current design. Code that differs from this file is a bug, or this file must be updated in the same commit.

## 1. Solution

```
LitePDF.slnx
src/LitePdf.Core      No dependencies. Document contract, geometry, text model, search, layout, imaging, storage.
src/LitePdf.Pdfium    PDFium wrapper (IPdfDocument implementation).
src/LitePdf.Ocr       Windows OCR engine + on-disk OCR cache.
src/LitePdf.App       WPF application.
tools/LitePdf.SampleGen  Generates sample/test PDFs (shared builder is compiled into the tests).
tests/LitePdf.Core.Tests xUnit: Core unit tests, PDFium end-to-end tests, OCR test.
```

## 2. Coordinates
Everything above the PDFium layer uses **normalized page coordinates**: `RectD` with 0..1 across the page *as displayed*
(page `/Rotate` applied, crop box ∩ media box honoured), top-left origin.

- `PdfiumDocument.PageFrame` is the only place that converts to PDF user space. Tests compare it with
  `FPDF_PageToDevice` on rotated and cropped pages.
- View rotation (user rotating the view) is a second, separate transform: `ViewTransform.ToView/FromView`.
- The viewer converts to DIPs with `rect × page size in DIPs`. OCR words are normalized to the recognized bitmap,
  which is the whole page, so OCR text and PDF text share one space.

## 3. Core (`LitePdf.Core`)
| Type | Purpose |
|---|---|
| `IPdfDocument` | Render (full page or a pixel clip of the scaled page, any rotation), text with boxes, outline with target Y, links, annotations CRUD, save copy, metadata, file ID |
| `PageText` | Text + per-char boxes. Visual lines that work for horizontal and rotated text, hit test, caret index (projected on reading direction), word/line ranges, range rectangles, text under rectangles, `FromOcr` |
| `TextRange` / `TextPosition` | Selection that can span pages |
| `TextMatcher` | Case/accent-insensitive search; whitespace runs (incl. line breaks) match one space; whole-word option; line-limited snippets |
| `DocumentLayout` | Exact page rectangles for single/two-page continuous layout, visible range, hit test, nearest page, fit sizes |
| `BitmapOps` | Dark (invert + hue rotate, compressed range) and sepia page modes, bilinear resize, crop |
| `Storage` | `AppPaths`, atomic `JsonFile`, `AppSettings`, `RecentFileStore` (reading position per file), `DocumentKey` |

## 4. PDFium (`LitePdf.Pdfium`)
- **`PdfiumWorker`:** one thread, `PriorityQueue` + `Monitor.Wait/Pulse`.
  - Priorities (lower runs first): Visible 0, Interactive 10, Nearby 20, Thumbnail 30, Background 40; dispose −1.
  - A cancelled item completes immediately and is skipped when dequeued.
- **`FileAccessBridge`:** `FPDF_FILEACCESS` lives in native memory for the document's lifetime and streams from a `FileStream`, so large files aren't loaded into memory.
- **Rendering:** `FPDF_RenderPageBitmap` with negative start offsets renders only a clip of the scaled page. This gives cheap sharp tiles at high zoom and region OCR.
- **Text:**
  - Boxes come from `FPDFText_GetLooseCharBox` (line-height boxes suit selection).
  - Generated chars carry no box.
  - The `\r\n` PDFium inserts becomes `\n`.
- **Annotations:**
  - Quads are written top-left, top-right, bottom-left, bottom-right.
  - Recoloring removes the appearance stream (`FPDFAnnot_SetAP(null)`) so PDFium regenerates it.
  - Color is read from `/C`, or from the appearance stream objects when `/C` isn't available.
- **Save:** `FPDF_SaveAsCopy` incremental, falling back to a full save. `SaveCopyAsync` refuses the open path; see §6 for replacing the file.

## 5. OCR (`LitePdf.Ocr`)
- **`WindowsOcrEngine`:** keeps one engine per language. Images larger than `MaxImageDimension` are resized. Word boxes are normalized to the bitmap.
- **`OcrCache`:** `%LocalAppData%\LitePDF\ocr\{documentKey}\{page}.json` holds normalized word boxes, one file per page.
- **Pages:** rendered at 300 DPI, long side capped at 4200 px.
- **Regions:** at least 300 DPI, and upscaled up to 4× so small areas are at least 1400 px wide.

## 6. App (`LitePdf.App`)
```
App.xaml(.cs)            startup, global error handling, --self-test
Themes/Light|Dark.xaml   color tokens (Brush.*). ThemeManager swaps the palette (removing any previous one; later
                         merged dictionaries win lookups)
Themes/Styles.xaml       complete control styles (buttons, inputs, menus, lists, tree, scrollbars, tooltips, dialogs)
Infrastructure/          Mvvm, Log, ErrorReporter (non-reentrant), ThemeManager, NativeWindow (dark title bar),
                         converters, LruCache, Icons, ClipboardHelper (retries), PrintService, SpeechService
Documents/               DocumentSession, ImageDocument (PNG/JPEG/TIFF… as pages), RenderCache + PageRenderer
Viewer/                  PdfViewer (input, zoom, selection, search hits), PagesPanel (IScrollInfo virtualization),
                         PageVisual (bitmap + detail tile + overlays)
ViewModels/              MainViewModel (bindable state) + item view models
Views/Dialogs.cs         DialogWindow base, message/password/text input/OCR result/properties/settings dialogs
MainWindow*.cs           Window controller split by area: core (keys, menus, layout), Document (open/save/print),
                         Panels (thumbnails, chapters, search, annotations), Actions (copy, markup, notes, OCR)
```

### DocumentSession
- Owns the `IPdfDocument` plus derived state:
  - PDF text LRU (48 pages) and OCR text (all recognized pages).
  - The OCR cache, links and annotations.
  - A per-page *generation* counter (incremented on edits so cached bitmaps are invalidated).
- Events: `PageInvalidated`, `PageTextChanged`, `AnnotationsChanged`, `DirtyChanged`, `DocumentReplaced`.
- **Save:**
  1. `SaveCopyAsync` to a temp file in the target folder.
  2. Dispose the document to release the file handle.
  3. `File.Replace` (same file) or `File.Move` (Save as).
  4. Reopen the document.
  - If replacing fails, the original is reopened and the temp file deleted.

### Viewer
- **`PagesPanel`:** implements `IScrollInfo`. It realizes only pages within ±½ viewport of the visible area and arranges them relative to the viewport, so there are no huge coordinates. Offsets come from `DocumentLayout`, so scrolling is exact.
- **Zoom:** keeps the document point under the anchor fixed (mouse for Ctrl+wheel, viewport center for buttons, top for fit modes). Fit modes re-fit on resize.
- **`PageVisual` rendering:**
  - Base bitmap capped at 12 MP. Beyond that, once scrolling settles (120 ms), a sharp *detail tile* of the visible area is rendered at full scale.
  - Stale bitmaps stay visible, stretched, until the new render arrives.
- **Input:**
  - Select tool: drag selection with auto-scroll, double-click word, triple-click line, Shift+click extend, link click, annotation select, Delete, Esc.
  - Hand tool and area tool (region → menu: copy text / recognize / copy image).
  - Right-click raises `ContextRequested`; the window builds the menu.
- **Render cache:** pages 128 MB and thumbnails 24 MB (LRU by bytes), keyed by page + generation + color mode + rotation.

### Main window behaviour highlights
- **Recent files:** restores page, offset, zoom mode, rotation and layout per file.
- **Scanned pages:** a banner offers OCR when a page has fewer than 8 visible characters and contains images.
- **OCR:** progress card with cancel. Recognized pages update selection and search immediately.
- **Search:** live (350 ms debounce), with a results list, F3/Shift+F3, and all hits drawn on pages. Capped at 5,000 results.
- **Annotations panel:** lists markup with the text under it; exports to Markdown.
- **Keyboard:**
  - Ctrl+O/S/Shift+S/P/W/F/G/B/D/C/H/U/R/V
  - Ctrl+ + / − / 0 / 1 / 2
  - F3, F4, F11, Esc
  - V/H/R tool keys when the viewer has focus
  - Alt combinations are left to Windows

## 7. Persistence
`%LocalAppData%\LitePDF\`:
- `settings.json`: theme, page color mode, default zoom, OCR language, sidebar, highlight color, window placement.
- `recent.json`: up to 30 entries with reading positions.
- `ocr\`: recognized text cache.
- `logs\litepdf.log`: 1 MB rolling log.

Reading is case-insensitive, so files from older builds load.
