[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StagingDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.12.4',
    [ValidateSet('public', 'private')][string]$PackageMode = 'public'
)

$ErrorActionPreference = 'Stop'
$stage = (Resolve-Path -LiteralPath $StagingDirectory).Path.TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $stage 'rtx3060-monitor.exe') -PathType Leaf)) {
    throw 'O pacote exige o executável nativo rtx3060-monitor.exe.'
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$items = @(Get-ChildItem -LiteralPath $stage -Recurse -Force)
if ($items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
    throw 'O pacote não aceita links simbólicos ou junctions no staging.'
}
$files = @($items | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
if ($files.Count -eq 0) { throw 'O staging está vazio.' }
$readerRoot = Join-Path $stage 'runtime\collector\research-readers'
$pythonRoot = Join-Path $stage 'runtime\python'
if ($PackageMode -eq 'public') {
    if ((Test-Path -LiteralPath $readerRoot) -or (Test-Path -LiteralPath $pythonRoot)) {
        throw 'O pacote público contém leitores privados ou Python embarcado.'
    }
}
else {
    foreach ($required in @((Join-Path $readerRoot 'worker.py'),
                           (Join-Path $readerRoot 'manifest.json'),
                           (Join-Path $pythonRoot 'python.exe'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Pacote privado incompleto: $required"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $readerRoot 'vendor') -PathType Container)) {
        throw 'Pacote privado incompleto: vendor ausente.'
    }
}
$proofPins = @{}
$observedProofs = @{}
$allowedGpuUuids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($PackageMode -eq 'public') {
    $policyPath = Join-Path $PSScriptRoot '..\..\scripts\publication\public-policy.json'
    if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { throw 'Política pública de identificadores ausente.' }
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    foreach ($value in $policy.allowed_synthetic_gpu_uuids) {
        if ($value -notmatch '^GPU-[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$') {
            throw 'UUID sintético inválido na política pública.'
        }
        [void]$allowedGpuUuids.Add($value)
    }
    if ($allowedGpuUuids.Count -eq 0) { throw 'Política pública sem fixtures sintéticas.' }
}
$proofManifest = Join-Path $stage 'runtime\collector\research-readers\manifest.json'
if (Test-Path -LiteralPath $proofManifest -PathType Leaf) {
    $proof = Get-Content -LiteralPath $proofManifest -Raw | ConvertFrom-Json
    if ($proof.schema_version -ne 1 -or $proof.settings_writes_requested -ne $false) { throw 'Manifesto de leitores inválido.' }
    foreach ($pin in $proof.files) {
        $key = ('runtime/collector/research-readers/vendor/' + $pin.path.Replace('\', '/')).ToLowerInvariant()
        if ($key -match '(^|/)\.\.(/|$)' -or $proofPins.ContainsKey($key)) { throw 'Caminho inválido ou duplicado no manifesto de provas.' }
        $proofPins[$key] = $pin.sha256
    }
}
$records = @()
foreach ($file in $files) {
    if (-not $file.FullName.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Arquivo fora do staging: $($file.FullName)"
    }
    $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
    if ($PackageMode -eq 'public' -and $relative -match '\.(?:exe|dll|pdb)$') {
        if ($relative -match '\.pdb$') { throw "Símbolos de depuração não devem entrar no pacote público: $relative" }
        $binaryBytes = [IO.File]::ReadAllBytes($file.FullName)
        foreach ($binaryText in @([Text.Encoding]::Latin1.GetString($binaryBytes),[Text.Encoding]::Unicode.GetString($binaryBytes))) {
            if ($binaryText -match '(?i)[a-z]:[\\/]users[\\/][^\\/\x00]+') {
                throw "Caminho local embutido no executável público: $relative"
            }
            $gpuUuidPattern = '(?i)GPU-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}'
            foreach ($uuid in [regex]::Matches($binaryText,$gpuUuidPattern)) {
                if (-not $allowedGpuUuids.Contains($uuid.Value)) {
                    throw "UUID individual embutido no executável público: $relative"
                }
            }
        }
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    $isPinnedProof = $proofPins.ContainsKey($relative.ToLowerInvariant())
    if ($isPinnedProof -and $hash -ne $proofPins[$relative.ToLowerInvariant()]) { throw "Prova revisada alterada: $relative" }
    if ($isPinnedProof) { $observedProofs[$relative.ToLowerInvariant()] = $true }
    if ($relative -match '(^|/)(?:_runtime|__pycache__|wwwroot)(/|$)' -or
        ((($relative -match '(^|/)data(/|$)') -or ($relative -match '\.(?:db|db-wal|db-shm|log|pyc|pyo|html)$')) -and -not $isPinnedProof)) {
        throw "Dados de execução ou interface web não devem entrar no MSI: $relative"
    }
    $records += [pscustomobject]@{
        path = $relative
        bytes = $file.Length
        sha256 = $hash
        source = $file.FullName
    }
}
foreach ($key in $proofPins.Keys) {
    if (-not $observedProofs.ContainsKey($key)) { throw "Prova revisada ausente: $key" }
}

function Get-TextHash([string]$text) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return $algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) }
    finally { $algorithm.Dispose() }
}
function Get-Identifier([string]$prefix, [string]$text) {
    $hex = ([BitConverter]::ToString((Get-TextHash $text))).Replace('-', '').ToLowerInvariant()
    return $prefix + '_' + $hex.Substring(0, 32)
}
function Get-StableGuid([string]$text) {
    $bytes = [byte[]](Get-TextHash ('Rtx3060Monitor.NativeInstaller|' + $text))[0..15]
    $bytes[7] = ($bytes[7] -band 15) -bor 80
    $bytes[8] = ($bytes[8] -band 63) -bor 128
    return ([Guid]::new($bytes)).ToString('D').ToUpperInvariant()
}
$contentIdentity = ($records | ForEach-Object { $_.path.ToLowerInvariant() + '|' + $_.bytes + '|' + $_.sha256 }) -join "`n"
$payloadHash = ([BitConverter]::ToString((Get-TextHash $contentIdentity))).Replace('-', '').ToLowerInvariant()
$productCode = Get-StableGuid ('product|' + $Version + '|' + $payloadHash)
$upgradeCode = 'B8F49BED-0ECF-55CC-9DF0-B659EA471DD7'
$namespace = 'http://schemas.microsoft.com/wix/2006/wi'
$xml = [Xml.XmlDocument]::new()
[void]$xml.AppendChild($xml.CreateXmlDeclaration('1.0', 'utf-8', $null))
function Add-Element($parent, [string]$name, [hashtable]$attributes = @{}, [string]$text = '') {
    $node = $xml.CreateElement($name, $namespace)
    foreach ($key in @($attributes.Keys | Sort-Object)) { $node.SetAttribute($key, [string]$attributes[$key]) }
    if ($text.Length -gt 0) { $node.InnerText = $text }
    [void]$parent.AppendChild($node)
    return $node
}
$wix = Add-Element $xml 'Wix'
$product = Add-Element $wix 'Product' @{
    Id = $productCode; Name = 'RTX 3060 Monitor'; Language = '1046'; Codepage = '1252'
    Version = $Version; Manufacturer = 'RTX Monitor'; UpgradeCode = $upgradeCode
}
[void](Add-Element $product 'Package' @{
    Id = '*'; InstallerVersion = '500'; Compressed = 'yes'; Platform = 'x64'
    InstallScope = 'perUser'; InstallPrivileges = 'limited'; Languages = '1046'
    Description = 'Monitor nativo da RTX 3060 com modelo 3D e aquisição somente leitura.'
    Comments = 'Instala somente os arquivos e atalhos deste aplicativo para o usuário atual.'
})
[void](Add-Element $product 'MajorUpgrade' @{
    Schedule = 'afterInstallInitialize'
    DowngradeErrorMessage = 'Uma versão mais recente do RTX 3060 Monitor já está instalada.'
})
[void](Add-Element $product 'MediaTemplate' @{ EmbedCab = 'yes'; CompressionLevel = 'high'; MaximumUncompressedMediaSize = '200' })
[void](Add-Element $product 'Property' @{ Id = 'ARPNOMODIFY'; Value = '1' })
[void](Add-Element $product 'Property' @{ Id = 'ARPHELPLINK'; Value = 'https://github.com/enoquesousa/rtx-monitor' })
[void](Add-Element $product 'Condition' @{ Message = 'Este aplicativo requer Windows de 64 bits.' } 'Installed OR VersionNT64')
# MSI standard directory trees; no machine-wide installation directories.
$target = Add-Element $product 'Directory' @{ Id = 'TARGETDIR'; Name = 'SourceDir' }
$local = Add-Element $target 'Directory' @{ Id = 'LocalAppDataFolder' }
$programs = Add-Element $local 'Directory' @{ Id = 'NativeProgramsFolder'; Name = 'Programs' }
$install = Add-Element $programs 'Directory' @{ Id = 'INSTALLFOLDER'; Name = 'Rtx3060Monitor' }
[void](Add-Element $target 'Directory' @{ Id = 'ProgramMenuFolder' })
[void](Add-Element $target 'Directory' @{ Id = 'DesktopFolder' })
$directories = @{ '' = $install }
foreach ($record in $records) {
    $relativeDirectory = [IO.Path]::GetDirectoryName($record.path.Replace('/', '\'))
    if ([string]::IsNullOrEmpty($relativeDirectory)) { continue }
    $parts = $relativeDirectory.Split('\')
    $current = ''
    $parent = $install
    foreach ($part in $parts) {
        $current = if ($current) { $current + '\' + $part } else { $part }
        if (-not $directories.ContainsKey($current)) {
            $directories[$current] = Add-Element $parent 'Directory' @{ Id = (Get-Identifier 'dir' $current.ToLowerInvariant()); Name = $part }
        }
        $parent = $directories[$current]
    }
}
$componentIds = [Collections.Generic.List[string]]::new()
$directoryCleanups = @{}
foreach ($record in $records) {
    $relativeWin = $record.path.Replace('/', '\')
    $relativeDirectory = [IO.Path]::GetDirectoryName($relativeWin)
    if ([string]::IsNullOrEmpty($relativeDirectory)) { $relativeDirectory = '' }
    $componentId = Get-Identifier 'cmp' $record.path.ToLowerInvariant()
    $component = Add-Element $directories[$relativeDirectory] 'Component' @{
        Id = $componentId; Guid = (Get-StableGuid ('file|' + $record.path.ToLowerInvariant())); Win64 = 'yes'
    }
    $fileId = if ($record.path -ieq 'rtx3060-monitor.exe') { 'MainExecutable' } else { Get-Identifier 'file' $record.path.ToLowerInvariant() }
    $fileAttributes = @{ Id = $fileId; Name = [IO.Path]::GetFileName($record.source); Source = $record.source; KeyPath = 'no' }
    # This native SQLite dependency exposes a fixed file version but no usable
    # MSI language list. Declare its resources language-neutral as a fallback;
    # this changes only installer metadata, never the DLL bytes.
    if ([IO.Path]::GetFileName($record.source) -ieq 'e_sqlite3.dll') { $fileAttributes.DefaultLanguage = '0' }
    [void](Add-Element $component 'File' $fileAttributes)
    [void](Add-Element $component 'RegistryValue' @{
        Id = (Get-Identifier 'reg' $record.path.ToLowerInvariant()); Root = 'HKCU'
        Key = 'Software\RtxMonitor\NativeDesktop\Components'; Name = $componentId
        Type = 'integer'; Value = '1'; KeyPath = 'yes'
    })
    if (-not $directoryCleanups.ContainsKey($relativeDirectory)) {
        [void](Add-Element $component 'RemoveFolder' @{ Id = (Get-Identifier 'cleanup' $relativeDirectory.ToLowerInvariant()); On = 'uninstall' })
        $directoryCleanups[$relativeDirectory] = $true
    }
    $componentIds.Add($componentId)
}
# Explicit per-user shortcuts. Non-advertised shortcuts never trigger driver actions.
foreach ($location in @(@{ directory = 'DesktopFolder'; suffix = 'Desktop' }, @{ directory = 'ProgramMenuFolder'; suffix = 'StartMenu' })) {
    $directoryRef = Add-Element $product 'DirectoryRef' @{ Id = $location.directory }
    $componentId = 'Native' + $location.suffix + 'Shortcut'
    $component = Add-Element $directoryRef 'Component' @{ Id = $componentId; Guid = (Get-StableGuid $componentId); Win64 = 'yes' }
    [void](Add-Element $component 'Shortcut' @{
        Id = ($componentId + 'Link'); Name = 'RTX 3060 Monitor'; Description = 'Monitoramento nativo da RTX 3060 · somente leitura'
        Target = '[INSTALLFOLDER]rtx3060-monitor.exe'; WorkingDirectory = 'INSTALLFOLDER'; Advertise = 'no'
    })
    [void](Add-Element $component 'RegistryValue' @{
        Root = 'HKCU'; Key = 'Software\RtxMonitor\NativeDesktop'; Name = ($location.suffix + 'Shortcut')
        Type = 'integer'; Value = '1'; KeyPath = 'yes'
    })
    $componentIds.Add($componentId)
}
$registration = Add-Element $install 'Component' @{ Id = 'NativeRegistration'; Guid = (Get-StableGuid 'NativeRegistration'); Win64 = 'yes' }
[void](Add-Element $registration 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\RtxMonitor\NativeDesktop'; Name = 'InstallPath'; Type = 'string'; Value = '[INSTALLFOLDER]'; KeyPath = 'yes' })
[void](Add-Element $registration 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\RtxMonitor\NativeDesktop'; Name = 'Version'; Type = 'string'; Value = $Version })
foreach ($relativeDirectory in @($directories.Keys | Sort-Object)) {
    if (-not $directoryCleanups.ContainsKey($relativeDirectory)) {
        [void](Add-Element $registration 'RemoveFolder' @{ Id = (Get-Identifier 'cleanup' $relativeDirectory.ToLowerInvariant()); Directory = $directories[$relativeDirectory].GetAttribute('Id'); On = 'uninstall' })
    }
}
[void](Add-Element $registration 'RemoveFolder' @{ Id = 'RemoveNativeProgramsIfEmpty'; Directory = 'NativeProgramsFolder'; On = 'uninstall' })
$componentIds.Add('NativeRegistration')
$feature = Add-Element $product 'Feature' @{ Id = 'NativeMonitor'; Title = 'RTX 3060 Monitor'; Level = '1'; Absent = 'disallow' }
foreach ($componentId in $componentIds) { [void](Add-Element $feature 'ComponentRef' @{ Id = $componentId }) }

$writerSettings = [Xml.XmlWriterSettings]::new()
$writerSettings.Indent = $true
$writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
$sourcePath = Join-Path $output 'Rtx3060Monitor.wxs'
$writer = [Xml.XmlWriter]::Create($sourcePath, $writerSettings)
try { $xml.Save($writer) } finally { $writer.Dispose() }
$manifest = [ordered]@{
    schema_version = 1; product_name = 'RTX 3060 Monitor'; version = $Version
    product_code = $productCode; upgrade_code = $upgradeCode; architecture = 'x64'
    install_scope = 'perUser'; install_directory = '%LOCALAPPDATA%\Programs\Rtx3060Monitor'
    userdata_directory_preserved = '%LOCALAPPDATA%\Rtx3060Monitor\data'
    custom_actions = 0; windows_services = 0; driver_packages = 0
    payload_sha256 = $payloadHash; file_count = $records.Count
    total_file_bytes = ($records | Measure-Object -Property bytes -Sum).Sum
    immutable_proof_files = $proofPins.Count
    files = @($records | Select-Object path, bytes, sha256)
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'payload-manifest.json') -Encoding utf8
[pscustomobject]@{ Source = $sourcePath; Manifest = (Join-Path $output 'payload-manifest.json'); FileCount = $records.Count; ProductCode = $productCode }
