# Скачивает xray.exe и wintun.dll в Assets, если их ещё нет (Windows x64).
$ErrorActionPreference = 'Stop'
$assets = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $PSScriptRoot '..') 'Assets'))
New-Item -ItemType Directory -Force -Path $assets | Out-Null

$xrayOut = Join-Path $assets 'xray.exe'
$wintunOut = Join-Path $assets 'wintun.dll'

if (-not (Test-Path $xrayOut)) {
    Write-Host "Downloading Xray-windows-64..."
    $zip = Join-Path $env:TEMP ("xray-windows-64-" + [Guid]::NewGuid().ToString('n') + '.zip')
    Invoke-WebRequest -Uri 'https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip' -OutFile $zip -UseBasicParsing
    $extract = Join-Path $env:TEMP ('xray-win-extract-' + [Guid]::NewGuid().ToString('n'))
    if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    Copy-Item (Join-Path $extract 'xray.exe') $xrayOut -Force
}

if (-not (Test-Path $wintunOut)) {
    Write-Host "Downloading Wintun..."
    $zip = Join-Path $env:TEMP ('wintun-' + [Guid]::NewGuid().ToString('n') + '.zip')
    $urls = @(
        'https://www.wintun.net/builds/wintun-0.14.1.zip',
        'https://git.zx2c4.com/wintun/snapshot/wintun-0.14.1.zip'
    )
    $downloaded = $false
    foreach ($u in $urls) {
        try {
            Invoke-WebRequest -Uri $u -OutFile $zip -UseBasicParsing -TimeoutSec 120
            $downloaded = $true
            break
        } catch {
            Write-Host "Failed: $u"
        }
    }
    if (-not $downloaded) { throw 'Failed to download Wintun from all sources' }
    $extract = Join-Path $env:TEMP ('wintun-extract-' + [Guid]::NewGuid().ToString('n'))
    if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $dll = Get-ChildItem -Path $extract -Recurse -Filter 'wintun.dll' | Where-Object { $_.FullName -match '(?i)amd64' } | Select-Object -First 1
    if (-not $dll) { $dll = Get-ChildItem -Path $extract -Recurse -Filter 'wintun.dll' | Select-Object -First 1 }
    if (-not $dll) { throw 'wintun.dll not found in archive' }
    Copy-Item $dll.FullName $wintunOut -Force
}

Write-Host "Assets OK: xray + wintun -> $assets"
