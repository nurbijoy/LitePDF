# TASKS

## Word conversion quality and text-only scope (2026-09-22)
- Removed OCR and whole-page raster fallback from the app's Word export. Image documents cannot use the
  command; image-only PDF pages and invisible OCR layers are rejected before output is created/replaced.
  Mixed documents can be exported by selecting their text pages. Short visible text remains valid.
- Keep source line endings and page boundaries in the app's editable Word output. Explicit paragraph
  spacing avoids inherited gaps; hard line breaks are not justified. Mixed paper sizes no longer add an
  unnecessary blank paragraph at the section boundary.
- Fix same-baseline two-column prose, literal `[Figure]` text loss, compound-word hyphens, and paragraph
  joins across skipped pages. Preserve list labels, punctuation, starting numbers and restarts; consecutive
  compatible numbered items share a Word numbering instance.
- Header detection now checks every selected page, only removes the header/footer actually represented
  in Word, and leaves changing chapter titles and extra repeated lines in the body.
- Large illustrations on text PDFs are no longer mistaken for scan backgrounds. Cancellation is checked
  again before the temporary output replaces an existing file.

Verification and remaining visual checks are recorded after the final test run below.

## Convert to Word (.docx), the rest of it (2026-09-22, T-F7)
Everything the first pass left on the floor: vector artwork, pictures inside form XObjects, the colour behind
a table cell, tables that draw no lines, tagged PDFs, sticky notes, and optional font embedding.

- **Vector artwork (`PdfContentReader`).** Paths are sorted into three kinds — a hairline is a table rule, an
  axis-aligned filled rectangle is a panel, everything else is artwork — and the artwork is clustered into
  regions and rasterized at 300 DPI onto a transparent bitmap with the text and image objects switched off
  (`FPDFPageObj_SetIsActive`), so a chart arrives as one PNG and its labels stay editable text. The rules
  inside a region go with it, so a chart keeps its axes. A region that is pale, page-sized, or has the body
  text inside it is a background and is left alone.
- **Form XObjects.** Objects are walked with a matrix stack, because a nested object's bounds are in the
  form's space. Pictures placed through a form are now found and placed correctly; paths inside one are found
  too, so a table drawn inside a form is rebuilt like any other.
- **Rules drawn as strokes.** A hairline box of no width used to be discarded as empty, which lost every
  table that draws its grid with `m`/`l`/`S` rather than filled rectangles. They are now kept.
- **Cell shading.** The smallest filled rectangle covering a cell becomes `w:shd`, so a shaded header row
  (one wide rectangle behind three cells) carries over while a panel behind the whole table does not.
- **Tagged PDFs (`TagIndex`).** `FPDF_StructTree_*` and `FPDFPageObj_GetMarkedContentID` give the structure
  tree and tie it to the characters. Heading levels, list items and their nesting, captions, figure alt text
  and tables come from the tags, and two blocks the geometry split that are one tagged element become one
  paragraph again. Appearance is never taken from the tags. No second front end: see `docs/DOCX-EXPORT.md`
  §2 for why.
- **Tagged tables.** `/Table`, `/TR`, `/TD`, `/TH` with `/ColSpan` and `/RowSpan`, laid out the way a browser
  lays out a table, with column widths from where the cells start and borders only where the PDF drew them.
- **Tables with no lines (`UnruledTableBuilder`, opt-in).** Three or more consecutive lines cut into the same
  number of pieces by gaps several characters wide, aligned on one edge down the whole run, no piece long
  enough to be a sentence. Off by default, because a false positive turns readable prose into a mangled grid.
- **Sticky notes become Word comments.** `word/comments.xml`, a comment range on the nearest paragraph, and
  the author from the annotation's `/T`. A highlight that carries a note of its own gets both.
- **Font embedding (opt-in).** `FPDFFont_GetFontData` for sfnt fonts only, written to `word/fonts/*.odttf`
  obfuscated against the GUID in `w:fontKey`, with `word/fontTable.xml` naming each family and style. Every
  font is declared `w:subsetted`, because PDFium returns the base name with the subset tag already stripped
  and nearly all embedded PDF fonts are subsets.
- **App.** Three more choices in the dialog under "Rebuilding the layout" (use the document's tags, rebuild
  tables with no lines, embed fonts) and a "Drawings" checkbox beside "Pictures"; the dialog body scrolls, so
  the dialog is capped to the work area and its body scrolls, so the buttons cannot end up off the screen.
  The costly reads (drawings, tags and fonts) are only done when the options ask for them, and pages that
  draw the same picture share one copy of it while the document is being read.

**Verification performed:**
- Build: 0 errors, 0 warnings. 209 automated tests pass (44 new). `--self-test` passes.
- Two new fixtures from `tools/LitePdf.SampleGen`: `sample-tagged.pdf` (structure tree, tagged list, tagged
  table that draws no lines, figure with alt text) and `sample-drawings.pdf` (bar chart drawn with path
  operators, table ruled with stroked lines and a shaded header, table with no lines, picture inside a form
  XObject, sticky note). Character conservation is asserted end to end on both.
