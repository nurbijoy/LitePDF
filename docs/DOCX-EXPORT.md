# DOCX export design

Design for "Convert to Word" (`T-F6`). Status: **phases 0–4 implemented** (2026-09-21); see `TASKS.md` for
what was verified and what is still missing, and `docs/BLUEPRINT.md` §5c/5d for the architecture as built.
This file keeps the reasoning behind the design.

## 1. Goal, and the honest ceiling

Produce a `.docx` that a person can open in Word and *work with*: real paragraphs that reflow when they type,
real character styles, real images, real tables. Not a picture of the PDF, and not a text dump.

**A PDF has no paragraphs.** It has positioned glyphs and drawing operators. Every converter — Acrobat included
— reconstructs structure by inference and is sometimes wrong. So the target is not "identical", it is:

1. **Nothing is lost.** Every visible character, every image, every figure reaches the .docx. Where structure
   cannot be inferred, content is still emitted, in reading order, with its styling. Losing a paragraph is a
   bug; misjudging that two paragraphs were one is a limitation.
2. **Nothing is invented.** No guessed heading levels that fight Word's outline, no tables built out of
   coincidentally aligned prose.
3. **It opens clean.** No repair prompt, ever. Word's "unreadable content" dialog is a hard failure.

Invariant 1 is testable and is the acceptance test for every phase (§8).

## 2. Two front ends, one model

The single biggest fidelity lever is that **a tagged PDF already contains the answer**. `FPDF_StructTree_*` and
`FPDF_StructElement_*` are exported by the shipped PDFium (155.0.8057) and expose `/P`, `/H1`–`/H6`, `/Table`,
`/TR`, `/TD`, `/L`, `/LI`, `/Figure` with alt text, plus marked-content IDs that tie each element to the page
objects that draw it (`FPDFPageObj_GetMarkedContentID`). Anything exported from Word, InDesign or a
government/accessibility pipeline is tagged.

```
tagged PDF   ──► StructReader     ─┐
                                   ├─► ExportDocument (IR) ──► DocxWriter ──► .docx
untagged PDF ──► ContentComposer  ─┤                     (ZipArchive + XmlWriter)
                                   │
scanned PDF  ──► OCR ──► PageText ─┘
```

Both front ends produce the same IR, so `DocxWriter` and every test are shared. The geometry path
(`ContentComposer`) is the fallback and carries the weight for untagged files; the structure path trusts the
tags for *grouping and order only* and still reads styling from the glyphs, because tags say nothing about
what a run looks like.

> **As built:** only the geometry path and the scan path exist. `StructReader` is not written; the exports it
> needs are present in the shipped PDFium and the IR is already shared, so it can be added without moving
> anything else. `T-F7` tracks it.

The scan path is nearly free: `OcrLayout` already returns a `PageText`, the same type PDF text produces, so
`ContentComposer` consumes it unchanged. Recognition quality is then the only difference.

## 3. Layering

| Where | What | Why |
|---|---|---|
| `Core/Export/PageContent.cs` | What a page yields: `TextStyle`, `StyledSpan`, `PlacedImage`, `RuleSegment`, `IPageContentSource` | Core has no dependencies; all of it is plain data |
| `Core/Export/DocxModel.cs` | IR: `DocxDocument`, `DocxSection`, `DocxParagraph`, `DocxRun`, `DocxTable`, `DocxPicture` | Prefixed because WPF has a `Paragraph`, `Run`, `Table` and `Section` of its own |
| `Core/Export/ContentComposer.cs` | Geometry → IR: columns, paragraphs, headings, lists, alignment, tables | Pure functions over boxes; unit-testable without a PDF |
| `Core/Export/DocumentProfile.cs` | Document-wide measurements and the running heads | Has to be global; see §5 |
| `Core/Export/TableBuilder.cs` | Ruled table reconstruction | Separable, and the most likely to churn |
| `Core/Export/FontMapper.cs` | PDF base font name → an installed family | Unit-testable on its own |
| `Core/Export/DocxWriter.cs` | IR → OPC package, and `IImageEncoder` | `System.IO.Compression` + `XmlWriter`, both BCL |
| `Pdfium/PdfContentReader.cs` | Page → `PageContent`: styled spans, placed images, rules | Only `Pdfium` may touch `Interop` (rule 1) |
| `App/Documents/DocxExport.cs` | Drives the conversion, and `WpfImageEncoder` | `PngBitmapEncoder` is the only PNG encoder in the box |
| `App/Views/ExportDocxDialog` + `MainWindow.Document.cs` | Range, options, progress, cancel | Mirrors the print dialog and the OCR progress card |

