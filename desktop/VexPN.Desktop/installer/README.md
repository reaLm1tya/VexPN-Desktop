# VexPN installer (x64 + x86)

В папке `installer/` лежат скрипты для Inno Setup:

- `VexPN-x64.iss` — установщик для Windows x64
- `VexPN-x86.iss` — установщик для Windows x86

## 1) Подготовка publish папок

Собрать single-file self-contained публикации:

```powershell
dotnet publish "desktop\\VexPN.Desktop\\VexPN.Desktop.csproj" -c Release -r win-x64 `
  /p:PublishDir="desktop\\VexPN.Desktop\\publish-x64\\" `
  /p:AppendRuntimeIdentifierToPublishPath=false `
  /p:SelfContained=true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:IncludeAllContentForSelfExtract=true

dotnet publish "desktop\\VexPN.Desktop\\VexPN.Desktop.csproj" -c Release -r win-x86 `
  /p:PublishDir="desktop\\VexPN.Desktop\\publish-x86\\" `
  /p:AppendRuntimeIdentifierToPublishPath=false `
  /p:SelfContained=true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:IncludeAllContentForSelfExtract=true
```

Важно:
- В `publish-x64\\Assets\\` должен быть `wintun.dll` **x64**, `xray.exe` **x64**
- В `publish-x86\\Assets\\` должен быть `wintun.dll` **x86**, `xray.exe` **x86**

## 2) Сборка uninstall.exe

Готовые файлы уже кладутся в:

- `installer\\uninstall\\x64\\uninstall.exe`
- `installer\\uninstall\\x86\\uninstall.exe`

При необходимости пересобрать:

```powershell
dotnet publish "desktop\\VexPN.UninstallStub\\VexPN.UninstallStub.csproj" -c Release -r win-x64 `
  /p:PublishSingleFile=true /p:SelfContained=true /p:PublishDir="desktop\\VexPN.Desktop\\installer\\uninstall\\x64\\"

dotnet publish "desktop\\VexPN.UninstallStub\\VexPN.UninstallStub.csproj" -c Release -r win-x86 `
  /p:PublishSingleFile=true /p:SelfContained=true /p:PublishDir="desktop\\VexPN.Desktop\\installer\\uninstall\\x86\\"
```

## 3) Сборка установщиков

Установи Inno Setup и скомпилируй `.iss`:

- открой Inno Setup Compiler → Compile `VexPN-x64.iss` и `VexPN-x86.iss`
- получатся `VexPN-Setup-x64.exe` и `VexPN-Setup-x86.exe`

Установщик создаёт ярлык на рабочем столе (опционально) и кладёт `uninstall.exe` в корень папки установки.

