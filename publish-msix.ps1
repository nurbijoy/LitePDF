<#
.SYNOPSIS
Build an unsigned, self-contained x64 MSIX for Microsoft Store submission.
.DESCRIPTION
Copy PackageName, Publisher and PublisherDisplayName exactly from Partner Center's
Product identity page for an MSIX listing. Microsoft signs the submitted package.
Requires MakeAppx.exe from the Windows SDK (or Microsoft's SDK BuildTools package).
ValidationOnly writes a clearly labelled test package outside dist; do not upload it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PackageName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Publisher,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PublisherDisplayName,
    [string]$MakeAppxPath,
    [switch]$ValidationOnly
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $MakeAppxPath) {
    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $MakeAppxPath = Get-ChildItem "$sdkBin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $MakeAppxPath) {
        $MakeAppxPath = Get-ChildItem (Join-Path $root 'artifacts\msix-tools\sdk\bin\*\x64\makeappx.exe') -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $MakeAppxPath -or -not (Test-Path -LiteralPath $MakeAppxPath)) {
    throw 'Install the Windows SDK or pass -MakeAppxPath pointing to its x64 MakeAppx.exe.'
}
$MakeAppxPath = (Resolve-Path -LiteralPath $MakeAppxPath).Path
[xml]$project = Get-Content (Join-Path $root 'src\LitePdf.App\LitePdf.App.csproj')
$displayName = [string]$project.Project.PropertyGroup.Product
$appVersion = [version][string]$project.Project.PropertyGroup.Version
$version = '{0}.{1}.{2}.0' -f $appVersion.Major, $appVersion.Minor, $appVersion.Build

# A new staging directory prevents files from an earlier build entering the package.
$stage = Join-Path $root ('artifacts\msix-stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
dotnet publish (Join-Path $root 'src\LitePdf.App\LitePdf.App.csproj') `
    -c Release -r win-x64 --self-contained true -o $stage `
    /p:PublishReadyToRun=true /p:DebugType=none /p:SatelliteResourceLanguages=en
if ($LASTEXITCODE -ne 0) { throw 'MSIX application publish failed.' }
& (Join-Path $stage 'LitePDF.exe') --self-test | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'MSIX application self-test failed.' }

$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
# Explicit unqualified images need no resource-index generation.
foreach ($logo in 'StoreLogo', 'Square44x44Logo', 'Square150x150Logo') {
    Copy-Item -LiteralPath (Join-Path $root "assets\store\msix\$logo.scale-100.png") -Destination (Join-Path $assets "$logo.png")
}
function Escape-Xml([string]$Value) { [Security.SecurityElement]::Escape($Value) }
$identityName = Escape-Xml $PackageName
$identityPublisher = Escape-Xml $Publisher
$publisherLabel = Escape-Xml $PublisherDisplayName
$appLabel = Escape-Xml $displayName
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap rescap">
 <Identity Name="$identityName" Publisher="$identityPublisher" Version="$version" ProcessorArchitecture="x64" />
 <Properties>
  <DisplayName>$appLabel</DisplayName>
  <PublisherDisplayName>$publisherLabel</PublisherDisplayName>
  <Description>A lightweight, private PDF and scan reader for Windows.</Description>
  <Logo>Assets\StoreLogo.png</Logo>
 </Properties>
 <Resources><Resource Language="en-us" /></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
 <Applications>
  <Application Id="LitePDF" Executable="LitePDF.exe" EntryPoint="Windows.FullTrustApplication">
   <uap:VisualElements DisplayName="$appLabel" Description="A lightweight, private PDF and scan reader for Windows."
    Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" BackgroundColor="#0F6CBD" />
   <Extensions>
    <uap:Extension Category="windows.fileTypeAssociation">
     <uap:FileTypeAssociation Name="litepdf.pdf">
      <uap:DisplayName>PDF Document</uap:DisplayName>
      <uap:SupportedFileTypes><uap:FileType>.pdf</uap:FileType></uap:SupportedFileTypes>
     </uap:FileTypeAssociation>
    </uap:Extension>
   </Extensions>
  </Application>
 </Applications>
 <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))
$outDir = if ($ValidationOnly) { Join-Path $root 'artifacts\msix-validation' } else { Join-Path $root 'dist' }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$suffix = if ($ValidationOnly) { '-VALIDATION-ONLY' } else { '' }
$package = Join-Path $outDir "LitePDF-$version-x64$suffix.msix"
& $MakeAppxPath pack /d $stage /p $package /o
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx validation or packaging failed.' }
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$package.sha256", "$hash  $([IO.Path]::GetFileName($package))`n")
Write-Host "Created unsigned MSIX: $package"
if ($ValidationOnly) { Write-Warning 'Validation identity only. Do not upload this package to the Store.' }
else { Write-Host 'Upload to the matching MSIX listing in Partner Center; this unsigned package is not for direct installation.' }