No new NuGet package. A `.docx` is a ZIP of XML; the OpenXML SDK would add several megabytes for a writer we
need roughly 600 lines of, against rule 7.

## 4. Reading the page (`PdfContentReader`)

Runs on the PDFium worker at `RenderPriority.Background`, one page at a time, cancellable. New `LibraryImport`
bindings are needed — all verified present in the shipped `pdfium.dll`:

- **Style per character:** `FPDFText_GetFontSize`, `FPDFText_GetFontInfo` (name + descriptor flags),
  `FPDFText_GetFontWeight`, `FPDFText_GetFillColor`, `FPDFText_GetCharAngle`, `FPDFText_GetTextObject` →
  `FPDFTextObj_GetFont` → `FPDFFont_GetItalicAngle` / `GetFamilyName` / `GetIsEmbedded` / `GetFontData`.
- **Render mode:** `FPDFTextObj_GetTextRenderMode` — mode 3 is invisible text (the OCR layer under a scan).
  It is used only when the page has no visible text of its own; otherwise a searchable scan would emit every
  word twice. When it is used, the picture it sits under is skipped for the same reason.
- **Images:** `FPDFImageObj_GetImageFilter`, `GetImageDataDecoded`, `GetRenderedBitmap`,
  `GetImagePixelSize`, `FPDFPageObj_GetMatrix`, `FPDFPageObj_GetBounds`.
- **Vectors:** `FPDFPath_CountSegments` / `GetPathSegment` / `GetDrawMode`, `FPDFPathSegment_GetPoint` /
  `GetType`, `FPDFPageObj_GetStrokeWidth`, `FPDFPageObj_SetIsActive`.
- **Tags:** `FPDF_StructTree_GetForPage` and the `FPDF_StructElement_*` family.

Characters with equal style *and* adjacent boxes collapse into a `StyledSpan`. Geometry stays normalized
(rule 2); only the writer converts to Word units.

### Why the visible size is measured, not read
`FPDFText_GetFontSize` returns the size from the text state and ignores the text matrix, so text scaled by the
matrix reports the wrong size. The visible size comes from the loose char box height measured against the page
height in points, with `FPDFText_GetFontSize` as a sanity check. Mis-sized body text is the most obvious defect
this feature could ship with.

### Why images take two paths
When the filter is `DCTDecode` and the object carries no soft mask, `GetImageDataDecoded` returns the original
JPEG bytes: they go into `word/media/` untouched — lossless, no re-encode, smallest file. Everything else
(masked, clipped, CCITT, JBIG2, indexed) goes through `GetRenderedBitmap`, which composites the mask and clip
as PDFium would draw them, and is then encoded as PNG. Re-encoding a photo to PNG would balloon the file;
re-encoding it to JPEG would lose a generation for nothing.

### Why vector art is rasterized
Word's DrawingML can express beziers, but translating PDF's graphics state — clips, soft masks, blend modes,
shadings, patterns — is a project the size of the rest of this feature. Instead, non-text objects that are not
images are clustered by overlapping bounds into a `VectorRegion`, text objects are switched off with
`FPDFPageObj_SetIsActive(false)`, the region is rendered at 300 DPI with `FPDF_RenderPageBitmapWithMatrix`, and
the text objects are switched back on. The result is one crisp PNG in the right place, and any text that sat
*inside* the region is still emitted as text rather than being swallowed — which is what
`PageStructure.FindFigures` already does for scans.

## 5. Building the document (`ContentComposer`)

Input is a `PageText` (its `Lines` are already visual lines that survive rotation) plus the page's spans,
images, rules and vector regions.

