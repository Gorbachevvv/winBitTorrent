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

; Use Inno Setup's native WinUI-like styling. The dynamic mode reads the
; Windows app-theme preference at startup and styles every built-in control,
; dialog and title bar consistently instead of recolouring individual widgets.
WizardStyle=modern dynamic windows11 includetitlebar hidebevels
WizardImageFile=assets\WizardImageFile_WinBitTorrent.png
WizardImageFileDynamicDark=assets\WizardImageFile_WinBitTorrent.png
WizardImageBackColor=$0B3563
WizardImageBackColorDynamicDark=$071F3A
WizardImageStretch=yes
WizardKeepAspectRatio=yes
WizardSizePercent=120

; Keep the branded welcome page visible—the side artwork is shown on the
; welcome and completion pages. The ready page gives a clear final summary.
DisableWelcomePage=no
DisableReadyPage=no
ShowLanguageDialog=auto
SetupLogging=yes

; Per-user install by default (no UAC); users may choose all-users in the UI.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 64-bit only, Windows 10 2004 (19041) or newer.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

Compression=lzma2/max
SolidCompression=yes

; The running app holds a mutex of this exact name for its whole lifetime (see App.xaml.cs).
; Without this, a silent in-app update raced the app's own async shutdown: Setup would start
; copying files while WinBitTorrent.exe (or a DLL it had loaded) was still locked, hit a sharing
; violation, and roll back the whole install. With AppMutex set, Setup detects the running
; instance itself and (combined with CloseApplications=force, requested via
; /FORCECLOSEAPPLICATIONS on the update command line) waits for/terminates it before writing any
; files. RestartApplications is off because the app already relaunches itself via the /RELAUNCH
; entry in [Run] - leaving Setup's own restart on would launch it twice.
AppMutex=WinBitTorrentAppMutex
CloseApplications=force
RestartApplications=no
ChangesAssociations=yes

OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "startmenuicon"; Description: "{cm:TaskStartMenuShortcut}"; GroupDescription: "{cm:TaskShortcuts}"; Flags: checkedonce
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:TaskShortcuts}"; Flags: unchecked
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:TaskSystemIntegration}"; Flags: unchecked
Name: "associatetorrent"; Description: "{cm:TaskAssociateTorrent}"; GroupDescription: "{cm:TaskAssociations}"; Flags: checkedonce
Name: "associatemagnet"; Description: "{cm:TaskAssociateMagnet}"; GroupDescription: "{cm:TaskAssociations}"; Flags: checkedonce

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startmenuicon
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
; Register only the handlers selected on the Tasks page. Keeping these separate
; matches qBittorrent's user-facing choices and respects Windows default-app UX.
Filename: "{app}\{#AppExe}"; Parameters: "--register-torrent-association"; Flags: runasoriginaluser runhidden waituntilterminated; StatusMsg: "{cm:RegisteringTorrentAssociation}"; Tasks: associatetorrent
Filename: "{app}\{#AppExe}"; Parameters: "--register-magnet-association"; Flags: runasoriginaluser runhidden waituntilterminated; StatusMsg: "{cm:RegisteringMagnetAssociation}"; Tasks: associatemagnet
; Normal (interactive) install: offer to launch on the Finished page.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; In-app update (installer started with /RELAUNCH, silently): relaunch automatically.
Filename: "{app}\{#AppExe}"; Flags: nowait postinstall; Check: WantsRelaunch

[UninstallRun]
; Remove the associations before the executable is deleted.
; ('runasoriginaluser' is a [Run]-only flag and is not valid here.)
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-associations"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterAssociations"

[UninstallDelete]
; Remove the (now empty) application folder on uninstall. User data in
; %LOCALAPPDATA%\WinBitTorrent is intentionally left untouched.
Type: dirifempty; Name: "{app}"

[CustomMessages]
english.TaskShortcuts=Shortcuts:
english.TaskStartMenuShortcut=Create a Start Menu shortcut
english.TaskSystemIntegration=System integration:
english.TaskStartup=Start WinBitTorrent when Windows starts
english.TaskAssociations=File and link associations:
english.TaskAssociateTorrent=Use WinBitTorrent for .torrent files
english.TaskAssociateMagnet=Use WinBitTorrent for magnet links
english.RegisteringTorrentAssociation=Registering .torrent file association...
english.RegisteringMagnetAssociation=Registering magnet link association...
russian.TaskShortcuts=Ярлыки:
russian.TaskStartMenuShortcut=Создать ярлык в меню «Пуск»
russian.TaskSystemIntegration=Интеграция с системой:
russian.TaskStartup=Запускать WinBitTorrent при входе в Windows
russian.TaskAssociations=Ассоциации файлов и ссылок:
russian.TaskAssociateTorrent=Использовать WinBitTorrent для файлов .torrent
russian.TaskAssociateMagnet=Использовать WinBitTorrent для magnet-ссылок
russian.RegisteringTorrentAssociation=Регистрация ассоциации файлов .torrent...
russian.RegisteringMagnetAssociation=Регистрация ассоциации magnet-ссылок...

[Code]
// True when the in-app updater started this installer with /RELAUNCH, so the app
// is relaunched automatically after a silent update.
function WantsRelaunch: Boolean;
begin
  Result := Pos('/RELAUNCH', UpperCase(GetCmdTail)) > 0;
end;
