# DOCX export design

**Current scope (2026-09-22 quality fixes):** the app converts text PDFs only. Scanned/image-only pages,
including invisible OCR layers, are rejected without creating or replacing output. OCR conversion and
whole-page image fallback are disabled. Pictures in text PDFs remain supported. The app preserves source
line endings and page boundaries while keeping editable text, lists, tables, links and comments.
The reflow pipeline described below remains available in Core; historical scan plans below are deferred.

Design for "Convert to Word" (`T-F6`, finished by `T-F7`). Status: **every phase implemented**
(2026-09-22); see `TASKS.md` for what was verified and what is still missing, and `docs/BLUEPRINT.md`
§5c/5d for the architecture as built. This file keeps the reasoning behind the design.

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

## 2. Two ways in, one model

The single biggest fidelity lever is that **a tagged PDF already contains the answer**. `FPDF_StructTree_*` and
`FPDF_StructElement_*` are exported by the shipped PDFium (155.0.8057) and expose `/P`, `/H1`–`/H6`, `/Table`,
`/TR`, `/TD`, `/L`, `/LI`, `/Figure` with alt text, plus marked-content IDs that tie each element to the page
objects that draw it (`FPDFPageObj_GetMarkedContentID`). Anything exported from Word, InDesign or a
government/accessibility pipeline is tagged.

```
tagged PDF   ──► tags ──► TagIndex ───┐  (grouping, trusted over inference)
                                     │
untagged PDF ──► glyphs ────────────┼─► ContentComposer ─► DocxDocument (IR) ─► DocxWriter ─► .docx
                                     │                                       (ZipArchive + XmlWriter)
scanned PDF  ──► OCR ──► PageText ────┘
```

One composer, one IR, one writer, so `DocxWriter` and every test are shared. The geometry carries the weight;
the tags are consulted for *grouping and order only* and styling always comes from the glyphs, because tags
say nothing true about what a run looks like.

> **As built (2026-09-22): one pipeline, not two.** The tags are read — `FPDF_StructTree_*` plus
> `FPDFPageObj_GetMarkedContentID` — but they did not get a front end of their own. `TagIndex` resolves the
> structure tree against the page's characters and hands the geometry path answers it trusts over its own
> inference: this element is a level-two heading, those two blocks are one paragraph, these nine ranges are
> a table's cells, that picture is this figure with this alt text.
>
> The second front end was dropped deliberately. Everything in §5 — columns, alignment, indents, hyphens,
> running heads, page joins — is needed whether or not a file is tagged, because tags describe grouping and
> say nothing about layout; a separate path would have had to borrow all of it and then drift from it. The
> cost of the choice is that reading order still comes from the geometry rather than from the tag order,
> which differ only where a page is set in an unusual way.

The scan path is deferred. Shared text types alone do not establish OCR conversion quality; the Word
export service neither calls OCR nor turns unsupported pages into full-page pictures.

## 3. Layering

| Where | What | Why |
|---|---|---|
| `Core/Export/PageContent.cs` | What a page yields: `TextStyle`, `StyledSpan`, `PlacedImage`, `RuleSegment`, `FilledArea`, `PageTag`, `MarkedRange`, `EmbeddedFont`, `IPageContentSource` | Core has no dependencies; all of it is plain data |
| `Core/Export/DocxModel.cs` | IR: `DocxDocument`, `DocxSection`, `DocxParagraph`, `DocxRun`, `DocxTable`, `DocxPicture`, `DocxComment` | Prefixed because WPF has a `Paragraph`, `Run`, `Table` and `Section` of its own |
| `Core/Export/ContentComposer.cs` | Geometry → IR: columns, paragraphs, headings, lists, alignment, tables, comments | Pure functions over boxes; unit-testable without a PDF |
| `Core/Export/DocumentProfile.cs` | Document-wide measurements and the running heads | Has to be global; see §5 |
| `Core/Export/TagIndex.cs` | The structure tree resolved against the page's characters | The one place that knows what a tag means; see §2 |
| `Core/Export/TableBuilder.cs` | Ruled table reconstruction | Separable, and the most likely to churn |
| `Core/Export/UnruledTableBuilder.cs` | Tables with no lines, from alignment alone | The one rule that can damage a document, so it is kept apart and opt-in |
| `Core/Export/FontMapper.cs` | PDF base font name → an installed family | Unit-testable on its own |
| `Core/Export/DocxWriter.cs` | IR → OPC package, and `IImageEncoder` | `System.IO.Compression` + `XmlWriter`, both BCL |
| `Pdfium/PdfContentReader.cs` | Page → `PageContent`: styled spans, images, drawings, rules, fills, tags, fonts | Only `Pdfium` may touch `Interop` (rule 1) |
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
images are clustered by overlapping bounds into a region, the text and image objects are switched off with
`FPDFPageObj_SetIsActive(false)`, the region is rendered at 300 DPI onto a transparent bitmap, and they are
switched back on. The result is one crisp PNG in the right place, and any text that sat *inside* the region is
still emitted as text rather than being swallowed — which is what `PageStructure.FindFigures` already does
for scans.

