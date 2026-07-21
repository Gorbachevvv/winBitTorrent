; Inno Setup script for WinBitTorrent
; -----------------------------------
; Builds a per-user / all-users setup .exe from the staged, self-contained
; publish payload. Compile with:
;
;   ISCC.exe build\installer\WinBitTorrent.iss
;
; Optional overrides (passed with /D on the ISCC command line):
;   /DAppVersion=1.0.0
;   /DPayloadDir=<absolute path to the staged win-x64 payload folder>
;   /DOutputDir=<absolute path for the produced setup .exe>

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

; Folder containing the published, self-contained portable app
; (WinBitTorrent.exe, Backend\, Licenses\, WindowsAppSDK runtime, ...).
; Paths are resolved relative to this .iss file unless an absolute path
; is provided.
#ifndef PayloadDir
  #define PayloadDir "..\..\dist\WinBitTorrent-" + AppVersion + "-portable"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\dist\WinBitTorrent-" + AppVersion + "-installer"
#endif

#define AppName "WinBitTorrent"
#define AppPublisher "Gorbachevvv"
#define AppUrl "https://github.com/Gorbachevvv/winBitTorrent"
#define AppExe "WinBitTorrent.exe"

[Setup]
; A stable, unique AppId keeps upgrades and uninstall entries consistent.
AppId={{B7E9C1A4-3F2D-4E8B-9A6C-1D5F0E2A7C83}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}.0

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=auto
AllowNoIcons=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\..\LICENSE
SetupIconFile=..\..\Assets\WinBitTorrent.ico
WizardStyle=modern

; Per-user install by default (no UAC); users may choose all-users in the UI.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 64-bit only, Windows 10 2004 (19041) or newer.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

Compression=lzma2/max
SolidCompression=yes

OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Register the .torrent / magnet associations right away, as the current user, so
; they work immediately after install instead of only after the first manual launch.
Filename: "{app}\{#AppExe}"; Parameters: "--register-associations"; Flags: runasoriginaluser runhidden waituntilterminated; StatusMsg: "Registering file associations..."
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the associations before the executable is deleted.
; ('runasoriginaluser' is a [Run]-only flag and is not valid here.)
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-associations"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterAssociations"

[UninstallDelete]
; Remove the (now empty) application folder on uninstall. User data in
; %LOCALAPPDATA%\WinBitTorrent is intentionally left untouched.
Type: dirifempty; Name: "{app}"
