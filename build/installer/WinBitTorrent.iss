; Inno Setup script for WinBitTorrent
; -----------------------------------
; Builds a per-user / all-users setup .exe from the staged, self-contained
; publish payload. Compile with:
;
;   ISCC.exe build\installer\WinBitTorrent.iss
;
; Optional overrides (passed with /D on the ISCC command line):
;   /DAppVersion=1.0.0
;   /DAppArchitecture=x64 (or arm64)
;   /DPayloadDir=<absolute path to the staged win-x64 payload folder>
;   /DOutputDir=<absolute path for the produced setup .exe>

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef AppArchitecture
  #define AppArchitecture "x64"
#endif
#if AppArchitecture != "x64" && AppArchitecture != "arm64"
  #error Unsupported AppArchitecture
#endif
#if AppArchitecture == "arm64"
  #define ArtifactSuffix "-arm64"
#else
  #define ArtifactSuffix ""
#endif

; Folder containing the published, self-contained portable app
; (WinBitTorrent.exe, Backend\, Licenses\, WindowsAppSDK runtime, ...).
; Paths are resolved relative to this .iss file unless an absolute path
; is provided.
#ifndef PayloadDir
  #define PayloadDir "..\..\dist\WinBitTorrent-" + AppVersion + ArtifactSuffix + "-portable"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\dist\WinBitTorrent-" + AppVersion + ArtifactSuffix + "-installer"
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
SetupIconFile=..\..\src\WinBitTorrent\Assets\WinBitTorrent.ico

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
#if AppArchitecture == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041

Compression=lzma2/max
SolidCompression=yes

; AppMutex is checked at STARTUP, before Restart Manager can close applications. With
; /SUPPRESSMSGBOXES a running instance makes Setup abort immediately. InitializeSetup below
; waits for an in-app updater's graceful shutdown BEFORE this built-in check. This also works
; with already shipped clients, which pass /RELAUNCH without a PID or a wait helper.
; Restart Manager remains responsible for any other processes holding installation files.
; RestartApplications is off because /RELAUNCH already starts the updated app exactly once.
AppMutex=WinBitTorrentAppMutex
CloseApplications=force
RestartApplications=no
ChangesAssociations=yes

OutputDir={#OutputDir}
; Keep ARM64 out of the *setup.exe match used by previously shipped x64 clients.
OutputBaseFilename={#AppName}-{#AppVersion}-setup{#ArtifactSuffix}

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

[InstallDelete]
; Versions through 1.1.9 were published without an explicit unpackaged app type and installed
; a generated resources.pri. Starting with 1.2.0 the unpackaged payload intentionally has no
; PRI file. Inno Setup upgrades files in place, so without this migration the obsolete PRI is
; left beside the new executable; WinUI then opens the incompatible resource index and crashes
; during startup with 0x80004002/0xC000027B. Delete only this known legacy artifact before the
; new payload is copied. Application data lives under LocalAppData and is unaffected.
Type: files; Name: "{app}\resources.pri"
; The libtorrent-native release no longer ships the qBittorrent/Qt local backend. Remove only
; the exact binaries installed by earlier WinBitTorrent versions; never touch LocalAppData.
Type: files; Name: "{app}\Backend\qbittorrent-nox.exe"
Type: files; Name: "{app}\Backend\Qt6Core.dll"
Type: files; Name: "{app}\Backend\Qt6Network.dll"
Type: files; Name: "{app}\Backend\Qt6Sql.dll"
Type: files; Name: "{app}\Backend\Qt6Xml.dll"
Type: files; Name: "{app}\Backend\sqldrivers\qsqlite.dll"
Type: dirifempty; Name: "{app}\Backend\sqldrivers"

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
english.UpdateShutdownTimeout=WinBitTorrent did not finish closing. Close the application and run the installer again.
russian.TaskShortcuts=Ярлыки:
russian.TaskStartMenuShortcut=Создать ярлык в меню «Пуск»
russian.TaskSystemIntegration=Интеграция с системой:
russian.TaskStartup=Запускать WinBitTorrent при входе в Windows
russian.TaskAssociations=Ассоциации файлов и ссылок:
russian.TaskAssociateTorrent=Использовать WinBitTorrent для файлов .torrent
russian.TaskAssociateMagnet=Использовать WinBitTorrent для magnet-ссылок
russian.RegisteringTorrentAssociation=Регистрация ассоциации файлов .torrent...
russian.RegisteringMagnetAssociation=Регистрация ассоциации magnet-ссылок...
russian.UpdateShutdownTimeout=WinBitTorrent не завершил работу. Закройте приложение и запустите установщик ещё раз.

[Code]
// True when the in-app updater started this installer with /RELAUNCH, so the app
// is relaunched automatically after a silent update.
function WantsRelaunch: Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), '/RELAUNCH') = 0 then
      Result := True;
end;

function InitializeSetup: Boolean;
var
  WaitedMilliseconds: Integer;
begin
  Result := True;
  if not WantsRelaunch then
    Exit;

  Log('In-app update: waiting for WinBitTorrent to finish shutting down.');
  WaitedMilliseconds := 0;
  while CheckForMutexes('WinBitTorrentAppMutex') do
  begin
    if WaitedMilliseconds >= 60000 then
    begin
      Log('In-app update aborted: application mutex still exists after 60 seconds. No files were changed.');
      SuppressibleMsgBox(CustomMessage('UpdateShutdownTimeout'), mbError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;
    Sleep(250);
    WaitedMilliseconds := WaitedMilliseconds + 250;
  end;
  Log('In-app update: application mutex released; continuing setup.');
end;