**As built,** three kinds of path are told apart before any of that, because they are three different things:
a hairline is a table rule and is kept as geometry; an axis-aligned filled rectangle is a panel and is kept as
a colour; everything else is artwork and is drawn. A filled rectangle counts as a background only when it is
pale, page-sized, or has the page's own text printed on top of it — a bar of a bar chart is none of those.
The rectangle test is geometric (four corners, edges along the axes) rather than a segment count, because a
triangle has four straight segments too, and a triangle with a caption across it would have been filed as a
panel behind the text and silently dropped.

Rules that fall *inside* a region are taken with it, so a chart keeps its axes. A region is skipped when it is
smaller than a mark, larger than most of the page, or covers half the page with the body text inside it: that
last one is a border drawn around the page, and rasterizing it would drop a picture of the page on top of the
document it was read from.

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
   edges are flush on every line but the last. The column of a single-measure page is its text area — the
   document's left margin (per side when the margins are mirrored) to the right edge the fuller pages reach —
   widened only where a line reaches past it. Centred means clear of both edges and balanced within a
   letter's width; lines sharing a left edge are set from the left, and a list item never counts as centred.
   Indent in twips from the column's left edge.
7. **Headings.** From tags when tagged. Otherwise: size above body size, or bold and short and spaced, ranked
   into Heading 1–3 by size cluster. **The outline is the tie-breaker** — `GetOutlineAsync` already returns
   bookmarks with a page and a Y, so a line at that Y is a heading at that bookmark's depth. That is exact
   information the app already loads, and it beats every heuristic.
8. **Lists.** A leading `•`, `‣`, `◦`, `-`, `1.`, `a)`, `(i)` plus a hanging indent shared across consecutive
   paragraphs. The marker is removed from the text and becomes `w:numPr`; left in, Word draws a second bullet.
9. **Headers and footers.** Lines in the top or bottom margin band whose text repeats across three or more
   pages — or repeats with only a changing number — become a real Word header/footer with a `PAGE` field. Left
   in the body they interrupt the text at every page break, which is the most-complained-about defect of every
   converter on the market. Each part of a head set in pieces keeps its own style, and every run of the PAGE
   field carries the number's formatting, since Word formats a refreshed field like its first run.
9a. **Contents entries.** Four or more dots (or middle dots) running from an entry's text to a page reference
   at the end of the line become a tab to a right-aligned stop with that leader. Underscores and hyphens count
   only before a bare number; anything else is a blank to fill in.
9b. **Panels.** Touching filled rectangles of one colour that hold whole lines of text, are not a table's and
   are not the page's background become a box: shading plus a border on every side, in the colour the page
   drew along that side or in the fill colour where it drew none. Border distances are the measured padding.
9c. **Code.** Lines set almost entirely in a monospaced face keep their breaks, their blank lines and their
   columns: each character goes back to the column its position gives, because indentation is drawn as a jump.
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
- **A paragraph never crosses a table's or a panel's edge.** A table replaces the paragraphs its lines are in.
- **The one-third-short rule is for unbroken text only.** A line ending a third of the measure short ends
  its paragraph whatever the next word — but only when the next line is one unbroken run (text set without
  spaces). In a narrow cell a third of the measure is less than one long word.

## 6. Tables

Phase 4, and the part most likely to need iteration against real files.

Three detectors, in the order they are trusted; the first to claim a region of the page wins. A table that
fails all three stays as paragraphs — a worse-looking but complete and editable result, which beats a
mangled `w:tbl`.

1. **Ruled tables.** Thin paths — filled hairline rectangles *or* stroked lines, which is how a great many
   PDFs draw a grid — snap into horizontal and vertical boundaries. Cells are grid rectangles; a cell
   spanning missing interior lines becomes `w:gridSpan` / `w:vMerge`. Fill from `FPDFPageObj_GetFillColor`
   carries over as `w:shd`, taken from the smallest rectangle that covers the cell: a header row is shaded
   with one wide rectangle behind three cells, while a rectangle behind the whole table is the table's own
   background and is left alone.
2. **Tagged tables.** `/Table`, `/TR`, `/TD`, `/TH`, with `/ColSpan` and `/RowSpan`. Cells are laid out the
   way a browser lays out a table — each takes the next free slot of its row, and a row-spanning cell keeps
   the slots under it occupied. Column widths come from where the cells *start*, not from how wide their text
   happens to be, or a table of short words comes out as a huddle of narrow columns in the middle of the page.
   Borders follow the PDF: a tagged table that drew no lines gets none in Word either.

A row of a table drawn without lines, which has nothing but wide gaps, is set as tab-separated parts where
its gaps line up with other lines' — even when the unruled-table guess is off. A stroked rule is half as
thick as PDFium's bounds for it, which grow it by the full stroke width on every side.

