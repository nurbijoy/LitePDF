param(
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

# Portable build: framework-dependent (needs the .NET 10 Desktop Runtime) unless -SelfContained.
# ReadyToRun precompiles the app for faster startup.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "publish"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root "src/LitePdf.App/LitePdf.App.csproj") `
    -c Release -r $Runtime --self-contained:$($SelfContained.IsPresent) -o $out `
    /p:PublishReadyToRun=true /p:DebugType=none /p:SatelliteResourceLanguages=en
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

& (Join-Path $out "LitePDF.exe") --self-test | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Self-test failed for the published build" }

$zip = Join-Path $root "LitePDF-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Published to $out ($size MB zipped: $zip)"
