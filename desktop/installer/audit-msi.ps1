[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$PayloadManifest,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$DarkPath
)
$ErrorActionPreference = 'Stop'
$msi = (Resolve-Path -LiteralPath $MsiPath).Path
$manifest = Get-Content -LiteralPath $PayloadManifest -Raw | ConvertFrom-Json
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msi, 0))
function Read-Table([string]$sql, [string[]]$columns) {
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($sql))
    try {
        [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        while ($true) {
            $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if ($null -eq $record) { break }
            try {
                $row = [ordered]@{}
                for ($index = 0; $index -lt $columns.Length; $index++) {
                    $row[$columns[$index]] = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @($index + 1))
                }
                [pscustomobject]$row
            } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
    } finally {
        [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}
try {
    $tables = @(Read-Table 'SELECT `Name` FROM `_Tables`' @('Name'))
    $names = @($tables.Name)
    foreach ($forbidden in @('CustomAction', 'ServiceInstall', 'ServiceControl', 'MsiDriverPackages', 'Environment', 'Class', 'AppId', 'TypeLib', 'SelfReg')) {
        if ($names -contains $forbidden) { throw "Tabela não autorizada no MSI: $forbidden" }
    }
    $properties = @{}
    foreach ($row in @(Read-Table 'SELECT `Property`, `Value` FROM `Property`' @('Property', 'Value'))) { $properties[$row.Property] = $row.Value }
    if ($properties['ALLUSERS']) { throw 'O pacote não pode instalar para todos os usuários.' }
    if ($properties['ProductCode'] -ne ('{' + $manifest.product_code + '}')) { throw 'ProductCode divergente.' }
    if ($properties['ProductVersion'] -ne $manifest.version) { throw 'Versão divergente.' }
    if ($properties['UpgradeCode'] -ne ('{' + $manifest.upgrade_code + '}')) { throw 'UpgradeCode divergente.' }
    $components = @(Read-Table 'SELECT `Component`, `Directory_`, `Attributes`, `KeyPath` FROM `Component`' @('Component', 'Directory', 'Attributes', 'KeyPath'))
    $registry = @(Read-Table 'SELECT `Registry`, `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`' @('Id', 'Root', 'Key', 'Name', 'Value', 'Component'))
    foreach ($row in $registry) {
        if ($row.Root -ne '1' -or -not $row.Key.StartsWith('Software\RtxMonitor\NativeDesktop', [StringComparison]::Ordinal)) {
            throw 'O MSI contém escrita de registro fora do escopo HKCU do aplicativo.'
        }
    }
    $registryIds = @($registry.Id)
    foreach ($component in $components) {
        if (([int]$component.Attributes -band 256) -ne 256 -or -not ($registryIds -contains $component.KeyPath)) {
            throw 'Componente sem keypath HKCU ou sem bitness x64.'
        }
    }
    $directories = @{}
    foreach ($row in @(Read-Table 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' @('Id', 'Parent', 'Name'))) { $directories[$row.Id] = $row }
    if ($directories['INSTALLFOLDER'].Parent -ne 'NativeProgramsFolder' -or
        $directories['NativeProgramsFolder'].Parent -ne 'LocalAppDataFolder') { throw 'Destino do aplicativo fora do perfil local.' }
    function Relative-Directory([string]$id) {
        if ($id -eq 'INSTALLFOLDER') { return '' }
        if (-not $directories.ContainsKey($id) -or $id -eq 'TARGETDIR' -or -not $directories[$id].Parent) {
            throw "Arquivo fora da pasta de instalação: $id"
        }
        $parent = Relative-Directory $directories[$id].Parent
        $name = $directories[$id].Name.Split('|')[-1]
        return $(if ($parent) { $parent + '/' + $name } else { $name })
    }
    $componentDirectories = @{}
    foreach ($component in $components) { $componentDirectories[$component.Component] = $component.Directory }
    $fileRows = @(Read-Table 'SELECT `File`, `Component_`, `FileName`, `FileSize` FROM `File`' @('Id', 'Component', 'Name', 'Bytes'))
    $actualFiles = @{}
    $fileIdentifiers = @{}
    foreach ($file in $fileRows) {
        $directory = Relative-Directory $componentDirectories[$file.Component]
        $name = $file.Name.Split('|')[-1]
        $relative = if ($directory) { $directory + '/' + $name } else { $name }
        if ($actualFiles.ContainsKey($relative.ToLowerInvariant())) { throw 'Arquivo duplicado no MSI.' }
        $actualFiles[$relative.ToLowerInvariant()] = [long]$file.Bytes
        $fileIdentifiers[$relative.ToLowerInvariant()] = $file.Id
    }
    if ($fileRows.Count -ne $manifest.file_count) { throw 'Quantidade de arquivos diferente do manifesto.' }
    foreach ($file in $manifest.files) {
        $key = $file.path.ToLowerInvariant()
        if (-not $actualFiles.ContainsKey($key) -or $actualFiles[$key] -ne [long]$file.bytes) { throw "Arquivo ausente ou tamanho divergente: $key" }
    }
    $shortcuts = @(Read-Table 'SELECT `Name`, `Directory_`, `Target`, `WkDir` FROM `Shortcut`' @('Name', 'Directory', 'Target', 'WorkingDirectory'))
    if ($shortcuts.Count -ne 2 -or @($shortcuts | Where-Object { $_.Target -ne '[INSTALLFOLDER]rtx3060-monitor.exe' -or $_.WorkingDirectory -ne 'INSTALLFOLDER' -or $_.Directory -notin @('DesktopFolder', 'ProgramMenuFolder') }).Count -ne 0) {
        throw 'Atalhos fora do escopo do executável nativo.'
    }
    $media = @(Read-Table 'SELECT `Cabinet` FROM `Media`' @('Cabinet'))
    if (@($media | Where-Object { -not $_.Cabinet.StartsWith('#', [StringComparison]::Ordinal) }).Count -ne 0) { throw 'O MSI depende de um CAB externo.' }
    $removeRows = @(Read-Table 'SELECT `FileName`, `DirProperty`, `InstallMode` FROM `RemoveFile`' @('FileName', 'Directory', 'Mode'))
    if (@($removeRows | Where-Object { $_.FileName -or $_.Mode -ne '2' }).Count -ne 0) { throw 'A remoção do pacote não pode usar curingas ou limpar dados.' }
    if ([string]::IsNullOrWhiteSpace($DarkPath)) { $DarkPath = (& (Join-Path $PSScriptRoot 'ensure-wix.ps1')).Dark }
    $verificationRoot = [IO.Path]::GetFullPath((Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath)))).TrimEnd('\', '/')
    $verificationDirectory = Join-Path $verificationRoot ('verify-payload-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $verificationDirectory | Out-Null
    $decompiled = Join-Path $verificationDirectory 'package.wxs'
    & $DarkPath -nologo -x $verificationDirectory -o $decompiled $msi
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível extrair os CABs do MSI para a verificação de bytes.' }
    $extractedBytes = 0L
    foreach ($file in $manifest.files) {
        $key = $file.path.ToLowerInvariant()
        $extracted = Join-Path (Join-Path $verificationDirectory 'File') $fileIdentifiers[$key]
        if (-not (Test-Path -LiteralPath $extracted -PathType Leaf) -or
            (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Bytes do CAB divergem do manifesto: $key"
        }
        $extractedBytes += (Get-Item -LiteralPath $extracted).Length
    }
    # Delete only the just-created extraction directory after checking its exact
    # absolute containment. This never touches the payload, app, or user data.
    $resolvedVerification = (Resolve-Path -LiteralPath $verificationDirectory).Path
    if (-not $resolvedVerification.StartsWith($verificationRoot + '\verify-payload-', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Caminho de verificação fora do diretório esperado; nenhuma limpeza foi executada.'
    }
    Remove-Item -LiteralPath $resolvedVerification -Recurse -Force
    $result = [ordered]@{
        schema_version = 1; audited_at_utc = [DateTimeOffset]::UtcNow.ToString('o')
        msi = $msi; sha256 = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
        product_code = $properties['ProductCode']; version = $properties['ProductVersion']; upgrade_code = $properties['UpgradeCode']
        per_user_only = $true; machine_registry_writes = 0; custom_actions = 0; services = 0; driver_packages = 0
        file_count = $fileRows.Count; component_count = $components.Count; hkcu_registry_values = $registry.Count
        embedded_cabinets = $media.Count; shortcut_count = $shortcuts.Count
        extracted_file_hashes_verified = $fileRows.Count; extracted_bytes_verified = $extractedBytes
        install_folder = '%LOCALAPPDATA%\Programs\Rtx3060Monitor'
        userdata_directory_not_packaged = '%LOCALAPPDATA%\Rtx3060Monitor\data'
        uninstall_has_no_file_wildcards = $true; package_was_not_installed_by_audit = $true
        intentionally_excluded_ice_rule = 'ICE91: this package is exclusively per-user; per-machine profile propagation is inapplicable.'
        sqlite_language_metadata = 'Original e_sqlite3.dll has no MSI language list; neutral default 0 is authored without changing the DLL. Binder notice 1101 is excluded only for this fallback; ICE60 remains enabled.'
        payload_manifest_sha256 = (Get-FileHash -LiteralPath $PayloadManifest -Algorithm SHA256).Hash.ToLowerInvariant()
        passed = $true
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    Write-Host "Auditoria MSI aprovada: $($fileRows.Count) arquivos, escopo por usuário, sem ações de driver/serviço."
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
