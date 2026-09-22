; Inno Setup script for {#AppName}.
;
; Built by publish.ps1, which publishes a self-contained build into publish\ first and passes its version
; in. Self-contained means the .NET runtime travels with the app, so a target PC needs nothing installed.
;
; To build by hand:
;   powershell -ExecutionPolicy Bypass -File publish.ps1
;   "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" installer\LitePDF.iss

; AppName is what people read. AppSlug is what Windows stores: the ProgID, the registry keys, the data folder
; and the setup filename. Keep them apart. A ProgID with a space in it is asking for trouble, the data folder
; has to keep matching AppPaths.Root in the app ("LitePDF"), and renaming the app must not orphan either.
#define AppName "Lite PDF"
#define AppSlug "LitePDF"
#define AppPublisher "Nur Innovative Solutions"
#define AppExe "LitePDF.exe"

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef OutputBaseName
  #define OutputBaseName AppSlug + "-" + AppVersion + "-setup"
#endif

[Setup]
; Never reuse this GUID for another application: it is how Windows recognizes an upgrade of this one.
AppId={{8E0C4C31-6A4B-4E9E-9E2C-5B6A0C7F1D42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
SetupIconFile=..\src\LitePdf.App\Assets\LitePDF.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; x64 only: the published build is win-x64.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

; Install for the current user without a UAC prompt when possible, or for everyone when run as admin.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
DisableDirPage=auto
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "associate"; Description: "Offer {#AppName} when opening PDF files"; GroupDescription: "File types:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Register as a PDF handler so the app appears under "Open with" and in Settings > Default apps.
; Windows 10 and 11 will not let an installer silently take over the default, and this does not try to:
; the user picks it themselves the first time they open a PDF with it.
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: associate

Root: HKA; Subkey: "Software\Classes\{#AppSlug}.Document"; ValueType: string; ValueData: "PDF Document"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#AppSlug}.Document\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#AppSlug}.Document\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgIds"; ValueType: string; ValueName: "{#AppSlug}.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate

; Listed in Settings > Apps > Default apps, so the user can make it the default from there.
Root: HKA; Subkey: "Software\{#AppSlug}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\{#AppSlug}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A lightweight, private PDF and scan reader."; Tasks: associate
Root: HKA; Subkey: "Software\{#AppSlug}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "{#AppSlug}.Document"; Tasks: associate
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppSlug}"; ValueData: "Software\{#AppSlug}\Capabilities"; Flags: uninsdeletevalue; Tasks: associate

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings, recent files, the OCR cache and logs. Removed only when the user asks for it.
Type: filesandordirs; Name: "{localappdata}\{#AppSlug}"; Check: ShouldRemoveData

[Code]
{ The app keeps its data in %LocalAppData%\{#AppSlug}. Leaving it behind after an uninstall is untidy, but
  deleting it unasked throws away reading positions and every page the user has had recognized. So ask once. }
var
  RemoveData: Boolean;

function InitializeUninstall(): Boolean;
begin
  { A silent uninstall has nobody to ask, so it keeps the data: losing it is the one thing that cannot be undone. }
  RemoveData := (not UninstallSilent) and
    (MsgBox('Also remove {#AppName}''s settings, recent file list and recognized text cache?'#13#10#13#10 +
      'Choose No to keep them for a future install.', mbConfirmation, MB_YESNO) = IDYES);
  Result := True;
end;

function ShouldRemoveData(): Boolean;
begin
  Result := RemoveData;
end;
