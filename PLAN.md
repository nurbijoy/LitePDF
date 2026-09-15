# LitePDF — Project Plan

A small, fast PDF reader for Windows with thumbnails, chapters, on-device OCR, copy and highlights.

---

## 1. Is it feasible?

**Yes.** Every requested feature can be built on free parts that run on the device:

- **Rendering, text, chapters, links, highlights** → **PDFium**, the PDF engine inside Chrome and Edge (BSD/Apache license, fine for private or open-source apps).
- **OCR** → **Windows' built-in OCR engine** (`Windows.Media.Ocr`). It is already on this PC (English installed). It needs no internet and no extra download.

The main limits are which languages the OCR supports and app size. Both are covered in [Risks](#8-risks--mitigations).

---

## 2. What "lite" means (targets)

Measured on this PC (Intel i3-1115G4, 8 GB RAM), which makes a good low-end test machine.

| Target | Goal |
|---|---|
| Startup to window | < 1 s |
| Open a 1,000-page PDF, first page shown | < 1 s |
| Scrolling | Smooth; page rendering never blocks the UI |
| Memory with a large PDF open | < 250 MB (page cache is capped) |
| Size on disk | < 40 MB, reduced further in Phase 7 |
| Network | None. Works offline, no telemetry |

---

## 3. Tech stack

| Layer | Choice | Why |
|---|---|---|
| Language / runtime | **C# on .NET 10 (LTS)** | Quick to build, direct access to Windows APIs, long-term support |
| UI | **WPF** with its built-in Windows 11 (Fluent) theme | Mature and reliable. Builds and debugs from the command line. Clipboard, drag-drop and printing are easy |
| PDF engine | **PDFium** (prebuilt `pdfium.dll` from `bblanchon/pdfium-binaries`) via a thin P/Invoke wrapper | Fast and battle-tested. Covers rendering, per-character text positions, outline, links, annotations and saving |
| OCR | **Windows.Media.Ocr** behind an `IOcrEngine` interface | On-device, free, fast. The interface lets a Tesseract engine be added later for other languages |
| MVVM helpers | CommunityToolkit.Mvvm | Small, source-generated, no runtime weight |
| Tests | xUnit | |

**Alternatives considered**

- **WinUI 3 + Native AOT**: slightly faster startup and smaller size. The cost is more build and debug friction, and printing is harder. Worth choosing if the native Windows 11 look matters more to you than development speed.
- **MuPDF**: an excellent engine, but its AGPL license forces the whole app to be open source (or a paid license).
- **Extending SumatraPDF** (C++, GPLv3): already very lite and has highlights and search. Thumbnails and OCR would need adding. It's the fastest route if C++ and the GPL are acceptable.
- **Web tech** (Tauri/Electron + pdf.js): easier UI work, but more memory and slower on big PDFs. That goes against "lite".

---

## 4. Features

### 4.1 Your list

1. **PDF reading**
   - Continuous vertical scroll
   - Zoom: Ctrl + mouse wheel, fit width, fit page
   - Go to page, keyboard navigation
   - Clickable links (inside the document and to the web)
   - Password-protected PDFs
2. **Thumbnail view**
   - Sidebar of page previews, loaded lazily
   - The current page is marked; click a thumbnail to jump to that page
3. **Chapter view**
   - The PDF's outline shown as a collapsible tree
   - The current chapter is highlighted as you scroll
   - A clear message when the PDF has no outline
4. **On-device OCR (text and images)**
   - **Scanned pages**: pages without real text are detected and OCR'd in the background, with progress and cancel. The OCR text can then be selected, copied, searched and highlighted.
   - **Region OCR**: draw a rectangle on any page and get its text in a popup with a Copy button.
   - **Images**: open PNG/JPG/TIFF/BMP files directly, or paste an image from the clipboard, then OCR it.
   - **Cached results**: each page is OCR'd only once, even if you reopen the file.
   - *Later:* "Save as searchable PDF", which writes an invisible text layer into the file.
5. **Copy**
   - Select text by dragging; double-click selects a word, triple-click a line
   - Ctrl+C copies the selection
   - Copy a region or a whole page as an image
6. **Highlight**
   - Highlight, underline or strike through selected text, in 5 colors; remove any of them
   - Saved as **standard PDF annotations**, so they show in Edge, Chrome and Acrobat
   - Also works on OCR'd text

### 4.2 Proposed extras ("etc.")

- **Reading**
  - Search (Ctrl+F) with a results list
  - Tabs
  - Recent files that reopen at the last page
  - Dark and sepia page modes
  - Two-page (book) view, rotate, full screen
  - Your own bookmarks
- **Notes**
  - Sticky notes
  - Annotations panel listing all highlights and notes
  - Export highlights to Markdown or TXT
- **Other**
  - Read aloud using Windows' on-device voices
  - Print
  - Document properties
  - Drag & drop, "Open with", set as default PDF app

---

## 5. Architecture

```
LitePDF/
├─ src/
│  ├─ LitePdf.App/      WPF UI: windows, views, view models, settings, clipboard
│  ├─ LitePdf.Core/     Engine-neutral: document/page models, text layer, selection,
│  │                    search, coordinate transforms, render scheduler, caches
│  ├─ LitePdf.Pdfium/   P/Invoke wrapper: render, text, outline, links, annotations, save
│  └─ LitePdf.Ocr/      IOcrEngine + Windows OCR implementation, OCR cache
├─ tests/
│  └─ LitePdf.Core.Tests/
├─ samples/             Test PDFs: text, scanned, with outline, 1,000 pages, password
└─ PLAN.md
```

```
 UI thread (WPF)                            Background
 ───────────────                            ──────────
 DocumentView ── request page N @ zoom ──▶  Render queue
      ▲                                     (visible > nearby > thumbnails)
      │                                           │  one PDFium worker thread
      └───────── bitmap (LRU cache) ◀─────────────┘

 Selection / Copy / Search / Highlight
      │ all use
      ▼
 ITextLayer (per page) ─┬─ PdfiumTextLayer  (real text in the PDF)
                        └─ OcrTextLayer     (Windows OCR words + boxes)
```

### Key design decisions

1. **One PDFium thread.** PDFium is not thread-safe, so every call goes through one worker with a priority queue. Requests for pages you've scrolled past are cancelled. The UI never waits on it.
2. **Unified text layer.** Selection, copy, search and highlight all work on `ITextLayer`: characters or words with their boxes. They don't care whether the text came from the PDF or from OCR. This is what makes OCR text behave like real text.
3. **One coordinate transform.** A single tested class converts PDF points (origin bottom-left) to screen pixels (zoom, monitor DPI, rotation) and back. Everything uses it.
4. **Standard annotations, safe saving.** Highlights are real PDF Highlight annotations.
   - Saves are **incremental**: new data is appended and the original bytes stay untouched.
   - Saves write to a temp file first, then replace the original, so a crash can't corrupt it.
5. **Local data only.** `%LocalAppData%\LitePDF\` holds settings, recent files, and the thumbnail and OCR caches.
   - Caches are keyed by the PDF's internal file ID, so a renamed file keeps its OCR results.

---

## 6. How the harder parts work

- **Rendering**
  - `FPDF_RenderPageBitmap` draws into a BGRA buffer, which is shown as a `WriteableBitmap`.
  - Visible pages are rendered at zoom × monitor DPI.
  - While zooming, the existing bitmap is stretched; the page is re-rendered once zoom settles.
  - The cache is capped at about 150 MB.
- **Thumbnails**: the same pipeline at about 150 px wide, at the lowest priority. Cached on disk for big files.
- **Chapters**
  - Walk the outline with `FPDFBookmark_*` to build the tree; `FPDFDest_GetDestPageIndex` gives each entry's page.
  - Current chapter = the last outline entry whose page ≤ the current page.
- **Select and copy**
  - `FPDFText_*` gives every character and its box.
  - A mouse position maps to a character index, and a selection is a range of characters shown as rectangles.
  - Copying extracts the text for that range.
- **Search**: runs over the text layer in the background and shows results as they are found.
- **OCR**
  1. Render the page at 300 DPI into BGRA.
  2. Wrap it in a `SoftwareBitmap`. It's the same pixel format, so no conversion is needed.
  3. `OcrEngine.RecognizeAsync` returns lines and words with boxes.
  4. Convert the boxes to PDF coordinates to build the `OcrTextLayer`.
  - Images larger than the OCR limit (10,000 px on this PC) are scaled down.
  - A page counts as "scanned" when it has little or no extractable text.
- **Highlight**
  - Selection rectangles become quad points, then `FPDFPage_CreateAnnot(HIGHLIGHT)` plus a color.
  - PDFium draws the highlight; `FPDF_SaveAsCopy` with `FPDF_INCREMENTAL` saves it.

---

## 7. Phases

Sizes are relative: S = small, M = medium, L = large.

### Phase 0: Setup & spike (S)
- Install the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).
  - Optional: VS Code + C# Dev Kit. It's much lighter than Visual Studio on 8 GB RAM.
- `git init`, create the solution skeleton, fetch `pdfium.dll`, collect sample PDFs.
- **Spike:** a tiny WPF window that renders page 1 with PDFium and OCRs it with Windows OCR.
- **Done when:** the spike shows a page and prints its OCR text, and OCR time per page has been measured.

### Phase 1: Core viewer (M)
- Open files by dialog, drag-drop or command line.
- Continuous scroll that only renders visible pages.
- Zoom modes, page navigation, keyboard shortcuts.
- Render queue and cache, password prompt, clickable links.
- **Done when:** a 1,000-page PDF opens in < 1 s and scrolls smoothly on this PC.

### Phase 2: Thumbnails & chapters (S)
- Thumbnail sidebar and outline tree, both tracking the current page.
- **Done when:** thumbnails load without slowing scrolling, outline entries jump to the right page, and the current chapter follows scrolling.

### Phase 3: Select, copy, search (M)
- Text selection, copy text, copy region or page as image, Ctrl+F search with a results list.
- **Done when:**
  - Copying from multi-column and rotated pages gives sensible text.
  - Search across 1,000 pages shows results as they come in.

### Phase 4: OCR (M)
- Scanned-page detection and background OCR with progress and cancel.
- OCR cache and region OCR popup.
- Open or paste images.
- Language picker (lists the installed Windows OCR languages).
- **Done when:**
  - A scanned PDF becomes selectable and searchable.
  - Region OCR copies text.
  - Reopening the file doesn't OCR it again.

### Phase 5: Highlights & annotations (M)
- Highlight, underline and strikethrough in colors; remove them.
- Sticky notes and an annotations panel.
- Save / Save As, unsaved-changes prompt, export highlights.
- **Done when:** highlights saved by LitePDF appear in Edge and Acrobat, survive reopening, and work on OCR'd pages.

### Phase 6: Comfort features (M)
- Tabs, recent files with resume, dark/sepia mode, two-page view.
- Rotate, full screen, bookmarks.
- Read aloud, print, document properties.

### Phase 7: Polish & release (S–M)
- Performance and memory pass on this PC.
- Settings page and default-app / file association.
- Installer or portable zip.
- Shrink the Windows API bindings to only the OCR parts, to cut size.
- Tests and bug fixing.
- **Optional:** Save as searchable PDF, Tesseract language packs, form filling.

---

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Windows OCR supports about 25 languages (Latin scripts, Cyrillic, Greek, Arabic, Chinese, Japanese, Korean). It has **no Indic scripts** (e.g. Bengali, Hindi) and is weak on handwriting | The `IOcrEngine` interface allows an optional Tesseract engine for the missing languages |
| Only English (en-US) OCR is installed on this PC | Add languages in Settings → Time & language → Language, or from an admin PowerShell: `Add-WindowsCapability -Online -Name "Language.OCR~~~fr-FR~0.0.1.0"` |
| PDFium is not thread-safe | One dedicated worker thread (design decision 1) |
| A save could corrupt the PDF | Incremental save to a temp file, then replace the original; save → reload tests |
| Poor scans give poor OCR | Render at 300 DPI in grayscale; add deskew and contrast fixes if needed |
| App size: the Windows API bindings are large and WPF can't be trimmed | Ship framework-dependent; generate bindings for only the OCR APIs in Phase 7 |
| Unusual or broken PDFs | PDFium handles real-world files in Chrome; keep a sample set of odd PDFs |

---

## 9. Testing

- **Unit tests:** coordinate transforms, selection → text, search, OCR box mapping, annotation save → reload.
- **Sample files:** kept in `samples/`.
- **Manual checklist** for each phase, run on this PC.
- **Performance log:** startup time, open time and memory recorded after each phase.

---

## 10. Decisions needed from you

1. **UI framework:** WPF (recommended) or WinUI 3?
2. **OCR languages:** do you need any besides English? Bengali or Hindi, for example, would need Tesseract.
3. **Highlights:** save into the PDF file (recommended; they show in other readers) or store them separately so originals are never modified?
4. **Distribution:** portable zip, installer, or Microsoft Store? Open source or private?
5. **Extras:** which items from section 4.2 matter most to you?
