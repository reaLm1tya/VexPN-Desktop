[Setup]
AppId={{F2A7F5BA-01E8-4B4B-9A2D-0E3A4A1B2C10}
AppName=VexPN
AppVersion=1.0.1
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

[Run]
Filename: "{app}\VexPN.exe"; Description: "Запустить VexPN"; Flags: nowait postinstall skipifsilent

