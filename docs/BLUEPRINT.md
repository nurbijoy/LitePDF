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
| `ScanPreprocessor` | Cleans a scan for recognition: colour pen marks read as paper, background divided out (show-through, uneven lighting), deskew, contrast stretched around the measured ink/paper split. `ScanPage` carries the grey image, the ink mask, and the map back to the page |
| `SkewEstimator` | Shears the baseline points and keeps the angle whose horizontal projection is spikiest |
| `ConnectedComponents` | Run-length 8-connected labelling of the ink mask into `InkBlob`s |
| `PageStructure` | What the ink is: glyph height, per-character boxes for a recognized word, thin rules, text columns, and (given the recognized lines) figure regions |
| `MathLayout` | Bars with type stacked above and below: the stacked fractions on the page |
| `FractionSheet` | Lays every fraction half out as lines of type on one sheet for a second recognition pass, and reads the words back by position |
| `OcrLayout` | Rebuilds the page from the recognizer's words: reading order by column, fractions, exponents, degree signs, figures |
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
`Windows.Media.Ocr` reads horizontal lines of type and nothing else. On a scanned page that leaves four things
broken, and each is fixed outside the recognizer, from the measured geometry of the ink rather than from the
words: a stacked fraction comes back as unrelated fragments or a stray dash, an exponent and a degree sign come
back as ordinary digits, a diagram leaks stray labels into the prose, and the lines arrive in no useful order.
`OcrOptions` turns the whole of it off (`OcrOptions.Plain`), which is what the settings toggle does.

Order of work in `WindowsOcrEngine.RecognizeAsync`:
1. `ScanPreprocessor.Prepare` — clean and straighten the page.
2. `PageStructure.Analyze` — blobs, glyph height, rules, columns. Needs no text, so it can run first.
3. `MathLayout.FindFractions` — bars with type stacked close above and below.
4. Blank those regions out of the page image, then recognize it. Left in, their digits come back as fragments
   that cannot be told apart from the words they sit between.
5. Recognize the `FractionSheet` — every half laid out side by side as one line of type. A lone digit
   surrounded by paper is what the recognizer drops; in a line it is read reliably.
6. `PageStructure.FindFigures` — now that the text is known, drawing-like ink that prose runs through is a pen
   mark, and ink holding rows of text is a table. What is left is a figure.
7. `OcrLayout.Rebuild` — group into lines, mark scripts against a locally measured baseline, order by column,
   and map every rectangle back through the deskew.

- **`OcrCache`:** `%LocalAppData%\LitePDF\ocr\{documentKey}\{page}.json`, one file per page: word boxes,
  line kind and figure rectangles. Per-character boxes are not kept — they would multiply the size of every
  page on disk, and without them selection falls back to splitting a word evenly.
- **Pages:** rendered at 400 DPI, long side capped at 5200 px. Scans in the wild are 200–300 DPI; rendering
  above that gives the recognizer whole pixels for small type without inventing detail.
- **Regions:** at least 400 DPI, and upscaled up to 4× so small areas are at least 1400 px wide.
- **Cost:** about 1.2 s a page against 0.5 s for the recognizer alone.

### Why these rules and not others
- **Scripts are digits only.** An exponent or an index in a scanned book is nearly always a digit, whereas a
  short letter measuring as raised is nearly always the baseline being off by a pixel. Marking the letter up
  corrupts an ordinary word, so it is left alone.
- **The baseline is local.** A page photographed out of a bound book does not merely tilt, it curves. Against
  any straight reference, whole runs of glyphs at one end of a line look raised.
- **Lines are grown left to right, from the last word on each.** A band that grew with every word would reach
  its neighbours and chain paragraphs into one line.
- **A figure is settled after the text is read.** Before it, a circled question number and a ruled table both
  look exactly like a drawing.

## 5b. Distribution
- **`publish.ps1`** publishes, runs `--self-test` against the published build, writes a portable zip, then
  builds the installer with Inno Setup. Everything lands in `dist/`. Run it with `-ExecutionPolicy Bypass`:
  Windows refuses unsigned local scripts by default. It deletes each artifact before writing it and checks the
  installer is there afterwards, so a stale build can never be reported as a fresh one.
- **Self-contained by default** (194 MB on disk, 55 MB installer). Framework-dependent is 33 MB but needs the
  .NET 10 Desktop Runtime, which few PCs have yet; `-FrameworkDependent` selects it.
- **Display name vs identifier.** `AppName` in the .iss is what people read; `AppSlug` (`LitePDF`, no space)
  is what Windows stores: the ProgID, the registry subkeys, the `RegisteredApplications` entry, the setup
  filename and the data folder to clean up. They are separate because a ProgID with a space in it is trouble,
  and because the data folder has to keep matching `AppPaths.Root` however the app is renamed. In the app the
  same split holds: `Product` in the project file drives `AppInfo.Name`, while `AssemblyName` stays `LitePDF`.
- **`installer/LitePDF.iss`** installs per-user under `%LocalAppData%\Programs` with no UAC prompt
  (`PrivilegesRequired=lowest`), or for all users when run as admin.
  - Start menu shortcut always; desktop shortcut and PDF association are opt-out tasks.
  - PDF association registers LitePDF as a *candidate*: `Applications\LitePDF.exe`, a `LitePDF.Document`
    ProgID, `.pdf\OpenWithProgIds` and `RegisteredApplications`. Windows 10/11 do not let an installer take
    the default over, and this does not try to — the user picks it from "Open with" or Settings > Default apps.
  - Uninstalling asks before removing `%LocalAppData%\LitePDF` (settings, reading positions, OCR cache), and
    a silent uninstall always keeps it.
  - `AppId` is a fixed GUID: it is how Windows recognizes an upgrade rather than a second copy.
- **Not signed.** SmartScreen will warn on first run until the download builds reputation or the exe is signed
  with an EV certificate.

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
- `settings.json`: theme, page color mode, default zoom, OCR language, OCR layout analysis, sidebar,
  highlight color, window placement.
- `recent.json`: up to 30 entries with reading positions.
- `ocr\`: recognized text cache.
- `logs\litepdf.log`: 1 MB rolling log.

Reading is case-insensitive, so files from older builds load.
