# Microsoft Store art

Generated from the app icon by `tools/make-logos.ps1` — edit the geometry in that script and
re-run it, don't retouch these PNGs:

```
powershell -ExecutionPolicy Bypass -File tools\make-logos.ps1
```

## What to upload, in Partner Center → Store listings

| File | Slot | |
|---|---|---|
| `AppTileIcon-300x300.png` | 1:1 App tile icon (300 × 300) | strongly recommended; the Store shows this in search results and the listing header, and prefers it over the icon inside the package |
| `BoxArt-1080x1080.png` | 1:1 box art | **required** for an MSI/EXE submission. `BoxArt-2160x2160.png` is the same image at the larger size the docs also accept |
| `PosterArt-720x1080.png` | 2:3 poster art | recommended |
| `HeroArt-1920x1080.png` | 16:9 super hero art | optional; must carry no text, which is why it is art only |

`LitePDF-1024.png` fills no Store slot — it is the plain icon for a README, a web page or
regenerating `src/LitePdf.App/Assets/LitePDF.ico`.

Still needed by hand: **at least one screenshot** (.png, 1366 × 768 or larger, up to 10;
four or more recommended). Take those from the running app — `samples/generated/` has
documents worth showing. Keep the important parts in the top two-thirds; the Store may lay
text over the bottom third.

## msix/

Only needed if LitePDF is ever packaged as MSIX instead of submitted as the Inno Setup
installer (TASKS.md, T-D2). Drop the folder's contents into the package's `Assets\` and point
`Package.appxmanifest` at them. Tiles hold the icon at 66% of the tile on transparency, so set
a `BackgroundColor` in the manifest — `#0F6CBD` matches the icon.

## Design notes

Colours come from `LitePDF.ico`: plate `#0F6CBD`, page white, folded corner `#C6DBF0`, text
lines `#7DA0C8`. Listing icons are full-bleed, since the plate already carries about 5%
padding. The promotional images drop the plate and put the page on a blue gradient
(`#177CD4` → `#0A5292`), placed above centre: a 44 px icon floating in the middle of a
1080 px square reads as a mistake, and the lower third has to stay clear for Store overlays.
