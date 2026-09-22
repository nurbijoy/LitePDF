# LitePDF

A fast, private reader for PDFs and scanned images on Windows. Everything, including text recognition, runs on
your PC; nothing is uploaded.

## Download
Get the latest build from [GitHub Releases](https://github.com/nurbijoy/LitePDF/releases/latest).
- **Installer:** `LitePDF-1.1.0-setup.exe`
- **Portable:** extract `LitePDF-1.1.0-win-x64-portable.zip` and run `LitePDF.exe`.

Requires Windows 10 (19041) or later, x64. Both downloads include the .NET runtime.
The app is not code-signed, so Windows SmartScreen may warn on first run.

## Features
- **Tabs & Single Instance:**
  - Multi-document tabbed interface: open multiple PDFs in a single window without duplicating memory usage
  - Opening files from File Explorer automatically opens them as new tabs in the active window
  - Tab close buttons, unsaved changes confirmation dialog, and tab navigation
- **Reading:**
  - Smooth continuous scrolling, sharp at any zoom, fit width / fit page, two-page view, rotate
  - Full screen, dark and sepia page colors, light/dark app theme
- **Navigation:**
  - Page thumbnails and chapters (table of contents), with the current chapter highlighted
  - Clickable links, reopens where you left off
- **Text:**
  - Select and copy (drag, double-click a word, triple-click a line), copy a page as text or image
  - Search with a results list
- **Text recognition (OCR):**
  - Detects scanned pages and recognizes their text, so it can be selected, copied, searched and highlighted
  - Recognize any area of a page
  - Open or paste images (PNG, JPEG, TIFF…)
- **Annotations:**
  - Highlight (5 colors), underline, strikethrough and sticky notes, saved as standard PDF annotations
  - Annotation list with Markdown export
- **Convert to Word**
  - Creates an editable `.docx`: real paragraphs that reflow, character styling, pictures, charts and other
    drawings, lists, tables with their shading, headings, headers and footers with a live page number, links,
    highlights, and sticky notes as Word comments
  - Uses the document's own tags when it has them, which is how a tagged PDF states what its headings, lists
    and tables really are
  - Scanned pages can be recognized first, so they convert to text rather than a picture
  - Choose the page range and what to keep; nothing leaves the device
- **Other:** print, read aloud, document properties.

## Keyboard
| Keys | Action |
|---|---|
| Ctrl+T | New tab (open file) |
| Ctrl+W | Close active tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+O / Ctrl+S / Ctrl+Shift+S | Open / Save / Save as |
| Ctrl+F, F3, Shift+F3 | Search, next / previous result |
| Ctrl+C / Ctrl+A | Copy selection / select page text |
| Ctrl+H / Ctrl+U | Highlight / underline selection |
| Ctrl+wheel, Ctrl+ + / − / 0 | Zoom |
| Ctrl+1 / Ctrl+2 | Fit width / fit page |
| Ctrl+R / Ctrl+Shift+R | Rotate view |
| Ctrl+G | Go to page |
| F4, F11 | Sidebar, full screen |
| V / H / R | Select text / hand / area tool |

## Build & Publish
Requires the .NET 10 SDK on Windows 10 (19041) or later.

### Development
```powershell
dotnet build LitePDF.slnx
dotnet test tests/LitePdf.Core.Tests
dotnet run --project src/LitePdf.App
```

### Publish Release
To build distribution packages for release:
```powershell
# Standard self-contained release (installer + portable ZIP in dist/)
powershell -ExecutionPolicy Bypass -File publish.ps1

# Portable ZIP only (does not require Inno Setup)
powershell -ExecutionPolicy Bypass -File publish.ps1 -NoInstaller

# Framework-dependent build (~33 MB, requires .NET 10 Desktop Runtime on target PC)
powershell -ExecutionPolicy Bypass -File publish.ps1 -FrameworkDependent
```
The publish script generates:
- `dist/LitePDF-<version>-setup.exe`: Self-contained installer (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php))
- `dist/LitePDF-<version>-portable.zip`: Self-contained portable archive
- Output staging in `publish/`

### Publishing a New Release to GitHub
1. **Update version:** In `src/LitePdf.App/LitePdf.App.csproj`, increment `<Version>` (e.g., `1.1.0`).
2. **Build release packages:**
   ```powershell
   powershell -ExecutionPolicy Bypass -File publish.ps1
   ```
3. **Commit and tag:**
   ```powershell
   git add .
   git commit -m "Release v1.1.0"
   git tag v1.1.0
   git push origin main --tags
   ```
4. **Create release on GitHub:**
   - Go to [GitHub Releases › New release](https://github.com/nurbijoy/LitePDF/releases/new).
   - Select the new tag (e.g., `v1.1.0`) and set the title (e.g., `Lite PDF 1.1.0`).
   - Drag and drop `dist/LitePDF-<version>-setup.exe` and `dist/LitePDF-<version>-win-x64-portable.zip`.
   - Click **Publish release**.

Text recognition uses the Windows OCR languages installed on the PC. To add one: **Settings › Time & language › Language & region**, add the language and include *Optical character recognition*.

## Microsoft Store (MSIX)
To build an unsigned, self-contained x64 MSIX package for Microsoft Store submission:

```powershell
powershell -ExecutionPolicy Bypass -File publish-msix.ps1 `
  -PackageName 'NurInnovativeSolutions.LitePDF' `
  -Publisher 'CN=A16B0419-624F-494D-8378-B2363E455A29' `
  -PublisherDisplayName 'Nur Innovative Solutions'
```

Requirements and options:
- Requires `MakeAppx.exe` from the Windows SDK (auto-detected, or specify with `-MakeAppxPath`).
- Add `-ValidationOnly` to create a test package in `artifacts/msix-validation/` without uploading.
- Produces `dist/LitePDF-<version>.0-x64.msix` and a corresponding `.sha256` checksum.
- For complete Store packaging and submission instructions, see [MSIX Packaging Guide](docs/MSIX.md).

See `AGENTS.md` for contributor guidance and `docs/BLUEPRINT.md` for the architecture.
