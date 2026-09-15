param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $false
)

$ErrorActionPreference = "Stop"

Write-Host "Publishing LitePDF $Configuration $Runtime self-contained:$SelfContained"

$outDir = Join-Path $PSScriptRoot "publish"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null

dotnet publish src/LitePdf.App/LitePdf.App.csproj -c $Configuration -r $Runtime --self-contained:$SelfContained -o $outDir /p:PublishReadyToRun=true /p:PublishSingleFile=false

# Copy pdfium.dll from NuGet cache if not present
$pdfium = Get-ChildItem -Path $env:USERPROFILE\.nuget\packages\bblanchon.pdfium.win32 -Recurse -Filter "pdfium.dll" | Where-Object { $_.FullName -like "*$Runtime*" } | Select-Object -First 1
if ($pdfium) {
    Copy-Item $pdfium.FullName $outDir -Force
    Write-Host "Copied pdfium.dll from $($pdfium.FullName)"
}

$zip = Join-Path $PSScriptRoot "LitePDF-portable-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$outDir\*" -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Created $zip"
