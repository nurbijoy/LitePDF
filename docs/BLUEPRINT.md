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
| `PageContent` | What the DOCX export needs from a page: styled character spans, placed images and drawings, thin rules, filled panels, the structure tree, embedded fonts |
| `ContentComposer` | Rebuilds a document from page geometry: columns, paragraphs, headings, lists, running heads, comments |
| `DocumentProfile` | Document-wide measurements: body size and font, text area, heading sizes, running heads |
| `TagIndex` | A tagged PDF's structure tree resolved against its characters: headings, list items, cells, figures |
| `TableBuilder` | Ruled tables: snaps the hairline rectangles into a grid and finds the spans |
| `UnruledTableBuilder` | Tables that draw no lines, from the alignment of the words alone (opt-in) |
| `DocxWriter` | Writes the Office Open XML package (`ZipArchive` + `XmlWriter`, no third-party library) |

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

## 5c. Word export (`Core/Export`, `PdfContentReader`)
A PDF has no paragraphs, only positioned glyphs, so every structure in the .docx is inferred. The ordering
principle is that **a rule that does not fire still leaves the content intact**: an unclaimed line becomes a
correctly styled paragraph in the right place, and nothing is ever dropped to tidy the output.

1. **`PdfContentReader`** (in `LitePdf.Pdfium`) reads the page: characters with their style, images,
   vector artwork, thin rules, filled panels and the structure tree. Style is read once per text object,
   not per character — a dense page has thousands of characters and a handful of objects.
   - The visible size is measured from the loose char box against the text matrix, because
     `FPDFText_GetFontSize` reports the text-state size and ignores matrix scaling.
   - A plain `DCTDecode` image is copied out as JPEG bytes, untouched. Anything else is rendered — and
     because `FPDFImageObj_GetRenderedBitmap` renders at the size the image is *placed* at, a stored image
     with more pixels than that is re-rendered from the page at its own resolution, or a 300 DPI scan would
     come back at 72 DPI.
   - Text drawn with render mode 3 is the invisible OCR layer under a scan. It is used only when the page
     has no visible text of its own, and then the picture it sits under is skipped, or every word would be
     written twice.
   - Objects inside a form XObject are walked with a matrix stack, because a nested object's bounds are in
     the form's space; without it a picture placed through a form is dropped or lands in the wrong place.
   - Paths are sorted into three kinds: a hairline is a table rule, an axis-aligned filled rectangle is a
     panel (a shaded cell, a background), and everything else is artwork. Artwork is clustered into regions
     and each is rasterized at 300 DPI with the text and image objects switched off
     (`FPDFPageObj_SetIsActive`), so a chart arrives as one transparent PNG and its labels stay text.
   - `FPDF_StructTree_*` gives the structure tree of a tagged PDF, and `FPDFPageObj_GetMarkedContentID`
     ties each element to the characters and pictures it drew.
2. **`DocumentProfile`** measures the document as a whole: body size (modal, weighted by characters),
   body font, text area and the running heads. Running heads are settled first, because a page number in
   the margin would otherwise drag the measured margins out to it.
3. **`TagIndex`** resolves the structure tree against the page's characters, when the PDF has one. There is
   no second front end: the tags feed the same composer as everything else, as hints it trusts over its own
   inference. A tagged file settles heading levels, which lines are one paragraph, which are list items and
   where a table's cells are — and nothing else, because tags say nothing true about appearance.
4. **`ContentComposer`** rebuilds each page: columns, then reading order, then paragraphs, then headings,
   lists and tables, then anchors the page's sticky notes as Word comments. See §5d for why each rule is
   drawn where it is.
5. **`DocxWriter`** writes the package: document, styles, numbering, media, headers and footers, comments,
   and — only when asked — the font table with each font obfuscated the way Word stores one. Everything
   goes through `XmlWriter`: one unescaped ampersand out of a PDF is a "Word found unreadable content"
   prompt, which is a hard failure.

