# Microsoft Store packaging

MSIX submissions upload the package to Microsoft, which provides Store hosting and signing.
The existing EXE/MSI submission's installer URL field does not accept an MSIX upload.
Use Partner Center's **Apps and games > New product > MSIX or PWA app** workflow.
Keep the current listing until the Store name reservation has been resolved; do not delete it just to free a name.

## Required identity

From the MSIX listing's **Product management > Product identity**, copy:
- `Package/Identity/Name`
- `Package/Identity/Publisher` (the entire value, including `CN=`)
- `Package/Properties/PublisherDisplayName`

The reserved display name must match `Product` in `src/LitePdf.App/LitePdf.App.csproj` (currently `Lite PDF`).
Do not invent Store identity values. A package with a validation identity is not upload-ready.

## Build

Install Windows SDK packaging tools, or extract Microsoft's `Microsoft.Windows.SDK.BuildTools`
NuGet package as build tooling only (it is not an application dependency).
Pass the x64 `makeappx.exe` path if the SDK is not installed in its standard location.

```powershell
powershell -ExecutionPolicy Bypass -File publish-msix.ps1 `
  -PackageName 'NurInnovativeSolutions.LitePDF' `
  -Publisher 'CN=A16B0419-624F-494D-8378-B2363E455A29' `
  -PublisherDisplayName 'Nur Innovative Solutions' `
  -MakeAppxPath 'C:\path\to\x64\makeappx.exe'
```

These identity values are from the Lite PDF Store listing `9MXVJV3SMMKB` supplied on 2026-09-16.

The script publishes a fresh self-contained ReadyToRun build, runs its self-test, stages the package artwork,
and runs MakeAppx with validation enabled. It declares a full-trust desktop application and a PDF file association.
The product version becomes a four-part MSIX version with a final zero, as required for Store submissions.
Output: `dist/LitePDF-1.0.0.0-x64.msix` and its `.sha256` checksum file.

Upload the unsigned MSIX on the matching listing's Packages page. It is not a directly installable signed download.
The Inno Setup installer and portable ZIP remain the separate direct-download formats.

## Verification status

A validation-only package has been successfully built with MakeAppx 10.0.28000.2705 and passed its manifest validation.
The final `dist/LitePDF-1.0.0.0-x64.msix` was then built with the supplied Store identity.
The staged application passed `--self-test`; the final archive's identity, runtime payload and checksum were checked.
Installation, launch, PDF activation, OCR and saving under the installed package identity still require verification;
MakeAppx validation alone does not establish Store certification readiness.

`-ValidationOnly` writes to `artifacts/msix-validation/` with `VALIDATION-ONLY` in the filename.
Never submit that test package to the Store. Staging directories under `artifacts/` are retained for inspection.

References: [Microsoft Store submission](https://learn.microsoft.com/en-us/windows/apps/publish/get-started),
[manual MSIX packaging](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion).