- That the text really is switched off while a drawing is rasterized is asserted on pixels: a pale triangle
  with black type across it rasterizes to fewer than 200 dark pixels, and the words are still in the .docx.
- Exercised by hand in the running app, both samples, and the .docx read back each time:
  `sample-drawings.pdf` with "Also rebuild tables that draw no lines" ticked gives both tables, the shaded
  header row, the rasterized chart, the form-XObject picture and the reviewer's comment;
  `sample-tagged.pdf` with the defaults gives the tagged table, the merged paragraph, both list items and
  the figure's alt text. Clicking through it is also what caught the dialog growing taller than the screen,
  which put the Convert button below the bottom of the display; the window is now capped to the work area.
- 1,000-page document: 6.6 s in Debug, 3.9 s in Release (2.9 s of it reading pages).

### Known limits
- **Not opened in Word or LibreOffice on this machine** — neither is installed. Every part is asserted to
  parse, every relationship to resolve and every extension to have a content type, but "Word opens it without
  a repair prompt" has not been checked by hand for comments or embedded fonts.
- **Reading order on a tagged page still comes from the geometry**, not from the tag order. They differ only
  where a page is set unusually.
- **Vector artwork is a picture, not shapes.** It cannot be edited in Word, only moved and resized.
- **A drawing and the text over it both reach the .docx**, so a chart with labels inside it has those labels
  twice: once in the picture's own artwork region if they were drawn as paths, once as text.

## Convert to Word (.docx) (2026-09-21, T-F6)
Editable reflow conversion: a PDF becomes a Word document with real paragraphs, character styling, images,
lists, tables, headers and footers. Design and reasoning in `docs/DOCX-EXPORT.md`; architecture summary in
`docs/BLUEPRINT.md` §5c/5d. No new NuGet package: a .docx is a ZIP of XML, so `ZipArchive` + `XmlWriter`
is the whole writer.

- **Reading (`PdfContentReader`, `LitePdf.Pdfium`):** per-character font family, size, weight, slope and
  colour, read once per text object. Images lifted out as their original JPEG bytes where the stream is a
  plain `DCTDecode`, otherwise rendered — and re-rendered from the page at the stored resolution when
  PDFium's placed-size render would lose detail. Thin path rectangles collected as table rules. Invisible
  (render mode 3) OCR text used only where a page has no visible text, and then its picture is skipped.
- **Rebuilding (`ContentComposer`, `DocumentProfile`, `TableBuilder`, `LitePdf.Core`):** columns and reading
  order, paragraphs, alignment and indent, headings (outline-driven where the PDF has one), bulleted and
  numbered lists, ruled tables with column spans and vertical merges, running heads lifted into real Word
  headers and footers with a live `PAGE` field, hyperlinks, highlights and paragraph joins across page breaks.
- **Writing (`DocxWriter`):** OPC package with styles scaled to the measured body size, numbering, media,
  headers/footers and per-page-size sections. Images are deduplicated by content hash.
- **App:** "Convert to Word…" in the More menu, a dialog for page range and what to keep, progress with
  cancel (the OCR progress card is now a general task card), and an optional OCR pass for scanned pages.
  Saving goes through a temp file in the target folder and `File.Replace`, so a failure never truncates an
  existing document.

**Verification performed:**
- Build: 0 errors, 0 warnings. 165 automated tests pass (86 new). `--self-test` passes, and it now
  instantiates the export dialog in both themes.
- Character conservation is asserted end to end: every visible character of the PDF reaches the .docx in
  order, for the plain sample, a rotated page, a 200-page document and the new formatted sample.
- Package validity is asserted on every written document: every part parses, every relationship id resolves
  to a part that exists, and every extension has a content type.
- `tools/LitePdf.SampleGen` now also writes `samples/generated/sample-formatted.pdf` — title, two heading
  levels, ragged prose, a bulleted and a numbered list, a ruled three-column table, a 200 DPI plate with a
  caption, and a running head and foot with a page number. Converted through the real pipeline: 5 headings,
  6 list items, 1 table, 1 picture, correct header/footer, 72 pt margins recovered.
- The WPF PNG encoder was checked separately for colour and alpha round-trip.
- 1000-page document converts in about 9.5 s (6 s of it reading pages).

### Known limits
- **Right-to-left and vertical text are untested.** Selection handles them; whether the composer's
  short-line and indent rules survive them is unknown.
- Tables without lines, vector drawings, form XObjects, cell shading, tagged PDFs and font embedding were all
  limits of this pass and were finished in `T-F7` above.

