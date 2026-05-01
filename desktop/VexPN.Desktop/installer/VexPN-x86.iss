[Setup]
AppId={{F2A7F5BA-01E8-4B4B-9A2D-0E3A4A1B2C10}
AppName=VexPN
AppVersion=1.0.2
AppPublisher=VexPN
DefaultDirName={autopf}\VexPN
DefaultGroupName=VexPN
UninstallDisplayName=VexPN
UninstallDisplayIcon={app}\VexPN.exe
SetupIconFile=..\Assets\app.ico
UninstallFilesDir={app}
OutputDir=dist
OutputBaseFilename=VexPN-Setup-x86
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x86
PrivilegesRequired=admin
WizardStyle=modern
AlwaysOnTop=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
; Собранная публикация x86 (нужно заранее: dotnet publish -r win-x86)
Source: "..\publish-x86\VexPN.exe"; DestDir: "{app}"; DestName: "VexPN.exe"; Flags: ignoreversion
Source: "..\publish-x86\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

; uninstall.exe (наш удобный ярлык на удаление)
Source: "uninstall\x86\uninstall.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall\x86\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\VexPN"; Filename: "{app}\VexPN.exe"
Name: "{group}\Удалить VexPN"; Filename: "{app}\uninstall.exe"
Name: "{commondesktop}\VexPN"; Filename: "{app}\VexPN.exe"; Tasks: desktopicon

; См. VexPN-x64.iss: запуск с UAC (requireAdministrator), иначе код 740.
[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not WizardSilent() then
      ShellExec('runas', ExpandConstant('{app}\VexPN.exe'), '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;

