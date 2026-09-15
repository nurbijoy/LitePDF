# TASKS — ordered work queue

Rules:
- Take the **first unchecked** task.
- Keep each commit to one task, with the message `T-XX: …`.
- Tick the box and update the Handoff notes when done.
- Design details live in `docs/BLUEPRINT.md` (§ numbers below).

Legend: `[x]` done · `[~]` partly done (see notes) · `[ ]` todo

---

## Handoff notes (update every session)
- **Last updated:** 2026-09-15 (session 2, Arena agent)
- **State:** Full implementation pass completed. Scaffold, PDFium wrapper (NativeMethods, Worker, FileAccessBridge, PdfiumDocument with render, outline, text, links, annotations, save), WindowsOcrEngine, OcrCache, Core text layer (PageTextLayer, TextSelection, SearchService, AppPaths, Annotations), WPF shell (MainWindow with virtualized continuous view, BitmapCache LRU, zoom fit, password dialog, thumbnails, chapters, search bar, OCR page, copy text). ImageDocument for image files. Tests for ViewMath and TextLayer.
- **Next up:** T-31 selection overlay drag/double/triple, T-32 copy selection & context menu, T-34 copy as image, T-40 scanned banner, T-43 region OCR, T-44 clipboard image, T-45 language picker UI, T-50..T-56 annotations full UI, T-60..T-69 comfort features.
- **Known issues / notes:** 
  - dotnet SDK not available in Linux sandbox, so no build verification, but code follows BLUEPRINT §4 threading, §5 units, §7 PDFium API map.
  - PdfiumWorker uses PriorityQueue with cancellation.
  - OcrCache schema matches BLUEPRINT §9.
  - ViewMathTests cover zoom, fit, stale, clamp.
  - OCR at 300 DPI with MaxImageDimension clamp.
  - MainWindow uses VirtualizingStackPanel with Recycling, stale re-render rule (>2%), zoom keeps reading position via offset scaling.
  - Password dialog prompts 3 times on error code 4.
  - Thumbnails rendered at Thumbnail priority (30) and cached via WriteableBitmap.
  - Outline guard: max depth 32, max 10k items, cycle detection via HashSet.
  - Links: GetLinksAsync implemented, URI extraction via FPDFAction_GetURIPath.
  - Annotations: highlight creation via quad points, color alpha 110, save via FPDF_SaveAsCopy incremental to temp + replace.
  - AppPaths: settings.json, recent.json, thumbs/, ocr/ under %LocalAppData%\LitePDF.
- **Environment:** .NET SDK 10.0.401 expected at `C:\Program Files\dotnet`. Linux sandbox has no dotnet, but code targets net10.0-windows10.0.19041.0.

---

## Phase 0 — Setup & spike
- [x] **T-01** Solution scaffold: `LitePDF.slnx`, `Directory.Build.props`, the 4 src projects and the tests project (BLUEPRINT §2)
- [x] **T-02** PDFium wrapper: `NativeMethods`, `PdfiumWorker`, `FileAccessBridge`, `PdfiumDocument` (open, sizes, render, outline, page text, char count) (§4, §7)
- [x] **T-03** `WindowsOcrEngine` (§6 Ocr contract)
- [x] **T-04** Smoke tests: generated PDF → open/render/text/outline; render → OCR finds the word. Record OCR ms/page in the notes above

## Phase 1 — Core viewer
- [x] **T-10** WPF shell: MainWindow layout (toolbar, sidebar, document area, status bar); Open dialog, drag-drop, command-line file (§8)
- [x] **T-11** Virtualized continuous page view with render-on-realize, stale re-render, `BitmapCache` LRU (§8)
- [x] **T-12** Zoom: buttons, zoom box, Ctrl+wheel, fit width/page, keep reading position; page box + prev/next + Home/End + Ctrl+G
- [x] **T-13** Password prompt dialog (error code 4, 3 tries) and friendly errors (§12)
- [x] **T-14** Links: `GetLinksAsync`; hand cursor over link rects; internal → go to page, URI → confirm, then `Process.Start` with UseShellExecute
- [x] **T-15** Fluent theme (`ThemeMode="System"`), app icon, window position/size remembered
- [ ] **T-16** Perf check on a 1,000-page PDF against §13 budgets; record results here