### 5d. Why these rules and not others
- **A line ends its paragraph only if the next line's first word would have fitted on it.** A fixed
  fraction of the measure gets this wrong constantly: ragged-right prose routinely stops 20 pt short
  because the next word is 30 pt wide, and that has ended nothing.
- **Columns are looked for before spanning lines are.** The other way round, every line of an ordinary
  single-column page looks like a spanning line, because there the line and the text area are one width.
- **The outline settles heading levels.** A bookmark carries a page and a Y, which is exact information the
  app already loads; it beats every font-size heuristic.
- **Body size is measured globally.** Per page, every page's largest line becomes a heading.
- **A line opening with a bullet or a number starts a new item** whatever the geometry says, or a list whose
  items are all one line and all the same width reads as one paragraph.
- **The page number in a running head is the digit run that tracks the page across every sample.**
  "Section 1, page 1" has two, and on the first page they are both 1.
- **A table is trusted in this order: drawn, tagged, then guessed.** The grid a PDF actually draws is a
  fact; a tagged file's cells are a fact it states; alignment alone is an inference, and getting it wrong
  turns readable prose into a mangled grid, so that one is off unless the export is asked for it.
- **Tags settle grouping, never appearance.** A tagged file will mark a run as `/P` and draw it bold at
  18 pt, so structure comes from the tags and every font, size and colour still comes from the glyphs.
- **Vector artwork is rasterized, not translated.** Word's DrawingML could express the beziers, but PDF's
  clips, soft masks, blend modes, shadings and patterns are a project the size of the rest of the export,
  and a half-translated chart is worse than a faithful picture of one.
- **A filled rectangle with text on top of it is a panel, not a drawing.** Rasterizing one would paint a
  picture of the page over the words that were lifted off it.
- **Left and top margins are the median; right and bottom are a high percentile capped by the facing
  margin.** A page only reaches the right margin when its content is long enough, so the median there
  reports the margins of the emptiest half of the document.

## 5b. Distribution
- **`publish-msix.ps1`** additionally builds an unsigned, self-contained x64 MSIX for Microsoft Store upload.
  It requires the exact Store identity, publishes to a fresh staging directory, runs the app self-test, and
  packages with Windows SDK MakeAppx validation. The manifest declares the full-trust app and PDF association.
  The display name comes from `Product`; Microsoft supplies Store signing and hosting. See `docs/MSIX.md`.
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
                         converters, LruCache, Icons, ClipboardHelper (retries), PrintService, SpeechService,
                         SingleInstance (named pipe IPC + session mutex)
Documents/               DocumentSession, ImageDocument (PNG/JPEG/TIFF… as pages), RenderCache + PageRenderer,
                         DocxExport (drives the Word conversion) + WpfImageEncoder
Viewer/                  PdfViewer (input, zoom, selection, search hits), PagesPanel (IScrollInfo virtualization),
                         PageVisual (bitmap + detail tile + overlays)
ViewModels/              MainViewModel (bindable state), DocumentTab (per-tab state) + item view models
Views/Dialogs.cs         DialogWindow base, message/password/text input/OCR result/properties/settings dialogs
Views/ExportDocxDialog   Page range, what to keep, and how much to infer, for the Word conversion
MainWindow*.cs           Window controller split by area: core (keys, menus, layout), Document (open/save/print/tabs),
                         Panels (thumbnails, chapters, search, annotations), Actions (copy, markup, notes, OCR)
```

### Tabs & Single-Instance IPC
- **Single-instance:** Second instance checks session mutex `Local\LitePDF-SingleInstance-{User}` and sends open arguments via named pipe `LitePDF-IPC-{User}` to the primary instance, then exits immediately.
- **Tabs:** Each document runs in a `DocumentTab` inside `MainWindow`. Tab strip displays document icon, name, dirty dot indicator, and close button `×`.
- **View state preservation:** Switching tabs preserves reading position, zoom, rotation, thumbnails, outline, search queries and results.
- **Close safety:** Closing the window with multiple tabs open prompts with a warning dialog to prevent accidental loss; individual tab close prompts on unsaved changes.

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