1. **Columns.** Vertical projection of the *narrow* line boxes; a gap wider than 1.6 glyph heights that no
   line crosses splits a column, and every band has to hold a column's worth of lines. Columns are looked
   for before spanning lines are: the other way round, every line of an ordinary single-column page looks
   like a spanning line, because there the line and the text area are the same width.
2. **Reading order.** Column by column, top to bottom — the rule `OcrLayout` already uses, for the same reason.
3. **Body size.** The modal visible size across the whole document, not per page: a chapter opening would
   otherwise set the baseline for headings everywhere.
4. **Paragraphs.** Lines join unless the vertical gap exceeds the median gap of the block by a clear margin;
   or the first line is indented differently from its neighbours; or the dominant style changes for a whole
   line; or the line opens with a list marker; or the *previous* line stopped short of the measure **and the
   first word of this line would have fitted on it**. That last test replaced a fixed fraction of the
   measure, which broke paragraphs constantly: ragged-right prose routinely stops 20 pt short because the
   next word is 30 pt wide, and that has ended nothing.
5. **Hyphens.** A line-final `-` followed by a lower-case continuation is removed and the words joined. A
   line-final `-` before a capital or a digit, or on a line that already ends short, is a real hyphen and kept.
6. **Alignment and indent.** Left/right/centre from the line edges against the column; justified when both
   edges are flush on every line but the last. Indent in twips from the column's left edge.
7. **Headings.** From tags when tagged. Otherwise: size above body size, or bold and short and spaced, ranked
   into Heading 1–3 by size cluster. **The outline is the tie-breaker** — `GetOutlineAsync` already returns
   bookmarks with a page and a Y, so a line at that Y is a heading at that bookmark's depth. That is exact
   information the app already loads, and it beats every heuristic.
8. **Lists.** A leading `•`, `‣`, `◦`, `-`, `1.`, `a)`, `(i)` plus a hanging indent shared across consecutive
   paragraphs. The marker is removed from the text and becomes `w:numPr`; left in, Word draws a second bullet.
9. **Headers and footers.** Lines in the top or bottom margin band whose text repeats across three or more
   pages — or repeats with only a changing number — become a real Word header/footer with a `PAGE` field. Left
   in the body they interrupt the text at every page break, which is the most-complained-about defect of every
   converter on the market.
