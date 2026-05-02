# Скачивает mihomo.exe (Clash Meta) для Windows x64 в Assets, если файла ещё нет.
$ErrorActionPreference = 'Stop'
$assets = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $PSScriptRoot '..') 'Assets'))
New-Item -ItemType Directory -Force -Path $assets | Out-Null

$mihomoOut = Join-Path $assets 'mihomo.exe'
if (Test-Path $mihomoOut) {
    Write-Host "mihomo.exe already present."
    exit 0
}

Write-Host "Resolving latest Mihomo release..."
$rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/MetaCubeX/mihomo/releases/latest' -Headers @{ 'User-Agent' = 'VexPN-Desktop-AssetScript' }
$asset = $rel.assets | Where-Object {
    $n = $_.name
    $n -match 'mihomo.*windows.*amd64' -and ($n -like '*.gz' -or $n -like '*.zip')
} | Select-Object -First 1
if (-not $asset) {
    throw 'Could not find mihomo Windows amd64 archive in latest release assets.'
}

$tmp = Join-Path $env:TEMP ('mihomo-dl-' + [Guid]::NewGuid().ToString('n') + [IO.Path]::GetExtension($asset.name))
Write-Host "Downloading $($asset.name)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmp -UseBasicParsing

try {
    if ($asset.name -like '*.zip') {
        $extract = Join-Path $env:TEMP ('mihomo-extract-' + [Guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Force -Path $extract | Out-Null
        try {
            Expand-Archive -Path $tmp -DestinationPath $extract -Force
            $exes = @(Get-ChildItem -Path $extract -Recurse -Filter '*.exe' -File -ErrorAction SilentlyContinue)
            $exe = $exes | Where-Object { $_.Name -match '(?i)mihomo' } | Select-Object -First 1
            if (-not $exe) {
                $exe = $exes | Where-Object { $_.Name -match '(?i)clash' } | Select-Object -First 1
            }
            if (-not $exe) {
                $exe = $exes | Select-Object -First 1
            }
            if (-not $exe) { throw 'No .exe found in Mihomo zip' }
            Copy-Item $exe.FullName $mihomoOut -Force
        } finally {
            if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
        }
    } else {
        Add-Type -AssemblyName System.IO.Compression
        $in = [System.IO.File]::OpenRead($tmp)
        try {
            $gs = New-Object System.IO.Compression.GZipStream($in, [System.IO.Compression.CompressionMode]::Decompress)
            try {
                $out = [System.IO.File]::Create($mihomoOut)
                try {
                    $gs.CopyTo($out)
                } finally { $out.Dispose() }
            } finally { $gs.Dispose() }
        } finally { $in.Dispose() }
    }
} finally {
    if (Test-Path $tmp) { Remove-Item -Force $tmp }
}

Write-Host "Mihomo OK -> $mihomoOut"
