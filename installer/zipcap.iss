; Zipcap installer (Inno Setup 6). Build with release.ps1, or:
;   ISCC.exe installer\zipcap.iss
; Installs for the current user only (no admin prompt) into %LOCALAPPDATA%\Programs\Zipcap.

#define AppVersion "0.1.0"

[Setup]
AppId={{6B7B1D2E-4E0C-4F7A-9B1E-2A9C3F5D8E41}
AppName=Zipcap
AppVersion={#AppVersion}
AppVerName=Zipcap {#AppVersion}
AppPublisher=Zipcap
AppPublisherURL=https://github.com/ZahinArman/zipcap
AppSupportURL=https://github.com/ZahinArman/zipcap/issues
AppUpdatesURL=https://github.com/ZahinArman/zipcap/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Zipcap
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=Zipcap
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=ZipcapSetup
SetupIconFile=..\zipcap.ico
UninstallDisplayIcon={app}\Zipcap.exe
UninstallDisplayName=Zipcap
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=force
RestartApplications=no
MinVersion=10.0

[Tasks]
Name: "startup"; Description: "Start Zipcap when Windows starts"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\Zipcap.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Zipcap"; Filename: "{app}\Zipcap.exe"
Name: "{userstartup}\Zipcap"; Filename: "{app}\Zipcap.exe"; Tasks: startup
Name: "{userdesktop}\Zipcap"; Filename: "{app}\Zipcap.exe"; Tasks: desktopicon

[InstallDelete]
; legacy: the hand-made shortcut from before Zipcap had an installer
Type: files; Name: "{userstartup}\Quick Capture.lnk"

[Run]
Filename: "{app}\Zipcap.exe"; Description: "Launch Zipcap"; Flags: nowait postinstall skipifsilent
; In-app updates run Setup with /SILENT: start the new version straight away.
Filename: "{app}\Zipcap.exe"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im Zipcap.exe /f"; Flags: runhidden; RunOnceId: "StopZipcap"

; Keys and waiting notes in %APPDATA%\Zipcap are kept on uninstall, so a reinstall picks up where you left off.

[Code]
// If Zipcap is still running (e.g. it just launched this update and is quitting),
// give it a few seconds to exit before files are replaced.
function InitializeSetup(): Boolean;
var
  i, code: Integer;
begin
  i := 0;
  while CheckForMutexes('ZipcapSingleInstance') and (i < 20) do
  begin
    Sleep(250);
    i := i + 1;
  end;
  if CheckForMutexes('ZipcapSingleInstance') and WizardSilent() then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/im Zipcap.exe /f', '', SW_HIDE, ewWaitUntilTerminated, code);
  Result := True;
end;
