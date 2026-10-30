[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PackageArchive,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string]$ExpectedSha256,
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$TargetVersion = '0.11.0',
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')] [string]$ServiceName = 'RtxMonitorService',
    [ValidateRange(1, 65535)] [int]$Port = 5136,
    [switch]$EnableProfileTelemetry,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path -LiteralPath $PackageArchive).Path
$serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if ($null -eq $serviceInfo -or $serviceInfo.State -ne 'Running') {
    throw 'A atualização requer o serviço existente em execução.'
}
# This updater intentionally supports only the documented single-executable installation.
if ($serviceInfo.PathName -notmatch '^"([^"\r\n]+\.exe)"$') {
    throw 'A instalação possui argumentos ou caminho não suportado; revise sua configuração primeiro.'
}
$oldExecutable = (Resolve-Path -LiteralPath $Matches[1]).Path
$oldDirectory = Split-Path -Parent $oldExecutable
$installRoot = Split-Path -Parent $oldDirectory
$destination = Join-Path $installRoot $TargetVersion
if (Test-Path -LiteralPath $destination) {
    throw "O destino já existe e não será sobrescrito: $destination"
}
$owner = @(Get-NetTCPConnection -State Listen -LocalPort $Port |
    Where-Object { $_.LocalAddress -eq '127.0.0.1' -and $_.OwningProcess -eq $serviceInfo.ProcessId })
if ($owner.Count -ne 1) { throw 'A porta local não pertence inequivocamente ao serviço selecionado.' }
$healthUri = "http://127.0.0.1:$Port/health"
$health = Invoke-RestMethod -Uri $healthUri -TimeoutSec 5
if ($health.status -ne 'healthy' -or $health.storage.schema_version -ne 1) {
    throw 'O serviço deve estar saudável e usar o schema SQLite 1.'
}
$database = (Resolve-Path -LiteralPath $health.storage.database_path).Path
$oldSettings = Get-Content -Raw -LiteralPath (Join-Path $oldDirectory 'appsettings.json') | ConvertFrom-Json -AsHashtable

