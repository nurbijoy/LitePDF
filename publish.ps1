param(
    [string]$Runtime = "win-x64",

    # Framework-dependent: ~33 MB, but the target PC needs the .NET 10 Desktop Runtime.
    # The default carries the runtime, so a fresh Windows 10/11 PC needs nothing installed.
    [switch]$FrameworkDependent,

    # Skip building the Setup .exe and produce only the portable folder and zip.
    [switch]$NoInstaller
)

# ReadyToRun precompiles the app for faster startup.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "publish"
$dist = Join-Path $root "dist"
$selfContained = -not $FrameworkDependent.IsPresent
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root "src/LitePdf.App/LitePdf.App.csproj") `
    -c Release -r $Runtime --self-contained:$selfContained -o $out `
    /p:PublishReadyToRun=true /p:DebugType=none /p:SatelliteResourceLanguages=en
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

& (Join-Path $out "LitePDF.exe") --self-test | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Self-test failed for the published build" }

$version = (Get-Item (Join-Path $out "LitePDF.exe")).VersionInfo.ProductVersion -replace '\+.*$', ''
if (-not $version) { $version = "1.0.0" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip = Join-Path $dist "LitePDF-$version-$Runtime-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal

$flavour = if ($selfContained) { "self-contained" } else { "needs .NET 10 Desktop Runtime" }
"Portable ({0}): {1:N0} MB zipped -> {2}" -f $flavour, ((Get-Item $zip).Length / 1MB), $zip | Write-Host

if ($NoInstaller) { return }

# Inno Setup builds the Setup .exe: winget install JRSoftware.InnoSetup
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup not found, so no installer was built. Install it with: winget install JRSoftware.InnoSetup"
    return
}

& $iscc /Qp `
    "/DAppVersion=$version" `
    "/DSourceDir=$out" `
    "/DOutputDir=$dist" `
    (Join-Path $root "installer/LitePDF.iss") | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

$setup = Join-Path $dist "LitePDF-$version-setup.exe"
"Installer: {0:N0} MB -> {1}" -f ((Get-Item $setup).Length / 1MB), $setup | Write-Host