10. **Page setup.** Size, orientation and margins (the content bounding box across the section's pages) into
    `w:sectPr`. A new section starts where size or orientation changes.
11. **Links and annotations.** `GetLinksAsync` already gives rects and URIs → `w:hyperlink`. Highlights become
    Word highlighting; sticky notes become Word comments. Both are nearly free, and both are content that
    would otherwise be dropped silently.

### Why these rules and not others
- **Nothing is dropped when a rule does not fire.** Every unclaimed line still becomes a paragraph. The
  fallback is always "a correctly styled paragraph in the right place".
- **The short-line rule beats the gap rule.** Line spacing varies with font size across a document, but a line
  that stops 30% short of the measure has ended a paragraph in every language that sets flush left.
- **Tags are trusted for grouping, never for style.** Tagged PDFs routinely mark a run as `/P` while drawing it
  bold at 18 pt. Structure from the tags, appearance from the glyphs.
- **Body size is global.** Measured per page, every page's largest line becomes a heading.

## 6. Tables

Phase 4, and the part most likely to need iteration against real files.

- **Ruled tables.** Thin path rectangles (`PageStructure.Rules` already finds these on scans; the vector path
  reader gives them exactly for PDF text) snap into a grid of horizontal and vertical lines. Cells are grid
  rectangles; a cell spanning missing interior lines becomes `w:gridSpan` / `w:vMerge`. Borders, and fill from
  `FPDFPageObj_GetFillColor`, carry over.
- **Unruled tables.** Three or more consecutive lines whose fragments align into the same two or more column
  bands with consistent gaps. Deliberately conservative: two columns of prose in a newsletter must not become
  a table.
- A table that fails both detectors stays as paragraphs — a worse-looking but complete and editable result,
  which beats a mangled `w:tbl`.

## 7. Writing the package (`DocxWriter`)

```
[Content_Types].xml      _rels/.rels              docProps/core.xml   docProps/app.xml
word/document.xml        word/_rels/document.xml.rels
word/styles.xml          word/numbering.xml       word/fontTable.xml
word/header1.xml         word/footer1.xml         word/media/image1.jpeg …
```

`ZipArchive` with `CompressionLevel.Optimal`, entry names with forward slashes, `[Content_Types].xml` written
first. `XmlWriter` throughout — never string concatenation, because one unescaped `&` out of a PDF is a repair
prompt.

**Units, exactly:** 1 pt = 20 twips (indent, spacing); `w:sz` is in **half**-points; 1 pt = 12700 EMU and
1 inch = 914400 EMU (image extents); colours are `RRGGBB` hex. Each has a different scale, and getting one
wrong is silent and ugly.

**Fonts** are referenced by name, not embedded, in phase 1: the subset prefix (`ABCDEF+`) is stripped and the
base-14 names are mapped (Helvetica → Arial, Times → Times New Roman, Courier → Courier New, ZapfDingbats →
Wingdings). Embedding is possible later — `FPDFFont_GetFontData` returns the font file, and Word's
`w:embedRegular` wants it obfuscated by XOR against the GUID in the part name — but an embedded *subset* holds
only the glyphs the PDF used, so typing a new letter shows a box. Phase 5, opt-in, never the default.

## 8. Verification

Beyond the standard definition of done in `AGENTS.md`:

- **Character conservation (the acceptance test).** For each sample, the visible characters of the .docx, in
  document order, equal the PDF's reading-order text modulo whitespace and soft hyphens. This is what
  "nothing is lost" means in code, and it runs from phase 1 onward.
- **Image conservation.** Image count and pixel dimensions match; JPEG-passthrough images are byte-identical.
- **Package validity.** Every part parses; every relationship id resolves; content types cover every extension.
- **Golden XPath assertions** on `word/document.xml` from `SampleGen` PDFs: run counts, bold and size on known
  words, paragraph counts, heading levels, table shape.
- **Manual.** Open each output in Word *and* LibreOffice with no repair prompt; edit a paragraph and confirm
  it reflows; compare against the PDF side by side.

`SampleGen` needs new fixtures: a styled multi-heading document, a ruled table, a bulleted list, a
header/footer with page numbers, a two-column page, an embedded photo, and a tagged PDF.

## 9. Phases

Each is independently shippable and leaves the app in a releasable state.

| | Work | State |
|---|---|---|
| **0** | Interop bindings, `PdfContentReader`, `StyledSpan`, IR types, tests | **Done** |
| **1** | `ContentComposer` paragraphs/headings/alignment, `DocxWriter`, export dialog, progress, cancel | **Done** |
| **2** | Images (JPEG passthrough + PNG), placement, vector regions | **Done** except vector regions |
| **3** | Lists, hyperlinks, headers/footers, outline-driven headings, highlights and notes | **Done** |
| **4** | Ruled, then unruled, tables | Ruled **done**; unruled deliberately not attempted |
| **5** | Multi-column reading order, tagged-PDF front end, OCR path for scans, optional font embedding | Columns and the OCR path **done**; tags and font embedding not |

Two things from phase 2 and 5 were moved out rather than built, and both for the same reason given above:
vector drawings need the figure-region rasterization to be worth doing properly, and the tagged-PDF front
end is a second code path that earns its keep only once the geometry path has been measured against real
files. `T-F7` in `TASKS.md` tracks them.

## 10. Open risks

- **Word's tolerance is the spec.** ECMA-376 is not what Word enforces; a package can be valid and still prompt
  for repair. Mitigated by testing every phase in real Word, not only by XML assertions.
- **Unruled table detection is the one rule that can actively damage a document**, by turning prose into a
  grid. It ships last, conservative, and behind a toggle if the samples disagree.
- **Per-character interop cost.** Six native calls per character on a dense page is far more traffic than
  rendering. Measured in phase 0; if it is slow, style is read per *text object* via `FPDFText_GetTextObject`
  and attributed to that object's character range instead.
- **RTL and vertical text** are not addressed. `PageText` handles them for selection; whether the composer's
  short-line and indent rules survive them is unknown and untested.