## Multi-tab document support and single-instance IPC (2026-09-20, T-F1)
- **Single-instance process management:** Added `SingleInstance` using session-scoped mutex (`Local\LitePDF-SingleInstance-{User}`) and asynchronous named pipe IPC (`LitePDF-IPC-{User}`). When opening additional documents from Explorer or command line, arguments are sent to the running instance and the new process exits with code 0.
- **Sidebar document tabs (Zero vertical space overhead):** Moved document tabs into the sidebar (`SidebarPanel.Documents`), eliminating the top tab bar row so the PDF viewer retains 100% of the window's vertical height.
  - Rail button for `Documents` (`&#xE8A5;`, `Ctrl+T`) opens the tab list.
  - Shows all open document tabs with document icons, filename with ellipsis, dirty dot indicator (`●`), and close button (`×`).
  - Sidebar header includes a `+` (New tab) button when the Documents panel is active.
- **Sidebar tab toggle on click:** Clicking an active rail button collapses/closes the sidebar; clicking an inactive rail button opens/switches to that panel; rail buttons cleanly uncheck when the sidebar is collapsed.
- **Tab management:**
  - Individual tab close via `×` button, middle-click, context menu, or `Ctrl+W`. Prompts to save if modified.
  - Tab switching (`Ctrl+Tab`, `Ctrl+Shift+Tab`, or click) preserves view state (page, offset, zoom, rotation, layout mode), thumbnails, outline, search queries, hits, and annotations.
  - Context menu: Close tab, Close other tabs, Close all tabs.
- **Close confirmation:** Closing the window with multiple tabs open prompts with a warning dialog (*"Close all tabs? You have {N} open tabs. Do you want to close all tabs?"*), checks all dirty tabs for unsaved changes, saves reading positions for all documents, and closes cleanly.
- **Verification:** Solution build passed with 0 errors and 0 warnings; all 79 automated tests passed; Debug application self-test passed in both Light and Dark themes. Tested IPC and multi-tab opening with generated sample PDFs.

## MSIX packaging preparation (2026-09-16)
- Added `publish-msix.ps1` and `docs/MSIX.md`: fresh self-contained x64 publish, self-test,
  Store identity parameters, package artwork, PDF association, MakeAppx validation and SHA-256 output.
- Built a validation-only package successfully with Microsoft MakeAppx 10.0.28000.2705;
  staged application self-test passed. Test identity is explicitly labelled and kept outside `dist/`.
- Solution build passed with 0 errors and 0 warnings; all 79 automated tests and Debug self-test passed.
- Built `dist/LitePDF-1.0.0.0-x64.msix` using the supplied identity for Store listing `9MXVJV3SMMKB`.
  MakeAppx validation and staged self-test passed; checked final archive identity, runtime payload and checksum.
- Still required: installed-package checks for launch, PDF activation, OCR and save, then Store certification.

## GitHub release preparation (2026-09-16, v1.0.0)
- Release assets: self-contained Windows x64 installer and portable ZIP, with SHA-256 checksums.
- README now links to GitHub Releases and describes the current packaging and runtime requirements.
- Release verification: solution build passed with 0 warnings and 0 errors; all 79 tests passed;
  Debug application self-test passed. `publish.ps1` also runs the published application self-test before packaging.
- Existing UI verification and known limits are recorded below; the release does not change application code.
- Repository visibility remains private, so release downloads require repository access.

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
- [x] Printing: custom Fluent print dialog with printer selection, page range, copies, duplexing, orientation, color mode, and live preview; spooling via XpsDocumentWriter on UI thread.
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
1. **T-V1** Open a converted .docx in Word and in LibreOffice on a machine that has them, including one with
   comments and one with embedded fonts; add an encrypted sample to SampleGen (needs an RC4/AES writer or a
   checked-in small file).
2. **T-P1** Measure startup with the ReadyToRun publish; profile startup (PDFium init, WPF theme load, DocumentKey hashing).
3. **T-P2** Pool render buffers (`ArrayPool`) to reduce LOH churn; consider rendering bitmaps straight into `WriteableBitmap`.
4. **T-F2** Save as searchable PDF: embed OCR text as invisible text (`FPDFText_SetText`, render mode 3).
5. **T-F3** Additional OCR languages beyond Windows' set (e.g. Bengali/Hindi) via an optional Tesseract `IOcrEngine`.
   The layout pass is engine-agnostic (it works from `OcrLine`/`OcrWord` boxes), so it would carry over unchanged.
6. **T-F5** Radicals and nested fractions: `MathLayout` finds one bar at a time and has no notion of a root sign.
7. **T-F4** Form filling (`FPDFDOC_InitFormFillEnvironment`), ink/freehand annotations.
8. **T-A1** Accessibility pass with Narrator: page text exposure via UI Automation for the viewer, focus order, high contrast theme.
9. **T-D2** Code-sign the installer so SmartScreen stops warning on first run (needs a certificate);
    consider an MSIX/Store package and a winget manifest.
10. **T-D3** Microsoft Store submission. Listing art is done (`assets/store/`, generated by
    `tools/make-logos.ps1`, which also emits the MSIX tile set T-D2 would need). Still missing:
    screenshots (one required, four or more recommended, 1366x768+), the listing text, an age
    rating questionnaire and a privacy policy URL — LitePDF collects nothing, but the Store
    still wants the statement.
