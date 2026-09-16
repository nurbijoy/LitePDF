# TASKS

## Status (2026-09-16, OCR layout analysis)
`Windows.Media.Ocr` on its own reads a scanned exam paper badly: stacked fractions come back as a stray dash,
exponents and degree signs as ordinary digits, diagrams as garbled labels, and the lines in no useful order.
A layout pass now sits either side of the recognizer and rebuilds the page from the geometry of the ink. See
`docs/BLUEPRINT.md` §5 for the order of work and why each rule is drawn where it is.

**Verification performed:**
- Build: 0 warnings. 79 automated tests pass. `--self-test` passes.
- Measured against `D:\Book\QBank` (IBA MBA papers: ~265 DPI photographs of a bound book, heavily annotated
  in pen, with show-through from the reverse side).
- Driven in the running app: scan banner → Recognize page, search, settings in both themes.

### Verified on the QBank scans
- [x] Show-through, uneven lighting and coloured pen marks removed before recognition
- [x] Stacked fractions rebuilt: 10 of 10 bars found on a page, read as `3/7`, `3.3/7`, `AB/C`, `22 1/2`
      (the one miss on that page is an option whose bar is buried under a pen stroke)
- [x] Exponents and degree signs: `x^3`, `3x^3`, `7.5°`, `90°`, `∠BAC = 90°`
- [x] Figures: the two diagrams on the page marked, the ruled budget table on another page read as text
- [x] Reading order: lines no longer interleaved; answer options `A.`–`E.` read across as one row
- [x] Searching `3/7` finds both fractions and highlights them on the printed page
- [x] No false scripts on a page of running prose (`MAY 2018` page 1)
- [x] Settings toggle off restores the plain recognizer output

### Packaging
- [x] App icon, assembly metadata (product, publisher, version)
- [x] `LitePDF-1.0.0-setup.exe` (55 MB): per-user install without a UAC prompt, Start menu and optional desktop
      shortcut, LitePDF offered under "Open with" for PDFs, entry in Settings > Apps
- [x] Installed, launched, opened a PDF, then uninstalled: every file, shortcut and registry key removed,
      and the uninstaller asks before deleting settings and the OCR cache
- [ ] Not code-signed, so SmartScreen warns on first run (More info > Run anyway). See T-D2.

### Known limits
- Ink the pen destroyed is gone: a question number or fraction bar scribbled over cannot be recovered.
- π and ∠ are only repaired where the recognizer's spelling cannot occur in a word (`Tt`/`1t` → π,
  `L` before point names followed by `=` → ∠). A lone `T` for π is left alone: guessing there would rewrite prose.
- Letters as exponents (`x^n`) are not marked, only digits. See `docs/BLUEPRINT.md` §5 for why.
- Radicals (`√`) are not detected.
- Recognition costs about 1.2 s a page against 0.5 s for the recognizer alone.

## Status (2026-09-16, v2 rewrite)
Core, PDFium wrapper, OCR and the WPF app were rewritten. The previous implementation didn't build, crashed at startup and had many broken features.

**Verification performed:**
- Build: 0 warnings.
- 79 automated tests: Core, PDFium end-to-end including rotated/cropped coordinate checks and annotation round-trips, OCR, scan preprocessing and layout analysis.
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
| OCR one Letter page at 300 DPI, recognizer only | ≈ 0.5–1 s | — |
| OCR one A4 scan at 400 DPI with layout analysis | ≈ 1.2 s | — |

## Next work (priority order)
1. **T-V1** Manually verify the "not yet exercised" items above; add an encrypted sample to SampleGen (needs an RC4/AES writer or a checked-in small file).
2. **T-P1** Measure startup with the ReadyToRun publish; profile startup (PDFium init, WPF theme load, DocumentKey hashing).
3. **T-P2** Pool render buffers (`ArrayPool`) to reduce LOH churn; consider rendering bitmaps straight into `WriteableBitmap`.
4. **T-F1** Tabs or multiple windows (currently one document per window; opening another replaces it after a save prompt).
5. **T-F2** Save as searchable PDF: embed OCR text as invisible text (`FPDFText_SetText`, render mode 3).
6. **T-F3** Additional OCR languages beyond Windows' set (e.g. Bengali/Hindi) via an optional Tesseract `IOcrEngine`.
   The layout pass is engine-agnostic (it works from `OcrLine`/`OcrWord` boxes), so it would carry over unchanged.
7. **T-F5** Radicals and nested fractions: `MathLayout` finds one bar at a time and has no notion of a root sign.
8. **T-F4** Form filling (`FPDFDOC_InitFormFillEnvironment`), ink/freehand annotations.
9. **T-A1** Accessibility pass with Narrator: page text exposure via UI Automation for the viewer, focus order, high contrast theme.
10. **T-D2** Code-sign the installer so SmartScreen stops warning on first run (needs a certificate);
    consider an MSIX/Store package and a winget manifest.
11. **T-D3** Microsoft Store submission. Listing art is done (`assets/store/`, generated by
    `tools/make-logos.ps1`, which also emits the MSIX tile set T-D2 would need). Still missing:
    screenshots (one required, four or more recommended, 1366x768+), the listing text, an age
    rating questionnaire and a privacy policy URL — LitePDF collects nothing, but the Store
    still wants the statement.
