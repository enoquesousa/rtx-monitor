[CmdletBinding()]
param([string]$ToolDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($ToolDirectory)) { $ToolDirectory = Join-Path $projectRoot 'artifacts\tools\wix3-3.14.1' }
$toolRoot = [IO.Path]::GetFullPath($ToolDirectory)
$zip = Join-Path $toolRoot 'wix314-binaries.zip'
$bin = Join-Path $toolRoot 'bin'
$url = 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip'
$expectedHash = '6AC824E1642D6F7277D0ED7EA09411A508F6116BA6FAE0AA5F2C7DAA2FF43D31'
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { Invoke-WebRequest -Uri $url -OutFile $zip }
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expectedHash) { throw 'O ZIP do WiX divergiu dos bytes revisados.' }
if ((Get-Item -LiteralPath $zip).Length -ne 41297555) { throw 'Tamanho inesperado no ZIP do WiX.' }
if (-not (Test-Path -LiteralPath (Join-Path $bin 'candle.exe') -PathType Leaf)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            $parts = $entry.FullName.Replace('\', '/').Split('/')
            if ($entry.FullName -match '^[/\\]' -or $entry.FullName -match '^[A-Za-z]:' -or $parts -contains '..') { throw 'Caminho inválido no ZIP do WiX.' }
        }
    } finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $zip -DestinationPath $bin
}
foreach ($name in @('candle.exe', 'light.exe', 'dark.exe')) {
    $path = Join-Path $bin $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).VersionInfo.FileVersion -ne '3.14.1.8722') {
        throw "Ferramenta WiX ausente ou versão divergente: $name"
    }
}
[pscustomobject]@{
    Directory = $bin; Candle = (Join-Path $bin 'candle.exe'); Light = (Join-Path $bin 'light.exe'); Dark = (Join-Path $bin 'dark.exe')
    Source = $url; ArchiveSha256 = $expectedHash; Version = '3.14.1.8722'
}