A table that carries on at the top of the next page with the same columns is joined to the one before; the
header row printed again is dropped and the first row marked to repeat. A cell whose text sits centred in a
taller row is centred (`w:vAlign`). Rasterized artwork lying on a table is dropped — the table draws its own
shading and rules.
3. **Unruled tables**, and only when the export is asked for them. Three or more consecutive lines, each cut
   into the same number of pieces by gaps several characters wide, whose columns line up on one edge down the
   whole run and never overlap, with no piece long enough to be a sentence. Two columns of prose never pass:
   they are split into reading columns long before this runs, and even side by side their words do not line up
   from one line to the next.

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

**Fonts** are referenced by name by default: the subset prefix (`ABCDEF+`) is stripped and the base-14 names
are mapped (Helvetica → Arial, Times → Times New Roman, Courier → Courier New, ZapfDingbats → Wingdings).

**Embedding is opt-in and stays that way.** `FPDFFont_GetFontData` returns the font file; only sfnt fonts
(TrueType and OpenType) are taken, because a Type 1 or a bare CFF is a part Word would refuse to open the
document over. Each goes to `word/fonts/fontN.odttf` with its first 32 bytes XORed against the 16 bytes of a
fresh GUID taken in reverse order, and that GUID goes in `w:fontKey`. It is not encryption — only enough that
the file is not an installable font — but a font stored plainly is a font Word will not load. Every embedded
font is declared `w:subsetted`: PDFium returns the base name with the subset tag already stripped, so there is
nothing left to tell a subset from a whole font, and nearly all of them are subsets. Declaring it is the safe
direction, because Word then knows the font may not hold every glyph. It is also exactly why this is never the
default: an embedded subset holds only the glyphs the PDF printed, so typing a new letter shows a box.

**Comments.** A PDF sticky note is a comment in everything but name, so it becomes one: `word/comments.xml`,
`w:commentRangeStart`/`End`, and a reference run on the paragraph the note sits against — the nearest one,
because the icon is placed beside the text rather than in it; beside a table, the cell of the row it stands
level with. Word's Save as PDF prints comments in a side pane by default, which scales the page down; that is
Word's choice, made in its options, and the note is kept rather than dropped to avoid it. A markup annotation carrying a note of its own
gets both the highlight and the comment. Left in the body a note would interrupt the text at the point it was
written, and dropped it would be content lost without a word.

## 8. Verification

Beyond the standard definition of done in `AGENTS.md`:

- **Character conservation (the acceptance test).** For each sample, the visible characters of the .docx, in
  document order, equal the PDF's reading-order text modulo whitespace and soft hyphens. This is what
  "nothing is lost" means in code, and it runs from phase 1 onward.
- **Image conservation.** Image count and pixel dimensions match; JPEG-passthrough images are byte-identical.
- **Package validity.** Every part parses; every relationship id resolves; content types cover every extension.
- **Golden XPath assertions** on `word/document.xml` from `SampleGen` PDFs: run counts, bold and size on known
  words, paragraph counts, heading levels, table shape.
- **Manual, in Word.** Convert through the app itself (More → Convert to Word… → Save), open the file in
  Word, and page through it with formatting marks on: a contents entry must be one paragraph with a tab, a
  list item one paragraph with one bullet, a cell one paragraph, a listing one block with its spaces. A PDF
  rendering of the .docx hides every one of those defects. Word opens it with no repair prompt; LibreOffice
  is still to be checked.

`SampleGen` writes the fixtures: `sample-formatted.pdf` (headings, ragged prose, a bulleted and a numbered
list, a ruled table, a plate at 200 DPI, a running head and foot with a page number), `sample-tagged.pdf` (a
structure tree with a heading, a two-item list, a three-column table that draws no lines, and a figure with
alt text — note that a reader finds a page's structure through `/ParentTree`, entry by marked-content id, not
by walking `/K` from the root, so a fixture without one comes back nearly empty), and `sample-drawings.pdf`
(a bar chart drawn with path operators, a table ruled with stroked lines and a shaded header row, a table with
no lines at all, a picture inside a form XObject, and a sticky note).

## 9. Phases

Each is independently shippable and leaves the app in a releasable state.

| | Work | State |
|---|---|---|
| **0** | Interop bindings, `PdfContentReader`, `StyledSpan`, IR types, tests | **Done** |
| **1** | `ContentComposer` paragraphs/headings/alignment, `DocxWriter`, export dialog, progress, cancel | **Done** |
| **2** | Images (JPEG passthrough + PNG), placement, vector regions | **Done** |
| **3** | Lists, hyperlinks, headers/footers, outline-driven headings, highlights and notes | **Done** |
| **4** | Ruled, then unruled, tables | **Done**; unruled is opt-in |
| **5** | Multi-column reading order, tagged PDFs, OCR path for scans, optional font embedding | **Done** |

Nothing is left outstanding as a phase. What remains is in the known limits in `TASKS.md`: reading order on a
tagged page still comes from the geometry rather than the tag order, and right-to-left and vertical text are
still untested.

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