# Hold a read-only handle without write sharing from hashing through extraction.
$archiveStream = [IO.File]::Open($archivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$zip = $null
try {
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($archiveStream)).ToLowerInvariant()
    if ($hash -ne $ExpectedSha256.ToLowerInvariant()) { throw 'SHA256 do pacote divergente.' }
    $archiveStream.Position = 0
    $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    $manifestEntry = $zip.GetEntry('package-manifest.json')
    if ($null -eq $manifestEntry -or $manifestEntry.Length -gt 1MB) { throw 'Manifesto ausente ou excessivo.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.product_version -ne $TargetVersion -or $manifest.abi_version -ne 8) {
        throw 'Este procedimento exige o pacote ABI8 e a versão alvo declarada.'
    }
    $expected = @{}
    foreach ($item in $manifest.files) {
        $name = [string]$item.path
        $invalidSegment = @($name.Split('/') | Where-Object {
            $_ -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]*$' -or $_.EndsWith('.') -or
            $_ -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)'
        }).Count -ne 0
        if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or
            $name.Contains('\') -or '..' -in $name.Split('/') -or
            $invalidSegment -or $name.EndsWith('/') -or $expected.ContainsKey($name)) {
            throw 'Caminho inválido ou duplicado no manifesto.'
        }
        $expected[$name] = $item
    }
    $seen = @{}
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName
        if ($seen.ContainsKey($name)) { throw 'Entrada duplicada no ZIP.' }
        $seen[$name] = $true
        if ($name -eq 'package-manifest.json') { continue }
        if (-not $expected.ContainsKey($name) -or $entry.Length -ne $expected[$name].size_bytes) {
            throw 'Conteúdo do ZIP não corresponde ao manifesto.'
        }
        $stream = $entry.Open()
        try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($entryHash -ne $expected[$name].sha256) { throw "Hash divergente: $name" }
    }
    if ($seen.Count -ne $expected.Count + 1 -or
        -not $expected.ContainsKey('RtxMonitor.Service.exe') -or
        -not $expected.ContainsKey('appsettings.json') -or
        -not $expected.ContainsKey('rtxmon_native.dll')) { throw 'Conjunto incompleto de arquivos.' }

    $databaseBytes = (Get-Item -LiteralPath $database).Length
    if (Test-Path -LiteralPath ($database + '-wal')) { $databaseBytes += (Get-Item -LiteralPath ($database + '-wal')).Length }
    $packageBytes = ($zip.Entries | Measure-Object -Property Length -Sum).Sum
    $databaseVolume = [IO.Path]::GetPathRoot($database)
    $installVolume = [IO.Path]::GetPathRoot($destination)
    $requiredBackupBytes = $databaseBytes + 64MB
    if ($databaseVolume -eq $installVolume) { $requiredBackupBytes += $packageBytes }
    if (([IO.DriveInfo]::new($databaseVolume)).AvailableFreeSpace -lt $requiredBackupBytes -or
        ([IO.DriveInfo]::new($installVolume)).AvailableFreeSpace -lt ($packageBytes + 64MB)) {
        throw 'Espaço livre insuficiente para preparar instalação e backup antes da parada.'
    }

    $plan = [ordered]@{
        mode = $(if ($Apply) { 'apply' } else { 'plan_only' })
        service = $ServiceName; old_binary_path = $serviceInfo.PathName
        old_version = $health.service_version; target_version = $TargetVersion
        destination = $destination; database = $database; package_sha256 = $hash
        profile_enabled = [bool]$EnableProfileTelemetry
        rollback = 'Restore original SCM binary path; preserve old binaries and database. No automatic database restore.'
    }
    if (-not $Apply) { $plan | ConvertTo-Json -Depth 10; return }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'A aplicação da atualização exige PowerShell elevado; o plano não exige elevação.'
    }

    $backupDirectory = Join-Path (Split-Path -Parent $database) (
        'backups\update-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backupDirectory | Out-Null
    $journal = [ordered]@{ plan = $plan; phase = 'staging'; backup_directory = $backupDirectory; error = $null }
    function Save-Journal { $journal | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $backupDirectory 'update.json') }
    Save-Journal
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach ($entry in $zip.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
        if (-not $target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Entrada fora do destino de instalação.'
        }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        $inputStream = $entry.Open()
        $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew)
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
    # Preserve operator configuration, replacing only the canonical profile opt-in.
    $oldSettings.RtxMonitor.Remove('ExperimentalTelemetryEnabled') | Out-Null
    $oldSettings.RtxMonitor.Remove('ExperimentalIntervalMilliseconds') | Out-Null
    $oldSettings.RtxMonitor.ProfileTelemetryEnabled = [bool]$EnableProfileTelemetry
    $oldSettings.RtxMonitor.ProfileIntervalMilliseconds = 1000
    # Relative database settings must not move the database when the binary directory changes.
    $oldSettings.RtxMonitor.DatabasePath = $database
    $oldSettings.RtxMonitor.Port = $Port
    $oldSettings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $destination 'appsettings.json')
    $journal['installed_settings_sha256'] = (Get-FileHash -LiteralPath (Join-Path $destination 'appsettings.json')).Hash.ToLowerInvariant()
    $journal.phase = 'staged'; Save-Journal

    $changedPath = $false
    $stopped = $false
    try {
        $current = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
        if ($current.PathName -ne $serviceInfo.PathName -or $current.ProcessId -ne $serviceInfo.ProcessId) {
            throw 'O serviço mudou durante a preparação; nenhuma parada foi solicitada.'
        }
        Stop-Service -Name $ServiceName
        $stopped = $true
        (Get-Service $ServiceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, '00:00:30')
        foreach ($suffix in @('', '-wal', '-shm')) {
            if (Test-Path -LiteralPath ($database + $suffix)) {
                Copy-Item -LiteralPath ($database + $suffix) -Destination (Join-Path $backupDirectory ('telemetry.db' + $suffix))
            }
        }
        Copy-Item -LiteralPath (Join-Path $oldDirectory 'appsettings.json') -Destination (Join-Path $backupDirectory 'previous-appsettings.json')
        $journal['backup_files'] = @(Get-ChildItem -LiteralPath $backupDirectory -File |
            Where-Object Name -ne 'update.json' | ForEach-Object {
                @{ name = $_.Name; size_bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
            })
        $journal.phase = 'backed_up'; Save-Journal
        $changedPath = $true
        & sc.exe config $ServiceName 'binPath=' ('"' + (Join-Path $destination 'RtxMonitor.Service.exe') + '"') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Falha ao atualizar caminho no SCM.' }
        Start-Service -Name $ServiceName
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
        $ready = $false
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try {
                $live = Invoke-RestMethod -Uri $healthUri -TimeoutSec 2
                $ready = $live.status -eq 'healthy' -and $live.service_version -eq $TargetVersion
                if ($ready -and $EnableProfileTelemetry) {
                    $uuid = 'GPU-11111111-2222-3333-4444-555555555555'
                    $report = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/v1/gpus/$uuid/profile-telemetry" -TimeoutSec 2
                    $ready = @($report.fields | Where-Object state -eq 'available').Count -eq 2
                }
                if ($ready) { break }
            }
            catch { $ready = $false }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw 'A nova versão não confirmou saúde e sensores no prazo.' }
        $journal.phase = 'healthy'; $journal['health'] = $live; Save-Journal
        $journal | ConvertTo-Json -Depth 20
    }
    catch {
        $updateError = $_
        $journal.error = $_.Exception.Message
        if ($stopped) {
            try {
                if ($changedPath) {
                    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
                    (Get-Service $ServiceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, '00:00:30')
                    & sc.exe config $ServiceName 'binPath=' $serviceInfo.PathName | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw 'SCM recusou o caminho anterior durante a reversão.' }
                }
                Start-Service -Name $ServiceName
                $rollbackDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
                $rollbackReady = $false
                while ([DateTimeOffset]::UtcNow -lt $rollbackDeadline) {
                    try {
                        $rollbackHealth = Invoke-RestMethod -Uri $healthUri -TimeoutSec 2
                        $rollbackReady = $rollbackHealth.status -eq 'healthy' -and $rollbackHealth.service_version -eq $health.service_version
                        if ($rollbackReady) { break }
                    }
                    catch { $rollbackReady = $false }
                    Start-Sleep -Milliseconds 500
                }
                if (-not $rollbackReady) { throw 'A versão anterior não confirmou saúde no prazo de reversão.' }
                $journal.phase = 'rolled_back_healthy'
                $journal['rollback_health'] = $rollbackHealth
            }
            catch {
                $journal.phase = 'rollback_failed'
                $journal['rollback_error'] = $_.Exception.Message
                Save-Journal
                throw
            }
        }
        else { $journal.phase = 'aborted_before_stop' }
        Save-Journal
        throw $updateError
    }
}
finally {
    if ($null -ne $zip) { $zip.Dispose() }
    $archiveStream.Dispose()
}
