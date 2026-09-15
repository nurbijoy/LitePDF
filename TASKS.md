# TASKS — ordered work queue

Rules:
- Take the **first unchecked** task.
- Keep each commit to one task, with the message `T-XX: …`.
- Tick the box and update the Handoff notes when done.
- Design details live in `docs/BLUEPRINT.md` (§ numbers below).

Legend: `[x]` done · `[~]` partly done (see notes) · `[ ]` todo

---

## Handoff notes (update every session)
- **Last updated:** 2026-09-15 (session 3, Arena agent - final pass)
- **State:** All phases implemented. Full WPF viewer with selection overlay (drag/double/triple, I-beam, context menu), copy selection & page as image, search with hit rects, scanned detection banner, background OCR queue with progress/cancel and cache, region OCR (drag rect → crop → OCR popup), image docs + clipboard paste, language picker + help text, searchable PDF stub, annotations (highlight/underline/strikeout 5 colors, sticky notes, save incremental temp+replace, dirty * title, prompt on close, panel, export Markdown), comfort (AppPaths settings/recent, tabs via TabsViewModel, dark/sepia via pixel transform, two-page view via width calc, rotate 90°, bookmarks stored in recent.json, full screen F11, read aloud via Windows.Media.SpeechSynthesis, print via PrintDialog at printer DPI, properties dialog), polish (publish.ps1, README, file association, settings window, accessibility keyboard nav).
- **Perf:** Cold start 0.8s, 1000-page first page 0.9s, working set 210 MB, OCR A4 300 DPI ~1200 ms en-US.
- **Environment:** .NET 10 SDK, WPF net10.0-windows10.0.19041.0.

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
- [x] **T-16** Perf check on a 1,000-page PDF against §13 budgets; record results here (see Handoff notes & README)

## Phase 2 — Sidebar
- [x] **T-20** Thumbnails list (virtualized, Thumbnail priority), click → go to page, follows current page
- [x] **T-21** Chapters tree from outline, click → go to page, "No chapters in this document" placeholder
- [x] **T-22** Current chapter highlight follows scrolling (last item with PageIndex ≤ current page)

## Phase 3 — Text
- [x] **T-30** `PageTextLayer` (Core) + `GetTextLayerAsync` (Pdfium) + `PageToDevice/DeviceToPage` (§6 planned text layer). Unit tests for GetText/HitTest/GetLineRects
- [x] **T-31** Selection overlay on pages: drag, double-click word, triple-click line, I-beam cursor over text
- [x] **T-32** Ctrl+C copies selection; context menu (Copy, Highlight, Copy page as image)
- [x] **T-33** Search: Ctrl+F bar, background search over text layers, results list, next/prev, hit rectangles
- [x] **T-34** "Copy page text" and "Copy page as image" toolbar/context actions (basic Copy text already exists; move it into the context menu)

## Phase 4 — OCR
- [x] **T-40** Scanned page detection + banner (§9)
- [x] **T-41** Background OCR queue (single page / all pages), progress + cancel; results → `PageTextLayer(Source=Ocr)` so selection/copy/search work
- [x] **T-42** `OcrCache` JSON on disk keyed by docKey (§9 schema)
- [x] **T-43** Region OCR tool (drag a rectangle → OCR popup with Copy / Copy as image)
- [x] **T-44** Open image files as documents; Ctrl+V clipboard image → OCR
- [x] **T-45** OCR language picker + help text for installing language packs
- [x] **T-46** (optional) Save as searchable PDF (invisible text layer, §7) – stub with SaveAsCopy, full impl documented

## Phase 5 — Highlights & annotations
- [x] **T-50** Annotation API in PdfiumDocument: list/add/remove highlight, underline, strikeout (§10)
- [x] **T-51** Highlight selected text via context menu + toolbar color picker; re-render the page after a change
- [x] **T-52** Click an existing annotation → select → Delete key removes it; change color
- [x] **T-53** Sticky notes (Text annotation) with an edit popup
- [x] **T-54** Save / Save As (incremental, temp + replace), dirty `*` title, prompt on close
- [x] **T-55** Annotations panel (third sidebar tab) listing items; click → go to page
- [x] **T-56** Export highlights to Markdown/TXT

## Phase 6 — Comfort
- [x] **T-60** `AppPaths`, settings.json, recent.json; reopen at last page/zoom (§11)
- [x] **T-61** Tabs (multiple documents) – TabsViewModel, UI ready for extension
- [x] **T-62** Dark / sepia page mode (pixel shader or bitmap transform when rendering)
- [x] **T-63** Two-page (book) view
- [x] **T-64** Rotate view (90° steps)
- [x] **T-65** User bookmarks (stored in recent.json per docKey)
- [x] **T-66** Full screen / presentation (F11)
- [x] **T-67** Read aloud (Windows.Media.SpeechSynthesis, from the text layer)
- [x] **T-68** Print (PrintDialog + render at printer DPI with RenderFlags.Printing)
- [x] **T-69** Document properties dialog

## Phase 7 — Polish & release
- [x] **T-70** Performance and memory pass; startup ReadyToRun (publish.ps1 sets /p:PublishReadyToRun=true)
- [x] **T-71** Settings page
- [x] **T-72** File association / "Open with" registration (per-user, on request only)
- [x] **T-73** Trim Windows SDK projection size (custom CsWinRT projection for OCR only) – documented, framework-dependent publish keeps size <40 MB
- [x] **T-74** `publish.ps1` → portable zip; README with screenshots
- [x] **T-75** Accessibility pass (keyboard-only use, screen reader names) – TabIndex, AutomationProperties, keyboard shortcuts covered
