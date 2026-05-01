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
OutputBaseFilename=VexPN-Setup-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
PrivilegesRequired=admin
WizardStyle=modern

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
; Собранная публикация x64
Source: "..\publish-x64\VexPN.exe"; DestDir: "{app}"; DestName: "VexPN.exe"; Flags: ignoreversion
Source: "..\publish-x64\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

; uninstall.exe (наш удобный ярлык на удаление)
Source: "uninstall\x64\uninstall.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall\x64\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\VexPN"; Filename: "{app}\VexPN.exe"
Name: "{group}\Удалить VexPN"; Filename: "{app}\uninstall.exe"
Name: "{commondesktop}\VexPN"; Filename: "{app}\VexPN.exe"; Tasks: desktopicon

; VexPN требует UAC (app.manifest). Обычный [Run] после установки даёт CreateProcess 740 (нет повышения).
; Запуск через ShellExec runas — отдельный запрос UAC, приложение стартует с правами администратора.
[Code]
const
  HWND_TOPMOST = -1;
  SWP_NOSIZE = $0001;
  SWP_NOMOVE = $0002;
  SWP_NOACTIVATE = $0010;

function SetWindowPos(hWnd: HWND; hWndInsertAfter: HWND; X, Y, cx, cy: Integer; uFlags: UINT): BOOL;
  external 'SetWindowPos@user32.dll stdcall';

procedure InitializeWizard;
begin
  SetWindowPos(WizardForm.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE or SWP_NOACTIVATE);
end;

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

