# TASKS

## Status (2026-09-16, v2 rewrite)
Core, PDFium wrapper, OCR and the WPF app were rewritten. The previous implementation didn't build, crashed at startup and had many broken features.

**Verification performed:**
- Build: 0 warnings.
- 44 automated tests: Core, PDFium end-to-end including rotated/cropped coordinate checks and annotation round-trips, OCR.
- `--self-test` passes in both themes.
- Driven with real mouse/keyboard input on the Intel i3-1115G4 / 8 GB dev PC, with screenshots, against the generated samples.

### Verified working in the running app
- [x] Open from command line, dialog, drag & drop, recent list; reading position restored
- [x] Continuous scroll, exact virtualization, zoom menu/buttons/Ctrl+wheel, fit width/page, sharp 400 % zoom (detail tiles)
- [x] Thumbnails (current page follows, annotations appear), chapters (navigates to XYZ position, current chapter marked)
- [x] Text selection and copy on normal and **rotated** pages; Ctrl+A; context menu
- [x] Search: live results list, hit highlighting, F3 navigation; 179/179 expected hits; 1,000-page document
- [x] Highlight via Ctrl+H; saved into the PDF (temp + replace, no temp files left); persists after reopen; annotations panel
- [x] Notes via context menu (dialog, icon on page and thumbnail)
- [x] Scanned page banner → Recognize page → OCR text selectable, copyable and searchable; status "Recognized text"
- [x] Area tool → Copy text (PDF text or OCR fallback)
- [x] Dark page mode, dark/light app theme (follows Windows), two-page view, properties dialog, settings dialog
- [x] Unsaved-changes prompt on Alt+F4 / close; settings saved on exit

### Implemented but not yet exercised in the UI (covered by unit/self tests or code review only)
- [ ] Password-protected PDFs: no encrypted sample yet. Needs a sample and a manual test of the prompt/retry flow.
- [ ] Printing: dialog, page range, background spooling. Test on a real printer and with "Microsoft Print to PDF".
- [ ] Read aloud (Windows voices), export annotations to Markdown, recolor/delete annotation from the context menu,
      underline/strikethrough, recognize all pages with cancel, paste image from clipboard, open image files (TIFF multi-frame)
- [ ] Links: external link confirmation dialog, blocked non-web schemes
- [ ] Full screen (F11) enter/exit, including exit when maximized
- [ ] Save as to a different folder; save failure on a read-only file (should keep the original and explain)

### Measured performance (Release, dev PC above)
| Metric | Result | Target |
|---|---|---|
| Launch → 1,000-page document shown | 2.35 s (not ReadyToRun) | < 1 s: use `publish.ps1` (ReadyToRun) and re-measure |
| Working set after opening 1,000 pages | 178 MB | < 250 MB |
| Working set after heavy scrolling | 255–265 MB (private 183 MB) | < 250 MB: close; see T-P2 |
| OCR one Letter page at 300 DPI | ≈ 0.5–1 s | — |

## Next work (priority order)
1. **T-V1** Manually verify the "not yet exercised" items above; add an encrypted sample to SampleGen (needs an RC4/AES writer or a checked-in small file).
2. **T-P1** Measure startup with the ReadyToRun publish; profile startup (PDFium init, WPF theme load, DocumentKey hashing).
3. **T-P2** Pool render buffers (`ArrayPool`) to reduce LOH churn; consider rendering bitmaps straight into `WriteableBitmap`.
4. **T-F1** Tabs or multiple windows (currently one document per window; opening another replaces it after a save prompt).
5. **T-F2** Save as searchable PDF: embed OCR text as invisible text (`FPDFText_SetText`, render mode 3).
6. **T-F3** Additional OCR languages beyond Windows' set (e.g. Bengali/Hindi) via an optional Tesseract `IOcrEngine`.
7. **T-F4** Form filling (`FPDFDOC_InitFormFillEnvironment`), ink/freehand annotations.
8. **T-A1** Accessibility pass with Narrator: page text exposure via UI Automation for the viewer, focus order, high contrast theme.
9. **T-D1** Installer (MSIX or Inno Setup), file association ("Open with"), app icon.
