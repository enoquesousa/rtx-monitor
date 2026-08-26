[CmdletBinding()]
param(
    [ValidateRange(10, 60)]
    [int]$DurationSeconds = 10,

    [int]$GpuzProcessId,

    [Parameter(Mandatory)]
    [string]$CandidateInventoryPath,

    [Parameter(Mandatory)]
    [string]$PriorObservationPath,

    [Parameter(Mandatory)]
    [string]$GpuzLogPath,

    [string]$CdbPath,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$failurePath = $null

trap {
    $failureText = $_ | Out-String
    if (-not [string]::IsNullOrWhiteSpace($failurePath)) {
        $failureText | Set-Content -LiteralPath $failurePath -Encoding utf8NoBOM
    }

    [Console]::Error.WriteLine($failureText.TrimEnd())
    exit 1
}

$profile = [ordered]@{
    name = 'gpuz-2.70.0-nvapi-610.88-therm-channel-status-v2'
    gpuz_sha256 = '6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29'
    nvapi_module_name = 'nvapi_impl.dll'
    nvapi_module_sha256 = 'fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf'
    interface_id = '0x65fe3aad'
    function_rva = '0x001ad310'
    caller_module_name = 'GPU-Z.exe'
    caller_rva = '0x002225b5'
    debugger_module_name = 'GPU_Z'
    structure_version = '0x000200a8'
    structure_size_bytes = 168
    fixed_point_fractional_bits = 8
    buffer_ebp_displacement = -0xac
    value_word_indices = @(10, 11)
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This attached thermal-channel capture requires an elevated PowerShell session.'
    }
}