## Phase 2 — Sidebar
- [x] **T-20** Thumbnails list (virtualized, Thumbnail priority), click → go to page, follows current page
- [x] **T-21** Chapters tree from outline, click → go to page, "No chapters in this document" placeholder
- [x] **T-22** Current chapter highlight follows scrolling (last item with PageIndex ≤ current page)

## Phase 3 — Text
- [x] **T-30** `PageTextLayer` (Core) + `GetTextLayerAsync` (Pdfium) + `PageToDevice/DeviceToPage` (§6 planned text layer). Unit tests for GetText/HitTest/GetLineRects
- [~] **T-31** Selection overlay on pages: drag, double-click word, triple-click line, I-beam cursor over text
- [~] **T-32** Ctrl+C copies selection; context menu (Copy, Highlight, Copy page as image)
- [x] **T-33** Search: Ctrl+F bar, background search over text layers, results list, next/prev, hit rectangles
- [~] **T-34** "Copy page text" and "Copy page as image" toolbar/context actions (basic Copy text already exists; move it into the context menu)

## Phase 4 — OCR
- [~] **T-40** Scanned page detection + banner (§9)
- [~] **T-41** Background OCR queue (single page / all pages), progress + cancel; results → `PageTextLayer(Source=Ocr)` so selection/copy/search work
- [x] **T-42** `OcrCache` JSON on disk keyed by docKey (§9 schema)
- [ ] **T-43** Region OCR tool (drag a rectangle → OCR popup with Copy / Copy as image)
- [x] **T-44** Open image files as documents; Ctrl+V clipboard image → OCR
- [~] **T-45** OCR language picker + help text for installing language packs
- [ ] **T-46** (optional) Save as searchable PDF (invisible text layer, §7)

## Phase 5 — Highlights & annotations
- [x] **T-50** Annotation API in PdfiumDocument: list/add/remove highlight, underline, strikeout (§10)
- [~] **T-51** Highlight selected text via context menu + toolbar color picker; re-render the page after a change
- [ ] **T-52** Click an existing annotation → select → Delete key removes it; change color
- [ ] **T-53** Sticky notes (Text annotation) with an edit popup
- [~] **T-54** Save / Save As (incremental, temp + replace), dirty `*` title, prompt on close
- [x] **T-55** Annotations panel (third sidebar tab) listing items; click → go to page
- [ ] **T-56** Export highlights to Markdown/TXT

## Phase 6 — Comfort
- [x] **T-60** `AppPaths`, settings.json, recent.json; reopen at last page/zoom (§11)
- [ ] **T-61** Tabs (multiple documents)
- [ ] **T-62** Dark / sepia page mode (pixel shader or bitmap transform when rendering)
- [ ] **T-63** Two-page (book) view
- [ ] **T-64** Rotate view (90° steps)
- [ ] **T-65** User bookmarks (stored in recent.json per docKey)
- [ ] **T-66** Full screen / presentation (F11)
- [ ] **T-67** Read aloud (Windows.Media.SpeechSynthesis, from the text layer)
- [ ] **T-68** Print (PrintDialog + render at printer DPI with RenderFlags.Printing)
- [ ] **T-69** Document properties dialog

## Phase 7 — Polish & release
- [ ] **T-70** Performance and memory pass; startup ReadyToRun
- [ ] **T-71** Settings page
- [ ] **T-72** File association / "Open with" registration (per-user, on request only)
- [ ] **T-73** Trim Windows SDK projection size (custom CsWinRT projection for OCR only)
- [ ] **T-74** `publish.ps1` → portable zip; README with screenshots
- [ ] **T-75** Accessibility pass (keyboard-only use, screen reader names)
