; Fileman – Setup (Inno Setup 6)
;
; Nicht direkt aufrufen, sondern über:   dotnet build installer/Fileman.Installer.proj -c Release
; Das Projekt veröffentlicht vorher beide Programme und übergibt AppVersion, PublishDir und OutputDir.
;
; Installiert pro Benutzer nach %LOCALAPPDATA%\Programs\Fileman (keine Administratorrechte nötig).
; Einstellungen (%LOCALAPPDATA%\Fileman) und das Archiv auf der Sicherungsplatte bleiben bei Updates erhalten.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Fileman"
#define AppExe "Fileman.exe"

[Setup]
; Die AppId darf sich nie ändern – daran erkennt Windows Updates und die Deinstallation.
AppId={{C11A3855-2512-4B6F-A426-1477F38703FB}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=Fileman-Setup-{#AppVersion}
SetupIconFile=..\src\Fileman.App\Fileman.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
; Laufende Instanzen beendet der [Code]-Abschnitt selbst (Fileman.exe --exit).
CloseApplications=no

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "autostart"; Description: "Fileman beim Anmelden an Windows automatisch starten (empfohlen)"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[InstallDelete]
; Bei Updates Programmdateien der Vorversion entfernen, damit keine veralteten Bibliotheken liegen bleiben.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.exe"
Type: files; Name: "{app}\*.json"

[Files]
; Fileman.exe (Tray), fileman-cli.exe und die gemeinsame .NET-Laufzeit
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Kurzanleitung.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{#AppName} - Archiv durchsuchen"; Filename: "{app}\{#AppExe}"; Parameters: "--archiv"
Name: "{autoprograms}\{#AppName} - Kurzanleitung"; Filename: "{app}\Kurzanleitung.txt"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Gleicher Eintrag wie "Mit Windows starten" in den Fileman-Einstellungen.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Fileman jetzt starten"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "StopFileman"

[Code]
// Vor einem Update: laufendes Fileman beenden, damit die Programmdatei ersetzt werden kann.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Nach der Deinstallation: Autostart-Eintrag entfernen (auch wenn er in Fileman selbst gesetzt wurde)
// und auf Wunsch die lokalen Einstellungen löschen. Das Archiv auf der Sicherungsplatte wird nie angetastet.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    DataDir := ExpandConstant('{localappdata}\Fileman');
    if DirExists(DataDir) then
      if SuppressibleMsgBox(
           'Sollen auch die Einstellungen und der lokale Zwischenspeicher von Fileman gelöscht werden?' + #13#10#13#10 +
           'Achtung: Im Zwischenspeicher können Änderungen liegen, die noch nicht auf die Sicherungsplatte übertragen wurden.' + #13#10#13#10 +
           'Das Sicherungsarchiv auf der Sicherungsplatte bleibt in jedem Fall erhalten.',
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