function Get-PeMachine {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($stream.Length -lt 64) {
            throw "The PE image is truncated: '$Path'."
        }

        $reader = [IO.BinaryReader]::new($stream)
        try {
            $stream.Position = 0x3c
            $peOffset = $reader.ReadUInt32()
            if ($peOffset -gt $stream.Length - 6) {
                throw "The PE header offset is invalid: '$Path'."
            }

            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "The PE signature is invalid: '$Path'."
            }

            return $reader.ReadUInt16()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Resolve-SignedModuleByHash {
    param(
        [Parameter(Mandatory)]
        [string]$ModuleName,

        [Parameter(Mandatory)]
        [string]$ExpectedSha256
    )

    if ($ModuleName -ne 'nvapi_impl.dll') {
        throw "The fixed thermal profile does not accept module '$ModuleName'."
    }

    $windowsRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $repository = Join-Path $windowsRoot 'System32\DriverStore\FileRepository'
    $matches = @(
        Get-ChildItem `
            -LiteralPath $repository `
            -Filter $ModuleName `
            -File `
            -Recurse `
            -ErrorAction SilentlyContinue |
            ForEach-Object {
                $path = [IO.Path]::GetFullPath($_.FullName)
                $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($hash -eq $ExpectedSha256) {
                    $signature = Get-AuthenticodeSignature -LiteralPath $path
                    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
                        $null -eq $signature.SignerCertificate -or
                        ($signature.SignerCertificate.Subject -notlike '*NVIDIA Corporation*' -and
                            $signature.SignerCertificate.Subject -notlike '*Microsoft Windows Hardware Compatibility Publisher*')) {
                        throw "NVAPI module signature validation failed: '$path'."
                    }

                    $path
                }
            } |
            Sort-Object -Unique
    )
    if ($matches.Count -eq 0) {
        throw "No signed '$ModuleName' matches SHA-256 $ExpectedSha256."
    }

    return $matches[0]
}

function Assert-RegularLocalJson {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: '$Path'."
    }

    $item = Get-Item -LiteralPath $Path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -gt 16MB) {
        throw "$Description must be a regular local file no larger than 16 MiB."
    }
}

function Get-LogProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $item = Get-Item -LiteralPath $Path
    $lastSample = Get-Content -LiteralPath $Path -Tail 16 |
        Where-Object { $_ -match '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\s*,' } |
        Select-Object -Last 1
    if ($null -eq $lastSample -or
        $lastSample -notmatch '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s*,') {
        throw 'The GPU-Z reference log does not end in a timestamped sensor sample.'
    }

    return [pscustomobject]@{
        size_bytes = $item.Length
        last_write_utc = [DateTimeOffset]$item.LastWriteTimeUtc
        last_sample_local = $Matches.timestamp
    }
}

function Convert-HexWordToSignedInt32 {
    param(
        [Parameter(Mandatory)]
        [string]$Word
    )

    $unsigned = [Convert]::ToUInt32($Word.Substring(2), 16)
    return [BitConverter]::ToInt32([BitConverter]::GetBytes($unsigned), 0)
}

function Invoke-CdbDetach {
    param(
        [Parameter(Mandatory)]
        [string]$PipeName,

        [Parameter(Mandatory)]
        [Diagnostics.Process]$ServerProcess,

        [Parameter(Mandatory)]
        [string]$DebugLogPath,

        [Parameter(Mandatory)]
        [string]$DetachOutputPath,

        [Parameter(Mandatory)]
        [string]$DetachErrorPath
    )

    $remote = 'npipe:server=localhost,pipe={0}' -f $PipeName
    $client = Start-Process `
        -FilePath $CdbPath `
        -ArgumentList @('-remote', $remote, '-bonc', '-c', 'qqd') `
        -RedirectStandardOutput $DetachOutputPath `
        -RedirectStandardError $DetachErrorPath `
        -WindowStyle Hidden `
        -PassThru

    if (-not $client.WaitForExit(15000)) {
        throw 'The CDB detach client did not exit within 15 seconds.'
    }

    if (-not $ServerProcess.WaitForExit(15000)) {
        throw 'CDB did not detach normally; it was left running to protect GPU-Z.'
    }

    $detachTranscript = Get-Content -LiteralPath $DebugLogPath -Raw
    if ($detachTranscript -notmatch '(?ms)> qqd\s+quit:') {
        throw "CDB exited without a confirmed qqd detach; client exit code: $($client.ExitCode)."
    }
}

if (-not $IsWindows) {
    throw 'Attached NVAPI thermal-channel tracing is supported only on Windows.'
}

Assert-Administrator
$projectRoot = Split-Path -Parent $PSScriptRoot
$candidateSchemaPath = Join-Path `
    $projectRoot `
    'docs\schema\nvapi-candidate-inventory-v1.schema.json'
$priorSchemaPath = Join-Path `
    $projectRoot `
    'docs\schema\nvapi-candidate-call-observation-v1.schema.json'
$reportSchemaPath = Join-Path `
    $projectRoot `
    'docs\schema\nvapi-therm-channel-v2-observation-v1.schema.json'

$CandidateInventoryPath = [IO.Path]::GetFullPath($CandidateInventoryPath)
$PriorObservationPath = [IO.Path]::GetFullPath($PriorObservationPath)
$GpuzLogPath = [IO.Path]::GetFullPath($GpuzLogPath)
Assert-RegularLocalJson -Path $CandidateInventoryPath -Description 'Candidate inventory'
Assert-RegularLocalJson -Path $PriorObservationPath -Description 'Prior attached observation'

$candidateInventoryJson = Get-Content -LiteralPath $CandidateInventoryPath -Raw
if (-not ($candidateInventoryJson | Test-Json -SchemaFile $candidateSchemaPath)) {
    throw 'Candidate inventory does not satisfy nvapi-candidate-inventory-v1.'
}

$priorObservationJson = Get-Content -LiteralPath $PriorObservationPath -Raw
if (-not ($priorObservationJson | Test-Json -SchemaFile $priorSchemaPath)) {
    throw 'Prior observation does not satisfy nvapi-candidate-call-observation-v1.'
}

$candidateInventory = $candidateInventoryJson | ConvertFrom-Json
$priorObservation = $priorObservationJson | ConvertFrom-Json
$candidateInventorySha256 = (Get-FileHash `
    -LiteralPath $CandidateInventoryPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$priorObservationSha256 = (Get-FileHash `
    -LiteralPath $PriorObservationPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
if ($candidateInventorySha256 -ne $priorObservation.candidate_inventory_sha256 -or
    $candidateInventory.gpuz_sha256 -ne $priorObservation.gpuz_sha256) {
    throw 'Prior observation is not anchored to the supplied candidate inventory.'
}

if ($candidateInventory.gpuz_sha256 -ne $profile.gpuz_sha256) {
    throw "GPU-Z hash is not allowlisted by fixed profile '$($profile.name)'."
}

$candidateMatches = @(
    $candidateInventory.candidates |
        Where-Object {
            $_.interface_id -eq $profile.interface_id -and
            $_.module_name -eq $profile.nvapi_module_name -and
            $_.module_sha256 -eq $profile.nvapi_module_sha256 -and
            $_.rva -eq $profile.function_rva
        }
)
if ($candidateMatches.Count -ne 1) {
    throw 'The candidate inventory does not contain the exact allowlisted thermal entry.'
}

$priorTargets = @(
    $priorObservation.targets |
        Where-Object {
            $_.interface_ids -contains $profile.interface_id -and
            $_.module_name -eq $profile.nvapi_module_name -and
            $_.module_sha256 -eq $profile.nvapi_module_sha256 -and
            $_.rva -eq $profile.function_rva -and
            $_.call_count -gt 0
        }
)
if ($priorTargets.Count -ne 1) {
    throw 'The prior polling observation does not execute the allowlisted thermal entry.'
}

$priorCallSites = @(
    $priorTargets[0].call_sites |
        Where-Object {
            $_.caller_module_name -eq $profile.caller_module_name -and
            $_.caller_rva -eq $profile.caller_rva -and
            $_.call_count -gt 0
        }
)
if ($priorCallSites.Count -ne 1) {
    throw 'The prior observation does not contain the exact allowlisted GPU-Z call site.'
}

$nvapiModulePath = Resolve-SignedModuleByHash `
    -ModuleName $profile.nvapi_module_name `
    -ExpectedSha256 $profile.nvapi_module_sha256
if ((Get-PeMachine -Path $nvapiModulePath) -ne 0x014c) {
    throw 'The allowlisted NVAPI implementation is not a 32-bit PE image.'
}

if ([string]::IsNullOrWhiteSpace($CdbPath)) {
    $winDbgPackage = Get-AppxPackage -Name 'Microsoft.WinDbg' |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1
    if ($null -eq $winDbgPackage) {
        throw 'The signed Microsoft WinDbg package is required.'
    }

    $CdbPath = Join-Path $winDbgPackage.InstallLocation 'x86\cdb.exe'
}

$CdbPath = [IO.Path]::GetFullPath($CdbPath)
if (-not (Test-Path -LiteralPath $CdbPath -PathType Leaf) -or
    (Get-PeMachine -Path $CdbPath) -ne 0x014c) {
    throw "The signed x86 CDB executable was not found at '$CdbPath'."
}

$cdbSignature = Get-AuthenticodeSignature -LiteralPath $CdbPath
if ($cdbSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $cdbSignature.SignerCertificate -or
    $cdbSignature.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
    throw 'Microsoft CDB signature validation failed.'
}

if (@(Get-Process -Name 'WinDbgX', 'cdb' -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'Close every existing WinDbg or CDB session before attaching to GPU-Z.'
}

$gpuProcesses = @(
    if ($GpuzProcessId -gt 0) {
        Get-Process -Id $GpuzProcessId -ErrorAction Stop
    }
    else {
        Get-Process -Name 'GPU-Z' -ErrorAction SilentlyContinue
    }
)
if ($gpuProcesses.Count -ne 1 -or $gpuProcesses[0].ProcessName -ne 'GPU-Z') {
    throw 'Exactly one GPU-Z target process is required.'
}

$gpuProcess = $gpuProcesses[0]
$gpuzPath = $gpuProcess.Path
if ([string]::IsNullOrWhiteSpace($gpuzPath)) {
    throw 'The elevated capture could not resolve the GPU-Z executable path.'
}

$gpuzSignature = Get-AuthenticodeSignature -LiteralPath $gpuzPath
if ($gpuzSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $gpuzSignature.SignerCertificate -or
    $gpuzSignature.SignerCertificate.Subject -notlike '*TechPowerUp LLC*') {
    throw 'GPU-Z signature validation failed.'
}

if ((Get-PeMachine -Path $gpuzPath) -ne 0x014c) {
    throw 'The fixed profile requires the signed 32-bit GPU-Z image.'
}

$gpuzSha256 = (Get-FileHash -LiteralPath $gpuzPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($gpuzSha256 -ne $profile.gpuz_sha256) {
    throw 'The running GPU-Z image does not match the fixed thermal profile.'
}

if (-not (Test-Path -LiteralPath $GpuzLogPath -PathType Leaf)) {
    throw "GPU-Z reference log was not found: '$GpuzLogPath'."
}

$gpuzLogItem = Get-Item -LiteralPath $GpuzLogPath
if (($gpuzLogItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $gpuzLogItem.Length -lt 1 -or
    $gpuzLogItem.Length -gt 64MB) {
    throw 'GPU-Z reference log must be a regular local file between 1 byte and 64 MiB.'
}

$logRoot = [IO.Path]::GetPathRoot($GpuzLogPath)
$logDrive = [IO.DriveInfo]::new($logRoot)
if ($logDrive.DriveType -ne [IO.DriveType]::Fixed) {
    throw 'GPU-Z reference log must reside on a fixed local drive.'
}

$initialLogProbe = Get-LogProbe -Path $GpuzLogPath
if (([DateTimeOffset]::UtcNow - $initialLogProbe.last_write_utc).TotalSeconds -gt 5) {
    throw 'GPU-Z reference log is not advancing immediately before capture.'
}

$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'evidence'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path `
        $evidenceRoot `
        ('gpuz-nvapi-therm-channel-v2-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$evidencePrefix = $evidenceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith(
        $evidencePrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a child of '$evidenceRoot'."
}

if (Test-Path -LiteralPath $OutputDirectory) {
    throw "OutputDirectory already exists: '$OutputDirectory'."
}

$null = New-Item -ItemType Directory -Path $OutputDirectory
$failurePath = Join-Path $OutputDirectory 'capture-error.txt'
$commandPath = Join-Path $OutputDirectory 'windbg-commands.txt'
$debugLogPath = Join-Path $OutputDirectory 'windbg-therm-channel-v2.log'
$debugOutputPath = Join-Path $OutputDirectory 'cdb-output.txt'
$debugErrorPath = Join-Path $OutputDirectory 'cdb-error.txt'
$detachOutputPath = Join-Path $OutputDirectory 'cdb-detach-output.txt'
$detachErrorPath = Join-Path $OutputDirectory 'cdb-detach-error.txt'
$reportPath = Join-Path $OutputDirectory 'nvapi-therm-channel-v2-report.json'

$wordFormatParts = @()
$wordExpressions = @()
for ($wordIndex = 0; $wordIndex -lt 42; $wordIndex++) {
    $wordFormatParts += 'w{0:D2}=0x%08x' -f $wordIndex
    $offset = $wordIndex * 4
    $wordExpressions += if ($offset -eq 0) {
        'poi(@ebp-0xac)'
    }
    else {
        'poi(@ebp-0xac+0x{0:x})' -f $offset
    }
}

$hitFormat = 'RTXMON_NVAPI_THERM_V2 tid=0x%08x channel=0x%08x status=0x%08x {0}\\n' -f
    ($wordFormatParts -join ' ')
$breakpointCommand = 'bp {0}+{1} ".printf \"{2}\", @$tid, @ebx, @eax, {3}; gc"' -f
    $profile.debugger_module_name,
    $profile.caller_rva,
    $hitFormat,
    ($wordExpressions -join ', ')
@(
    '.echo RTXMON_ATTACH_READY',
    $breakpointCommand,
    'g'
) | Set-Content -LiteralPath $commandPath -Encoding ascii

$pipeName = 'rtx-monitor-gpuz-therm-v2-{0}-{1}' -f `
    $gpuProcess.Id,
    ([Guid]::NewGuid().ToString('N'))
$debugger = $null
$captureStartedUtc = $null
$midpointLogProbe = $null
try {
    $debugger = Start-Process `
        -FilePath $CdbPath `
        -ArgumentList @(
            '-server', ('npipe:pipe={0}' -f $pipeName),
            '-pd',
            '-noshell',
            '-nosqm',
            '-logo', ('"{0}"' -f $debugLogPath),
            '-p', [string]$gpuProcess.Id,
            '-cf', ('"{0}"' -f $commandPath)
        ) `
        -RedirectStandardOutput $debugOutputPath `
        -RedirectStandardError $debugErrorPath `
        -WindowStyle Hidden `
        -PassThru

    $readyDeadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $ready = (Test-Path -LiteralPath $debugLogPath -PathType Leaf) -and
            (Select-String `
                -LiteralPath $debugLogPath `
                -Pattern 'RTXMON_ATTACH_READY' `
                -Quiet)
        $debugger.Refresh()
    } while (-not $ready -and -not $debugger.HasExited -and
        (Get-Date) -lt $readyDeadline)

    if (-not $ready) {
        throw 'CDB did not reach the attached thermal capture ready marker.'
    }

    $captureStartedUtc = [DateTimeOffset]::UtcNow
    $firstHalfSeconds = [Math]::Floor($DurationSeconds / 2)
    Start-Sleep -Seconds $firstHalfSeconds
    $midpointLogProbe = Get-LogProbe -Path $GpuzLogPath
    if ($midpointLogProbe.size_bytes -le $initialLogProbe.size_bytes -or
        $midpointLogProbe.last_write_utc -le $initialLogProbe.last_write_utc) {
        throw 'GPU-Z reference log did not grow during the first half of capture.'
    }

    Start-Sleep -Seconds ($DurationSeconds - $firstHalfSeconds)
    Invoke-CdbDetach `
        -PipeName $pipeName `
        -ServerProcess $debugger `
        -DebugLogPath $debugLogPath `
        -DetachOutputPath $detachOutputPath `
        -DetachErrorPath $detachErrorPath
}
finally {
    if ($null -ne $debugger) {
        $debugger.Refresh()
        if (-not $debugger.HasExited) {
            Invoke-CdbDetach `
                -PipeName $pipeName `
                -ServerProcess $debugger `
                -DebugLogPath $debugLogPath `
                -DetachOutputPath $detachOutputPath `
                -DetachErrorPath $detachErrorPath
        }
    }
}

$capturedUtc = [DateTimeOffset]::UtcNow
$finalLogProbe = Get-LogProbe -Path $GpuzLogPath
if ($null -eq $midpointLogProbe -or
    $finalLogProbe.size_bytes -le $midpointLogProbe.size_bytes -or
    $finalLogProbe.last_write_utc -le $midpointLogProbe.last_write_utc) {
    throw 'GPU-Z reference log did not continue growing through the capture window.'
}

$gpuProcess.Refresh()
if ($gpuProcess.HasExited) {
    throw 'GPU-Z exited while the debugger detached.'
}

$debugText = Get-Content -LiteralPath $debugLogPath -Raw
$debuggerCommandFailure = [regex]::Match(
    $debugText,
    'Command file execution failed|Syntax error|Some commands were skipped|Numeric expression missing|Couldn.t resolve|Unable to insert breakpoint',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($debuggerCommandFailure.Success) {
    throw "CDB reported a command failure: $($debuggerCommandFailure.Value)."
}

$samples = [Collections.Generic.List[object]]::new()
$hitRecords = [regex]::Matches(
    $debugText,
    'RTXMON_NVAPI_THERM_V2 tid=0x[0-9a-f]{8} channel=0x[0-9a-f]{8} status=0x[0-9a-f]{8}(?: w[0-9]{2}=0x[0-9a-f]{8}){42}',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
foreach ($hitRecord in $hitRecords) {
    $hitLine = $hitRecord.Value
    $header = [regex]::Match(
        $hitLine,
        'RTXMON_NVAPI_THERM_V2 tid=0x(?<tid>[0-9a-f]{8}) channel=0x(?<channel>[0-9a-f]{8}) status=0x(?<status>[0-9a-f]{8})',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $header.Success) {
        throw 'A thermal hit line has an invalid header.'
    }

    $wordMatches = [regex]::Matches(
        $hitLine,
        'w(?<index>[0-9]{2})=0x(?<value>[0-9a-f]{8})',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($wordMatches.Count -ne 42) {
        throw 'A thermal hit does not contain exactly 42 bounded DWORDs.'
    }

    $rawWords = @(
        for ($index = 0; $index -lt 42; $index++) {
            $match = $wordMatches[$index]
            if ([int]$match.Groups['index'].Value -ne $index) {
                throw 'Thermal hit DWORD indices are not contiguous.'
            }

            '0x{0}' -f $match.Groups['value'].Value.ToLowerInvariant()
        }
    )
    $channelIndex = [Convert]::ToInt32($header.Groups['channel'].Value, 16)
    if ($channelIndex -lt 0 -or $channelIndex -gt 1) {
        throw "The fixed profile observed an unexpected thermal channel index: $channelIndex."
    }

    $returnStatus = '0x{0}' -f $header.Groups['status'].Value.ToLowerInvariant()
    if ($returnStatus -ne '0x00000000') {
        throw "The fixed thermal call returned a non-success status: $returnStatus."
    }

    if ($rawWords[0] -ne $profile.structure_version) {
        throw "Thermal structure version changed from $($profile.structure_version)."
    }

    $expectedMask = '0x{0:x8}' -f (1 -shl $channelIndex)
    if ($rawWords[1] -ne $expectedMask) {
        throw "Thermal channel mask $($rawWords[1]) does not match channel $channelIndex."
    }

    $selectedWordIndex = $profile.value_word_indices[$channelIndex]
    $selectedRaw = Convert-HexWordToSignedInt32 -Word $rawWords[$selectedWordIndex]
    $samples.Add([pscustomobject]@{
            sequence = $samples.Count + 1
            thread_id = '0x{0}' -f $header.Groups['tid'].Value.ToLowerInvariant()
            channel_index = $channelIndex
            return_status = $returnStatus
            structure_version = $rawWords[0]
            channel_mask = $rawWords[1]
            raw_words = $rawWords
            selected_word_index = $selectedWordIndex
            selected_raw_fixed_8 = $selectedRaw
            selected_celsius = $selectedRaw / [Math]::Pow(2, $profile.fixed_point_fractional_bits)
        })
}

if ($samples.Count -eq 0) {
    throw 'No successful thermal-channel v2 call was observed during the bounded window.'
}

$report = [ordered]@{
    schema_version = 1
    source_kind = 'nvapi_therm_channel_v2_observation'
    capture_started_utc = $captureStartedUtc.ToString('O')
    captured_utc = $capturedUtc.ToString('O')
    duration_seconds = $DurationSeconds
    process_id = $gpuProcess.Id
    gpuz_sha256 = $gpuzSha256
    debugger_sha256 = (Get-FileHash `
        -LiteralPath $CdbPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    debugger_file_version = (Get-Item -LiteralPath $CdbPath).VersionInfo.FileVersion
    candidate_inventory_sha256 = $candidateInventorySha256
    prior_observation_sha256 = $priorObservationSha256
    nvapi_module_sha256 = $profile.nvapi_module_sha256
    interface_id = $profile.interface_id
    function_rva = $profile.function_rva
    caller_module_name = $profile.caller_module_name
    caller_rva = $profile.caller_rva
    structure_version = $profile.structure_version
    structure_size_bytes = $profile.structure_size_bytes
    fixed_point_fractional_bits = $profile.fixed_point_fractional_bits
    value_word_indices = $profile.value_word_indices
    reference_log = [ordered]@{
        size_bytes_before = $initialLogProbe.size_bytes
        size_bytes_midpoint = $midpointLogProbe.size_bytes
        size_bytes_after = $finalLogProbe.size_bytes
        last_write_utc_before = $initialLogProbe.last_write_utc.ToString('O')
        last_write_utc_midpoint = $midpointLogProbe.last_write_utc.ToString('O')
        last_write_utc_after = $finalLogProbe.last_write_utc.ToString('O')
        last_sample_local_before = $initialLogProbe.last_sample_local
        last_sample_local_midpoint = $midpointLogProbe.last_sample_local
        last_sample_local_after = $finalLogProbe.last_sample_local
        grew_during_capture = $true
    }
    call_count = $samples.Count
    samples = @($samples)
    warning = 'This fixed, hash-allowlisted profile passively reads the 168-byte buffer that signed GPU-Z already supplied to NvAPI_GPU_ThermChannelGetStatus v2. It does not call NVAPI, modify the buffer, or generalize the channel meaning beyond the anchored GPU-Z, NVAPI, board, and driver evidence.'
}
$reportJson = $report | ConvertTo-Json -Depth 8
if (-not ($reportJson | Test-Json -SchemaFile $reportSchemaPath)) {
    throw 'Thermal-channel report did not satisfy nvapi-therm-channel-v2-observation-v1.'
}

$reportJson | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
if (Test-Path -LiteralPath $failurePath -PathType Leaf) {
    Remove-Item -LiteralPath $failurePath -Force
}

$report | ConvertTo-Json -Depth 8
