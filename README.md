# LitePDF

A fast, private reader for PDFs and scanned images on Windows. Everything, including text recognition, runs on
your PC; nothing is uploaded.

## Download
Get the latest build from [GitHub Releases](https://github.com/nurbijoy/LitePDF/releases/latest).
- **Installer:** `LitePDF-1.0.0-setup.exe`
- **Portable:** extract `LitePDF-1.0.0-win-x64-portable.zip` and run `LitePDF.exe`.

Requires Windows 10 (19041) or later, x64. Both downloads include the .NET runtime.
The app is not code-signed, so Windows SmartScreen may warn on first run.

## Features
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
- **Other:** print, read aloud, document properties.

## Keyboard
| Keys | Action |
|---|---|
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

## Build
Requires the .NET 10 SDK on Windows 10 (19041) or later.
```
dotnet build LitePDF.slnx
dotnet test tests/LitePdf.Core.Tests
dotnet run --project src/LitePdf.App
powershell -ExecutionPolicy Bypass -File publish.ps1
```
The last command produces a self-contained portable build in `publish\`, plus a portable ZIP and an installer
in `dist\`. Building the installer requires Inno Setup 6. Use `-NoInstaller` for the portable ZIP only,
or `-FrameworkDependent` for a smaller build that requires the .NET 10 Desktop Runtime on the target PC.

Text recognition uses the Windows OCR languages installed on the PC. To add one: **Settings › Time & language ›
Language & region**, add the language and include *Optical character recognition*.

## Microsoft Store (MSIX)
`publish-msix.ps1` builds an unsigned, self-contained x64 package for a matching MSIX listing in Partner Center.
It requires the Windows SDK's `MakeAppx.exe` and the exact Store package identity. See
[MSIX packaging](docs/MSIX.md) for the command and submission steps.

See `AGENTS.md` for contributor guidance and `docs/BLUEPRINT.md` for the architecture.
